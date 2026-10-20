// Closed Node-only lifecycle fixture. No Mac, game, native helper or store work.
import { setTimeout as delay } from 'node:timers/promises';
let deadline;
process.on('disconnect', () => {
  clearTimeout(deadline);
  process.stdout.write('refused\n', () => { process.exitCode = 2; });
});
process.once('message', async message => {
  if (message !== 'observe-disconnect') throw Error('closed lifecycle probe required');
  deadline = setTimeout(() => { process.exitCode = 3; if (process.connected) process.disconnect(); }, 1500);
  process.stdout.write('ready\n');
  await delay(100);
});
