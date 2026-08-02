namespace STFCCommunityMod.Launcher.Core;

public static class SelfUpdateArchitecture
{
    public const string Strategy = "Verified replace-on-exit bootstrapper";

    public static readonly IReadOnlyList<string> RequiredPhases =
    [
        "download",
        "verify-size-and-sha256",
        "stage-on-same-volume",
        "start-bootstrapper",
        "wait-for-launcher-exit",
        "atomic-replace",
        "start-and-health-check",
        "rollback-on-failure",
    ];
}

public static class LauncherSelfUpdateAuthority
{
    // Kept on the currently shipped release feed until #4 moves launcher
    // delivery. Provider selection cannot alter this launcher-owned authority.
    public const string ReleaseRepository = "Guffawaffle/stfc-mod";
    public const string ReleaseManifestAssetName = "stfc-community-mod-release-manifest.json";
    public const string WindowsArtifactPublisher = "Joseph Gustavson";
}
