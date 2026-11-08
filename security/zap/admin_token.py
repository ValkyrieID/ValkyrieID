#!/usr/bin/env python3
"""Gets an Admin API access token for the authenticated ZAP scan (security/zap/run.sh). Standard library only.

Signs in like a person would: the authorization code flow with PKCE (S256) as the built-in valkyrieid-admin-ui client,
the sign-in form, and two-factor authentication. The first time, it enrolls a TOTP authenticator (2FA is required
for admins) and keeps the secret in the state file; later it signs in with codes from it, or reuses the SSO session.

The token is cached in the state file and reused while it is valid for at least --min-validity seconds, so the ZAP
script can call this before every Admin API request batch. A lock serializes concurrent calls.

With --pending 2fa|enroll it prints the pending-login cookie (__Host-valkyrie.pending) of a sign-in that stopped after
the password, for the sign-in page scan (pending-login.js). For 2fa it enrolls an authenticator first if needed.

Usage: admin_token.py --issuer <url> --username <name> [--cafile <ca.crt>] [--state <file>] [--min-validity <s>] [--json]
                      [--pending 2fa|enroll]
The password is read from the ADMIN_PASSWORD environment variable, never from the command line.
"""

import argparse
import base64
import hashlib
import hmac
import html.parser
import http.cookiejar
import json
import os
import secrets
import ssl
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

try:
    import fcntl
except ImportError:  # Windows: no locking; run one instance at a time.
    fcntl = None

CLIENT_ID = 'valkyrieid-admin-ui'
SCOPE = 'openid profile valkyrieid.admin'
PENDING_COOKIE = '__Host-valkyrie.pending'


class Page(html.parser.HTMLParser):
    """The parts of ValkyrieID's sign-in pages this script needs: form fields, the TOTP secret, and links."""

    def __init__(self, body):
        super().__init__()
        self.inputs, self.links, self.secret, self._in_secret = {}, [], None, False
        self.feed(body)

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == 'input' and a.get('name'):
            self.inputs[a['name']] = a.get('value') or ''
        elif tag == 'a' and a.get('href'):
            self.links.append(a['href'])
        elif tag == 'code' and a.get('id') == 'totp-secret':
            self._in_secret = True

    def handle_endtag(self, tag):
        if tag == 'code':
            self._in_secret = False

    def handle_data(self, data):
        if self._in_secret:
            self.secret = (self.secret or '') + data


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def totp(secret_b32, at=None):
    """RFC 6238 with ValkyrieID's parameters: HMAC-SHA1, 6 digits, 30-second steps."""
    key = base64.b32decode(secret_b32 + '=' * (-len(secret_b32) % 8))
    counter = int((at or time.time()) // 30)
    digest = hmac.new(key, struct.pack('>Q', counter), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    return '%06d' % ((struct.unpack('>I', digest[offset:offset + 4])[0] & 0x7FFFFFFF) % 1_000_000)


class Client:
    def __init__(self, issuer, cafile, cookie_file):
        self.issuer = issuer.rstrip('/')
        self.redirect_uri = self.issuer + '/admin/callback'
        self.cookies = http.cookiejar.MozillaCookieJar(cookie_file)
        if cookie_file and os.path.exists(cookie_file):
            self.cookies.load(ignore_discard=True, ignore_expires=False)
        context = ssl.create_default_context(cafile=cafile)
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPSHandler(context=context), urllib.request.HTTPCookieProcessor(self.cookies), NoRedirect())

    def send(self, url, form=None):
        """One request without following redirects: (status, location, body)."""
        data = urllib.parse.urlencode(form).encode() if form is not None else None
        request = urllib.request.Request(url, data=data, headers={'User-Agent': 'ValkyrieID ZAP admin token'})
        try:
            with self.opener.open(request, timeout=30) as response:
                return response.status, None, response.read().decode()
        except urllib.error.HTTPError as e:
            return e.code, e.headers.get('Location'), e.read().decode(errors='replace')

    def follow(self, url, form=None):
        """Follows redirects until a page, or the admin UI callback, is reached: (url, status, body)."""
        for _ in range(10):
            status, location, body = self.send(url, form)
            form = None
            if status not in (301, 302, 303, 307, 308):
                return url, status, body
            url = urllib.parse.urljoin(url, location)
            if url.startswith(self.redirect_uri):
                return url, status, body
        raise RuntimeError('Too many redirects.')


def sign_in(client, username, password, state_data):
    verifier = secrets.token_urlsafe(48)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b'=').decode()
    state, nonce = secrets.token_urlsafe(16), secrets.token_urlsafe(16)
    url = client.issuer + '/connect/authorize?' + urllib.parse.urlencode({
        'client_id': CLIENT_ID, 'redirect_uri': client.redirect_uri, 'response_type': 'code', 'scope': SCOPE,
        'state': state, 'nonce': nonce, 'code_challenge': challenge, 'code_challenge_method': 'S256'})

    url, status, body = client.follow(url)
    for _ in range(6):
        if url.startswith(client.redirect_uri):
            break
        path = urllib.parse.urlsplit(url).path
        page = Page(body)
        if path == '/account/login':
            fields = {**page.inputs, 'Identifier': username, 'Password': password}
        elif path == '/account/login/enroll' and page.secret:
            state_data['totp_secret'] = page.secret.replace(' ', '')
            fields = {**page.inputs, 'Code': totp(state_data['totp_secret'])}
        elif path == '/account/login/enroll':
            # Recovery codes shown after enrolling; continue to the authorization request.
            continue_link = next(link for link in page.links if link.startswith('/connect/authorize'))
            url, status, body = client.follow(urllib.parse.urljoin(url, continue_link))
            continue
        elif path == '/account/login/2fa':
            if 'totp_secret' not in state_data:
                raise RuntimeError('2FA is on for this user, but the state file has no TOTP secret.')
            # A code can only be used once; wait for the next step if this one was used by the last sign-in.
            if state_data.get('totp_step') == int(time.time() // 30):
                time.sleep(30 - time.time() % 30 + 1)
            state_data['totp_step'] = int(time.time() // 30)
            fields = {**page.inputs, 'Code': totp(state_data['totp_secret'])}
        else:
            raise RuntimeError(f'Unexpected page {path} (HTTP {status}).')
        url, status, body = client.follow(url, fields)
    else:
        raise RuntimeError('Sign-in did not finish; check the username and password.')

    query = urllib.parse.parse_qs(urllib.parse.urlsplit(url).query)
    if query.get('state') != [state]:
        raise RuntimeError('The authorization response has the wrong state.')
    if query.get('iss') != [client.issuer]:  # RFC 9207
        raise RuntimeError('The authorization response has the wrong issuer.')
    if 'code' not in query:
        raise RuntimeError('Authorization failed: ' + query.get('error', ['?'])[0])

    status, _, body = client.send(client.issuer + '/connect/token', {
        'grant_type': 'authorization_code', 'code': query['code'][0], 'redirect_uri': client.redirect_uri,
        'client_id': CLIENT_ID, 'code_verifier': verifier})
    if status != 200:
        raise RuntimeError(f'The token request failed (HTTP {status}).')
    token = json.loads(body)
    return token['access_token'], int(time.time()) + int(token['expires_in'])


def pending_cookie(client, username, password, step):
    """Signs in with the password only and returns the pending-login cookie for the second step (2fa or enroll)."""
    url, status, body = client.follow(client.issuer + '/account/login')
    page = Page(body)
    url, status, body = client.follow(url, {**page.inputs, 'Identifier': username, 'Password': password})
    path = urllib.parse.urlsplit(url).path
    if path != '/account/login/' + step:
        raise RuntimeError(f'Expected the {step} step after the password, got {path} (HTTP {status}).')
    cookie = next((c for c in client.cookies if c.name == PENDING_COOKIE), None)
    if cookie is None:
        raise RuntimeError('No pending-login cookie after the password.')
    return cookie.value


def main():
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('--issuer', required=True)
    parser.add_argument('--username', required=True)
    parser.add_argument('--cafile')
    parser.add_argument('--state', default=os.path.join(os.path.dirname(os.path.abspath(__file__)), 'out', 'admin-token-state.json'))
    parser.add_argument('--min-validity', type=int, default=120)
    parser.add_argument('--json', action='store_true', help='print {"access_token", "expires_at"} instead of the token')
    parser.add_argument('--pending', choices=['2fa', 'enroll'],
                        help='print a pending-login cookie for this step instead of a token (sign-in page scan)')
    args = parser.parse_args()
    password = os.environ.get('ADMIN_PASSWORD')
    if not password:
        parser.error('Set ADMIN_PASSWORD.')

    os.makedirs(os.path.dirname(os.path.abspath(args.state)), exist_ok=True)
    with open(args.state + '.lock', 'w') as lock:
        if fcntl:
            fcntl.flock(lock, fcntl.LOCK_EX)
        state_data = {}
        if os.path.exists(args.state):
            with open(args.state) as f:
                state_data = json.load(f)
        # The 2fa step needs an enrolled authenticator: sign in fully once, which enrolls one.
        stale = state_data.get('expires_at', 0) - time.time() < args.min_validity
        if (args.pending is None and stale) or (args.pending == '2fa' and 'totp_secret' not in state_data):
            client = Client(args.issuer, args.cafile, args.state + '.cookies')
            state_data['access_token'], state_data['expires_at'] = sign_in(client, args.username, password, state_data)
            client.cookies.save(ignore_discard=True)
            # Holds the TOTP secret and session cookie of a throwaway test admin; readable by the owner only.
            fd = os.open(args.state, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
            with os.fdopen(fd, 'w') as f:
                json.dump(state_data, f)

    if args.pending:
        # A fresh cookie jar: an SSO session would skip the sign-in form.
        print(pending_cookie(Client(args.issuer, args.cafile, None), args.username, password, args.pending))
    elif args.json:
        print(json.dumps({'access_token': state_data['access_token'], 'expires_at': state_data['expires_at']}))
    else:
        print(state_data['access_token'])


if __name__ == '__main__':
    try:
        main()
    except RuntimeError as e:
        print('admin_token.py: ' + str(e), file=sys.stderr)
        sys.exit(1)
