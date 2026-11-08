import { defineConfig, devices } from '@playwright/test';
import { adminStorageState, baseUrl } from './support/env.ts';

export default defineConfig({
  testDir: './tests',
  // One worker: the tests share one server, its users and its runtime settings (branding).
  workers: 1,
  fullyParallel: false,
  forbidOnly: !!process.env['CI'],
  retries: 0,
  timeout: 120_000,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL: baseUrl,
    // The test server uses the .NET development certificate, which needn't be trusted.
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
    ...devices['Desktop Chrome'],
  },
  projects: [
    { name: 'setup', testMatch: /setup\.spec\.ts/ },
    { name: 'login', testMatch: /login-pages\.spec\.ts/, dependencies: ['admin-dark'] },
    {
      name: 'admin',
      testMatch: /admin-ui\.spec\.ts/,
      dependencies: ['admin-dark'],
      use: { storageState: adminStorageState },
    },
    {
      name: 'login-dark',
      testMatch: /login-dark\.spec\.ts/,
      dependencies: ['login'],
      use: { colorScheme: 'dark' },
    },
    {
      name: 'admin-dark',
      testMatch: /admin-dark\.spec\.ts/,
      // Before the other suites, which sign the admin's stored session out.
      dependencies: ['setup'],
      use: { storageState: adminStorageState, colorScheme: 'dark' },
    },
    {
      name: 'branded',
      testMatch: /branded\.spec\.ts/,
      // Last: the branding settings stay in place for the rest of the run.
      dependencies: ['login', 'admin', 'login-dark', 'admin-dark'],
    },
  ],
  webServer: {
    command: 'node scripts/serve.ts',
    url: `${baseUrl}/health/ready`,
    ignoreHTTPSErrors: true,
    // Publishing builds the server and the admin UI.
    timeout: 600_000,
    reuseExistingServer: false,
    stdout: 'pipe',
    stderr: 'pipe',
  },
});
