// Starts the server under test for Playwright's webServer: publishes it (unless A11Y_SKIP_PUBLISH=1), starts the SMTP
// sink, and runs the published server on a fresh SQLite database.
import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, rmSync } from 'node:fs';
import {
  dataDir,
  exportCertificate,
  mailDir,
  repoRoot,
  serverDir,
  serverDll,
  serverEnvironment,
  smtpPort,
} from '../support/env.ts';
import { startSmtpSink } from '../support/smtp-sink.ts';

if (process.env['A11Y_SKIP_PUBLISH'] !== '1') {
  const publish = spawnSync(
    'dotnet',
    ['publish', 'src/ValkyrieID.Server', '-c', 'Release', '-o', serverDir, '--nologo'],
    { cwd: repoRoot, stdio: 'inherit', shell: process.platform === 'win32' },
  );
  if (publish.status !== 0) {
    process.exit(publish.status ?? 1);
  }
}

rmSync(dataDir, { recursive: true, force: true });
rmSync(mailDir, { recursive: true, force: true });
mkdirSync(dataDir, { recursive: true });
exportCertificate();

const sink = await startSmtpSink(smtpPort, mailDir);
const server = spawn('dotnet', [serverDll], {
  cwd: serverDir,
  env: { ...process.env, ...serverEnvironment },
  stdio: 'inherit',
});

const stop = () => {
  server.kill();
  sink.close();
};
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
server.on('exit', (code) => {
  sink.close();
  process.exit(code ?? 0);
});
