import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { cleanupOwnedBrowser } from '../browser-cleanup.mjs';

function child() {
  const server = new EventEmitter();
  server.exitCode = null; server.signalCode = null; server.kills = 0;
  server.kill = () => { server.kills++; server.signalCode = 'SIGTERM'; server.emit('exit'); };
  return server;
}
test('browser close failure still logs and stops the owned Vite child', async () => {
  const server = child(); let logged = false;
  const failure = new Error('synthetic-close');
  await assert.rejects(cleanupOwnedBrowser({ server, browser: { close: async () => { throw failure; } },
    writeLog: () => { logged = true; } }), error => error instanceof AggregateError && error.errors.includes(failure));
  assert.equal(logged, true); assert.equal(server.kills, 1);
});
test('log failure still stops only a live owned child and preserves cleanup errors', async () => {
  const server = child(), failure = new Error('synthetic-log');
  await assert.rejects(cleanupOwnedBrowser({ server, writeLog: () => { throw failure; } }),
    error => error instanceof AggregateError && error.errors.includes(failure));
  assert.equal(server.kills, 1);
  await cleanupOwnedBrowser({ server, writeLog: () => {} });
  assert.equal(server.kills, 1);
});
