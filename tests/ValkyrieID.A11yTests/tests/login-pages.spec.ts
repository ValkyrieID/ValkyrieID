import { test } from '../support/a11y.ts';
import { defineLoginPageTests } from '../support/login-suite.ts';

test.describe('login pages, default branding', () => {
  defineLoginPageTests('default');
});
