namespace STFCCommunityMod.Launcher.Core;

public enum LauncherHomeTone
{
    Neutral,
    Success,
    Warning,
    Error,
}

public sealed record LauncherHomePresentation(
    string GameFolderStatus,
    string GameFolderIcon,
    LauncherHomeTone GameFolderTone,
    string GameFolderStatusAutomationName,
    string GameFolderActionLabel,
    string GameFolderActionAutomationName,
    string GameClientStatus,
    string GameClientIcon,
    LauncherHomeTone GameClientTone,
    string GameClientStatusAutomationName,
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

        var (folderStatus, folderIcon, folderTone, folderAutomationName, actionLabel, actionAutomationName) =
            gameFolderIsSet
                ? (
                    string.Empty,
                    "✓",
                    LauncherHomeTone.Success,
                    "Game folder set",
                    "_Change",
                    "Change confirmed STFC game folder")
                : savedSelectionNeedsAttention
                    ? (
                        "Needs attention",
                        "×",
                        LauncherHomeTone.Error,
                        "Game folder needs attention",
                        "_Choose folder",
                        "Choose a replacement STFC game folder")
                    : boundedCandidateFound
                        ? (
                            "Found",
                            "!",
                            LauncherHomeTone.Warning,
                            "Game folder found and awaiting confirmation",
                            "_Confirm",
                            "Confirm discovered STFC game folder")
                        : (
                            "Not set",
                            "!",
                            LauncherHomeTone.Warning,
                            "Game folder not set",
                            "_Set folder",
                            "Set STFC game folder");

        var (clientStatus, clientIcon, clientTone, clientAutomationName) =
            snapshot.IsGameRunning
                ? (
                    "Running",
                    "●",
                    LauncherHomeTone.Success,
                    "STFC game client is running")
                : (
                    "Not running",
                    "○",
                    LauncherHomeTone.Neutral,
                    "STFC game client is not running");

        return new(
            folderStatus,
            folderIcon,
            folderTone,
            folderAutomationName,
            actionLabel,
            actionAutomationName,
            clientStatus,
            clientIcon,
            clientTone,
            clientAutomationName,
            snapshot.IsGameRunning);
    }
}
