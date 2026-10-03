using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherEnvironmentProbeTests
{
    private static readonly PerUserInstallLayout InstallLayout =
        PerUserInstallLayout.FromLocalApplicationData(Path.Combine(Path.GetTempPath(), "launcher-tests"));

    [TestMethod]
    public void CaptureWhenGameIsRunningReportsNormalInformationalState()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        TemporaryDirectory.CreateFile(temporaryDirectory.Path, "prime.exe");
        var probe = CreateProbe(
            true,
            GameInstallSelectionLoadResult.Loaded(
                new(temporaryDirectory.Path, DateTimeOffset.UtcNow)));

        var result = probe.Capture();

        Assert.AreEqual(LauncherHealthCode.GameRunning, result.HealthCode);
        Assert.IsTrue(result.IsGameRunning);
        StringAssert.Contains(result.StatusTitle, "GAME CLIENT");
        StringAssert.Contains(result.StatusDetail, "running normally");
        Assert.IsTrue(
            result.HealthDimensions.Any(
                dimension =>
                    dimension.Category == LauncherHealthDimensionCategory.ProcessSafety
                    && dimension.Severity == LauncherHealthSeverity.Informational));
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
        var processInspector = new FakeProcessInspector(GameProcessInspectionState.RunningTarget);
        var probe = CreateProbe(processInspector, selection);

        var result = probe.Capture();

        Assert.AreEqual(LauncherHealthCode.GameRunning, result.HealthCode);
        Assert.AreEqual(temporaryDirectory.Path, result.SelectedGameDirectory);
        Assert.AreEqual(temporaryDirectory.Path, processInspector.InspectedDirectory);
        Assert.IsTrue(
            result.HealthDimensions.Any(
                dimension =>
                    dimension.Category == LauncherHealthDimensionCategory.ProcessSafety
                    && dimension.Severity == LauncherHealthSeverity.Informational));
        Assert.IsTrue(
            result.HealthDimensions.Any(
                dimension =>
                    dimension.Category == LauncherHealthDimensionCategory.InstallationSelection
                    && dimension.Severity == LauncherHealthSeverity.Healthy));
        Assert.IsFalse(result.StatusDetail.Contains(temporaryDirectory.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            result.HealthDimensions.Any(
                dimension => dimension.Detail.Contains(temporaryDirectory.Path, StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(
            result.HealthDimensions.Any(
                dimension =>
                    dimension.Category == LauncherHealthDimensionCategory.InstallationSelection
                    && dimension.Detail.Contains("hidden for privacy", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void CaptureDoesNotRenderPathsFromPersistedSelectionErrors()
    {
        const string sensitivePath = @"C:\Users\Streamer\AppData\Local\STFC Mod Bridge\install-selection.json";
        var probe = CreateProbe(
            false,
            GameInstallSelectionLoadResult.Invalid($"Access denied: {sensitivePath}"));

        var result = probe.Capture();

        Assert.AreEqual(LauncherHealthCode.SelectionInvalid, result.HealthCode);
        Assert.IsFalse(result.StatusDetail.Contains(sensitivePath, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            result.HealthDimensions.Any(
                dimension => dimension.Detail.Contains(sensitivePath, StringComparison.OrdinalIgnoreCase)));
        StringAssert.Contains(result.StatusDetail, "could not be read");
    }

    [TestMethod]
    public void CaptureWhenPrimeCannotBeAttributedRequiresAttentionAndBlocksMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        TemporaryDirectory.CreateFile(temporaryDirectory.Path, "prime.exe");
        var probe = CreateProbe(
            new FakeProcessInspector(GameProcessInspectionState.Unattributable),
            GameInstallSelectionLoadResult.Loaded(
                new(temporaryDirectory.Path, DateTimeOffset.UtcNow)));

        var result = probe.Capture();

        Assert.AreEqual(LauncherHealthCode.GameProcessUnattributable, result.HealthCode);
        Assert.AreEqual(GameProcessInspectionState.Unattributable, result.GameProcessState);
        Assert.IsTrue(result.IsGameRunning, "Unattributable prime.exe processes must still fail closed for mutation.");
        Assert.IsTrue(
            result.HealthDimensions.Any(
                dimension =>
                    dimension.Category == LauncherHealthDimensionCategory.ProcessSafety
                    && dimension.Severity == LauncherHealthSeverity.ActionRequired));
    }

    [TestMethod]
    public void RestartRetainsConfirmedRecoveryTargetWhenPrimeIsMissingWithoutSelectingAnotherCandidate()
    {
        using var directory = new TemporaryDirectory();
        var chosen = Path.Combine(directory.Path, "chosen");
        var other = Path.Combine(directory.Path, "other");
        Directory.CreateDirectory(chosen);
        Directory.CreateDirectory(other);
        TemporaryDirectory.CreateFile(chosen, "prime.exe");
        TemporaryDirectory.CreateFile(other, "prime.exe");
        var state = Path.Combine(directory.Path, "bridge-state");
        new GameInstallDiscovery(new JsonGameInstallSelectionStore(state), []).ConfirmManualSelection(chosen);
        File.Move(Path.Combine(chosen, "prime.exe"), Path.Combine(chosen, "interrupted-backup.exe"));
        var restarted = new LauncherEnvironmentProbe(new FakeProcessInspector(GameProcessInspectionState.NotRunning),
            InstallLayout, new GameInstallDiscovery(new JsonGameInstallSelectionStore(state),
                [new BoundedGameInstallCandidateProvider([new(other, [])])]));
        var captured = restarted.Capture();
        Assert.IsNull(captured.SelectedGameDirectory);
        Assert.AreEqual(chosen, captured.ConfirmedGameInstallationDirectory);
        Assert.AreEqual(LauncherHealthCode.SelectionInvalid, captured.HealthCode);
        Assert.IsTrue(captured.Discovery.ValidCandidates.Any(candidate => candidate.GameDirectory == other));
    }

    [TestMethod]
    public void MissingInvalidAndRelativeConfirmationsNeverBecomeRecoveryTargets()
    {
        foreach (var selection in new[] { GameInstallSelectionLoadResult.Missing(),
            GameInstallSelectionLoadResult.Invalid("Corrupt saved selection"),
            GameInstallSelectionLoadResult.Loaded(new("relative", DateTimeOffset.UtcNow)) })
            Assert.IsNull(CreateProbe(false, selection).Capture().ConfirmedGameInstallationDirectory);
    }

    private static LauncherEnvironmentProbe CreateProbe(
        bool gameRunning,
        GameInstallSelectionLoadResult? selection = null)
        => CreateProbe(
            new FakeProcessInspector(
                gameRunning
                    ? GameProcessInspectionState.RunningTarget
                    : GameProcessInspectionState.NotRunning),
            selection);

    private static LauncherEnvironmentProbe CreateProbe(
        FakeProcessInspector processInspector,
        GameInstallSelectionLoadResult? selection = null)
    {
        var store = new FakeSelectionStore(
            selection ?? GameInstallSelectionLoadResult.Missing());
        var discovery = new GameInstallDiscovery(store, []);
        return new(
            processInspector,
            InstallLayout,
            discovery);
    }

    private sealed class FakeProcessInspector(GameProcessInspectionState state) : IGameProcessInspector
    {
        public string? InspectedDirectory { get; private set; }

        public GameProcessInspectionState Inspect(string gameDirectory)
        {
            InspectedDirectory = gameDirectory;
            return state;
        }
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
