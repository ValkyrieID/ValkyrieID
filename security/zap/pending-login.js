// ZAP httpsender script for the sign-in page scan (login-pages.yaml). The two-factor, recovery code, and enrollment
// pages only show their forms during a sign-in that stopped after the password, which is held in the pending-login
// cookie. This script gets one from admin_token.py --pending (zapadmin with an authenticator for the 2fa and
// recovery pages, zapenroll without one for the enrollment page) and puts it on every request to those pages, so
// the spider and the active scan reach the forms. Pending logins live 5 minutes; a cookie is reused for 3.
// See docs/security/zap.md.

var ScriptVars = Java.type('org.zaproxy.zap.extension.script.ScriptVars');
var ProcessBuilder = Java.type('java.lang.ProcessBuilder');
var JString = Java.type('java.lang.String');
var StandardCharsets = Java.type('java.nio.charset.StandardCharsets');

var ISSUER = 'https://valkyrieid.localtest.me:9443';
var COOKIE = '__Host-valkyrie.pending';
var REUSE_MS = 3 * 60 * 1000;
var PAGES = {
  '/account/login/2fa': { step: '2fa', user: 'zapadmin' },
  '/account/login/recovery': { step: '2fa', user: 'zapadmin' },
  '/account/login/enroll': { step: 'enroll', user: 'zapenroll' },
};

function pendingCookie(page) {
  // ZAP global variable keys must be shorter than 30 characters.
  var key = 'vid.pending.' + page.user;
  var cached = ScriptVars.getGlobalVar(key);
  var createdAt = Number(ScriptVars.getGlobalVar(key + '.at') || 0);
  if (cached && Date.now() - createdAt < REUSE_MS) {
    return cached;
  }

  var process = new ProcessBuilder(
    'python3', '/zap/wrk/admin_token.py',
    '--issuer', ISSUER, '--username', page.user,
    '--cafile', '/certs/ca.crt', '--state', '/zap/wrk/out/admin-token-state.json',
    '--pending', page.step
  ).redirectError(ProcessBuilder.Redirect.INHERIT).start();
  var output = new JString(process.getInputStream().readAllBytes(), StandardCharsets.UTF_8).trim();
  if (process.waitFor() !== 0 || !output) {
    throw new Error('admin_token.py --pending failed; see the ZAP output.');
  }

  ScriptVars.setGlobalVar(key, output);
  ScriptVars.setGlobalVar(key + '.at', String(Date.now()));
  return output;
}

function sendingRequest(msg, initiator, helper) {
  var uri = msg.getRequestHeader().getURI();
  var page = PAGES[String(uri.getPath() || '')];
  if (uri.getHost() !== 'valkyrieid.localtest.me' || !page) {
    return;
  }

  var others = String(msg.getRequestHeader().getHeader('Cookie') || '')
    .split(';')
    .map(function (c) { return c.trim(); })
    .filter(function (c) { return c && c.indexOf(COOKIE + '=') !== 0; });
  others.push(COOKIE + '=' + pendingCookie(page));
  msg.getRequestHeader().setHeader('Cookie', others.join('; '));
}

function responseReceived(msg, initiator, helper) {}
