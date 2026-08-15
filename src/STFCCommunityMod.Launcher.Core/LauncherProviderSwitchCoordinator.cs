using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherProviderSwitchEndpoint(
    string ProviderId,
    ModManagementCoordinator Coordinator,
    string? ReleaseChannelId = null)
{
    public LauncherProviderSelection Selection => new(
        ProviderId,
        ReleaseChannelId ?? Coordinator.ReleaseChannelId);
}

public sealed record LauncherProviderAtomicSwitchPreview(
    LauncherProviderSwitchPreview Configuration,
    ModOperationPreparation? Artifact,
    ModInstallationEvidence SourceInstallation,
    string? GameDirectory = null)
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
    ConfigurationCommitting,
}

public sealed record LauncherProviderAtomicSwitchJournal(
    int SchemaVersion,
    string TransactionId,
    LauncherProviderAtomicSwitchPhase Phase,
    LauncherProviderSwitchPreview Preview,
    ConfigurationBackupReceipt? ConfigurationBackup,
    ModReleaseArtifact? TargetArtifact,
    DateTimeOffset UpdatedAtUtc,
    string? Error = null);

/// <summary>
/// Coordinates a provider artifact replacement with the provider-selection and
/// TOML transaction. The target deployment owns the installation lease and
/// retains its exact DLL rollback copy until this transaction is complete.
/// </summary>
public sealed class LauncherProviderAtomicSwitchCoordinator
{
    private const int SchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly LauncherProviderSourceSwitchService configurationSwitch;
    private readonly Dictionary<LauncherProviderSelection, ModManagementCoordinator> endpoints;
    private readonly string journalPath;
    private readonly LauncherOperationLock providerSwitchLock;
    private readonly LauncherOperationLock rootOperationLock;
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
        var materializedEndpoints = endpoints.ToArray();
        this.endpoints = materializedEndpoints.ToDictionary(
            endpoint => endpoint.Selection,
            endpoint => endpoint.Coordinator,
            EqualityComparer<LauncherProviderSelection>.Default);
        if (this.endpoints.Count == 0)
        {
            throw new ArgumentException("At least one provider-switch endpoint is required.", nameof(endpoints));
        }
        if (this.endpoints.Any(pair =>
                !string.Equals(pair.Key.ProviderId, pair.Value.ProviderId, StringComparison.Ordinal)
                || !string.Equals(
                    pair.Key.ReleaseChannelId,
                    pair.Value.ReleaseChannelId,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException("A provider-switch endpoint is bound to the wrong provider.", nameof(endpoints));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var normalizedStateDirectory = Path.GetFullPath(stateDirectory);
        journalPath = Path.Combine(normalizedStateDirectory, "provider-switch-journal.json");
        providerSwitchLock = new(Path.Combine(normalizedStateDirectory, "provider-switch"));
        rootOperationLock = new(normalizedStateDirectory);
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
        if (journal.SchemaVersion is not (LegacySchemaVersion or SchemaVersion)
            || string.IsNullOrWhiteSpace(journal.TransactionId)
            || (journal.TargetArtifact is not null
                && !endpoints.ContainsKey(journal.Preview.Target)))
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
        var gameValidation = GameInstallValidator.Validate(gameDirectory);
        if (!gameValidation.IsValid)
        {
            throw new InvalidOperationException(gameValidation.Message);
        }
        var expectedConfigurationPath = Path.Combine(
            gameValidation.GameDirectory,
            "community_patch_settings.toml");
        if (!string.IsNullOrWhiteSpace(configurationPath)
            && !string.Equals(
                Path.GetFullPath(configurationPath),
                expectedConfigurationPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The provider-switch configuration path does not belong to the selected game installation.");
        }
        var configuration = configurationSwitch.Preview(
            targetProviderId,
            targetReleaseChannelId,
            expectedConfigurationPath);
        if (configuration.SourceResolutionState == LauncherProviderSelectionResolutionState.InvalidSelection)
        {
            throw new InvalidOperationException(
                "Repair the unreadable release-source selection before switching the installed artifact.");
        }
        if (!endpoints.TryGetValue(configuration.Source, out var sourceEndpoint))
        {
            throw new InvalidOperationException(
                $"The source provider/channel '{configuration.Source.ProviderId}/"
                + $"{configuration.Source.ReleaseChannelId}' has no installation endpoint.");
        }
        if (!endpoints.TryGetValue(configuration.Target, out var targetEndpoint))
        {
            throw new InvalidOperationException(
                $"The target provider/channel '{configuration.Target.ProviderId}/"
                + $"{configuration.Target.ReleaseChannelId}' has no installation endpoint.");
        }

        var sourceHealth = sourceEndpoint.CaptureHealth(gameValidation.GameDirectory, isGameRunning);
        var sourceInstallation = sourceHealth.Installation;
        if (sourceInstallation.State == ModInstallationEvidenceState.NotInstalled)
        {
            return new(configuration, Artifact: null, sourceInstallation, gameValidation.GameDirectory);
        }
        if (sourceInstallation.State != ModInstallationEvidenceState.ManagedVerified
            || !string.Equals(
                sourceInstallation.InstalledProviderId,
                configuration.Source.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                sourceInstallation.InstalledReleaseChannelId,
                configuration.Source.ReleaseChannelId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected release source does not match the verified Mod Bridge-managed DLL. "
                + "Use the separate install, adoption, or repair flow first.");
        }
        var artifact = await targetEndpoint.PrepareProviderSwitchTargetAsync(
            gameValidation.GameDirectory,
            isGameRunning,
            sourceInstallation,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                artifact.ProviderId,
                configuration.Target.ProviderId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The target provider endpoint returned an artifact for a different provider.");
        }
        return new(configuration, artifact, sourceInstallation, gameValidation.GameDirectory);
    }

    public async Task<LauncherProviderAtomicSwitchResult> ExecuteAsync(
        LauncherProviderAtomicSwitchPreview preview,
        string confirmationText,
        CancellationToken cancellationToken = default) =>
        await ExecuteCoreAsync(
            preview,
            confirmationText,
            candidateLease: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<LauncherProviderAtomicSwitchResult> ExecuteCandidateAsync(
        LauncherProviderAtomicSwitchPreview preview,
        ReviewedModArtifactCandidateLease candidateLease,
        string confirmationText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateLease);
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Artifact is null
            || candidateLease.Receipt.Artifact != preview.Artifact.Artifact
            || !string.Equals(
                candidateLease.Receipt.InstallationAttribution.ProviderId,
                preview.Configuration.Target.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                candidateLease.Receipt.InstallationAttribution.ReleaseChannelId,
                preview.Configuration.Target.ReleaseChannelId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The exact candidate does not match the reviewed provider-switch target.");
        }
        return await ExecuteCoreAsync(
            preview,
            confirmationText,
            candidateLease,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LauncherProviderAtomicSwitchResult> ExecuteCoreAsync(
        LauncherProviderAtomicSwitchPreview preview,
        string confirmationText,
        ReviewedModArtifactCandidateLease? candidateLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ValidateAtomicPreviewScope(preview);
        await using var providerSwitchLease = await providerSwitchLock.TryAcquireAsync(cancellationToken);
        if (providerSwitchLease is null)
        {
            throw new InvalidOperationException("Another provider switch or recovery is already active.");
        }
        RejectIncompleteTransaction();
        await using var rootLease = await rootOperationLock.TryAcquireAsync(cancellationToken);
        if (rootLease is null)
        {
            throw new InvalidOperationException(
                "Another Mod Bridge mutation is already active. Try the provider switch again after it finishes.");
        }
        if (preview.Artifact is null)
        {
            var preparedConfiguration = await configurationSwitch.PrepareAsync(
                preview.Configuration,
                confirmationText,
                cancellationToken).ConfigureAwait(false);
            var configurationJournal = new LauncherProviderAtomicSwitchJournal(
                SchemaVersion,
                preview.Configuration.TransactionId,
                LauncherProviderAtomicSwitchPhase.Prepared,
                preparedConfiguration.Preview,
                preparedConfiguration.ConfigurationBackup,
                TargetArtifact: null,
                timeProvider.GetUtcNow());
            Persist(configurationJournal);
            try
            {
                configurationJournal = configurationJournal with
                {
                    Phase = LauncherProviderAtomicSwitchPhase.ConfigurationCommitting,
                    UpdatedAtUtc = timeProvider.GetUtcNow(),
                };
                Persist(configurationJournal);
                var configurationOnly = await configurationSwitch.CommitAsync(
                    preparedConfiguration,
                    cancellationToken).ConfigureAwait(false);
                configurationJournal = configurationJournal with
                {
                    Phase = LauncherProviderAtomicSwitchPhase.ConfigurationCommitted,
                    UpdatedAtUtc = timeProvider.GetUtcNow(),
                };
                Persist(configurationJournal);
                Persist(configurationJournal with
                {
                    Phase = LauncherProviderAtomicSwitchPhase.Completed,
                    UpdatedAtUtc = timeProvider.GetUtcNow(),
                });
                return new(
                    configurationOnly.Selection,
                    InstalledArtifact: null,
                    configurationOnly.ConfigurationBackup,
                    configurationOnly.Message);
            }
            catch (Exception switchException) when (
                switchException is OperationCanceledException
                    or IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException)
            {
                configurationJournal = configurationJournal with
                {
                    Phase = LauncherProviderAtomicSwitchPhase.RollingBack,
                    UpdatedAtUtc = timeProvider.GetUtcNow(),
                    Error = switchException.Message,
                };
                Persist(configurationJournal);
                try
                {
                    await configurationSwitch.RollBackAsync(
                        preparedConfiguration,
                        CancellationToken.None).ConfigureAwait(false);
                    Persist(configurationJournal with
                    {
                        Phase = LauncherProviderAtomicSwitchPhase.RolledBack,
                        UpdatedAtUtc = timeProvider.GetUtcNow(),
                    });
                }
                catch (Exception rollbackException) when (
                    rollbackException is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or InvalidOperationException)
                {
                    Persist(configurationJournal with
                    {
                        Phase = LauncherProviderAtomicSwitchPhase.RecoveryRequired,
                        UpdatedAtUtc = timeProvider.GetUtcNow(),
                        Error = rollbackException.Message,
                    });
                    throw new InvalidOperationException(
                        "The configuration-only provider switch failed and rollback requires recovery.",
                        new AggregateException(switchException, rollbackException));
                }
                throw;
            }
        }
        if (!string.Equals(
                preview.Artifact.ProviderId,
                preview.Configuration.Target.ProviderId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The reviewed target artifact does not match the provider-switch target.");
        }
        if (!endpoints.TryGetValue(preview.Configuration.Target, out var targetEndpoint))
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
            deployment = candidateLease is null
                ? await targetEndpoint.ExecuteCoordinatedCoreAsync(
                    preview.Artifact,
                    preview.Configuration.TransactionId,
                    participant,
                    rootLease,
                    cancellationToken).ConfigureAwait(false)
                : await targetEndpoint.ExecuteCandidateCoordinatedCoreAsync(
                    preview.Artifact,
                    candidateLease,
                    preview.Configuration.TransactionId,
                    participant,
                    rootLease,
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
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                deployment.InstalledState.ReleaseChannelId,
                preview.Configuration.Target.ReleaseChannelId,
                StringComparison.Ordinal))
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
        await using var providerSwitchLease = await providerSwitchLock.TryAcquireAsync(cancellationToken);
        if (providerSwitchLease is null)
        {
            return new(false, false, "Another provider switch or recovery is already active.");
        }
        await using var rootLease = await rootOperationLock.TryAcquireAsync(cancellationToken);
        if (rootLease is null)
        {
            return new(false, false, "Another Mod Bridge mutation is already active.");
        }
        var journal = ReadJournal();
        if (journal is null
            || journal.Phase is LauncherProviderAtomicSwitchPhase.Completed
                or LauncherProviderAtomicSwitchPhase.RolledBack)
        {
            return new(true, false, "No incomplete provider-switch transaction was found.");
        }
        ModManagementCoordinator? targetEndpoint = null;
        if (journal.TargetArtifact is not null
            && !endpoints.TryGetValue(journal.Preview.Target, out targetEndpoint))
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
        if (journal.TargetArtifact is not null
            && interruptedPhase != LauncherProviderAtomicSwitchPhase.Prepared)
        {
            var artifactRollback = await targetEndpoint!.RollBackCoordinatedCoreAsync(
                journal.TransactionId,
                rootLease,
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

    private static void ValidateAtomicPreviewScope(LauncherProviderAtomicSwitchPreview preview)
    {
        if (string.IsNullOrWhiteSpace(preview.GameDirectory))
        {
            throw new InvalidDataException(
                "The provider-switch preview is missing its exact game installation.");
        }
        var validation = GameInstallValidator.Validate(preview.GameDirectory);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }
        var expectedConfigurationPath = Path.Combine(
            validation.GameDirectory,
            "community_patch_settings.toml");
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                preview.Configuration.ConfigurationPath,
                expectedConfigurationPath,
                pathComparison)
            || (preview.Artifact is not null
                && !string.Equals(
                    Path.GetFullPath(preview.Artifact.GameDirectory),
                    validation.GameDirectory,
                    pathComparison)))
        {
            throw new InvalidOperationException(
                "The provider-switch preview is bound to a different game installation or configuration path.");
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
