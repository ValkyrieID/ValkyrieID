// @ts-check
import eslint from '@eslint/js';
import { defineConfig } from 'eslint/config';
import prettier from 'eslint-config-prettier';
import tseslint from 'typescript-eslint';

export default defineConfig([
  { ignores: ['node_modules/', '.run/', 'test-results/', 'playwright-report/'] },
  {
    files: ['**/*.ts'],
    extends: [
      eslint.configs.recommended,
      tseslint.configs.strict,
      tseslint.configs.stylistic,
      prettier,
    ],
    rules: {
      '@typescript-eslint/no-explicit-any': 'error',
    },
  },
]);
