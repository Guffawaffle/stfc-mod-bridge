<script lang="ts">
  import { onDestroy } from 'svelte';
  import { BridgeClient, ObservationStore, type ClientOutcome, type ObservationState } from '../src/client';
  import { ScenarioTransport, type MockScript, type MockState } from '../src/mocks/scenario-transport';
  import { ManualClock } from '../src/mocks/clock';
  import type { Request, Event } from '../src/generated/protocol';
  import catalog from './catalog.json';

  // The generated catalog is schema checked from the same Rust-conformant
  // fixtures; normal scripts cannot invent backend replies or native authority.
  const scripts = catalog.scripts as MockScript[];
  let selected = $state(scripts[0].id);
  let latency = $state(100);
  let clock: ManualClock;
  let transport: ScenarioTransport;
  let client: BridgeClient;
  let observations: ObservationStore;
  let disposers: (() => void)[] = [];
  let observerDisposer: (() => void) | undefined;
  let generation = 0;
  let mock = $state<MockState>();
  let observed = $state<ObservationState>();
  let elapsed = $state(0);
  let pending = $state(0);
  let deadlines = $state(0);
  let outcome = $state<ClientOutcome<unknown>>();
  let lastEvent = $state<Readonly<Event>>();
  let next = $state<Readonly<Request>>();
  const current = $derived(scripts.find(script => script.id === selected)!);

  function cleanup() {
    generation++;
    observerDisposer?.(); observerDisposer = undefined;
    disposers.forEach(dispose => dispose()); disposers = [];
    client?.dispose(); transport?.dispose(); clock?.dispose();
  }
  function refresh() {
    mock = transport.state;
    next = transport.expectedRequest as Readonly<Request> | undefined;
    elapsed = clock.now; pending = client.pendingCount; deadlines = clock.pendingCount;
  }
  function load() {
    cleanup();
    clock = new ManualClock();
    transport = new ScenarioTransport(scripts.find(script => script.id === selected)!, { clock, latencyMs: latency });
    client = new BridgeClient(transport, { requestId: () => crypto.randomUUID(), clock, timeoutMs: 1000 });
    observations = new ObservationStore();
    outcome = undefined; lastEvent = undefined;
    disposers.push(transport.subscribeState(refresh), observations.subscribe(state => { observed = state; }));
    listen(); refresh();
  }
  function listen() {
    observerDisposer?.();
    const active = generation;
    const store = observations;
    const stop = client.subscribe(event => {
      if (active !== generation) return;
      lastEvent = event as Event;
      store.acceptEvent(event);
      refresh();
    }, fault => {
      if (active !== generation) return;
      generation++;
      observerDisposer?.(); observerDisposer = undefined;
      outcome = { kind: 'fault', fault }; store.abandonSnapshot(); refresh();
    });
    if (active === generation) observerDisposer = stop;
    else stop();
  }
  async function dispatch(request: Readonly<Request>): Promise<ClientOutcome<unknown>> {
    const active = generation;
    const currentClient = client;
    const store = observations;
    const current = () => active === generation;
    const body = request.body;
    if (body.type === 'query') {
      if (body.query.name === 'snapshot') {
        store.beginSnapshot();
        const result = await currentClient.query('snapshot', body.query.input);
        if (current()) {
          if (result.kind === 'result') store.acceptSnapshot(result.value);
          else store.abandonSnapshot();
        }
        return result;
      }
      if (body.query.name === 'get_operation') {
        const result = await currentClient.query('get_operation', body.query.input);
        if (current() && result.kind === 'result') store.observeOperationResult(body.query.input.operationId, result.value);
        return result;
      }
      if (body.query.name === 'hello') {
        const result = await currentClient.query('hello', body.query.input);
        if (current() && result.kind === 'result') store.observeHello(result.value.hostEpoch);
        return result;
      }
      if (body.query.name === 'resume_events') {
        const result = await currentClient.query('resume_events', body.query.input);
        if (current()) {
          if (result.kind === 'result') store.acceptBatch(result.value, body.query.input.after, body.query.input.maximumEvents);
          else if (result.kind === 'rejected' && result.error.code === 'resnapshot_required') store.invalidate('sequence_gap');
        }
        return result;
      }
      return currentClient.query(body.query.name, body.query.input);
    }
    if (body.command.name === 'commit') {
      const result = await currentClient.command('commit', body.command.input);
      if (current() && result.kind === 'result') store.observeOperation(result.value);
      return result;
    }
    if (body.command.name === 'cancel_operation') {
      const result = await currentClient.command('cancel_operation', body.command.input);
      if (current() && result.kind === 'result') store.observeOperation(result.value.operation);
      return result;
    }
    if (body.command.name === 'request_host_close') {
      const result = await currentClient.command('request_host_close', body.command.input);
      if (current() && result.kind === 'result') store.observeClose(result.value);
      return result;
    }
    if (body.command.name === 'open_draft') {
      const result = await currentClient.command('open_draft', body.command.input);
      if (current() && result.kind === 'result') store.observeDraft(result.value);
      return result;
    }
    if (body.command.name === 'set_draft_changes') {
      const result = await currentClient.command('set_draft_changes', body.command.input);
      if (current() && result.kind === 'result') store.observeDraft(result.value.snapshot);
      return result;
    }
    if (body.command.name === 'discard_draft') {
      const result = await currentClient.command('discard_draft', body.command.input);
      if (current() && result.kind === 'result') store.observeDiscard(result.value);
      return result;
    }
    return currentClient.command(body.command.name, body.command.input);
  }
  function send() {
    const request = transport.expectedRequest;
    if (!request) return;
    const active = generation;
    void dispatch(request as Request).then(result => {
      if (generation === active) { outcome = result; refresh(); }
    });
    refresh();
  }
  function advance(milliseconds: number) { clock.advanceBy(milliseconds); refresh(); }
  function nextDeadline() { clock.runNext(); refresh(); }
  function disconnect() {
    generation++; observerDisposer?.(); observerDisposer = undefined;
    transport.disconnect(); observations.abandonSnapshot(); outcome = undefined; refresh();
  }
  function reconnect() { generation++; transport.reconnect(); listen(); refresh(); }
  function faultReply() { transport.injectNextReply('{"protocolVersion":1,"protocolVersion":1}'); refresh(); }
  load();
  onDestroy(cleanup);
</script>

<main>
  <header>
    <p class="eyebrow">STFC MOD BRIDGE · DEVELOPMENT</p>
    <h1>Scenario workbench</h1>
    <p>Shared synthetic journeys through the typed client. Manual time controls delivery; native operations stay unqualified.</p>
  </header>
  <section class="picker" aria-label="Scenario selection">
    <label for="scenario">Journey</label>
    <select id="scenario" bind:value={selected} onchange={load}>
      {#each scripts as script}<option value={script.id}>{script.scenario} · {script.case}</option>{/each}
    </select>
    <label for="latency">Delivery latency (ms)</label>
    <input id="latency" type="number" min="0" max="10000" step="50" bind:value={latency} onchange={() => transport.setLatency(latency)} />
    <button onclick={load}>Reset journey</button>
  </section>
  <div class="grid">
    <section aria-label="Delivery controls">
      <h2>Delivery</h2>
      <div class="badges">
        <span data-testid="connection">{mock?.connected ? 'Connected' : 'Disconnected'}</span>
        <span data-testid="backend">{mock?.backendBusy ? 'Script busy' : 'Script idle'}</span>
        <span data-testid="pending">{pending} observers pending</span>
        <span data-testid="time">{elapsed} ms</span>
      </div>
      <p class="description">{current.case}</p>
      <p data-testid="next-method">Next: {next?.body.type === 'query' ? next.body.query.name : next?.body.type === 'command' ? next.body.command.name : 'no exchange ready'}</p>
      <div class="controls">
        <button onclick={send} disabled={!next}>Send next request</button>
        <button onclick={nextDeadline} disabled={!deadlines}>Next deadline</button>
        <button onclick={() => advance(50)}>Advance 50 ms</button>
        <button onclick={() => advance(1000)}>Advance 1000 ms</button>
        <button onclick={disconnect} disabled={!mock?.connected}>Disconnect observer</button>
        <button onclick={reconnect} disabled={mock?.connected}>Reconnect observer</button>
        <button onclick={() => { transport.dropNextReply(); refresh(); }}>Lose next reply</button>
        <button onclick={faultReply}>Malformed next reply</button>
      </div>
      {#if mock?.failureMode}<p class="notice">Explicit delivery fault mode</p>{/if}
      <p class="muted">A timeout or disconnected observer does not prove cancellation or operation failure.</p>
    </section>
    <section aria-label="Observed result">
      <h2>Client observation</h2>
      <p data-testid="outcome">{outcome?.kind === 'fault' ? `Local fault: ${outcome.fault.code}` : outcome?.kind === 'rejected' ? `Backend refusal: ${outcome.error.code}` : outcome?.kind === 'result' ? 'Validated result' : 'Awaiting request'}</p>
      <p data-testid="confidence">Confidence: {observed?.confidence} {observed?.reason ?? ''}</p>
      {#if observed?.resnapshotRequired}<p class="notice">Authoritative snapshot required</p>{/if}
      <p data-testid="boundary">Script boundary: {mock?.boundary?.reason ?? 'none'}</p>
      <p data-testid="operation-count">{observed?.operations.length ?? 0} known operations</p>
      <pre data-testid="result-data">{outcome?.kind === 'result' ? JSON.stringify(outcome.value, null, 2) : outcome?.kind === 'rejected' ? JSON.stringify(outcome.error, null, 2) : ''}</pre>
    </section>
    <section class="events" aria-label="Scripted event">
      <h2>Last validated event</h2>
      <p class="muted">Recorded event data is visible for development. A stream without an authoritative anchor remains uncertain.</p>
      <pre data-testid="event-data">{lastEvent ? JSON.stringify(lastEvent, null, 2) : 'No event delivered'}</pre>
    </section>
  </div>
</main>

<style>
  :global(body) { margin: 0; background: #101720; color: #e5edf6; font-family: system-ui, sans-serif; }
  :global(button), :global(input), :global(select) { font: inherit; }
  main { max-width: 90rem; margin: auto; padding: 2rem; }
  header { max-width: 55rem; margin-bottom: 2rem; }
  .eyebrow { color: #91b8dd; letter-spacing: .12em; font-size: .75rem; }
  h1 { font-size: 2rem; margin: .5rem 0; } h2 { font-size: 1.1rem; margin-top: 0; }
  p { line-height: 1.5; }
  .picker { display: grid; grid-template-columns: auto minmax(12rem,1fr) auto 7rem auto; align-items: center; gap: .75rem; margin-bottom: 1.2rem; }
  select, input, button { color: inherit; border: 1px solid #41566b; border-radius: .35rem; background: #1c2a39; padding: .65rem; }
  button { cursor: pointer; } button:disabled { opacity: .45; cursor: default; }
  button:focus-visible, select:focus-visible, input:focus-visible { outline: 2px solid #b4d7ff; outline-offset: 3px; }
  .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 1.2rem; }
  .grid section { background: #18222e; border: 1px solid #2c3c4d; border-radius: .6rem; padding: 1.3rem; min-width: 0; }
  .badges, .controls { display: flex; flex-wrap: wrap; gap: .5rem; }
  .badges span { background: #26384b; border-radius: .3rem; padding: .3rem .5rem; font-size: .8rem; }
  .notice { color: #f1cc8e; } .muted { color: #aab9c9; font-size: .875rem; }
  .description { color: #c7d8e9; }
  pre { max-height: 26rem; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; font-size: .8rem; color: #cfdfed; }
  .events { grid-column: 1 / -1; }
  @media (max-width: 60rem) { .picker { grid-template-columns: 1fr; } .grid { grid-template-columns: 1fr; } main { padding: 1rem; } }
</style>
