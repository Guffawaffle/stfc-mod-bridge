import { runFrontendSuite } from './frontend-suite.mjs';

runFrontendSuite({
  name: 'frontend-settings',
  document: 'docs/next/FRONTEND_SETTINGS.md',
  focused: { driver: 'scripts/next/frontend-settings-tests.mjs', schema: 'bridge-frontend-settings-tests/v1', tests: 168, files: 8 },
  browser: { driver: 'scripts/next/frontend-settings-browser.mjs', schema: 'bridge-frontend-settings-browser/v1', checks: 21 },
});
