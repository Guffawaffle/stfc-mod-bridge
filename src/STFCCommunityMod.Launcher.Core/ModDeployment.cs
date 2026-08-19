using System.Net;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

internal enum ModDeploymentFileCheckpoint
{
    PriorDllBackedUp,
    PriorRuntimeManifestBackedUp,
    TargetDllInstalled,
    TargetRuntimeManifestInstalled,
    DurableDllBackupCopyStarted,
    DurableRuntimeManifestBackupCopyStarted,
    DurableDllBackupPromoted,
    DurableRuntimeManifestBackupPromoted,
    DurableDllSourceRemoved,
    DurableRuntimeManifestSourceRemoved,
    ManagedDllRemoved,
    ManagedRuntimeManifestRemoved,
    AdoptedDllRestoreCopyStarted,
    AdoptedRuntimeManifestRestoreCopyStarted,
    AdoptedDllRestoreStaged,
    AdoptedRuntimeManifestRestoreStaged,
    AdoptedDllRestored,
    AdoptedRuntimeManifestRestored,
    RollbackDllRestoreStaged,
    RollbackRuntimeManifestRestoreStaged,
    RollbackDllRestoreCopyStarted,
    RollbackRuntimeManifestRestoreCopyStarted,
    RollbackDllRestored,
    RollbackRuntimeManifestRestored,
}

internal sealed class SimulatedProcessTerminationException(ModDeploymentFileCheckpoint checkpoint)
    : Exception($"Simulated process termination after {checkpoint}.");

internal sealed record ModDeploymentCopyStageReceipt(
    int SchemaVersion,
    string StagePath,
    ModDeploymentCopyStagePhase Phase,
    CandidateFileIdentity FileIdentity,
    ExactFileRevision? LastOwnedRevision = null);

internal enum ModDeploymentCopyStagePhase
{
    Writing,
    Complete,
}

public sealed partial class ModDeploymentService : IModDeploymentStateReader
{
    private const int DeploymentJournalSchemaVersion = 2;
    private const int CopyStageReceiptSchemaVersion = 2;
    private const int InstalledReceiptSchemaVersion = 1;
    private const int InstalledRegistrySchemaVersion = 2;
    private const long MaximumArtifactSize = 128L * 1024L * 1024L;
    private const string ManagedFileName = "version.dll";
    private readonly string stateDirectory;
    private readonly LauncherOperationLock operationLock;
    private readonly IModArtifactDownloader downloader;
    private readonly IModArtifactVersionReader versionReader;
    private readonly IModArtifactAuthenticityVerifier authenticityVerifier;
    private readonly Func<string, bool> isGameRunning;
    private readonly TimeProvider timeProvider;
    private readonly ModInstallationAttribution installationAttribution;
    private readonly ReviewedReleaseCertification? reviewedCertification;
    private readonly IReadOnlyList<ReviewedReleaseCertification> reviewedCertifications;
    private readonly Func<ModDeploymentPhase, CancellationToken, ValueTask>? afterPhasePersisted;
    private readonly Func<ModDeploymentFileCheckpoint, CancellationToken, ValueTask>? afterFileCheckpoint;
    private readonly Func<string, ExactFileRevision, bool>? afterArtifactCommitted;
    private readonly Func<string, string, CancellationToken, ValueTask>? afterDurableCopyBytesFlushed;
    private readonly Func<string, string, long, CancellationToken, ValueTask>?
        afterDurableCopyChunkWritten;
    private readonly Func<string, string, CancellationToken, ValueTask>? afterDurableCopyCompleted;

    public ModDeploymentService(
        string stateDirectory,
        IModArtifactDownloader downloader,
        IModArtifactVersionReader versionReader,
        IModArtifactAuthenticityVerifier authenticityVerifier,
        Func<string, bool> isGameRunning,
        ModInstallationAttribution installationAttribution,
        TimeProvider? timeProvider = null,
        Func<ModDeploymentPhase, CancellationToken, ValueTask>? afterPhasePersisted = null,
        ReviewedReleaseCertification? reviewedCertification = null,
        IEnumerable<ReviewedReleaseCertification>? reviewedCertifications = null,
        Func<string, string, CancellationToken, ValueTask>? afterDurableCopyBytesFlushed = null,
        Func<string, string, long, CancellationToken, ValueTask>? afterDurableCopyChunkWritten = null,
        Func<string, string, CancellationToken, ValueTask>? afterDurableCopyCompleted = null)
        : this(
            stateDirectory,
            downloader,
            versionReader,
            authenticityVerifier,
            isGameRunning,
            installationAttribution,
            timeProvider,
            afterPhasePersisted,
            reviewedCertification,
            afterFileCheckpoint: null,
            reviewedCertifications: reviewedCertifications,
            afterDurableCopyBytesFlushed: afterDurableCopyBytesFlushed,
            afterDurableCopyChunkWritten: afterDurableCopyChunkWritten,
            afterDurableCopyCompleted: afterDurableCopyCompleted)
    {
    }

    internal ModDeploymentService(
        string stateDirectory,
        IModArtifactDownloader downloader,
        IModArtifactVersionReader versionReader,
        IModArtifactAuthenticityVerifier authenticityVerifier,
        Func<string, bool> isGameRunning,
        ModInstallationAttribution installationAttribution,
        TimeProvider? timeProvider,
        Func<ModDeploymentPhase, CancellationToken, ValueTask>? afterPhasePersisted,
        ReviewedReleaseCertification? reviewedCertification,
        Func<ModDeploymentFileCheckpoint, CancellationToken, ValueTask>? afterFileCheckpoint,
        Func<string, ExactFileRevision, bool>? afterArtifactCommitted = null,
        IEnumerable<ReviewedReleaseCertification>? reviewedCertifications = null,
        Func<string, string, CancellationToken, ValueTask>? afterDurableCopyBytesFlushed = null,
        Func<string, string, long, CancellationToken, ValueTask>? afterDurableCopyChunkWritten = null,
        Func<string, string, CancellationToken, ValueTask>? afterDurableCopyCompleted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        this.stateDirectory = Path.GetFullPath(stateDirectory);
        operationLock = new(this.stateDirectory);
        this.downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        this.versionReader = versionReader ?? throw new ArgumentNullException(nameof(versionReader));
        this.authenticityVerifier = authenticityVerifier ?? throw new ArgumentNullException(nameof(authenticityVerifier));
        this.isGameRunning = isGameRunning ?? throw new ArgumentNullException(nameof(isGameRunning));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.afterPhasePersisted = afterPhasePersisted;
        this.installationAttribution = installationAttribution
            ?? throw new ArgumentNullException(nameof(installationAttribution));
        this.reviewedCertification = reviewedCertification;
        this.reviewedCertifications = (reviewedCertifications
                ?? (reviewedCertification is null ? [] : [reviewedCertification]))
            .GroupBy(
                certification => (
                    certification.ProviderId,
                    certification.ChannelId,
                    certification.RuntimeDistributionId),
                EqualityComparer<(string, string, string)>.Default)
            .Select(group => group.Single())
            .ToArray();
        this.afterFileCheckpoint = afterFileCheckpoint;
        this.afterArtifactCommitted = afterArtifactCommitted;
        this.afterDurableCopyBytesFlushed = afterDurableCopyBytesFlushed;
        this.afterDurableCopyChunkWritten = afterDurableCopyChunkWritten;
        this.afterDurableCopyCompleted = afterDurableCopyCompleted;
    }

    public string JournalPath => Path.Combine(stateDirectory, "deployment-journal.json");

    public string InstalledStatePath => Path.Combine(stateDirectory, "installed-mod.json");

    public ModDeploymentJournal? ReadJournal()
    {
        var journal = ReadJson<ModDeploymentJournal>(JournalPath);
        if (journal is not null)
        {
            ValidatePersistedJournal(journal);
        }
        return journal;
    }

    public ModInstalledArtifactState? ReadInstalledState()
    {
        var installations = ReadInstalledStates();
        return installations.Count switch
        {
            0 => null,
            1 => installations[0],
            _ => throw new InvalidOperationException(
                "An explicit game installation is required when multiple managed receipts exist."),
        };
    }

    public ModInstalledArtifactState? ReadInstalledState(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        var normalizedGameDirectory = NormalizeGameDirectory(gameDirectory);
        return ReadInstalledStates()
            .SingleOrDefault(state => PathEquals(state.GameDirectory, normalizedGameDirectory));
    }

    public IReadOnlyList<ModInstalledArtifactState> ReadInstalledStates() =>
        ReadInstalledRegistry().Installations;

    public string? ReadReleaseProductVersionFloor(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        return FindReleaseProductVersionFloor(
            ReadInstalledState(gameDirectory),
            installationAttribution)?.ReleaseProductVersion;
    }

    public string? GetReleaseProductVersionAdmissionFailure(
        string gameDirectory,
        ModReleaseArtifact artifact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(artifact);
        return ValidateReleaseProductVersionFloor(
            ReadInstalledState(gameDirectory),
            artifact,
            installationAttribution);
    }

    public bool MatchesRecordedRelease(
        ModInstalledArtifactState receipt,
        ModReleaseArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(artifact);
        return ArtifactMatchesInstalledReceipt(receipt, artifact)
            && (receipt.ReleaseProductVersion is null
                || string.Equals(
                    receipt.ReleaseProductVersion,
                    ResolveReleaseProductVersion(artifact, installationAttribution),
                    StringComparison.Ordinal));
    }

    public async Task<ModDeploymentResult> DeployAsync(
        string gameDirectory,
        ModReleaseArtifact artifact,
        ExistingArtifactPolicy existingArtifactPolicy,
        CancellationToken cancellationToken = default) =>
        await DeployCoreAsync(
            gameDirectory,
            artifact,
            existingArtifactPolicy,
            allowManagedRepair: false,
            commitParticipant: null,
            coordinatedTransactionId: null,
            candidateLease: null,
            candidateClaim: null,
            cancellationToken);

    public async Task<ModDeploymentResult> DeployCandidateAsync(
        string gameDirectory,
        ReviewedModArtifactCandidateLease candidateLease,
        ExistingArtifactPolicy existingArtifactPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateLease);
        return await ExecuteCandidateLeaseAsync(
            candidateLease,
            claim => DeployCoreAsync(
                gameDirectory,
                candidateLease.Receipt.Artifact,
                existingArtifactPolicy,
                allowManagedRepair: false,
                commitParticipant: null,
                coordinatedTransactionId: null,
                candidateLease,
                claim,
                cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ModDeploymentResult> DeployCoordinatedAsync(
        string gameDirectory,
        ModReleaseArtifact artifact,
        ExistingArtifactPolicy existingArtifactPolicy,
        string transactionId,
        IModDeploymentCommitParticipant commitParticipant,
        CancellationToken cancellationToken = default) =>
        await DeployCoordinatedCoreAsync(
            gameDirectory,
            artifact,
            existingArtifactPolicy,
            transactionId,
            commitParticipant,
            operationLease: null,
            cancellationToken).ConfigureAwait(false);

    internal async Task<ModDeploymentResult> DeployCoordinatedCoreAsync(
        string gameDirectory,
        ModReleaseArtifact artifact,
        ExistingArtifactPolicy existingArtifactPolicy,
        string transactionId,
        IModDeploymentCommitParticipant commitParticipant,
        LauncherOperationLease? operationLease,
        CancellationToken cancellationToken) =>
        await DeployCoreAsync(
            gameDirectory,
            artifact,
            existingArtifactPolicy,
            allowManagedRepair: false,
            commitParticipant ?? throw new ArgumentNullException(nameof(commitParticipant)),
            ValidateTransactionId(transactionId),
            candidateLease: null,
            candidateClaim: null,
            cancellationToken,
            operationLease).ConfigureAwait(false);

    public async Task<ModDeploymentResult> DeployCandidateCoordinatedAsync(
        string gameDirectory,
        ReviewedModArtifactCandidateLease candidateLease,
        ExistingArtifactPolicy existingArtifactPolicy,
        string transactionId,
        IModDeploymentCommitParticipant commitParticipant,
        CancellationToken cancellationToken = default) =>
        await DeployCandidateCoordinatedCoreAsync(
            gameDirectory,
            candidateLease,
            existingArtifactPolicy,
            transactionId,
            commitParticipant,
            operationLease: null,
            cancellationToken).ConfigureAwait(false);

    internal async Task<ModDeploymentResult> DeployCandidateCoordinatedCoreAsync(
        string gameDirectory,
        ReviewedModArtifactCandidateLease candidateLease,
        ExistingArtifactPolicy existingArtifactPolicy,
        string transactionId,
        IModDeploymentCommitParticipant commitParticipant,
        LauncherOperationLease? operationLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidateLease);
        return await ExecuteCandidateLeaseAsync(
            candidateLease,
            claim => DeployCoreAsync(
                gameDirectory,
                candidateLease.Receipt.Artifact,
                existingArtifactPolicy,
                allowManagedRepair: false,
                commitParticipant ?? throw new ArgumentNullException(nameof(commitParticipant)),
                ValidateTransactionId(transactionId),
                candidateLease,
                claim,
                cancellationToken,
                operationLease)).ConfigureAwait(false);
    }

    public async Task<ModDeploymentResult> RepairAsync(
        string gameDirectory,
        ModReleaseArtifact artifact,
        CancellationToken cancellationToken = default) =>
        await DeployCoreAsync(
            gameDirectory,
            artifact,
            ExistingArtifactPolicy.Reject,
            allowManagedRepair: true,
            commitParticipant: null,
            coordinatedTransactionId: null,
            candidateLease: null,
            candidateClaim: null,
            cancellationToken);

    public async Task<ModDeploymentResult> RepairCandidateAsync(
        string gameDirectory,
        ReviewedModArtifactCandidateLease candidateLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateLease);
        return await ExecuteCandidateLeaseAsync(
            candidateLease,
            claim => DeployCoreAsync(
                gameDirectory,
                candidateLease.Receipt.Artifact,
                ExistingArtifactPolicy.Reject,
                allowManagedRepair: true,
                commitParticipant: null,
                coordinatedTransactionId: null,
                candidateLease,
                claim,
                cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<ModDeploymentResult> ExecuteCandidateLeaseAsync(
        ReviewedModArtifactCandidateLease candidateLease,
        Func<object, Task<ModDeploymentResult>> operation)
    {
        if (!candidateLease.TryClaim(out var claim) || claim is null)
        {
            return new(
                ModDeploymentResultState.VerificationFailed,
                "The reviewed artifact candidate is already claimed, consumed, or awaiting cleanup.");
        }
        try
        {
            await candidateLease.AfterClaimedAsync(CancellationToken.None).ConfigureAwait(false);
            var result = await operation(claim).ConfigureAwait(false);
            try
            {
                await candidateLease.CleanupClaimAsync(claim).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new(
                    ModDeploymentResultState.VerificationFailed,
                    "The reviewed candidate could not be cleaned safely; deployment did not start.");
            }
            return result;
        }
        catch (Exception operationFailure)
        {
            try
            {
                await candidateLease.CleanupClaimAsync(claim).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
            {
                throw new AggregateException(
                    "Reviewed candidate execution and exact cleanup both failed.",
                    operationFailure,
                    cleanupFailure);
            }
            throw;
        }
    }

    private async Task<ModDeploymentResult> DeployCoreAsync(
        string gameDirectory,
        ModReleaseArtifact artifact,
        ExistingArtifactPolicy existingArtifactPolicy,
        bool allowManagedRepair,
        IModDeploymentCommitParticipant? commitParticipant,
        string? coordinatedTransactionId,
        ReviewedModArtifactCandidateLease? candidateLease,
        object? candidateClaim,
        CancellationToken cancellationToken,
        LauncherOperationLease? operationLease = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var validationFailure = ValidateRequest(gameDirectory, artifact, out var normalizedGameDirectory);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        if (isGameRunning(normalizedGameDirectory))
        {
            return new(ModDeploymentResultState.GameRunning, "Close Star Trek Fleet Command before changing the mod.");
        }

        await using var acquiredLease = operationLease is null
            ? await operationLock.TryAcquireAsync(cancellationToken).ConfigureAwait(false)
            : null;
        if (operationLease is null && acquiredLease is null)
        {
            return new(ModDeploymentResultState.Busy, "Another Mod Bridge mutation is already active.");
        }
        using var retainedOperationScope = operationLease?.RetainFor(stateDirectory);
        if (isGameRunning(normalizedGameDirectory))
        {
            return new(ModDeploymentResultState.GameRunning, "Close Star Trek Fleet Command before changing the mod.");
        }

        ModDeploymentJournal? incompleteJournal;
        ModInstalledArtifactState? previousInstalledState;
        var participantCommitStarted = false;
        try
        {
            incompleteJournal = ReadJournal();
            previousInstalledState = ReadInstalledState(normalizedGameDirectory);
        }
        catch (Exception exception) when (IsStateReadFailure(exception))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                $"Mod Bridge deployment state could not be read: {exception.Message}");
        }
        if (incompleteJournal is not null
            && (!IsTerminal(incompleteJournal.Phase)
                || incompleteJournal.Phase == ModDeploymentPhase.Committed
                    && HasSameVolumeTransactionResidue(incompleteJournal)))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                "An incomplete mod transaction must be recovered before another mutation can start.");
        }
        if (allowManagedRepair
            && (previousInstalledState is null
                || !ArtifactMatchesInstalledReceipt(previousInstalledState, artifact)
                || !string.Equals(
                    previousInstalledState.ProviderId,
                    installationAttribution.ProviderId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    previousInstalledState.ReleaseChannelId,
                    installationAttribution.ReleaseChannelId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    previousInstalledState.RuntimeDistributionId,
                    installationAttribution.RuntimeDistributionId,
                    StringComparison.Ordinal)))
        {
            return new(
                ModDeploymentResultState.VerificationFailed,
                "Repair requires the exact artifact and provider attribution recorded for this installation.");
        }
        var releaseFloorFailure = ValidateReleaseProductVersionFloor(
            previousInstalledState,
            artifact,
            installationAttribution);
        if (releaseFloorFailure is not null)
        {
            return new(ModDeploymentResultState.VerificationFailed, releaseFloorFailure);
        }
        ReviewedModArtifactCandidateContents? candidateContents = null;
        if (candidateLease is not null)
        {
            if (reviewedCertification is null)
            {
                return new(
                    ModDeploymentResultState.VerificationFailed,
                    "The reviewed artifact candidate has no matching launcher certification authority.");
            }
            try
            {
                candidateContents = await candidateLease.ConsumeAsync(
                    candidateClaim ?? throw new InvalidOperationException("The candidate deployment claim is missing."),
                    ReviewedModArtifactCandidateAcquirer.CertificationFingerprint(reviewedCertification),
                    installationAttribution,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                return new(ModDeploymentResultState.VerificationFailed, exception.Message);
            }
            try
            {
                await candidateLease.CleanupClaimAsync(candidateClaim!).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new(
                    ModDeploymentResultState.VerificationFailed,
                    "The verified candidate could not be cleaned safely; deployment did not start.");
            }
        }
        var legacyUpgrade = UpgradeLegacyBackupReceipts(previousInstalledState);
        if (legacyUpgrade.Failure is not null)
        {
            return new(ModDeploymentResultState.RecoveryRequired, legacyUpgrade.Failure);
        }
        previousInstalledState = legacyUpgrade.State;

        var targetPath = Path.Combine(normalizedGameDirectory, ManagedFileName);
        var runtimeManifestPath = RuntimeManifestTargetPath(normalizedGameDirectory);
        var hadExistingArtifact = File.Exists(targetPath);
        var hadExistingRuntimeManifest = File.Exists(runtimeManifestPath);
        var existingArtifactIdentity = hadExistingArtifact ? CaptureIdentity(targetPath) : null;
        var existingRuntimeManifestIdentity = hadExistingRuntimeManifest
            ? CaptureIdentity(runtimeManifestPath)
            : null;
        var isManagedUpdate = false;
        if (previousInstalledState is not null)
        {
            if ((!hadExistingArtifact
                || !string.Equals(
                    ComputeFileSha256(targetPath),
                    previousInstalledState.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                && !allowManagedRepair)
            {
                return new(
                    ModDeploymentResultState.ManagedArtifactChanged,
                    "The installed version.dll no longer matches Mod Bridge-managed state; repair is required.");
            }
            if (previousInstalledState.RuntimeManifest is not null
                && (!hadExistingRuntimeManifest
                    || !string.Equals(
                        ComputeFileSha256(runtimeManifestPath),
                        previousInstalledState.RuntimeManifest.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                && !allowManagedRepair)
            {
                return new(
                    ModDeploymentResultState.ManagedArtifactChanged,
                    "The managed runtime manifest no longer matches Mod Bridge state; repair is required.");
            }
            isManagedUpdate = true;
        }

        if (previousInstalledState is not null)
        {
            var retainedBackupFailure = ValidateDeclaredBackup(
                previousInstalledState.PreviousArtifactBackupPath,
                previousInstalledState.PreviousArtifactBackupIdentity,
                "adopted DLL");
            retainedBackupFailure ??= ValidateDeclaredBackup(
                previousInstalledState.PreviousRuntimeManifestBackupPath,
                previousInstalledState.PreviousRuntimeManifestBackupIdentity,
                "adopted runtime manifest");
            if (retainedBackupFailure is not null)
            {
                return new(ModDeploymentResultState.RecoveryRequired, retainedBackupFailure);
            }
        }

        var requiresExplicitAdoption = hadExistingArtifact && !isManagedUpdate
            || hadExistingRuntimeManifest
                && artifact.RuntimeManifest is not null
                && previousInstalledState?.RuntimeManifest is null;
        if (requiresExplicitAdoption
            && existingArtifactPolicy == ExistingArtifactPolicy.Reject)
        {
            return new(
                ModDeploymentResultState.ExistingArtifactRequiresAdoption,
                "An existing mod file requires explicit adoption before Mod Bridge can replace it.");
        }

        Directory.CreateDirectory(stateDirectory);
        var transactionId = coordinatedTransactionId ?? Guid.NewGuid().ToString("N");
        var stagePath = Path.Combine(normalizedGameDirectory, $".{ManagedFileName}.{transactionId}.stage");
        var sameVolumeBackupPath = Path.Combine(
            normalizedGameDirectory,
            $".{ManagedFileName}.{transactionId}.rollback");
        var durableBackupPath = Path.Combine(stateDirectory, "rollback", transactionId, ManagedFileName);
        var normalizedRuntimeManifest = artifact.RuntimeManifest is null
            ? null
            : artifact.RuntimeManifest with { Sha256 = NormalizeSha256(artifact.RuntimeManifest.Sha256) };
        var journal = new ModDeploymentJournal(
            DeploymentJournalSchemaVersion,
            transactionId,
            ModDeploymentOperation.Deploy,
            ModDeploymentPhase.Planned,
            normalizedGameDirectory,
            artifact with
            {
                Sha256 = NormalizeSha256(artifact.Sha256),
                RuntimeManifest = normalizedRuntimeManifest,
            },
            stagePath,
            sameVolumeBackupPath,
            durableBackupPath,
            hadExistingArtifact,
            previousInstalledState,
            timeProvider.GetUtcNow(),
            HadExistingRuntimeManifest: hadExistingRuntimeManifest,
            HasCommitParticipant: commitParticipant is not null,
            CommitParticipantCompleted: commitParticipant is null,
            ExistingArtifactIdentity: existingArtifactIdentity,
            ExistingRuntimeManifestIdentity: existingRuntimeManifestIdentity,
            TargetInstallationAttribution: installationAttribution);
        ExactFileRevision? exactStagedArtifactRevision = null;
        ExactFileMutation? exactStagedArtifact = null;
        ExactFileMutation? exactStagedRuntimeManifest = null;

        try
        {
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Planned, cancellationToken);
            if (commitParticipant is not null)
            {
                await commitParticipant.BeginAsync(
                    new(
                        transactionId,
                        normalizedGameDirectory,
                        journal.Artifact,
                        previousInstalledState,
                        hadExistingArtifact),
                    cancellationToken).ConfigureAwait(false);
            }
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Downloading, cancellationToken);

            var download = candidateContents is null
                ? await downloader.DownloadAsync(artifact.DownloadUri, cancellationToken)
                : new ModArtifactDownload(HttpStatusCode.OK, candidateContents.Dll, candidateContents.Dll.LongLength);
            var downloadFailure = VerifyDownload(download, journal.Artifact);
            if (downloadFailure is not null)
            {
                await PersistPhaseAsync(
                    journal with { Error = downloadFailure.Message },
                    ModDeploymentPhase.Failed,
                    cancellationToken);
                return downloadFailure;
            }

            ModArtifactDownload? runtimeManifestDownload = null;
            ParsedRuntimeManifest? parsedRuntimeManifest = null;
            ReviewedRuntimeActivation? reviewedRuntimeActivation = null;
            if (journal.Artifact.RuntimeManifest is not null)
            {
                runtimeManifestDownload = candidateContents is null
                    ? await downloader.DownloadAsync(
                        journal.Artifact.RuntimeManifest.DownloadUri,
                        cancellationToken)
                    : new ModArtifactDownload(
                        HttpStatusCode.OK,
                        candidateContents.RuntimeManifest
                            ?? throw new InvalidDataException("The reviewed candidate pair is incomplete."),
                        candidateContents.RuntimeManifest?.LongLength);
                var runtimeFailure = VerifyDownload(runtimeManifestDownload, journal.Artifact.RuntimeManifest);
                if (runtimeFailure is not null)
                {
                    await PersistPhaseAsync(
                        journal with { Error = runtimeFailure.Message },
                        ModDeploymentPhase.Failed,
                        cancellationToken);
                    return runtimeFailure;
                }
                parsedRuntimeManifest = ArtifactBoundRuntimeManifestParser.Parse(
                    runtimeManifestDownload.Contents,
                    journal.Artifact,
                    journal.Artifact.RuntimeManifest,
                    installationAttribution.RuntimeDistributionId);
                reviewedRuntimeActivation = candidateContents?.RuntimeActivation
                    ?? ArtifactBoundRuntimeManifestParser.AuthorizeActivation(
                        parsedRuntimeManifest,
                        journal.Artifact,
                        journal.Artifact.RuntimeManifest,
                        reviewedCertification);
                if (reviewedRuntimeActivation is null)
                {
                    throw new InvalidDataException(
                        "The runtime manifest pair is not authorized by the launcher-bundled reviewed certification.");
                }
            }

            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Verified, cancellationToken);
            await WriteStageAsync(stagePath, download.Contents, cancellationToken);
            exactStagedArtifact = ExactFileMutation.Open(stagePath);
            exactStagedArtifactRevision = exactStagedArtifact.CaptureRevision();
            VerifyFile(exactStagedArtifactRevision, journal.Artifact);
            VerifyAuthenticity(stagePath);
            VerifyVersion(stagePath, journal.Artifact);
            if (runtimeManifestDownload is not null && journal.Artifact.RuntimeManifest is not null)
            {
                await WriteStageAsync(
                    RuntimeManifestStagePath(journal),
                    runtimeManifestDownload.Contents,
                    cancellationToken);
                exactStagedRuntimeManifest = ExactFileMutation.Open(RuntimeManifestStagePath(journal));
                VerifyFile(
                    exactStagedRuntimeManifest.CaptureRevision(),
                    journal.Artifact.RuntimeManifest);
            }
            exactStagedArtifactRevision = exactStagedArtifact.CaptureRevision();
            journal = journal with
            {
                TargetArtifactFileIdentity = FileIdentity(exactStagedArtifact.Identity),
                TargetRuntimeManifestFileIdentity = exactStagedRuntimeManifest is null
                    ? null
                    : FileIdentity(exactStagedRuntimeManifest.Identity),
            };
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Staged, cancellationToken);
            var commitMutationStarted = false;
            try
            {
                using var exactExistingArtifact = OpenValidatedLiveMember(
                    targetPath,
                    journal.HadExistingArtifact,
                    journal.ExistingArtifactIdentity,
                    "DLL");
                using var exactExistingRuntimeManifest = OpenValidatedLiveMember(
                    runtimeManifestPath,
                    journal.HadExistingRuntimeManifest,
                    journal.ExistingRuntimeManifestIdentity,
                    "runtime manifest");
                ValidateCommitPreconditions(
                    journal,
                    exactStagedArtifact,
                    exactStagedRuntimeManifest,
                    exactExistingArtifact,
                    exactExistingRuntimeManifest);
                journal = await PersistPhaseAsync(
                    journal,
                    ModDeploymentPhase.Committing,
                    cancellationToken);

                if (hadExistingArtifact)
                {
                    commitMutationStarted = true;
                    exactExistingArtifact!.MoveExactNoReplace(sameVolumeBackupPath);
                    VerifyFile(
                        exactExistingArtifact.CaptureRevision(),
                        existingArtifactIdentity!,
                        "prior DLL");
                    await CheckpointAsync(
                        ModDeploymentFileCheckpoint.PriorDllBackedUp,
                        cancellationToken);
                }
                if (ShouldMutateRuntimeManifest(journal) && hadExistingRuntimeManifest)
                {
                    commitMutationStarted = true;
                    exactExistingRuntimeManifest!.MoveExactNoReplace(
                        RuntimeManifestSameVolumeBackupPath(journal));
                    VerifyFile(
                        exactExistingRuntimeManifest.CaptureRevision(),
                        existingRuntimeManifestIdentity!,
                        "prior runtime manifest");
                    await CheckpointAsync(
                        ModDeploymentFileCheckpoint.PriorRuntimeManifestBackedUp,
                        cancellationToken);
                }
            }
            catch (Exception exception) when (
                !commitMutationStarted
                && exception is InvalidDataException or InvalidOperationException)
            {
                try
                {
                    exactStagedArtifact.Dispose();
                    exactStagedArtifact = null;
                    exactStagedRuntimeManifest?.Dispose();
                    exactStagedRuntimeManifest = null;
                    DeleteVerifiedResidue(journal.StagePath, Identity(journal.Artifact), "DLL stage");
                    DeleteVerifiedResidue(
                        RuntimeManifestStagePath(journal),
                        journal.Artifact.RuntimeManifest is null
                            ? null
                            : Identity(journal.Artifact.RuntimeManifest),
                        "runtime-manifest stage");
                    await PersistPhaseAsync(
                        journal with { Error = exception.Message },
                        ModDeploymentPhase.Failed,
                        cancellationToken);
                    return new(
                        exception is InvalidOperationException
                            ? ModDeploymentResultState.GameRunning
                            : ModDeploymentResultState.ManagedArtifactChanged,
                        exception.Message);
                }
                catch (Exception cleanupException)
                {
                    return new(
                        ModDeploymentResultState.RecoveryRequired,
                        $"The live files were not changed, but verified staging cleanup requires recovery: "
                            + cleanupException.Message);
                }
            }
            if (afterArtifactCommitted is not null)
            {
                journal = journal with
                {
                    PreserveLiveArtifactDuringRecovery = true,
                    UpdatedAtUtc = timeProvider.GetUtcNow(),
                };
                WriteJsonAtomically(JournalPath, journal);
            }
            exactStagedArtifact.MoveExactNoReplace(targetPath);
            await CheckpointAsync(ModDeploymentFileCheckpoint.TargetDllInstalled, cancellationToken);
            if (journal.Artifact.RuntimeManifest is not null)
            {
                exactStagedRuntimeManifest!.MoveExactNoReplace(runtimeManifestPath);
                await CheckpointAsync(
                    ModDeploymentFileCheckpoint.TargetRuntimeManifestInstalled,
                    cancellationToken);
            }
            VerifyFile(exactStagedArtifact.CaptureRevision(), journal.Artifact);
            VerifyAuthenticity(targetPath);
            VerifyVersion(targetPath, journal.Artifact);
            if (afterArtifactCommitted is not null
                && exactStagedArtifactRevision is not null
                && !afterArtifactCommitted(targetPath, exactStagedArtifactRevision))
            {
                exactStagedArtifact.Dispose();
                exactStagedArtifact = null;
                exactStagedRuntimeManifest?.Dispose();
                exactStagedRuntimeManifest = null;
                return new(
                    ModDeploymentResultState.RecoveryRequired,
                    "The mod DLL committed, but its exact recovery ownership could not be confirmed. "
                        + "The live file was preserved for explicit recovery.",
                    Changed: true,
                    RuntimeActivation: reviewedRuntimeActivation);
            }
            if (journal.Artifact.RuntimeManifest is not null)
            {
                VerifyFile(
                    exactStagedRuntimeManifest!.CaptureRevision(),
                    journal.Artifact.RuntimeManifest);
            }
            exactStagedArtifact.Dispose();
            exactStagedArtifact = null;
            exactStagedRuntimeManifest?.Dispose();
            exactStagedRuntimeManifest = null;

            var retainedBackupPath = isManagedUpdate
                ? previousInstalledState?.PreviousArtifactBackupPath
                : hadExistingArtifact
                    ? durableBackupPath
                    : null;
            var retainNewRuntimeBackup = ShouldMutateRuntimeManifest(journal)
                && hadExistingRuntimeManifest
                && (!isManagedUpdate || previousInstalledState?.RuntimeManifest is null);
            var retainedRuntimeBackupPath = isManagedUpdate
                ? previousInstalledState?.PreviousRuntimeManifestBackupPath
                    ?? (retainNewRuntimeBackup ? RuntimeManifestDurableBackupPath(journal) : null)
                : retainNewRuntimeBackup
                    ? RuntimeManifestDurableBackupPath(journal)
                    : null;

            var installedRuntimeManifest = journal.Artifact.RuntimeManifest is null
                ? null
                : new ModInstalledRuntimeManifestState(
                    journal.Artifact.RuntimeManifest.FileName,
                    journal.Artifact.RuntimeManifest.Size,
                    journal.Artifact.RuntimeManifest.Sha256,
                    parsedRuntimeManifest!.SourceRevision,
                    journal.Artifact.RuntimeManifest.ExpectedRepository,
                    journal.Artifact.RuntimeManifest.ExpectedTag);

            var installedState = new ModInstalledArtifactState(
                InstalledReceiptSchemaVersion,
                normalizedGameDirectory,
                ManagedFileName,
                journal.Artifact.ExpectedVersion,
                journal.Artifact.Size,
                journal.Artifact.Sha256,
                timeProvider.GetUtcNow(),
                retainedBackupPath,
                installationAttribution.ProviderId,
                installationAttribution.ReleaseChannelId,
                installationAttribution.RuntimeDistributionId,
                installedRuntimeManifest,
                retainedRuntimeBackupPath,
                isManagedUpdate
                    ? previousInstalledState?.PreviousArtifactBackupIdentity
                    : retainedBackupPath is null ? null : existingArtifactIdentity,
                isManagedUpdate
                    ? previousInstalledState?.PreviousRuntimeManifestBackupIdentity
                        ?? (retainedRuntimeBackupPath is null ? null : existingRuntimeManifestIdentity)
                    : retainedRuntimeBackupPath is null ? null : existingRuntimeManifestIdentity,
                ResolveReleaseProductVersion(journal.Artifact, installationAttribution),
                BuildReleaseHighWaterMarks(
                    previousInstalledState,
                    installationAttribution,
                    ResolveReleaseProductVersion(journal.Artifact, installationAttribution),
                    journal.Artifact));
            UpsertInstalledState(installedState);

            if (commitParticipant is not null)
            {
                participantCommitStarted = true;
                await commitParticipant.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!isManagedUpdate && File.Exists(sameVolumeBackupPath))
            {
                await PromoteDurableBackupAsync(
                    sameVolumeBackupPath,
                    durableBackupPath,
                    existingArtifactIdentity!,
                    ModDeploymentFileCheckpoint.DurableDllBackupCopyStarted,
                    ModDeploymentFileCheckpoint.DurableDllBackupPromoted,
                    ModDeploymentFileCheckpoint.DurableDllSourceRemoved,
                    cancellationToken);
            }
            if (retainNewRuntimeBackup && File.Exists(RuntimeManifestSameVolumeBackupPath(journal)))
            {
                await PromoteDurableBackupAsync(
                    RuntimeManifestSameVolumeBackupPath(journal),
                    RuntimeManifestDurableBackupPath(journal),
                    existingRuntimeManifestIdentity!,
                    ModDeploymentFileCheckpoint.DurableRuntimeManifestBackupCopyStarted,
                    ModDeploymentFileCheckpoint.DurableRuntimeManifestBackupPromoted,
                    ModDeploymentFileCheckpoint.DurableRuntimeManifestSourceRemoved,
                    cancellationToken);
            }
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.CleanupPending, cancellationToken);
            var preCompletionFailure = ValidateCommittedOutcome(journal);
            if (preCompletionFailure is not null)
            {
                throw new InvalidDataException(preCompletionFailure);
            }
            if (commitParticipant is not null)
            {
                try
                {
                    await commitParticipant.CompleteAsync(cancellationToken).ConfigureAwait(false);
                    journal = journal with
                    {
                        CommitParticipantCompleted = true,
                        UpdatedAtUtc = timeProvider.GetUtcNow(),
                    };
                    WriteJsonAtomically(JournalPath, journal);
                }
                catch (Exception exception)
                {
                    return new(
                        ModDeploymentResultState.RecoveryRequired,
                        $"The mod pair committed, but provider-switch finalization requires recovery: {exception.Message}",
                        installedState,
                        Changed: true,
                        RuntimeActivation: reviewedRuntimeActivation);
                }
            }
            var outcomeFailure = ValidateCommittedOutcome(journal);
            if (outcomeFailure is not null)
            {
                WriteCleanupPendingError(journal, outcomeFailure);
                return new(
                    ModDeploymentResultState.RecoveryRequired,
                    outcomeFailure,
                    installedState,
                    Changed: true,
                    RuntimeActivation: reviewedRuntimeActivation);
            }
            var cleanup = CleanupCommittedResidue(journal);
            if (!cleanup.IsSuccess)
            {
                WriteCleanupPendingError(journal, cleanup.Message);
                return new(
                    ModDeploymentResultState.RecoveryRequired,
                    cleanup.Message,
                    installedState,
                    Changed: true,
                    RuntimeActivation: reviewedRuntimeActivation);
            }
            try
            {
                journal = await PersistPhaseAsync(
                    journal with { PreserveLiveArtifactDuringRecovery = false },
                    ModDeploymentPhase.Committed,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                WriteCleanupPendingError(journal, exception.Message);
                return new(
                    ModDeploymentResultState.RecoveryRequired,
                    $"The mod pair committed, but final cleanup recording requires recovery: {exception.Message}",
                    installedState,
                    Changed: true,
                    RuntimeActivation: reviewedRuntimeActivation);
            }
            return new(
                ModDeploymentResultState.Succeeded,
                "Community Mod installed successfully.",
                installedState,
                Changed: true,
                RuntimeActivation: reviewedRuntimeActivation);
        }
        catch (SimulatedProcessTerminationException)
        {
            exactStagedArtifact?.Dispose();
            exactStagedRuntimeManifest?.Dispose();
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            exactStagedArtifact?.Dispose();
            exactStagedRuntimeManifest?.Dispose();
            await RollBackCoordinatedAsync(
                journal,
                targetPath,
                commitParticipant,
                participantCommitStarted,
                exactStagedArtifactRevision,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            exactStagedArtifact?.Dispose();
            exactStagedRuntimeManifest?.Dispose();
            var rolledBack = await RollBackCoordinatedAsync(
                journal with { Error = exception.Message },
                targetPath,
                commitParticipant,
                participantCommitStarted,
                exactStagedArtifactRevision,
                CancellationToken.None).ConfigureAwait(false);
            return new(
                rolledBack ? ModDeploymentResultState.FailedAndRolledBack : ModDeploymentResultState.RecoveryRequired,
                rolledBack
                    ? $"The mod transaction failed and the previous state was restored: {exception.Message}"
                    : $"The mod transaction failed and requires recovery: {exception.Message}");
        }
    }

    private async Task<bool> RollBackCoordinatedAsync(
        ModDeploymentJournal journal,
        string targetPath,
        IModDeploymentCommitParticipant? commitParticipant,
        bool participantCommitStarted,
        ExactFileRevision? exactPromotedArtifactRevision,
        CancellationToken cancellationToken)
    {
        if (journal.PreserveLiveArtifactDuringRecovery)
        {
            try
            {
                using var exactTarget = ExactFileMutation.Open(targetPath);
                if (exactPromotedArtifactRevision is null
                    || !exactPromotedArtifactRevision.Matches(exactTarget.CaptureRevision()))
                {
                    return false;
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException
                    or NotSupportedException
                    or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
        var artifactRolledBack = await RollBackAsync(
                journal,
                targetPath,
                cancellationToken,
                journal.PreserveLiveArtifactDuringRecovery ? exactPromotedArtifactRevision : null)
            .ConfigureAwait(false);
        if (journal.PreserveLiveArtifactDuringRecovery && !artifactRolledBack)
        {
            return false;
        }
        if (!participantCommitStarted || commitParticipant is null)
        {
            return artifactRolledBack;
        }

        try
        {
            await commitParticipant.RollBackAsync(cancellationToken).ConfigureAwait(false);
            return artifactRolledBack;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ModDeploymentResult> StopManagingAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        string normalizedGameDirectory;
        try
        {
            if (!Path.IsPathFullyQualified(gameDirectory))
            {
                return new(
                    ModDeploymentResultState.InvalidGameTarget,
                    "Stop managing requires an absolute game installation path.");
            }
            normalizedGameDirectory = NormalizeGameDirectory(gameDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(
                ModDeploymentResultState.InvalidGameTarget,
                $"Stop managing requires a valid absolute game installation path: {exception.Message}");
        }
        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            return new(ModDeploymentResultState.Busy, "Another Mod Bridge mutation is already active.");
        }

        ModDeploymentJournal? journal;
        ModInstalledArtifactState? installedState;
        try
        {
            journal = ReadJournal();
            installedState = ReadInstalledState(normalizedGameDirectory);
        }
        catch (Exception exception) when (IsStateReadFailure(exception))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                $"Mod Bridge deployment state could not be read: {exception.Message}");
        }

        if (journal is not null
            && (!IsTerminal(journal.Phase)
                || journal.Phase == ModDeploymentPhase.Committed
                    && HasSameVolumeTransactionResidue(journal)))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                "The incomplete mod transaction must be recovered before ownership can be detached.");
        }
        if (installedState is null)
        {
            return new(
                ModDeploymentResultState.Succeeded,
                $"Mod Bridge is not managing the installation at '{normalizedGameDirectory}'.");
        }

        var legacyUpgrade = UpgradeLegacyBackupReceipts(installedState, persistUpgrade: false);
        if (legacyUpgrade.Failure is not null)
        {
            return new(ModDeploymentResultState.RecoveryRequired, legacyUpgrade.Failure);
        }
        installedState = legacyUpgrade.State!;

        ModDetachedAdoptionBackupState? retainedBackup = null;
        if (!string.IsNullOrWhiteSpace(installedState.PreviousArtifactBackupPath)
            || !string.IsNullOrWhiteSpace(installedState.PreviousRuntimeManifestBackupPath))
        {
            retainedBackup = new(
                Guid.NewGuid().ToString("N"),
                installedState.GameDirectory,
                DateTimeOffset.UtcNow,
                installedState.ProviderId,
                installedState.ReleaseChannelId,
                installedState.RuntimeDistributionId,
                installedState.PreviousArtifactBackupPath,
                installedState.PreviousArtifactBackupIdentity,
                installedState.PreviousRuntimeManifestBackupPath,
                installedState.PreviousRuntimeManifestBackupIdentity);
        }

        try
        {
            DetachInstalledState(normalizedGameDirectory, retainedBackup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                $"Mod Bridge could not detach the ownership receipt: {exception.Message}");
        }

        return new(
            ModDeploymentResultState.Succeeded,
            $"Mod Bridge stopped managing '{normalizedGameDirectory}'. Game files were not changed.",
            Changed: true);
    }

    public async Task<ModDeploymentResult> UninstallAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        var validation = GameInstallValidator.Validate(gameDirectory);
        if (!validation.IsValid)
        {
            return new(ModDeploymentResultState.InvalidGameTarget, validation.Message);
        }

        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            return new(ModDeploymentResultState.Busy, "Another Mod Bridge mutation is already active.");
        }

        ModDeploymentJournal? incompleteJournal;
        ModInstalledArtifactState? installedState;
        try
        {
            incompleteJournal = ReadJournal();
            installedState = ReadInstalledState(validation.GameDirectory);
        }
        catch (Exception exception) when (IsStateReadFailure(exception))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                $"Mod Bridge deployment state could not be read: {exception.Message}");
        }
        if (incompleteJournal is not null
            && (!IsTerminal(incompleteJournal.Phase)
                || incompleteJournal.Phase == ModDeploymentPhase.Committed
                    && HasSameVolumeTransactionResidue(incompleteJournal)))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                "An incomplete mod transaction must be recovered before another mutation can start.");
        }

        var legacyUpgrade = UpgradeLegacyBackupReceipts(installedState);
        if (legacyUpgrade.Failure is not null)
        {
            return new(ModDeploymentResultState.RecoveryRequired, legacyUpgrade.Failure);
        }
        installedState = legacyUpgrade.State;

        if (installedState is null)
        {
            return new(ModDeploymentResultState.Succeeded, "No Mod Bridge-managed mod installation was found.");
        }

        if (isGameRunning(validation.GameDirectory))
        {
            return new(ModDeploymentResultState.GameRunning, "Close Star Trek Fleet Command before removing the mod.");
        }

        var targetPath = Path.Combine(installedState.GameDirectory, ManagedFileName);
        if (!File.Exists(targetPath)
            || !string.Equals(ComputeFileSha256(targetPath), installedState.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ModDeploymentResultState.ManagedArtifactChanged,
                "The installed version.dll no longer matches Mod Bridge-managed state; it was not removed.");
        }
        var runtimeManifestPath = RuntimeManifestTargetPath(installedState.GameDirectory);
        var managedRuntimeManifestMatches = installedState.RuntimeManifest is not null
            && File.Exists(runtimeManifestPath)
            && string.Equals(
                ComputeFileSha256(runtimeManifestPath),
                installedState.RuntimeManifest.Sha256,
                StringComparison.OrdinalIgnoreCase);
        if (installedState.RuntimeManifest is not null && !managedRuntimeManifestMatches)
        {
            return new(
                ModDeploymentResultState.ManagedArtifactChanged,
                "The managed runtime manifest is missing or changed; repair it before uninstalling the managed pair.");
        }
        var priorBackupFailure = ValidateDeclaredBackup(
            installedState.PreviousArtifactBackupPath,
            installedState.PreviousArtifactBackupIdentity,
            "adopted DLL");
        priorBackupFailure ??= ValidateDeclaredBackup(
            installedState.PreviousRuntimeManifestBackupPath,
            installedState.PreviousRuntimeManifestBackupIdentity,
            "adopted runtime manifest");
        if (priorBackupFailure is not null)
        {
            return new(ModDeploymentResultState.RecoveryRequired, priorBackupFailure);
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var removedArtifactPath = Path.Combine(
            installedState.GameDirectory,
            $".{ManagedFileName}.{transactionId}.rollback");
        var journal = new ModDeploymentJournal(
            DeploymentJournalSchemaVersion,
            transactionId,
            ModDeploymentOperation.Uninstall,
            ModDeploymentPhase.Planned,
            installedState.GameDirectory,
            new(
                new Uri("https://local.invalid/managed-version.dll"),
                ManagedFileName,
                installedState.Size,
                installedState.Sha256,
                installedState.Version),
            Path.Combine(installedState.GameDirectory, $".{ManagedFileName}.{transactionId}.stage"),
            removedArtifactPath,
            Path.Combine(stateDirectory, "rollback", transactionId, ManagedFileName),
            true,
            installedState,
            timeProvider.GetUtcNow(),
            HadExistingRuntimeManifest: managedRuntimeManifestMatches,
            CommitParticipantCompleted: true,
            ExistingArtifactIdentity: new(installedState.Size, installedState.Sha256),
            ExistingRuntimeManifestIdentity: installedState.RuntimeManifest is null
                ? null
                : new(installedState.RuntimeManifest.Size, installedState.RuntimeManifest.Sha256));

        try
        {
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Planned, cancellationToken);
            var commitMutationStarted = false;
            try
            {
                using var exactManagedArtifact = OpenValidatedLiveMember(
                    targetPath,
                    existed: true,
                    journal.ExistingArtifactIdentity,
                    "managed DLL");
                using var exactManagedRuntimeManifest = installedState.RuntimeManifest is null
                    ? null
                    : OpenValidatedLiveMember(
                        runtimeManifestPath,
                        existed: true,
                        journal.ExistingRuntimeManifestIdentity,
                        "managed runtime manifest");
                ValidateUninstallCommitPreconditions(
                    journal,
                    installedState,
                    exactManagedArtifact!,
                    exactManagedRuntimeManifest);
                journal = await PersistPhaseAsync(
                    journal,
                    ModDeploymentPhase.Committing,
                    cancellationToken);
                commitMutationStarted = true;
                exactManagedArtifact!.MoveExactNoReplace(removedArtifactPath);
                VerifyFile(
                    exactManagedArtifact.CaptureRevision(),
                    journal.ExistingArtifactIdentity!,
                    "managed DLL rollback");
                await CheckpointAsync(
                    ModDeploymentFileCheckpoint.ManagedDllRemoved,
                    cancellationToken);
                if (managedRuntimeManifestMatches)
                {
                    commitMutationStarted = true;
                    exactManagedRuntimeManifest!.MoveExactNoReplace(
                        RuntimeManifestSameVolumeBackupPath(journal));
                    VerifyFile(
                        exactManagedRuntimeManifest.CaptureRevision(),
                        journal.ExistingRuntimeManifestIdentity!,
                        "managed runtime-manifest rollback");
                    await CheckpointAsync(
                        ModDeploymentFileCheckpoint.ManagedRuntimeManifestRemoved,
                        cancellationToken);
                }
            }
            catch (Exception exception) when (
                !commitMutationStarted
                && exception is InvalidDataException or InvalidOperationException)
            {
                await PersistPhaseAsync(
                    journal with { Error = exception.Message },
                    ModDeploymentPhase.Failed,
                    cancellationToken);
                return new(
                    exception is InvalidOperationException
                        ? ModDeploymentResultState.GameRunning
                        : ModDeploymentResultState.ManagedArtifactChanged,
                    exception.Message,
                    installedState);
            }
            if (!string.IsNullOrWhiteSpace(installedState.PreviousArtifactBackupPath)
                && File.Exists(installedState.PreviousArtifactBackupPath))
            {
                await CopyBackupToSameVolumeStageAsync(
                    installedState.PreviousArtifactBackupPath,
                    journal.StagePath,
                    installedState.PreviousArtifactBackupIdentity!,
                    ModDeploymentFileCheckpoint.AdoptedDllRestoreCopyStarted,
                    ModDeploymentFileCheckpoint.AdoptedDllRestoreStaged,
                    cancellationToken);
                journal = journal with
                {
                    RestoredAdoptedArtifactFileIdentity = FileIdentity(
                        GetCompletedOwnedCopyStageFileIdentity(
                            journal.StagePath,
                            "adopted DLL restore stage")),
                    UpdatedAtUtc = timeProvider.GetUtcNow(),
                };
                WriteJsonAtomically(JournalPath, journal);
                MoveCompletedOwnedCopyStage(
                    journal.StagePath,
                    targetPath,
                    installedState.PreviousArtifactBackupIdentity!,
                    "adopted DLL restore stage");
                await CheckpointAsync(ModDeploymentFileCheckpoint.AdoptedDllRestored, cancellationToken);
            }
            if (!File.Exists(runtimeManifestPath)
                && !string.IsNullOrWhiteSpace(installedState.PreviousRuntimeManifestBackupPath)
                && File.Exists(installedState.PreviousRuntimeManifestBackupPath))
            {
                await CopyBackupToSameVolumeStageAsync(
                    installedState.PreviousRuntimeManifestBackupPath,
                    RuntimeManifestStagePath(journal),
                    installedState.PreviousRuntimeManifestBackupIdentity!,
                    ModDeploymentFileCheckpoint.AdoptedRuntimeManifestRestoreCopyStarted,
                    ModDeploymentFileCheckpoint.AdoptedRuntimeManifestRestoreStaged,
                    cancellationToken);
                journal = journal with
                {
                    RestoredAdoptedRuntimeManifestFileIdentity = FileIdentity(
                        GetCompletedOwnedCopyStageFileIdentity(
                            RuntimeManifestStagePath(journal),
                            "adopted runtime-manifest restore stage")),
                    UpdatedAtUtc = timeProvider.GetUtcNow(),
                };
                WriteJsonAtomically(JournalPath, journal);
                MoveCompletedOwnedCopyStage(
                    RuntimeManifestStagePath(journal),
                    runtimeManifestPath,
                    installedState.PreviousRuntimeManifestBackupIdentity!,
                    "adopted runtime-manifest restore stage");
                await CheckpointAsync(
                    ModDeploymentFileCheckpoint.AdoptedRuntimeManifestRestored,
                    cancellationToken);
            }

            RemoveInstalledState(installedState.GameDirectory);
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.CleanupPending, cancellationToken);
            var outcomeFailure = ValidateCommittedOutcome(journal);
            if (outcomeFailure is not null)
            {
                WriteCleanupPendingError(journal, outcomeFailure);
                return new(ModDeploymentResultState.RecoveryRequired, outcomeFailure, Changed: true);
            }
            var cleanup = CleanupCommittedResidue(journal);
            if (!cleanup.IsSuccess)
            {
                WriteCleanupPendingError(journal, cleanup.Message);
                return new(
                    ModDeploymentResultState.RecoveryRequired,
                    cleanup.Message,
                    Changed: true);
            }
            try
            {
                journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Committed, cancellationToken);
            }
            catch (Exception exception)
            {
                WriteCleanupPendingError(journal, exception.Message);
                return new(
                    ModDeploymentResultState.RecoveryRequired,
                    $"The uninstall committed, but final cleanup recording requires recovery: {exception.Message}",
                    Changed: true);
            }
            return new(
                ModDeploymentResultState.Succeeded,
                "The Mod Bridge-managed mod was removed.",
                Changed: true);
        }
        catch (SimulatedProcessTerminationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollBackAsync(journal, targetPath, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var rolledBack = await RollBackAsync(journal with { Error = exception.Message }, targetPath, CancellationToken.None);
            return new(
                rolledBack ? ModDeploymentResultState.FailedAndRolledBack : ModDeploymentResultState.RecoveryRequired,
                rolledBack
                    ? $"The uninstall failed and the managed installation was restored: {exception.Message}"
                    : $"The uninstall failed and requires recovery: {exception.Message}");
        }
    }

    public async Task<ModDeploymentResult> RollBackCoordinatedAsync(
        string transactionId,
        CancellationToken cancellationToken = default) =>
        await RollBackCoordinatedCoreAsync(
            transactionId,
            operationLease: null,
            cancellationToken).ConfigureAwait(false);

    internal async Task<ModDeploymentResult> RollBackCoordinatedCoreAsync(
        string transactionId,
        LauncherOperationLease? operationLease,
        CancellationToken cancellationToken)
    {
        transactionId = ValidateTransactionId(transactionId);
        await using var acquiredLease = operationLease is null
            ? await operationLock.TryAcquireAsync(cancellationToken).ConfigureAwait(false)
            : null;
        if (operationLease is null && acquiredLease is null)
        {
            return new(ModDeploymentResultState.Busy, "Another Mod Bridge mutation is already active.");
        }
        using var retainedOperationScope = operationLease?.RetainFor(stateDirectory);
        ModDeploymentJournal? journal;
        try
        {
            journal = ReadJournal();
        }
        catch (Exception exception) when (IsStateReadFailure(exception))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                $"Mod Bridge deployment state could not be read: {exception.Message}");
        }
        if (journal is null || !string.Equals(journal.TransactionId, transactionId, StringComparison.Ordinal))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                "The provider-switch deployment journal does not match the recovery transaction.");
        }
        if (journal.Phase == ModDeploymentPhase.RolledBack)
        {
            return new(ModDeploymentResultState.Succeeded, "The provider-switch DLL is already rolled back.");
        }
        if (journal.Phase == ModDeploymentPhase.Failed)
        {
            return new(ModDeploymentResultState.Succeeded, "The provider-switch DLL never reached commit.");
        }
        if (journal.PreserveLiveArtifactDuringRecovery)
        {
            return PreserveLiveArtifactRecoveryResult();
        }
        if (isGameRunning(journal.GameDirectory))
        {
            return new(ModDeploymentResultState.GameRunning, "Close Star Trek Fleet Command before provider-switch recovery.");
        }
        var targetPath = Path.Combine(journal.GameDirectory, ManagedFileName);
        var rolledBack = await RollBackAsync(journal, targetPath, cancellationToken).ConfigureAwait(false);
        return new(
            rolledBack ? ModDeploymentResultState.Succeeded : ModDeploymentResultState.RecoveryRequired,
            rolledBack
                ? "The provider-switch DLL was restored to its exact prior state."
                : "The provider-switch DLL requires manual recovery.",
            ReadInstalledState(journal.GameDirectory),
            Changed: rolledBack);
    }

    public async Task<ModDeploymentResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            return new(ModDeploymentResultState.Busy, "Another Mod Bridge mutation is already active.");
        }

        ModDeploymentJournal? journal;
        ModInstalledArtifactState? installedState;
        try
        {
            journal = ReadJournal();
            installedState = journal is null ? null : ReadInstalledState(journal.GameDirectory);
        }
        catch (Exception exception) when (IsStateReadFailure(exception))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                $"Mod Bridge deployment state could not be read: {exception.Message}");
        }
        if (journal is null)
        {
            return new(ModDeploymentResultState.Succeeded, "No incomplete mod transaction was found.", installedState);
        }
        if (journal.PreserveLiveArtifactDuringRecovery)
        {
            return PreserveLiveArtifactRecoveryResult(installedState);
        }
        if (journal.Phase == ModDeploymentPhase.Committed)
        {
            if (HasSameVolumeTransactionResidue(journal))
            {
                return new(
                    ModDeploymentResultState.RecoveryRequired,
                    "A legacy committed transaction has preserved staging residue that requires manual review.",
                    installedState);
            }
            return new(
                ModDeploymentResultState.Succeeded,
                "No incomplete mod transaction was found.",
                installedState);
        }
        if (journal.Phase == ModDeploymentPhase.CleanupPending)
        {
            if (CanCleanCommittedResidue(journal))
            {
                var outcomeFailure = ValidateCommittedOutcome(journal);
                if (outcomeFailure is not null)
                {
                    WriteCleanupPendingError(journal, outcomeFailure);
                    return new(
                        ModDeploymentResultState.RecoveryRequired,
                        outcomeFailure,
                        installedState);
                }
                var cleanup = CleanupCommittedResidue(journal);
                if (!cleanup.IsSuccess)
                {
                    WriteCleanupPendingError(journal, cleanup.Message);
                    return new(
                        ModDeploymentResultState.RecoveryRequired,
                        cleanup.Message,
                        installedState);
                }
                try
                {
                    journal = await PersistPhaseAsync(
                        journal,
                        ModDeploymentPhase.Committed,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    WriteCleanupPendingError(journal, exception.Message);
                    return new(
                        ModDeploymentResultState.RecoveryRequired,
                        $"Cleanup completed, but its durable completion record failed: {exception.Message}",
                        installedState);
                }
                return new(
                    ModDeploymentResultState.Succeeded,
                    cleanup.Changed
                        ? "Completed transaction residue was removed."
                        : "No incomplete mod transaction was found.",
                    installedState,
                    Changed: cleanup.Changed);
            }
            return new(
                ModDeploymentResultState.Succeeded,
                "The coordinated transaction is committed; its outer recovery boundary owns final cleanup.",
                installedState);
        }
        if (IsTerminal(journal.Phase))
        {
            return new(ModDeploymentResultState.Succeeded, "No incomplete mod transaction was found.", installedState);
        }
        if (isGameRunning(journal.GameDirectory))
        {
            return new(ModDeploymentResultState.GameRunning, "Close Star Trek Fleet Command before recovery.");
        }

        var targetPath = Path.Combine(journal.GameDirectory, ManagedFileName);
        var rolledBack = await RollBackAsync(journal, targetPath, cancellationToken);
        return new(
            rolledBack ? ModDeploymentResultState.Succeeded : ModDeploymentResultState.RecoveryRequired,
            rolledBack ? "The incomplete mod transaction was rolled back." : "The incomplete transaction requires manual recovery.",
            ReadInstalledState(journal.GameDirectory),
            Changed: rolledBack);
    }

    private static ModDeploymentResult? ValidateRequest(
        string gameDirectory,
        ModReleaseArtifact artifact,
        out string normalizedGameDirectory)
    {
        normalizedGameDirectory = string.Empty;
        if (!string.Equals(artifact.FileName, ManagedFileName, StringComparison.OrdinalIgnoreCase)
            || artifact.Size <= 0
            || artifact.Size > MaximumArtifactSize
            || string.IsNullOrWhiteSpace(artifact.ExpectedVersion)
            || artifact.ExpectedProductVersion is not null
                && (artifact.ExpectedProductVersion.Length is <= 0 or > 160
                    || artifact.ExpectedProductVersion.Any(char.IsControl))
            || !TryNormalizeSha256(artifact.Sha256, out _)
            || artifact.DownloadUri is null
            || !artifact.DownloadUri.IsAbsoluteUri
            || artifact.DownloadUri.Scheme != Uri.UriSchemeHttps)
        {
            return new(ModDeploymentResultState.VerificationFailed, "The selected release artifact metadata is invalid.");
        }
        if (artifact.RuntimeManifest is not null
            && (artifact.RuntimeManifest.FileName != ArtifactBoundRuntimeManifestParser.ManagedFileName
                || artifact.RuntimeManifest.Size is <= 0 or > ArtifactBoundRuntimeManifestParser.MaximumManifestBytes
                || !TryNormalizeSha256(artifact.RuntimeManifest.Sha256, out _)
                || artifact.RuntimeManifest.ExpectedSourceRevision is not { Length: 40 }
                || !artifact.RuntimeManifest.ExpectedSourceRevision.All(Uri.IsHexDigit)
                || artifact.RuntimeManifest.ExpectedRepository is not { Length: > 0 and <= 160 }
                || artifact.RuntimeManifest.ExpectedRepository.Count(character => character == '/') != 1
                || artifact.RuntimeManifest.ExpectedTag is not { Length: > 0 and <= 160 }
                || artifact.RuntimeManifest.DownloadUri is null
                || !artifact.RuntimeManifest.DownloadUri.IsAbsoluteUri
                || artifact.RuntimeManifest.DownloadUri.Scheme != Uri.UriSchemeHttps))
        {
            return new(
                ModDeploymentResultState.VerificationFailed,
                "The selected runtime-manifest metadata is invalid.");
        }

        try
        {
            normalizedGameDirectory = Path.GetFullPath(gameDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new(ModDeploymentResultState.InvalidGameTarget, exception.Message);
        }

        var validation = GameInstallValidator.Validate(normalizedGameDirectory);
        if (!validation.IsValid)
        {
            return new(ModDeploymentResultState.InvalidGameTarget, validation.Message);
        }

        return null;
    }

    private static ModDeploymentResult? VerifyDownload(ModArtifactDownload download, ModReleaseArtifact artifact)
        => VerifyDownload(download, artifact.Size, artifact.Sha256, "artifact");

    private static ModDeploymentResult? VerifyDownload(
        ModArtifactDownload download,
        ModRuntimeManifestArtifact artifact) =>
        VerifyDownload(download, artifact.Size, artifact.Sha256, "runtime manifest");

    private static ModDeploymentResult? VerifyDownload(
        ModArtifactDownload download,
        long expectedSize,
        string expectedSha256,
        string subject)
    {
        if (download.StatusCode != HttpStatusCode.OK)
        {
            return new(
                ModDeploymentResultState.DownloadRejected,
                $"The {subject} request returned HTTP {(int)download.StatusCode}.");
        }

        if (download.DeclaredContentLength is not null && download.DeclaredContentLength != expectedSize)
        {
            return new(
                ModDeploymentResultState.VerificationFailed,
                $"The {subject} HTTP content length does not match release metadata.");
        }

        if (download.Contents.LongLength != expectedSize)
        {
            return new(
                ModDeploymentResultState.VerificationFailed,
                $"The downloaded {subject} size does not match release metadata.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(download.Contents));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(expectedSha256)))
        {
            return new(
                ModDeploymentResultState.VerificationFailed,
                $"The downloaded {subject} SHA-256 does not match release metadata.");
        }

        return null;
    }

    private async Task<ModDeploymentJournal> PersistPhaseAsync(
        ModDeploymentJournal journal,
        ModDeploymentPhase phase,
        CancellationToken cancellationToken)
    {
        var updated = journal with { Phase = phase, UpdatedAtUtc = timeProvider.GetUtcNow() };
        WriteJsonAtomically(JournalPath, updated);
        if (afterPhasePersisted is not null)
        {
            await afterPhasePersisted(phase, cancellationToken);
        }
        return updated;
    }

    private static ModDeploymentResult PreserveLiveArtifactRecoveryResult(
        ModInstalledArtifactState? installedState = null) =>
        new(
            ModDeploymentResultState.RecoveryRequired,
            "Automatic recovery was stopped because the live mod DLL did not match its exact commit identity. "
                + "The live file was preserved for explicit recovery.",
            installedState);

    private ValueTask CheckpointAsync(
        ModDeploymentFileCheckpoint checkpoint,
        CancellationToken cancellationToken) => afterFileCheckpoint is null
        ? ValueTask.CompletedTask
        : afterFileCheckpoint(checkpoint, cancellationToken);

    private void ValidateCommitPreconditions(
        ModDeploymentJournal journal,
        ExactFileMutation exactStagedArtifact,
        ExactFileMutation? exactStagedRuntimeManifest,
        ExactFileMutation? exactExistingArtifact,
        ExactFileMutation? exactExistingRuntimeManifest)
    {
        var gameValidation = GameInstallValidator.Validate(journal.GameDirectory);
        if (!gameValidation.IsValid)
        {
            throw new InvalidDataException(
                $"The game installation changed while the release was being prepared: {gameValidation.Message}");
        }
        if (isGameRunning(journal.GameDirectory))
        {
            throw new InvalidOperationException(
                "Star Trek Fleet Command started while the release was being prepared; no mod files were changed.");
        }
        ValidateOpenedLiveMember(
            exactExistingArtifact,
            journal.HadExistingArtifact,
            journal.ExistingArtifactIdentity,
            "DLL");
        ValidateOpenedLiveMember(
            exactExistingRuntimeManifest,
            journal.HadExistingRuntimeManifest,
            journal.ExistingRuntimeManifestIdentity,
            "runtime manifest");
        VerifyFile(exactStagedArtifact.CaptureRevision(), journal.Artifact);
        if (journal.Artifact.RuntimeManifest is not null)
        {
            VerifyFile(
                exactStagedRuntimeManifest?.CaptureRevision()
                    ?? throw new InvalidDataException(
                        "The runtime-manifest stage lost its exact commit handle."),
                journal.Artifact.RuntimeManifest);
        }
        var previous = journal.PreviousInstalledState;
        var failure = ValidateDeclaredBackup(
            previous?.PreviousArtifactBackupPath,
            previous?.PreviousArtifactBackupIdentity,
            "adopted DLL");
        failure ??= ValidateDeclaredBackup(
            previous?.PreviousRuntimeManifestBackupPath,
            previous?.PreviousRuntimeManifestBackupIdentity,
            "adopted runtime manifest");
        if (failure is not null)
        {
            throw new InvalidDataException(failure);
        }
    }

    private void ValidateUninstallCommitPreconditions(
        ModDeploymentJournal journal,
        ModInstalledArtifactState installedState,
        ExactFileMutation exactManagedArtifact,
        ExactFileMutation? exactManagedRuntimeManifest)
    {
        var validation = GameInstallValidator.Validate(journal.GameDirectory);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                $"The game installation changed before uninstall: {validation.Message}");
        }
        if (isGameRunning(journal.GameDirectory))
        {
            throw new InvalidOperationException(
                "Star Trek Fleet Command started before uninstall; no mod files were changed.");
        }
        ValidateOpenedLiveMember(
            exactManagedArtifact,
            existed: true,
            journal.ExistingArtifactIdentity,
            "managed DLL");
        if (installedState.RuntimeManifest is not null)
        {
            ValidateOpenedLiveMember(
                exactManagedRuntimeManifest,
                existed: true,
                journal.ExistingRuntimeManifestIdentity,
                "managed runtime manifest");
        }
        var backupFailure = ValidateDeclaredBackup(
            installedState.PreviousArtifactBackupPath,
            installedState.PreviousArtifactBackupIdentity,
            "adopted DLL");
        backupFailure ??= ValidateDeclaredBackup(
            installedState.PreviousRuntimeManifestBackupPath,
            installedState.PreviousRuntimeManifestBackupIdentity,
            "adopted runtime manifest");
        if (backupFailure is not null)
        {
            throw new InvalidDataException(backupFailure);
        }
    }

    private static void ValidateUnchangedLiveMember(
        string path,
        bool existed,
        ModArtifactIdentityReceipt? identity,
        string subject)
    {
        if (existed ? identity is null || !MatchesIdentity(path, identity) : File.Exists(path))
        {
            throw new InvalidDataException(
                $"The live {subject} changed while the release was being prepared; no live files were replaced.");
        }
    }

    private static ExactFileMutation? OpenValidatedLiveMember(
        string path,
        bool existed,
        ModArtifactIdentityReceipt? identity,
        string subject)
    {
        if (!existed)
        {
            ValidateUnchangedLiveMember(path, existed: false, identity, subject);
            return null;
        }
        if (identity is null)
        {
            throw new InvalidDataException(
                $"The live {subject} has no exact transaction identity and was preserved.");
        }
        var exact = ExactFileMutation.Open(path);
        try
        {
            ValidateOpenedLiveMember(exact, existed: true, identity, subject);
            return exact;
        }
        catch
        {
            exact.Dispose();
            throw;
        }
    }

    private static void ValidateOpenedLiveMember(
        ExactFileMutation? exact,
        bool existed,
        ModArtifactIdentityReceipt? identity,
        string subject)
    {
        if (!existed)
        {
            if (exact is not null)
            {
                throw new InvalidDataException(
                    $"The live {subject} appeared while the release was being prepared; no live files were replaced.");
            }
            return;
        }
        if (exact is null || identity is null || !MatchesIdentity(exact.CaptureRevision(), identity))
        {
            throw new InvalidDataException(
                $"The live {subject} changed while the release was being prepared; no live files were replaced.");
        }
    }

    private async Task<bool> RollBackAsync(
        ModDeploymentJournal journal,
        string targetPath,
        CancellationToken cancellationToken,
        ExactFileRevision? exactLiveArtifactRevision = null)
    {
        try
        {
            NormalizeIncompleteOwnedCopyStages(journal);
            var runtimeManifestPath = RuntimeManifestTargetPath(journal.GameDirectory);
            var plan = BuildRollbackPlan(journal, targetPath, runtimeManifestPath);
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.RollingBack, cancellationToken);
            plan = await MaterializeSameVolumeRollbackStagesAsync(plan, journal, cancellationToken);

            var dllRollbackStage = plan.Dll.BackupPath;
            ApplyRollbackMember(plan.Dll, exactLiveArtifactRevision);
            if (dllRollbackStage is not null && !File.Exists(dllRollbackStage))
            {
                ReleaseOwnedCopyStage(dllRollbackStage);
            }
            await CheckpointAsync(ModDeploymentFileCheckpoint.RollbackDllRestored, cancellationToken);
            var runtimeRollbackStage = plan.RuntimeManifest?.BackupPath;
            ApplyRollbackMember(plan.RuntimeManifest);
            if (runtimeRollbackStage is not null && !File.Exists(runtimeRollbackStage))
            {
                ReleaseOwnedCopyStage(runtimeRollbackStage);
            }
            await CheckpointAsync(ModDeploymentFileCheckpoint.RollbackRuntimeManifestRestored, cancellationToken);
            VerifyRollbackMember(plan.Dll);
            VerifyRollbackMember(plan.RuntimeManifest);

            if (journal.Operation == ModDeploymentOperation.Uninstall)
            {
                DeleteVerifiedCopyStageResidue(
                    journal.StagePath,
                    journal.PreviousInstalledState?.PreviousArtifactBackupIdentity,
                    "DLL stage");
                DeleteVerifiedCopyStageResidue(
                    RuntimeManifestStagePath(journal),
                    journal.PreviousInstalledState?.PreviousRuntimeManifestBackupIdentity,
                    "runtime-manifest stage");
            }
            else
            {
                DeleteVerifiedResidue(journal.StagePath, Identity(journal.Artifact), "DLL stage");
                DeleteVerifiedResidue(
                    RuntimeManifestStagePath(journal),
                    journal.Artifact.RuntimeManifest is null
                        ? null
                        : Identity(journal.Artifact.RuntimeManifest),
                    "runtime-manifest stage");
            }
            DeleteVerifiedCopyStageResidue(
                DurablePromotionStagePath(journal),
                journal.ExistingArtifactIdentity,
                "durable DLL promotion stage");
            DeleteVerifiedCopyStageResidue(
                RuntimeManifestDurablePromotionStagePath(journal),
                journal.ExistingRuntimeManifestIdentity,
                "durable runtime-manifest promotion stage");
            DeleteVerifiedCopyStageResidue(
                RollbackRestoreStagePath(journal),
                journal.ExistingArtifactIdentity,
                "DLL rollback restore stage");
            DeleteVerifiedCopyStageResidue(
                RuntimeManifestRollbackRestoreStagePath(journal),
                journal.ExistingRuntimeManifestIdentity,
                "runtime-manifest rollback restore stage");
            DeleteVerifiedResidue(
                journal.DurableBackupPath,
                journal.ExistingArtifactIdentity,
                "redundant durable DLL rollback");
            DeleteVerifiedResidue(
                RuntimeManifestDurableBackupPath(journal),
                journal.ExistingRuntimeManifestIdentity,
                "redundant durable runtime-manifest rollback");
            RestoreInstalledState(journal.GameDirectory, journal.PreviousInstalledState);
            await PersistPhaseAsync(
                journal with { PreserveLiveArtifactDuringRecovery = false },
                ModDeploymentPhase.RolledBack,
                cancellationToken);
            return true;
        }
        catch (SimulatedProcessTerminationException)
        {
            throw;
        }
        catch (Exception rollbackException)
        {
            try
            {
                WriteJsonAtomically(
                    JournalPath,
                    journal with
                    {
                        Phase = ModDeploymentPhase.RollingBack,
                        Error = $"{journal.Error}; rollback failed: {rollbackException.Message}",
                        UpdatedAtUtc = timeProvider.GetUtcNow(),
                    });
            }
            catch
            {
                // Preserve the original recovery paths when even journal persistence is unavailable.
            }
            return false;
        }
    }

    private RollbackPlan BuildRollbackPlan(
        ModDeploymentJournal journal,
        string targetPath,
        string runtimeManifestPath)
    {
        if (journal.Operation == ModDeploymentOperation.Deploy
            && journal.PreviousInstalledState is not null)
        {
            var retainedFailure = ValidateDeclaredBackup(
                journal.PreviousInstalledState.PreviousArtifactBackupPath,
                journal.PreviousInstalledState.PreviousArtifactBackupIdentity,
                "retained adopted DLL");
            retainedFailure ??= ValidateDeclaredBackup(
                journal.PreviousInstalledState.PreviousRuntimeManifestBackupPath,
                journal.PreviousInstalledState.PreviousRuntimeManifestBackupIdentity,
                "retained adopted runtime manifest");
            if (retainedFailure is not null)
            {
                throw new InvalidDataException(retainedFailure);
            }
        }
        var priorDllIdentity = journal.ExistingArtifactIdentity
            ?? (journal.PreviousInstalledState is null
                ? null
                : new(journal.PreviousInstalledState.Size, journal.PreviousInstalledState.Sha256));
        var priorRuntimeIdentity = journal.ExistingRuntimeManifestIdentity
            ?? (journal.PreviousInstalledState?.RuntimeManifest is null
                ? null
                : new(
                    journal.PreviousInstalledState.RuntimeManifest.Size,
                    journal.PreviousInstalledState.RuntimeManifest.Sha256));
        var dll = BuildRollbackMember(
            targetPath,
            journal.HadExistingArtifact,
            priorDllIdentity,
            Identity(journal.Artifact),
            journal.TargetArtifactFileIdentity,
            FindExactBackup(
                journal.DurableBackupPath,
                journal.SameVolumeBackupPath,
                priorDllIdentity,
                "prior DLL"),
            journal.Operation == ModDeploymentOperation.Uninstall
                ? journal.PreviousInstalledState?.PreviousArtifactBackupPath
                : null,
            journal.PreviousInstalledState?.PreviousArtifactBackupIdentity,
            journal.RestoredAdoptedArtifactFileIdentity,
            "DLL");
        var shouldHandleRuntime = ShouldMutateRuntimeManifest(journal) || journal.HadExistingRuntimeManifest;
        var runtime = shouldHandleRuntime
            ? BuildRollbackMember(
                runtimeManifestPath,
                journal.HadExistingRuntimeManifest,
                priorRuntimeIdentity,
                journal.Artifact.RuntimeManifest is null ? null : Identity(journal.Artifact.RuntimeManifest),
                journal.TargetRuntimeManifestFileIdentity,
                FindExactBackup(
                    RuntimeManifestDurableBackupPath(journal),
                    RuntimeManifestSameVolumeBackupPath(journal),
                    priorRuntimeIdentity,
                    "prior runtime manifest"),
                journal.Operation == ModDeploymentOperation.Uninstall
                    ? journal.PreviousInstalledState?.PreviousRuntimeManifestBackupPath
                    : null,
                journal.PreviousInstalledState?.PreviousRuntimeManifestBackupIdentity,
                journal.RestoredAdoptedRuntimeManifestFileIdentity,
                "runtime manifest")
            : null;
        ValidateResidue(
            journal.StagePath,
            journal.Operation == ModDeploymentOperation.Uninstall
                ? journal.PreviousInstalledState?.PreviousArtifactBackupIdentity
                : Identity(journal.Artifact),
            "DLL stage");
        ValidateResidue(
            RuntimeManifestStagePath(journal),
            journal.Operation == ModDeploymentOperation.Uninstall
                ? journal.PreviousInstalledState?.PreviousRuntimeManifestBackupIdentity
                : journal.Artifact.RuntimeManifest is null ? null : Identity(journal.Artifact.RuntimeManifest),
            "runtime-manifest stage");
        ValidateResidue(
            DurablePromotionStagePath(journal),
            journal.ExistingArtifactIdentity,
            "durable DLL promotion stage");
        ValidateResidue(
            RuntimeManifestDurablePromotionStagePath(journal),
            journal.ExistingRuntimeManifestIdentity,
            "durable runtime-manifest promotion stage");
        return new(dll, runtime);
    }

    private void NormalizeIncompleteOwnedCopyStages(ModDeploymentJournal journal)
    {
        NormalizeIncompleteOwnedCopyStage(
            DurablePromotionStagePath(journal),
            journal.ExistingArtifactIdentity,
            journal.SameVolumeBackupPath,
            journal.DurableBackupPath);
        NormalizeIncompleteOwnedCopyStage(
            RuntimeManifestDurablePromotionStagePath(journal),
            journal.ExistingRuntimeManifestIdentity,
            RuntimeManifestSameVolumeBackupPath(journal),
            RuntimeManifestDurableBackupPath(journal));
        NormalizeIncompleteOwnedCopyStage(
            RollbackRestoreStagePath(journal),
            journal.ExistingArtifactIdentity,
            journal.DurableBackupPath);
        NormalizeIncompleteOwnedCopyStage(
            RuntimeManifestRollbackRestoreStagePath(journal),
            journal.ExistingRuntimeManifestIdentity,
            RuntimeManifestDurableBackupPath(journal));
        if (journal.Operation == ModDeploymentOperation.Uninstall)
        {
            NormalizeIncompleteOwnedCopyStage(
                journal.StagePath,
                journal.PreviousInstalledState?.PreviousArtifactBackupIdentity,
                journal.PreviousInstalledState?.PreviousArtifactBackupPath);
            NormalizeIncompleteOwnedCopyStage(
                RuntimeManifestStagePath(journal),
                journal.PreviousInstalledState?.PreviousRuntimeManifestBackupIdentity,
                journal.PreviousInstalledState?.PreviousRuntimeManifestBackupPath);
        }
    }

    private void NormalizeIncompleteOwnedCopyStage(
        string stagePath,
        ModArtifactIdentityReceipt? identity,
        params string?[] authoritativeSources)
    {
        if (!File.Exists(stagePath))
        {
            ReleaseOwnedCopyStage(stagePath);
            return;
        }
        if (identity is null)
        {
            throw new InvalidDataException(
                "An incomplete copy stage has no expected artifact identity and was preserved.");
        }
        var receipt = ReadOwnedCopyStage(stagePath);
        if (receipt is null)
        {
            throw new InvalidDataException(
                "An incomplete copy stage has no durable file-identity receipt and was preserved.");
        }
        using var exactStage = ExactFileMutation.OpenForMetadata(stagePath);
        var stageRevision = exactStage.CaptureRevision();
        if (exactStage.Identity != receipt.FileIdentity)
        {
            throw new InvalidDataException(
                "An incomplete copy stage was replaced after Bridge recorded it and was preserved.");
        }
        if (receipt.Phase == ModDeploymentCopyStagePhase.Complete)
        {
            if (receipt.LastOwnedRevision is null
                || !receipt.LastOwnedRevision.Matches(stageRevision)
                || !MatchesIdentity(stageRevision, identity))
            {
                throw new InvalidDataException(
                    "A completed copy stage changed after Bridge recorded it and was preserved.");
            }
            return;
        }
        if (!authoritativeSources.Any(path =>
                !string.IsNullOrWhiteSpace(path) && MatchesIdentity(path, identity)))
        {
            throw new InvalidDataException(
                "An interrupted copy stage has no exact authoritative source and was preserved.");
        }
        exactStage.DeleteExactIgnoringReadOnly();
        ReleaseOwnedCopyStage(stagePath);
    }

    private void VerifyCompletedOwnedCopyStage(
        string stagePath,
        ModArtifactIdentityReceipt identity,
        string subject)
    {
        var receipt = ReadOwnedCopyStage(stagePath)
            ?? throw new InvalidDataException($"The {subject} has no durable ownership receipt.");
        using var exact = ExactFileMutation.OpenForMetadata(stagePath);
        var revision = exact.CaptureRevision();
        if (receipt.Phase != ModDeploymentCopyStagePhase.Complete
            || exact.Identity != receipt.FileIdentity
            || receipt.LastOwnedRevision is null
            || !receipt.LastOwnedRevision.Matches(revision)
            || !MatchesIdentity(revision, identity))
        {
            throw new InvalidDataException($"The {subject} was replaced or changed and was preserved.");
        }
    }

    private CandidateFileIdentity GetCompletedOwnedCopyStageFileIdentity(
        string stagePath,
        string subject)
    {
        var receipt = ReadOwnedCopyStage(stagePath)
            ?? throw new InvalidDataException($"The {subject} has no durable ownership receipt.");
        if (receipt.Phase != ModDeploymentCopyStagePhase.Complete
            || receipt.LastOwnedRevision is null
            || receipt.LastOwnedRevision.Identity != receipt.FileIdentity)
        {
            throw new InvalidDataException($"The {subject} ownership receipt is incomplete.");
        }
        return receipt.FileIdentity;
    }

    private void MoveCompletedOwnedCopyStage(
        string stagePath,
        string destinationPath,
        ModArtifactIdentityReceipt identity,
        string subject)
    {
        var receipt = ReadOwnedCopyStage(stagePath)
            ?? throw new InvalidDataException($"The {subject} has no durable ownership receipt.");
        using (var exact = ExactFileMutation.OpenForMetadata(stagePath))
        {
            var revision = exact.CaptureRevision();
            if (receipt.Phase != ModDeploymentCopyStagePhase.Complete
                || exact.Identity != receipt.FileIdentity
                || receipt.LastOwnedRevision is null
                || !receipt.LastOwnedRevision.Matches(revision)
                || !MatchesIdentity(revision, identity))
            {
                throw new InvalidDataException($"The {subject} was replaced or changed and was preserved.");
            }
            exact.MoveExactNoReplace(destinationPath);
            if (!receipt.LastOwnedRevision.Matches(exact.CaptureRevision()))
            {
                throw new InvalidDataException(
                    $"The {subject} changed during its exact ownership-preserving move.");
            }
        }
        ReleaseOwnedCopyStage(stagePath);
    }

    private static RollbackMember BuildRollbackMember(
        string livePath,
        bool hadExisting,
        ModArtifactIdentityReceipt? priorIdentity,
        ModArtifactIdentityReceipt? targetIdentity,
        ModFileIdentityReceipt? targetFileIdentity,
        string? backupPath,
        string? displacedLiveDestination,
        ModArtifactIdentityReceipt? displacedLiveIdentity,
        ModFileIdentityReceipt? displacedLiveFileIdentity,
        string subject)
    {
        var liveExists = File.Exists(livePath);
        if (hadExisting && priorIdentity is null)
        {
            throw new InvalidDataException($"The {subject} rollback has no exact prior identity receipt.");
        }
        if (displacedLiveDestination is not null)
        {
            if (displacedLiveIdentity is null)
            {
                throw new InvalidDataException($"The displaced {subject} has no exact backup identity receipt.");
            }
            var displacedExists = File.Exists(displacedLiveDestination);
            if (displacedExists && !MatchesIdentity(displacedLiveDestination, displacedLiveIdentity))
            {
                throw new InvalidDataException($"The retained adopted {subject} backup changed.");
            }
            if (hadExisting
                && liveExists
                && MatchesIdentity(livePath, priorIdentity!)
                && displacedExists)
            {
                return new(
                    livePath,
                    null,
                    RestoreExisting: true,
                    displacedLiveDestination,
                    displacedLiveIdentity,
                    displacedLiveFileIdentity,
                    priorIdentity,
                    targetIdentity,
                    targetFileIdentity);
            }
            if (backupPath is null)
            {
                if (!liveExists || !MatchesIdentity(livePath, priorIdentity!) || !displacedExists)
                {
                    throw new InvalidDataException($"The completed {subject} rollback pair is incomplete.");
                }
                return new(
                    livePath,
                    null,
                    RestoreExisting: true,
                    displacedLiveDestination,
                    displacedLiveIdentity,
                    displacedLiveFileIdentity,
                    priorIdentity,
                    targetIdentity,
                    targetFileIdentity);
            }
            if (liveExists)
            {
                if (!MatchesIdentity(livePath, displacedLiveIdentity))
                {
                    throw new InvalidDataException($"The live/displaced {subject} rollback state is ambiguous.");
                }
            }
            else if (!displacedExists)
            {
                throw new InvalidDataException($"The adopted {subject} is missing from both rollback locations.");
            }
            return new(
                livePath,
                backupPath,
                RestoreExisting: true,
                displacedLiveDestination,
                displacedLiveIdentity,
                displacedLiveFileIdentity,
                priorIdentity,
                targetIdentity,
                targetFileIdentity);
        }
        if (hadExisting && liveExists && MatchesIdentity(livePath, priorIdentity!))
        {
            return new(
                livePath,
                null,
                RestoreExisting: true,
                null,
                null,
                null,
                priorIdentity,
                targetIdentity,
                targetFileIdentity);
        }
        if (backupPath is null)
        {
            if (hadExisting && (!liveExists || !MatchesIdentity(livePath, priorIdentity!)))
            {
                throw new InvalidDataException($"The exact prior {subject} backup is missing.");
            }
            if (!hadExisting && liveExists && (targetIdentity is null || !MatchesIdentity(livePath, targetIdentity)))
            {
                throw new InvalidDataException($"The live {subject} is not an exact transaction artifact.");
            }
            return new(
                livePath,
                null,
                hadExisting,
                null,
                null,
                null,
                priorIdentity,
                targetIdentity,
                targetFileIdentity);
        }
        if (liveExists
            && (targetIdentity is null || !MatchesIdentity(livePath, targetIdentity))
            && (priorIdentity is null || !MatchesIdentity(livePath, priorIdentity)))
        {
            throw new InvalidDataException($"The live {subject} changed during rollback.");
        }
        return new(
            livePath,
            backupPath,
            RestoreExisting: true,
            null,
            null,
            null,
            priorIdentity,
            targetIdentity,
            targetFileIdentity);
    }

    private static string? FindExactBackup(
        string durablePath,
        string sameVolumePath,
        ModArtifactIdentityReceipt? identity,
        string subject)
    {
        var existing = new[] { durablePath, sameVolumePath }.Where(File.Exists).ToArray();
        if (existing.Length == 0)
        {
            return null;
        }
        if (identity is null || existing.Any(path => !MatchesIdentity(path, identity)))
        {
            throw new InvalidDataException($"The {subject} rollback copy is unrecognized or changed.");
        }
        return File.Exists(sameVolumePath) ? sameVolumePath : durablePath;
    }

    private async Task<RollbackPlan> MaterializeSameVolumeRollbackStagesAsync(
        RollbackPlan plan,
        ModDeploymentJournal journal,
        CancellationToken cancellationToken)
    {
        var dll = await MaterializeSameVolumeRollbackStageAsync(
            plan.Dll,
            journal.DurableBackupPath,
            RollbackRestoreStagePath(journal),
            ModDeploymentFileCheckpoint.RollbackDllRestoreCopyStarted,
            ModDeploymentFileCheckpoint.RollbackDllRestoreStaged,
            cancellationToken);
        var runtime = plan.RuntimeManifest is null
            ? null
            : await MaterializeSameVolumeRollbackStageAsync(
                plan.RuntimeManifest,
                RuntimeManifestDurableBackupPath(journal),
                RuntimeManifestRollbackRestoreStagePath(journal),
                ModDeploymentFileCheckpoint.RollbackRuntimeManifestRestoreCopyStarted,
                ModDeploymentFileCheckpoint.RollbackRuntimeManifestRestoreStaged,
                cancellationToken);
        return new(dll, runtime);
    }

    private async Task<RollbackMember> MaterializeSameVolumeRollbackStageAsync(
        RollbackMember member,
        string durablePath,
        string restoreStagePath,
        ModDeploymentFileCheckpoint copyStartedCheckpoint,
        ModDeploymentFileCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (member.BackupPath is null || !PathEquals(member.BackupPath, durablePath))
        {
            return member;
        }
        if (member.PriorIdentity is null)
        {
            throw new InvalidDataException("A durable rollback copy has no exact identity receipt.");
        }
        if (!File.Exists(restoreStagePath))
        {
            await CopyFileDurablyAsync(
                durablePath,
                restoreStagePath,
                member.PriorIdentity,
                copyStartedCheckpoint,
                cancellationToken);
        }
        VerifyFile(restoreStagePath, member.PriorIdentity, "same-volume rollback restore stage");
        VerifyCompletedOwnedCopyStage(
            restoreStagePath,
            member.PriorIdentity,
            "same-volume rollback restore stage");
        await CheckpointAsync(checkpoint, cancellationToken);
        return member with { BackupPath = restoreStagePath };
    }

    private void ApplyRollbackMember(
        RollbackMember? member,
        ExactFileRevision? exactLiveRevision = null)
    {
        if (member is null)
        {
            return;
        }
        if (exactLiveRevision is not null)
        {
            ApplyExactRollbackMember(member, exactLiveRevision);
            return;
        }
        if (member.BackupPath is null)
        {
            if (!member.RestoreExisting)
            {
                DeleteRollbackLiveMember(member);
            }
            return;
        }
        if (File.Exists(member.LivePath))
        {
            if (member.DisplacedLiveDestination is not null)
            {
                if (File.Exists(member.DisplacedLiveDestination))
                {
                    DeleteOrMoveRestoredAdoptedMember(member, destinationPath: null);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(member.DisplacedLiveDestination)!);
                    DeleteOrMoveRestoredAdoptedMember(
                        member,
                        member.DisplacedLiveDestination);
                }
            }
            else
            {
                DeleteRollbackLiveMember(member);
            }
        }
        if (File.Exists(CopyStageReceiptPath(member.BackupPath)))
        {
            MoveCompletedOwnedCopyStage(
                member.BackupPath,
                member.LivePath,
                member.PriorIdentity
                    ?? throw new InvalidDataException(
                        "The rollback copy has no exact prior identity receipt."),
                "rollback restore stage");
        }
        else
        {
            File.Move(member.BackupPath, member.LivePath);
        }
    }

    private static void DeleteOrMoveRestoredAdoptedMember(
        RollbackMember member,
        string? destinationPath)
    {
        var contentIdentity = member.DisplacedLiveIdentity
            ?? throw new InvalidDataException(
                "The displaced rollback member has no exact content receipt.");
        var fileIdentity = member.DisplacedLiveFileIdentity
            ?? throw new InvalidDataException(
                "The restored adopted rollback member has no exact file-identity receipt and was preserved.");
        using var exact = ExactFileMutation.Open(member.LivePath);
        var revision = exact.CaptureRevision();
        if (!MatchesIdentity(revision, contentIdentity)
            || !MatchesFileIdentity(exact.Identity, fileIdentity))
        {
            throw new InvalidDataException(
                "The restored adopted rollback member was replaced or changed and was preserved.");
        }
        if (destinationPath is null)
        {
            exact.DeleteExactIgnoringReadOnly();
        }
        else
        {
            exact.MoveExactNoReplace(destinationPath);
        }
    }

    private static void DeleteRollbackLiveMember(RollbackMember member)
    {
        if (!File.Exists(member.LivePath))
        {
            return;
        }
        using var exact = ExactFileMutation.Open(member.LivePath);
        var revision = exact.CaptureRevision();
        if (member.TargetIdentity is not null && MatchesIdentity(revision, member.TargetIdentity))
        {
            if (member.TargetFileIdentity is null
                || !MatchesFileIdentity(exact.Identity, member.TargetFileIdentity))
            {
                throw new InvalidDataException(
                    "The live rollback member has the target bytes but not the exact transaction file identity "
                        + "and was preserved.");
            }
        }
        else if (member.PriorIdentity is null || !MatchesIdentity(revision, member.PriorIdentity))
        {
            throw new InvalidDataException(
                "The live rollback member is not an exact transaction-owned revision and was preserved.");
        }
        exact.DeleteExactIgnoringReadOnly();
    }

    private static void ApplyExactRollbackMember(
        RollbackMember member,
        ExactFileRevision expectedLiveRevision)
    {
        if (member.BackupPath is not null)
        {
            throw new InvalidDataException(
                "Exact rollback requires restoring a prior mod DLL; automatic recovery preserved the live file "
                    + "and the rollback evidence for explicit recovery.");
        }

        using var exactLive = ExactFileMutation.Open(member.LivePath);
        if (!expectedLiveRevision.Matches(exactLive.CaptureRevision()))
        {
            throw new InvalidDataException(
                "The live mod DLL changed before exact rollback; the current file was preserved.");
        }

        if (member.BackupPath is null)
        {
            if (!member.RestoreExisting)
            {
                exactLive.DeleteExact();
            }
            return;
        }
    }

    private static void VerifyRollbackMember(RollbackMember? member)
    {
        if (member is null)
        {
            return;
        }
        if (member.RestoreExisting)
        {
            if (member.PriorIdentity is null || !MatchesIdentity(member.LivePath, member.PriorIdentity))
            {
                throw new InvalidDataException("Rollback did not restore the exact prior managed artifact.");
            }
        }
        else if (File.Exists(member.LivePath))
        {
            throw new InvalidDataException("Rollback did not remove the transaction artifact.");
        }
        if (member.DisplacedLiveDestination is not null
            && (member.DisplacedLiveIdentity is null
                || !MatchesIdentity(member.DisplacedLiveDestination, member.DisplacedLiveIdentity)))
        {
            throw new InvalidDataException("Rollback did not preserve the exact adopted artifact.");
        }
    }

    private static void ValidateResidue(
        string path,
        ModArtifactIdentityReceipt? identity,
        string subject)
    {
        if (File.Exists(path) && (identity is null || !MatchesIdentity(path, identity)))
        {
            throw new InvalidDataException($"The {subject} is unrecognized and was preserved.");
        }
    }

    private static void DeleteVerifiedResidue(
        string path,
        ModArtifactIdentityReceipt? identity,
        string subject)
    {
        ValidateResidue(path, identity, subject);
        if (identity is not null)
        {
            DeleteOwnedFile(path, identity, subject);
        }
    }

    private void DeleteVerifiedCopyStageResidue(
        string path,
        ModArtifactIdentityReceipt? identity,
        string subject)
    {
        if (!File.Exists(path))
        {
            ReleaseOwnedCopyStage(path);
            return;
        }
        if (identity is null)
        {
            throw new InvalidDataException($"The {subject} has no exact identity and was preserved.");
        }
        var receipt = ReadOwnedCopyStage(path);
        if (receipt is null)
        {
            throw new InvalidDataException(
                $"The {subject} has no durable ownership receipt and was preserved.");
        }
        using var exact = ExactFileMutation.OpenForMetadata(path);
        var revision = exact.CaptureRevision();
        if (exact.Identity != receipt.FileIdentity
            || receipt.Phase == ModDeploymentCopyStagePhase.Complete
                && (receipt.LastOwnedRevision is null
                    || !receipt.LastOwnedRevision.Matches(revision)
                    || !MatchesIdentity(revision, identity)))
        {
            throw new InvalidDataException($"The {subject} was replaced or changed and was preserved.");
        }
        exact.DeleteExactIgnoringReadOnly();
        ReleaseOwnedCopyStage(path);
    }

    private sealed record RollbackPlan(RollbackMember Dll, RollbackMember? RuntimeManifest);

    private sealed record RollbackMember(
        string LivePath,
        string? BackupPath,
        bool RestoreExisting,
        string? DisplacedLiveDestination,
        ModArtifactIdentityReceipt? DisplacedLiveIdentity,
        ModFileIdentityReceipt? DisplacedLiveFileIdentity,
        ModArtifactIdentityReceipt? PriorIdentity,
        ModArtifactIdentityReceipt? TargetIdentity,
        ModFileIdentityReceipt? TargetFileIdentity);

    private void RestoreInstalledState(
        string gameDirectory,
        ModInstalledArtifactState? state)
    {
        if (state is null)
        {
            RemoveInstalledState(gameDirectory);
        }
        else
        {
            if (!PathEquals(state.GameDirectory, gameDirectory))
            {
                throw new InvalidDataException(
                    "The rollback receipt belongs to a different game installation.");
            }
            UpsertInstalledState(state);
        }
    }

    private LegacyStateUpgrade UpgradeLegacyBackupReceipts(
        ModInstalledArtifactState? state,
        bool persistUpgrade = true)
    {
        if (state is null)
        {
            return new(null, null);
        }
        try
        {
            var dllReceipt = state.PreviousArtifactBackupIdentity;
            if (!string.IsNullOrWhiteSpace(state.PreviousArtifactBackupPath) && dllReceipt is null)
            {
                if (!File.Exists(state.PreviousArtifactBackupPath))
                {
                    return new(state, "The adopted DLL backup is missing; it was not replaced or discarded.");
                }
                dllReceipt = CaptureIdentity(state.PreviousArtifactBackupPath);
            }
            var runtimeReceipt = state.PreviousRuntimeManifestBackupIdentity;
            if (!string.IsNullOrWhiteSpace(state.PreviousRuntimeManifestBackupPath) && runtimeReceipt is null)
            {
                if (!File.Exists(state.PreviousRuntimeManifestBackupPath))
                {
                    return new(state, "The adopted runtime-manifest backup is missing; it was not replaced or discarded.");
                }
                runtimeReceipt = CaptureIdentity(state.PreviousRuntimeManifestBackupPath);
            }
            if (dllReceipt == state.PreviousArtifactBackupIdentity
                && runtimeReceipt == state.PreviousRuntimeManifestBackupIdentity)
            {
                return new(state, null);
            }
            var upgraded = state with
            {
                PreviousArtifactBackupIdentity = dllReceipt,
                PreviousRuntimeManifestBackupIdentity = runtimeReceipt,
            };
            if (persistUpgrade)
            {
                UpsertInstalledState(upgraded);
            }
            return new(upgraded, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(state, $"The adopted backup identity could not be migrated safely: {exception.Message}");
        }
    }

    private sealed record LegacyStateUpgrade(ModInstalledArtifactState? State, string? Failure);

    private static async Task WriteStageAsync(string path, byte[] contents, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }

    private async Task PromoteDurableBackupAsync(
        string sameVolumeSource,
        string durablePath,
        ModArtifactIdentityReceipt identity,
        ModDeploymentFileCheckpoint copyStartedCheckpoint,
        ModDeploymentFileCheckpoint promotedCheckpoint,
        ModDeploymentFileCheckpoint sourceRemovedCheckpoint,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(durablePath)!);
        var promotionStage = durablePath + ".stage";
        await CopyFileDurablyAsync(
            sameVolumeSource,
            promotionStage,
            identity,
            copyStartedCheckpoint,
            cancellationToken);
        VerifyFile(promotionStage, identity, "durable backup promotion stage");
        MoveCompletedOwnedCopyStage(
            promotionStage,
            durablePath,
            identity,
            "durable backup promotion stage");
        await CheckpointAsync(promotedCheckpoint, cancellationToken);
        VerifyFile(sameVolumeSource, identity, "same-volume rollback source");
        DeleteOwnedFile(sameVolumeSource, identity, "same-volume rollback source");
        await CheckpointAsync(sourceRemovedCheckpoint, cancellationToken);
    }

    private async Task CopyBackupToSameVolumeStageAsync(
        string durableSource,
        string stagePath,
        ModArtifactIdentityReceipt identity,
        ModDeploymentFileCheckpoint copyStartedCheckpoint,
        ModDeploymentFileCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await CopyFileDurablyAsync(
            durableSource,
            stagePath,
            identity,
            copyStartedCheckpoint,
            cancellationToken);
        VerifyFile(stagePath, identity, "adopted restore stage");
        await CheckpointAsync(checkpoint, cancellationToken);
    }

    private async Task CopyFileDurablyAsync(
        string sourcePath,
        string destinationPath,
        ModArtifactIdentityReceipt identity,
        ModDeploymentFileCheckpoint copyStartedCheckpoint,
        CancellationToken cancellationToken)
    {
        var sourceAttributes = identity.Attributes ?? File.GetAttributes(sourcePath);
        var sourceLastWriteTimeUtc = identity.LastWriteTimeUtcTicks is { } ticks
            ? new DateTime(ticks, DateTimeKind.Utc)
            : File.GetLastWriteTimeUtc(sourcePath);
        ExactFileRevision completedRevision;
        await using (var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            try
            {
                RegisterOwnedCopyStage(destinationPath, destination.SafeFileHandle);
            }
            catch
            {
                CandidateFileNative.TryMarkDeleteOnClose(destination.SafeFileHandle);
                throw;
            }
            await CheckpointAsync(copyStartedCheckpoint, cancellationToken);
            var buffer = new byte[81920];
            long written = 0;
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
                written += count;
                if (afterDurableCopyChunkWritten is not null)
                {
                    await afterDurableCopyChunkWritten(
                        sourcePath,
                        destinationPath,
                        written,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            await destination.FlushAsync(cancellationToken);
            destination.Flush(true);
            if (afterDurableCopyBytesFlushed is not null)
            {
                await afterDurableCopyBytesFlushed(
                    sourcePath,
                    destinationPath,
                    cancellationToken).ConfigureAwait(false);
            }
            ExactFileMutation.SetMetadata(
                destination.SafeFileHandle,
                sourceAttributes,
                sourceLastWriteTimeUtc.Ticks);
            destination.Flush(true);
            var fileIdentity = CandidateFileNative.ReadIdentity(destination.SafeFileHandle);
            completedRevision = ExactFileMutation.CaptureRevision(
                destination,
                destinationPath,
                fileIdentity);
            if (!MatchesIdentity(completedRevision, identity))
            {
                throw new InvalidDataException(
                    "The durable backup copy bytes or metadata changed before completion.");
            }
            CompleteOwnedCopyStage(destinationPath, fileIdentity, completedRevision);
        }
        if (afterDurableCopyCompleted is not null)
        {
            await afterDurableCopyCompleted(sourcePath, destinationPath, cancellationToken)
                .ConfigureAwait(false);
        }
        VerifyCompletedOwnedCopyStage(destinationPath, identity, "durable backup copy");
    }

    private string CopyStageReceiptPath(string stagePath)
    {
        var normalized = NormalizeGameDirectory(stagePath);
        var key = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(stateDirectory, "copy-stage-ownership", $"{key}.json");
    }

    private void RegisterOwnedCopyStage(
        string stagePath,
        Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var fileIdentity = CandidateFileNative.ReadIdentity(handle);
        var receipt = new ModDeploymentCopyStageReceipt(
            CopyStageReceiptSchemaVersion,
            Path.GetFullPath(stagePath),
            ModDeploymentCopyStagePhase.Writing,
            fileIdentity,
            LastOwnedRevision: null);
        WriteJsonAtomically(CopyStageReceiptPath(stagePath), receipt);
    }

    private void CompleteOwnedCopyStage(
        string stagePath,
        CandidateFileIdentity fileIdentity,
        ExactFileRevision completedRevision)
    {
        var receipt = ReadOwnedCopyStage(stagePath)
            ?? throw new InvalidDataException(
                "The durable copy stage lost its ownership receipt before completion.");
        if (receipt.Phase != ModDeploymentCopyStagePhase.Writing
            || receipt.FileIdentity != fileIdentity
            || !completedRevision.Identity.Equals(fileIdentity))
        {
            throw new InvalidDataException(
                "The durable copy stage was replaced before its owned revision could be recorded.");
        }
        WriteJsonAtomically(
            CopyStageReceiptPath(stagePath),
            receipt with
            {
                Phase = ModDeploymentCopyStagePhase.Complete,
                LastOwnedRevision = completedRevision,
            });
    }

    private ModDeploymentCopyStageReceipt? ReadOwnedCopyStage(string stagePath)
    {
        var receipt = ReadJson<ModDeploymentCopyStageReceipt>(CopyStageReceiptPath(stagePath));
        if (receipt is null)
        {
            return null;
        }
        if (receipt.SchemaVersion != CopyStageReceiptSchemaVersion
            || !Path.IsPathFullyQualified(receipt.StagePath)
            || !PathEquals(receipt.StagePath, stagePath)
            || !Enum.IsDefined(receipt.Phase)
            || receipt.FileIdentity is null
            || receipt.FileIdentity.VolumeSerialNumber is not { Length: 8 }
            || !receipt.FileIdentity.VolumeSerialNumber.All(Uri.IsHexDigit)
            || receipt.FileIdentity.FileIndex is not { Length: 16 }
            || !receipt.FileIdentity.FileIndex.All(Uri.IsHexDigit)
            || receipt.Phase == ModDeploymentCopyStagePhase.Writing
                && receipt.LastOwnedRevision is not null
            || receipt.Phase == ModDeploymentCopyStagePhase.Complete
                && receipt.LastOwnedRevision is null
            || receipt.LastOwnedRevision is { } revision
                && (revision.Identity is null
                    || revision.Identity != receipt.FileIdentity
                    || revision.Length < 0
                    || revision.Length > MaximumArtifactSize
                    || !TryNormalizeSha256(revision.Sha256, out _)
                    || revision.Attributes.HasFlag(FileAttributes.Directory)
                    || revision.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || revision.Attributes.HasFlag(FileAttributes.Device)
                    || revision.LastWriteTimeUtcTicks < DateTime.MinValue.Ticks
                    || revision.LastWriteTimeUtcTicks > DateTime.MaxValue.Ticks))
        {
            throw new InvalidDataException(
                "The durable copy-stage ownership receipt is invalid; the stage was preserved.");
        }
        return receipt;
    }

    private void ReleaseOwnedCopyStage(string stagePath)
    {
        var receiptPath = CopyStageReceiptPath(stagePath);
        if (!File.Exists(receiptPath))
        {
            return;
        }
        if (File.Exists(stagePath))
        {
            _ = ReadOwnedCopyStage(stagePath);
        }
        DeleteIfExists(receiptPath);
        var directory = Path.GetDirectoryName(receiptPath)!;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory, recursive: false);
        }
    }

    private static void VerifyFile(string path, ModReleaseArtifact artifact)
        => VerifyFile(path, artifact.Size, artifact.Sha256, "artifact");

    private static void VerifyFile(ExactFileRevision revision, ModReleaseArtifact artifact)
        => VerifyFile(revision, artifact.Size, artifact.Sha256, "artifact");

    private static void VerifyFile(string path, ModRuntimeManifestArtifact artifact) =>
        VerifyFile(path, artifact.Size, artifact.Sha256, "runtime manifest");

    private static void VerifyFile(
        ExactFileRevision revision,
        ModRuntimeManifestArtifact artifact) =>
        VerifyFile(revision, artifact.Size, artifact.Sha256, "runtime manifest");

    private static void VerifyFile(
        string path,
        ModArtifactIdentityReceipt identity,
        string subject)
    {
        if (!MatchesIdentity(path, identity))
        {
            throw new InvalidDataException($"The staged {subject} bytes or metadata changed before commit.");
        }
    }

    private static void VerifyFile(
        ExactFileRevision revision,
        ModArtifactIdentityReceipt identity,
        string subject)
    {
        if (!MatchesIdentity(revision, identity))
        {
            throw new InvalidDataException(
                $"The staged {subject} bytes or metadata changed before commit.");
        }
    }

    private static void VerifyFile(
        ExactFileRevision revision,
        long expectedSize,
        string expectedSha256,
        string subject)
    {
        if (revision.Length != expectedSize
            || !string.Equals(revision.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The staged {subject} does not match reviewed metadata.");
        }
    }

    private static void VerifyFile(string path, long expectedSize, string expectedSha256, string subject)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedSize)
        {
            throw new InvalidDataException($"The staged {subject} size changed before commit.");
        }
        if (!string.Equals(ComputeFileSha256(path), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The staged {subject} SHA-256 changed before commit.");
        }
    }

    private static string RuntimeManifestTargetPath(string gameDirectory) =>
        Path.Combine(gameDirectory, ArtifactBoundRuntimeManifestParser.ManagedFileName);

    private static string RuntimeManifestStagePath(ModDeploymentJournal journal) =>
        Path.Combine(
            journal.GameDirectory,
            $".{ArtifactBoundRuntimeManifestParser.ManagedFileName}.{journal.TransactionId}.stage");

    private static string RuntimeManifestSameVolumeBackupPath(ModDeploymentJournal journal) =>
        Path.Combine(
            journal.GameDirectory,
            $".{ArtifactBoundRuntimeManifestParser.ManagedFileName}.{journal.TransactionId}.rollback");

    private string RuntimeManifestDurableBackupPath(ModDeploymentJournal journal) =>
        Path.Combine(
            stateDirectory,
            "rollback",
            journal.TransactionId,
            ArtifactBoundRuntimeManifestParser.ManagedFileName);

    private static string DurablePromotionStagePath(ModDeploymentJournal journal) =>
        journal.DurableBackupPath + ".stage";

    private string RuntimeManifestDurablePromotionStagePath(ModDeploymentJournal journal) =>
        RuntimeManifestDurableBackupPath(journal) + ".stage";

    private static string RollbackRestoreStagePath(ModDeploymentJournal journal) =>
        Path.Combine(journal.GameDirectory, $".{ManagedFileName}.{journal.TransactionId}.restore");

    private static string RuntimeManifestRollbackRestoreStagePath(ModDeploymentJournal journal) =>
        Path.Combine(
            journal.GameDirectory,
            $".{ArtifactBoundRuntimeManifestParser.ManagedFileName}.{journal.TransactionId}.restore");

    private static bool ShouldMutateRuntimeManifest(ModDeploymentJournal journal) =>
        journal.Artifact.RuntimeManifest is not null
        || journal.PreviousInstalledState?.RuntimeManifest is not null;

    private bool CanCleanCommittedResidue(ModDeploymentJournal journal)
    {
        if (!journal.HasCommitParticipant || journal.CommitParticipantCompleted)
        {
            return true;
        }
        try
        {
            var outer = ReadJson<LauncherProviderAtomicSwitchJournal>(
                Path.Combine(stateDirectory, "provider-switch-journal.json"));
            return outer is not null
                && outer.TransactionId == journal.TransactionId
                && outer.Phase == LauncherProviderAtomicSwitchPhase.Completed;
        }
        catch (Exception exception) when (IsStateReadFailure(exception))
        {
            return false;
        }
    }

    private bool HasSameVolumeTransactionResidue(ModDeploymentJournal journal) =>
        File.Exists(journal.StagePath)
        || File.Exists(journal.SameVolumeBackupPath)
        || File.Exists(RuntimeManifestStagePath(journal))
        || File.Exists(RuntimeManifestSameVolumeBackupPath(journal))
        || File.Exists(DurablePromotionStagePath(journal))
        || File.Exists(RuntimeManifestDurablePromotionStagePath(journal))
        || File.Exists(RollbackRestoreStagePath(journal))
        || File.Exists(RuntimeManifestRollbackRestoreStagePath(journal));

    private void WriteCleanupPendingError(ModDeploymentJournal journal, string error)
    {
        try
        {
            WriteJsonAtomically(
                JournalPath,
                journal with
                {
                    Phase = ModDeploymentPhase.CleanupPending,
                    Error = error,
                    UpdatedAtUtc = timeProvider.GetUtcNow(),
                });
        }
        catch
        {
            // Preserve the last durable journal when the error update itself cannot be recorded.
        }
    }

    private string? ValidateCommittedOutcome(ModDeploymentJournal journal)
    {
        try
        {
            var dllPath = Path.Combine(journal.GameDirectory, ManagedFileName);
            var runtimePath = RuntimeManifestTargetPath(journal.GameDirectory);
            if (journal.Operation == ModDeploymentOperation.Deploy)
            {
                if (!MatchesIdentity(dllPath, Identity(journal.Artifact)))
                {
                    return "Committed cleanup was blocked because the installed DLL changed or is missing.";
                }
                if (journal.Artifact.RuntimeManifest is null)
                {
                    var preserveLooseRuntime = journal.PreviousInstalledState?.RuntimeManifest is null
                        && journal.HadExistingRuntimeManifest;
                    if (preserveLooseRuntime
                        ? journal.ExistingRuntimeManifestIdentity is null
                            || !MatchesIdentity(runtimePath, journal.ExistingRuntimeManifestIdentity)
                        : File.Exists(runtimePath))
                    {
                        return "Committed cleanup was blocked because the final runtime-manifest state changed.";
                    }
                }
                else if (!MatchesIdentity(runtimePath, Identity(journal.Artifact.RuntimeManifest)))
                {
                    return "Committed cleanup was blocked because the installed runtime manifest changed or is missing.";
                }
                var state = ReadInstalledState(journal.GameDirectory);
                var targetAttribution = journal.TargetInstallationAttribution
                    ?? (journal.Phase == ModDeploymentPhase.Committed ? installationAttribution : null);
                if (state is null
                    || targetAttribution is null
                    || !PathEquals(state.GameDirectory, journal.GameDirectory)
                    || state.Size != journal.Artifact.Size
                    || !string.Equals(state.Sha256, journal.Artifact.Sha256, StringComparison.OrdinalIgnoreCase)
                    || state.Version != journal.Artifact.ExpectedVersion
                    || !string.Equals(state.ProviderId, targetAttribution.ProviderId, StringComparison.Ordinal)
                    || !string.Equals(
                        state.ReleaseChannelId,
                        targetAttribution.ReleaseChannelId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        state.RuntimeDistributionId,
                        targetAttribution.RuntimeDistributionId,
                        StringComparison.Ordinal)
                    || !RuntimeInstalledStateMatches(state.RuntimeManifest, journal.Artifact.RuntimeManifest))
                {
                    return "Committed cleanup was blocked because installed-mod state does not match the live pair.";
                }
                var lineageFailure = ValidateRetainedBackupLineage(journal, state);
                if (lineageFailure is not null)
                {
                    return lineageFailure;
                }
                return null;
            }

            if (ReadInstalledState(journal.GameDirectory) is not null)
            {
                return "Committed uninstall cleanup was blocked because managed installed state still exists.";
            }
            var previous = journal.PreviousInstalledState;
            var runtimeOutcomeMatches = previous?.RuntimeManifest is null
                && string.IsNullOrWhiteSpace(previous?.PreviousRuntimeManifestBackupPath)
                || MatchesExpectedRestoredArtifact(
                    runtimePath,
                    previous?.PreviousRuntimeManifestBackupPath,
                    previous?.PreviousRuntimeManifestBackupIdentity);
            if (!MatchesExpectedRestoredArtifact(
                    dllPath,
                    previous?.PreviousArtifactBackupPath,
                    previous?.PreviousArtifactBackupIdentity)
                || !runtimeOutcomeMatches)
            {
                return "Committed uninstall cleanup was blocked because the restored adopted files changed or are incomplete.";
            }
            return null;
        }
        catch (Exception exception) when (IsStateReadFailure(exception))
        {
            return $"Committed cleanup could not verify the final managed state: {exception.Message}";
        }
    }

    private static bool RuntimeInstalledStateMatches(
        ModInstalledRuntimeManifestState? installed,
        ModRuntimeManifestArtifact? target) => target is null
        ? installed is null
        : installed is not null
            && installed.Size == target.Size
            && string.Equals(installed.Sha256, target.Sha256, StringComparison.OrdinalIgnoreCase)
            && installed.SourceRevision == target.ExpectedSourceRevision
            && installed.Repository == target.ExpectedRepository
            && installed.Tag == target.ExpectedTag;

    private string? ValidateRetainedBackupLineage(
        ModDeploymentJournal journal,
        ModInstalledArtifactState installed)
    {
        var previous = journal.PreviousInstalledState;
        var expectedDllPath = previous is not null
            ? previous.PreviousArtifactBackupPath
            : journal.HadExistingArtifact ? journal.DurableBackupPath : null;
        var expectedDllIdentity = previous is not null
            ? previous.PreviousArtifactBackupIdentity
            : expectedDllPath is null ? null : journal.ExistingArtifactIdentity;
        var retainNewRuntime = ShouldMutateRuntimeManifest(journal)
            && journal.HadExistingRuntimeManifest
            && (previous is null || previous.RuntimeManifest is null);
        var expectedRuntimePath = previous is not null
            ? previous.PreviousRuntimeManifestBackupPath
                ?? (retainNewRuntime ? RuntimeManifestDurableBackupPath(journal) : null)
            : retainNewRuntime ? RuntimeManifestDurableBackupPath(journal) : null;
        var expectedRuntimeIdentity = previous is not null
            ? previous.PreviousRuntimeManifestBackupIdentity
                ?? (expectedRuntimePath is null ? null : journal.ExistingRuntimeManifestIdentity)
            : expectedRuntimePath is null ? null : journal.ExistingRuntimeManifestIdentity;
        if (!OptionalPathEquals(installed.PreviousArtifactBackupPath, expectedDllPath)
            || installed.PreviousArtifactBackupIdentity != expectedDllIdentity
            || !OptionalPathEquals(installed.PreviousRuntimeManifestBackupPath, expectedRuntimePath)
            || installed.PreviousRuntimeManifestBackupIdentity != expectedRuntimeIdentity)
        {
            return "Committed cleanup was blocked because retained rollback lineage does not match the transaction.";
        }
        var failure = ValidateDeclaredBackup(expectedDllPath, expectedDllIdentity, "retained adopted DLL");
        failure ??= ValidateDeclaredBackup(
            expectedRuntimePath,
            expectedRuntimeIdentity,
            "retained adopted runtime manifest");
        return failure;
    }

    private static bool OptionalPathEquals(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right)
        || left is not null && right is not null && PathEquals(left, right);

    private static bool MatchesExpectedRestoredArtifact(
        string livePath,
        string? priorBackupPath,
        ModArtifactIdentityReceipt? priorIdentity) => string.IsNullOrWhiteSpace(priorBackupPath)
        ? !File.Exists(livePath)
        : priorIdentity is not null && MatchesIdentity(livePath, priorIdentity);

    private CommittedCleanupResult CleanupCommittedResidue(ModDeploymentJournal journal)
    {
        var previous = journal.PreviousInstalledState;
        var candidates = new[]
        {
            new CleanupCandidate(
                journal.StagePath,
                journal.Operation == ModDeploymentOperation.Uninstall
                    ? previous?.PreviousArtifactBackupIdentity
                    : Identity(journal.Artifact),
                "DLL stage"),
            new CleanupCandidate(
                journal.SameVolumeBackupPath,
                journal.ExistingArtifactIdentity ?? Identity(previous?.Size, previous?.Sha256),
                "DLL rollback"),
            new CleanupCandidate(
                RuntimeManifestStagePath(journal),
                journal.Operation == ModDeploymentOperation.Uninstall
                    ? previous?.PreviousRuntimeManifestBackupIdentity
                    : journal.Artifact.RuntimeManifest is null
                        ? null
                        : Identity(journal.Artifact.RuntimeManifest),
                "runtime-manifest stage"),
            new CleanupCandidate(
                RuntimeManifestSameVolumeBackupPath(journal),
                journal.ExistingRuntimeManifestIdentity
                    ?? Identity(previous?.RuntimeManifest?.Size, previous?.RuntimeManifest?.Sha256),
                "runtime-manifest rollback"),
            new CleanupCandidate(
                DurablePromotionStagePath(journal),
                journal.ExistingArtifactIdentity,
                "durable DLL promotion stage"),
            new CleanupCandidate(
                RuntimeManifestDurablePromotionStagePath(journal),
                journal.ExistingRuntimeManifestIdentity,
                "durable runtime-manifest promotion stage"),
            new CleanupCandidate(
                RollbackRestoreStagePath(journal),
                journal.ExistingArtifactIdentity,
                "DLL rollback restore stage"),
            new CleanupCandidate(
                RuntimeManifestRollbackRestoreStagePath(journal),
                journal.ExistingRuntimeManifestIdentity,
                "runtime-manifest rollback restore stage"),
            new CleanupCandidate(
                journal.Operation == ModDeploymentOperation.Uninstall
                    ? previous?.PreviousArtifactBackupPath ?? string.Empty
                    : string.Empty,
                previous?.PreviousArtifactBackupIdentity,
                "retired adopted DLL backup"),
            new CleanupCandidate(
                journal.Operation == ModDeploymentOperation.Uninstall
                    ? previous?.PreviousRuntimeManifestBackupPath ?? string.Empty
                    : string.Empty,
                previous?.PreviousRuntimeManifestBackupIdentity,
                "retired adopted runtime-manifest backup"),
        };
        try
        {
            foreach (var candidate in candidates.Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Path) && !File.Exists(candidate.Path)))
            {
                ReleaseOwnedCopyStage(candidate.Path);
            }
            var existing = candidates.Where(candidate => File.Exists(candidate.Path)).ToArray();
            foreach (var candidate in existing)
            {
                if (candidate.Identity is null
                    || !MatchesIdentity(candidate.Path, candidate.Identity))
                {
                    return new(
                        false,
                        false,
                        $"Committed cleanup preserved an unrecognized {candidate.Description}; manual recovery is required.");
                }
            }
            foreach (var candidate in existing)
            {
                if (File.Exists(CopyStageReceiptPath(candidate.Path)))
                {
                    DeleteVerifiedCopyStageResidue(
                        candidate.Path,
                        candidate.Identity!,
                        candidate.Description);
                }
                else
                {
                    DeleteOwnedFile(candidate.Path, candidate.Identity!, candidate.Description);
                }
            }
            return new(true, existing.Length > 0, string.Empty);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(
                false,
                false,
                $"Committed cleanup could not finish safely: {exception.Message}");
        }
    }

    private sealed record CleanupCandidate(
        string Path,
        ModArtifactIdentityReceipt? Identity,
        string Description);

    private sealed record CommittedCleanupResult(bool IsSuccess, bool Changed, string Message);

    private void VerifyVersion(string path, ModReleaseArtifact artifact)
    {
        var actualVersion = versionReader.ReadVersion(path);
        if (!string.Equals(actualVersion, artifact.ExpectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The installed artifact version '{actualVersion ?? "unreadable"}' "
                    + $"does not match '{artifact.ExpectedVersion}'.");
        }
        if (artifact.ExpectedProductVersion is null)
        {
            return;
        }
        if (versionReader is not IModArtifactProductVersionReader productVersionReader)
        {
            throw new InvalidDataException(
                "The selected release requires signed product-version evidence that the version reader cannot inspect.");
        }
        var actualProductVersion = productVersionReader.ReadProductVersion(path);
        if (!string.Equals(
                actualProductVersion,
                artifact.ExpectedProductVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The installed artifact product version '{actualProductVersion ?? "unreadable"}' "
                    + $"does not match '{artifact.ExpectedProductVersion}'.");
        }
    }

    private void VerifyAuthenticity(string path)
    {
        var result = authenticityVerifier.Verify(path);
        if (!result.IsTrusted)
        {
            throw new InvalidDataException($"Artifact authenticity verification failed: {result.Message}");
        }
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static ModArtifactIdentityReceipt CaptureIdentity(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            81920,
            FileOptions.SequentialScan);
        var size = stream.Length;
        if (size is <= 0 or > MaximumArtifactSize)
        {
            throw new InvalidDataException("The prior artifact is outside the supported backup size boundary.");
        }
        return new(
            size,
            Convert.ToHexString(SHA256.HashData(stream)),
            File.GetAttributes(path),
            File.GetLastWriteTimeUtc(path).Ticks);
    }

    private static ModArtifactIdentityReceipt Identity(ModReleaseArtifact artifact) =>
        new(artifact.Size, artifact.Sha256);

    private static ModArtifactIdentityReceipt Identity(ModRuntimeManifestArtifact artifact) =>
        new(artifact.Size, artifact.Sha256);

    private static ModFileIdentityReceipt FileIdentity(CandidateFileIdentity identity) =>
        new(identity.VolumeSerialNumber, identity.FileIndex);

    private static bool MatchesFileIdentity(
        CandidateFileIdentity identity,
        ModFileIdentityReceipt receipt) =>
        string.Equals(
            identity.VolumeSerialNumber,
            receipt.VolumeSerialNumber,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(identity.FileIndex, receipt.FileIndex, StringComparison.OrdinalIgnoreCase);

    private static ModArtifactIdentityReceipt? Identity(long? size, string? sha256) =>
        size is null || string.IsNullOrWhiteSpace(sha256)
            ? null
            : new(size.Value, sha256);

    private static bool ArtifactMatchesInstalledReceipt(
        ModInstalledArtifactState receipt,
        ModReleaseArtifact artifact) =>
        receipt.Size == artifact.Size
        && string.Equals(receipt.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(receipt.Version, artifact.ExpectedVersion, StringComparison.Ordinal)
        && (receipt.RuntimeManifest is null
            ? artifact.RuntimeManifest is null
            : artifact.RuntimeManifest is not null
                && receipt.RuntimeManifest.Size == artifact.RuntimeManifest.Size
                && string.Equals(
                    receipt.RuntimeManifest.Sha256,
                    artifact.RuntimeManifest.Sha256,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    receipt.RuntimeManifest.FileName,
                    artifact.RuntimeManifest.FileName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    receipt.RuntimeManifest.SourceRevision,
                    artifact.RuntimeManifest.ExpectedSourceRevision,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    receipt.RuntimeManifest.Repository,
                    artifact.RuntimeManifest.ExpectedRepository,
                    StringComparison.Ordinal)
                && string.Equals(
                    receipt.RuntimeManifest.Tag,
                    artifact.RuntimeManifest.ExpectedTag,
                    StringComparison.Ordinal));

    private sealed record ReleaseFloorEvidence(
        string ReleaseProductVersion,
        long AcceptedArtifactSize,
        string AcceptedArtifactSha256);

    private static ReleaseFloorEvidence? FindReleaseProductVersionFloor(
        ModInstalledArtifactState? state,
        ModInstallationAttribution attribution)
    {
        if (state is null)
        {
            return null;
        }
        var retained = state.ReleaseHighWaterMarks?
            .SingleOrDefault(mark =>
                string.Equals(mark.ProviderId, attribution.ProviderId, StringComparison.Ordinal)
                && string.Equals(
                    mark.ReleaseChannelId,
                    attribution.ReleaseChannelId,
                    StringComparison.Ordinal)
                && string.Equals(
                    mark.RuntimeDistributionId,
                    attribution.RuntimeDistributionId,
                    StringComparison.Ordinal));
        if (retained is not null)
        {
            return new(
                retained.ReleaseProductVersion,
                retained.AcceptedArtifactSize,
                retained.AcceptedArtifactSha256);
        }
        return string.Equals(state.ProviderId, attribution.ProviderId, StringComparison.Ordinal)
            && string.Equals(
                state.ReleaseChannelId,
                attribution.ReleaseChannelId,
                StringComparison.Ordinal)
            && string.Equals(
                state.RuntimeDistributionId,
                attribution.RuntimeDistributionId,
                StringComparison.Ordinal)
                ? state.ReleaseProductVersion is null
                    ? null
                    : new(state.ReleaseProductVersion, state.Size, state.Sha256)
                : null;
    }

    private string? ValidateReleaseProductVersionFloor(
        ModInstalledArtifactState? state,
        ModReleaseArtifact artifact,
        ModInstallationAttribution attribution)
    {
        if (state is not null
            && state.ReleaseProductVersion is null
            && (!MatchesAttribution(state, attribution)
                || !ArtifactMatchesInstalledReceipt(state, artifact)))
        {
            return "The managed installation predates signed release-order receipts, and its exact release "
                + "cannot be established from the bundled reviewed evidence. Mod Bridge preserved it and "
                + "requires an explicit migration or downgrade recovery decision before replacing it.";
        }
        var floor = FindReleaseProductVersionFloor(state, attribution);
        if (floor is null)
        {
            return null;
        }
        var candidateProductVersion = ResolveReleaseProductVersion(artifact, attribution);
        if (candidateProductVersion is null)
        {
            return $"The selected release has no signed product-version identity, but this installation retains {floor.ReleaseProductVersion} as its highest accepted release for this provider and channel.";
        }
        int order;
        try
        {
            order = WindowsReleaseSelectionPolicy.CompareProductReleaseOrderingVersions(
                candidateProductVersion,
                floor.ReleaseProductVersion);
        }
        catch (InvalidDataException)
        {
            return $"The selected {candidateProductVersion} release cannot be safely ordered against this installation's retained {floor.ReleaseProductVersion} release floor. Use an explicit replacement or downgrade recovery flow to replace it.";
        }
        if (order < 0)
        {
            return $"The selected {candidateProductVersion} release is older than this installation's retained {floor.ReleaseProductVersion} release floor. Use an explicit downgrade recovery flow to install it.";
        }
        if (order == 0
            && (!string.Equals(
                    candidateProductVersion,
                    floor.ReleaseProductVersion,
                    StringComparison.Ordinal)
                || artifact.Size != floor.AcceptedArtifactSize
                || !string.Equals(
                    artifact.Sha256,
                    floor.AcceptedArtifactSha256,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return $"The selected {candidateProductVersion} release does not exactly match the retained signed tag and artifact identity for this release floor. Use an explicit replacement or downgrade recovery flow to replace it.";
        }
        return null;
    }

    private static bool MatchesAttribution(
        ModInstalledArtifactState state,
        ModInstallationAttribution attribution) =>
        string.Equals(state.ProviderId, attribution.ProviderId, StringComparison.Ordinal)
        && string.Equals(
            state.ReleaseChannelId,
            attribution.ReleaseChannelId,
            StringComparison.Ordinal)
        && string.Equals(
            state.RuntimeDistributionId,
            attribution.RuntimeDistributionId,
            StringComparison.Ordinal);

    private string? ResolveReleaseProductVersion(
        ModReleaseArtifact artifact,
        ModInstallationAttribution attribution)
    {
        if (artifact.ExpectedProductVersion is not null)
        {
            return artifact.ExpectedProductVersion;
        }
        var matches = reviewedCertifications
            .Where(certification =>
                IsOrderableReleaseProductVersion(certification.Tag)
                &&
                certification.ProviderId == attribution.ProviderId
                && certification.ChannelId == attribution.ReleaseChannelId
                && certification.RuntimeDistributionId == attribution.RuntimeDistributionId
                && certification.PayloadFileName.Equals(
                    artifact.FileName,
                    StringComparison.OrdinalIgnoreCase)
                && certification.PayloadSize == artifact.Size
                && certification.PayloadSha256.Equals(
                    artifact.Sha256,
                    StringComparison.OrdinalIgnoreCase)
                && certification.PayloadVersion == artifact.ExpectedVersion
                && (artifact.RuntimeManifest is null
                    ? certification.RuntimeManifest is null
                    : certification.RuntimeManifest is not null
                        && certification.RuntimeManifest.FileName.Equals(
                            artifact.RuntimeManifest.FileName,
                            StringComparison.OrdinalIgnoreCase)
                        && certification.RuntimeManifest.Size == artifact.RuntimeManifest.Size
                        && certification.RuntimeManifest.Sha256.Equals(
                            artifact.RuntimeManifest.Sha256,
                            StringComparison.OrdinalIgnoreCase)
                        && certification.SourceCommit.Equals(
                            artifact.RuntimeManifest.ExpectedSourceRevision,
                            StringComparison.OrdinalIgnoreCase)
                        && certification.Repository == artifact.RuntimeManifest.ExpectedRepository
                        && certification.Tag == artifact.RuntimeManifest.ExpectedTag))
            .Select(certification => certification.Tag)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException(
                "The selected release matches multiple reviewed product-version identities."),
        };
    }

    private static bool IsOrderableReleaseProductVersion(string value)
    {
        try
        {
            _ = WindowsReleaseSelectionPolicy.ParseProductReleaseOrderingVersion(value);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static ModReleaseHighWaterState[]? BuildReleaseHighWaterMarks(
        ModInstalledArtifactState? previous,
        ModInstallationAttribution target,
        string? targetProductVersion,
        ModReleaseArtifact targetArtifact)
    {
        var candidates = new List<ModReleaseHighWaterState>(previous?.ReleaseHighWaterMarks ?? []);
        if (previous?.ReleaseProductVersion is not null)
        {
            candidates.Add(new(
                previous.ProviderId,
                previous.ReleaseChannelId,
                previous.RuntimeDistributionId,
                previous.ReleaseProductVersion,
                previous.Size,
                previous.Sha256));
        }
        if (targetProductVersion is not null)
        {
            candidates.Add(new(
                target.ProviderId,
                target.ReleaseChannelId,
                target.RuntimeDistributionId,
                targetProductVersion,
                targetArtifact.Size,
                targetArtifact.Sha256));
        }
        var marks = candidates
            .GroupBy(mark => (
                mark.ProviderId,
                mark.ReleaseChannelId,
                mark.RuntimeDistributionId))
            .Select(group => group
                .OrderByDescending(mark =>
                    mark.ReleaseProductVersion,
                    Comparer<string>.Create(
                        WindowsReleaseSelectionPolicy.CompareProductReleaseOrderingVersions))
                .First())
            .Where(mark => targetProductVersion is null
                || !string.Equals(mark.ProviderId, target.ProviderId, StringComparison.Ordinal)
                || !string.Equals(
                    mark.ReleaseChannelId,
                    target.ReleaseChannelId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    mark.RuntimeDistributionId,
                    target.RuntimeDistributionId,
                    StringComparison.Ordinal))
            .OrderBy(mark => mark.ProviderId, StringComparer.Ordinal)
            .ThenBy(mark => mark.ReleaseChannelId, StringComparer.Ordinal)
            .ThenBy(mark => mark.RuntimeDistributionId, StringComparer.Ordinal)
            .ToArray();
        return marks.Length == 0 ? null : marks;
    }

    private static bool MatchesIdentity(string path, ModArtifactIdentityReceipt identity)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            81920,
            FileOptions.SequentialScan);
        return stream.Length == identity.Size
            && string.Equals(
                Convert.ToHexString(SHA256.HashData(stream)),
                identity.Sha256,
                StringComparison.OrdinalIgnoreCase)
            && (identity.Attributes is null
                || File.GetAttributes(path) == identity.Attributes)
            && (identity.LastWriteTimeUtcTicks is null
                || File.GetLastWriteTimeUtc(path).Ticks == identity.LastWriteTimeUtcTicks);
    }

    private static bool MatchesIdentity(
        ExactFileRevision revision,
        ModArtifactIdentityReceipt identity) =>
        revision.Length == identity.Size
        && string.Equals(revision.Sha256, identity.Sha256, StringComparison.OrdinalIgnoreCase)
        && (identity.Attributes is null || revision.Attributes == identity.Attributes)
        && (identity.LastWriteTimeUtcTicks is null
            || revision.LastWriteTimeUtcTicks == identity.LastWriteTimeUtcTicks);

    private static void DeleteOwnedFile(
        string path,
        ModArtifactIdentityReceipt identity,
        string subject)
    {
        if (!File.Exists(path))
        {
            return;
        }
        using var exact = ExactFileMutation.OpenForMetadata(path);
        var revision = exact.CaptureRevision();
        if (!MatchesIdentity(revision, identity))
        {
            throw new InvalidDataException($"The {subject} is unrecognized and was preserved.");
        }
        exact.DeleteExactIgnoringReadOnly();
    }

    private static string? ValidateDeclaredBackup(
        string? path,
        ModArtifactIdentityReceipt? identity,
        string subject)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return identity is null ? null : $"The {subject} identity has no owned backup path.";
        }
        if (identity is null || !MatchesIdentity(path, identity))
        {
            return $"The {subject} backup is missing or changed; no managed files were modified.";
        }
        return null;
    }

    private static bool IsTerminal(ModDeploymentPhase phase) =>
        phase is ModDeploymentPhase.Committed or ModDeploymentPhase.RolledBack or ModDeploymentPhase.Failed;

    private static string NormalizeSha256(string value) =>
        TryNormalizeSha256(value, out var normalized)
            ? normalized
            : throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.", nameof(value));

    private static string ValidateTransactionId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Length == 32 && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : throw new ArgumentException(
                "A coordinated transaction ID must contain exactly 32 hexadecimal characters.",
                nameof(value));
    }

    private static bool TryNormalizeSha256(string value, out string normalized)
    {
        normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
    }

}
