import { expect, type Page } from '@playwright/test';
import { cli, readState } from './env.ts';
import { freshTotp } from './totp.ts';

export const admin = {
  userName: 'admin',
  email: 'admin@example.test',
  password: 'correct horse battery staple admin',
};

export const alicePassword = 'correct horse battery staple alice';

/** Creates a user with the CLI and returns its credentials. */
export async function createUser(
  userName: string,
): Promise<{ userName: string; email: string; password: string }> {
  const user = {
    userName,
    email: `${userName}@example.test`,
    password: `correct horse battery ${userName}`,
  };
  await cli(['user', 'create', userName, '--email', user.email], user.password);
  return user;
}

/** Fills in the sign-in form on the current page. */
export async function submitLogin(page: Page, identifier: string, password: string): Promise<void> {
  await page.getByLabel('Username or email').fill(identifier);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

/** Signs in as the admin, including the second factor. */
export async function signInAsAdmin(page: Page, returnUrl = '/account'): Promise<void> {
  await page.goto(`/account/login?returnUrl=${encodeURIComponent(returnUrl)}`);
  await submitLogin(page, admin.userName, admin.password);
  await expect(page.getByRole('heading', { name: 'Two-factor authentication' })).toBeVisible();
  await page
    .getByLabel('Code from your authenticator app')
    .fill(await freshTotp(readState().adminTotpSecret));
  await page.getByRole('button', { name: 'Verify' }).click();
}
