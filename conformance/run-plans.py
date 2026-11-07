"""Runs the conformance plans against ValkyrieID with the suite's scripts/run-test-plan.py.

Runs in the runner service of docker-compose.yml (started by run.sh / run.ps1), after setup.sh --docker has written
the plan configurations to out/. Standard library only; the suite's own requirements are installed into /suite.

- Adds the browser automation (sign-in form, sign-out confirmation, screenshots of ValkyrieID's error and signed-out
  pages) to the plan configurations.
- Turns expected-results.json into the suite's expected failures and skips files.
- Runs the plans, then checks every non-PASSED result against expected-results.json, so a result that isn't listed
  fails the run, and so does a listed result that no longer happens.
- Writes out/results/summary.json and summary.md, next to the exported logs (one zip per plan).
"""

import copy
import fnmatch
import json
import os
import pathlib
import shutil
import subprocess
import sys
import zipfile

WORK = pathlib.Path('/work')
OUT = WORK / 'out'
RESULTS = OUT / 'results'
SUITE_VERSION = os.environ.get('SUITE_VERSION', 'release-v5.3.4')
SUITE = pathlib.Path('/suite') / SUITE_VERSION
SUITE_URL = 'https://localhost.emobix.co.uk:8443/'
ISSUER = 'https://valkyrieid.localtest.me:9443'

BASIC = 'oidcc-basic-certification-test-plan[server_metadata=discovery][client_registration=static_client]'
FORM_POST = 'oidcc-formpost-basic-certification-test-plan[server_metadata=discovery][client_registration=static_client]'
CONFIG = 'oidcc-config-certification-test-plan'
RP_LOGOUT = 'oidcc-rp-initiated-logout-certification-test-plan[response_type=code][client_registration=static_client]'
BACKCHANNEL = 'oidcc-backchannel-rp-initiated-logout-certification-test-plan[response_type=code][client_registration=static_client]'
# The certification plans fix client_secret_basic (except one module of Basic OP), so client_secret_post runs the
# Basic OP code-flow modules of the comprehensive plan.
SECRET_POST = (
    'oidcc-test-plan[client_auth_type=client_secret_post][response_type=code][response_mode=default]'
    '[client_registration=static_client]:'
    'oidcc-server,oidcc-refresh-token,oidcc-userinfo-get,oidcc-userinfo-post-header,oidcc-userinfo-post-body,'
    'oidcc-codereuse,oidcc-codereuse-30seconds,oidcc-ensure-request-with-valid-pkce-succeeds'
)

PLANS = {
    # Every run: nightly, manual, and tags.
    'default': [(BASIC, 'basic-op.json'), (CONFIG, 'config-op.json'),
                (RP_LOGOUT, 'rp-initiated-logout.json'), (BACKCHANNEL, 'backchannel-logout.json')],
    # Release tags add the client_secret_post and form_post variants.
    'full': [(BASIC, 'basic-op.json'), (CONFIG, 'config-op.json'),
             (RP_LOGOUT, 'rp-initiated-logout.json'), (BACKCHANNEL, 'backchannel-logout.json'),
             (SECRET_POST, 'basic-op.json'), (FORM_POST, 'basic-op.json')],
}


def sign_in(user):
    """Fills in and submits ValkyrieID's sign-in form, if it is shown (it isn't while the session is active)."""
    return {
        'task': 'Sign in',
        'optional': True,
        'match': f'{ISSUER}/account/login*',
        'commands': [
            ['text', 'id', 'identifier', user['username']],
            ['text', 'id', 'password', user['password']],
            ['click', 'css', 'form button[type=submit]'],
        ],
    }


def authorize(user, screenshot=None):
    """Signs in when asked and waits for the suite's callback. `screenshot` uploads the sign-in page first."""
    login = sign_in(user)
    if screenshot:
        login['commands'].insert(0, ['wait', 'xpath', '//*', 10, 'Sign in', screenshot])
    return {
        'match': f'{ISSUER}/connect/authorize*',
        'tasks': [
            login,
            {'task': 'Verify Complete', 'match': '*/test/*/callback*', 'commands': [['wait', 'id', 'submission_complete', 10]]},
        ],
    }


def authorize_error():
    """ValkyrieID shows an error page instead of redirecting to an unregistered redirect URI (RFC 6749 §4.1.2.1)."""
    return {
        'match': f'{ISSUER}/connect/authorize*',
        'tasks': [{
            'task': 'Expect the sign-in error page',
            'match': f'{ISSUER}/connect/authorize*',
            'commands': [['wait', 'xpath', '//*', 10, 'Sign-in error', 'update-image-placeholder']],
        }],
    }


def end_session(redirected):
    """
    Confirms the sign-out (ValkyrieID always asks, RP-Initiated Logout 1.0 §2). Then either the browser returns to the
    suite's post-logout redirect URI, or, when the request may not be redirected (§3, §4), ValkyrieID's signed-out
    page is shown and uploaded as a screenshot.
    """
    tasks = [{
        'task': 'Confirm sign-out',
        'match': f'{ISSUER}/account/logout*',
        'commands': [['click', 'css', 'form button[type=submit]']],
    }]
    if redirected:
        tasks.append({'task': 'Verify Complete', 'match': '*/test/*/post_logout_redirect*'})
    else:
        tasks.append({
            'task': 'Expect the signed-out page',
            'match': f'{ISSUER}/account/logout*',
            'commands': [['wait', 'xpath', '//*', 10, 'Signed out', 'update-image-placeholder']],
        })
    return {'match': f'{ISSUER}/connect/endsession*', 'tasks': tasks}


def browser_config(user):
    normal = [authorize(user), end_session(redirected=True)]
    override = {
        # The suite asks for a screenshot showing that the user was asked to sign in again.
        'oidcc-prompt-login': {'browser': [authorize(user, 'update-image-placeholder-optional')]},
        'oidcc-max-age-1': {'browser': [authorize(user, 'update-image-placeholder-optional')]},
        'oidcc-ensure-registered-redirect-uri': {'browser': [authorize_error()]},
        # The redirect URI is only in the request object, which isn't supported, so the request has none.
        'oidcc-ensure-request-object-with-redirect-uri': {'browser': [authorize_error()]},
    }
    for module in ['oidcc-rp-initiated-logout-bad-post-logout-redirect-uri',
                   'oidcc-rp-initiated-logout-query-added-to-post-logout-redirect-uri',
                   'oidcc-rp-initiated-logout-modified-id-token-hint',
                   'oidcc-rp-initiated-logout-bad-id-token-hint',
                   'oidcc-rp-initiated-logout-no-id-token-hint',
                   'oidcc-rp-initiated-logout-no-params',
                   'oidcc-rp-initiated-logout-no-post-logout-redirect-uri',
                   'oidcc-rp-initiated-logout-only-state']:
        override[module] = {'browser': [authorize(user), end_session(redirected=False)]}
    return normal, override


def write_configs(plans):
    user = json.loads((OUT / 'test-user.json').read_text())
    browser, override = browser_config(user)
    target = OUT / 'plans'
    target.mkdir(exist_ok=True)
    for name in sorted({config for _, config in plans}):
        config = json.loads((OUT / name).read_text())
        config['browser'] = copy.deepcopy(browser)
        config['override'] = copy.deepcopy(override)
        (target / name).write_text(json.dumps(config, indent=2))
    return target


def plan_name(plan):
    return plan.split('[', 1)[0].split(':', 1)[0]


def matches(entry, plan, module, variant):
    if not fnmatch.fnmatch(plan, entry['plan']) or not fnmatch.fnmatch(module, entry['module']):
        return False
    return all(variant.get(k) == v for k, v in entry.get('variant', {}).items())


def write_expected(expected, plans):
    """
    Translates expected-results.json into run-test-plan.py's expected failures and skips files, for the plans that
    run (it fails on entries that don't match any test).
    """
    ran = {plan_name(p) for p, _ in plans}
    failures, skips = [], []
    for entry in expected:
        if not any(fnmatch.fnmatch(p, entry['plan']) for p in ran):
            continue
        base = {'test-name': entry['module'], 'variant': entry.get('variant') or '*', 'configuration-filename': '*'}
        if entry['result'] == 'SKIPPED':
            skips.append(base)
        for condition in entry.get('conditions', []):
            failures.append({**base, 'current-block': condition.get('block', '*'), 'condition': condition['condition'],
                             'expected-result': condition.get('expected', 'warning')})
    (OUT / 'expected-failures.json').write_text(json.dumps(failures, indent=2))
    (OUT / 'expected-skips.json').write_text(json.dumps(skips, indent=2))


def ensure_suite():
    if not (SUITE / 'scripts' / 'run-test-plan.py').exists():
        shutil.rmtree(SUITE, ignore_errors=True)
        subprocess.run(['git', 'clone', '--quiet', '--depth', '1', '--branch', SUITE_VERSION,
                        'https://gitlab.com/openid/conformance-suite.git', str(SUITE)], check=True)
    subprocess.run([sys.executable, '-m', 'pip', 'install', '--quiet', '--disable-pip-version-check', '--root-user-action=ignore',
                    '-r', str(SUITE / 'scripts' / 'requirements.txt')], check=True)


def read_results(plans):
    """Every test module result in the exported zips: (plan, module, variant, result)."""
    # The zips are named after the plan, its variants, and its id.
    names = sorted({plan_name(p) for p, _ in plans}, key=len, reverse=True)
    results = []
    for archive in sorted(RESULTS.glob('*.zip')):
        plan = next((n for n in names if archive.name.startswith(n + '-')), archive.stem)
        with zipfile.ZipFile(archive) as z:
            for name in z.namelist():
                if not name.endswith('.json') or name.endswith('.sig'):
                    continue
                data = json.loads(z.read(name))
                if 'testInfo' in data:
                    info = data['testInfo']
                elif 'testName' in data and 'result' in data:
                    info = data
                else:
                    continue
                results.append({
                    'plan': plan,
                    'module': info['testName'],
                    'variant': info.get('variant') or {},
                    'result': info.get('result') or 'UNKNOWN',
                    'archive': archive.name,
                })
    return results


def check(expected, results):
    problems, used = [], set()
    for r in results:
        if r['result'] == 'PASSED':
            continue
        hits = [i for i, e in enumerate(expected)
                if e['result'] == r['result'] and matches(e, r['plan'], r['module'], r['variant'])]
        if hits:
            used.update(hits)
            r['reason'] = expected[hits[0]]['reason']
        else:
            problems.append(f"{r['result']}: {r['plan']} {r['module']} {json.dumps(r['variant'], sort_keys=True)} isn't in expected-results.json")
    ran = {plan_name(p) for p, _ in PLANS[os.environ.get('PLANS', 'default')]}
    for i, e in enumerate(expected):
        if i not in used and any(fnmatch.fnmatch(p, e['plan']) for p in ran):
            problems.append(f"Listed in expected-results.json but didn't happen: {e['result']} {e['plan']} {e['module']}")
    return problems


def summarize(results):
    order = {'FAILED': 0, 'UNKNOWN': 1, 'WARNING': 2, 'REVIEW': 3, 'SKIPPED': 4, 'PASSED': 5}
    results.sort(key=lambda r: (r['plan'], order.get(r['result'], 9), r['module']))
    (RESULTS / 'summary.json').write_text(json.dumps({'suite': SUITE_VERSION, 'results': results}, indent=2))
    lines = ['| Plan | Test module | Variant | Result | Reason |', '|---|---|---|---|---|']
    for r in results:
        variant = ', '.join(f'{k}={v}' for k, v in sorted(r['variant'].items()))
        lines.append(f"| `{r['plan']}` | `{r['module']}` | {variant} | {r['result']} | {r.get('reason', '')} |")
    (RESULTS / 'summary.md').write_text('\n'.join(lines) + '\n')


def main():
    plans = PLANS[os.environ.get('PLANS', 'default')]
    expected = json.loads((WORK / 'expected-results.json').read_text())['results']
    ensure_suite()
    configs = write_configs(plans)
    write_expected(expected, plans)
    shutil.rmtree(RESULTS, ignore_errors=True)
    RESULTS.mkdir(parents=True)

    params = []
    for plan, config in plans:
        params += [plan, str(configs / config)]
    env = {**os.environ, 'CONFORMANCE_SERVER': SUITE_URL, 'CONFORMANCE_DEV_MODE': '1'}
    run = subprocess.run([sys.executable, str(SUITE / 'scripts' / 'run-test-plan.py'), '--verbose',
                          '--export-dir', str(RESULTS),
                          '--expected-failures-file', str(OUT / 'expected-failures.json'),
                          '--expected-skips-file', str(OUT / 'expected-skips.json')] + params,
                         env=env, cwd=SUITE / 'scripts')

    results = read_results(plans)
    problems = check(expected, results)
    summarize(results)
    print(f'\n{len(results)} test modules: ' + ', '.join(
        f'{n} {s}' for s in ['PASSED', 'WARNING', 'REVIEW', 'SKIPPED', 'FAILED'] if (n := sum(r['result'] == s for r in results))))
    for p in problems:
        print('::error::' + p if os.environ.get('GITHUB_ACTIONS') else 'ERROR: ' + p)
    if run.returncode != 0:
        print(f'run-test-plan.py exited with {run.returncode}.')
    sys.exit(1 if problems or run.returncode != 0 or not results else 0)


if __name__ == '__main__':
    main()
