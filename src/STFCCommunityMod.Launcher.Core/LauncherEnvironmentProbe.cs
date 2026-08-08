namespace STFCCommunityMod.Launcher.Core;

public sealed class LauncherEnvironmentProbe(
    IGameProcessInspector processInspector,
    PerUserInstallLayout installLayout,
    GameInstallDiscovery installDiscovery)
{
    private const string SavedSelectionReadFailureDetail =
        "The saved installation selection could not be read. Choose the STFC game folder again.";

    public LauncherEnvironmentSnapshot Capture(CancellationToken cancellationToken = default)
    {
        var discovery = installDiscovery.Discover(cancellationToken);
        var persistedCandidate = discovery.Candidates.FirstOrDefault(
            candidate => candidate.Evidence.Any(
                evidence => evidence.Source == GameInstallCandidateSource.PersistedSelection));
        var selectedGameDirectory = persistedCandidate?.Validation.IsValid == true
            ? persistedCandidate.GameDirectory
            : null;
        var processState = selectedGameDirectory is null
            ? GameProcessInspectionState.NotRunning
            : processInspector.Inspect(selectedGameDirectory);
        var gameRunning = processState != GameProcessInspectionState.NotRunning;
        var dimensions = CreateHealthDimensions(processState, discovery, persistedCandidate);
        var aggregate = CreateAggregateState(processState, discovery, persistedCandidate);

        return new(
            aggregate.Code,
            aggregate.Title,
            aggregate.Detail,
            gameRunning,
            installLayout,
            selectedGameDirectory,
            discovery,
            dimensions)
        {
            GameProcessState = processState,
        };
    }

    public GameInstallCandidate ConfirmManualSelection(string gameDirectory)
    {
        return installDiscovery.ConfirmManualSelection(gameDirectory);
    }

    private static IReadOnlyList<LauncherHealthDimension> CreateHealthDimensions(
        GameProcessInspectionState processState,
        GameInstallDiscoverySnapshot discovery,
        GameInstallCandidate? persistedCandidate)
    {
        var processDimension = processState switch
        {
            GameProcessInspectionState.RunningTarget => new LauncherHealthDimension(
                LauncherHealthDimensionCategory.ProcessSafety,
                LauncherHealthSeverity.Informational,
                "Game client is running",
                "Normal play remains available; close the game only before a mod mutation."),
            GameProcessInspectionState.Unattributable => new(
                LauncherHealthDimensionCategory.ProcessSafety,
                LauncherHealthSeverity.ActionRequired,
                "Game process needs attention",
                "A prime.exe process could not be attributed safely; mod mutations remain blocked."),
            _ => new(
                LauncherHealthDimensionCategory.ProcessSafety,
                LauncherHealthSeverity.Healthy,
                "Game client is stopped",
                "The process boundary currently permits later transactional work."),
        };

        LauncherHealthDimension selectionDimension;
        if (discovery.PersistedSelection.State == GameInstallSelectionState.Invalid)
        {
            selectionDimension = new(
                LauncherHealthDimensionCategory.InstallationSelection,
                LauncherHealthSeverity.ActionRequired,
                "Saved selection is unreadable",
                SavedSelectionReadFailureDetail);
        }
        else if (persistedCandidate?.Validation.IsValid == true)
        {
            selectionDimension = new(
                LauncherHealthDimensionCategory.InstallationSelection,
                LauncherHealthSeverity.Healthy,
                "Confirmed installation is valid",
                "The confirmed folder still contains prime.exe. Its path is hidden for privacy.");
        }
        else if (persistedCandidate is not null)
        {
            selectionDimension = new(
                LauncherHealthDimensionCategory.InstallationSelection,
                LauncherHealthSeverity.ActionRequired,
                "Confirmed installation is no longer valid",
                persistedCandidate.Validation.Message);
        }
        else
        {
            selectionDimension = new(
                LauncherHealthDimensionCategory.InstallationSelection,
                LauncherHealthSeverity.Informational,
                "No installation confirmed",
                "Review a discovered candidate or choose the game folder manually.");
        }

        var validCount = discovery.ValidCandidates.Count;
        var discoveryDimension = validCount > 0
            ? new LauncherHealthDimension(
                LauncherHealthDimensionCategory.Discovery,
                LauncherHealthSeverity.Healthy,
                $"{validCount} valid installation candidate{(validCount == 1 ? string.Empty : "s")}",
                "Discovery inspected only explicit, bounded locations.")
            : new(
                LauncherHealthDimensionCategory.Discovery,
                LauncherHealthSeverity.Informational,
                "No valid installation candidate",
                "Choose the folder that directly contains prime.exe.");

        return [processDimension, selectionDimension, discoveryDimension];
    }

    private static (LauncherHealthCode Code, string Title, string Detail) CreateAggregateState(
        GameProcessInspectionState processState,
        GameInstallDiscoverySnapshot discovery,
        GameInstallCandidate? persistedCandidate)
    {
        if (processState == GameProcessInspectionState.Unattributable)
        {
            return (
                LauncherHealthCode.GameProcessUnattributable,
                "GAME PROCESS NEEDS ATTENTION",
                "A prime.exe process could not be attributed safely. Mod mutations remain blocked.");
        }

        if (processState == GameProcessInspectionState.RunningTarget)
        {
            return (
                LauncherHealthCode.GameRunning,
                "GAME CLIENT DETECTED",
                "STFC is running normally. Close it only before installing, updating, repairing, or removing the mod.");
        }

        if (discovery.PersistedSelection.State == GameInstallSelectionState.Invalid
            || persistedCandidate is { Validation.IsValid: false })
        {
            return (
                LauncherHealthCode.SelectionInvalid,
                "SELECTION NEEDS ATTENTION",
                persistedCandidate?.Validation.Message
                    ?? SavedSelectionReadFailureDetail);
        }

        if (persistedCandidate?.Validation.IsValid == true)
        {
            return (
                LauncherHealthCode.InstallationReady,
                "INSTALLATION READY",
                "The confirmed game folder still contains prime.exe. Its path is hidden for privacy.");
        }

        if (discovery.ValidCandidates.Count > 0)
        {
            return (
                LauncherHealthCode.CandidateFound,
                "INSTALLATION FOUND",
                "A bounded candidate contains prime.exe. Confirm the folder before later deployment work.");
        }

        return (
            LauncherHealthCode.ReadyForDiscovery,
            "READY FOR DISCOVERY",
            "No valid bounded candidate was found. Choose the game folder that directly contains prime.exe.");
    }
}
