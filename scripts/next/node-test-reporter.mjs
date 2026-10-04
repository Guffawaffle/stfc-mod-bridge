// Public node:test custom reporter interface. Retain only test/summary events.
export default async function* report(source) {
  for await (const event of source) {
    if (['test:pass', 'test:fail', 'test:summary'].includes(event.type)) yield JSON.stringify(event) + '\n';
  }
}
