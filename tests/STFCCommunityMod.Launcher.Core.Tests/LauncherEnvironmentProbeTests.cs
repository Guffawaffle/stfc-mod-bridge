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
        var probe = CreateProbe(true);

        var result = probe.Capture();

        Assert.AreEqual(LauncherHealthCode.GameRunning, result.HealthCode);
        Assert.IsTrue(result.IsGameRunning);
        StringAssert.Contains(result.StatusTitle, "GAME CLIENT");
        StringAssert.Contains(result.StatusDetail, "blocked");
    }

    [TestMethod]
    public void CaptureWhenGameIsStoppedReportsDiscoveryReadiness()
    {
        var probe = CreateProbe(false);

        var result = probe.Capture();

        Assert.AreEqual(LauncherHealthCode.ReadyForDiscovery, result.HealthCode);
        Assert.IsFalse(result.IsGameRunning);
        StringAssert.Contains(result.StatusTitle, "READY");
        StringAssert.Contains(result.StatusDetail, "bounded candidate");
    }

    [TestMethod]
    public void CaptureKeepsProcessAndInstallationHealthComposable()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        TemporaryDirectory.CreateFile(temporaryDirectory.Path, "prime.exe");
        var selection = GameInstallSelectionLoadResult.Loaded(
            new(temporaryDirectory.Path, DateTimeOffset.UtcNow));
        var probe = CreateProbe(true, selection);

        var result = probe.Capture();

        Assert.AreEqual(LauncherHealthCode.GameRunning, result.HealthCode);
        Assert.AreEqual(temporaryDirectory.Path, result.SelectedGameDirectory);
        Assert.IsTrue(
            result.HealthDimensions.Any(
                dimension =>
                    dimension.Category == LauncherHealthDimensionCategory.ProcessSafety
                    && dimension.Severity == LauncherHealthSeverity.ActionRequired));
        Assert.IsTrue(
            result.HealthDimensions.Any(
                dimension =>
                    dimension.Category == LauncherHealthDimensionCategory.InstallationSelection
                    && dimension.Severity == LauncherHealthSeverity.Healthy));
    }

    private static LauncherEnvironmentProbe CreateProbe(
        bool gameRunning,
        GameInstallSelectionLoadResult? selection = null)
    {
        var store = new FakeSelectionStore(
            selection ?? GameInstallSelectionLoadResult.Missing());
        var discovery = new GameInstallDiscovery(store, []);
        return new(
            new FakeProcessInspector(gameRunning),
            InstallLayout,
            discovery);
    }

    private sealed class FakeProcessInspector(bool isRunning) : IGameProcessInspector
    {
        public bool IsGameRunning() => isRunning;
    }

    private sealed class FakeSelectionStore(GameInstallSelectionLoadResult result)
        : IGameInstallSelectionStore
    {
        public GameInstallSelectionLoadResult Load() => result;

        public void Save(string gameDirectory)
        {
        }
    }
}
