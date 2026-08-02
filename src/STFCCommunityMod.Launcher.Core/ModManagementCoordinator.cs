using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

public enum ModManagementActionKind
{
    None,
    Install,
    AdoptAndInstall,
    CheckForUpdate,
    Repair,
    Recover,
}

public sealed record ModManagementPresentation(
    string Status,
    LauncherHomeTone Tone,
    string ActionLabel,
    ModManagementActionKind ActionKind,
    bool CanExecute,
    string AutomationName);

public enum ModOperationPreparationState
{
    Ready,
    UpToDate,
}

public sealed record ModOperationPreparation(
    ModOperationPreparationState State,
    string Message,
    string GameDirectory,
    string ReleaseVersion,
    ModReleaseArtifact Artifact,
    ExistingArtifactPolicy ExistingArtifactPolicy,
    ModManagementActionKind ActionKind);

public sealed class ModManagementCoordinator(
    ModDeploymentService deploymentService,
    IWindowsReleaseDiscoveryClient releaseDiscoveryClient,
    Version launcherVersion,
    string channel = "stable",
    string? providerUnavailableReason = null)
{
    public ModManagementPresentation CapturePresentation(
        string? gameDirectory,
        bool isGameRunning)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return new(
                "Select a game folder",
                LauncherHomeTone.Warning,
                "Install mod",
                ModManagementActionKind.None,
                false,
                "Community mod unavailable until a game folder is selected");
        }

        string normalizedGameDirectory;
        try
        {
            normalizedGameDirectory = Path.GetFullPath(gameDirectory);
            var validation = GameInstallValidator.Validate(normalizedGameDirectory);
            if (!validation.IsValid)
            {
                return RepairRequired(validation.Message);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return RepairRequired(exception.Message);
        }

        ModInstalledArtifactState? state;
        try
        {
            var journal = deploymentService.ReadJournal();
            if (journal is not null
                && journal.Phase is not (ModDeploymentPhase.Committed
                    or ModDeploymentPhase.RolledBack
                    or ModDeploymentPhase.Failed))
            {
                return new(
                    "Recovery required",
                    LauncherHomeTone.Error,
                    "Recover",
                    ModManagementActionKind.Recover,
                    !isGameRunning,
                    "Community mod transaction recovery is required");
            }
            state = deploymentService.ReadInstalledState();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return RepairRequired(exception.Message);
        }

        if (!string.IsNullOrWhiteSpace(providerUnavailableReason))
        {
            return new(
                "Provider capabilities unknown",
                LauncherHomeTone.Warning,
                "Unavailable",
                ModManagementActionKind.None,
                false,
                providerUnavailableReason);
        }

        var targetPath = Path.Combine(normalizedGameDirectory, "version.dll");
        if (state is null)
        {
            var hasManualArtifact = File.Exists(targetPath);
            return new(
                hasManualArtifact ? "Manual install found" : "Not installed",
                hasManualArtifact ? LauncherHomeTone.Warning : LauncherHomeTone.Neutral,
                hasManualArtifact ? "Adopt & update" : "Install mod",
                hasManualArtifact ? ModManagementActionKind.AdoptAndInstall : ModManagementActionKind.Install,
                !isGameRunning,
                hasManualArtifact
                    ? "Adopt the existing community mod and install the selected release"
                    : "Install the community mod");
        }

        if (!PathEquals(state.GameDirectory, normalizedGameDirectory)
            || !File.Exists(targetPath)
            || !string.Equals(ComputeSha256(targetPath), state.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return RepairRequired(
                "The installed artifact no longer matches Mod Control-managed state.",
                !isGameRunning);
        }

        return new(
            $"Installed {state.Version}",
            LauncherHomeTone.Success,
            "Check for updates",
            ModManagementActionKind.CheckForUpdate,
            !isGameRunning,
            $"Check for a community mod update; installed version {state.Version}");
    }

    public async Task<ModOperationPreparation> PrepareLatestAsync(
        string gameDirectory,
        bool isGameRunning,
        CancellationToken cancellationToken = default)
    {
        var presentation = CapturePresentation(gameDirectory, isGameRunning);
        if (!presentation.CanExecute || presentation.ActionKind == ModManagementActionKind.None)
        {
            throw new InvalidOperationException(presentation.AutomationName);
        }

        if (presentation.ActionKind == ModManagementActionKind.Recover)
        {
            throw new InvalidOperationException("Recovery does not require release discovery.");
        }

        var discovery = await releaseDiscoveryClient.DiscoverLatestAsync(
            channel,
            launcherVersion,
            cancellationToken);
        var installedState = deploymentService.ReadInstalledState();
        if (presentation.ActionKind != ModManagementActionKind.Repair
            && installedState is not null
            && string.Equals(installedState.Sha256, discovery.ModArtifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ModOperationPreparationState.UpToDate,
                $"Community mod {discovery.Manifest.ReleaseVersion} is already installed.",
                Path.GetFullPath(gameDirectory),
                discovery.Manifest.ReleaseVersion,
                discovery.ModArtifact,
                ExistingArtifactPolicy.Reject,
                presentation.ActionKind);
        }

        var policy = presentation.ActionKind == ModManagementActionKind.AdoptAndInstall
            ? ExistingArtifactPolicy.AdoptAndPreserve
            : ExistingArtifactPolicy.Reject;
        var action = presentation.ActionKind == ModManagementActionKind.Install
            ? "Install"
            : presentation.ActionKind == ModManagementActionKind.AdoptAndInstall
                ? "Adopt the existing version.dll and install"
                : presentation.ActionKind == ModManagementActionKind.Repair
                    ? "Repair with"
                : "Update to";
        return new(
            ModOperationPreparationState.Ready,
            $"{action} community mod {discovery.Manifest.ReleaseVersion} in the selected game folder.",
            Path.GetFullPath(gameDirectory),
            discovery.Manifest.ReleaseVersion,
            discovery.ModArtifact,
            policy,
            presentation.ActionKind);
    }

    public Task<ModDeploymentResult> ExecuteAsync(
        ModOperationPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (preparation.State != ModOperationPreparationState.Ready)
        {
            throw new InvalidOperationException("Only a ready mod operation can execute.");
        }
        return preparation.ActionKind == ModManagementActionKind.Repair
            ? deploymentService.RepairAsync(
                preparation.GameDirectory,
                preparation.Artifact,
                cancellationToken)
            : deploymentService.DeployAsync(
                preparation.GameDirectory,
                preparation.Artifact,
                preparation.ExistingArtifactPolicy,
                cancellationToken);
    }

    public Task<ModDeploymentResult> RecoverAsync(CancellationToken cancellationToken = default) =>
        deploymentService.RecoverAsync(cancellationToken);

    public Task<ModDeploymentResult> UninstallAsync(CancellationToken cancellationToken = default) =>
        deploymentService.UninstallAsync(cancellationToken);

    private static ModManagementPresentation RepairRequired(string detail, bool canExecute = false) => new(
        "Repair required",
        LauncherHomeTone.Error,
        "Repair",
        canExecute ? ModManagementActionKind.Repair : ModManagementActionKind.None,
        canExecute,
        $"Community mod repair is required: {detail}");

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
