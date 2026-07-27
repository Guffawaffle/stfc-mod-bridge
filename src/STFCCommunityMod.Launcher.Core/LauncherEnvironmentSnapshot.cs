namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherEnvironmentSnapshot(
    LauncherHealthCode HealthCode,
    string StatusTitle,
    string StatusDetail,
    bool IsGameRunning,
    PerUserInstallLayout InstallLayout);
