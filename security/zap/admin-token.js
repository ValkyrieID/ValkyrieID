// ZAP httpsender script for the authenticated Admin API scan (admin-api.yaml). Adds an Admin API access token to
// every request to /api/admin. Tokens live 10 minutes; admin_token.py caches the token and signs in again when it
// has less than 3 minutes left, and this script asks it again after 2 minutes, so a token never expires mid-scan.
// See docs/security/zap.md.

var ScriptVars = Java.type('org.zaproxy.zap.extension.script.ScriptVars');
var ProcessBuilder = Java.type('java.lang.ProcessBuilder');
var JString = Java.type('java.lang.String');
var StandardCharsets = Java.type('java.nio.charset.StandardCharsets');

var ISSUER = 'https://valkyrieid.localtest.me:9443';
var RECHECK_MS = 2 * 60 * 1000;

function token() {
  var cached = ScriptVars.getGlobalVar('valkyrieid.adminToken');
  var checkedAt = Number(ScriptVars.getGlobalVar('valkyrieid.adminTokenCheckedAt') || 0);
  if (cached && Date.now() - checkedAt < RECHECK_MS) {
    return cached;
  }

  var process = new ProcessBuilder(
    'python3', '/zap/wrk/admin_token.py',
    '--issuer', ISSUER, '--username', 'zapadmin',
    '--cafile', '/certs/ca.crt', '--state', '/zap/wrk/out/admin-token-state.json',
    '--min-validity', '180'
  ).redirectError(ProcessBuilder.Redirect.INHERIT).start();
  var output = new JString(process.getInputStream().readAllBytes(), StandardCharsets.UTF_8).trim();
  if (process.waitFor() !== 0 || !output) {
    throw new Error('admin_token.py failed; see the ZAP output.');
  }

  ScriptVars.setGlobalVar('valkyrieid.adminToken', output);
  ScriptVars.setGlobalVar('valkyrieid.adminTokenCheckedAt', String(Date.now()));
  return output;
}

function sendingRequest(msg, initiator, helper) {
  var uri = msg.getRequestHeader().getURI();
  if (uri.getHost() === 'valkyrieid.localtest.me' && String(uri.getPath() || '').indexOf('/api/admin') === 0) {
    msg.getRequestHeader().setHeader('Authorization', 'Bearer ' + token());
  }
}

function responseReceived(msg, initiator, helper) {}
