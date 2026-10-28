import js from '@eslint/js';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';

// Secure-context APIs are unavailable over plain HTTP on the LAN (3.3 spec section 3), and
// browser storage must never hold credentials: these rules keep both out of the code.
const restrictedProperties = [
  { object: 'navigator', property: 'clipboard', message: 'Unavailable over plain HTTP. Use copyText() from lib/clipboard.' },
  { object: 'crypto', property: 'randomUUID', message: 'Unavailable over plain HTTP. Use localId() from lib/ids.' },
  { object: 'window', property: 'localStorage', message: 'Only lib/prefs.ts may use localStorage.' },
  { object: 'window', property: 'sessionStorage', message: 'Do not use sessionStorage.' },
];

export default tseslint.config(
  { ignores: ['dist', 'node_modules', 'playwright-report', 'test-results', 'src/api/schema.d.ts'] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  jsxA11y.flatConfigs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: { globals: { ...globals.browser, ...globals.node } },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'warn',
      'no-restricted-properties': ['error', ...restrictedProperties],
      'no-restricted-globals': [
        'error',
        { name: 'localStorage', message: 'Only lib/prefs.ts may use localStorage.' },
        { name: 'sessionStorage', message: 'Do not use sessionStorage.' },
      ],
      'no-restricted-syntax': [
        'error',
        {
          selector: "JSXAttribute[name.name='dangerouslySetInnerHTML']",
          message: 'Never render raw HTML. Use the markdown renderer or plain text.',
        },
      ],
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }],
    },
  },
  {
    // The one module allowed to touch localStorage (a non-sensitive UI preference).
    files: ['src/lib/prefs.ts'],
    rules: { 'no-restricted-globals': 'off', 'no-restricted-properties': 'off' },
  },
  {
    // Tests stub the secure-context APIs to prove the UI works without them.
    files: ['tests/**', 'e2e/**'],
    rules: { 'no-restricted-properties': 'off', 'no-restricted-globals': 'off' },
  },
);
