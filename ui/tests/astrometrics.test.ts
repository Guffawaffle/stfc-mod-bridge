import { expect, test } from 'vitest';
import { connectionSettings } from '../src/views/astrometrics/connection';
import type { AstrometricsView } from '../src/generated/protocol';
const disabled: AstrometricsView = {available:true,historyAvailable:true,enabled:false,notice:'disabled',storedEvents:'0',rejectedRequests:'0',endpoint:null,syncToken:null,events:[]};
test('local target explicitly overrides an inherited proxy and limits delivery to diagnostics', () => {
  const snippet=connectionSettings({...disabled,enabled:true,notice:'listening',endpoint:'http://127.0.0.1:58237/sync',syncToken:'10000000-0000-4000-8000-000000000001'});
  const globalProxy='http://proxy.example:8080';
  const override=snippet.match(/^proxy = "([^"]*)"$/m)?.[1];
  expect(override ?? globalProxy).toBe('');
  expect(snippet).toContain('diagnostics = true');
  expect(snippet).toContain('battlelogs = false');expect(snippet).toContain('research = false');
  expect(connectionSettings(disabled)).toBe('');
});
