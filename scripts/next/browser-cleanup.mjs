import assert from 'node:assert/strict';

export async function cleanupOwnedBrowser({ browser, server, writeLog }) {
  const failures = [];
  try { await browser?.close(); } catch (error) { failures.push(error); }
  try { writeLog(); } catch (error) { failures.push(error); }
  try {
    if (server && server.exitCode === null && server.signalCode === null) {
      let timer;
      const exited = new Promise(resolve => server.once('exit', resolve));
      server.kill();
      try { await Promise.race([exited, new Promise(resolve => { timer = setTimeout(resolve, 5000); })]); }
      finally { clearTimeout(timer); }
      assert.ok(server.exitCode !== null || server.signalCode !== null, 'Owned Vite child must exit');
    }
  } catch (error) { failures.push(error); }
  if (failures.length) throw new AggregateError(failures, 'Owned browser helper cleanup failed');
}
