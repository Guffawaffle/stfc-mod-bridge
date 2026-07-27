namespace STFCCommunityMod.Launcher.Core;

public enum LauncherHomeTone
{
    Neutral,
    Success,
    Warning,
    Error,
}

public sealed record LauncherHomePresentation(
    string Headline,
    string Detail,
    string GameFolderStatus,
    string GameFolderIcon,
    LauncherHomeTone GameFolderTone,
    string GameFolderActionLabel,
    string GameFolderActionAutomationName,
    string GameClientStatus,
    bool IsGameRunning)
{
    public static LauncherHomePresentation FromSnapshot(LauncherEnvironmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var persistedCandidate = snapshot.Discovery.Candidates.FirstOrDefault(
            candidate => candidate.Evidence.Any(
                evidence => evidence.Source == GameInstallCandidateSource.PersistedSelection));
        var savedSelectionNeedsAttention =
            snapshot.Discovery.PersistedSelection.State == GameInstallSelectionState.Invalid
            || persistedCandidate is { Validation.IsValid: false };
        var gameFolderIsSet = snapshot.SelectedGameDirectory is not null;
        var boundedCandidateFound = !gameFolderIsSet && snapshot.Discovery.ValidCandidates.Count > 0;

        var (headline, detail) = snapshot.IsGameRunning
            ? (
                "Game is running",
                gameFolderIsSet
                    ? "The saved game folder remains ready while you play."
                    : "Installation checks remain available while you play.")
            : snapshot.HealthCode switch
            {
                LauncherHealthCode.InstallationReady => (
                    "Game folder ready",
                    "STFC was found and validated."),
                LauncherHealthCode.SelectionInvalid => (
                    "Game folder needs attention",
                    "Choose the folder that directly contains prime.exe."),
                LauncherHealthCode.CandidateFound => (
                    "Game installation found",
                    "Confirm the folder before the launcher manages the mod."),
                _ => (
                    "Choose your game folder",
                    "Select the folder that directly contains prime.exe."),
            };

        var (folderStatus, folderIcon, folderTone, actionLabel, automationName) =
            gameFolderIsSet
                ? (
                    "Set",
                    "✓",
                    LauncherHomeTone.Success,
                    "_Change",
                    "Change confirmed STFC game folder")
                : savedSelectionNeedsAttention
                    ? (
                        "Needs attention",
                        "×",
                        LauncherHomeTone.Error,
                        "_Choose folder",
                        "Choose a replacement STFC game folder")
                    : boundedCandidateFound
                        ? (
                            "Found",
                            "!",
                            LauncherHomeTone.Warning,
                            "_Confirm",
                            "Confirm discovered STFC game folder")
                        : (
                            "Not set",
                            "!",
                            LauncherHomeTone.Warning,
                            "_Set folder",
                            "Set STFC game folder");

        return new(
            headline,
            detail,
            folderStatus,
            folderIcon,
            folderTone,
            actionLabel,
            automationName,
            snapshot.IsGameRunning ? "Running" : "Not running",
            snapshot.IsGameRunning);
    }
}
