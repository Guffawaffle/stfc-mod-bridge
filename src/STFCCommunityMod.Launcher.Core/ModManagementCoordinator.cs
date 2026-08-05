namespace STFCCommunityMod.Launcher.Core;

public enum ModManagementActionKind
{
    None,
    Install,
    UpdateManualInstallation,
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
    ModManagementActionKind ActionKind,
    string ProviderId);

public interface IModManagementCoordinator
{
    string ProviderId { get; }

    LauncherHealthSnapshot CaptureHealth(string? gameDirectory, bool isGameRunning);

    LauncherHealthSnapshot ResolveHealth(ModInstallationEvidence installation);

    ModManagementPresentation CapturePresentation(string? gameDirectory, bool isGameRunning);

    Task<ModOperationPreparation> PrepareLatestAsync(
        string gameDirectory,
        bool isGameRunning,
        CancellationToken cancellationToken = default);

    Task<ModOperationPreparation> PrepareLatestFromEvidenceAsync(
        string gameDirectory,
        bool isGameRunning,
        ModInstallationEvidence installation,
        CancellationToken cancellationToken = default);

    Task<ModDeploymentResult> ExecuteAsync(
        ModOperationPreparation preparation,
        CancellationToken cancellationToken = default);

    Task<ModDeploymentResult> RecoverAsync(CancellationToken cancellationToken = default);

    Task<ModDeploymentResult> UninstallAsync(CancellationToken cancellationToken = default);
}

public sealed class ModManagementCoordinator(
    ModDeploymentService deploymentService,
    IWindowsReleaseDiscoveryClient releaseDiscoveryClient,
    Version launcherVersion,
    string channel = "stable",
    string? providerUnavailableReason = null,
    LauncherHealthService? healthService = null) : IModManagementCoordinator
{
    private readonly LauncherHealthService healthService = healthService ?? new(
        new ModInstallationInspector(deploymentService, new SystemModInstallationFileSystem()),
        new LauncherProviderHealthContext(
            "unattributed",
            channel,
            "unknown",
            string.IsNullOrWhiteSpace(providerUnavailableReason),
            providerUnavailableReason ?? string.Empty));

    public string ProviderId => healthService.ProviderId;

    public LauncherHealthSnapshot CaptureHealth(
        string? gameDirectory,
        bool isGameRunning) =>
        healthService.Capture(gameDirectory, isGameRunning);

    public LauncherHealthSnapshot ResolveHealth(ModInstallationEvidence installation) =>
        healthService.Resolve(installation);

    public ModManagementPresentation CapturePresentation(
        string? gameDirectory,
        bool isGameRunning) =>
        CaptureHealth(gameDirectory, isGameRunning).ModManagement;

    public Task<ModOperationPreparation> PrepareLatestAsync(
        string gameDirectory,
        bool isGameRunning,
        CancellationToken cancellationToken = default) =>
        PrepareLatestCoreAsync(
            gameDirectory,
            isGameRunning,
            CaptureHealth(gameDirectory, isGameRunning),
            cancellationToken);

    public Task<ModOperationPreparation> PrepareLatestFromEvidenceAsync(
        string gameDirectory,
        bool isGameRunning,
        ModInstallationEvidence installation,
        CancellationToken cancellationToken = default) =>
        PrepareLatestCoreAsync(
            gameDirectory,
            isGameRunning,
            ResolveHealth(installation),
            cancellationToken);

    private async Task<ModOperationPreparation> PrepareLatestCoreAsync(
        string gameDirectory,
        bool isGameRunning,
        LauncherHealthSnapshot health,
        CancellationToken cancellationToken)
    {
        var presentation = health.ModManagement;
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
        healthService.RecordUpdateObservation(health.Installation, discovery);
        if (presentation.ActionKind != ModManagementActionKind.Repair
            && string.Equals(
                health.Installation.InstalledSha256,
                discovery.ModArtifact.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ModOperationPreparationState.UpToDate,
                $"Community mod {discovery.Manifest.ReleaseVersion} is already installed.",
                Path.GetFullPath(gameDirectory),
                discovery.Manifest.ReleaseVersion,
                discovery.ModArtifact,
                ExistingArtifactPolicy.Reject,
                presentation.ActionKind,
                healthService.ProviderId);
        }

        var policy = presentation.ActionKind == ModManagementActionKind.UpdateManualInstallation
            ? ExistingArtifactPolicy.AdoptAndPreserve
            : ExistingArtifactPolicy.Reject;
        var action = presentation.ActionKind == ModManagementActionKind.Install
            ? "Install"
            : presentation.ActionKind == ModManagementActionKind.UpdateManualInstallation
                ? "Update the existing installation to"
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
            presentation.ActionKind,
            healthService.ProviderId);
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
        if (!string.Equals(preparation.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Prepared provider '{preparation.ProviderId}' does not match '{ProviderId}'.");
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

    public async Task<ModOperationPreparation> PrepareProviderSwitchTargetAsync(
        string gameDirectory,
        bool isGameRunning,
        ModInstallationEvidence sourceInstallation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceInstallation);
        var validation = GameInstallValidator.Validate(gameDirectory);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }
        if (isGameRunning || sourceInstallation.IsGameRunning)
        {
            throw new InvalidOperationException(
                "Close Star Trek Fleet Command in the selected installation before switching release source.");
        }
        if (sourceInstallation.State != ModInstallationEvidenceState.ManagedVerified
            || string.IsNullOrWhiteSpace(sourceInstallation.InstalledProviderId))
        {
            throw new InvalidOperationException(
                "A release-source switch requires a verified Mod Bridge-managed DLL. "
                + "Manual or changed DLLs require their separate adoption or repair flow.");
        }

        var discovery = await releaseDiscoveryClient.DiscoverLatestAsync(
            channel,
            launcherVersion,
            cancellationToken).ConfigureAwait(false);
        return new(
            ModOperationPreparationState.Ready,
            $"Switch the managed community mod to {discovery.Manifest.ReleaseVersion}.",
            validation.GameDirectory,
            discovery.Manifest.ReleaseVersion,
            discovery.ModArtifact,
            ExistingArtifactPolicy.Reject,
            ModManagementActionKind.UpdateManualInstallation,
            ProviderId);
    }

    public Task<ModDeploymentResult> ExecuteCoordinatedAsync(
        ModOperationPreparation preparation,
        string transactionId,
        IModDeploymentCommitParticipant commitParticipant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(commitParticipant);
        if (preparation.State != ModOperationPreparationState.Ready)
        {
            throw new InvalidOperationException("Only a ready provider-switch artifact can execute.");
        }
        if (!string.Equals(preparation.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Prepared provider '{preparation.ProviderId}' does not match '{ProviderId}'.");
        }
        return deploymentService.DeployCoordinatedAsync(
            preparation.GameDirectory,
            preparation.Artifact,
            preparation.ExistingArtifactPolicy,
            transactionId,
            commitParticipant,
            cancellationToken);
    }

    public Task<ModDeploymentResult> RollBackCoordinatedAsync(
        string transactionId,
        CancellationToken cancellationToken = default) =>
        deploymentService.RollBackCoordinatedAsync(transactionId, cancellationToken);

    public Task<ModDeploymentResult> RecoverAsync(CancellationToken cancellationToken = default) =>
        deploymentService.RecoverAsync(cancellationToken);

    public Task<ModDeploymentResult> UninstallAsync(CancellationToken cancellationToken = default) =>
        deploymentService.UninstallAsync(cancellationToken);

}

public sealed record ModProviderManagementEndpoint(
    string ProviderId,
    string RuntimeDistributionId,
    IModManagementCoordinator Coordinator);

public sealed class ProviderAwareModManagementCoordinator : IModManagementCoordinator
{
    private readonly string selectedProviderId;
    private readonly Dictionary<string, ModProviderManagementEndpoint> endpoints;
    private readonly Dictionary<string, string> providerIdByRuntimeDistribution;

    public ProviderAwareModManagementCoordinator(
        string selectedProviderId,
        IEnumerable<ModProviderManagementEndpoint> endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedProviderId);
        ArgumentNullException.ThrowIfNull(endpoints);
        this.selectedProviderId = selectedProviderId;
        var resolved = endpoints.ToDictionary(endpoint => endpoint.ProviderId, StringComparer.Ordinal);
        if (resolved.Values.Any(endpoint =>
                !string.Equals(endpoint.ProviderId, endpoint.Coordinator.ProviderId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A mod-management endpoint is bound to a different coordinator provider.",
                nameof(endpoints));
        }
        if (!resolved.ContainsKey(selectedProviderId))
        {
            throw new ArgumentException("Selected provider has no mod-management endpoint.", nameof(endpoints));
        }
        this.endpoints = resolved;
        providerIdByRuntimeDistribution = resolved.Values.ToDictionary(
            endpoint => endpoint.RuntimeDistributionId,
            endpoint => endpoint.ProviderId,
            StringComparer.Ordinal);
    }

    public string ProviderId => selectedProviderId;

    public LauncherHealthSnapshot CaptureHealth(string? gameDirectory, bool isGameRunning)
    {
        var selectedSnapshot = endpoints[selectedProviderId].Coordinator.CaptureHealth(gameDirectory, isGameRunning);
        return ResolveHealth(selectedSnapshot.Installation, selectedSnapshot);
    }

    public LauncherHealthSnapshot ResolveHealth(ModInstallationEvidence installation) =>
        ResolveHealth(installation, selectedSnapshot: null);

    public ModManagementPresentation CapturePresentation(string? gameDirectory, bool isGameRunning) =>
        CaptureHealth(gameDirectory, isGameRunning).ModManagement;

    public Task<ModOperationPreparation> PrepareLatestAsync(
        string gameDirectory,
        bool isGameRunning,
        CancellationToken cancellationToken = default)
    {
        var snapshot = CaptureHealth(gameDirectory, isGameRunning);
        var providerId = ResolveProviderId(snapshot.Installation);
        return endpoints[providerId].Coordinator.PrepareLatestFromEvidenceAsync(
            gameDirectory,
            isGameRunning,
            snapshot.Installation,
            cancellationToken);
    }

    public Task<ModOperationPreparation> PrepareLatestFromEvidenceAsync(
        string gameDirectory,
        bool isGameRunning,
        ModInstallationEvidence installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var providerId = ResolveProviderId(installation);
        return endpoints[providerId].Coordinator.PrepareLatestFromEvidenceAsync(
            gameDirectory,
            isGameRunning,
            installation,
            cancellationToken);
    }

    public Task<ModDeploymentResult> ExecuteAsync(
        ModOperationPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return endpoints.TryGetValue(preparation.ProviderId, out var endpoint)
            ? endpoint.Coordinator.ExecuteAsync(preparation, cancellationToken)
            : Task.FromException<ModDeploymentResult>(
                new InvalidOperationException(
                    $"Prepared provider '{preparation.ProviderId}' is no longer available."));
    }

    public Task<ModDeploymentResult> RecoverAsync(CancellationToken cancellationToken = default) =>
        endpoints[selectedProviderId].Coordinator.RecoverAsync(cancellationToken);

    public Task<ModDeploymentResult> UninstallAsync(CancellationToken cancellationToken = default) =>
        endpoints[selectedProviderId].Coordinator.UninstallAsync(cancellationToken);

    private string ResolveProviderId(ModInstallationEvidence installation)
    {
        if (!string.IsNullOrWhiteSpace(installation.InstalledProviderId)
            && endpoints.ContainsKey(installation.InstalledProviderId))
        {
            return installation.InstalledProviderId;
        }
        var detectedProviderId = installation.BinaryProvenance?.DetectedProviderId;
        if (!string.IsNullOrWhiteSpace(detectedProviderId) && endpoints.ContainsKey(detectedProviderId))
        {
            return detectedProviderId;
        }
        var runtimeDistributionId = installation.BinaryProvenance?.DetectedRuntimeDistributionId;
        return !string.IsNullOrWhiteSpace(runtimeDistributionId)
            && providerIdByRuntimeDistribution.TryGetValue(runtimeDistributionId, out var providerId)
                ? providerId
                : selectedProviderId;
    }

    private LauncherHealthSnapshot ResolveHealth(
        ModInstallationEvidence installation,
        LauncherHealthSnapshot? selectedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var providerId = ResolveProviderId(installation);
        return string.Equals(providerId, selectedProviderId, StringComparison.Ordinal)
            && selectedSnapshot is not null
                ? selectedSnapshot
                : endpoints[providerId].Coordinator.ResolveHealth(installation);
    }
}
