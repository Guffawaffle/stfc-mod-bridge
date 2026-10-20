// Synthetic protocol descriptors only. These cases perform no discovery,
// dispatch, native consent, account access, mutation or game launch.
import type {
  ActionId, BridgeError, Command, CommandResult, CreateProfileCapture, CreateProfileInput,
  EffectReceipt, InstallationBinding, InstallationProjection, IsolatedProfileRef,
  IsolatedLaunchInput, OrdinaryLaunchInput,
  MutationIntent, OperationSnapshot, OperationState, OrdinaryProfileRef, PlanSemantics,
  PreferredInstallationEdit, PreparedPlan, ProfileProjection, ProfileSetup, Query,
  QueryResult, RegisteredInstallationBinding, ResolvedTarget, RuntimeBinding,
  SessionBinding, SessionProjection, Snapshot
} from '../../ui/src/generated/protocol.js';
import {
  commandReply, commandRequest, isolatedBinding, isolatedSelector, ordinaryBinding,
  ordinarySelector, protocolEvent, queryReply, queryRequest, rejectedReply, syntheticId
} from './helpers.ts';
import {
  cursor, digest, evidence, eventFixture, hostEpoch, inventory, observed, operation,
  prepared, refusalFixture, replyFixture, requestFixture, streamId, unavailable
} from './authoring.ts';
import type { FixtureHooks, GoldenCatalog, GoldenFixture, GoldenStep, GoldenTranscript, ScenarioId } from './model.ts';

const registration: RegisteredInstallationBinding = {
  kind: 'registered', registrationId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
  registrationRevision: 'synthetic-installation-revision-1',
  physicalId: 'synthetic-installation-physical-1', nativeTargetRef: 'synthetic-native-target-1'
};
const alternateRegistration: RegisteredInstallationBinding = {
  ...registration, registrationId: 'dddddddddddddddddddddddddddddddd',
  physicalId: 'synthetic-installation-physical-2', nativeTargetRef: 'synthetic-native-target-2'
};
const ordinary: OrdinaryProfileRef = {
  kind: 'ordinary', catalogId: 'cccccccccccccccccccccccccccccccc',
  revision: 'synthetic-ordinary-revision-1', ownerScope: 'synthetic-native-owner-1'
};
const ordinaryLaunchTarget: OrdinaryLaunchInput['target'] = { installation: ordinarySelector.installation, profile: { kind: 'ordinary' } };
const isolatedLaunchTarget: IsolatedLaunchInput['target'] = { installation: isolatedSelector.installation, profile: { kind: 'isolated', id: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' } };
const profile = (state: 'active'|'archived' = 'active', id = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'): IsolatedProfileRef => ({ id, state, revision: 'synthetic-profile-revision-1' });
const isolatedProfile = (id = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', name = 'Synthetic profile'): ProfileProjection => ({
  kind: 'isolated', reference: profile('active', id), name,
  preferredInstallation: registration.registrationId, store: observed('new' as const)
});
const runtime: RuntimeBinding = {
  providerId: 'guffawaffle', distributionId: 'guffawaffle.stfc-community-mod',
  artifactDigest: digest('a'), manifest: { status: 'observed', digest: digest('b') },
  configurationSchemaDigest: digest('c'), clientRevision: 'synthetic-client-270',
  platform: 'windows', architecture: 'x86_64'
};
const error = (code: BridgeError['code'], retryDisposition: BridgeError['retryDisposition'] = 'never'): BridgeError => ({ code, retryDisposition, violations: [] });
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value));

export function buildCatalog(hooks: FixtureHooks): GoldenCatalog {
  const fixtures: GoldenFixture[] = [];
  const transcripts: GoldenTranscript[] = [];
  let sequence = 8000;
  const next = (): number => sequence++;
  const nextId = (): string => syntheticId(next());
  const key = (scenario: ScenarioId, name: string): string => `${scenario.toLowerCase()}-${name}`;
  const boundary = (): GoldenStep => ({ type: 'boundary', reason: 'initial', cursor });
  const transcript = (scenario: ScenarioId, name: string, steps: GoldenStep[], expected: GoldenTranscript['expected'] = { accepted: true }): void => {
    transcripts.push({ id: key(scenario, `${name}-journey`), scenario, case: name, steps: [boundary(), ...steps], expected });
  };
  type ExchangeStep = Extract<GoldenStep, {type:'exchange'}>;
  const queryPair = (scenario: ScenarioId, name: string, query: Query, result: QueryResult): ExchangeStep => {
    const requestId = nextId(), request = key(scenario, `${name}-request`), reply = key(scenario, `${name}-reply`);
    fixtures.push(requestFixture(request, scenario, name, queryRequest(requestId, query), ['query', 'synthetic']));
    fixtures.push(replyFixture(reply, scenario, name, queryReply(requestId, result), ['query', 'synthetic']));
    return { type: 'exchange', request, reply };
  };
  const commandPair = (scenario: ScenarioId, name: string, command: Command, result: CommandResult): ExchangeStep => {
    const requestId = nextId(), request = key(scenario, `${name}-request`), reply = key(scenario, `${name}-reply`);
    fixtures.push(requestFixture(request, scenario, name, commandRequest(requestId, command), ['command', 'synthetic']));
    fixtures.push(replyFixture(reply, scenario, name, commandReply(requestId, result), ['command', 'synthetic']));
    return { type: 'exchange', request, reply };
  };
  const rejected = (scenario: ScenarioId, name: string, command: Command, failure: BridgeError): void => {
    const requestId = nextId(), request = key(scenario, `${name}-request`), reply = key(scenario, `${name}-reply`);
    fixtures.push(requestFixture(request, scenario, name, commandRequest(requestId, command), ['requested', 'synthetic']));
    fixtures.push(replyFixture(reply, scenario, name, rejectedReply(requestId, failure), ['domain-refusal', 'synthetic']));
    transcript(scenario, name, [{ type: 'exchange', request, reply }]);
  };
  const review = (scenario: ScenarioId, name: string, intent: MutationIntent, semantics: PlanSemantics): { plan: PreparedPlan; step: ExchangeStep } => {
    const plan = prepared(hooks, semantics, next());
    return { plan, step: commandPair(scenario, `${name}-prepare`, { name: 'prepare', input: { intent } }, { name: 'prepare', output: plan }) };
  };
  const commit = (scenario: ScenarioId, name: string, plan: PreparedPlan, state: OperationState): { snapshot: OperationSnapshot; step: ExchangeStep; command: Command } => {
    const snapshot = operation(plan, next(), '1', state);
    const command: Command = { name: 'commit', input: { planRef: plan.planRef, idempotencyKey: nextId() } };
    return { snapshot, command, step: commandPair(scenario, `${name}-commit`, command, { name: 'commit', output: snapshot }) };
  };
  const completed = (receipt: EffectReceipt): OperationState => ({ status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt } });
  const unchanged: OperationState = { status: 'completed', outcome: { kind: 'no_change', reason: 'already_satisfied' } };
  const failed: OperationState = { status: 'completed', outcome: { kind: 'failed', error: error('internal_failure') } };
  const journey = (scenario: ScenarioId, name: string, intent: MutationIntent, semantics: PlanSemantics, state: OperationState): { plan: PreparedPlan; snapshot: OperationSnapshot } => {
    const reviewed = review(scenario, name, intent, semantics);
    const committed = commit(scenario, name, reviewed.plan, state);
    const observe = queryPair(scenario, `${name}-observe`, { name: 'get_operation', input: { operationId: committed.snapshot.operationId } }, { name: 'get_operation', output: { operation: observed(committed.snapshot) } });
    transcript(scenario, name, [reviewed.step, committed.step, observe]);
    return { plan: reviewed.plan, snapshot: committed.snapshot };
  };
  const session = (target: ResolvedTarget = ordinaryBinding, pid = 4200): SessionBinding => ({
    sessionId: nextId(), revision: 'synthetic-session-revision-1',
    process: { pid, startIdentity: { platform: 'windows', value: 'synthetic-process-generation-1' },
      executableIdentity: 'synthetic-game-executable-1', installationPhysicalId: target.installation.physicalId, architecture: 'x86_64' }
  });
  const sessionProjection = (binding: SessionBinding, target: ResolvedTarget, readiness: 'ordinary_spawned'|'isolated_initializing'|'isolated_ready'|'isolation_failed'): SessionProjection => ({
    binding, target: observed(target), liveIdentity: observed(true), readiness: observed(readiness)
  });
  const ordinarySemantics = (runtimeExpectation: Extract<PlanSemantics['capture'], {kind:'launch_ordinary'}>['runtime'] = { kind: 'absent' }): PlanSemantics => ({
    hashProfile: 'bridge-plan-semantic-json-v1', action: 'launch_ordinary', trustDomain: 'session', effects: ['launch_session'],
    capture: { kind: 'launch_ordinary', target: ordinaryBinding, catalogRevision: 'synthetic-catalog-1', runtime: runtimeExpectation }
  });
  const isolatedSemantics = (target = isolatedBinding, storeMode: 'new'|'resume'|'existing' = 'new'): PlanSemantics => ({
    hashProfile: 'bridge-plan-semantic-json-v1', action: 'launch_isolated', trustDomain: 'session',
    effects: storeMode === 'new' ? ['create_isolated_store', 'launch_session'] : ['launch_session'],
    capture: { kind: 'launch_isolated', target, catalogRevision: 'synthetic-catalog-1', runtime: { kind: 'absent' }, storeMode }
  });

  // SC-01: read-only inventories carry no selected process or target. A saved
  // preference is only a candidate; explicit resolve_target follows separately.
  for (const hostKind of ['windows_x64', 'macos_arm64'] as const) {
    const step = queryPair('SC-01', `hello-${hostKind.replaceAll('_', '-')}`, { name: 'hello', input: {} }, {
      name: 'hello', output: { supportedVersions: [1], hostEpoch, hostKind, implementedCommands: [
        'prepare', 'commit', 'cancel_operation', 'request_host_close', 'open_draft',
        'set_draft_changes', 'discard_draft', 'request_import_discovery', 'request_sensitive_input', 'request_export_destination'
      ] }
    });
    transcript('SC-01', `hello-${hostKind.replaceAll('_', '-')}`, [step]);
  }
  const installationProjection = (binding: InstallationBinding, name: string): InstallationProjection => ({ binding, name, client: unavailable(), update: unavailable() });
  const installations = [installationProjection(registration, 'Synthetic install A'), installationProjection(alternateRegistration, 'Synthetic install B')];
  const profiles: ProfileProjection[] = [{ kind: 'ordinary', reference: ordinary, preferredInstallation: registration.registrationId }, isolatedProfile(), isolatedProfile('eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', 'Synthetic profile')];
  const initial: Snapshot = {
    cursor, preferences: observed({ revision: 'synthetic-preferences-1', values: { theme: 'system', motion: 'system', lastTarget: { installationId: registration.registrationId, profile: { kind: 'ordinary' } } } }),
    profiles: observed(inventory(profiles)), installations: observed(inventory(installations)),
    sessions: observed(inventory([])), operations: inventory([]), capabilities: inventory([])
  };
  const initialStep = queryPair('SC-01', 'restore-multiple-candidates', { name: 'snapshot', input: {} }, { name: 'snapshot', output: initial });
  const resolveStep = queryPair('SC-01', 'resolve-explicit-ordinary', { name: 'resolve_target', input: { target: ordinarySelector } }, { name: 'resolve_target', output: { target: observed(ordinaryBinding) } });
  transcript('SC-01', 'restore-then-explicit-selection', [initialStep, resolveStep]);
  for (const status of ['missing', 'unavailable', 'invalid'] as const) {
    const snapshot: Snapshot = status === 'missing' ? { ...initial, profiles: { status: 'missing', evidence }, installations: { status: 'missing', evidence } }
      : status === 'unavailable' ? { ...initial, profiles: unavailable(), installations: unavailable(), sessions: unavailable() }
      : { ...initial, installations: observed({ items: [], completeness: 'partial', issues: [{ code: 'invalid_metadata', resource: { kind: 'installation', id: registration.registrationId } }], revision: 'synthetic-invalid-catalog-1' }) };
    transcript('SC-01', `discovery-${status}`, [queryPair('SC-01', `discovery-${status}`, { name: 'snapshot', input: {} }, { name: 'snapshot', output: snapshot })]);
  }
  transcript('SC-01', 'registered-installations-list', [queryPair('SC-01', 'registered-installations-list', { name: 'list_installations', input: {} }, { name: 'list_installations', output: observed(inventory(installations)) })]);
  const directoryTarget: ResolvedTarget = { installation: { kind: 'directory', physicalId: 'synthetic-explicit-directory', nativeTargetRef: 'synthetic-explicit-native-1' }, profile: ordinaryBinding.profile };
  transcript('SC-01', 'explicit-directory-override', [queryPair('SC-01', 'explicit-directory-override', { name: 'resolve_target', input: { target: { installation: { kind: 'directory', directory: { platform: 'windows', value: 'C:\\Synthetic\\SelectedGame' } }, profile: { kind: 'ordinary' } } } }, { name: 'resolve_target', output: { target: observed(directoryTarget) } })]);
  const invalidSnapshot = { ...initial, installations: observed({ items: [], completeness: 'partial' as const, issues: [], revision: 'synthetic-incomplete-1' }) };
  fixtures.push(refusalFixture('sc-01-partial-without-issue', 'SC-01', 'partial inventory needs an explicit issue', 'reply', queryReply(nextId(), { name: 'snapshot', output: invalidSnapshot })));
  const registerInput = { directory: { platform: 'windows' as const, value: 'C:\\Synthetic\\RegisteredGame' }, name: 'Synthetic registration', expectedCatalogRevision: 'synthetic-catalog-1' };
  journey('SC-01', 'register-explicit-installation', { kind: 'register_installation', input: registerInput }, {
    hashProfile: 'bridge-plan-semantic-json-v1', action: 'register_installation', trustDomain: 'profile_state', effects: ['register_installation'],
    capture: { kind: 'register_installation', input: { name: registerInput.name, physicalId: registration.physicalId, nativeTargetRef: registration.nativeTargetRef, catalogRevision: registerInput.expectedCatalogRevision } }
  }, completed({ kind: 'installation_registered', installation: registration, name: registerInput.name }));
  const editInstallInput = { installation: registration, name: 'Synthetic renamed registration' };
  journey('SC-01', 'edit-installation-name', { kind: 'edit_installation', input: editInstallInput }, {
    hashProfile: 'bridge-plan-semantic-json-v1', action: 'edit_installation', trustDomain: 'profile_state', effects: ['edit_installation_metadata'], capture: { kind: 'edit_installation', input: editInstallInput }
  }, completed({ kind: 'installation_edited', installation: { ...registration, registrationRevision: 'synthetic-installation-revision-2' }, name: editInstallInput.name }));

  // SC-02: observed spawn is distinct from an account/login claim. The per-attempt
  // unrecognized choice is captured, hashed, and never saved as a preference.
  for (const state of ['absent', 'verified'] as const) {
    const semantics = ordinarySemantics(state === 'absent' ? { kind: 'absent' } : { kind: 'verified', binding: runtime });
    journey('SC-02', `ordinary-${state}`, { kind: 'launch_ordinary', input: { target: ordinaryLaunchTarget } }, semantics,
      completed({ kind: 'session_spawned', session: sessionProjection(session(), ordinaryBinding, 'ordinary_spawned') }));
  }
  const allowOnce = ordinarySemantics({ kind: 'unrecognized', artifactDigest: digest('d'), choice: 'allow_once' });
  if (allowOnce.capture.kind !== 'launch_ordinary') throw new Error('Synthetic ordinary capture');
  allowOnce.capture.unrecognizedRuntimeChoice = 'allow_once';
  const allowed = journey('SC-02', 'ordinary-allow-once', { kind: 'launch_ordinary', input: { target: ordinaryLaunchTarget, unrecognizedRuntimeChoice: 'allow_once' } }, allowOnce,
    completed({ kind: 'session_spawned', session: sessionProjection(session(), ordinaryBinding, 'ordinary_spawned') }));
  const contradictory = clone(allowed.plan);
  if (contradictory.semantics.capture.kind !== 'launch_ordinary') throw new Error('Synthetic ordinary capture');
  contradictory.semantics.capture.unrecognizedRuntimeChoice = 'reject';
  fixtures.push(refusalFixture('sc-02-allow-once-capture-conflict', 'SC-02', 'runtime choice conflicts with captured intent', 'reply', commandReply(nextId(), { name: 'prepare', output: contradictory })));
  rejected('SC-02', 'unrecognized-without-consent', { name: 'prepare', input: { intent: { kind: 'launch_ordinary', input: { target: ordinaryLaunchTarget } } } }, error('artifact_unrecognized', 'after_user_choice'));
  rejected('SC-02', 'unknown-process-ownership', { name: 'prepare', input: { intent: { kind: 'launch_ordinary', input: { target: ordinaryLaunchTarget } } } }, error('target_unknown', 'after_resnapshot'));
  rejected('SC-02', 'unfinished-writer-blocks-launch', { name: 'prepare', input: { intent: { kind: 'launch_ordinary', input: { target: ordinaryLaunchTarget } } } }, error('operation_busy', 'after_resnapshot'));
  transcript('SC-02', 'ordinary-ready-action', [queryPair('SC-02', 'ordinary-ready-action', { name: 'get_actions', input: { scope: { kind: 'target', target: ordinaryBinding }, actions: ['launch_ordinary'] } }, { name: 'get_actions', output: [{ action: 'launch_ordinary', availability: { status: 'available', revision: 'synthetic-action-1', grantsLock: false, grantsPermission: false } }] })]);
  transcript('SC-02', 'ordinary-unknown-action', [queryPair('SC-02', 'ordinary-unknown-action', { name: 'get_actions', input: { scope: { kind: 'target', target: ordinaryBinding }, actions: ['launch_ordinary'] } }, { name: 'get_actions', output: [{ action: 'launch_ordinary', availability: { status: 'unknown', reason: { code: 'unknown_identity' } } }] })]);
  journey('SC-02', 'ordinary-no-change', { kind: 'launch_ordinary', input: { target: ordinaryLaunchTarget } }, ordinarySemantics(), unchanged);
  journey('SC-02', 'ordinary-failed', { kind: 'launch_ordinary', input: { target: ordinaryLaunchTarget } }, ordinarySemantics(), failed);

  // SC-03: immutable profiles share an installation but do not share session IDs.
  for (const [suffix, target, selector] of [
    ['profile-one', isolatedBinding, isolatedLaunchTarget],
    ['profile-two', { ...isolatedBinding, profile: { kind: 'isolated' as const, id: 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', revision: 'synthetic-profile-revision-1' } }, { ...isolatedLaunchTarget, profile: { kind: 'isolated' as const, id: 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee' } }]
  ] as const) {
    journey('SC-03', suffix, { kind: 'launch_isolated', input: { target: selector, storeMode: 'new' } }, isolatedSemantics(target),
      completed({ kind: 'session_spawned', session: sessionProjection(session(target), target, 'isolated_initializing') }));
  }
  for (const storeMode of ['resume', 'existing'] as const) {
    journey('SC-03', `isolated-${storeMode}`, { kind: 'launch_isolated', input: { target: isolatedLaunchTarget, storeMode } }, isolatedSemantics(isolatedBinding, storeMode),
      completed({ kind: 'session_spawned', session: sessionProjection(session(isolatedBinding), isolatedBinding, 'isolated_ready') }));
  }
  const isolatedSession = session(isolatedBinding);
  transcript('SC-03', 'failed-isolation-readiness', [queryPair('SC-03', 'failed-isolation-readiness', { name: 'list_sessions', input: {} }, { name: 'list_sessions', output: observed(inventory([sessionProjection(isolatedSession, isolatedBinding, 'isolation_failed')])) })]);
  rejected('SC-03', 'unsupported-runtime-isolation', { name: 'prepare', input: { intent: { kind: 'launch_isolated', input: { target: isolatedLaunchTarget, storeMode: 'existing' } } } }, error('unsupported_capability'));
  const duplicateReview = review('SC-03', 'duplicate-submit', { kind: 'launch_isolated', input: { target: isolatedLaunchTarget, storeMode: 'new' } }, isolatedSemantics());
  const duplicateCommit = commit('SC-03', 'duplicate-submit', duplicateReview.plan, { status: 'admitted' });
  const duplicateReplay = commandPair('SC-03', 'duplicate-submit-replay', duplicateCommit.command, { name: 'commit', output: duplicateCommit.snapshot });
  transcript('SC-03', 'duplicate-submit-original-operation', [duplicateReview.step, { ...duplicateCommit.step, delivery: 'lost' }, duplicateReplay]);
  fixtures.push(refusalFixture('sc-03-ordinary-target-in-isolated-intent', 'SC-03', 'isolated launch requires immutable isolated selector', 'request', {
    protocolVersion: 1, requestId: nextId(), body: { type: 'command', command: { name: 'prepare', input: { intent: { kind: 'launch_isolated', input: { target: ordinarySelector, storeMode: 'new' } } } } }
  }, false));
  const working = review('SC-03', 'working-readiness', { kind: 'launch_isolated', input: { target: isolatedLaunchTarget, storeMode: 'existing' } }, isolatedSemantics(isolatedBinding, 'existing'));
  const admitted = commit('SC-03', 'working-readiness', working.plan, { status: 'admitted' });
  const running = { ...admitted.snapshot, operationRevision: '2', state: { status: 'running', progress: { phase: 'initializing', measurement: { unit: 'unknown' } } } } satisfies OperationSnapshot;
  const finished = { ...admitted.snapshot, operationRevision: '3', state: completed({ kind: 'session_spawned', session: sessionProjection(session(isolatedBinding), isolatedBinding, 'isolated_ready') }) } satisfies OperationSnapshot;
  const runningId = 'sc-03-working-readiness-event', finishedId = 'sc-03-completed-readiness-event';
  fixtures.push(eventFixture(runningId, 'SC-03', 'working does not claim readiness', protocolEvent({ hostEpoch, streamId, sequence: '1' }, { type: 'operation_changed', operation: running })));
  fixtures.push(eventFixture(finishedId, 'SC-03', 'completed isolated readiness is explicit', protocolEvent({ hostEpoch, streamId, sequence: '2' }, { type: 'operation_changed', operation: finished })));
  transcript('SC-03', 'working-then-observed-ready', [working.step, admitted.step, { type: 'event', event: runningId }, { type: 'event', event: finishedId }]);

  // SC-04: focus uses the complete observed PID/start/executable tuple. Next-
  // launch metadata and a recycled PID cannot rewrite this session binding.
  const existingSession = session(isolatedBinding, 4300);
  const focusIntent: MutationIntent = { kind: 'focus_session', input: { session: existingSession } };
  const focusSemantics: PlanSemantics = { hashProfile: 'bridge-plan-semantic-json-v1', action: 'focus_session', trustDomain: 'session', effects: ['focus_session'], capture: { kind: 'focus_session', session: existingSession, target: isolatedBinding } };
  const focus = journey('SC-04', 'focus-exact-session', focusIntent, focusSemantics, completed({ kind: 'session_focused', session: existingSession }));
  const recycled = { ...existingSession, sessionId: nextId(), process: { ...existingSession.process, startIdentity: { platform: 'windows' as const, value: 'synthetic-process-generation-2' } } };
  const historical: SessionProjection = {
    ...sessionProjection(existingSession, isolatedBinding, 'isolated_ready'), liveIdentity: observed(false),
    target: { ...observed(isolatedBinding), evidence: { ...evidence, source: 'session_receipt' } },
    readiness: { ...observed('isolated_ready' as const), evidence: { ...evidence, source: 'session_receipt' } }
  };
  transcript('SC-04', 'multiple-and-recycled-process-observations', [queryPair('SC-04', 'multiple-and-recycled-process-observations', { name: 'list_sessions', input: {} }, { name: 'list_sessions', output: observed(inventory([
    historical, sessionProjection(recycled, isolatedBinding, 'isolated_ready'), sessionProjection(session(isolatedBinding, 4301), isolatedBinding, 'isolated_ready')
  ])) })]);
  for (const [name, code] of [['exited-session', 'target_missing'], ['stale-advisory-receipt', 'stale_revision'], ['inaccessible-live-identity', 'target_unknown']] as const) {
    rejected('SC-04', name, { name: 'prepare', input: { intent: focusIntent } }, error(code, 'after_resnapshot'));
  }
  const wrongFocus = clone(focus.plan);
  if (wrongFocus.semantics.capture.kind !== 'focus_session') throw new Error('Synthetic focus capture');
  wrongFocus.semantics.capture.session.process.installationPhysicalId = 'synthetic-other-installation';
  fixtures.push(refusalFixture('sc-04-focus-physical-binding-conflict', 'SC-04', 'exact process does not belong to captured installation', 'reply', commandReply(nextId(), { name: 'prepare', output: wrongFocus })));
  const staleRequest = commandRequest(nextId(), { name: 'prepare', input: { intent: { kind: 'focus_session', input: { session: recycled } } } });
  fixtures.push(requestFixture('sc-04-recycled-session-request', 'SC-04', 'same PID with a different start is a different request', staleRequest));
  const wrongReply = commandReply(staleRequest.requestId, { name: 'prepare', output: focus.plan });
  fixtures.push(replyFixture('sc-04-old-focus-reply', 'SC-04', 'independently valid old capture cannot answer new session', wrongReply));
  transcript('SC-04', 'refuse-cross-session-correspondence', [{ type: 'exchange', request: 'sc-04-recycled-session-request', reply: 'sc-04-old-focus-reply' }], { accepted: false, code: 'transcript_prepare_session', step: 1 });

  // SC-05: setup refs are producer-owned opaque references. Duplicate display
  // names are valid; immutable IDs and revisions preserve identity.
  transcript('SC-05', 'catalog-with-duplicate-display-names', [queryPair('SC-05', 'catalog-with-duplicate-display-names', { name: 'list_profiles', input: { state: 'all' } }, { name: 'list_profiles', output: observed(inventory(profiles)) })]);
  const createCase = (scenario: ScenarioId, name: string, setup: ProfileSetup, state?: OperationState): { plan: PreparedPlan; snapshot: OperationSnapshot } => {
    const input: CreateProfileInput = { name: 'Synthetic profile', setup, preferredInstallation: { kind: 'registered', id: registration.registrationId }, expectedCatalogRevision: 'synthetic-catalog-1' };
    const capture: CreateProfileCapture = { name: input.name, setup, preferredInstallation: registration, catalogRevision: input.expectedCatalogRevision, nativePreparationRef: `synthetic-${name}-prepare`, destinationOwner: ordinary.ownerScope };
    const semantics: PlanSemantics = { hashProfile: 'bridge-plan-semantic-json-v1', action: 'create_profile', trustDomain: 'profile_state', effects: ['publish_profile'], capture: { kind: 'create_profile', input: capture } };
    return journey(scenario, name, { kind: 'create_profile', input }, semantics, state ?? completed({ kind: 'profile_published', profile: isolatedProfile() }));
  };
  createCase('SC-05', 'create-new', { kind: 'new' });
  const storeStates = ['new', 'interrupted', 'established', 'missing_established'] as const;
  const storeProfiles: ProfileProjection[] = storeStates.map((state, index) => ({
    kind: 'isolated', reference: profile('active', (index + 10).toString(16).padStart(32, '0')), name: `Synthetic ${state} store`,
    preferredInstallation: registration.registrationId,
    store: observed(state)
  }));
  transcript('SC-05', 'observe-canonical-first-use-store-states', [queryPair('SC-05', 'observe-canonical-first-use-store-states', { name: 'list_profiles', input: { state: 'active' } }, { name: 'list_profiles', output: observed(inventory(storeProfiles)) })]);
  for (const [name, choice, preferred] of [
    ['ordinary-set', { kind: 'set', installation: registration }, registration.registrationId],
    ['ordinary-clear', { kind: 'clear' }, undefined],
    ['ordinary-keep', { kind: 'keep', expected: { kind: 'registered', id: registration.registrationId } }, registration.registrationId]
  ] satisfies [string, PreferredInstallationEdit, string|undefined][]) {
    const input = { profile: ordinary, preferredInstallation: choice };
    const projected: ProfileProjection = { kind: 'ordinary', reference: { ...ordinary, revision: 'synthetic-ordinary-revision-2' }, ...(preferred ? { preferredInstallation: preferred } : {}) };
    journey('SC-05', name, { kind: 'edit_ordinary_profile', input }, { hashProfile: 'bridge-plan-semantic-json-v1', action: 'edit_ordinary_profile', trustDomain: 'profile_state', effects: ['edit_profile_metadata'], capture: { kind: 'edit_ordinary_profile', input } }, completed({ kind: 'profile_edited', profile: projected }));
  }
  const editProfileInput = { profile: profile(), name: 'Synthetic renamed profile', preferredInstallation: { kind: 'keep' as const, expected: { kind: 'registered' as const, id: registration.registrationId } } };
  const edited = journey('SC-05', 'edit-isolated-name-preserves-preference', { kind: 'edit_isolated_profile', input: editProfileInput }, { hashProfile: 'bridge-plan-semantic-json-v1', action: 'edit_isolated_profile', trustDomain: 'profile_state', effects: ['edit_profile_metadata'], capture: { kind: 'edit_isolated_profile', input: editProfileInput } }, completed({ kind: 'profile_edited', profile: isolatedProfile(profile().id, editProfileInput.name) }));
  journey('SC-05', 'edit-already-satisfied-no-change', { kind: 'edit_isolated_profile', input: editProfileInput }, edited.plan.semantics, unchanged);
  const wrongPreference = clone(edited.snapshot);
  if (wrongPreference.state.status !== 'completed' || wrongPreference.state.outcome.kind !== 'changed' || wrongPreference.state.outcome.receipt?.kind !== 'profile_edited') throw new Error('Synthetic edited profile');
  wrongPreference.state.outcome.receipt.profile.preferredInstallation = alternateRegistration.registrationId;
  fixtures.push(refusalFixture('sc-05-edited-preference-conflict', 'SC-05', 'keep preserves captured registered preference', 'reply', commandReply(nextId(), { name: 'commit', output: wrongPreference })));
  fixtures.push(refusalFixture('sc-05-directory-is-not-saved-preference', 'SC-05', 'path override does not imply registration', 'request', {
    protocolVersion: 1, requestId: nextId(), body: { type: 'command', command: { name: 'prepare', input: { intent: { kind: 'create_profile', input: { name: 'Synthetic path preference', setup: { kind: 'new' }, preferredInstallation: { kind: 'directory', directory: { platform: 'windows', value: 'C:\\Synthetic\\Game' } }, expectedCatalogRevision: 'synthetic-catalog-1' } } } } }
  }, false));
  rejected('SC-05', 'stale-profile-revision', { name: 'prepare', input: { intent: { kind: 'edit_isolated_profile', input: editProfileInput } } }, error('stale_revision', 'after_resnapshot'));
  rejected('SC-05', 'missing-established-store', { name: 'prepare', input: { intent: { kind: 'launch_isolated', input: { target: isolatedLaunchTarget, storeMode: 'existing' } } } }, error('target_missing'));
  fixtures.push(refusalFixture('sc-05-client-setup-reference-cannot-create-identity', 'SC-05', 'first-use store modes belong to launch of an existing immutable ID', 'request', {
    protocolVersion: 1, requestId: nextId(), body: { type: 'command', command: { name: 'prepare', input: { intent: { kind: 'create_profile', input: { name: 'Synthetic invalid setup', setup: { kind: 'resume', nativeSetupRef: 'client-invented-setup-reference' }, preferredInstallation: { kind: 'registered', id: registration.registrationId }, expectedCatalogRevision: 'synthetic-catalog-1' } } } } }
  }, false));

  // SC-06: protected discovery is a request for native consent, never password
  // collection. A declined/cancelled import publishes no modeled profile.
  const importSource = { sourceId: 'synthetic-native-import-source', sourceRevision: 'synthetic-source-1', destinationOwner: ordinary.ownerScope, requiresNativeApproval: true };
  const sourceProjection = { reference: importSource, name: 'Synthetic native user', accessibility: observed(true) };
  transcript('SC-06', 'discover-readable-import-sources', [queryPair('SC-06', 'discover-readable-import-sources', { name: 'list_import_sources', input: {} }, { name: 'list_import_sources', output: observed(inventory([sourceProjection])) })]);
  for (const approval of ['decline', 'request_native_approval'] as const) {
    const step = commandPair('SC-06', `native-discovery-${approval.replaceAll('_', '-')}`, { name: 'request_import_discovery', input: { approval, expectedDestinationOwner: ordinary.ownerScope } }, { name: 'request_import_discovery', output: approval === 'decline' ? unavailable() : observed(inventory([sourceProjection])) });
    transcript('SC-06', `native-discovery-${approval.replaceAll('_', '-')}`, [step]);
  }
  transcript('SC-06', 'import-platform-unavailable', [queryPair('SC-06', 'import-platform-unavailable', { name: 'list_import_sources', input: {} }, { name: 'list_import_sources', output: { status: 'unavailable', reason: 'unsupported_platform' } })]);
  const imported = createCase('SC-06', 'import-reviewed-owner-and-consent', { kind: 'windows_user_import', source: importSource, approval: 'request_native_approval' });
  rejected('SC-06', 'import-declined-consent', { name: 'prepare', input: { intent: { kind: 'create_profile', input: { name: 'Synthetic import', setup: { kind: 'windows_user_import', source: importSource, approval: 'decline' }, preferredInstallation: { kind: 'registered', id: registration.registrationId }, expectedCatalogRevision: 'synthetic-catalog-1' } } } }, error('unsupported_capability', 'after_user_choice'));
  rejected('SC-06', 'import-denied-native-access', { name: 'request_import_discovery', input: { approval: 'request_native_approval', expectedDestinationOwner: ordinary.ownerScope } }, error('wrong_owner', 'after_user_choice'));
  const cancelledReview = review('SC-06', 'import-cancel', { kind: 'create_profile', input: { name: 'Synthetic profile', setup: { kind: 'windows_user_import', source: importSource, approval: 'request_native_approval' }, preferredInstallation: { kind: 'registered', id: registration.registrationId }, expectedCatalogRevision: 'synthetic-catalog-1' } }, imported.plan.semantics);
  const importing = commit('SC-06', 'import-cancel', cancelledReview.plan, { status: 'admitted' });
  const cancelled = { ...importing.snapshot, operationRevision: '2', state: { status: 'completed', outcome: { kind: 'cancelled_before_commit', reason: 'cancellation_accepted' } } } satisfies OperationSnapshot;
  const cancelStep = commandPair('SC-06', 'import-cancel-before-publication', { name: 'cancel_operation', input: { operationId: importing.snapshot.operationId, expectedOperationRevision: '1' } }, { name: 'cancel_operation', output: { kind: 'cancelled_before_commit', operation: cancelled } });
  transcript('SC-06', 'import-cancel-before-publication', [cancelledReview.step, importing.step, cancelStep]);
  const wrongOwner = clone(imported.plan);
  if (wrongOwner.semantics.capture.kind !== 'create_profile') throw new Error('Synthetic import capture');
  wrongOwner.semantics.capture.input.destinationOwner = 'synthetic-other-native-owner';
  fixtures.push(refusalFixture('sc-06-import-owner-conflict', 'SC-06', 'native source destination owner must match reviewed capture', 'reply', commandReply(nextId(), { name: 'prepare', output: wrongOwner })));
  fixtures.push(refusalFixture('sc-06-password-is-not-protocol-input', 'SC-06', 'protected credentials never enter protocol payloads', 'request', {
    protocolVersion: 1, requestId: nextId(), body: { type: 'command', command: { name: 'request_import_discovery', input: { approval: 'request_native_approval', expectedDestinationOwner: ordinary.ownerScope, password: 'SYNTHETIC_SECRET_SENTINEL' } } }
  }, false));

  // SC-07: lifecycle state never replaces immutable ID. Permanent deletion has
  // its own confirmation and ordinary entries advertise a protected refusal.
  for (const kind of ['archive_profile', 'restore_profile', 'delete_profile'] as const) {
    const inputProfile = profile(kind === 'archive_profile' ? 'active' : 'archived');
    const intent: MutationIntent = kind === 'delete_profile' ? { kind, input: { profile: inputProfile, confirmation: 'delete_entire_owned_profile' } } : { kind, input: { profile: inputProfile } };
    const capture: PlanSemantics['capture'] = kind === 'delete_profile' ? { kind, input: { profile: inputProfile, confirmation: 'delete_entire_owned_profile' } } : { kind, input: { profile: inputProfile } };
    const action: ActionId = kind;
    const receipt: EffectReceipt = kind === 'archive_profile' ? { kind: 'profile_archived', profile: { ...inputProfile, state: 'archived', revision: 'synthetic-profile-revision-2' } }
      : kind === 'restore_profile' ? { kind: 'profile_restored', profile: { ...inputProfile, state: 'active', revision: 'synthetic-profile-revision-2' } }
      : { kind: 'profile_deleted', profileId: inputProfile.id };
    journey('SC-07', kind.replaceAll('_', '-'), intent, { hashProfile: 'bridge-plan-semantic-json-v1', action, capture, trustDomain: 'profile_state', effects: [kind === 'delete_profile' ? 'delete_owned_profile' : kind] }, completed(receipt));
    rejected('SC-07', `${kind.replaceAll('_', '-')}-active-session`, { name: 'prepare', input: { intent } }, error('operation_busy', 'after_resnapshot'));
  }
  transcript('SC-07', 'ordinary-lifecycle-protected', [queryPair('SC-07', 'ordinary-lifecycle-protected', { name: 'get_actions', input: { scope: { kind: 'target', target: ordinaryBinding }, actions: ['archive_profile', 'delete_profile'] } }, { name: 'get_actions', output: [
    { action: 'archive_profile', availability: { status: 'blocked', reasons: [{ code: 'wrong_profile_kind', resource: { kind: 'profile', id: ordinary.catalogId } }] } },
    { action: 'delete_profile', availability: { status: 'blocked', reasons: [{ code: 'wrong_profile_kind', resource: { kind: 'profile', id: ordinary.catalogId } }] } }
  ] })]);
  fixtures.push(refusalFixture('sc-07-archive-already-archived', 'SC-07', 'archive requires active lifecycle baseline', 'request', commandRequest(nextId(), { name: 'prepare', input: { intent: { kind: 'archive_profile', input: { profile: profile('archived') } } } })));
  fixtures.push(refusalFixture('sc-07-delete-needs-separate-confirmation', 'SC-07', 'deletion confirmation cannot be inferred from archive', 'request', {
    protocolVersion: 1, requestId: nextId(), body: { type: 'command', command: { name: 'prepare', input: { intent: { kind: 'delete_profile', input: { profile: profile('archived') } } } } }
  }, false));
  const lifecycleIntent: MutationIntent = { kind: 'archive_profile', input: { profile: profile() } };
  const lifecycleSemantics: PlanSemantics = { hashProfile: 'bridge-plan-semantic-json-v1', action: 'archive_profile', trustDomain: 'profile_state', effects: ['archive_profile'], capture: { kind: 'archive_profile', input: { profile: profile() } } };
  const abandoned = review('SC-07', 'delete-review-abandoned', { kind: 'delete_profile', input: { profile: profile('archived'), confirmation: 'delete_entire_owned_profile' } }, {
    hashProfile: 'bridge-plan-semantic-json-v1', action: 'delete_profile', trustDomain: 'profile_state', effects: ['delete_owned_profile'], capture: { kind: 'delete_profile', input: { profile: profile('archived'), confirmation: 'delete_entire_owned_profile' } }
  });
  transcript('SC-07', 'delete-review-abandoned-without-admission', [abandoned.step]);
  const lifecycleReview = review('SC-07', 'lifecycle-recovery', lifecycleIntent, lifecycleSemantics);
  const recoveryNumber = next(), recoveryOperationId = syntheticId(recoveryNumber);
  const recovering = operation(lifecycleReview.plan, recoveryNumber, '1', { status: 'recovery_required', reason: 'native_custody_unresolved', recovery: { operationId: recoveryOperationId, transaction: 'synthetic-owned-profile-journal', target: { kind: 'profile', profile: profile() } } });
  const recoveryStep = commandPair('SC-07', 'lifecycle-recovery-commit', { name: 'commit', input: { planRef: lifecycleReview.plan.planRef, idempotencyKey: nextId() } }, { name: 'commit', output: recovering });
  transcript('SC-07', 'lifecycle-native-custody-recovery', [lifecycleReview.step, recoveryStep]);

  // Preserve type-only authoring: the factories return generated DTOs. These
  // synthetic captures/receipts are checked by the actual Rust codec at emit.
  return { fixtures, transcripts };
}
