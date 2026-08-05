using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class SelfUpdateArchitectureTests
{
    private static readonly string[] ExpectedPhases =
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

    [TestMethod]
    public void StrategyNeverReplacesTheRunningLauncherInPlace()
    {
        CollectionAssert.AreEqual(
            ExpectedPhases,
            SelfUpdateArchitecture.RequiredPhases.ToArray());
        StringAssert.Contains(SelfUpdateArchitecture.Strategy, "replace-on-exit");
    }

    [TestMethod]
    public void SelfUpdateAuthorityBelongsToStandaloneLauncherRepository()
    {
        Assert.AreEqual("Guffawaffle/stfc-mod-bridge", LauncherSelfUpdateAuthority.ReleaseRepository);
        Assert.AreEqual(
            "stfc-mod-bridge-release-manifest.json",
            LauncherSelfUpdateAuthority.ReleaseManifestAssetName);
        Assert.AreEqual(
            "CN=Joseph Gustavson, O=Joseph Gustavson, L=Dousman, S=Wisconsin, C=US, PostalCode=53118",
            LauncherSelfUpdateAuthority.WindowsArtifactPublisher);
        Assert.AreEqual(
            "1.3.6.1.4.1.311.97.664386437.910814316.510550690.722133748",
            LauncherSelfUpdateAuthority.WindowsArtifactSigningIdentityEku);
    }
}
