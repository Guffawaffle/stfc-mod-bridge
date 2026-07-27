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

        Assert.AreEqual("Game folder ready", result.Headline);
        Assert.AreEqual("Set", result.GameFolderStatus);
        Assert.AreEqual("✓", result.GameFolderIcon);
        Assert.AreEqual(LauncherHomeTone.Success, result.GameFolderTone);
        Assert.AreEqual("Not running", result.GameClientStatus);
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

        Assert.AreEqual("Game folder needs attention", result.Headline);
        Assert.AreEqual("Needs attention", result.GameFolderStatus);
        Assert.AreEqual("×", result.GameFolderIcon);
        Assert.AreEqual(LauncherHomeTone.Error, result.GameFolderTone);
        Assert.IsFalse(
            result.Detail.Contains(sensitivePath, StringComparison.OrdinalIgnoreCase));
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

        Assert.AreEqual("Game installation found", result.Headline);
        Assert.AreEqual("Found", result.GameFolderStatus);
        Assert.AreEqual(LauncherHomeTone.Warning, result.GameFolderTone);
        StringAssert.Contains(result.GameFolderActionAutomationName, "Confirm");
        Assert.IsFalse(
            string.Join(
                    " ",
                    result.Headline,
                    result.Detail,
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

        Assert.AreEqual("Game is running", result.Headline);
        Assert.AreEqual("Set", result.GameFolderStatus);
        Assert.AreEqual("Running", result.GameClientStatus);
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
