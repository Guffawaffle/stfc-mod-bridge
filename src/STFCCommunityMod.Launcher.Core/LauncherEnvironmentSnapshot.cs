namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherEnvironmentSnapshot(
    LauncherHealthCode HealthCode,
    string StatusTitle,
    string StatusDetail,
    bool IsGameRunning,
    PerUserInstallLayout InstallLayout,
    string? SelectedGameDirectory,
    GameInstallDiscoverySnapshot Discovery,
    IReadOnlyList<LauncherHealthDimension> HealthDimensions)
{
    public GameProcessInspectionState GameProcessState { get; init; } = IsGameRunning
        ? GameProcessInspectionState.RunningTarget
        : GameProcessInspectionState.NotRunning;
}
