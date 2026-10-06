import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { readFileSync, readdirSync, mkdirSync, writeFileSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import path from 'node:path';
import { projectBrowser } from './browser-runtime.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';
import { cleanupOwnedBrowser } from './browser-cleanup.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(process.argv.length, 2, 'The browser development suite accepts no overrides');
const directory = ownedArtifactPath(root, `artifacts/next/browser/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
const checks = [];
function command(id, argv) {
  const result = spawnSync(process.execPath, argv, { cwd: root, windowsHide: true,
    encoding: 'utf8', timeout: 180000, maxBuffer: 4 * 1024 * 1024 });
  const observation = { id, executable: process.execPath, argv, cwd: root,
    exitCode: result.status, error: result.error?.code ?? null,
    stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  writeFileSync(path.join(directory, `${id}.json`), JSON.stringify(observation, null, 2) + '\n');
  checks.push({ id, exitCode: result.status });
  assert.equal(result.status, 0, `Browser check ${id} failed`);
  assert.ok(!result.error);
}
command('validator-drift', ['scripts/next/generate-browser-validators.mjs', '--check']);
command('mock-catalog-drift', ['scripts/next/generate-mock-catalog.mjs', '--check']);
ownedArtifactPath(root, 'ui/dist', 'directory', { allowMissing: true });
ownedArtifactPath(root, 'artifacts/next/frontend', 'directory', { allowMissing: true });
command('production-build', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'build']);
const graph = JSON.parse(readFileSync(ownedArtifactPath(root, 'artifacts/next/frontend/production-graph.json'), 'utf8'));
assert.equal(graph.productionMockHostPresent, false);
assert.ok(graph.modules.some(module => module.replaceAll('\\', '/').endsWith('/src/client/transport.ts')), 'Production must include the real common client seam');
assert.ok(!graph.modules.some(module => /[/\\](?:mocks|scenarios|fixtures|tests|gallery|home-preview|settings-preview|management-preview)[/\\]|@wdio|webdriver|playwright/i.test(module)));
const catalog = JSON.parse(readFileSync(path.join(root, 'ui/scenarios/catalog.json'), 'utf8'));
const canaries = ['synthetic-native-target-1', 'synthetic-client-270', 'mock_clock_task_limit', ...catalog.scripts.map(script => script.id)];
const outputs = [];
function inspectOutputs(directory_) {
  for (const entry of readdirSync(directory_, { withFileTypes: true })) {
    const file = path.join(directory_, entry.name);
    if (entry.isDirectory()) inspectOutputs(file);
    else {
      assert.ok(entry.isFile(), 'Build output cannot be a link or native filesystem entry');
      const bytes = readFileSync(file);
      if (/\.(?:js|css|html|map|json)$/.test(entry.name)) {
        const text = bytes.toString('utf8');
        for (const marker of canaries) assert.ok(!text.includes(marker), `Production contains a synthetic fixture/control marker`);
      }
      outputs.push({ path: path.relative(root, file).replaceAll('\\', '/'),
        bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') });
    }
  }
}
inspectOutputs(ownedArtifactPath(root, 'ui/dist', 'directory'));

process.env.PLAYWRIGHT_BROWSERS_PATH = path.join(root, 'artifacts/next/tooling/browsers');
const { chromium } = await import('playwright');
const browserIdentity = projectBrowser(root);
let browser, server, page;
let serverOutput = '';
const browserErrors = [];
const journeys = [];
try {
  server = spawn(process.execPath, [path.join(root, 'ui/node_modules/vite/bin/vite.js'), '--host', '127.0.0.1', '--port', '0', '--strictPort'], {
    cwd: path.join(root, 'ui'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  const url = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Owned Vite startup timed out')), 30000);
    const fail = error => { clearTimeout(timer); reject(error); };
    server.once('error', fail);
    server.once('exit', code => fail(new Error(`Owned Vite exited ${code}`)));
    const output = bytes => {
      serverOutput = (serverOutput + bytes.toString()).slice(-16000);
      const match = serverOutput.replace(/\u001b\[[0-9;]*m/g, '').match(/http:\/\/127\.0\.0\.1:\d+\//);
      if (match) { clearTimeout(timer); resolve(match[0]); }
    };
    server.stdout.on('data', output); server.stderr.on('data', output);
  });
  browser = await chromium.launch({ headless: true, executablePath: browserIdentity.executable });
  assert.equal(browser.version(), browserIdentity.expectedVersion, 'Actual browser version differs from pinned shell metadata');
  const context = await browser.newContext({ viewport: { width: 1440, height: 1100 } });
  page = await context.newPage();
  page.on('pageerror', error => browserErrors.push({ name: error.name, message: error.message.slice(0,300) }));
  await page.goto(new URL('scenarios/index.html', url).href);
  await page.getByRole('heading', { name: 'Scenario workbench' }).waitFor();
  await page.getByRole('button', { name: 'Send next request' }).click();
  await page.getByTestId('backend').filter({ hasText: 'Script busy' }).waitFor();
  await page.getByRole('button', { name: 'Next deadline', exact: true }).click();
  await page.getByTestId('outcome').filter({ hasText: 'Validated result' }).waitFor();
  journeys.push('browser picker and manually scheduled valid reply');

  await page.getByRole('button', { name: 'Reset journey' }).click();
  await page.getByRole('button', { name: 'Malformed next reply' }).click();
  await page.getByRole('button', { name: 'Send next request' }).click();
  await page.getByRole('button', { name: 'Next deadline', exact: true }).click();
  await page.getByTestId('outcome').filter({ hasText: 'Local fault: framing' }).waitFor();
  journeys.push('same validation path refuses injected duplicate-key raw reply');

  await page.getByRole('button', { name: 'Reset journey' }).click();
  await page.getByRole('button', { name: 'Lose next reply' }).click();
  await page.getByRole('button', { name: 'Send next request' }).click();
  await page.getByRole('button', { name: 'Advance 1000 ms', exact: true }).click();
  await page.getByTestId('outcome').filter({ hasText: 'Local fault: timeout' }).waitFor();
  await page.getByTestId('backend').filter({ hasText: 'Script idle' }).waitFor();
  journeys.push('lost reply times out observation after scripted backend completion');

  await page.getByRole('button', { name: 'Reset journey' }).click();
  await page.getByRole('button', { name: 'Disconnect observer' }).click();
  await page.getByTestId('connection').filter({ hasText: 'Disconnected' }).waitFor();
  await page.getByRole('button', { name: 'Reconnect observer' }).click();
  await page.getByTestId('connection').filter({ hasText: 'Connected' }).waitFor();
  await page.getByRole('button', { name: 'Send next request' }).click();
  await page.getByRole('button', { name: 'Next deadline', exact: true }).click();
  await page.getByTestId('outcome').filter({ hasText: 'Validated result' }).waitFor();
  journeys.push('explicit disconnect and new subscription on reconnect');

  const progress = catalog.scripts.find(script => script.steps.some(step => step.type === 'event'
    && step.event.body.type === 'operation_changed' && step.event.body.operation.state.status === 'running'));
  assert.ok(progress, 'Shared scripts must include progress');
  await page.getByLabel('Journey', { exact: true }).selectOption(progress.id);
  for (let i = 0; i < 20 && !(await page.getByTestId('event-data').textContent()).includes('"running"'); i++) {
    if (await page.getByRole('button', { name: 'Send next request' }).isEnabled()) await page.getByRole('button', { name: 'Send next request' }).click();
    if (await page.getByRole('button', { name: 'Next deadline', exact: true }).isEnabled()) await page.getByRole('button', { name: 'Next deadline', exact: true }).click();
    if (await page.getByRole('button', { name: 'Reconnect observer' }).isEnabled()) await page.getByRole('button', { name: 'Reconnect observer' }).click();
  }
  assert.ok((await page.getByTestId('event-data').textContent()).includes('"running"'), 'Browser must display validated scripted progress');
  journeys.push('shared operation progress event rendered with explicit observation confidence');

  await page.getByRole('button', { name: 'Reset journey' }).click();
  await page.getByRole('button', { name: 'Send next request' }).click();
  await page.getByRole('button', { name: 'Next deadline', exact: true }).click();
  await page.getByTestId('next-method').filter({ hasText: 'commit' }).waitFor();
  await page.getByRole('button', { name: 'Send next request' }).click();
  // Resolve the old commit and reset in one browser task, before Promise
  // reconciliation runs. An outer rendering guard alone cannot protect a store.
  await page.evaluate(() => {
    const button = name => [...document.querySelectorAll('button')].find(element => element.textContent === name);
    button('Next deadline').click(); button('Reset journey').click();
  });
  await page.getByTestId('outcome').filter({ hasText: 'Awaiting request' }).waitFor();
  await page.getByTestId('operation-count').filter({ hasText: '0 known operations' }).waitFor();
  assert.equal(await page.getByTestId('confidence').textContent(), 'Confidence: uninitialized ');
  journeys.push('resolved old commit cannot reconcile into a reset scenario generation');

  const buffered = catalog.scripts.find(script => script.id === 'sc14-reconnect-retention-gap-resnapshot');
  assert.ok(buffered);
  await page.getByLabel('Journey', { exact: true }).selectOption(buffered.id);
  for (let i = 0; i < 24 && !(await page.getByTestId('next-method').textContent()).includes('Next: snapshot'); i++) {
    if (await page.getByRole('button', { name: 'Reconnect observer' }).isEnabled()) await page.getByRole('button', { name: 'Reconnect observer' }).click();
    if (await page.getByRole('button', { name: 'Send next request' }).isEnabled()) await page.getByRole('button', { name: 'Send next request' }).click();
    if (await page.getByRole('button', { name: 'Next deadline', exact: true }).isEnabled()) await page.getByRole('button', { name: 'Next deadline', exact: true }).click();
  }
  assert.equal(await page.getByTestId('next-method').textContent(), 'Next: snapshot');
  await page.getByRole('button', { name: 'Send next request' }).click();
  // Deliver a reply and its post-watermark invalidation before the snapshot
  // Promise continues; the subscription must already be buffering.
  await page.evaluate(() => {
    const advance = [...document.querySelectorAll('button')].find(element => element.textContent === 'Advance 50 ms');
    advance.click(); advance.click(); advance.click();
  });
  await page.getByTestId('confidence').filter({ hasText: 'stale snapshot_invalidated' }).waitFor();
  journeys.push('event during pending snapshot survives authoritative watermark reconciliation');
  await page.screenshot({ path: path.join(directory, 'workbench.png'), fullPage: true });

  await page.goto(url);
  await page.getByRole('heading', { name: 'Shuttle Bay', exact: true }).waitFor();
  await page.getByRole('status').filter({ hasText: 'Observations need refresh' }).waitFor();
  await page.getByRole('alert').filter({ hasText: 'Observations are unavailable. Refresh is required.' }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Refresh observations', exact: true }).isEnabled(), true, 'An unavailable connection must offer refresh');
  assert.equal(await page.getByRole('button', { name: 'Use target', exact: true }).isDisabled(), true, 'An unobserved target cannot be applied');
  assert.equal(await page.getByRole('button', { name: 'Review launch', exact: true }).isDisabled(), true, 'An unobserved launch cannot be prepared');
  const unboundText = await page.locator('body').innerText();
  for (const required of ['Installations have not been observed.', 'Profiles have not been observed.', 'Sessions have not been observed.', 'Target not confirmed']) {
    assert.ok(unboundText.includes(required), `Unbound Home must preserve unknown observation: ${required}`);
  }
  await page.screenshot({ path: path.join(directory, 'unbound-home.png'), fullPage: true });
  journeys.push('unbound Home uses the common client, reports refresh-required observations and refuses unobserved target/launch');
  assert.equal(browserErrors.length, 0, 'Browser application errors require correction');
  assert.deepEqual(projectBrowser(root), browserIdentity, 'Project browser bytes or metadata changed during the test');
  writeFileSync(path.join(directory, 'browser.json'), JSON.stringify({
    schemaVersion: 'bridge-browser-development-observation/v1',
    host: { platform: process.platform, architecture: process.arch, node: process.version },
    browser: { ...browserIdentity, version: browser.version(), launchSelection: 'explicit-pinned-headless-shell' },
    journeys, checks, graph, outputs, browserErrors,
    rustBuildInvoked: false, gameRequired: false, nativeRuntimeQualified: false, releaseQualified: false
  }, null, 2) + '\n');
  console.log(JSON.stringify({ result: 'passed', journeys: journeys.length,
    receipt: path.join(directory, 'browser.json'), rustBuildInvoked: false,
    nativeRuntimeQualified: false, releaseQualified: false }));
} catch (error) {
  if (page && !page.isClosed()) {
    await page.screenshot({ path: path.join(directory, 'failure.png'), fullPage: true }).catch(() => {});
    writeFileSync(path.join(directory, 'failure.json'), JSON.stringify({
      name: error.name, message: error.message, completedJourneys: journeys,
      // The owned pages contain only tracked synthetic fixtures/unbound status.
      ownedPageText: await page.locator('body').innerText().catch(() => ''), browserErrors
    }, null, 2) + '\n');
  }
  throw error;
} finally {
  await cleanupOwnedBrowser({ browser, server, writeLog: () => writeFileSync(path.join(directory, 'vite.log'), serverOutput) });
}
