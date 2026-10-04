import assert from 'node:assert/strict';

export const SCENARIOS = Object.freeze(Array.from({ length: 18 }, (_, i) => `SC-${String(i + 1).padStart(2, '0')}`));
const required = Object.freeze({
  'SC-01': ['query:hello', 'query:resolve_target', 'query:snapshot', 'query:list_installations', 'action:register_installation', 'action:edit_installation'],
  'SC-02': ['action:launch_ordinary'],
  'SC-03': ['action:launch_isolated'],
  'SC-04': ['action:focus_session', 'query:list_sessions'],
  'SC-05': ['action:create_profile', 'action:edit_ordinary_profile', 'action:edit_isolated_profile', 'query:list_profiles'],
  'SC-06': ['command:request_import_discovery', 'query:list_import_sources', 'setup:windows_user_import'],
  'SC-07': ['action:archive_profile', 'action:restore_profile', 'action:delete_profile'],
  'SC-08': ['command:open_draft', 'command:set_draft_changes', 'command:discard_draft'],
  'SC-09': ['query:read_configuration', 'command:set_draft_changes', 'command:request_sensitive_input'],
  'SC-10': ['action:save_configuration', 'action:restore_configuration', 'query:configuration_history'],
  'SC-11': ['query:check_runtime_release', ...['install','update','repair','adopt','remove','stop_managing','switch_source'].map(value => `action:runtime_${value}`)],
  'SC-12': ['query:check_game_update', 'action:game_update', 'action:recover_game_update'],
  'SC-13': ['query:check_bridge_update', 'action:bridge_update', 'action:recover_bridge_update'],
  'SC-14': ['command:commit', 'query:resume_events'],
  'SC-15': ['command:cancel_operation', 'command:request_host_close', 'query:get_operation'],
  'SC-16': ['query:diagnostic_preview', 'command:request_export_destination', 'action:export_diagnostics'],
  'SC-17': ['action:save_application_preferences'],
  'SC-18': ['query:hello']
});

function features(value) {
  const found = new Set();
  const body = value?.body;
  if (body?.type === 'query') found.add(`query:${body.query?.name}`);
  if (body?.type === 'command') {
    found.add(`command:${body.command?.name}`);
    const intent = body.command?.name === 'prepare' ? body.command.input?.intent : undefined;
    if (intent) {
      found.add(`action:${intent.kind}`);
      if (intent.input?.setup) found.add(`setup:${intent.input.setup.kind}`);
    }
  }
  if (body?.type === 'result') {
    const result = body.result;
    if (result.type === 'query') found.add(`query:${result.query.name}`);
    if (result.type === 'command') {
      found.add(`command:${result.command.name}`);
      const semantics = result.command.output?.semantics;
      if (semantics) found.add(`action:${semantics.action}`);
    }
  }
  return found;
}

function schemaVariants(schema, name, tag) {
  const definition = schema.definitions?.[name];
  assert.ok(definition, `Missing Rust-derived definition ${name}`);
  const variants = definition.enum ?? (definition.oneOf ?? definition.anyOf)?.map(item => item.properties?.[tag]?.const);
  assert.ok(Array.isArray(variants) && variants.length && variants.every(value => typeof value === 'string'), `Unrecognized Rust-derived ${name} shape`);
  return variants;
}

export function verifyScenarioCoverage(fixtures, transcripts, aggregateSchema) {
  const positives = fixtures.filter(fixture => fixture.expectedSemantic);
  const byId = new Map(positives.map(fixture => [fixture.id, fixture]));
  const pairedFeatures = new Map(SCENARIOS.map(scenario => [scenario, new Set()]));
  const pairedExchanges = new Map(SCENARIOS.map(scenario => [scenario, 0]));
  for (const transcript of transcripts.filter(value => value.expected.accepted)) {
    assert.ok(SCENARIOS.includes(transcript.scenario));
    assert.ok(Array.isArray(transcript.steps));
    for (const step of transcript.steps.filter(value => value.type === 'exchange')) {
      const request = byId.get(step.request), reply = byId.get(step.reply);
      assert.equal(request?.kind, 'request', 'Paired coverage needs a codec-accepted request');
      assert.equal(reply?.kind, 'reply', 'Paired coverage needs a codec-accepted reply');
      assert.equal(request.payload.requestId, reply.payload.requestId, 'Paired coverage needs exact correlation');
      assert.equal(request.payload.protocolVersion, reply.payload.protocolVersion);
      pairedExchanges.set(transcript.scenario, pairedExchanges.get(transcript.scenario) + 1);
      const requested = request.payload.body;
      const returned = reply.payload.body;
      if (returned.type === 'result') {
        const result = returned.result;
        assert.equal(result.type, requested.type, 'Paired coverage needs a matching result family');
        assert.equal(result[result.type].name, requested[requested.type].name, 'Paired coverage needs a matching result method');
        if (requested.type === 'command' && requested.command.name === 'prepare') {
          assert.equal(result.command.output.semantics.action, requested.command.input.intent.kind, 'Paired action coverage needs its corresponding prepared result');
        }
        for (const feature of features(request.payload)) pairedFeatures.get(transcript.scenario).add(feature);
      }
    }
  }
  const completeFeatures = new Set([...pairedFeatures.values()].flatMap(values => [...values]));
  for (const [definition, tag, prefix] of [['ActionId', null, 'action'], ['Query', 'name', 'query'], ['Command', 'name', 'command']]) {
    for (const name of schemaVariants(aggregateSchema, definition, tag)) assert.ok(completeFeatures.has(`${prefix}:${name}`), `Missing accepted ${definition} vector: ${name}`);
  }
  const summaries = [];
  for (const scenario of SCENARIOS) {
    const cases = fixtures.filter(fixture => fixture.scenario === scenario);
    const accepted = cases.filter(fixture => fixture.expectedSemantic);
    const rejected = cases.filter(fixture => !fixture.expectedSemantic);
    const present = pairedFeatures.get(scenario);
    for (const feature of required[scenario]) assert.ok(present.has(feature), `${scenario} lacks its accepted domain case ${feature}`);
    assert.ok(rejected.length, `${scenario} lacks a codec refusal vector`);
    if (scenario !== 'SC-18') assert.ok(rejected.some(fixture => [...features(fixture.payload)].some(feature => present.has(feature))), `${scenario} refusal must exercise its own domain surface`);
    const journeys = transcripts.filter(transcript => transcript.scenario === scenario);
    assert.ok(journeys.some(transcript => transcript.expected.accepted), `${scenario} lacks an accepted paired synthetic transcript`);
    assert.ok(pairedExchanges.get(scenario) > 0, `${scenario} cannot be covered by a boundary-only transcript`);
    if (['SC-14', 'SC-15', 'SC-18'].includes(scenario)) assert.ok(journeys.some(transcript => !transcript.expected.accepted), `${scenario} lacks a deliberate relationship refusal transcript`);
    summaries.push({ scenario, accepted: accepted.length, codecRefusals: rejected.length, pairedTranscripts: journeys.length, pairedExchanges: pairedExchanges.get(scenario), features: [...present].sort() });
  }
  // These checks require representative data, not a generic hello/array pair
  // re-labelled eighteen times. Later packages prove behavior and interaction.
  const allValues = positives.map(fixture => fixture.payload);
  const collect = (value, key, result = new Set()) => {
    if (Array.isArray(value)) { for (const item of value) collect(item, key, result); }
    else if (value && typeof value === 'object') {
      if (Object.hasOwn(value, key) && typeof value[key] === 'string') result.add(value[key]);
      for (const child of Object.values(value)) collect(child, key, result);
    }
    return result;
  };
  const kinds = collect(allValues, 'kind');
  for (const name of schemaVariants(aggregateSchema, 'FieldType', 'kind')) assert.ok(kinds.has(name), `Missing schema field type ${name}`);
  const states = collect(allValues, 'state');
  for (const state of ['clean', 'dirty', 'invalid', 'stale']) assert.ok(states.has(state), `Missing configuration draft state ${state}`);
  const modes = collect(allValues, 'mode');
  for (const mode of ['legacy', 'sidecar', 'majel']) assert.ok(modes.has(mode), `Missing Data Sync mode ${mode}`);
  const themes = collect(allValues, 'theme');
  for (const theme of ['system', 'light', 'dark']) assert.ok(themes.has(theme), `Missing presentation preference ${theme}`);
  const hosts = collect(allValues, 'hostKind');
  for (const host of ['windows_x64', 'macos_arm64']) assert.ok(hosts.has(host), `Missing synthetic host binding ${host}`);
  return summaries;
}
