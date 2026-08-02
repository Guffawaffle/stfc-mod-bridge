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
    string? providerUnavailableReason = null,
    LauncherHealthService? healthService = null)
{
    private readonly LauncherHealthService healthService = healthService ?? new(
        new ModInstallationInspector(deploymentService, new SystemModInstallationFileSystem()),
        new LauncherProviderHealthContext(
            "unattributed",
            channel,
            "unknown",
            string.IsNullOrWhiteSpace(providerUnavailableReason),
            providerUnavailableReason ?? string.Empty));

    public LauncherHealthSnapshot CaptureHealth(
        string? gameDirectory,
        bool isGameRunning) =>
        healthService.Capture(gameDirectory, isGameRunning);

    public ModManagementPresentation CapturePresentation(
        string? gameDirectory,
        bool isGameRunning) =>
        CaptureHealth(gameDirectory, isGameRunning).ModManagement;

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
        healthService.RecordUpdateObservation(gameDirectory, isGameRunning, discovery);
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

}
