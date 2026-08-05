using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherProviderSwitchEndpoint(
    string ProviderId,
    ModManagementCoordinator Coordinator);

public sealed record LauncherProviderAtomicSwitchPreview(
    LauncherProviderSwitchPreview Configuration,
    ModOperationPreparation? Artifact,
    ModInstallationEvidence SourceInstallation)
{
    public string ConfirmationText => Configuration.ConfirmationText;
}

public sealed record LauncherProviderAtomicSwitchResult(
    LauncherProviderSelection Selection,
    ModInstalledArtifactState? InstalledArtifact,
    ConfigurationBackupReceipt? ConfigurationBackup,
    string Message);

public sealed record LauncherProviderAtomicSwitchRecoveryResult(
    bool IsSuccess,
    bool Changed,
    string Message);

public enum LauncherProviderAtomicSwitchPhase
{
    Prepared,
    ArtifactCommitting,
    ConfigurationCommitted,
    Completed,
    RollingBack,
    RolledBack,
    RecoveryRequired,
}

public sealed record LauncherProviderAtomicSwitchJournal(
    int SchemaVersion,
    string TransactionId,
    LauncherProviderAtomicSwitchPhase Phase,
    LauncherProviderSwitchPreview Preview,
    ConfigurationBackupReceipt? ConfigurationBackup,
    ModReleaseArtifact TargetArtifact,
    DateTimeOffset UpdatedAtUtc,
    string? Error = null);

/// <summary>
/// Coordinates a provider artifact replacement with the provider-selection and
/// TOML transaction. The target deployment owns the installation lease and
/// retains its exact DLL rollback copy until this transaction is complete.
/// </summary>
public sealed class LauncherProviderAtomicSwitchCoordinator
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly LauncherProviderSourceSwitchService configurationSwitch;
    private readonly Dictionary<string, ModManagementCoordinator> endpoints;
    private readonly string journalPath;
    private readonly LauncherOperationLock operationLock;
    private readonly TimeProvider timeProvider;

    public LauncherProviderAtomicSwitchCoordinator(
        LauncherProviderSourceSwitchService configurationSwitch,
        IEnumerable<LauncherProviderSwitchEndpoint> endpoints,
        string stateDirectory,
        TimeProvider? timeProvider = null)
    {
        this.configurationSwitch = configurationSwitch
            ?? throw new ArgumentNullException(nameof(configurationSwitch));
        ArgumentNullException.ThrowIfNull(endpoints);
        this.endpoints = endpoints.ToDictionary(
            endpoint => endpoint.ProviderId,
            endpoint => endpoint.Coordinator,
            StringComparer.Ordinal);
        if (this.endpoints.Count == 0)
        {
            throw new ArgumentException("At least one provider-switch endpoint is required.", nameof(endpoints));
        }
        if (this.endpoints.Any(pair => !string.Equals(pair.Key, pair.Value.ProviderId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("A provider-switch endpoint is bound to the wrong provider.", nameof(endpoints));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var normalizedStateDirectory = Path.GetFullPath(stateDirectory);
        journalPath = Path.Combine(normalizedStateDirectory, "provider-switch-journal.json");
        operationLock = new(Path.Combine(normalizedStateDirectory, "provider-switch"));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public LauncherProviderAtomicSwitchJournal? ReadJournal()
    {
        if (!File.Exists(journalPath))
        {
            return null;
        }
        using var stream = File.OpenRead(journalPath);
        var journal = JsonSerializer.Deserialize<LauncherProviderAtomicSwitchJournal>(
            stream,
            SerializerOptions)
            ?? throw new InvalidDataException("Provider-switch journal is empty.");
        if (journal.SchemaVersion != SchemaVersion
            || string.IsNullOrWhiteSpace(journal.TransactionId)
            || !endpoints.ContainsKey(journal.Preview.Target.ProviderId))
        {
            throw new InvalidDataException("Provider-switch journal identity is invalid.");
        }
        return journal;
    }

    public async Task<LauncherProviderAtomicSwitchPreview> PreviewAsync(
        string targetProviderId,
        string? targetReleaseChannelId,
        string gameDirectory,
        bool isGameRunning,
        string? configurationPath,
        CancellationToken cancellationToken = default)
    {
        var configuration = configurationSwitch.Preview(
            targetProviderId,
            targetReleaseChannelId,
            configurationPath);
        if (configuration.SourceResolutionState == LauncherProviderSelectionResolutionState.InvalidSelection)
        {
            throw new InvalidOperationException(
                "Repair the unreadable release-source selection before switching the installed artifact.");
        }
        if (!endpoints.TryGetValue(configuration.Source.ProviderId, out var sourceEndpoint))
        {
            throw new InvalidOperationException(
                $"The source provider '{configuration.Source.ProviderId}' has no installation endpoint.");
        }
        if (!endpoints.TryGetValue(configuration.Target.ProviderId, out var targetEndpoint))
        {
            throw new InvalidOperationException(
                $"The target provider '{configuration.Target.ProviderId}' has no installation endpoint.");
        }

        var sourceHealth = sourceEndpoint.CaptureHealth(gameDirectory, isGameRunning);
        var sourceInstallation = sourceHealth.Installation;
        if (sourceInstallation.State == ModInstallationEvidenceState.NotInstalled)
        {
            return new(configuration, Artifact: null, sourceInstallation);
        }
        if (sourceInstallation.State != ModInstallationEvidenceState.ManagedVerified
            || !string.Equals(
                sourceInstallation.InstalledProviderId,
                configuration.Source.ProviderId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected release source does not match the verified Mod Bridge-managed DLL. "
                + "Use the separate install, adoption, or repair flow first.");
        }
        var artifact = await targetEndpoint.PrepareProviderSwitchTargetAsync(
            gameDirectory,
            isGameRunning,
            sourceInstallation,
            cancellationToken).ConfigureAwait(false);
        return new(configuration, artifact, sourceInstallation);
    }

    public async Task<LauncherProviderAtomicSwitchResult> ExecuteAsync(
        LauncherProviderAtomicSwitchPreview preview,
        string confirmationText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            throw new InvalidOperationException("Another provider switch or recovery is already active.");
        }
        RejectIncompleteTransaction();
        if (preview.Artifact is null)
        {
            var selectionOnly = await configurationSwitch.ExecuteAsync(
                preview.Configuration,
                confirmationText,
                cancellationToken).ConfigureAwait(false);
            return new(
                selectionOnly.Selection,
                InstalledArtifact: null,
                selectionOnly.ConfigurationBackup,
                selectionOnly.Message);
        }
        if (!endpoints.TryGetValue(preview.Configuration.Target.ProviderId, out var targetEndpoint))
        {
            throw new InvalidOperationException("The reviewed target provider is no longer available.");
        }

        var prepared = await configurationSwitch.PrepareAsync(
            preview.Configuration,
            confirmationText,
            cancellationToken).ConfigureAwait(false);
        var journal = new LauncherProviderAtomicSwitchJournal(
            SchemaVersion,
            preview.Configuration.TransactionId,
            LauncherProviderAtomicSwitchPhase.Prepared,
            preview.Configuration,
            prepared.ConfigurationBackup,
            preview.Artifact.Artifact,
            timeProvider.GetUtcNow());
        Persist(journal);
        var participant = new ConfigurationCommitParticipant(
            this,
            configurationSwitch,
            prepared,
            preview.SourceInstallation,
            journal);
        ModDeploymentResult deployment;
        try
        {
            deployment = await targetEndpoint.ExecuteCoordinatedAsync(
                preview.Artifact,
                preview.Configuration.TransactionId,
                participant,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            participant.MarkCanceled();
            throw;
        }

        if (!deployment.IsSuccess || deployment.InstalledState is null)
        {
            participant.MarkDeploymentFailure(deployment);
            throw new InvalidOperationException(deployment.Message);
        }
        if (!string.Equals(
                deployment.InstalledState.ProviderId,
                preview.Configuration.Target.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                deployment.InstalledState.Sha256,
                preview.Artifact.Artifact.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            participant.MarkRecoveryRequired(
                "The coordinated switch committed but target artifact verification did not match the reviewed plan.");
            throw new InvalidOperationException(
                "The coordinated switch requires recovery because its final artifact identity did not match.");
        }

        var configurationResult = participant.ConfigurationResult
            ?? throw new InvalidOperationException(
                "The artifact committed without a configuration/provider commit receipt.");
        return new(
            configurationResult.Selection,
            deployment.InstalledState,
            configurationResult.ConfigurationBackup,
            $"Switched the managed DLL, release source, and TOML state to "
            + $"{preview.Configuration.TargetDisplayName}.");
    }

    public async Task<LauncherProviderAtomicSwitchRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            return new(false, false, "Another provider switch or recovery is already active.");
        }
        var journal = ReadJournal();
        if (journal is null
            || journal.Phase is LauncherProviderAtomicSwitchPhase.Completed
                or LauncherProviderAtomicSwitchPhase.RolledBack)
        {
            return new(true, false, "No incomplete provider-switch transaction was found.");
        }
        if (!endpoints.TryGetValue(journal.Preview.Target.ProviderId, out var targetEndpoint))
        {
            Persist(journal with
            {
                Phase = LauncherProviderAtomicSwitchPhase.RecoveryRequired,
                UpdatedAtUtc = timeProvider.GetUtcNow(),
                Error = "The target provider endpoint is no longer available.",
            });
            return new(false, false, "Provider-switch recovery requires the original target provider endpoint.");
        }

        var interruptedPhase = journal.Phase;
        journal = journal with
        {
            Phase = LauncherProviderAtomicSwitchPhase.RollingBack,
            UpdatedAtUtc = timeProvider.GetUtcNow(),
        };
        Persist(journal);
        if (interruptedPhase != LauncherProviderAtomicSwitchPhase.Prepared)
        {
            var artifactRollback = await targetEndpoint.RollBackCoordinatedAsync(
                journal.TransactionId,
                cancellationToken).ConfigureAwait(false);
            if (!artifactRollback.IsSuccess)
            {
                Persist(journal with
                {
                    Phase = LauncherProviderAtomicSwitchPhase.RecoveryRequired,
                    UpdatedAtUtc = timeProvider.GetUtcNow(),
                    Error = artifactRollback.Message,
                });
                return new(false, false, artifactRollback.Message);
            }
        }

        try
        {
            await configurationSwitch.RollBackAsync(
                new(journal.Preview, journal.ConfigurationBackup, TargetConfiguration: null),
                cancellationToken).ConfigureAwait(false);
            Persist(journal with
            {
                Phase = LauncherProviderAtomicSwitchPhase.RolledBack,
                UpdatedAtUtc = timeProvider.GetUtcNow(),
                Error = null,
            });
            return new(true, true, "The incomplete provider switch was restored to its exact prior state.");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException)
        {
            Persist(journal with
            {
                Phase = LauncherProviderAtomicSwitchPhase.RecoveryRequired,
                UpdatedAtUtc = timeProvider.GetUtcNow(),
                Error = exception.Message,
            });
            return new(false, false, $"Provider-switch recovery failed: {exception.Message}");
        }
    }

    private void RejectIncompleteTransaction()
    {
        var existing = ReadJournal();
        if (existing is not null
            && existing.Phase is not (LauncherProviderAtomicSwitchPhase.Completed
                or LauncherProviderAtomicSwitchPhase.RolledBack))
        {
            throw new InvalidOperationException(
                "An incomplete provider-switch transaction requires recovery before another switch.");
        }
    }

    private void Persist(LauncherProviderAtomicSwitchJournal journal)
    {
        var parent = Path.GetDirectoryName(journalPath)
            ?? throw new InvalidOperationException("Provider-switch journal path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporaryPath = Path.Combine(parent, $".provider-switch.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(journal, SerializerOptions));
            if (File.Exists(journalPath))
            {
                File.Replace(temporaryPath, journalPath, null, true);
            }
            else
            {
                File.Move(temporaryPath, journalPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed class ConfigurationCommitParticipant(
        LauncherProviderAtomicSwitchCoordinator owner,
        LauncherProviderSourceSwitchService configurationSwitch,
        PreparedLauncherProviderSwitch prepared,
        ModInstallationEvidence expectedSourceInstallation,
        LauncherProviderAtomicSwitchJournal initialJournal) : IModDeploymentCommitParticipant
    {
        private LauncherProviderAtomicSwitchJournal journal = initialJournal;

        public LauncherProviderSwitchResult? ConfigurationResult { get; private set; }

        public Task BeginAsync(
            ModDeploymentCommitContext context,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            var previous = context.PreviousInstalledState;
            if (expectedSourceInstallation.State != ModInstallationEvidenceState.ManagedVerified
                || previous is null
                || !string.Equals(
                    previous.ProviderId,
                    expectedSourceInstallation.InstalledProviderId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    previous.ReleaseChannelId,
                    expectedSourceInstallation.InstalledReleaseChannelId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    previous.RuntimeDistributionId,
                    expectedSourceInstallation.InstalledRuntimeDistributionId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    previous.Sha256,
                    expectedSourceInstallation.InstalledSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The installed source artifact changed after review. Review the provider switch again.");
            }
            Persist(LauncherProviderAtomicSwitchPhase.ArtifactCommitting);
            return Task.CompletedTask;
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            ConfigurationResult = await configurationSwitch.CommitAsync(prepared, cancellationToken)
                .ConfigureAwait(false);
            Persist(LauncherProviderAtomicSwitchPhase.ConfigurationCommitted);
        }

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Persist(LauncherProviderAtomicSwitchPhase.Completed);
            return Task.CompletedTask;
        }

        public async Task RollBackAsync(CancellationToken cancellationToken)
        {
            Persist(LauncherProviderAtomicSwitchPhase.RollingBack);
            try
            {
                await configurationSwitch.RollBackAsync(prepared, cancellationToken).ConfigureAwait(false);
                Persist(LauncherProviderAtomicSwitchPhase.RolledBack);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException)
            {
                Persist(LauncherProviderAtomicSwitchPhase.RecoveryRequired, exception.Message);
                throw;
            }
        }

        public void Persist(LauncherProviderAtomicSwitchPhase phase, string? error = null)
        {
            journal = journal with
            {
                Phase = phase,
                UpdatedAtUtc = owner.timeProvider.GetUtcNow(),
                Error = error,
            };
            owner.Persist(journal);
        }

        public void MarkDeploymentFailure(ModDeploymentResult result)
        {
            if (journal.Phase is LauncherProviderAtomicSwitchPhase.RolledBack
                or LauncherProviderAtomicSwitchPhase.RecoveryRequired)
            {
                return;
            }
            Persist(
                result.State == ModDeploymentResultState.RecoveryRequired
                    ? LauncherProviderAtomicSwitchPhase.RecoveryRequired
                    : LauncherProviderAtomicSwitchPhase.RolledBack,
                result.Message);
        }

        public void MarkCanceled()
        {
            if (journal.Phase is not (LauncherProviderAtomicSwitchPhase.RolledBack
                or LauncherProviderAtomicSwitchPhase.RecoveryRequired))
            {
                Persist(LauncherProviderAtomicSwitchPhase.RolledBack, "The switch was canceled.");
            }
        }

        public void MarkRecoveryRequired(string message) =>
            Persist(LauncherProviderAtomicSwitchPhase.RecoveryRequired, message);
    }
}
