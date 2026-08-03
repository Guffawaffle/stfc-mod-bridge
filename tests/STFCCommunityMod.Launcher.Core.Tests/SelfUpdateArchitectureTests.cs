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
        Assert.AreEqual("Joseph Gustavson", LauncherSelfUpdateAuthority.WindowsArtifactPublisher);
    }
}
