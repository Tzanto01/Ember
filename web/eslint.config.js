import { defineConfig } from 'eslint/config';
import tseslint from 'typescript-eslint';
import reactHooks from 'eslint-plugin-react-hooks';

const reactHooksRecommended = reactHooks.configs.flat['recommended-latest'];

export default defineConfig(
  {
    ignores: ['dist/**', 'playwright-report/**', 'test-results/**'],
  },
  {
    // Playwright specs and bun scripts are outside tsconfig.json, which type-aware lint needs.
    ignores: ['e2e/**', 'scripts/**'],
  },
  {
    // Owned by a parallel slice in this wave; lint after merge.
    ignores: ['src/lib/guide/**', 'src/locales/**'],
  },
  tseslint.configs.strictTypeChecked,
  {
    ...reactHooksRecommended,
    rules: Object.fromEntries(
      Object.keys(reactHooksRecommended.rules).map((rule) => [rule, 'error']),
    ),
  },
  {
    linterOptions: { reportUnusedDisableDirectives: 'error' },
    languageOptions: {
      parserOptions: {
        projectService: { allowDefaultProject: ['eslint.config.js', 'public/*.js'] },
        tsconfigRootDir: import.meta.dirname,
      },
    },
    rules: {
      // Arrow shorthand like `onClick={() => setOpen(true)}` and `return respond()` in void callbacks are idiomatic here; the rule still flags void values used inside expressions.
      '@typescript-eslint/no-confusing-void-expression': [
        'error',
        { ignoreArrowShorthand: true, ignoreVoidReturningFunctions: true },
      ],
      // Every reject passes on an IndexedDB `error` (DOMException | null) or a caught value; wrapping them would change what callers catch.
      '@typescript-eslint/prefer-promise-reject-errors': 'off',
    },
  },
  {
    files: ['src/**/*.test.ts', 'src/**/*.test.tsx', 'src/test-setup.ts', 'src/test-utils.tsx'],
    rules: {
      // Tests assert fixture presence with `!`; a missing value fails the test anyway.
      '@typescript-eslint/no-non-null-assertion': 'off',
      // `expect(fake.method)` on vi.fn() fakes is the Vitest idiom and never calls the method unbound.
      '@typescript-eslint/unbound-method': 'off',
      // Async fakes implement Promise-returning interfaces without needing an await.
      '@typescript-eslint/require-await': 'off',
      // Tests read back JSON.parse output and mock.calls payloads, which are `any` by design.
      '@typescript-eslint/no-unsafe-assignment': 'off',
      '@typescript-eslint/no-unsafe-member-access': 'off',
      '@typescript-eslint/no-unsafe-return': 'off',
      '@typescript-eslint/no-unsafe-call': 'off',
      // Tests spread localStorage, navigator and ASCII literals into plain fixtures on purpose.
      '@typescript-eslint/no-misused-spread': 'off',
      // Empty classes stand in for browser globals such as PushManager.
      '@typescript-eslint/no-extraneous-class': ['error', { allowEmpty: true }],
    },
  },
  {
    // TEMPORARY:rules with findings left to fix; this block must be empty when the lint pass is done.
    name: 'temporary-todo',
    rules: {
      '@typescript-eslint/require-await': 'off',
      '@typescript-eslint/no-non-null-assertion': 'off',
      '@typescript-eslint/restrict-template-expressions': 'off',
      '@typescript-eslint/no-unnecessary-type-assertion': 'off',
      '@typescript-eslint/only-throw-error': 'off',
      '@typescript-eslint/no-unnecessary-condition': 'off',
      '@typescript-eslint/no-deprecated': 'off',
      '@typescript-eslint/no-unused-vars': 'off',
      'react-hooks/set-state-in-effect': 'off',
      'react-hooks/exhaustive-deps': 'off',
      'react-hooks/refs': 'off',
    },
  },
);
