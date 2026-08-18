namespace STFCCommunityMod.Launcher.ViewModels;

public enum WorkspaceSaveStateKind
{
    NoChanges,
    Ready,
    Blocked,
}

public enum WorkspaceSaveBlockerKind
{
    None,
    InvalidSetting,
    SiblingWorkspace,
    ExternalChange,
    SelectedConfigurationChanged,
    DataSyncValidation,
    LegacyMigration,
    WorkspaceUnavailable,
}

public enum WorkspaceSaveRecoveryKind
{
    None,
    ReviewSetting,
    GoToSettings,
    GoToDataSync,
    DiscardAndReload,
    ReviewDestination,
    ApproveLegacyMigration,
}

public sealed record WorkspaceSaveState(
    WorkspaceSaveStateKind Kind,
    WorkspaceSaveBlockerKind Blocker,
    string Message,
    WorkspaceSaveRecoveryKind Recovery = WorkspaceSaveRecoveryKind.None,
    string RecoveryActionLabel = "",
    string? TargetId = null)
{
    public bool CanSave => Kind == WorkspaceSaveStateKind.Ready;

    public bool IsBlocked => Kind == WorkspaceSaveStateKind.Blocked;

    public bool HasRecoveryAction =>
        IsBlocked
        && Recovery != WorkspaceSaveRecoveryKind.None
        && !string.IsNullOrWhiteSpace(RecoveryActionLabel);
}
