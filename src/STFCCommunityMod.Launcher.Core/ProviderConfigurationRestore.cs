using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public enum ProviderConfigurationCompatibilityState
{
    Compatible,
    Attention,
    Unknown,
    Blocked,
    Unreadable,
}

public sealed record ProviderConfigurationHistoryEntry(
    ConfigurationBackupReceipt Receipt,
    string DestinationPath,
    ProviderConfigurationCompatibilityState CompatibilityState,
    string CompatibilitySummary,
    string? CatalogId,
    Version? CatalogVersion)
{
    public bool CanRestore =>
        CompatibilityState is ProviderConfigurationCompatibilityState.Compatible
            or ProviderConfigurationCompatibilityState.Attention
            or ProviderConfigurationCompatibilityState.Unknown;
}

public sealed record ProviderConfigurationRestorePreview(
    string TransactionId,
    LauncherProviderSelection Selection,
    ConfigurationBackupReceipt Backup,
    string DestinationPath,
    ConfigurationDocumentRevision ExpectedLiveRevision,
    ProviderConfigurationCompatibilityState CompatibilityState,
    string CompatibilitySummary,
    string ConfirmationText);

public enum ProviderConfigurationRestoreResultState
{
    Succeeded,
    NoIncompleteRestore,
    Busy,
    Conflict,
    Blocked,
    Failed,
    RecoveryRequired,
}

public sealed record ProviderConfigurationRestoreResult(
    ProviderConfigurationRestoreResultState State,
    string Message,
    ConfigurationBackupReceipt? PreRestoreBackup = null,
    ConfigurationBackupReceipt? RestoredBackup = null)
{
    public bool IsSuccess =>
        State is ProviderConfigurationRestoreResultState.Succeeded
            or ProviderConfigurationRestoreResultState.NoIncompleteRestore;
}

public enum ProviderConfigurationRestorePhase
{
    Prepared,
    ConfigurationCommitted,
    BackupMarkedRestored,
    Completed,
    Failed,
    RecoveryRequired,
}

public sealed record ProviderConfigurationRestoreJournal(
    int SchemaVersion,
    ProviderConfigurationRestorePhase Phase,
    ProviderConfigurationRestorePreview Preview,
    ConfigurationBackupReceipt? PreRestoreBackup,
    DateTimeOffset UpdatedAtUtc,
    string? Error = null);

/// <summary>
/// Restores one verified backup from the active provider partition through the
/// shared configuration transaction. The durable journal lets a later process
/// finish receipt publication without guessing whether the TOML replacement won.
/// </summary>
public sealed class ProviderConfigurationRestoreCoordinator
{
    private const int SchemaVersion = 1;
    private const long MaximumConfigurationBytes = 8 * 1024 * 1024;
    private const long MaximumJournalBytes = 1024 * 1024;
    private const string ConfigurationFileName = "community_patch_settings.toml";
    private const string RestoreReason = "manual-restore";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ProviderScopedConfigurationBackupStore backupStore;
    private readonly LauncherDistributionProviderCatalog providerCatalog;
    private readonly ILauncherProviderSelectionStore selectionStore;
    private readonly LauncherProviderSelection activeSelection;
    private readonly LauncherConfigurationDiagnosisEvidence diagnosisEvidence;
    private readonly Func<string?> configurationPathProvider;
    private readonly IGameProcessInspector gameProcessInspector;
    private readonly LauncherOperationLock mutationAdmission;
    private readonly TimeProvider timeProvider;
    private readonly IAtomicTomlMutationAdmission? atomicTomlMutationAdmission;
    private readonly string journalPath;
    private readonly Func<ProviderConfigurationRestorePhase, CancellationToken, ValueTask>?
        checkpoint;

    public ProviderConfigurationRestoreCoordinator(
        ProviderScopedConfigurationBackupStore backupStore,
        LauncherDistributionProviderCatalog providerCatalog,
        ILauncherProviderSelectionStore selectionStore,
        LauncherProviderSelection activeSelection,
        LauncherConfigurationDiagnosisEvidence diagnosisEvidence,
        string stateDirectory,
        Func<string?> configurationPathProvider,
        IGameProcessInspector? gameProcessInspector = null,
        TimeProvider? timeProvider = null)
        : this(
            backupStore,
            providerCatalog,
            selectionStore,
            activeSelection,
            diagnosisEvidence,
            stateDirectory,
            configurationPathProvider,
            gameProcessInspector,
            timeProvider,
            checkpoint: null,
            atomicTomlMutationAdmission: null)
    {
    }

    internal ProviderConfigurationRestoreCoordinator(
        ProviderScopedConfigurationBackupStore backupStore,
        LauncherDistributionProviderCatalog providerCatalog,
        ILauncherProviderSelectionStore selectionStore,
        LauncherProviderSelection activeSelection,
        LauncherConfigurationDiagnosisEvidence diagnosisEvidence,
        string stateDirectory,
        Func<string?> configurationPathProvider,
        IGameProcessInspector? gameProcessInspector,
        TimeProvider? timeProvider,
        Func<ProviderConfigurationRestorePhase, CancellationToken, ValueTask>? checkpoint,
        IAtomicTomlMutationAdmission? atomicTomlMutationAdmission = null)
    {
        this.backupStore = backupStore ?? throw new ArgumentNullException(nameof(backupStore));
        this.providerCatalog = providerCatalog ?? throw new ArgumentNullException(nameof(providerCatalog));
        this.selectionStore = selectionStore ?? throw new ArgumentNullException(nameof(selectionStore));
        this.activeSelection = activeSelection ?? throw new ArgumentNullException(nameof(activeSelection));
        this.diagnosisEvidence = diagnosisEvidence
            ?? throw new ArgumentNullException(nameof(diagnosisEvidence));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ValidateEvidenceBinding(activeSelection, diagnosisEvidence, providerCatalog);
        this.configurationPathProvider = configurationPathProvider
            ?? throw new ArgumentNullException(nameof(configurationPathProvider));
        this.gameProcessInspector = gameProcessInspector ?? new SystemGameProcessInspector();
        mutationAdmission = new LauncherOperationLock(stateDirectory);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.checkpoint = checkpoint;
        this.atomicTomlMutationAdmission = atomicTomlMutationAdmission;
        journalPath = Path.Combine(
            Path.GetFullPath(stateDirectory),
            "configuration-restore-journal.json");
    }

    public IReadOnlyList<ProviderConfigurationHistoryEntry> LoadHistory()
    {
        var target = ResolveSelectedTarget();
        EnsureActiveSelection();
        return backupStore.List(target.GameDirectory, activeSelection.ProviderId)
            .Select(receipt => Inspect(receipt, target.ConfigurationPath))
            .ToArray();
    }

    public ProviderConfigurationRestorePreview Preview(string backupId) =>
        BuildPreview(
            backupId,
            Guid.NewGuid().ToString("N"),
            rejectIncomplete: true);

    public ProviderConfigurationRestoreJournal? ReadJournal()
    {
        if (!File.Exists(journalPath))
        {
            return null;
        }
        if (new FileInfo(journalPath).Length > MaximumJournalBytes)
        {
            throw new InvalidDataException("Configuration restore journal is too large.");
        }
        using var stream = File.OpenRead(journalPath);
        var journal = JsonSerializer.Deserialize<ProviderConfigurationRestoreJournal>(
            stream,
            JsonOptions)
            ?? throw new InvalidDataException("Configuration restore journal is empty.");
        if (journal.SchemaVersion != SchemaVersion
            || !Enum.IsDefined(journal.Phase)
            || journal.Preview is null
            || journal.Preview.Selection is null
            || journal.Preview.Backup is null
            || journal.Preview.ExpectedLiveRevision is null
            || !Guid.TryParseExact(journal.Preview.TransactionId, "N", out _)
            || journal.Preview.Selection != activeSelection
            || !string.Equals(
                journal.Preview.Backup.ProviderId,
                activeSelection.ProviderId,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(journal.Preview.DestinationPath)
            || !string.Equals(
                journal.Preview.ConfirmationText,
                activeSelection.ProviderId,
                StringComparison.Ordinal)
            || !Enum.IsDefined(journal.Preview.CompatibilityState)
            || (journal.Phase is ProviderConfigurationRestorePhase.ConfigurationCommitted
                    or ProviderConfigurationRestorePhase.BackupMarkedRestored
                    or ProviderConfigurationRestorePhase.Completed
                && journal.PreRestoreBackup is null))
        {
            throw new InvalidDataException("Configuration restore journal identity is invalid.");
        }
        return journal;
    }

    public async Task<ProviderConfigurationRestoreResult> ExecuteAsync(
        ProviderConfigurationRestorePreview preview,
        string confirmationText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!Guid.TryParseExact(preview.TransactionId, "N", out _))
        {
            throw new InvalidDataException("Configuration restore transaction identity is invalid.");
        }
        var canonical = BuildPreview(
            preview.Backup.BackupId,
            preview.TransactionId,
            rejectIncomplete: true);
        VerifyPreview(preview, canonical);
        if (!string.Equals(confirmationText, activeSelection.ProviderId, StringComparison.Ordinal))
        {
            return new(
                ProviderConfigurationRestoreResultState.Blocked,
                $"Type '{activeSelection.ProviderId}' to confirm this configuration restore.");
        }

        await using var lease = await mutationAdmission.TryAcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            return new(
                ProviderConfigurationRestoreResultState.Busy,
                "Another Mod Bridge change is active. Nothing was restored; try again after it finishes.");
        }
        canonical = BuildPreview(
            preview.Backup.BackupId,
            preview.TransactionId,
            rejectIncomplete: true);
        VerifyPreview(preview, canonical);
        var target = ResolveTarget(canonical.DestinationPath);
        EnsureGameClosed(target.GameDirectory);
        var desiredContents = backupStore.Read(
            target.GameDirectory,
            activeSelection.ProviderId,
            canonical.Backup.BackupId);
        var baselineContents = ReadConfiguration(target.ConfigurationPath);
        var journal = new ProviderConfigurationRestoreJournal(
            SchemaVersion,
            ProviderConfigurationRestorePhase.Prepared,
            canonical,
            PreRestoreBackup: null,
            timeProvider.GetUtcNow());
        Persist(journal);
        await ObserveCheckpointAsync(
            ProviderConfigurationRestorePhase.Prepared,
            cancellationToken).ConfigureAwait(false);

        try
        {
            var transactionIdentity = RestoreIdentity(canonical.TransactionId);
            var mutationBackup = new ProviderScopedConfigurationMutationBackup(
                backupStore,
                activeSelection.ProviderId,
                transactionIdentity,
                RestoreReason,
                pinnedBackupId: canonical.Backup.BackupId);
            var repository = new TomlConfigurationRepository(
                store: atomicTomlMutationAdmission is null
                    ? new AtomicTomlStore(mutationBackup)
                    : new AtomicTomlStore(
                        mutationBackup,
                        beforeReplace: null,
                        retainAdjacentBackup: false,
                        mutationAdmission: atomicTomlMutationAdmission));
            EnsureActiveSelection();
            var selectedBeforeCommit = ResolveSelectedTarget();
            if (!PathEquals(selectedBeforeCommit.ConfigurationPath, target.ConfigurationPath))
            {
                throw new InvalidOperationException(
                    "The selected game installation changed before configuration restore commit.");
            }
            EnsureGameClosed(target.GameDirectory);
            var commit = await repository.CommitDocumentAsync(
                new(
                    target.ConfigurationPath,
                    canonical.ExpectedLiveRevision,
                    baselineContents,
                    desiredContents),
                cancellationToken).ConfigureAwait(false);
            if (!commit.IsSuccess || commit.State == AtomicTomlWriteState.NoChange)
            {
                var message = commit.State == AtomicTomlWriteState.NoChange
                    ? "That protected history entry already matches the active configuration."
                    : commit.ValidationError?.Message
                        ?? commit.Error
                        ?? "The configuration restore could not be committed.";
                Persist(journal with
                {
                    Phase = ProviderConfigurationRestorePhase.Failed,
                    UpdatedAtUtc = timeProvider.GetUtcNow(),
                    Error = message,
                });
                return new(MapFailure(commit.State), message, commit.BackupReceipt);
            }
            if (commit.BackupReceipt is null
                || !string.Equals(
                    commit.BackupReceipt.ReleaseIdentity,
                    transactionIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The configuration restore did not return its verified pre-restore backup receipt.");
            }

            journal = journal with
            {
                Phase = ProviderConfigurationRestorePhase.ConfigurationCommitted,
                PreRestoreBackup = commit.BackupReceipt,
                UpdatedAtUtc = timeProvider.GetUtcNow(),
            };
            Persist(journal);
            await ObserveCheckpointAsync(
                ProviderConfigurationRestorePhase.ConfigurationCommitted,
                cancellationToken).ConfigureAwait(false);
            EnsureActiveSelection();
            var selectedAfterCommit = ResolveSelectedTarget();
            if (!PathEquals(selectedAfterCommit.ConfigurationPath, target.ConfigurationPath))
            {
                throw new InvalidOperationException(
                    "The selected game installation changed after configuration restore commit.");
            }
            var restored = await backupStore.MarkRestoredAsync(
                target.GameDirectory,
                activeSelection.ProviderId,
                canonical.Backup.BackupId,
                canonical.TransactionId,
                cancellationToken).ConfigureAwait(false);
            journal = journal with
            {
                Phase = ProviderConfigurationRestorePhase.BackupMarkedRestored,
                UpdatedAtUtc = timeProvider.GetUtcNow(),
            };
            Persist(journal);
            await ObserveCheckpointAsync(
                ProviderConfigurationRestorePhase.BackupMarkedRestored,
                cancellationToken).ConfigureAwait(false);
            journal = journal with
            {
                Phase = ProviderConfigurationRestorePhase.Completed,
                UpdatedAtUtc = timeProvider.GetUtcNow(),
                Error = null,
            };
            Persist(journal);
            await ObserveCheckpointAsync(
                ProviderConfigurationRestorePhase.Completed,
                cancellationToken).ConfigureAwait(false);
            return new(
                ProviderConfigurationRestoreResultState.Succeeded,
                $"Restored the selected {activeSelection.ProviderId} configuration history entry.",
                commit.BackupReceipt,
                restored);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkInterrupted(journal, desiredContents, "The configuration restore was interrupted.");
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or CryptographicException)
        {
            var interruptedPhase = MarkInterrupted(journal, desiredContents, exception.Message);
            return new(
                interruptedPhase == ProviderConfigurationRestorePhase.Failed
                    ? ProviderConfigurationRestoreResultState.Failed
                    : ProviderConfigurationRestoreResultState.RecoveryRequired,
                interruptedPhase == ProviderConfigurationRestorePhase.Failed
                    ? "The configuration restore failed before changing the active TOML."
                    : "The configuration restore did not finish cleanly. Open Configuration history again to recover.",
                journal.PreRestoreBackup);
        }
    }

    public async Task<ProviderConfigurationRestoreResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var journal = ReadJournal();
        if (journal is null
            || journal.Phase is ProviderConfigurationRestorePhase.Completed
                or ProviderConfigurationRestorePhase.Failed)
        {
            return new(
                ProviderConfigurationRestoreResultState.NoIncompleteRestore,
                "No incomplete configuration restore was found.");
        }
        await using var lease = await mutationAdmission.TryAcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            return new(
                ProviderConfigurationRestoreResultState.Busy,
                "Another Mod Bridge change is active. Configuration restore recovery will retry later.");
        }

        EnsureActiveSelection();
        var target = ResolveSelectedTarget();
        if (!PathEquals(target.ConfigurationPath, journal.Preview.DestinationPath))
        {
            throw new InvalidOperationException(
                "Select the game installation named in the interrupted restore before recovering it.");
        }
        EnsureGameClosed(target.GameDirectory);
        var verifiedSourceBackup = backupStore.List(
                target.GameDirectory,
                activeSelection.ProviderId)
            .SingleOrDefault(receipt => string.Equals(
                receipt.BackupId,
                journal.Preview.Backup.BackupId,
                StringComparison.Ordinal));
        if (verifiedSourceBackup is null
            || !SourceReceiptMatchesJournal(journal, verifiedSourceBackup))
        {
            const string changed =
                "The selected restore backup receipt is missing or changed; no recovery bookkeeping was applied.";
            Persist(journal with
            {
                Phase = ProviderConfigurationRestorePhase.RecoveryRequired,
                UpdatedAtUtc = timeProvider.GetUtcNow(),
                Error = changed,
            });
            return new(ProviderConfigurationRestoreResultState.RecoveryRequired, changed);
        }
        var desiredContents = backupStore.Read(
            target.GameDirectory,
            activeSelection.ProviderId,
            journal.Preview.Backup.BackupId);
        var liveContents = ReadConfiguration(target.ConfigurationPath);
        var liveRevision = ConfigurationDocumentRevision.FromContents(liveContents);
        if (liveRevision == journal.Preview.ExpectedLiveRevision)
        {
            Persist(journal with
            {
                Phase = ProviderConfigurationRestorePhase.Failed,
                UpdatedAtUtc = timeProvider.GetUtcNow(),
                Error = "The interrupted restore made no configuration change.",
            });
            return new(
                ProviderConfigurationRestoreResultState.NoIncompleteRestore,
                "The interrupted restore made no configuration change.");
        }
        if (!liveContents.AsSpan().SequenceEqual(desiredContents))
        {
            const string conflict =
                "The active configuration changed after the interrupted restore. Its current bytes were preserved.";
            Persist(journal with
            {
                Phase = ProviderConfigurationRestorePhase.RecoveryRequired,
                UpdatedAtUtc = timeProvider.GetUtcNow(),
                Error = conflict,
            });
            return new(ProviderConfigurationRestoreResultState.RecoveryRequired, conflict);
        }

        var transactionIdentity = RestoreIdentity(journal.Preview.TransactionId);
        var preRestoreCandidates = backupStore.List(
                target.GameDirectory,
                activeSelection.ProviderId)
            .Where(receipt =>
                string.Equals(receipt.Reason, RestoreReason, StringComparison.Ordinal)
                && string.Equals(
                    receipt.ReleaseIdentity,
                    transactionIdentity,
                    StringComparison.Ordinal)
                && string.Equals(
                    receipt.ContentSha256,
                    journal.Preview.ExpectedLiveRevision.Sha256,
                    StringComparison.Ordinal))
            .ToArray();
        var verifiedPreRestoreBackup = journal.PreRestoreBackup is null
            ? preRestoreCandidates.FirstOrDefault()
            : preRestoreCandidates.SingleOrDefault(receipt => receipt == journal.PreRestoreBackup);
        if (verifiedPreRestoreBackup is null)
        {
            const string missing =
                "The restored TOML is present, but its verified pre-restore backup receipt is missing or changed.";
            Persist(journal with
            {
                Phase = ProviderConfigurationRestorePhase.RecoveryRequired,
                UpdatedAtUtc = timeProvider.GetUtcNow(),
                Error = missing,
            });
            return new(ProviderConfigurationRestoreResultState.RecoveryRequired, missing);
        }
        var preRestoreContents = backupStore.Read(
            target.GameDirectory,
            activeSelection.ProviderId,
            verifiedPreRestoreBackup.BackupId);
        if (ConfigurationDocumentRevision.FromContents(preRestoreContents)
            != journal.Preview.ExpectedLiveRevision)
        {
            const string changed =
                "The restored TOML is present, but its pre-restore backup no longer matches the reviewed live revision.";
            Persist(journal with
            {
                Phase = ProviderConfigurationRestorePhase.RecoveryRequired,
                UpdatedAtUtc = timeProvider.GetUtcNow(),
                Error = changed,
            });
            return new(ProviderConfigurationRestoreResultState.RecoveryRequired, changed);
        }

        var restored = await backupStore.MarkRestoredAsync(
            target.GameDirectory,
            activeSelection.ProviderId,
            journal.Preview.Backup.BackupId,
            journal.Preview.TransactionId,
            cancellationToken).ConfigureAwait(false);
        Persist(journal with
        {
            Phase = ProviderConfigurationRestorePhase.Completed,
            PreRestoreBackup = verifiedPreRestoreBackup,
            UpdatedAtUtc = timeProvider.GetUtcNow(),
            Error = null,
        });
        return new(
            ProviderConfigurationRestoreResultState.Succeeded,
            "Finished the interrupted configuration restore and verified its receipts.",
            verifiedPreRestoreBackup,
            restored);
    }

    private ProviderConfigurationRestorePreview BuildPreview(
        string backupId,
        string transactionId,
        bool rejectIncomplete)
    {
        if (rejectIncomplete)
        {
            RejectIncompleteRestore();
        }
        EnsureActiveSelection();
        var target = ResolveSelectedTarget();
        EnsureGameClosed(target.GameDirectory);
        var receipt = backupStore.List(target.GameDirectory, activeSelection.ProviderId)
            .SingleOrDefault(candidate => string.Equals(
                candidate.BackupId,
                backupId,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The selected history entry no longer belongs to the active provider and installation.");
        var inspection = Inspect(receipt, target.ConfigurationPath);
        if (!inspection.CanRestore)
        {
            throw new InvalidOperationException(inspection.CompatibilitySummary);
        }
        var liveContents = ReadConfiguration(target.ConfigurationPath);
        if (string.Equals(
                ConfigurationDocumentRevision.FromContents(liveContents).Sha256,
                receipt.ContentSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "That history entry already matches the active configuration.");
        }
        return new(
            transactionId,
            activeSelection,
            receipt,
            target.ConfigurationPath,
            ConfigurationDocumentRevision.FromContents(liveContents),
            inspection.CompatibilityState,
            inspection.CompatibilitySummary,
            activeSelection.ProviderId);
    }

    private ProviderConfigurationHistoryEntry Inspect(
        ConfigurationBackupReceipt receipt,
        string destinationPath)
    {
        try
        {
            var gameDirectory = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidDataException("The configuration path has no game directory.");
            var contents = backupStore.Read(
                gameDirectory,
                activeSelection.ProviderId,
                receipt.BackupId);
            var load = SparseTomlDocument.Load(contents, out var document);
            var validation = load.IsValid && document is not null
                ? document.ValidateForMutation()
                : load;
            if (!validation.IsValid)
            {
                return new(
                    receipt,
                    destinationPath,
                    ProviderConfigurationCompatibilityState.Blocked,
                    "This protected entry is not safe for the conservative TOML restore path.",
                    diagnosisEvidence.Catalog?.Identity.CatalogId,
                    diagnosisEvidence.Catalog?.Identity.CatalogVersion);
            }
            if (diagnosisEvidence.CapabilityStatus != LauncherProviderCapabilityStatus.Supported
                || diagnosisEvidence.Catalog is null)
            {
                return new(
                    receipt,
                    destinationPath,
                    ProviderConfigurationCompatibilityState.Unknown,
                    "The bytes are intact and parser-safe; exact provider compatibility evidence is unavailable.",
                    CatalogId: null,
                    CatalogVersion: null);
            }

            var report = new ConfigurationHealthAnalyzer(timeProvider).Analyze(
                new ConfigurationDocumentSnapshot(destinationPath, contents),
                diagnosisEvidence);
            var blocking = report.Findings.Count(finding =>
                finding.Confidence == ConfigurationDiagnosisConfidence.Established
                && finding.Severity is ConfigurationDiagnosisSeverity.Error
                    or ConfigurationDiagnosisSeverity.Unknown);
            if (blocking > 0)
            {
                return new(
                    receipt,
                    destinationPath,
                    ProviderConfigurationCompatibilityState.Blocked,
                    $"The exact provider catalog reports {blocking} blocking compatibility condition(s).",
                    diagnosisEvidence.Catalog.Identity.CatalogId,
                    diagnosisEvidence.Catalog.Identity.CatalogVersion);
            }
            // Unknown entries are safe to preserve, but restoring them still deserves a warning.
            var attention = report.Findings.Count(finding =>
                finding.Severity is ConfigurationDiagnosisSeverity.Attention
                    or ConfigurationDiagnosisSeverity.Unknown
                || finding.Code is "CONFIG_UNKNOWN_KEY" or "CONFIG_UNKNOWN_TABLE");
            return new(
                receipt,
                destinationPath,
                attention == 0
                    ? ProviderConfigurationCompatibilityState.Compatible
                    : ProviderConfigurationCompatibilityState.Attention,
                attention == 0
                    ? "Compatible with the exact active provider catalog."
                    : $"Restorable with {attention} preserved compatibility warning(s).",
                diagnosisEvidence.Catalog.Identity.CatalogId,
                diagnosisEvidence.Catalog.Identity.CatalogVersion);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or CryptographicException)
        {
            return new(
                receipt,
                destinationPath,
                ProviderConfigurationCompatibilityState.Unreadable,
                "This history entry could not be verified and will not be restored.",
                CatalogId: null,
                CatalogVersion: null);
        }
    }

    private ProviderConfigurationRestorePhase MarkInterrupted(
        ProviderConfigurationRestoreJournal journal,
        byte[] desiredContents,
        string error)
    {
        var phase = ProviderConfigurationRestorePhase.RecoveryRequired;
        try
        {
            var live = ReadConfiguration(journal.Preview.DestinationPath);
            if (ConfigurationDocumentRevision.FromContents(live)
                == journal.Preview.ExpectedLiveRevision)
            {
                phase = ProviderConfigurationRestorePhase.Failed;
            }
            else if (!live.AsSpan().SequenceEqual(desiredContents))
            {
                error += " The current TOML differs from both reviewed revisions and was preserved.";
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error += " The live TOML could not be classified safely.";
        }
        Persist(journal with
        {
            Phase = phase,
            UpdatedAtUtc = timeProvider.GetUtcNow(),
            Error = error,
        });
        return phase;
    }

    private void RejectIncompleteRestore()
    {
        var journal = ReadJournal();
        if (journal is not null
            && journal.Phase is not (ProviderConfigurationRestorePhase.Completed
                or ProviderConfigurationRestorePhase.Failed))
        {
            throw new InvalidOperationException(
                "An interrupted configuration restore must be recovered before another history action.");
        }
    }

    private void EnsureActiveSelection()
    {
        LauncherProviderSelectionResolution resolution;
        try
        {
            resolution = LauncherProviderSelectionResolver.Resolve(
                providerCatalog,
                selectionStore.Load());
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException)
        {
            throw new InvalidOperationException(
                "The active release source cannot be verified for configuration history.",
                exception);
        }
        if (resolution.State == LauncherProviderSelectionResolutionState.InvalidSelection
            || resolution.Selection != activeSelection)
        {
            throw new InvalidOperationException(
                "Configuration history belongs to a different active release source. Reopen Settings.");
        }
    }

    private void EnsureGameClosed(string gameDirectory)
    {
        if (gameProcessInspector.Inspect(gameDirectory) != GameProcessInspectionState.NotRunning)
        {
            throw new InvalidOperationException(
                "Close Star Trek Fleet Command in this installation before restoring configuration history.");
        }
    }

    private static void ValidateEvidenceBinding(
        LauncherProviderSelection selection,
        LauncherConfigurationDiagnosisEvidence evidence,
        LauncherDistributionProviderCatalog providerCatalog)
    {
        if (!string.Equals(evidence.ProviderId, selection.ProviderId, StringComparison.Ordinal)
            || !string.Equals(evidence.ChannelId, selection.ReleaseChannelId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Configuration history evidence is bound to another provider or release channel.",
                nameof(evidence));
        }
        if (evidence.Catalog is null)
        {
            return;
        }
        var provider = providerCatalog.GetProvider(selection.ProviderId);
        var trackMatches = string.Equals(
                evidence.Catalog.Identity.TrackId,
                selection.ReleaseChannelId,
                StringComparison.Ordinal)
            || (string.Equals(
                    evidence.Catalog.Identity.TrackId,
                    "unversioned",
                    StringComparison.Ordinal)
                && string.Equals(
                    selection.ReleaseChannelId,
                    provider.DefaultReleaseChannelId,
                    StringComparison.Ordinal));
        if (!trackMatches)
        {
            throw new ArgumentException(
                "Configuration history evidence is bound to another release track.",
                nameof(evidence));
        }
    }

    private static void VerifyPreview(
        ProviderConfigurationRestorePreview expected,
        ProviderConfigurationRestorePreview actual)
    {
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(expected.TransactionId, actual.TransactionId, StringComparison.Ordinal)
            || expected.Selection != actual.Selection
            || expected.Backup != actual.Backup
            || !string.Equals(expected.DestinationPath, actual.DestinationPath, pathComparison)
            || expected.ExpectedLiveRevision != actual.ExpectedLiveRevision
            || expected.CompatibilityState != actual.CompatibilityState
            || !string.Equals(
                expected.CompatibilitySummary,
                actual.CompatibilitySummary,
                StringComparison.Ordinal)
            || !string.Equals(expected.ConfirmationText, actual.ConfirmationText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The configuration history restore changed after review. Review it again.");
        }
    }

    private static bool SourceReceiptMatchesJournal(
        ProviderConfigurationRestoreJournal journal,
        ConfigurationBackupReceipt actual)
    {
        var expected = journal.Preview.Backup;
        var immutableFieldsMatch = string.Equals(
                actual.BackupId,
                expected.BackupId,
                StringComparison.Ordinal)
            && string.Equals(actual.InstallationId, expected.InstallationId, StringComparison.Ordinal)
            && string.Equals(actual.ProviderId, expected.ProviderId, StringComparison.Ordinal)
            && string.Equals(actual.TargetProviderId, expected.TargetProviderId, StringComparison.Ordinal)
            && actual.CreatedAtUtc == expected.CreatedAtUtc
            && string.Equals(actual.ContentSha256, expected.ContentSha256, StringComparison.Ordinal)
            && string.Equals(actual.Reason, expected.Reason, StringComparison.Ordinal)
            && string.Equals(actual.ReleaseIdentity, expected.ReleaseIdentity, StringComparison.Ordinal);
        if (!immutableFieldsMatch)
        {
            return false;
        }
        return actual == expected
            || actual.WasRestored
                && actual.RestoredAtUtc is not null
                && string.Equals(
                    actual.RestoreTransactionId,
                    journal.Preview.TransactionId,
                    StringComparison.Ordinal);
    }

    private static ProviderConfigurationRestoreResultState MapFailure(
        AtomicTomlWriteState state) =>
        state switch
        {
            AtomicTomlWriteState.Busy => ProviderConfigurationRestoreResultState.Busy,
            AtomicTomlWriteState.Conflict => ProviderConfigurationRestoreResultState.Conflict,
            AtomicTomlWriteState.Invalid => ProviderConfigurationRestoreResultState.Blocked,
            _ => ProviderConfigurationRestoreResultState.Failed,
        };

    private static (string GameDirectory, string ConfigurationPath) ResolveTarget(
        string? configurationPath)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            throw new InvalidOperationException(
                "Select a valid game installation before opening configuration history.");
        }
        var normalized = Path.GetFullPath(configurationPath);
        var gameDirectory = Path.GetDirectoryName(normalized)
            ?? throw new InvalidDataException("The configuration path has no game directory.");
        var validation = GameInstallValidator.Validate(gameDirectory);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(validation.Message);
        }
        var expected = Path.Combine(validation.GameDirectory, ConfigurationFileName);
        if (!PathEquals(normalized, expected))
        {
            throw new InvalidDataException(
                "Configuration history must target the selected game installation's expected TOML path.");
        }
        return (validation.GameDirectory, expected);
    }

    private (string GameDirectory, string ConfigurationPath) ResolveSelectedTarget() =>
        ResolveTarget(configurationPathProvider());

    private static byte[] ReadConfiguration(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new InvalidOperationException(
                "No active TOML exists to protect before restoring configuration history.");
        }
        if (info.Length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException(
                $"Configuration exceeds the {MaximumConfigurationBytes}-byte restore limit.");
        }
        return File.ReadAllBytes(path);
    }

    private void Persist(ProviderConfigurationRestoreJournal journal)
    {
        var parent = Path.GetDirectoryName(journalPath)
            ?? throw new InvalidOperationException("Configuration restore journal has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporaryPath = Path.Combine(parent, $".configuration-restore.{Guid.NewGuid():N}.tmp");
        try
        {
            var serialized = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(serialized);
                stream.Flush(flushToDisk: true);
            }
            var written = JsonSerializer.Deserialize<ProviderConfigurationRestoreJournal>(
                File.ReadAllBytes(temporaryPath),
                JsonOptions);
            if (written != journal)
            {
                throw new InvalidDataException(
                    "Configuration restore journal verification failed.");
            }
            if (File.Exists(journalPath))
            {
                File.Replace(temporaryPath, journalPath, null, ignoreMetadataErrors: true);
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

    private ValueTask ObserveCheckpointAsync(
        ProviderConfigurationRestorePhase current,
        CancellationToken cancellationToken) =>
        checkpoint?.Invoke(current, cancellationToken) ?? ValueTask.CompletedTask;

    private static string RestoreIdentity(string transactionId) =>
        $"configuration-history-restore/{transactionId}";

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
