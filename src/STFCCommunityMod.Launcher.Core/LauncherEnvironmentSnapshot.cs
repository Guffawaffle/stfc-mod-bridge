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
    /// <summary>Saved user confirmation retained for updater status/recovery even when prime.exe is temporarily absent.</summary>
    public string? ConfirmedGameInstallationDirectory
    {
        get
        {
            var directory = Discovery.PersistedSelection is
                { State: GameInstallSelectionState.Loaded, Selection: { } selection }
                ? selection.GameDirectory : null;
            try
            {
                return !string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory)
                    ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) : null;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            { return null; }
        }
    }

    public GameProcessInspectionState GameProcessState { get; init; } = IsGameRunning
        ? GameProcessInspectionState.RunningTarget
        : GameProcessInspectionState.NotRunning;
}
