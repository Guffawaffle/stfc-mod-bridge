import { expect, test, vi, afterEach } from 'vitest';
import { readFileSync } from 'node:fs';

const context = vi.hoisted(() => ({ native: false, invoke: vi.fn() }));
vi.mock('@tauri-apps/api/core', () => ({ isTauri: () => context.native, invoke: context.invoke }));
const epoch = '10000000-0000-4000-8000-000000000001';
const clients: Array<{dispose(): void}> = [];
afterEach(() => { for (const client of clients.splice(0)) client.dispose(); vi.resetModules(); context.invoke.mockReset(); });

test('ordinary browser stays unavailable and never invokes native code', async () => {
  context.native = false;
  const { bridgeClient } = await import('../src/client-instance'); clients.push(bridgeClient);
  expect(await bridgeClient.query('snapshot', {})).toMatchObject({ kind: 'fault', fault: { code: 'unavailable_binding', delivery: 'not_sent' } });
  expect(context.invoke).not.toHaveBeenCalled();
});

test('native composition uses only the root-issued epoch and real transport readiness', async () => {
  context.native = true;
  context.invoke.mockImplementation(async (command: string, args: Record<string, string> = {}) => {
    if (command === 'bridge_bootstrap') return { documentEpoch: epoch };
    expect(args.documentEpoch).toBe(epoch);
    if (command === 'bridge_subscribe' || command === 'bridge_unsubscribe') return { schemaVersion: 1, registrationKey: args.registrationKey };
    if (command === 'bridge_poll') return { schemaVersion: 1, registrationKey: args.registrationKey, frames: [] };
    if (command === 'bridge_exchange') {
      const reply = JSON.parse(readFileSync(new URL('../../contracts/fixtures/sc15-complete-empty-snapshot-reply.json', import.meta.url), 'utf8'));
      reply.requestId = JSON.parse(args.frame).requestId;
      return JSON.stringify(reply);
    }
    throw new Error('unexpected native command');
  });
  const { bridgeClient } = await import('../src/client-instance'); clients.push(bridgeClient);
  const stop = bridgeClient.subscribe(() => {});
  expect(await bridgeClient.query('snapshot', {})).toMatchObject({ kind: 'result' });
  const names = context.invoke.mock.calls.map(call => call[0]);
  expect(names.indexOf('bridge_subscribe')).toBeLessThan(names.indexOf('bridge_exchange'));
  expect(context.invoke.mock.calls.filter(call => call[0] !== 'bridge_bootstrap')
    .every(call => !('nativeRoot' in call[1]) && !('path' in call[1]))).toBe(true);
  stop();
});

test('invalid bootstrap never falls back to guessed epochs or a mock backend', async () => {
  context.native = true;
  context.invoke.mockResolvedValue({ documentEpoch: epoch, extra: true });
  const { bridgeClient } = await import('../src/client-instance'); clients.push(bridgeClient);
  expect(await bridgeClient.query('snapshot', {})).toMatchObject({ kind: 'fault', fault: { code: 'unavailable_binding', delivery: 'not_sent' } });
  expect(context.invoke.mock.calls.map(call => call[0])).toEqual(['bridge_bootstrap']);
});
