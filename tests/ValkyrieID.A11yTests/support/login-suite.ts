import { cli, readState } from './env.ts';
import { checkCopy, checkPage, expect, tabThrough, test } from './a11y.ts';
import { linkFromMail } from './mail.ts';
import { freshTotp } from './totp.ts';
import { admin, createUser, signInAsAdmin, submitLogin } from './users.ts';

/** PKCE S256 challenge for a fixed verifier; the flows below never redeem the code. */
const authorizeQuery = new URLSearchParams({
  client_id: 'webapp',
  response_type: 'code',
  scope: 'openid profile',
  redirect_uri: 'http://127.0.0.1:8765/callback',
  state: 'state',
  nonce: 'nonce',
  code_challenge: 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGywjDLkqA',
  code_challenge_method: 'S256',
});

/**
 * Every server-rendered page and state. Runs once with the default branding and once with the custom branding
 * fixture (fixtures/branding); `variant` keeps the users of the two runs apart.
 */
export function defineLoginPageTests(variant: string): void {
  test('sign-in, 2FA and recovery code pages', async ({ page }) => {
    await page.goto(`/connect/authorize?${authorizeQuery.toString()}`);
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
    await expect(page).toHaveTitle(/^Sign in – /);
    // The entry animation plays on the fresh GET of the sign-in page only; the art panel is decorative.
    await expect(page.locator('html')).toHaveClass('entry');
    await expect(page.locator('.art')).toHaveAttribute('aria-hidden', 'true');
    await checkPage(page, 'sign-in');

    // 2.4.3, 2.4.7, 2.4.11: a logical order, with a visible, unobscured focus indicator. The first field has
    // the focus already (autofocus).
    await expect(page.getByLabel('Username or email')).toBeFocused();
    expect(await tabThrough(page, 3)).toEqual(['Password', 'Sign in', 'Forgot your password?']);

    await submitLogin(page, admin.userName, 'wrong password');
    await expect(page.getByRole('alert')).toBeVisible();
    await expect(page.locator('html')).not.toHaveClass(/entry/);
    await checkPage(page, 'sign-in with errors');

    await submitLogin(page, admin.userName, admin.password);
    await expect(page.getByRole('heading', { name: 'Two-factor authentication' })).toBeVisible();
    await expect(page.locator('html')).not.toHaveClass(/entry/);
    await checkPage(page, '2FA');
    // 3.3.8: the code can be pasted and filled in by password managers and authenticator apps.
    const code = page.getByLabel('Code from your authenticator app');
    await expect(code).toHaveAttribute('autocomplete', 'one-time-code');
    await expect(code).toHaveAttribute('inputmode', 'numeric');
    await code.fill('000000');
    await page.getByRole('button', { name: 'Verify' }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await checkPage(page, '2FA with errors');

    await page.getByRole('link', { name: 'Use a recovery code instead' }).click();
    await expect(page.getByRole('heading', { name: 'Use a recovery code' })).toBeVisible();
    await checkPage(page, 'recovery code');
    await page.getByLabel('Recovery code').fill('not-a-code');
    await page.getByRole('button', { name: 'Verify' }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await checkPage(page, 'recovery code with errors');

    await page.getByRole('link', { name: 'Use your authenticator app instead' }).click();
    await page
      .getByLabel('Code from your authenticator app')
      .fill(await freshTotp(readState().adminTotpSecret));
    const callback = page.waitForRequest(/^http:\/\/127\.0\.0\.1:8765\/callback\?code=/);
    await page.getByRole('button', { name: 'Verify' }).click();
    await callback;
  });

  test('protocol pages', async ({ browser, page }) => {
    await page.goto(
      '/connect/authorize?client_id=unknown&response_type=code&redirect_uri=https%3A%2F%2Fexample.test',
    );
    await expect(page.getByRole('heading', { name: 'Sign-in error' })).toBeVisible();
    await checkPage(page, 'protocol error');

    // form_post without JavaScript shows a button instead of submitting by script. axe needs JavaScript, so the
    // page is checked as a copy without the script.
    const context = await browser.newContext({ javaScriptEnabled: false });
    const noScript = await context.newPage();
    await signInAsAdmin(
      noScript,
      `/connect/authorize?${authorizeQuery.toString()}&response_mode=form_post`,
    );
    await expect(
      noScript.getByRole('heading', { name: 'Continue to the application' }),
    ).toBeVisible();
    await expect(noScript.getByRole('button', { name: 'Continue' })).toBeVisible();
    await checkCopy(page, await noScript.content(), 'form_post response');
    await context.close();
  });

  test('account pages', async ({ page }) => {
    const user = await createUser(`alice-${variant}`);
    await page.goto('/account');
    await submitLogin(page, user.userName, user.password);
    await expect(page.getByRole('heading', { name: 'Your account' })).toBeVisible();
    await checkPage(page, 'account');
    const accountNav = page.getByRole('navigation', { name: 'Account' });
    await expect(accountNav.getByRole('link', { name: 'Overview' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await expect(page.locator('html')).not.toHaveClass(/entry/);
    // The skip link comes first and moves past the top bar and the navigation (2.4.1).
    await page.keyboard.press('Tab');
    await expect(page.getByRole('link', { name: 'Skip to content' })).toBeFocused();

    await accountNav.getByRole('link', { name: 'Profile', exact: true }).click();
    await expect(accountNav.getByRole('link', { name: 'Profile' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await checkPage(page, 'profile');
    await page.getByLabel('Display name').fill('Alice');
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByRole('status')).toBeVisible();
    await checkPage(page, 'profile saved');

    await page.goto('/account/password');
    await checkPage(page, 'change password');
    await page.getByLabel('Current password').fill('wrong password');
    await page.getByLabel('New password', { exact: true }).fill('a new password for alice');
    await page.getByLabel('Confirm new password').fill('a new password for alice');
    await page.getByRole('button', { name: 'Change password' }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await checkPage(page, 'change password with errors');
    await page.getByLabel('Current password').fill(user.password);
    await page.getByLabel('New password', { exact: true }).fill(`${user.password} 2`);
    await page.getByLabel('Confirm new password').fill(`${user.password} 2`);
    await page.getByRole('button', { name: 'Change password' }).click();
    await expect(page.getByRole('status')).toBeVisible();
    await checkPage(page, 'password changed');
    user.password = `${user.password} 2`;

    await page.goto('/account/two-factor');
    await checkPage(page, '2FA setup');
    const secret = (await page.locator('#totp-secret').textContent())?.trim() ?? '';
    await page.getByLabel('Code from your authenticator app').fill('000000');
    await page.getByLabel('Current password').fill(user.password);
    await page.getByRole('button', { name: 'Turn on two-factor authentication' }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await checkPage(page, '2FA setup with errors');
    await page.getByLabel('Code from your authenticator app').fill(await freshTotp(secret));
    await page.getByLabel('Current password').fill(user.password);
    await page.getByRole('button', { name: 'Turn on two-factor authentication' }).click();
    await expect(page.getByRole('heading', { name: 'Your recovery codes' })).toBeVisible();
    await checkPage(page, '2FA turned on');
    await page.goto('/account/two-factor');
    await checkPage(page, '2FA on');

    await page.goto('/account/sessions');
    await checkPage(page, 'sessions');

    // Email: verification and change, following the links from the emails.
    await page.goto('/account/email');
    await checkPage(page, 'email');
    let since = Date.now();
    await page.getByRole('button', { name: 'Send verification link' }).click();
    await expect(page.getByRole('status')).toBeVisible();
    await checkPage(page, 'email verification sent');
    await page.goto(await linkFromMail(user.email, '/account/verify-email', since));
    await checkPage(page, 'verify email');
    await page.getByRole('button', { name: 'Verify email address' }).click();
    await expect(page.getByRole('status')).toBeVisible();
    await checkPage(page, 'email verified');
    await page.goto('/account/verify-email?token=invalid');
    await page.getByRole('button', { name: 'Verify email address' }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await checkPage(page, 'verify email with an invalid link');

    await page.goto('/account/email');
    since = Date.now();
    const newEmail = `${user.userName}.new@example.test`;
    await page.getByLabel('New email address').fill(newEmail);
    await page.getByLabel('Current password').fill(user.password);
    await page.getByRole('button', { name: 'Change email address' }).click();
    await expect(page.getByRole('status')).toBeVisible();
    await checkPage(page, 'email change requested');
    await page.goto(await linkFromMail(newEmail, '/account/confirm-email', since));
    await checkPage(page, 'confirm email change');
    await page.getByRole('button', { name: 'Change email address' }).click();
    await expect(page.getByRole('status')).toBeVisible();
    await checkPage(page, 'email changed');

    await page.goto('/account/logout');
    await checkPage(page, 'sign out');
    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page.getByRole('heading', { name: 'Signed out' })).toBeVisible();
    await checkPage(page, 'signed out');
  });

  test('password reset', async ({ page }) => {
    const user = await createUser(`bob-${variant}`);
    await page.goto('/account/login');
    await page.getByRole('link', { name: 'Forgot your password?' }).click();
    await checkPage(page, 'forgot password');
    const since = Date.now();
    await page.getByLabel('Username or email').fill(user.userName);
    await page.getByRole('button', { name: 'Send reset link' }).click();
    await expect(page.getByRole('status')).toBeVisible();
    await checkPage(page, 'reset link sent');

    await page.goto(await linkFromMail(user.email, '/account/reset-password', since));
    await checkPage(page, 'reset password');
    await page.getByLabel('New password', { exact: true }).fill('a new password for bob');
    await page.getByLabel('Confirm new password').fill('something else');
    await page.getByRole('button', { name: 'Reset password' }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await checkPage(page, 'reset password with errors');
    await page.getByLabel('New password', { exact: true }).fill('a new password for bob');
    await page.getByLabel('Confirm new password').fill('a new password for bob');
    await page.getByRole('button', { name: 'Reset password' }).click();
    await expect(page.getByRole('status')).toBeVisible();
    await checkPage(page, 'password reset');

    await page.goto('/account/reset-password?token=invalid');
    await checkPage(page, 'reset password with an invalid link');
  });

  test('email verification at sign-in', async ({ page }) => {
    const user = await createUser(`carol-${variant}`);
    await cli(['settings', 'set', 'Email:VerificationAtSignIn', 'Require']);
    try {
      // Applies when signing in to an application. Settings:ReloadInterval is 1 s in the test server.
      await expect
        .poll(
          async () => {
            await page.context().clearCookies();
            await page.goto(`/connect/authorize?${authorizeQuery.toString()}`);
            const returned = page.waitForRequest(/^http:\/\/127\.0\.0\.1:8765\//);
            await submitLogin(page, user.userName, user.password);
            await Promise.race([page.waitForURL(/verify-required/), returned]);
            return page.url();
          },
          { timeout: 15_000 },
        )
        .toContain('/account/verify-required');
      await page.waitForLoadState();
      await checkPage(page, 'verification required');
      await page.getByRole('button', { name: 'Send verification link' }).click();
      await expect(page.getByRole('status')).toBeVisible();
      await checkPage(page, 'verification required, link sent');
    } finally {
      await cli(['settings', 'reset', 'Email:VerificationAtSignIn']);
    }
  });

  test('sign-out requested by an application', async ({ page }) => {
    const user = await createUser(`dave-${variant}`);
    await page.goto('/account');
    await submitLogin(page, user.userName, user.password);
    await page.goto(
      '/connect/endsession?client_id=webapp&post_logout_redirect_uri=http%3A%2F%2F127.0.0.1%3A8765%2Fsigned-out',
    );
    await expect(page.getByRole('heading', { name: 'Sign out' })).toBeVisible();
    await checkPage(page, 'sign out requested by an application');
    await page.goto(
      '/connect/endsession?client_id=webapp&post_logout_redirect_uri=https%3A%2F%2Fevil.test',
    );
    await checkPage(page, 'invalid sign-out request');
  });
}
