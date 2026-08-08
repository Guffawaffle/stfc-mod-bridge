using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherHomePresentationTests
{
    private static readonly PerUserInstallLayout InstallLayout =
        new("launcher-program", "launcher-state");

    [TestMethod]
    public void ReadyInstallationPresentsCompactSuccessState()
    {
        var candidate = CreateCandidate(
            @"D:\Games\STFC",
            GameInstallCandidateSource.PersistedSelection,
            isValid: true);
        var snapshot = CreateSnapshot(
            LauncherHealthCode.InstallationReady,
            isGameRunning: false,
            candidate.GameDirectory,
            [candidate],
            GameInstallSelectionLoadResult.Loaded(
                new(candidate.GameDirectory, DateTimeOffset.UtcNow)));

        var result = LauncherHomePresentation.FromSnapshot(snapshot);

        Assert.AreEqual(string.Empty, result.GameFolderStatus);
        Assert.AreEqual("Installation found · Not running", result.GameSectionStatus);
        Assert.AreEqual("✓", result.GameFolderIcon);
        Assert.AreEqual(LauncherHomeTone.Success, result.GameFolderTone);
        Assert.AreEqual("Game folder set", result.GameFolderStatusAutomationName);
        Assert.AreEqual("Not running", result.GameClientStatus);
        Assert.AreEqual("○", result.GameClientIcon);
        Assert.AreEqual(LauncherHomeTone.Neutral, result.GameClientTone);
    }

    [TestMethod]
    public void InvalidSavedSelectionPresentsActionRequiredState()
    {
        const string sensitivePath = @"C:\Users\Streamer\Games\STFC";
        var snapshot = CreateSnapshot(
            LauncherHealthCode.SelectionInvalid,
            isGameRunning: false,
            selectedGameDirectory: null,
            candidates: [],
            GameInstallSelectionLoadResult.Invalid($"Access denied: {sensitivePath}"));

        var result = LauncherHomePresentation.FromSnapshot(snapshot);

        Assert.AreEqual("Needs attention", result.GameFolderStatus);
        Assert.AreEqual("Installation needs attention · Not running", result.GameSectionStatus);
        Assert.AreEqual("×", result.GameFolderIcon);
        Assert.AreEqual(LauncherHomeTone.Error, result.GameFolderTone);
        Assert.IsFalse(
            result.GameFolderStatusAutomationName.Contains(
                sensitivePath,
                StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DiscoveredCandidateRequestsConfirmationWithoutDisplayingPath()
    {
        const string sensitivePath = @"C:\Users\Streamer\Games\STFC";
        var candidate = CreateCandidate(
            sensitivePath,
            GameInstallCandidateSource.OfficialLauncherSettings,
            isValid: true);
        var snapshot = CreateSnapshot(
            LauncherHealthCode.CandidateFound,
            isGameRunning: false,
            selectedGameDirectory: null,
            [candidate],
            GameInstallSelectionLoadResult.Missing());

        var result = LauncherHomePresentation.FromSnapshot(snapshot);

        Assert.AreEqual("Found", result.GameFolderStatus);
        Assert.AreEqual("Installation found; confirmation needed · Not running", result.GameSectionStatus);
        Assert.AreEqual(LauncherHomeTone.Warning, result.GameFolderTone);
        StringAssert.Contains(result.GameFolderActionAutomationName, "Confirm");
        Assert.IsFalse(
            string.Join(
                    " ",
                    result.GameFolderStatus,
                    result.GameFolderActionAutomationName)
                .Contains(sensitivePath, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void RunningGameRemainsIndependentFromFolderHealth()
    {
        var candidate = CreateCandidate(
            @"D:\Games\STFC",
            GameInstallCandidateSource.PersistedSelection,
            isValid: true);
        var snapshot = CreateSnapshot(
            LauncherHealthCode.GameRunning,
            isGameRunning: true,
            candidate.GameDirectory,
            [candidate],
            GameInstallSelectionLoadResult.Loaded(
                new(candidate.GameDirectory, DateTimeOffset.UtcNow)));

        var result = LauncherHomePresentation.FromSnapshot(snapshot);

        Assert.AreEqual(string.Empty, result.GameFolderStatus);
        Assert.AreEqual("Running", result.GameClientStatus);
        Assert.AreEqual("Installation found · Running", result.GameSectionStatus);
        Assert.AreEqual("●", result.GameClientIcon);
        Assert.AreEqual(LauncherHomeTone.Success, result.GameClientTone);
        Assert.AreEqual("STFC game client is running normally", result.GameClientStatusAutomationName);
        Assert.IsTrue(result.IsGameRunning);
    }

    [TestMethod]
    public void UnattributablePrimeProcessRemainsGenuineAttention()
    {
        var candidate = CreateCandidate(
            @"D:\Games\STFC",
            GameInstallCandidateSource.PersistedSelection,
            isValid: true);
        var snapshot = CreateSnapshot(
            LauncherHealthCode.GameProcessUnattributable,
            isGameRunning: true,
            candidate.GameDirectory,
            [candidate],
            GameInstallSelectionLoadResult.Loaded(
                new(candidate.GameDirectory, DateTimeOffset.UtcNow))) with
        {
            GameProcessState = GameProcessInspectionState.Unattributable,
        };

        var result = LauncherHomePresentation.FromSnapshot(snapshot);

        Assert.AreEqual("Needs attention", result.GameClientStatus);
        Assert.AreEqual(LauncherHomeTone.Warning, result.GameClientTone);
        StringAssert.Contains(result.GameClientStatusAutomationName, "could not be attributed safely");
        Assert.IsTrue(result.IsGameRunning);
    }

    private static LauncherEnvironmentSnapshot CreateSnapshot(
        LauncherHealthCode healthCode,
        bool isGameRunning,
        string? selectedGameDirectory,
        IReadOnlyList<GameInstallCandidate> candidates,
        GameInstallSelectionLoadResult persistedSelection)
    {
        return new(
            healthCode,
            "internal title",
            "internal detail",
            isGameRunning,
            InstallLayout,
            selectedGameDirectory,
            new(candidates, persistedSelection),
            []);
    }

    private static GameInstallCandidate CreateCandidate(
        string gameDirectory,
        GameInstallCandidateSource source,
        bool isValid)
    {
        var validationCode = isValid
            ? GameInstallValidationCode.Valid
            : GameInstallValidationCode.DirectoryMissing;
        return new(
            gameDirectory,
            GameInstallConfidence.UserConfirmed,
            [new(source, GameInstallConfidence.UserConfirmed, "test evidence")],
            new(
                validationCode,
                gameDirectory,
                isValid ? Path.Combine(gameDirectory, "prime.exe") : null,
                isValid ? "valid" : "invalid"));
    }
}
