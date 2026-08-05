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
    public const string WindowsArtifactPublisher =
        "CN=Joseph Gustavson, O=Joseph Gustavson, L=Dousman, S=Wisconsin, C=US, PostalCode=53118";
    public const string WindowsArtifactSigningIdentityEku =
        "1.3.6.1.4.1.311.97.664386437.910814316.510550690.722133748";
}
