import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { projectBrowser } from './browser-runtime.mjs';
import { cleanupOwnedBrowser } from './browser-cleanup.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(process.argv.length, 2, 'Home browser suite accepts no overrides');
assert.equal(process.version, `v${JSON.parse(readFileSync(path.join(root, 'package.json'))).engines.node}`);
const directory = ownedArtifactPath(root, `artifacts/next/frontend-home-browser/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
function files(relative) {
  return readdirSync(ownedArtifactPath(root, relative, 'directory'), { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name)).flatMap(entry => {
    assert.ok(entry.isFile() || entry.isDirectory());
    return entry.isDirectory() ? files(`${relative}/${entry.name}`) : [`${relative}/${entry.name}`];
  });
}
const sourcePaths = [...new Set(['package.json', 'pnpm-lock.yaml', 'ui/package.json', 'ui/vite.config.ts', 'ui/svelte.config.js', 'ui/tsconfig.json',
  ...files('ui/src'), ...files('ui/home-preview'), ...files('ui/tests/home'), ...files('ui/tests/home-preview'), ...files('contracts/fixtures'),
  'contracts/codec/strict-json.mjs', 'assets/stfc-mod-bridge.png', 'scripts/next/frontend-home-browser.mjs', 'scripts/next/browser-runtime.mjs',
  'scripts/next/browser-cleanup.mjs', 'scripts/next/owned-artifact.mjs'])].sort();
const hashSources = () => sourcePaths.map(relative => { const bytes = readFileSync(ownedArtifactPath(root, relative)); return { path: relative, bytes: bytes.length, sha256: sha(bytes) }; });
const before = hashSources();
const shared = JSON.parse(readFileSync(path.join(root, 'contracts/fixtures/generated-manifest.json')));
const fixtureHashes = new Map(shared.files.map(value => [value.path.slice('contracts/fixtures/'.length, -5), value.sha256]));
process.env.PLAYWRIGHT_BROWSERS_PATH = ownedArtifactPath(root, 'artifacts/next/tooling/browsers', 'directory');
const { chromium } = await import('playwright');
const binary = projectBrowser(root);
const expected = ['initial-no-implicit-target', 'ordinary-explicit-review-and-modal', 'review-Stay-reenables-same-context', 'ordinary-admission-is-not-completion',
  'ordinary-observed-completion', 'isolated-explicit-mode-and-capture', 'isolated-readiness-waits-for-completion',
  'focus-exact-session-among-recycled-identities', 'lost-delivery-retains-explicit-replay', 'exact-replay-then-observed-completion',
  'partial-unknown-availability', 'complete-missing-installation', 'offline-refuses-launch', 'recovery-refuses-launch',
  'dirty-view-navigation-preserves-draft', 'dirty-target-Stay-restores-opener', 'dirty-Save-keeps-target-until-completed',
  'confirmed-Discard-applies-target', 'reset-disposes-pending-observation', 'three-maintenance-destinations',
  'normal-screen-no-private-paths', 'home-dark-presentation', 'home-forced-colors-reduced-motion', 'home-text-200-desktop', 'home-text-200-compact'];
assert.equal(new Set(expected).size, expected.length);
const observations = [], screenshots = [], browserErrors = [], externalRequests = [], provenance = [];
let browser, server, page, url, browserVersion, output = '';
const button = name => page.getByRole('button', { name, exact: true });
const dialog = name => page.getByRole('dialog', { name, exact: true });
const state = (action, save = 'idle') => page.getByTestId('home-preview-action-state').filter({ hasText: new RegExp(`^action: ${action}; save: ${save}$`) }).waitFor();
const selected = () => page.getByTestId('home-preview-selection').innerText();
const methods = async () => (await page.getByTestId('home-preview-requests').locator('li').allTextContents()).map(value => value.split(' · ')[0]);
async function picture(name) {
  assert.match(name, /^[a-z0-9-]+$/);
  const file = path.join(directory, `${name}.png`); await page.screenshot({ path: file, fullPage: true });
  const bytes = readFileSync(file); screenshots.push({ name, path: path.relative(root, file).replaceAll('\\', '/'), bytes: bytes.length, sha256: sha(bytes) });
}
async function observed(id, details, screenshot) {
  assert.equal(id, expected[observations.length]);
  assert.match(await page.getByTestId('home-preview-script').innerText(), /fault: none;/, 'Strict fixture transport refused an unexpected request');
  if (screenshot) await picture(screenshot);
  observations.push({ id, result: 'passed', details }); console.log(`Home ${observations.length}/${expected.length}: ${id}`);
}
async function choose(mode) {
  await page.getByLabel('Home scenario', { exact: true }).selectOption(mode);
  await page.getByTestId('home-preview-confidence').filter({ hasText: /^(authoritative|partial)$/ }).waitFor();
  await state('idle'); await page.getByRole('heading', { name: 'Shuttle Bay', exact: true }).waitFor();
  assert.equal(await page.getByLabel('Home scenario', { exact: true }).inputValue(), mode);
  assert.equal(await selected(), 'Not selected');
  const sources = await page.getByTestId('home-preview-provenance').locator('li').allTextContents();
  assert.ok(sources.length > 0);
  for (const value of sources) { const match = value.match(/^([^:]+): ([a-f0-9]{64})$/); assert.ok(match); assert.equal(fixtureHashes.get(match[1]), match[2]); }
  provenance.push({ mode, sources });
}
async function applyTarget(isolated = false) {
  await page.getByLabel('Installation', { exact: true }).selectOption('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
  await page.getByLabel('Profile', { exact: true }).selectOption(isolated ? 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' : 'ordinary');
  await button('Use target').click();
}
async function available() { await button('Review launch').waitFor(); await page.waitForFunction(() => [...document.querySelectorAll('button')].some(value => value.textContent?.trim() === 'Review launch' && !value.disabled)); }
async function admission() { await button('Confirm action').click(); await state('observing'); assert.equal(await page.locator('dialog[open]').count(), 0); }
async function completion() { await button('Refresh action outcome').click(); await state('idle'); }
async function geometry() {
  const value = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
    font: getComputedStyle(document.documentElement).fontSize,
    escaped: [...document.querySelectorAll('button,input,select')].filter(element => element.getClientRects().length).filter(element => {
      const r = element.getBoundingClientRect(); return r.left < -1 || r.right > innerWidth + 1;
    }).map(element => element.id || element.textContent?.trim()) }));
  assert.ok(value.scrollWidth <= value.width + 1); assert.deepEqual(value.escaped, []); return value;
}
try {
  server = spawn(process.execPath, [path.join(root, 'ui/node_modules/vite/bin/vite.js'), '--host', '127.0.0.1', '--port', '0', '--strictPort'],
    { cwd: path.join(root, 'ui'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  url = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Owned Home Vite startup timed out')), 30000);
    const fail = error => { clearTimeout(timer); reject(error); }; server.once('error', fail); server.once('exit', code => fail(new Error(`Owned Home Vite exited ${code}`)));
    const accept = bytes => { output = (output + bytes.toString()).slice(-24000); const match = output.replace(/\u001b\[[0-9;]*m/g, '').match(/http:\/\/127\.0\.0\.1:\d+\//); if (match) { clearTimeout(timer); resolve(match[0]); } };
    server.stdout.on('data', accept); server.stderr.on('data', accept);
  });
  browser = await chromium.launch({ headless: true, executablePath: binary.executable }); browserVersion = browser.version(); assert.equal(browserVersion, binary.expectedVersion);
  const context = await browser.newContext({ viewport: { width: 1440, height: 1100 }, colorScheme: 'light' });
  page = await context.newPage(); page.setDefaultTimeout(8000);
  page.on('pageerror', error => browserErrors.push({ name: error.name, message: error.message.slice(0, 500) }));
  page.on('console', message => { if (message.type() === 'error') browserErrors.push({ name: 'console.error', message: message.text().slice(0, 500) }); });
  await page.route('**/*', route => { const requested = new URL(route.request().url()); if (requested.protocol.startsWith('http') && requested.origin !== new URL(url).origin) { externalRequests.push({ origin: requested.origin, pathname: requested.pathname }); return route.abort(); } return route.continue(); });
  await page.goto(new URL('home-preview/', url).href); await page.getByRole('heading', { name: 'Shuttle Bay', exact: true }).waitFor();
  await page.getByTestId('home-preview-confidence').filter({ hasText: /^authoritative$/ }).waitFor();
  assert.equal(await selected(), 'Not selected'); assert.equal(await button('Review launch').isDisabled(), true);
  assert.equal(await page.getByLabel('Installation', { exact: true }).inputValue(), ''); assert.equal(await page.getByLabel('Profile', { exact: true }).inputValue(), '');
  assert.deepEqual(await methods(), ['snapshot']); await observed('initial-no-implicit-target', { explicitSelectors: true, methods: await methods() }, 'home-initial');

  await applyTarget(); await available(); await button('Review launch').click(); await dialog('Review ordinary launch').waitFor(); await state('review');
  const modal = await dialog('Review ordinary launch').evaluate(value => ({ native: value instanceof HTMLDialogElement, modal: value.matches(':modal'), focusInside: value.contains(document.activeElement) }));
  assert.deepEqual(modal, { native: true, modal: true, focusInside: true });
  assert.match(await dialog('Review ordinary launch').innerText(), /Synthetic install A[\s\S]*Ordinary/);
  assert.equal((await methods()).filter(value => value === 'commit').length, 0);
  await page.waitForTimeout(250); await state('review');
  await observed('ordinary-explicit-review-and-modal', { ...modal, noAutoCommit: true }, 'ordinary-review');
  await dialog('Review ordinary launch').getByRole('button', { name: 'Stay', exact: true }).click(); await state('idle');
  assert.equal(await page.getByLabel('Installation', { exact: true }).isEnabled(), true);
  assert.equal(await page.getByLabel('Profile', { exact: true }).isEnabled(), true);
  assert.equal(await button('Review launch').isEnabled(), true);
  assert.equal(await button('Review launch').evaluate(value => value === document.activeElement), true);
  assert.equal((await methods()).filter(value => value === 'commit').length, 0);
  await observed('review-Stay-reenables-same-context', { noResetBeforeAssertion: true, targetRetained: true, openerEnabledAndRestored: true }, 'review-stayed');
  // Restart the fixed script only after same-instance release has been proved.
  await choose('ordinary_ready'); await applyTarget(); await available(); await button('Review launch').click(); await state('review');
  await admission(); assert.match(await selected(), /"kind":"ordinary"/);
  assert.equal(await button('Refresh action outcome').isEnabled(), true);
  assert.equal(await button('Review launch').isDisabled(), true, 'An admitted action cannot prepare a competing review');
  assert.equal(await page.getByLabel('Installation', { exact: true }).isEnabled(), true);
  assert.equal(await page.getByLabel('Profile', { exact: true }).isEnabled(), true);
  await observed('ordinary-admission-is-not-completion', { state: 'observing', completionQueried: false, reviewDisabledWhileObserving: true, selectorsAvailableAfterAdmission: true }, 'ordinary-admitted');
  await completion(); assert.match(await page.getByRole('status').allTextContents().then(value => value.join(' ')), /completed/i);
  assert.equal(await page.getByLabel('Installation', { exact: true }).isEnabled(), true);
  assert.equal(await page.getByLabel('Profile', { exact: true }).isEnabled(), true); assert.equal(await button('Review launch').isEnabled(), true);
  await observed('ordinary-observed-completion', { methods: await methods(), state: 'idle', controlsReenabledWithoutReset: true }, 'ordinary-completed');

  await choose('isolated_ready'); await applyTarget(true); await page.getByLabel('Isolated data mode', { exact: true }).waitFor();
  await page.waitForTimeout(500); assert.equal(await button('Review launch').isDisabled(), true);
  await page.getByLabel('Isolated data mode', { exact: true }).selectOption('existing'); await available(); await button('Review launch').click();
  await dialog('Review isolated launch').waitFor(); await state('review'); assert.match(await dialog('Review isolated launch').innerText(), /Isolated data mode: existing/);
  await observed('isolated-explicit-mode-and-capture', { explicitMode: 'existing', unchangedReviewedCapture: true }, 'isolated-review');
  await admission(); await completion(); await observed('isolated-readiness-waits-for-completion', { admissionState: 'observing', explicitCompletionQuery: true });

  await choose('focus_sessions'); assert.equal(await page.locator('.session').count(), 4);
  await button('Check focus for session 1').click(); await page.waitForFunction(() => [...document.querySelectorAll('button')].some(value => value.textContent?.trim() === 'Review focus for session 1' && !value.disabled));
  await button('Review focus for session 1').click(); await dialog('Review session focus').waitFor();
  const focusFixture = JSON.parse(readFileSync(path.join(root, 'contracts/fixtures/sc18-windows-x86-64-focus-prepare-reply.json')));
  const pid = focusFixture.body.result.command.output.semantics.capture.session.process.pid;
  assert.match(await dialog('Review session focus').innerText(), new RegExp(`PID ${pid} · exact captured session`));
  assert.equal(await selected(), 'Not selected'); await button('Confirm action').click(); await state('idle');
  assert.equal(await page.getByLabel('Installation', { exact: true }).isEnabled(), true);
  assert.equal(await button('Check focus for session 1').isEnabled(), true);
  await observed('focus-exact-session-among-recycled-identities', { explicitSession: 1, pid, selectedTarget: 'none', rows: 4, controlsReenabledWithoutReset: true }, 'focus-exact');

  await choose('launch_uncertain'); await applyTarget(); await available(); await button('Review launch').click(); await state('review'); await button('Confirm action').click(); await state('uncertain');
  assert.equal(await button('Replay exact action').isEnabled(), true); assert.equal(await button('Use target').isDisabled(), true);
  assert.equal((await methods()).filter(value => value === 'commit').length, 1);
  await observed('lost-delivery-retains-explicit-replay', { uncertain: true, submissions: 1, autoRetry: false }, 'launch-uncertain');
  await button('Replay exact action').click(); await state('observing'); await completion();
  assert.equal((await methods()).filter(value => value === 'commit').length, 2);
  await observed('exact-replay-then-observed-completion', { exactInputProvedByStrictTransport: true, submissions: 2 });

  for (const [mode, criterion, copy] of [['unknown', 'partial-unknown-availability', /Unknown\./], ['missing', 'complete-missing-installation', /No installations were reported/],
    ['offline', 'offline-refuses-launch', /connection/], ['recovery', 'recovery-refuses-launch', /needs recovery/]]) {
    await choose(mode); if (mode !== 'missing') { await applyTarget(); await page.waitForTimeout(700); }
    assert.equal(await button('Review launch').isDisabled(), true); assert.match(await page.locator('.home').innerText(), copy);
    assert.ok(!(await methods()).includes('prepare')); await observed(criterion, { mode, reasonShown: true, preparationSubmitted: false }, `home-${mode}`);
  }

  await choose('dirty_draft'); assert.match(await page.getByTestId('home-preview-draft').innerText(), /revision: 2; edits: 1; dirty: true/);
  await page.getByRole('navigation', { name: 'Bridge workspace', exact: true }).getByRole('button', { name: 'Engineering', exact: true }).click();
  assert.match(await page.getByTestId('home-preview-navigation').innerText(), /view: engineering; queued: none/);
  assert.match(await page.getByTestId('home-preview-draft').innerText(), /edits: 1; dirty: true/);
  await page.getByRole('navigation', { name: 'Bridge workspace', exact: true }).getByRole('button', { name: 'Shuttle Bay', exact: true }).click();
  await observed('dirty-view-navigation-preserves-draft', { viewChangePreservedRevision: '2', edits: 1 });
  await applyTarget(true); await dialog('Unsaved changes').waitFor(); assert.equal(await page.locator('dialog[open]').count(), 1); await dialog('Unsaved changes').getByRole('button', { name: 'Stay', exact: true }).click();
  assert.equal(await selected(), 'Not selected'); assert.equal(await button('Use target').evaluate(value => value === document.activeElement), true);
  assert.deepEqual(await methods(), ['snapshot']); await observed('dirty-target-Stay-restores-opener', { modalCount: 1, noMutation: true, openerRestored: true }, 'dirty-stay');
  await button('Use target').click(); await dialog('Unsaved changes').waitFor(); await dialog('Unsaved changes').getByRole('button', { name: 'Save', exact: true }).click();
  await dialog('Review Save').waitFor(); await state('idle', 'review'); assert.equal(await selected(), 'Not selected');
  await button('Confirm Save').click(); await state('idle', 'observing'); assert.equal(await selected(), 'Not selected');
  await button('Refresh Save outcome').click(); await state('idle'); await page.waitForFunction(() => document.querySelector('[data-testid="home-preview-selection"]')?.textContent !== 'Not selected');
  assert.match(await page.getByTestId('home-preview-draft').innerText(), /edits: 0; dirty: false/); await observed('dirty-Save-keeps-target-until-completed', { explicitReview: true, targetAfterCompletedOnly: true }, 'dirty-saved');
  await choose('dirty_draft'); await page.getByLabel('Development draft outcome', { exact: true }).selectOption('discard');
  await page.getByTestId('home-preview-confidence').filter({ hasText: /^authoritative$/ }).waitFor(); await applyTarget(true); await dialog('Unsaved changes').waitFor();
  await dialog('Unsaved changes').getByRole('button', { name: 'Discard', exact: true }).click(); await page.waitForFunction(() => document.querySelector('[data-testid="home-preview-selection"]')?.textContent !== 'Not selected');
  assert.match(await page.getByTestId('home-preview-draft').innerText(), /dirty: false/); await observed('confirmed-Discard-applies-target', { exactOldRevisionReceipt: true });

  await choose('ordinary_ready'); await applyTarget(); await button('Reset Home scenario').click(); await page.waitForTimeout(1600);
  assert.equal(await selected(), 'Not selected'); await state('idle'); assert.deepEqual(await methods(), ['snapshot']);
  await observed('reset-disposes-pending-observation', { oldTargetReadNotApplied: true });
  const maintenance = [];
  for (const [control, heading] of [['Game and recovery', 'Game and recovery'], ['Community Mod', 'Community Mod'], ['Bridge and preferences', 'Bridge and preferences']]) {
    await choose('ordinary_ready'); await button(control).click(); await page.getByRole('heading', { name: heading, exact: true }).waitFor(); maintenance.push(heading);
  }
  await observed('three-maintenance-destinations', { headings: maintenance, separateReviewsStillRequired: true });
  await choose('ordinary_ready'); const normal = await page.locator('.bridge-shell').innerText(); assert.doesNotMatch(normal, /[A-Za-z]:[\\/]|\/Users\/|\/home\/|private-token|credential/i);
  await observed('normal-screen-no-private-paths', { explicitDevelopmentDiagnosticsExcluded: true });
  await page.emulateMedia({ colorScheme: 'dark' }); await geometry(); await observed('home-dark-presentation', { colorScheme: 'dark', nativeMacClaim: false }, 'home-dark');
  await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' }); const media = await page.evaluate(() => ({ forcedColors: matchMedia('(forced-colors: active)').matches, reducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches }));
  assert.deepEqual(media, { forcedColors: true, reducedMotion: true }); await geometry(); await observed('home-forced-colors-reduced-motion', media, 'home-forced-colors');
  await page.emulateMedia({ colorScheme: 'light', forcedColors: 'none', reducedMotion: 'reduce' }); await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  for (const [name, width, height] of [['desktop', 1440, 1100], ['compact', 480, 900]]) {
    await page.setViewportSize({ width, height }); const layout = await geometry(); assert.equal(layout.font, '32px');
    const readability = await page.locator('.selected-target dd').evaluateAll(values => values.map(value => {
      const style = getComputedStyle(value), canvas = document.createElement('canvas'), context = canvas.getContext('2d');
      context.font = style.font; const longest = (value.textContent ?? '').split(/\s+/).reduce((a, b) => a.length > b.length ? a : b, '');
      return { text: value.textContent, availableWidth: value.getBoundingClientRect().width, longestWord: longest, wordWidth: context.measureText(longest).width };
    }));
    assert.equal(readability.length, 2); for (const value of readability) assert.ok(value.availableWidth >= value.wordWidth, 'Target value cannot fit its longest word at the sampled text size');
    await observed(`home-text-200-${name}`, { ...layout, readability, browserZoomClaim: false }, `home-text-200-${name}`);
  }
  assert.deepEqual(observations.map(value => value.id), expected); assert.deepEqual(browserErrors, []); assert.deepEqual(externalRequests, []);
  assert.deepEqual(hashSources(), before); assert.deepEqual(projectBrowser(root), binary);
} catch (error) {
  if (page && !page.isClosed()) { await picture('failure').catch(() => {}); writeFileSync(path.join(directory, 'failure.json'), JSON.stringify({ name: error.name, message: error.message, observations,
    browserErrors, externalRequests, ownedSyntheticPageText: await page.locator('body').innerText().catch(() => '') }, null, 2) + '\n', { flag: 'wx' }); }
  throw error;
} finally { await cleanupOwnedBrowser({ browser, server, writeLog: () => writeFileSync(path.join(directory, 'vite.log'), output, { flag: 'wx' }) }); }
const receipt = path.join(directory, 'home-browser.json');
writeFileSync(receipt, JSON.stringify({ schemaVersion: 'bridge-frontend-home-browser/v1', result: 'passed', completedAt: new Date().toISOString(),
  browser: { ...binary, observedVersion: browserVersion }, sources: before, expected, observations, screenshots, provenance,
  browserErrors, externalRequests, ownedBrowserAndServerCleanupCompleted: true,
  boundary: 'Actual private pinned Chromium journeys over strict hashed shared synthetic fixtures in real App. Native webview, operating-system assistive technology, game, Mac execution and release qualify separately.',
  rustBuildInvoked: false, nativeWebviewQualified: false, nativeRuntimeQualified: false, macNativeQualified: false, releaseQualified: false }, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ result: 'passed', checks: observations.length, screenshots: screenshots.length, receipt, nativeRuntimeQualified: false, releaseQualified: false }));
