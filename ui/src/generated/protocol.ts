/* Generated from Rust DTOs by scripts/next/generate-protocol.mjs. Do not edit. */

/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "EventBody".
 */
export type EventBody =
  | {
      reason: SnapshotInvalidationReason;
      type: 'snapshot_invalidated';
    }
  | {
      operation: OperationSnapshot;
      type: 'operation_changed';
    }
  | {
      obligations: BoundedList_CloseObligation_128;
      type: 'host_close_deferred';
    }
  | {
      draft: DraftSnapshot;
      type: 'draft_changed';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SnapshotInvalidationReason".
 */
export type SnapshotInvalidationReason =
  'catalog_changed' | 'session_changed' | 'operation_changed' | 'host_restarted' | 'retention_gap';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "OperationId".
 */
export type OperationId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RevisionCounter".
 */
export type RevisionCounter = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ActionId".
 */
export type ActionId =
  | 'launch_ordinary'
  | 'launch_isolated'
  | 'focus_session'
  | 'create_profile'
  | 'edit_ordinary_profile'
  | 'edit_isolated_profile'
  | 'archive_profile'
  | 'restore_profile'
  | 'delete_profile'
  | 'register_installation'
  | 'edit_installation'
  | 'save_configuration'
  | 'restore_configuration'
  | 'runtime_install'
  | 'runtime_update'
  | 'runtime_repair'
  | 'runtime_adopt'
  | 'runtime_remove'
  | 'runtime_stop_managing'
  | 'runtime_switch_source'
  | 'game_update'
  | 'recover_game_update'
  | 'bridge_update'
  | 'recover_bridge_update'
  | 'export_diagnostics'
  | 'save_application_preferences';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PreparedCapture".
 */
export type PreparedCapture =
  | {
      catalogRevision: OpaqueRevision;
      kind: 'launch_ordinary';
      runtime: RuntimeExpectation;
      target: ResolvedTarget;
      unrecognizedRuntimeChoice?: UnrecognizedRuntimeChoice;
    }
  | {
      catalogRevision: OpaqueRevision;
      kind: 'launch_isolated';
      runtime: RuntimeExpectation;
      storeMode: StoreMode;
      target: ResolvedTarget;
      unrecognizedRuntimeChoice?: UnrecognizedRuntimeChoice;
    }
  | {
      kind: 'focus_session';
      session: SessionBinding;
      target: ResolvedTarget;
    }
  | {
      input: CreateProfileCapture;
      kind: 'create_profile';
    }
  | {
      input: EditOrdinaryProfileInput;
      kind: 'edit_ordinary_profile';
    }
  | {
      input: EditIsolatedProfileInput;
      kind: 'edit_isolated_profile';
    }
  | {
      input: ProfileLifecycleInput;
      kind: 'archive_profile';
    }
  | {
      input: ProfileLifecycleInput;
      kind: 'restore_profile';
    }
  | {
      input: DeleteProfileInput;
      kind: 'delete_profile';
    }
  | {
      input: RegisterInstallationCapture;
      kind: 'register_installation';
    }
  | {
      input: EditInstallationInput;
      kind: 'edit_installation';
    }
  | {
      input: SaveConfigurationCapture;
      kind: 'save_configuration';
    }
  | {
      input: RestoreConfigurationInput;
      kind: 'restore_configuration';
    }
  | {
      input: RuntimeDeployInput;
      kind: 'runtime_install';
      preparedConfiguration: PreparedConfigurationEffect;
    }
  | {
      input: RuntimeDeployInput;
      kind: 'runtime_update';
      preparedConfiguration: PreparedConfigurationEffect;
    }
  | {
      input: RuntimeDeployInput;
      kind: 'runtime_repair';
      preparedConfiguration: PreparedConfigurationEffect;
    }
  | {
      input: RuntimeAdoptInput;
      kind: 'runtime_adopt';
    }
  | {
      input: ManagedRuntimeInput;
      kind: 'runtime_remove';
    }
  | {
      input: ManagedRuntimeInput;
      kind: 'runtime_stop_managing';
    }
  | {
      input: RuntimeSwitchSourceInput;
      kind: 'runtime_switch_source';
      preparedConfiguration: PreparedConfigurationEffect;
    }
  | {
      input: GameUpdateInput;
      kind: 'game_update';
    }
  | {
      input: RecoverGameUpdateInput;
      kind: 'recover_game_update';
    }
  | {
      input: BridgeUpdateInput;
      kind: 'bridge_update';
    }
  | {
      input: RecoverBridgeUpdateInput;
      kind: 'recover_bridge_update';
    }
  | {
      input: ExportDiagnosticsInput;
      kind: 'export_diagnostics';
    }
  | {
      input: SaveApplicationPreferencesInput;
      kind: 'save_application_preferences';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "OpaqueRevision".
 */
export type OpaqueRevision = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RuntimeExpectation".
 */
export type RuntimeExpectation =
  | {
      kind: 'absent';
    }
  | {
      binding: RuntimeBinding;
      kind: 'verified';
    }
  | {
      artifactDigest: Sha256;
      choice: UnrecognizedRuntimeChoice;
      kind: 'unrecognized';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProcessArchitecture".
 */
export type ProcessArchitecture = 'x86_64' | 'arm64';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Sha256".
 */
export type Sha256 = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DistributionId".
 */
export type DistributionId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RuntimeManifestObservation".
 */
export type RuntimeManifestObservation =
  | {
      digest: Sha256;
      status: 'observed';
    }
  | {
      status: 'missing';
    }
  | {
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ObservationReason".
 */
export type ObservationReason =
  | 'access_denied'
  | 'incomplete_inventory'
  | 'native_unavailable'
  | 'unrecognized_identity'
  | 'conflicting_evidence'
  | 'unsupported_platform';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SupportedPlatform".
 */
export type SupportedPlatform = 'windows' | 'macos';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProviderId".
 */
export type ProviderId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "UnrecognizedRuntimeChoice".
 */
export type UnrecognizedRuntimeChoice = 'reject' | 'allow_once';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "InstallationBinding".
 */
export type InstallationBinding =
  | {
      kind: 'registered';
      nativeTargetRef: NativeTargetRef;
      physicalId: PhysicalInstallationId;
      registrationId: InstallationId;
      registrationRevision: OpaqueRevision;
    }
  | {
      kind: 'directory';
      nativeTargetRef: NativeTargetRef;
      physicalId: PhysicalInstallationId;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "NativeTargetRef".
 */
export type NativeTargetRef = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PhysicalInstallationId".
 */
export type PhysicalInstallationId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "InstallationId".
 */
export type InstallationId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProfileBinding".
 */
export type ProfileBinding =
  | {
      kind: 'ordinary';
      ordinaryId?: ProfileId | null;
      ownerScope: OwnerScope;
    }
  | {
      id: ProfileId;
      kind: 'isolated';
      revision: OpaqueRevision;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProfileId".
 */
export type ProfileId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "OwnerScope".
 */
export type OwnerScope = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "StoreMode".
 */
export type StoreMode = 'new' | 'resume' | 'existing';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ExecutableIdentity".
 */
export type ExecutableIdentity = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Pid".
 */
export type Pid = number;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProcessStartIdentity".
 */
export type ProcessStartIdentity =
  | {
      platform: 'windows';
      value: ProcessGeneration;
    }
  | {
      platform: 'macos';
      value: ProcessGeneration;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProcessGeneration".
 */
export type ProcessGeneration = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SessionId".
 */
export type SessionId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DisplayName".
 */
export type DisplayName = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "NativePreparationRef".
 */
export type NativePreparationRef = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RegisteredInstallationBinding".
 */
export type RegisteredInstallationBinding = {
  kind: 'registered';
  nativeTargetRef: NativeTargetRef;
  physicalId: PhysicalInstallationId;
  registrationId: InstallationId;
  registrationRevision: OpaqueRevision;
};
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProfileSetup".
 */
export type ProfileSetup =
  | {
      kind: 'new';
    }
  | {
      approval: NativeApprovalChoice;
      kind: 'windows_user_import';
      source: ImportSourceRef;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "NativeApprovalChoice".
 */
export type NativeApprovalChoice = 'decline' | 'request_native_approval';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ImportSourceId".
 */
export type ImportSourceId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PreferredInstallationEdit".
 */
export type PreferredInstallationEdit =
  | {
      expected: SavedInstallationPreference;
      kind: 'keep';
    }
  | {
      kind: 'clear';
    }
  | {
      installation: RegisteredInstallationBinding;
      kind: 'set';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SavedInstallationPreference".
 */
export type SavedInstallationPreference =
  | {
      kind: 'none';
    }
  | {
      id: InstallationId;
      kind: 'registered';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "OrdinaryProfileRef".
 */
export type OrdinaryProfileRef = {
  catalogId: ProfileId;
  kind: 'ordinary';
  ownerScope: OwnerScope;
  revision: OpaqueRevision;
};
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProfileState".
 */
export type ProfileState = 'active' | 'archived';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DeletionConfirmation".
 */
export type DeletionConfirmation = 'delete_entire_owned_profile';
/**
 * @maxItems 3
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_ApplyTiming_3".
 */
export type BoundedList_ApplyTiming_3 =
  [] | [ApplyTiming] | [ApplyTiming, ApplyTiming] | [ApplyTiming, ApplyTiming, ApplyTiming];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ApplyTiming".
 */
export type ApplyTiming = 'immediate' | 'next_launch' | 'restart_required';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DocumentBaseline".
 */
export type DocumentBaseline =
  | {
      kind: 'missing';
    }
  | {
      contentDigest: Sha256;
      fileIdentity: NativeFileIdentity;
      kind: 'existing';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "NativeFileIdentity".
 */
export type NativeFileIdentity = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DocumentId".
 */
export type DocumentId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SchemaId".
 */
export type SchemaId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SchemaVersion".
 */
export type SchemaVersion = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DraftId".
 */
export type DraftId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "HostEpoch".
 */
export type HostEpoch = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ConfigurationEdit".
 */
export type ConfigurationEdit =
  | {
      fieldId: FieldId;
      kind: 'set_public';
      value: PublicConfigValue;
    }
  | {
      fieldId: FieldId;
      kind: 'set_private';
      reference: PrivateValueRef;
    }
  | {
      fieldId: FieldId;
      kind: 'replace_secret';
      reference: SecretRef;
    }
  | {
      fieldId: FieldId;
      kind: 'clear_secret';
    }
  | {
      fieldId: FieldId;
      kind: 'remove_override';
    }
  | {
      destination: NewSyncDestination;
      kind: 'add_sync_destination';
    }
  | {
      destinationId: DestinationId;
      kind: 'remove_sync_destination';
    }
  | {
      destinationId: DestinationId;
      feedId: FeedId;
      kind: 'set_sync_feed';
      value: InheritedBoolean;
    }
  | {
      destinationId: DestinationId;
      kind: 'set_sync_proxy';
      value: ProxyChoice;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "FieldId".
 */
export type FieldId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PublicConfigValue".
 */
export type PublicConfigValue =
  | {
      kind: 'boolean';
      value: boolean;
    }
  | {
      kind: 'integer';
      value: SignedInteger;
    }
  | {
      kind: 'number';
      value: DecimalValue;
    }
  | {
      kind: 'string';
      value: ConfigString;
    }
  | {
      kind: 'enum';
      value: EnumValue;
    }
  | {
      kind: 'keybinding';
      value: BoundedList_KeyChord_8;
    }
  | {
      kind: 'notification_policy';
      value: NotificationPolicy;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SignedInteger".
 */
export type SignedInteger = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DecimalValue".
 */
export type DecimalValue = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ConfigString".
 */
export type ConfigString = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "EnumValue".
 */
export type EnumValue = string;
/**
 * @maxItems 8
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_KeyChord_8".
 */
export type BoundedList_KeyChord_8 =
  | []
  | [KeyChord]
  | [KeyChord, KeyChord]
  | [KeyChord, KeyChord, KeyChord]
  | [KeyChord, KeyChord, KeyChord, KeyChord]
  | [KeyChord, KeyChord, KeyChord, KeyChord, KeyChord]
  | [KeyChord, KeyChord, KeyChord, KeyChord, KeyChord, KeyChord]
  | [KeyChord, KeyChord, KeyChord, KeyChord, KeyChord, KeyChord, KeyChord]
  | [KeyChord, KeyChord, KeyChord, KeyChord, KeyChord, KeyChord, KeyChord, KeyChord];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "KeyToken".
 */
export type KeyToken = string;
/**
 * @maxItems 4
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_Modifier_4".
 */
export type BoundedList_Modifier_4 =
  | []
  | [Modifier]
  | [Modifier, Modifier]
  | [Modifier, Modifier, Modifier]
  | [Modifier, Modifier, Modifier, Modifier];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Modifier".
 */
export type Modifier = 'control' | 'alt' | 'shift' | 'meta';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "NotificationPolicy".
 */
export type NotificationPolicy =
  | {
      kind: 'disabled';
    }
  | {
      kind: 'system_only';
    }
  | {
      audio: boolean;
      kind: 'channels';
      sound: EnumValue;
      system: boolean;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PrivateValueId".
 */
export type PrivateValueId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SecretRefId".
 */
export type SecretRefId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "InheritedBoolean".
 */
export type InheritedBoolean = 'inherit' | 'on' | 'off';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "FeedId".
 */
export type FeedId = string;
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_SyncFeedOverride_128".
 */
export type BoundedList_SyncFeedOverride_128 = SyncFeedOverride[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DestinationId".
 */
export type DestinationId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SyncMode".
 */
export type SyncMode = 'legacy' | 'sidecar' | 'majel';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProxyChoice".
 */
export type ProxyChoice =
  | {
      kind: 'global';
    }
  | {
      kind: 'none';
    }
  | {
      kind: 'custom';
      reference: PrivateValueRef;
    };
/**
 * @maxItems 256
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_ConfigurationEdit_256".
 */
export type BoundedList_ConfigurationEdit_256 = ConfigurationEdit[];
/**
 * @maxItems 16
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_BoundedList_TomlPathSegment_16_16".
 */
export type BoundedList_BoundedList_TomlPathSegment_16_16 =
  | []
  | [BoundedList_TomlPathSegment_16]
  | [BoundedList_TomlPathSegment_16, BoundedList_TomlPathSegment_16]
  | [BoundedList_TomlPathSegment_16, BoundedList_TomlPathSegment_16, BoundedList_TomlPathSegment_16]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ]
  | [
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16,
      BoundedList_TomlPathSegment_16
    ];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "TomlPathSegment".
 */
export type TomlPathSegment = string;
/**
 * @maxItems 16
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_TomlPathSegment_16".
 */
export type BoundedList_TomlPathSegment_16 = TomlPathSegment[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CategoryId".
 */
export type CategoryId = string;
/**
 * @maxItems 2
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_SupportedPlatform_2".
 */
export type BoundedList_SupportedPlatform_2 =
  [] | [SupportedPlatform] | [SupportedPlatform, SupportedPlatform];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SchemaText".
 */
export type SchemaText = string;
/**
 * @maxItems 32
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_SchemaText_32".
 */
export type BoundedList_SchemaText_32 = SchemaText[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Sensitivity".
 */
export type Sensitivity = 'public' | 'private' | 'secret';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "FieldType".
 */
export type FieldType =
  | {
      kind: 'boolean';
    }
  | {
      kind: 'integer';
      maximum?: SignedInteger | null;
      minimum?: SignedInteger | null;
    }
  | {
      kind: 'number';
      maximum?: DecimalValue | null;
      minimum?: DecimalValue | null;
    }
  | {
      kind: 'string';
      maximumLength: ProgressCount;
    }
  | {
      kind: 'enum';
      values: BoundedList_EnumValue_128;
    }
  | {
      keys: BoundedList_KeyToken_128;
      kind: 'keybinding';
      multiple: boolean;
    }
  | {
      kind: 'notification_policy';
      sounds: BoundedList_EnumValue_128;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProgressCount".
 */
export type ProgressCount = string;
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_EnumValue_128".
 */
export type BoundedList_EnumValue_128 = EnumValue[];
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_KeyToken_128".
 */
export type BoundedList_KeyToken_128 = KeyToken[];
/**
 * @maxItems 512
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_FieldDefinition_512".
 */
export type BoundedList_FieldDefinition_512 = FieldDefinition[];
/**
 * @maxItems 16
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_SyncTypeDefinition_16".
 */
export type BoundedList_SyncTypeDefinition_16 =
  | []
  | [SyncTypeDefinition]
  | [SyncTypeDefinition, SyncTypeDefinition]
  | [SyncTypeDefinition, SyncTypeDefinition, SyncTypeDefinition]
  | [SyncTypeDefinition, SyncTypeDefinition, SyncTypeDefinition, SyncTypeDefinition]
  | [
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition
    ]
  | [
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition
    ]
  | [
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition
    ]
  | [
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition
    ]
  | [
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition
    ]
  | [
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition
    ]
  | [
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition
    ]
  | [
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition
    ]
  | [
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition
    ]
  | [
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition
    ]
  | [
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition
    ]
  | [
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition,
      SyncTypeDefinition
    ];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SyncExposure".
 */
export type SyncExposure = 'creatable' | 'existing_configuration_only' | 'hidden';
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_FeedId_128".
 */
export type BoundedList_FeedId_128 = FeedId[];
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_FieldId_128".
 */
export type BoundedList_FieldId_128 = FieldId[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DraftState".
 */
export type DraftState = 'clean' | 'dirty' | 'invalid' | 'stale';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DraftViolationCode".
 */
export type DraftViolationCode =
  | 'unknown_field'
  | 'invalid_type'
  | 'constraint_violation'
  | 'private_value_required'
  | 'secret_reference_required'
  | 'unsupported_sync_field'
  | 'stale_schema';
/**
 * @maxItems 64
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_DraftViolation_64".
 */
export type BoundedList_DraftViolation_64 = DraftViolation[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BackupId".
 */
export type BackupId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "UtcTimestamp".
 */
export type UtcTimestamp = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ConfigurationParticipant".
 */
export type ConfigurationParticipant =
  | {
      document: DocumentBinding;
      kind: 'unchanged';
    }
  | {
      draft: DraftRef;
      kind: 'save_reviewed_draft';
    }
  | {
      destination: SchemaBinding;
      document: DocumentBinding;
      kind: 'compatible_migration';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RuntimeOwnership".
 */
export type RuntimeOwnership =
  | {
      kind: 'absent';
    }
  | {
      kind: 'managed';
      reference: ManagedRuntimeRef;
    }
  | {
      artifactDigest: Sha256;
      kind: 'unmanaged';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RuntimeReceiptId".
 */
export type RuntimeReceiptId = string;
/**
 * @maxItems 16
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_RuntimeArtifactSubject_16".
 */
export type BoundedList_RuntimeArtifactSubject_16 =
  | []
  | [RuntimeArtifactSubject]
  | [RuntimeArtifactSubject, RuntimeArtifactSubject]
  | [RuntimeArtifactSubject, RuntimeArtifactSubject, RuntimeArtifactSubject]
  | [RuntimeArtifactSubject, RuntimeArtifactSubject, RuntimeArtifactSubject, RuntimeArtifactSubject]
  | [
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject
    ]
  | [
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject
    ]
  | [
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject
    ]
  | [
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject
    ]
  | [
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject
    ]
  | [
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject
    ]
  | [
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject
    ]
  | [
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject
    ]
  | [
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject
    ]
  | [
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject
    ]
  | [
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject
    ]
  | [
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject,
      RuntimeArtifactSubject
    ];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ArtifactRole".
 */
export type ArtifactRole =
  'runtime_module' | 'runtime_loader' | 'runtime_manifest' | 'configuration_schema';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ArtifactAuthorityRef".
 */
export type ArtifactAuthorityRef = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ChannelId".
 */
export type ChannelId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ReleaseVersion".
 */
export type ReleaseVersion = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RuntimeReleaseId".
 */
export type RuntimeReleaseId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PreparedConfigurationEffect".
 */
export type PreparedConfigurationEffect =
  | {
      document: DocumentBinding;
      kind: 'unchanged';
    }
  | {
      baseline: DocumentBinding;
      candidateDigest: Sha256;
      destinationSchema: SchemaBinding;
      kind: 'write';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RuntimeAdoptionConfirmation".
 */
export type RuntimeAdoptionConfirmation = 'adopt_exact_recognized_artifact';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SourceSwitchConfirmation".
 */
export type SourceSwitchConfirmation = 'switch_runtime_and_configuration_source';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "GameUpdateId".
 */
export type GameUpdateId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "GameUpdateRoute".
 */
export type GameUpdateRoute = 'canonical_native_direct' | 'canonical_managed_handoff';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "NativeTransactionRef".
 */
export type NativeTransactionRef = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BridgeAuthorityRef".
 */
export type BridgeAuthorityRef = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ApplicationIdentity".
 */
export type ApplicationIdentity = string;
/**
 * @maxItems 8
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_BridgePayloadSubject_8".
 */
export type BoundedList_BridgePayloadSubject_8 =
  | []
  | [BridgePayloadSubject]
  | [BridgePayloadSubject, BridgePayloadSubject]
  | [BridgePayloadSubject, BridgePayloadSubject, BridgePayloadSubject]
  | [BridgePayloadSubject, BridgePayloadSubject, BridgePayloadSubject, BridgePayloadSubject]
  | [
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject
    ]
  | [
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject
    ]
  | [
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject
    ]
  | [
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject,
      BridgePayloadSubject
    ];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BridgePayloadRole".
 */
export type BridgePayloadRole = 'application' | 'profiles_native' | 'toml_native' | 'update_helper';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BridgeReleaseId".
 */
export type BridgeReleaseId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ExportDestinationId".
 */
export type ExportDestinationId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DiagnosticDisclosure".
 */
export type DiagnosticDisclosure = 'redacted' | 'include_paths';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PreviewId".
 */
export type PreviewId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProfileSelector".
 */
export type ProfileSelector =
  | {
      catalogIdAssertion?: ProfileId | null;
      kind: 'ordinary';
    }
  | {
      id: ProfileId;
      kind: 'isolated';
      revisionAssertion?: OpaqueRevision | null;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "MotionPreference".
 */
export type MotionPreference = 'system' | 'reduced';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ThemePreference".
 */
export type ThemePreference = 'system' | 'light' | 'dark';
/**
 * @maxItems 16
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_ProposedEffect_16".
 */
export type BoundedList_ProposedEffect_16 =
  | []
  | [ProposedEffect]
  | [ProposedEffect, ProposedEffect]
  | [ProposedEffect, ProposedEffect, ProposedEffect]
  | [ProposedEffect, ProposedEffect, ProposedEffect, ProposedEffect]
  | [ProposedEffect, ProposedEffect, ProposedEffect, ProposedEffect, ProposedEffect]
  | [ProposedEffect, ProposedEffect, ProposedEffect, ProposedEffect, ProposedEffect, ProposedEffect]
  | [
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect
    ]
  | [
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect
    ]
  | [
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect
    ]
  | [
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect
    ]
  | [
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect
    ]
  | [
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect
    ]
  | [
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect
    ]
  | [
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect
    ]
  | [
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect
    ]
  | [
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect,
      ProposedEffect
    ];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProposedEffect".
 */
export type ProposedEffect =
  | 'launch_session'
  | 'create_isolated_store'
  | 'focus_session'
  | 'publish_profile'
  | 'edit_profile_metadata'
  | 'archive_profile'
  | 'restore_profile'
  | 'delete_owned_profile'
  | 'register_installation'
  | 'edit_installation_metadata'
  | 'write_configuration'
  | 'replace_runtime'
  | 'adopt_runtime'
  | 'remove_managed_runtime'
  | 'release_runtime_management'
  | 'update_game'
  | 'recover_game'
  | 'replace_bridge'
  | 'recover_bridge'
  | 'export_diagnostics'
  | 'save_application_preferences';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "HashProfile".
 */
export type HashProfile = 'bridge-plan-semantic-json-v1';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "TrustDomain".
 */
export type TrustDomain =
  | 'session'
  | 'profile_state'
  | 'configuration'
  | 'runtime_distribution'
  | 'game_client'
  | 'bridge_application'
  | 'application_state'
  | 'diagnostics';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "OperationState".
 */
export type OperationState =
  | {
      status: 'admitted';
    }
  | {
      progress: Progress;
      status: 'running';
    }
  | {
      progress: Progress;
      status: 'cancellation_requested';
    }
  | {
      outcome: CompletionOutcome;
      status: 'completed';
    }
  | {
      reason: RecoveryReason;
      recovery: RecoveryRef;
      status: 'recovery_required';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Measurement".
 */
export type Measurement =
  | {
      unit: 'unknown';
    }
  | {
      completed: ProgressCount;
      total?: ProgressCount | null;
      unit: 'bytes';
    }
  | {
      completed: ProgressCount;
      total?: ProgressCount | null;
      unit: 'files';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PhaseId".
 */
export type PhaseId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CompletionOutcome".
 */
export type CompletionOutcome =
  | {
      kind: 'changed';
      reason: CompletionReason;
      receipt?: EffectReceipt | null;
    }
  | {
      kind: 'no_change';
      reason: CompletionReason;
    }
  | {
      kind: 'cancelled_before_commit';
      reason: CompletionReason;
    }
  | {
      kind: 'rolled_back';
      reason: CompletionReason;
    }
  | {
      error: BridgeError;
      kind: 'failed';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CompletionReason".
 */
export type CompletionReason =
  'applied' | 'already_satisfied' | 'cancellation_accepted' | 'rollback_completed';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "EffectReceipt".
 */
export type EffectReceipt =
  | {
      kind: 'session_spawned';
      session: SessionProjection;
    }
  | {
      kind: 'session_focused';
      session: SessionBinding;
    }
  | {
      kind: 'profile_published';
      profile: ProfileProjection;
    }
  | {
      kind: 'profile_edited';
      profile: ProfileProjection;
    }
  | {
      kind: 'profile_archived';
      profile: IsolatedProfileRef;
    }
  | {
      kind: 'profile_restored';
      profile: IsolatedProfileRef;
    }
  | {
      kind: 'profile_deleted';
      profileId: ProfileId;
    }
  | {
      installation: InstallationBinding;
      kind: 'installation_registered';
      name: DisplayName;
    }
  | {
      installation: InstallationBinding;
      kind: 'installation_edited';
      name: DisplayName;
    }
  | {
      backup?: BackupReceiptRef | null;
      document: DocumentBinding;
      kind: 'configuration_written';
    }
  | {
      configuration: ConfigurationParticipantOutcome;
      kind: 'runtime_managed';
      reference: ManagedRuntimeRef;
    }
  | {
      kind: 'runtime_adopted';
      reference: ManagedRuntimeRef;
    }
  | {
      kind: 'runtime_removed';
      target: ResolvedTarget;
    }
  | {
      kind: 'runtime_unmanaged';
      target: ResolvedTarget;
    }
  | {
      client: GameClientBinding;
      installation: InstallationBinding;
      kind: 'game_updated';
    }
  | {
      application: BridgeApplicationBinding;
      kind: 'bridge_updated';
    }
  | {
      destination: ExportDestinationRef;
      kind: 'diagnostics_exported';
      previewDigest: Sha256;
    }
  | {
      kind: 'application_preferences_saved';
      snapshot: ApplicationPreferencesSnapshot;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation3".
 */
export type Observation3 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: boolean;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ObservationId".
 */
export type ObservationId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "EvidenceSource".
 */
export type EvidenceSource =
  | 'native_live'
  | 'catalog_metadata'
  | 'session_receipt'
  | 'disk_file_hash'
  | 'artifact_self_description'
  | 'verified_release';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation4".
 */
export type Observation4 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: SessionReadiness;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SessionReadiness".
 */
export type SessionReadiness =
  'ordinary_spawned' | 'isolated_initializing' | 'isolated_ready' | 'isolation_failed';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation".
 */
export type Observation =
  | {
      evidence: Evidence;
      status: 'observed';
      value: ResolvedTarget;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProfileProjection".
 */
export type ProfileProjection =
  | {
      kind: 'ordinary';
      preferredInstallation?: InstallationId | null;
      reference: OrdinaryProfileRef;
    }
  | {
      kind: 'isolated';
      name: DisplayName;
      preferredInstallation?: InstallationId | null;
      reference: IsolatedProfileRef;
      store: Observation5;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation5".
 */
export type Observation5 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: IsolatedStoreState;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "IsolatedStoreState".
 */
export type IsolatedStoreState = 'new' | 'established' | 'interrupted' | 'missing_established';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ConfigurationParticipantOutcome".
 */
export type ConfigurationParticipantOutcome =
  | {
      document: DocumentBinding;
      kind: 'unchanged';
    }
  | {
      backup?: BackupReceiptRef | null;
      document: DocumentBinding;
      kind: 'written';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ErrorCode".
 */
export type ErrorCode =
  | 'invalid_request'
  | 'unsupported_protocol'
  | 'unknown_command'
  | 'invalid_target'
  | 'conflicting_target'
  | 'target_missing'
  | 'target_unknown'
  | 'stale_revision'
  | 'profile_archived'
  | 'wrong_owner'
  | 'unsupported_capability'
  | 'operation_busy'
  | 'plan_expired'
  | 'plan_host_mismatch'
  | 'idempotency_conflict'
  | 'native_unavailable'
  | 'recovery_required'
  | 'export_preview_changed'
  | 'internal_failure'
  | 'invalid_configuration'
  | 'unsupported_schema'
  | 'unsupported_preservation_syntax'
  | 'backup_unavailable'
  | 'persistence_failed'
  | 'artifact_unrecognized'
  | 'verification_failed'
  | 'release_withdrawn'
  | 'pairing_mismatch'
  | 'unsupported_platform'
  | 'resnapshot_required';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RevisionScope".
 */
export type RevisionScope =
  | {
      id: ProfileId;
      kind: 'profile';
    }
  | {
      kind: 'installation';
      physicalId: PhysicalInstallationId;
    }
  | {
      id: DocumentId;
      kind: 'document';
      schemaDigest: Sha256;
      target: ResolvedTarget;
    }
  | {
      id: OperationId;
      kind: 'operation';
    }
  | {
      id: ApplicationIdentity;
      kind: 'application';
    }
  | {
      kind: 'catalog';
      owner: OwnerScope;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RecoveryTarget".
 */
export type RecoveryTarget =
  | {
      kind: 'launch';
      target: ResolvedTarget;
    }
  | {
      kind: 'session';
      session: SessionBinding;
      target: ResolvedTarget;
    }
  | {
      kind: 'profile';
      profile: IsolatedProfileRef;
    }
  | {
      kind: 'ordinary_profile';
      profile: OrdinaryProfileRef;
    }
  | {
      kind: 'profile_creation';
      nativePreparationRef: NativePreparationRef;
      owner: OwnerScope;
    }
  | {
      installation: InstallationBinding;
      kind: 'installation';
    }
  | {
      kind: 'installation_registration';
      nativeTargetRef: NativeTargetRef;
      physicalId: PhysicalInstallationId;
    }
  | {
      document: DocumentBinding;
      kind: 'configuration';
    }
  | {
      kind: 'runtime';
      target: ResolvedTarget;
    }
  | {
      kind: 'game';
      recovery: NativeGameRecoveryRef;
    }
  | {
      kind: 'bridge';
      recovery: BridgeRecoveryRef;
    }
  | {
      destination: ExportDestinationRef;
      kind: 'diagnostics';
      preview: PreviewRef;
    }
  | {
      kind: 'application_preferences';
      revision: OpaqueRevision;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RetryDisposition".
 */
export type RetryDisposition =
  'never' | 'after_resnapshot' | 'after_user_choice' | 'after_recovery';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProtocolVersion".
 */
export type ProtocolVersion = 1;
/**
 * @maxItems 8
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_FieldViolation_8".
 */
export type BoundedList_FieldViolation_8 =
  | []
  | [FieldViolation]
  | [FieldViolation, FieldViolation]
  | [FieldViolation, FieldViolation, FieldViolation]
  | [FieldViolation, FieldViolation, FieldViolation, FieldViolation]
  | [FieldViolation, FieldViolation, FieldViolation, FieldViolation, FieldViolation]
  | [FieldViolation, FieldViolation, FieldViolation, FieldViolation, FieldViolation, FieldViolation]
  | [
      FieldViolation,
      FieldViolation,
      FieldViolation,
      FieldViolation,
      FieldViolation,
      FieldViolation,
      FieldViolation
    ]
  | [
      FieldViolation,
      FieldViolation,
      FieldViolation,
      FieldViolation,
      FieldViolation,
      FieldViolation,
      FieldViolation,
      FieldViolation
    ];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ViolationCode".
 */
export type ViolationCode =
  'invalid_framing' | 'invalid_shape' | 'invalid_value' | 'conflicting_binding';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "FieldPath".
 */
export type FieldPath =
  | 'envelope'
  | 'protocol_version'
  | 'request_id'
  | 'body'
  | 'target'
  | 'session'
  | 'plan'
  | 'operation'
  | 'cursor';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RecoveryReason".
 */
export type RecoveryReason =
  'interrupted_transaction' | 'rollback_incomplete' | 'native_custody_unresolved';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CloseObligation".
 */
export type CloseObligation =
  | {
      kind: 'operation';
      operationId: OperationId;
      operationRevision: RevisionCounter;
    }
  | {
      kind: 'session_custody';
      session: SessionBinding;
    };
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_CloseObligation_128".
 */
export type BoundedList_CloseObligation_128 = CloseObligation[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Sequence".
 */
export type Sequence = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "StreamId".
 */
export type StreamId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ReplyBody".
 */
export type ReplyBody =
  | {
      result: ResultPayload;
      type: 'result';
    }
  | {
      error: BridgeError;
      type: 'rejected';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ResultPayload".
 */
export type ResultPayload =
  | {
      query: QueryResult;
      type: 'query';
    }
  | {
      command: CommandResult;
      type: 'command';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "QueryResult".
 */
export type QueryResult =
  | {
      name: 'hello';
      output: HelloResult;
    }
  | {
      name: 'resolve_target';
      output: ResolveTargetResult;
    }
  | {
      name: 'get_operation';
      output: GetOperationResult;
    }
  | {
      name: 'snapshot';
      output: Snapshot;
    }
  | {
      name: 'list_profiles';
      output: Observation7;
    }
  | {
      name: 'list_installations';
      output: Observation8;
    }
  | {
      name: 'list_sessions';
      output: Observation11;
    }
  | {
      name: 'list_import_sources';
      output: Observation12;
    }
  | {
      name: 'get_actions';
      output: BoundedList_ActionProjection_64;
    }
  | {
      name: 'read_configuration';
      output: Observation13;
    }
  | {
      name: 'configuration_history';
      output: Observation15;
    }
  | {
      name: 'check_runtime_release';
      output: Observation16;
    }
  | {
      name: 'check_game_update';
      output: Observation17;
    }
  | {
      name: 'check_bridge_update';
      output: Observation18;
    }
  | {
      name: 'resume_events';
      output: EventBatch;
    }
  | {
      name: 'diagnostic_preview';
      output: DiagnosticPreview;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "HostKind".
 */
export type HostKind = 'windows_x64' | 'macos_arm64';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CommandId".
 */
export type CommandId =
  | 'prepare'
  | 'commit'
  | 'cancel_operation'
  | 'request_host_close'
  | 'open_draft'
  | 'set_draft_changes'
  | 'discard_draft'
  | 'request_import_discovery'
  | 'request_sensitive_input'
  | 'request_export_destination';
/**
 * @maxItems 32
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_CommandId_32".
 */
export type BoundedList_CommandId_32 = CommandId[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation2".
 */
export type Observation2 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: OperationSnapshot;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Completeness".
 */
export type Completeness = 'complete' | 'partial';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProjectionIssueCode".
 */
export type ProjectionIssueCode =
  | 'access_denied'
  | 'missing_catalog'
  | 'incomplete_catalog'
  | 'invalid_metadata'
  | 'conflicting_identity'
  | 'stale_receipt'
  | 'native_unavailable'
  | 'unsupported_platform';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ResourceRef".
 */
export type ResourceRef =
  | {
      id: ProfileId;
      kind: 'profile';
    }
  | {
      id: InstallationId;
      kind: 'installation';
    }
  | {
      id: SessionId;
      kind: 'session';
    }
  | {
      id: DocumentId;
      kind: 'document';
    }
  | {
      id: OperationId;
      kind: 'operation';
    }
  | {
      identity: ApplicationIdentity;
      kind: 'application';
    };
/**
 * @maxItems 64
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_ProjectionIssue_64".
 */
export type BoundedList_ProjectionIssue_64 = ProjectionIssue[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CapabilityId".
 */
export type CapabilityId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CapabilityStatus".
 */
export type CapabilityStatus =
  | {
      evidence: Evidence;
      status: 'supported';
    }
  | {
      reason: AvailabilityReason;
      status: 'unsupported';
    }
  | {
      reason: AvailabilityReason;
      status: 'unknown';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "AvailabilityReasonCode".
 */
export type AvailabilityReasonCode =
  | 'missing_target'
  | 'unknown_identity'
  | 'wrong_profile_kind'
  | 'archived_profile'
  | 'wrong_owner'
  | 'active_session'
  | 'busy'
  | 'stale_revision'
  | 'unrecognized_runtime'
  | 'unsupported_schema'
  | 'unavailable_native_route'
  | 'unqualified_isolation'
  | 'offline'
  | 'interrupted_transaction'
  | 'dirty_draft'
  | 'unsupported_platform';
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_CapabilityProjection_128".
 */
export type BoundedList_CapabilityProjection_128 = CapabilityProjection[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation8".
 */
export type Observation8 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: Inventory2;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation9".
 */
export type Observation9 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: GameClientBinding;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation10".
 */
export type Observation10 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: NativeGameRecoveryRef;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_InstallationProjection_128".
 */
export type BoundedList_InstallationProjection_128 = InstallationProjection[];
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_OperationSnapshot_128".
 */
export type BoundedList_OperationSnapshot_128 = OperationSnapshot[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation6".
 */
export type Observation6 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: ApplicationPreferencesSnapshot;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation7".
 */
export type Observation7 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: Inventory;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_ProfileProjection_128".
 */
export type BoundedList_ProfileProjection_128 = ProfileProjection[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation11".
 */
export type Observation11 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: Inventory3;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_SessionProjection_128".
 */
export type BoundedList_SessionProjection_128 = SessionProjection[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation12".
 */
export type Observation12 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: Inventory6;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_ImportSourceProjection_128".
 */
export type BoundedList_ImportSourceProjection_128 = ImportSourceProjection[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ActionAvailability".
 */
export type ActionAvailability =
  | {
      grantsLock: FalseFlag;
      grantsPermission: FalseFlag;
      revision: OpaqueRevision;
      status: 'available';
    }
  | {
      reasons: BoundedList_AvailabilityReason_16;
      status: 'blocked';
    }
  | {
      reason: AvailabilityReason;
      status: 'unavailable';
    }
  | {
      reason: AvailabilityReason;
      status: 'unknown';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "FalseFlag".
 */
export type FalseFlag = false;
/**
 * @maxItems 16
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_AvailabilityReason_16".
 */
export type BoundedList_AvailabilityReason_16 =
  | []
  | [AvailabilityReason]
  | [AvailabilityReason, AvailabilityReason]
  | [AvailabilityReason, AvailabilityReason, AvailabilityReason]
  | [AvailabilityReason, AvailabilityReason, AvailabilityReason, AvailabilityReason]
  | [
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason
    ]
  | [
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason
    ]
  | [
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason
    ]
  | [
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason
    ]
  | [
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason
    ]
  | [
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason
    ]
  | [
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason
    ]
  | [
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason
    ]
  | [
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason
    ]
  | [
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason
    ]
  | [
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason
    ]
  | [
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason,
      AvailabilityReason
    ];
/**
 * @maxItems 64
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_ActionProjection_64".
 */
export type BoundedList_ActionProjection_64 = ActionProjection[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation13".
 */
export type Observation13 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: DocumentSnapshot;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProjectedValue".
 */
export type ProjectedValue =
  | {
      kind: 'public';
      value: PublicConfigValue;
    }
  | {
      kind: 'private';
      reference: PrivateValueRef;
    }
  | {
      configured: boolean;
      kind: 'secret';
    }
  | {
      kind: 'absent';
    }
  | {
      kind: 'unknown';
      reason: ObservationReason;
    };
/**
 * @maxItems 512
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_FieldState_512".
 */
export type BoundedList_FieldState_512 = FieldState[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PreservationState".
 */
export type PreservationState =
  'supported' | 'unsupported_target_syntax' | 'invalid_document' | 'native_unavailable';
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_SyncFeed_128".
 */
export type BoundedList_SyncFeed_128 = SyncFeed[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation14".
 */
export type Observation14 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: PrivateValueRef;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_SyncDestination_128".
 */
export type BoundedList_SyncDestination_128 = SyncDestination[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation15".
 */
export type Observation15 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: Inventory7;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_BackupReceiptRef_128".
 */
export type BoundedList_BackupReceiptRef_128 = BackupReceiptRef[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation16".
 */
export type Observation16 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: RuntimeReleaseSelectionRef;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation17".
 */
export type Observation17 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: CheckedGameUpdateRef;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation18".
 */
export type Observation18 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: BridgeReleaseSelectionRef;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * @maxItems 128
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_Event_128".
 */
export type BoundedList_Event_128 = Event[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DiagnosticFact".
 */
export type DiagnosticFact =
  | {
      kind: 'target';
      value: ResolvedTarget;
    }
  | {
      kind: 'session';
      value: Observation19;
    }
  | {
      kind: 'runtime';
      value: Observation20;
    }
  | {
      kind: 'game';
      value: Observation9;
    }
  | {
      kind: 'bridge';
      value: Observation21;
    }
  | {
      kind: 'capability';
      value: CapabilityProjection;
    }
  | {
      kind: 'issue';
      value: ProjectionIssue;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation19".
 */
export type Observation19 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: SessionBinding;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation20".
 */
export type Observation20 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: RuntimeOwnership;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Observation21".
 */
export type Observation21 =
  | {
      evidence: Evidence;
      status: 'observed';
      value: BridgeApplicationBinding;
    }
  | {
      evidence: Evidence;
      status: 'missing';
    }
  | {
      evidence: Evidence;
      reason: ObservationReason;
      status: 'unknown';
    }
  | {
      reason: ObservationReason;
      status: 'unavailable';
    };
/**
 * @maxItems 64
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_DiagnosticFact_64".
 */
export type BoundedList_DiagnosticFact_64 = DiagnosticFact[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "NativeAbsolutePath".
 */
export type NativeAbsolutePath =
  | {
      platform: 'windows';
      value: WindowsAbsolutePath;
    }
  | {
      platform: 'macos';
      value: MacAbsolutePath;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "WindowsAbsolutePath".
 */
export type WindowsAbsolutePath = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "MacAbsolutePath".
 */
export type MacAbsolutePath = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CommandResult".
 */
export type CommandResult =
  | {
      name: 'prepare';
      output: PreparedPlan;
    }
  | {
      name: 'commit';
      output: OperationSnapshot;
    }
  | {
      name: 'cancel_operation';
      output: CancelDisposition;
    }
  | {
      name: 'request_host_close';
      output: CloseDisposition;
    }
  | {
      name: 'open_draft';
      output: DraftSnapshot;
    }
  | {
      name: 'set_draft_changes';
      output: SetDraftChangesResult;
    }
  | {
      name: 'discard_draft';
      output: DiscardedDraft;
    }
  | {
      name: 'request_import_discovery';
      output: Observation12;
    }
  | {
      name: 'request_sensitive_input';
      output: SensitiveInputResult;
    }
  | {
      name: 'request_export_destination';
      output: ExportDestinationResult;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PlanId".
 */
export type PlanId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CancelDisposition".
 */
export type CancelDisposition =
  | {
      kind: 'requested';
      operation: OperationSnapshot;
    }
  | {
      kind: 'cancelled_before_commit';
      operation: OperationSnapshot;
    }
  | {
      kind: 'too_late';
      operation: OperationSnapshot;
    }
  | {
      kind: 'already_terminal';
      operation: OperationSnapshot;
    }
  | {
      kind: 'recovery_required';
      operation: OperationSnapshot;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CloseDisposition".
 */
export type CloseDisposition =
  | {
      kind: 'ready';
    }
  | {
      kind: 'deferred';
      obligations: BoundedList_CloseObligation_128;
    }
  | {
      kind: 'recovery_required';
      recoveries: BoundedList_RecoveryRef_64;
    };
/**
 * @maxItems 64
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_RecoveryRef_64".
 */
export type BoundedList_RecoveryRef_64 = RecoveryRef[];
/**
 * A closed substitution of backend-owned protected entry references. The
 * protocol proves binding continuity, never the native payload or custody.
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProtectedReferenceTransfer".
 */
export type ProtectedReferenceTransfer =
  | {
      from: PrivateValueRef;
      kind: 'private';
      to: PrivateValueRef;
    }
  | {
      from: SecretRef;
      kind: 'secret';
      to: SecretRef;
    };
/**
 * @maxItems 256
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_ProtectedReferenceTransfer_256".
 */
export type BoundedList_ProtectedReferenceTransfer_256 = ProtectedReferenceTransfer[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SensitiveInputKind".
 */
export type SensitiveInputKind = 'private' | 'secret';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SensitiveInputOutcome".
 */
export type SensitiveInputOutcome =
  | {
      reference: PrivateValueRef;
      status: 'captured_private';
    }
  | {
      reference: SecretRef;
      status: 'captured_secret';
    }
  | {
      status: 'cancelled';
    }
  | {
      reason: SensitiveInputUnavailableReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SensitiveInputUnavailableReason".
 */
export type SensitiveInputUnavailableReason =
  'native_unavailable' | 'unsupported_platform' | 'access_denied' | 'protected_entry_unavailable';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ExportDestinationOutcome".
 */
export type ExportDestinationOutcome =
  | {
      destination: ExportDestinationRef;
      status: 'captured';
    }
  | {
      status: 'cancelled';
    }
  | {
      reason: ExportDestinationUnavailableReason;
      status: 'unavailable';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ExportDestinationUnavailableReason".
 */
export type ExportDestinationUnavailableReason =
  'native_unavailable' | 'unsupported_platform' | 'access_denied' | 'selection_unavailable';
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ReplyRequestId".
 */
export type ReplyRequestId = RequestId | null;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RequestId".
 */
export type RequestId = string;
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RequestBody".
 */
export type RequestBody =
  | {
      query: Query;
      type: 'query';
    }
  | {
      command: Command;
      type: 'command';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Query".
 */
export type Query =
  | {
      input: EmptyInput;
      name: 'hello';
    }
  | {
      input: ResolveTargetInput;
      name: 'resolve_target';
    }
  | {
      input: GetOperationInput;
      name: 'get_operation';
    }
  | {
      input: EmptyInput;
      name: 'snapshot';
    }
  | {
      input: ListProfilesInput;
      name: 'list_profiles';
    }
  | {
      input: EmptyInput;
      name: 'list_installations';
    }
  | {
      input: EmptyInput;
      name: 'list_sessions';
    }
  | {
      input: EmptyInput;
      name: 'list_import_sources';
    }
  | {
      input: GetActionsInput;
      name: 'get_actions';
    }
  | {
      input: ReadConfigurationInput;
      name: 'read_configuration';
    }
  | {
      input: ConfigurationHistoryInput;
      name: 'configuration_history';
    }
  | {
      input: CheckRuntimeReleaseInput;
      name: 'check_runtime_release';
    }
  | {
      input: CheckGameUpdateInput;
      name: 'check_game_update';
    }
  | {
      input: CheckBridgeUpdateInput;
      name: 'check_bridge_update';
    }
  | {
      input: ResumeEventsInput;
      name: 'resume_events';
    }
  | {
      input: DiagnosticPreviewInput;
      name: 'diagnostic_preview';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "InstallationSelector".
 */
export type InstallationSelector =
  | {
      directoryAssertion?: NativeAbsolutePath | null;
      id: InstallationId;
      kind: 'registered';
      revisionAssertion?: OpaqueRevision | null;
    }
  | {
      directory: NativeAbsolutePath;
      kind: 'directory';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProfileFilter".
 */
export type ProfileFilter = 'active' | 'archived' | 'all';
/**
 * @maxItems 64
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BoundedList_ActionId_64".
 */
export type BoundedList_ActionId_64 = ActionId[];
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ActionScope".
 */
export type ActionScope =
  | {
      kind: 'target';
      target: ResolvedTarget;
    }
  | {
      kind: 'session';
      session: SessionBinding;
    }
  | {
      kind: 'profile';
      profile: IsolatedProfileRef;
    }
  | {
      document: DocumentBinding;
      kind: 'document';
    }
  | {
      application: BridgeApplicationBinding;
      kind: 'application';
    }
  | {
      kind: 'catalog';
      revision: OpaqueRevision;
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Command".
 */
export type Command =
  | {
      input: PrepareInput;
      name: 'prepare';
    }
  | {
      input: CommitInput;
      name: 'commit';
    }
  | {
      input: CancelOperationInput;
      name: 'cancel_operation';
    }
  | {
      input: RequestHostCloseInput;
      name: 'request_host_close';
    }
  | {
      input: OpenDraftInput;
      name: 'open_draft';
    }
  | {
      input: SetDraftChangesInput;
      name: 'set_draft_changes';
    }
  | {
      input: DiscardDraftInput;
      name: 'discard_draft';
    }
  | {
      input: RequestImportDiscoveryInput;
      name: 'request_import_discovery';
    }
  | {
      input: RequestSensitiveInputInput;
      name: 'request_sensitive_input';
    }
  | {
      input: RequestExportDestinationInput;
      name: 'request_export_destination';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "MutationIntent".
 */
export type MutationIntent =
  | {
      input: OrdinaryLaunchInput;
      kind: 'launch_ordinary';
    }
  | {
      input: IsolatedLaunchInput;
      kind: 'launch_isolated';
    }
  | {
      input: FocusSessionInput;
      kind: 'focus_session';
    }
  | {
      input: CreateProfileInput;
      kind: 'create_profile';
    }
  | {
      input: EditOrdinaryProfileInput;
      kind: 'edit_ordinary_profile';
    }
  | {
      input: EditIsolatedProfileInput;
      kind: 'edit_isolated_profile';
    }
  | {
      input: ProfileLifecycleInput;
      kind: 'archive_profile';
    }
  | {
      input: ProfileLifecycleInput;
      kind: 'restore_profile';
    }
  | {
      input: DeleteProfileInput;
      kind: 'delete_profile';
    }
  | {
      input: RegisterInstallationInput;
      kind: 'register_installation';
    }
  | {
      input: EditInstallationInput;
      kind: 'edit_installation';
    }
  | {
      input: SaveConfigurationInput;
      kind: 'save_configuration';
    }
  | {
      input: RestoreConfigurationInput;
      kind: 'restore_configuration';
    }
  | {
      input: RuntimeDeployInput;
      kind: 'runtime_install';
    }
  | {
      input: RuntimeDeployInput;
      kind: 'runtime_update';
    }
  | {
      input: RuntimeDeployInput;
      kind: 'runtime_repair';
    }
  | {
      input: RuntimeAdoptInput;
      kind: 'runtime_adopt';
    }
  | {
      input: ManagedRuntimeInput;
      kind: 'runtime_remove';
    }
  | {
      input: ManagedRuntimeInput;
      kind: 'runtime_stop_managing';
    }
  | {
      input: RuntimeSwitchSourceInput;
      kind: 'runtime_switch_source';
    }
  | {
      input: GameUpdateInput;
      kind: 'game_update';
    }
  | {
      input: RecoverGameUpdateInput;
      kind: 'recover_game_update';
    }
  | {
      input: BridgeUpdateInput;
      kind: 'bridge_update';
    }
  | {
      input: RecoverBridgeUpdateInput;
      kind: 'recover_bridge_update';
    }
  | {
      input: ExportDiagnosticsInput;
      kind: 'export_diagnostics';
    }
  | {
      input: SaveApplicationPreferencesInput;
      kind: 'save_application_preferences';
    };
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "OrdinaryProfileSelector".
 */
export type OrdinaryProfileSelector = {
  catalogIdAssertion?: ProfileId | null;
  kind: 'ordinary';
};
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "IsolatedProfileSelector".
 */
export type IsolatedProfileSelector = {
  id: ProfileId;
  kind: 'isolated';
  revisionAssertion?: OpaqueRevision | null;
};
/**
 * A saved native catalog preference names a registration. Path overrides use
 * `InstallationSelector` separately and never create a registration implicitly.
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RegisteredInstallationSelector".
 */
export type RegisteredInstallationSelector = {
  directoryAssertion?: NativeAbsolutePath | null;
  id: InstallationId;
  kind: 'registered';
  revisionAssertion?: OpaqueRevision | null;
};
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "IdempotencyKey".
 */
export type IdempotencyKey = string;

export interface ProtocolContract {
  event: Event;
  reply: Reply;
  request: Request;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Event".
 */
export interface Event {
  body: EventBody;
  cursor: Cursor;
  protocolVersion: ProtocolVersion;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "OperationSnapshot".
 */
export interface OperationSnapshot {
  operationId: OperationId;
  operationRevision: RevisionCounter;
  semantics: PlanSemantics;
  state: OperationState;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PlanSemantics".
 */
export interface PlanSemantics {
  action: ActionId;
  capture: PreparedCapture;
  effects: BoundedList_ProposedEffect_16;
  hashProfile: HashProfile;
  trustDomain: TrustDomain;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RuntimeBinding".
 */
export interface RuntimeBinding {
  architecture: ProcessArchitecture;
  artifactDigest: Sha256;
  clientRevision: OpaqueRevision;
  configurationSchemaDigest: Sha256;
  distributionId: DistributionId;
  manifest: RuntimeManifestObservation;
  platform: SupportedPlatform;
  providerId: ProviderId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ResolvedTarget".
 */
export interface ResolvedTarget {
  installation: InstallationBinding;
  profile: ProfileBinding;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SessionBinding".
 */
export interface SessionBinding {
  process: ProcessIdentity;
  revision: OpaqueRevision;
  sessionId: SessionId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProcessIdentity".
 */
export interface ProcessIdentity {
  architecture: ProcessArchitecture;
  executableIdentity: ExecutableIdentity;
  installationPhysicalId: PhysicalInstallationId;
  pid: Pid;
  startIdentity: ProcessStartIdentity;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CreateProfileCapture".
 */
export interface CreateProfileCapture {
  catalogRevision: OpaqueRevision;
  destinationOwner: OwnerScope;
  name: DisplayName;
  nativePreparationRef: NativePreparationRef;
  preferredInstallation: RegisteredInstallationBinding;
  setup: ProfileSetup;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ImportSourceRef".
 */
export interface ImportSourceRef {
  destinationOwner: OwnerScope;
  requiresNativeApproval: boolean;
  sourceId: ImportSourceId;
  sourceRevision: OpaqueRevision;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "EditOrdinaryProfileInput".
 */
export interface EditOrdinaryProfileInput {
  preferredInstallation: PreferredInstallationEdit;
  profile: OrdinaryProfileRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "EditIsolatedProfileInput".
 */
export interface EditIsolatedProfileInput {
  name?: DisplayName | null;
  preferredInstallation: PreferredInstallationEdit;
  profile: IsolatedProfileRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "IsolatedProfileRef".
 */
export interface IsolatedProfileRef {
  id: ProfileId;
  revision: OpaqueRevision;
  state: ProfileState;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProfileLifecycleInput".
 */
export interface ProfileLifecycleInput {
  profile: IsolatedProfileRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DeleteProfileInput".
 */
export interface DeleteProfileInput {
  confirmation: DeletionConfirmation;
  profile: IsolatedProfileRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RegisterInstallationCapture".
 */
export interface RegisterInstallationCapture {
  catalogRevision: OpaqueRevision;
  name: DisplayName;
  nativeTargetRef: NativeTargetRef;
  physicalId: PhysicalInstallationId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "EditInstallationInput".
 */
export interface EditInstallationInput {
  installation: InstallationBinding;
  name: DisplayName;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SaveConfigurationCapture".
 */
export interface SaveConfigurationCapture {
  candidateDigest: Sha256;
  draft: DraftSnapshot;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DraftSnapshot".
 */
export interface DraftSnapshot {
  apply: BoundedList_ApplyTiming_3;
  draft: DraftRef;
  edits: BoundedList_ConfigurationEdit_256;
  schema: ConfigurationSchema;
  state: DraftState;
  validation: BoundedList_DraftViolation_64;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DraftRef".
 */
export interface DraftRef {
  document: DocumentBinding;
  draftId: DraftId;
  hostEpoch: HostEpoch;
  revision: RevisionCounter;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DocumentBinding".
 */
export interface DocumentBinding {
  baseline: DocumentBaseline;
  documentId: DocumentId;
  revision: OpaqueRevision;
  schema: SchemaBinding;
  target: ResolvedTarget;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SchemaBinding".
 */
export interface SchemaBinding {
  digest: Sha256;
  providerId: ProviderId;
  runtimeArtifactDigest: Sha256;
  schemaId: SchemaId;
  schemaVersion: SchemaVersion;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "KeyChord".
 */
export interface KeyChord {
  key: KeyToken;
  modifiers: BoundedList_Modifier_4;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PrivateValueRef".
 */
export interface PrivateValueRef {
  /**
   * Present only for Bridge-protected entry custody. Saved document values
   * remain bound to the document revision and do not claim a native handle.
   */
  capturedFor?: DraftRef | null;
  document: DocumentBinding;
  fieldId: FieldId;
  revision: OpaqueRevision;
  valueId: PrivateValueId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SecretRef".
 */
export interface SecretRef {
  draft: DraftRef;
  fieldId: FieldId;
  secretId: SecretRefId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "NewSyncDestination".
 */
export interface NewSyncDestination {
  endpoint: PrivateValueRef;
  feeds: BoundedList_SyncFeedOverride_128;
  id: DestinationId;
  mode: SyncMode;
  proxy: ProxyChoice;
  secret: SecretRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SyncFeedOverride".
 */
export interface SyncFeedOverride {
  desired: InheritedBoolean;
  feedId: FeedId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ConfigurationSchema".
 */
export interface ConfigurationSchema {
  binding: SchemaBinding;
  fields: BoundedList_FieldDefinition_512;
  sync: BoundedList_SyncTypeDefinition_16;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "FieldDefinition".
 */
export interface FieldDefinition {
  aliases: BoundedList_BoundedList_TomlPathSegment_16_16;
  apply: ApplyTiming;
  category: CategoryId;
  defaultValue?: PublicConfigValue | null;
  deprecated: boolean;
  fieldId: FieldId;
  path: BoundedList_TomlPathSegment_16;
  platforms: BoundedList_SupportedPlatform_2;
  searchTerms: BoundedList_SchemaText_32;
  sensitivity: Sensitivity;
  valueType: FieldType;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SyncTypeDefinition".
 */
export interface SyncTypeDefinition {
  endpointFieldId: FieldId;
  exposure: SyncExposure;
  feeds: BoundedList_FeedId_128;
  fields: BoundedList_FieldId_128;
  inheritsGlobalProxy: boolean;
  mode: SyncMode;
  proxyFieldId?: FieldId | null;
  secretFieldId: FieldId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DraftViolation".
 */
export interface DraftViolation {
  code: DraftViolationCode;
  fieldId?: FieldId | null;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RestoreConfigurationInput".
 */
export interface RestoreConfigurationInput {
  backup: BackupReceiptRef;
  document: DocumentBinding;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BackupReceiptRef".
 */
export interface BackupReceiptRef {
  backupId: BackupId;
  createdAt: UtcTimestamp;
  document: DocumentBinding;
  nativeBackupRef: NativeTargetRef;
  retainedDigest: Sha256;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RuntimeDeployInput".
 */
export interface RuntimeDeployInput {
  configuration: ConfigurationParticipant;
  expectedOwnership: RuntimeOwnership;
  selectedRelease: RuntimeReleaseSelectionRef;
  target: ResolvedTarget;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ManagedRuntimeRef".
 */
export interface ManagedRuntimeRef {
  binding: RuntimeBinding;
  receiptId: RuntimeReceiptId;
  revision: OpaqueRevision;
  target: ResolvedTarget;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RuntimeReleaseSelectionRef".
 */
export interface RuntimeReleaseSelectionRef {
  artifacts: BoundedList_RuntimeArtifactSubject_16;
  authority: ArtifactAuthorityRef;
  channelId: ChannelId;
  clientRevision: OpaqueRevision;
  configurationSchema: SchemaBinding;
  distributionId: DistributionId;
  hostEpoch: HostEpoch;
  providerId: ProviderId;
  releaseVersion: ReleaseVersion;
  revision: OpaqueRevision;
  selectionId: RuntimeReleaseId;
  target: ResolvedTarget;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RuntimeArtifactSubject".
 */
export interface RuntimeArtifactSubject {
  architecture: ProcessArchitecture;
  digest: Sha256;
  platform: SupportedPlatform;
  role: ArtifactRole;
  size: ProgressCount;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RuntimeAdoptInput".
 */
export interface RuntimeAdoptInput {
  confirmation: RuntimeAdoptionConfirmation;
  observedArtifactDigest: Sha256;
  recognitionAuthority: ArtifactAuthorityRef;
  recognizedBinding: RuntimeBinding;
  target: ResolvedTarget;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ManagedRuntimeInput".
 */
export interface ManagedRuntimeInput {
  reference: ManagedRuntimeRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RuntimeSwitchSourceInput".
 */
export interface RuntimeSwitchSourceInput {
  configuration: ConfigurationParticipant;
  confirmation: SourceSwitchConfirmation;
  current: ManagedRuntimeRef;
  selectedRelease: RuntimeReleaseSelectionRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "GameUpdateInput".
 */
export interface GameUpdateInput {
  checkedUpdate: CheckedGameUpdateRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CheckedGameUpdateRef".
 */
export interface CheckedGameUpdateRef {
  checkId: GameUpdateId;
  currentClient: GameClientBinding;
  hostEpoch: HostEpoch;
  installation: InstallationBinding;
  offeredClient: GameClientBinding;
  officialManifestDigest: Sha256;
  revision: OpaqueRevision;
  route: GameUpdateRoute;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "GameClientBinding".
 */
export interface GameClientBinding {
  architecture: ProcessArchitecture;
  executableDigest: Sha256;
  version: ReleaseVersion;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RecoverGameUpdateInput".
 */
export interface RecoverGameUpdateInput {
  recovery: NativeGameRecoveryRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "NativeGameRecoveryRef".
 */
export interface NativeGameRecoveryRef {
  expectedClient: GameClientBinding;
  installation: InstallationBinding;
  nativeTransaction: NativeTransactionRef;
  revision: OpaqueRevision;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BridgeUpdateInput".
 */
export interface BridgeUpdateInput {
  selectedRelease: BridgeReleaseSelectionRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BridgeReleaseSelectionRef".
 */
export interface BridgeReleaseSelectionRef {
  authority: BridgeAuthorityRef;
  current: BridgeApplicationBinding;
  hostEpoch: HostEpoch;
  offered: BridgeApplicationBinding;
  releaseVersion: ReleaseVersion;
  revision: OpaqueRevision;
  selectionId: BridgeReleaseId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BridgeApplicationBinding".
 */
export interface BridgeApplicationBinding {
  applicationId: ApplicationIdentity;
  architecture: ProcessArchitecture;
  channelId: ChannelId;
  installationRef: NativeTargetRef;
  packageDigest: Sha256;
  pairingDigest: Sha256;
  payloads: BoundedList_BridgePayloadSubject_8;
  platform: SupportedPlatform;
  revision: OpaqueRevision;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BridgePayloadSubject".
 */
export interface BridgePayloadSubject {
  digest: Sha256;
  role: BridgePayloadRole;
  size: ProgressCount;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RecoverBridgeUpdateInput".
 */
export interface RecoverBridgeUpdateInput {
  recovery: BridgeRecoveryRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BridgeRecoveryRef".
 */
export interface BridgeRecoveryRef {
  application: BridgeApplicationBinding;
  bridgeJournalRef: NativeTransactionRef;
  expectedApplication: BridgeApplicationBinding;
  revision: OpaqueRevision;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ExportDiagnosticsInput".
 */
export interface ExportDiagnosticsInput {
  destination: ExportDestinationRef;
  preview: PreviewRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ExportDestinationRef".
 */
export interface ExportDestinationRef {
  destinationId: ExportDestinationId;
  hostEpoch: HostEpoch;
  nativeTargetRef: NativeTargetRef;
  revision: OpaqueRevision;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PreviewRef".
 */
export interface PreviewRef {
  digest: Sha256;
  disclosure: DiagnosticDisclosure;
  hostEpoch: HostEpoch;
  previewId: PreviewId;
  revision: OpaqueRevision;
  target: ResolvedTarget;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SaveApplicationPreferencesInput".
 */
export interface SaveApplicationPreferencesInput {
  expectedRevision: OpaqueRevision;
  values: ApplicationPreferences;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ApplicationPreferences".
 */
export interface ApplicationPreferences {
  lastTarget?: SavedTargetPreference | null;
  motion: MotionPreference;
  provider?: ProviderPreference | null;
  theme: ThemePreference;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SavedTargetPreference".
 */
export interface SavedTargetPreference {
  installationId: InstallationId;
  profile: ProfileSelector;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProviderPreference".
 */
export interface ProviderPreference {
  channelId: ChannelId;
  providerId: ProviderId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Progress".
 */
export interface Progress {
  measurement: Measurement;
  phase: PhaseId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SessionProjection".
 */
export interface SessionProjection {
  binding: SessionBinding;
  liveIdentity: Observation3;
  readiness: Observation4;
  target: Observation;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Evidence".
 */
export interface Evidence {
  observationId: ObservationId;
  observedAt: UtcTimestamp;
  source: EvidenceSource;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ApplicationPreferencesSnapshot".
 */
export interface ApplicationPreferencesSnapshot {
  revision: OpaqueRevision;
  values: ApplicationPreferences;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "BridgeError".
 */
export interface BridgeError {
  code: ErrorCode;
  expectedRevision?: ScopedRevisionRef | null;
  observedRevision?: ScopedRevisionRef | null;
  recovery?: RecoveryRef | null;
  retryDisposition: RetryDisposition;
  /**
   * @minItems 1
   * @maxItems 1
   */
  supportedVersions?: [ProtocolVersion] | null;
  violations: BoundedList_FieldViolation_8;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ScopedRevisionRef".
 */
export interface ScopedRevisionRef {
  revision: OpaqueRevision;
  scope: RevisionScope;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RecoveryRef".
 */
export interface RecoveryRef {
  operationId: OperationId;
  target: RecoveryTarget;
  transaction: NativeTransactionRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "FieldViolation".
 */
export interface FieldViolation {
  code: ViolationCode;
  field: FieldPath;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Cursor".
 */
export interface Cursor {
  hostEpoch: HostEpoch;
  sequence: Sequence;
  streamId: StreamId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Reply".
 */
export interface Reply {
  body: ReplyBody;
  protocolVersion: ProtocolVersion;
  requestId: ReplyRequestId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "HelloResult".
 */
export interface HelloResult {
  hostEpoch: HostEpoch;
  hostKind: HostKind;
  implementedCommands: BoundedList_CommandId_32;
  /**
   * @minItems 1
   * @maxItems 1
   */
  supportedVersions: [ProtocolVersion];
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ResolveTargetResult".
 */
export interface ResolveTargetResult {
  target: Observation;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "GetOperationResult".
 */
export interface GetOperationResult {
  operation: Observation2;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Snapshot".
 */
export interface Snapshot {
  capabilities: Inventory5;
  cursor: Cursor;
  installations: Observation8;
  operations: Inventory4;
  preferences: Observation6;
  profiles: Observation7;
  sessions: Observation11;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Inventory5".
 */
export interface Inventory5 {
  completeness: Completeness;
  issues: BoundedList_ProjectionIssue_64;
  items: BoundedList_CapabilityProjection_128;
  revision: OpaqueRevision;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ProjectionIssue".
 */
export interface ProjectionIssue {
  code: ProjectionIssueCode;
  resource?: ResourceRef | null;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CapabilityProjection".
 */
export interface CapabilityProjection {
  id: CapabilityId;
  status: CapabilityStatus;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "AvailabilityReason".
 */
export interface AvailabilityReason {
  code: AvailabilityReasonCode;
  remediation?: ActionId | null;
  resource?: ResourceRef | null;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Inventory2".
 */
export interface Inventory2 {
  completeness: Completeness;
  issues: BoundedList_ProjectionIssue_64;
  items: BoundedList_InstallationProjection_128;
  revision: OpaqueRevision;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "InstallationProjection".
 */
export interface InstallationProjection {
  binding: InstallationBinding;
  client: Observation9;
  name: DisplayName;
  update: Observation10;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Inventory4".
 */
export interface Inventory4 {
  completeness: Completeness;
  issues: BoundedList_ProjectionIssue_64;
  items: BoundedList_OperationSnapshot_128;
  revision: OpaqueRevision;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Inventory".
 */
export interface Inventory {
  completeness: Completeness;
  issues: BoundedList_ProjectionIssue_64;
  items: BoundedList_ProfileProjection_128;
  revision: OpaqueRevision;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Inventory3".
 */
export interface Inventory3 {
  completeness: Completeness;
  issues: BoundedList_ProjectionIssue_64;
  items: BoundedList_SessionProjection_128;
  revision: OpaqueRevision;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Inventory6".
 */
export interface Inventory6 {
  completeness: Completeness;
  issues: BoundedList_ProjectionIssue_64;
  items: BoundedList_ImportSourceProjection_128;
  revision: OpaqueRevision;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ImportSourceProjection".
 */
export interface ImportSourceProjection {
  accessibility: Observation3;
  name: DisplayName;
  reference: ImportSourceRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ActionProjection".
 */
export interface ActionProjection {
  action: ActionId;
  availability: ActionAvailability;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DocumentSnapshot".
 */
export interface DocumentSnapshot {
  binding: DocumentBinding;
  fields: BoundedList_FieldState_512;
  preservation: PreservationState;
  schema: ConfigurationSchema;
  sync: BoundedList_SyncDestination_128;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "FieldState".
 */
export interface FieldState {
  fieldId: FieldId;
  overridden: boolean;
  value: ProjectedValue;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SyncDestination".
 */
export interface SyncDestination {
  desiredProxy: ProxyChoice;
  endpoint: PrivateValueRef;
  exposure: SyncExposure;
  feeds: BoundedList_SyncFeed_128;
  id: DestinationId;
  mode: SyncMode;
  resolvedProxy: Observation14;
  secretConfigured: boolean;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SyncFeed".
 */
export interface SyncFeed {
  desired: InheritedBoolean;
  feedId: FeedId;
  resolved: Observation3;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Inventory7".
 */
export interface Inventory7 {
  completeness: Completeness;
  issues: BoundedList_ProjectionIssue_64;
  items: BoundedList_BackupReceiptRef_128;
  revision: OpaqueRevision;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "EventBatch".
 */
export interface EventBatch {
  after: Cursor;
  events: BoundedList_Event_128;
  next: Cursor;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DiagnosticPreview".
 */
export interface DiagnosticPreview {
  content: DiagnosticContent;
  reference: PreviewRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DiagnosticContent".
 */
export interface DiagnosticContent {
  disclosure: DiagnosticDisclosure;
  facts: BoundedList_DiagnosticFact_64;
  paths?: DisclosedPaths | null;
  target: ResolvedTarget;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DisclosedPaths".
 */
export interface DisclosedPaths {
  installation: NativeAbsolutePath;
  profile?: NativeAbsolutePath | null;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PreparedPlan".
 */
export interface PreparedPlan {
  expiresAt: UtcTimestamp;
  grantsLock: FalseFlag;
  grantsPermission: FalseFlag;
  planRef: PlanRef;
  semantics: PlanSemantics;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PlanRef".
 */
export interface PlanRef {
  hostEpoch: HostEpoch;
  planId: PlanId;
  reviewDigest: Sha256;
}
/**
 * Exact old input and its single successor. Only the backend may mint the
 * transfers after proving native custody. An exact retry returns this retained
 * acknowledgment; it must not perform another transfer or revision increment.
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SetDraftChangesResult".
 */
export interface SetDraftChangesResult {
  accepted: SetDraftChangesInput;
  protectedTransfers: BoundedList_ProtectedReferenceTransfer_256;
  snapshot: DraftSnapshot;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SetDraftChangesInput".
 */
export interface SetDraftChangesInput {
  draft: DraftRef;
  edits: BoundedList_ConfigurationEdit_256;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DiscardedDraft".
 */
export interface DiscardedDraft {
  draftId: DraftId;
  hostEpoch: HostEpoch;
  previousRevision: RevisionCounter;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SensitiveInputResult".
 */
export interface SensitiveInputResult {
  binding: RequestSensitiveInputInput;
  outcome: SensitiveInputOutcome;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RequestSensitiveInputInput".
 */
export interface RequestSensitiveInputInput {
  draft: DraftRef;
  fieldId: FieldId;
  sensitivity: SensitiveInputKind;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ExportDestinationResult".
 */
export interface ExportDestinationResult {
  binding: RequestExportDestinationInput;
  outcome: ExportDestinationOutcome;
}
/**
 * A transport-neutral request to the backend's native/headless save-selection
 * port. The opaque reference remains backend custody, never a client path.
 *
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RequestExportDestinationInput".
 */
export interface RequestExportDestinationInput {
  preview: PreviewRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "Request".
 */
export interface Request {
  body: RequestBody;
  protocolVersion: ProtocolVersion;
  requestId: RequestId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "EmptyInput".
 */
export interface EmptyInput {}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ResolveTargetInput".
 */
export interface ResolveTargetInput {
  target: TargetSelector;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "TargetSelector".
 */
export interface TargetSelector {
  installation: InstallationSelector;
  profile: ProfileSelector;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "GetOperationInput".
 */
export interface GetOperationInput {
  operationId: OperationId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ListProfilesInput".
 */
export interface ListProfilesInput {
  state: ProfileFilter;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "GetActionsInput".
 */
export interface GetActionsInput {
  actions: BoundedList_ActionId_64;
  scope: ActionScope;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ReadConfigurationInput".
 */
export interface ReadConfigurationInput {
  target: TargetSelector;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ConfigurationHistoryInput".
 */
export interface ConfigurationHistoryInput {
  document: DocumentBinding;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CheckRuntimeReleaseInput".
 */
export interface CheckRuntimeReleaseInput {
  channelId: ChannelId;
  providerId: ProviderId;
  target: ResolvedTarget;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CheckGameUpdateInput".
 */
export interface CheckGameUpdateInput {
  installation: InstallationSelector;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CheckBridgeUpdateInput".
 */
export interface CheckBridgeUpdateInput {
  application: BridgeApplicationBinding;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "ResumeEventsInput".
 */
export interface ResumeEventsInput {
  after: Cursor;
  maximumEvents: ProgressCount;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DiagnosticPreviewInput".
 */
export interface DiagnosticPreviewInput {
  disclosure: DiagnosticDisclosure;
  target: TargetSelector;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "PrepareInput".
 */
export interface PrepareInput {
  intent: MutationIntent;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "OrdinaryLaunchInput".
 */
export interface OrdinaryLaunchInput {
  target: OrdinaryTargetSelector;
  unrecognizedRuntimeChoice?: UnrecognizedRuntimeChoice;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "OrdinaryTargetSelector".
 */
export interface OrdinaryTargetSelector {
  installation: InstallationSelector;
  profile: OrdinaryProfileSelector;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "IsolatedLaunchInput".
 */
export interface IsolatedLaunchInput {
  storeMode: StoreMode;
  target: IsolatedTargetSelector;
  unrecognizedRuntimeChoice?: UnrecognizedRuntimeChoice;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "IsolatedTargetSelector".
 */
export interface IsolatedTargetSelector {
  installation: InstallationSelector;
  profile: IsolatedProfileSelector;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "FocusSessionInput".
 */
export interface FocusSessionInput {
  session: SessionBinding;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CreateProfileInput".
 */
export interface CreateProfileInput {
  expectedCatalogRevision: OpaqueRevision;
  name: DisplayName;
  preferredInstallation: RegisteredInstallationSelector;
  setup: ProfileSetup;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RegisterInstallationInput".
 */
export interface RegisterInstallationInput {
  directory: NativeAbsolutePath;
  expectedCatalogRevision: OpaqueRevision;
  name: DisplayName;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "SaveConfigurationInput".
 */
export interface SaveConfigurationInput {
  draft: DraftRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CommitInput".
 */
export interface CommitInput {
  idempotencyKey: IdempotencyKey;
  planRef: PlanRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "CancelOperationInput".
 */
export interface CancelOperationInput {
  expectedOperationRevision: RevisionCounter;
  operationId: OperationId;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RequestHostCloseInput".
 */
export interface RequestHostCloseInput {
  expectedCursor: Cursor;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "OpenDraftInput".
 */
export interface OpenDraftInput {
  document: DocumentBinding;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "DiscardDraftInput".
 */
export interface DiscardDraftInput {
  draft: DraftRef;
}
/**
 * This interface was referenced by `ProtocolContract`'s JSON-Schema
 * via the `definition` "RequestImportDiscoveryInput".
 */
export interface RequestImportDiscoveryInput {
  approval: NativeApprovalChoice;
  expectedDestinationOwner: OwnerScope;
}
