import { spawn, spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

/** Shared by the test server (scripts/serve.ts) and the tests, which run CLI commands against the same database. */
const here = dirname(fileURLToPath(import.meta.url));
export const packageDir = resolve(here, '..');
export const repoRoot = resolve(packageDir, '..', '..');
export const runDir = join(packageDir, '.run');
export const serverDir = join(runDir, 'server');
export const dataDir = join(runDir, 'data');
export const mailDir = join(runDir, 'mail');
export const brandingDir = join(packageDir, 'fixtures', 'branding');
export const stateFile = join(runDir, 'state.json');
export const adminStorageState = join(runDir, 'admin.json');

export const port = Number(process.env['A11Y_PORT'] ?? 5199);
export const smtpPort = Number(process.env['A11Y_SMTP_PORT'] ?? 2525);
export const baseUrl = `https://localhost:${port}`;

/** Test-only key; the database is recreated on every run. */
const masterKey = 'wazz+xAkvcBH/Tac+8ZA2DfVezVTn0y54f2svNv3KeI=';
const unlimited = '100000';

/** The login pages' cookies are `Secure`, so the server runs on HTTPS with a development certificate. */
export const certificateFile = join(runDir, 'localhost.pfx');
const certificatePassword = 'valkyrieid-a11y';

export const serverEnvironment: Record<string, string> = {
  ASPNETCORE_ENVIRONMENT: 'Production',
  ASPNETCORE_URLS: baseUrl,
  Kestrel__Certificates__Default__Path: certificateFile,
  Kestrel__Certificates__Default__Password: certificatePassword,
  DOTNET_NOLOGO: 'true',
  Issuer__Url: baseUrl,
  MasterKey__Current: masterKey,
  Database__Provider: 'Sqlite',
  Database__ConnectionString: `Data Source=${join(dataDir, 'valkyrieid.db')}`,
  Logging__LogLevel__Default: 'Warning',
  'Logging__LogLevel__Microsoft.AspNetCore.Antiforgery': 'Error',
  'Logging__LogLevel__Microsoft.EntityFrameworkCore': 'Error',
  Logging__Console__FormatterName: 'simple',
  Settings__ReloadInterval: '00:00:01',
  Branding__Directory: brandingDir,
  Smtp__Host: 'localhost',
  Smtp__Port: String(smtpPort),
  Smtp__Security: 'None',
  Smtp__FromAddress: 'valkyrieid@example.test',
  Smtp__PollInterval: '00:00:01',
  // Every test signs in several times from the same address.
  ...Object.fromEntries(
    ['Login', 'LoginAccount', 'Token', 'Authorize', 'TwoFactor', 'Email', 'Account'].map((name) => [
      `RateLimits__${name}__PermitLimit`,
      unlimited,
    ]),
  ),
};

export const serverDll = join(serverDir, 'valkyrieid.dll');

/** Exports the .NET development certificate (creating it if needed) for the test server. */
export function exportCertificate(): void {
  const result = spawnSync(
    'dotnet',
    ['dev-certs', 'https', '--export-path', certificateFile, '--password', certificatePassword],
    { stdio: 'inherit' },
  );
  if (result.status !== 0) {
    throw new Error('dotnet dev-certs https failed.');
  }
}

/** Runs a `valkyrieid` CLI command; `input` is written to standard input (e.g. a password). */
export function cli(args: string[], input?: string): Promise<string> {
  if (!existsSync(serverDll)) {
    throw new Error(`${serverDll} not found; the test server publishes it.`);
  }

  return new Promise((resolvePromise, reject) => {
    const child = spawn('dotnet', [serverDll, ...args], {
      cwd: serverDir,
      env: { ...process.env, ...serverEnvironment },
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    let output = '';
    child.stdout.on('data', (chunk: Buffer) => (output += chunk.toString()));
    child.stderr.on('data', (chunk: Buffer) => (output += chunk.toString()));
    child.on('error', reject);
    child.on('close', (code) =>
      code === 0
        ? resolvePromise(output)
        : reject(new Error(`valkyrieid ${args.join(' ')} exited with ${code}:\n${output}`)),
    );
    child.stdin.end(input === undefined ? undefined : `${input}\n`);
  });
}

export interface RunState {
  adminTotpSecret: string;
  adminRecoveryCodes: string[];
}

export function readState(): RunState {
  return JSON.parse(readFileSync(stateFile, 'utf8')) as RunState;
}
