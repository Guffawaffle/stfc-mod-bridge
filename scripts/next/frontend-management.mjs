import { runFrontendSuite } from './frontend-suite.mjs';

runFrontendSuite({
  name: 'frontend-management',
  document: 'docs/next/FRONTEND_MANAGEMENT.md',
  focused: { driver: 'scripts/next/frontend-management-tests.mjs', schema: 'bridge-frontend-management-tests/v1', tests: 190, files: 9 },
  browser: { driver: 'scripts/next/frontend-management-browser.mjs', schema: 'bridge-frontend-management-browser/v1', checks: 30 },
});
