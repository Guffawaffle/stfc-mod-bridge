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
    public const string ReleaseRepository = "Guffawaffle/stfc-mod-bridge";
    public const string ReleaseManifestAssetName = ModBridgeProductIdentity.ReleaseManifestName;
    public const string WindowsArtifactPublisher = "Joseph Gustavson";
}
