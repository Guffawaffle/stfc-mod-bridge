import { test, expect } from 'vitest';
import { readFileSync } from 'node:fs';

test('browser development entry does not import native transport or automation', () => {
  const entry = readFileSync(new URL('../src/main.ts', import.meta.url), 'utf8');
  expect(entry).not.toMatch(/@tauri-apps|@wdio|node:|invoke\(/);
});

test('production shell grants no broad plugin or remote page capabilities', () => {
  const capability = JSON.parse(readFileSync(new URL('../../apps/desktop/src-tauri/capabilities/default.json', import.meta.url), 'utf8'));
  expect(capability.permissions).toEqual([]);
  expect(capability.remote).toBeUndefined();
  const shell = readFileSync(new URL('../../apps/desktop/src-tauri/src/main.rs', import.meta.url), 'utf8');
  expect(shell).not.toMatch(/wdio|webdriver|\.plugin\(/);
});
