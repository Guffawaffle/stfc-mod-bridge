using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherEnvironmentProbeTests
{
    private static readonly PerUserInstallLayout InstallLayout =
        PerUserInstallLayout.FromLocalApplicationData(Path.Combine(Path.GetTempPath(), "launcher-tests"));

    [TestMethod]
    public void CaptureWhenGameIsRunningReportsMutationBlockInText()
    {
        var probe = new LauncherEnvironmentProbe(new FakeProcessInspector(true), InstallLayout);

        var result = probe.Capture();

        Assert.AreEqual(LauncherHealthCode.GameRunning, result.HealthCode);
        Assert.IsTrue(result.IsGameRunning);
        StringAssert.Contains(result.StatusTitle, "GAME CLIENT");
        StringAssert.Contains(result.StatusDetail, "blocked");
    }

    [TestMethod]
    public void CaptureWhenGameIsStoppedReportsDiscoveryReadiness()
    {
        var probe = new LauncherEnvironmentProbe(new FakeProcessInspector(false), InstallLayout);

        var result = probe.Capture();

        Assert.AreEqual(LauncherHealthCode.ReadyForDiscovery, result.HealthCode);
        Assert.IsFalse(result.IsGameRunning);
        StringAssert.Contains(result.StatusTitle, "READY");
        StringAssert.Contains(result.StatusDetail, "bounded");
    }

    private sealed class FakeProcessInspector(bool isRunning) : IGameProcessInspector
    {
        public bool IsGameRunning() => isRunning;
    }
}
