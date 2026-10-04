import type {
  BridgeApplicationBinding, BridgeRecoveryRef, BridgeReleaseSelectionRef,
  CheckedGameUpdateRef, GameClientBinding, ManagedRuntimeRef, NativeGameRecoveryRef,
  InstallationBinding, InstallationProjection, OperationSnapshot, PlanSemantics, PreparedPlan, Request, Reply, RuntimeBinding,
  RuntimeDeployInput, RuntimeReleaseSelectionRef, RuntimeSwitchSourceInput
} from '../../ui/src/generated/protocol.js';
import { commandReply, commandRequest, ordinaryBinding, queryReply, queryRequest, rejectedReply, syntheticId } from './helpers.ts';
import { cursor, digest, hostEpoch, inventory, observed, operation, prepared, refusalFixture, replyFixture, requestFixture } from './authoring.ts';
import { existingDocument, schemaBinding } from './configuration-cases.ts';
import type { FixtureHooks, GoldenCatalog, GoldenFixture, GoldenTranscript, ScenarioId } from './model.ts';

const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value));
export const runtimeBinding: RuntimeBinding = {
  providerId: 'guffawaffle', distributionId: 'guffawaffle.stfc-community-mod', artifactDigest: digest('b'),
  manifest: { status: 'observed', digest: digest('f') }, configurationSchemaDigest: digest('a'),
  clientRevision: 'synthetic-client-270', platform: 'windows', architecture: 'x86_64'
};
export const runtimeRelease: RuntimeReleaseSelectionRef = {
  selectionId: syntheticId(3000), hostEpoch, revision: 'synthetic-runtime-release-1', target: ordinaryBinding,
  providerId: 'guffawaffle', distributionId: 'guffawaffle.stfc-community-mod', channelId: 'stable', releaseVersion: '2.1.0-guffa.9',
  clientRevision: 'synthetic-client-270', artifacts: [
    { role: 'runtime_module', platform: 'windows', architecture: 'x86_64', digest: digest('b'), size: '512' },
    { role: 'runtime_manifest', platform: 'windows', architecture: 'x86_64', digest: digest('f'), size: '256' },
    { role: 'configuration_schema', platform: 'windows', architecture: 'x86_64', digest: digest('a'), size: '1024' }
  ], configurationSchema: schemaBinding, authority: 'synthetic-runtime-authority-1'
};
export const managedRuntime: ManagedRuntimeRef = { receiptId: 'synthetic-runtime-receipt-1', revision: 'synthetic-runtime-management-1', target: ordinaryBinding, binding: runtimeBinding };
const client = (version: string): GameClientBinding => ({ version, executableDigest: digest(version === '270' ? 'c' : 'd'), architecture: 'x86_64' });
export const checkedUpdate: CheckedGameUpdateRef = {
  checkId: syntheticId(3100), hostEpoch, revision: 'synthetic-game-check-1', installation: ordinaryBinding.installation,
  currentClient: client('270'), offeredClient: client('271'), officialManifestDigest: digest('e'), route: 'canonical_native_direct'
};
export const nativeRecovery: NativeGameRecoveryRef = {
  installation: ordinaryBinding.installation, nativeTransaction: 'synthetic-native-installation-path-key',
  revision: 'synthetic-game-journal-1', expectedClient: client('270')
};
export const bridgeApplication: BridgeApplicationBinding = {
  applicationId: 'guffawaffle.stfc-mod-bridge', installationRef: 'synthetic-bridge-installation-1', revision: 'synthetic-bridge-app-1',
  channelId: 'stable', platform: 'windows', architecture: 'x86_64', packageDigest: digest('1'), pairingDigest: digest('2'),
  payloads: [
    { role: 'application', digest: digest('1'), size: '1024' },
    { role: 'profiles_native', digest: digest('3'), size: '512' },
    { role: 'toml_native', digest: digest('4'), size: '512' },
    { role: 'update_helper', digest: digest('5'), size: '512' }
  ]
};
const offeredBridge: BridgeApplicationBinding = { ...bridgeApplication, revision: 'synthetic-bridge-app-2', packageDigest: digest('6'), pairingDigest: digest('7'), payloads: [
  { role: 'application', digest: digest('6'), size: '1024' },
  { role: 'profiles_native', digest: digest('3'), size: '512' },
  { role: 'toml_native', digest: digest('4'), size: '512' },
  { role: 'update_helper', digest: digest('5'), size: '512' }
] };
export const bridgeRelease: BridgeReleaseSelectionRef = { selectionId: syntheticId(3200), hostEpoch, current: bridgeApplication, offered: offeredBridge, releaseVersion: '1.0.0', authority: 'synthetic-bridge-authority-1', revision: 'synthetic-bridge-release-1' };
export const bridgeRecovery: BridgeRecoveryRef = { application: bridgeApplication, expectedApplication: bridgeApplication, bridgeJournalRef: 'synthetic-bridge-journal-1', revision: 'synthetic-bridge-journal-1' };

export function buildCatalog(hooks: FixtureHooks): GoldenCatalog {
  const fixtures: GoldenFixture[] = [], transcripts: GoldenTranscript[] = [];
  let sequence = 3300;
  const id = () => syntheticId(sequence++);
  function pair(name: string, scenario: ScenarioId, case_: string, request: Request, reply: Reply): void {
    const requestId = `${name}-request`, replyId = `${name}-reply`;
    fixtures.push(requestFixture(requestId, scenario, case_, request), replyFixture(replyId, scenario, case_, reply));
    transcripts.push({ id: `${name}-journey`, scenario, case: case_, steps: [{ type: 'boundary', reason: 'initial', cursor }, { type: 'exchange', request: requestId, reply: replyId }], expected: { accepted: true } });
  }
  function preparePair(name: string, scenario: ScenarioId, request: Request, plan: PreparedPlan): void {
    pair(name, scenario, 'Exact reviewed domain input remains separate from admission and execution', request, commandReply(request.requestId, { name: 'prepare', output: plan }));
  }
  function resultPair(name: string, scenario: ScenarioId, value: OperationSnapshot): void {
    const requestId = id();
    pair(name, scenario, 'Typed operation result accounts for captured participants and their resulting bytes',
      queryRequest(requestId, { name: 'get_operation', input: { operationId: value.operationId } }),
      queryReply(requestId, { name: 'get_operation', output: { operation: observed(value) } }));
  }
  let requestId = id();
  pair('sc11-check-runtime-release', 'SC-11', 'Runtime metadata uses its own reviewed provider authority',
    queryRequest(requestId, { name: 'check_runtime_release', input: { target: ordinaryBinding, providerId: 'guffawaffle', channelId: 'stable' } }),
    queryReply(requestId, { name: 'check_runtime_release', output: observed(runtimeRelease) }));
  const deploy = (managed: boolean): RuntimeDeployInput => ({ target: ordinaryBinding, selectedRelease: runtimeRelease, expectedOwnership: managed ? { kind: 'managed', reference: managedRuntime } : { kind: 'absent' }, configuration: { kind: 'unchanged', document: existingDocument } });
  const plans: PreparedPlan[] = [];
  for (const action of ['runtime_install', 'runtime_update', 'runtime_repair'] as const) {
    const input = deploy(action !== 'runtime_install');
    const semantics: PlanSemantics = { hashProfile: 'bridge-plan-semantic-json-v1', action, capture: { kind: action, input, preparedConfiguration: { kind: 'unchanged', document: existingDocument } }, trustDomain: 'runtime_distribution', effects: ['replace_runtime'] };
    const plan = prepared(hooks, semantics, sequence++); plans.push(plan);
    preparePair(`sc11-${action.replaceAll('_', '-')}`, 'SC-11', commandRequest(id(), { name: 'prepare', input: { intent: { kind: action, input } } }), plan);
    resultPair(`sc11-${action.replaceAll('_', '-')}-result`, 'SC-11', operation(plan, sequence++, '3', { status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt: { kind: 'runtime_managed', reference: managedRuntime, configuration: { kind: 'unchanged', document: existingDocument } } } }));
  }
  const adoption = { target: ordinaryBinding, observedArtifactDigest: digest('b'), recognizedBinding: runtimeBinding, recognitionAuthority: 'synthetic-runtime-recognition-1', confirmation: 'adopt_exact_recognized_artifact' as const };
  const adopt = prepared(hooks, { hashProfile: 'bridge-plan-semantic-json-v1', action: 'runtime_adopt', capture: { kind: 'runtime_adopt', input: adoption }, trustDomain: 'runtime_distribution', effects: ['adopt_runtime'] }, sequence++);
  preparePair('sc11-runtime-adopt', 'SC-11', commandRequest(id(), { name: 'prepare', input: { intent: { kind: 'runtime_adopt', input: adoption } } }), adopt);
  for (const action of ['runtime_remove', 'runtime_stop_managing'] as const) {
    const input = { reference: managedRuntime };
    const plan = prepared(hooks, { hashProfile: 'bridge-plan-semantic-json-v1', action, capture: { kind: action, input }, trustDomain: 'runtime_distribution', effects: [action === 'runtime_remove' ? 'remove_managed_runtime' : 'release_runtime_management'] }, sequence++);
    preparePair(`sc11-${action.replaceAll('_', '-')}`, 'SC-11', commandRequest(id(), { name: 'prepare', input: { intent: { kind: action, input } } }), plan);
    resultPair(`sc11-${action.replaceAll('_', '-')}-result`, 'SC-11', operation(plan, sequence++, '3', { status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt: { kind: action === 'runtime_remove' ? 'runtime_removed' : 'runtime_unmanaged', target: ordinaryBinding } } }));
  }
  const destinationSchema = { ...schemaBinding, providerId: 'netniv', digest: digest('9'), runtimeArtifactDigest: digest('8') };
  const destinationRelease: RuntimeReleaseSelectionRef = { ...runtimeRelease, selectionId: id(), providerId: 'netniv', distributionId: 'netniv.stfc-community-mod', releaseVersion: '1.1.6.0', configurationSchema: destinationSchema, artifacts: [{ role: 'runtime_module', platform: 'windows', architecture: 'x86_64', digest: digest('8'), size: '512' }, { role: 'configuration_schema', platform: 'windows', architecture: 'x86_64', digest: digest('9'), size: '512' }], authority: 'synthetic-netniv-runtime-authority-1' };
  const switchInput: RuntimeSwitchSourceInput = { current: managedRuntime, selectedRelease: destinationRelease, configuration: { kind: 'compatible_migration', document: existingDocument, destination: destinationSchema }, confirmation: 'switch_runtime_and_configuration_source' };
  const switchPlan = prepared(hooks, { hashProfile: 'bridge-plan-semantic-json-v1', action: 'runtime_switch_source', capture: { kind: 'runtime_switch_source', input: switchInput, preparedConfiguration: { kind: 'write', baseline: existingDocument, destinationSchema, candidateDigest: digest('7') } }, trustDomain: 'runtime_distribution', effects: ['replace_runtime', 'write_configuration'] }, sequence++);
  preparePair('sc11-runtime-switch-source', 'SC-11', commandRequest(id(), { name: 'prepare', input: { intent: { kind: 'runtime_switch_source', input: switchInput } } }), switchPlan);
  const switchedDocument = { ...existingDocument, revision: 'synthetic-document-2', schema: destinationSchema, baseline: { kind: 'existing' as const, fileIdentity: 'synthetic-file-2', contentDigest: digest('7') } };
  const switchedReference: ManagedRuntimeRef = { ...managedRuntime, revision: 'synthetic-runtime-management-2', binding: { ...runtimeBinding, providerId: 'netniv', distributionId: destinationRelease.distributionId, artifactDigest: digest('8'), configurationSchemaDigest: digest('9'), manifest: { status: 'missing' } } };
  const switched = operation(switchPlan, sequence++, '3', { status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt: { kind: 'runtime_managed', reference: switchedReference, configuration: { kind: 'written', document: switchedDocument, backup: { backupId: id(), document: existingDocument, retainedDigest: digest('c'), nativeBackupRef: 'synthetic-native-source-switch-backup', createdAt: '2026-10-03T11:00:00Z' } } } } });
  resultPair('sc11-runtime-compound-result', 'SC-11', switched);
  const missingConfiguration = clone(switched);
  if (missingConfiguration.state.status === 'completed' && missingConfiguration.state.outcome.kind === 'changed' && missingConfiguration.state.outcome.receipt?.kind === 'runtime_managed') missingConfiguration.state.outcome.receipt.configuration = { kind: 'unchanged', document: existingDocument };
  fixtures.push(refusalFixture('sc11-runtime-omitted-configuration-participant', 'SC-11', 'Runtime success cannot report unchanged configuration for a reviewed migration', 'reply', queryReply(id(), { name: 'get_operation', output: { operation: observed(missingConfiguration) } })));
  const alteredAuthorityBinding = clone(deploy(false)); alteredAuthorityBinding.selectedRelease.configurationSchema.providerId = 'netniv';
  fixtures.push(refusalFixture('sc11-runtime-provider-schema-mismatch', 'SC-11', 'Provider identity cannot cross the selected runtime schema authority', 'request', commandRequest(id(), { name: 'prepare', input: { intent: { kind: 'runtime_install', input: alteredAuthorityBinding } } })));
  for (const code of ['verification_failed', 'artifact_unrecognized', 'release_withdrawn', 'operation_busy'] as const) {
    requestId = id(); pair(`sc11-runtime-${code.replaceAll('_', '-')}`, 'SC-11', `Runtime refusal ${code} remains visible before mutation`, commandRequest(requestId, { name: 'prepare', input: { intent: { kind: 'runtime_install', input: deploy(false) } } }), rejectedReply(requestId, { code, retryDisposition: 'after_user_choice', violations: [] }));
  }

  requestId = id();
  pair('sc12-check-official-game', 'SC-12', 'Checked native game route binds platform version and installation', queryRequest(requestId, { name: 'check_game_update', input: { installation: { kind: 'registered', id: 'a'.repeat(32) } } }), queryReply(requestId, { name: 'check_game_update', output: observed(checkedUpdate) }));
  const updateInput = { checkedUpdate };
  const gamePlan = prepared(hooks, { hashProfile: 'bridge-plan-semantic-json-v1', action: 'game_update', capture: { kind: 'game_update', input: updateInput }, trustDomain: 'game_client', effects: ['update_game'] }, sequence++);
  preparePair('sc12-game-update', 'SC-12', commandRequest(id(), { name: 'prepare', input: { intent: { kind: 'game_update', input: updateInput } } }), gamePlan);
  resultPair('sc12-game-update-result', 'SC-12', operation(gamePlan, sequence++, '3', { status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt: { kind: 'game_updated', installation: ordinaryBinding.installation, client: client('271') } } }));
  const recoveryOperation = operation(gamePlan, sequence++, '3', { status: 'recovery_required', reason: 'interrupted_transaction', recovery: { operationId: syntheticId(sequence), transaction: 'synthetic-bridge-game-operation-1', target: { kind: 'game', recovery: nativeRecovery } } });
  if (recoveryOperation.state.status === 'recovery_required') recoveryOperation.state.recovery.operationId = recoveryOperation.operationId;
  resultPair('sc12-game-rollback-required', 'SC-12', recoveryOperation);
  const recoverInput = { recovery: nativeRecovery };
  const recoveryPlan = prepared(hooks, { hashProfile: 'bridge-plan-semantic-json-v1', action: 'recover_game_update', capture: { kind: 'recover_game_update', input: recoverInput }, trustDomain: 'game_client', effects: ['recover_game'] }, sequence++);
  preparePair('sc12-recover-prior-game-image', 'SC-12', commandRequest(id(), { name: 'prepare', input: { intent: { kind: 'recover_game_update', input: recoverInput } } }), recoveryPlan);
  resultPair('sc12-prior-game-restored', 'SC-12', operation(recoveryPlan, sequence++, '3', { status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt: { kind: 'game_updated', installation: ordinaryBinding.installation, client: client('270') } } }));
  const forwardRecovery = clone(recoveryOperation);
  if (forwardRecovery.state.status === 'recovery_required' && forwardRecovery.state.recovery.target.kind === 'game') forwardRecovery.state.recovery.target.recovery.expectedClient = client('271');
  fixtures.push(refusalFixture('sc12-forward-image-as-native-rollback', 'SC-12', 'Canonical native rollback must restore current270 rather than offered271', 'reply', queryReply(id(), { name: 'get_operation', output: { operation: observed(forwardRecovery) } })));
  const projection: InstallationProjection = {
    binding: ordinaryBinding.installation, name: 'Synthetic registered installation',
    client: observed(client('270')), update: observed(nativeRecovery)
  };
  const retained = clone(projection);
  if (retained.update.status === 'observed' && retained.update.value.installation.kind === 'registered') {
    retained.update.value.installation.registrationRevision = 'synthetic-installation-revision-0';
  }
  requestId = id();
  pair('sc12-installation-retained-recovery-revision', 'SC-12', 'Recovery retains the same installation despite older registration metadata',
    queryRequest(requestId, { name: 'list_installations', input: {} }),
    queryReply(requestId, { name: 'list_installations', output: observed(inventory([retained])) }));
  const binding = ordinaryBinding.installation;
  if (binding.kind !== 'registered') throw new Error('Synthetic recovery vectors require a registered installation');
  const foreignBindings: [string, InstallationBinding][] = [
    ['registration', { ...binding, registrationId: 'b'.repeat(32) }],
    ['physical', { ...binding, physicalId: 'synthetic-foreign-installation' }],
    ['native-target', { ...binding, nativeTargetRef: 'synthetic-foreign-native-target' }],
    ['directory-kind', { kind: 'directory', physicalId: binding.physicalId, nativeTargetRef: binding.nativeTargetRef }]
  ];
  for (const [name, foreign] of foreignBindings) {
    const invalid = clone(projection);
    if (invalid.update.status === 'observed') invalid.update.value.installation = foreign;
    fixtures.push(refusalFixture(`sc12-installation-foreign-recovery-${name}`, 'SC-12',
      'A projected recovery cannot change registration, physical, native or binding kind identity', 'reply',
      queryReply(id(), { name: 'list_installations', output: observed(inventory([invalid])) })));
  }
  requestId = id();
  pair('sc12-game-route-unavailable', 'SC-12', 'A platform without a qualified native update route is explicitly unavailable', queryRequest(requestId, { name: 'check_game_update', input: { installation: { kind: 'registered', id: 'a'.repeat(32) } } }), rejectedReply(requestId, { code: 'native_unavailable', retryDisposition: 'after_user_choice', violations: [] }));

  requestId = id();
  pair('sc13-check-bridge-update', 'SC-13', 'Bridge update authority is separate from runtime and game authorities', queryRequest(requestId, { name: 'check_bridge_update', input: { application: bridgeApplication } }), queryReply(requestId, { name: 'check_bridge_update', output: observed(bridgeRelease) }));
  const bridgeInput = { selectedRelease: bridgeRelease };
  const bridgePlan = prepared(hooks, { hashProfile: 'bridge-plan-semantic-json-v1', action: 'bridge_update', capture: { kind: 'bridge_update', input: bridgeInput }, trustDomain: 'bridge_application', effects: ['replace_bridge'] }, sequence++);
  preparePair('sc13-bridge-update', 'SC-13', commandRequest(id(), { name: 'prepare', input: { intent: { kind: 'bridge_update', input: bridgeInput } } }), bridgePlan);
  resultPair('sc13-bridge-update-exact-pair', 'SC-13', operation(bridgePlan, sequence++, '3', { status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt: { kind: 'bridge_updated', application: offeredBridge } } }));
  for (const [name, expected] of [['current', bridgeApplication], ['offered', offeredBridge]] as const) {
    const operationId = id();
    const recovery = { operationId, transaction: 'synthetic-bridge-journal-1', target: {
      kind: 'bridge' as const, recovery: { ...bridgeRecovery, expectedApplication: expected }
    } };
    const value: OperationSnapshot = { operationId, operationRevision: '3', semantics: bridgePlan.semantics,
      state: { status: 'recovery_required', reason: 'interrupted_transaction', recovery } };
    resultPair(`sc13-bridge-recovery-reviewed-${name}`, 'SC-13', value);
    if (name === 'current') {
      const alteredApplications: [string, BridgeApplicationBinding][] = [
        ['channel', { ...expected, channelId: 'development' }],
        ['platform', { ...expected, platform: 'macos', architecture: 'arm64' }],
        ['application-id', { ...expected, applicationId: 'synthetic.foreign-bridge' }],
        ['missing-application-payload', { ...expected, payloads: [] }],
        ['third-pair', { ...expected, revision: 'synthetic-bridge-app-3' }]
      ];
      for (const [alteration, application] of alteredApplications) {
        const invalid = clone(value);
        if (invalid.state.status === 'recovery_required' && invalid.state.recovery.target.kind === 'bridge') {
          invalid.state.recovery.target.recovery.expectedApplication = application;
        }
        fixtures.push(refusalFixture(`sc13-bridge-recovery-invalid-${alteration}`, 'SC-13',
          'Interrupted Bridge update recovery must retain a valid exact reviewed current or offered pair', 'reply',
          queryReply(id(), { name: 'get_operation', output: { operation: observed(invalid) } })));
        if (alteration !== 'third-pair') {
          const malformed = { ...recovery, target: { kind: 'bridge' as const,
            recovery: { ...bridgeRecovery, expectedApplication: application } } };
          fixtures.push(refusalFixture(`sc13-bridge-error-recovery-invalid-${alteration}`, 'SC-13',
            'Standalone errors must validate nested recovery consistency', 'reply',
            rejectedReply(id(), { code: 'recovery_required', retryDisposition: 'after_recovery', violations: [], recovery: malformed })));
          fixtures.push(refusalFixture(`sc13-bridge-close-recovery-invalid-${alteration}`, 'SC-13',
            'Host close obligations must validate nested recovery consistency', 'reply',
            commandReply(id(), { name: 'request_host_close', output: { kind: 'recovery_required', recoveries: [malformed] } })));
        }
      }
    }
  }
  const bridgeRecoveryInput = { recovery: bridgeRecovery };
  const bridgeRecoveryPlan = prepared(hooks, { hashProfile: 'bridge-plan-semantic-json-v1', action: 'recover_bridge_update', capture: { kind: 'recover_bridge_update', input: bridgeRecoveryInput }, trustDomain: 'bridge_application', effects: ['recover_bridge'] }, sequence++);
  preparePair('sc13-recover-bridge-update', 'SC-13', commandRequest(id(), { name: 'prepare', input: { intent: { kind: 'recover_bridge_update', input: bridgeRecoveryInput } } }), bridgeRecoveryPlan);
  resultPair('sc13-bridge-prior-pair-restored', 'SC-13', operation(bridgeRecoveryPlan, sequence++, '3', { status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt: { kind: 'bridge_updated', application: bridgeApplication } } }));
  const channelMismatch = clone(bridgeInput); channelMismatch.selectedRelease.offered.channelId = 'development';
  fixtures.push(refusalFixture('sc13-bridge-channel-crossing', 'SC-13', 'Selected Bridge channel cannot silently change during update', 'request', commandRequest(id(), { name: 'prepare', input: { intent: { kind: 'bridge_update', input: channelMismatch } } })));
  for (const code of ['pairing_mismatch', 'verification_failed', 'release_withdrawn', 'persistence_failed'] as const) {
    requestId = id(); pair(`sc13-bridge-${code.replaceAll('_', '-')}`, 'SC-13', `Bridge refusal ${code} remains explicit`, commandRequest(requestId, { name: 'prepare', input: { intent: { kind: 'bridge_update', input: bridgeInput } } }), rejectedReply(requestId, { code, retryDisposition: 'after_user_choice', violations: [] }));
  }
  return { fixtures, transcripts };
}
