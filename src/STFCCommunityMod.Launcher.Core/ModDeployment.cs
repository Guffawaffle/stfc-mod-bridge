using System.Net;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

public sealed partial class ModDeploymentService
{
    private const int SchemaVersion = 1;
    private const long MaximumArtifactSize = 128L * 1024L * 1024L;
    private const string ManagedFileName = "version.dll";
    private readonly string stateDirectory;
    private readonly LauncherOperationLock operationLock;
    private readonly IModArtifactDownloader downloader;
    private readonly IModArtifactVersionReader versionReader;
    private readonly IModArtifactAuthenticityVerifier authenticityVerifier;
    private readonly Func<bool> isGameRunning;
    private readonly TimeProvider timeProvider;
    private readonly Func<ModDeploymentPhase, CancellationToken, ValueTask>? afterPhasePersisted;

    public ModDeploymentService(
        string stateDirectory,
        IModArtifactDownloader downloader,
        IModArtifactVersionReader versionReader,
        IModArtifactAuthenticityVerifier authenticityVerifier,
        Func<bool> isGameRunning,
        TimeProvider? timeProvider = null,
        Func<ModDeploymentPhase, CancellationToken, ValueTask>? afterPhasePersisted = null)
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
        var state = ReadJson<ModInstalledArtifactState>(InstalledStatePath);
        if (state is not null)
        {
            ValidatePersistedInstalledState(state);
        }
        return state;
    }

    public async Task<ModDeploymentResult> DeployAsync(
        string gameDirectory,
        ModReleaseArtifact artifact,
        ExistingArtifactPolicy existingArtifactPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var validationFailure = ValidateRequest(gameDirectory, artifact, out var normalizedGameDirectory);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        if (isGameRunning())
        {
            return new(ModDeploymentResultState.GameRunning, "Close Star Trek Fleet Command before changing the mod.");
        }

        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            return new(ModDeploymentResultState.Busy, "Another launcher mutation is already active.");
        }

        ModDeploymentJournal? incompleteJournal;
        ModInstalledArtifactState? previousInstalledState;
        try
        {
            incompleteJournal = ReadJournal();
            previousInstalledState = ReadInstalledState();
        }
        catch (Exception exception) when (IsStateReadFailure(exception))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                $"Launcher deployment state could not be read: {exception.Message}");
        }
        if (incompleteJournal is not null && !IsTerminal(incompleteJournal.Phase))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                "An incomplete mod transaction must be recovered before another mutation can start.");
        }

        var targetPath = Path.Combine(normalizedGameDirectory, ManagedFileName);
        var hadExistingArtifact = File.Exists(targetPath);
        var isManagedUpdate = false;
        if (previousInstalledState is not null)
        {
            if (!PathEquals(previousInstalledState.GameDirectory, normalizedGameDirectory))
            {
                return new(
                    ModDeploymentResultState.RecoveryRequired,
                    "Launcher-managed mod state belongs to a different game installation; remove or repair it first.");
            }
            if (!hadExistingArtifact
                || !string.Equals(
                    ComputeFileSha256(targetPath),
                    previousInstalledState.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    ModDeploymentResultState.ManagedArtifactChanged,
                    "The installed version.dll no longer matches launcher-managed state; repair is required.");
            }
            isManagedUpdate = true;
        }

        if (hadExistingArtifact
            && !isManagedUpdate
            && existingArtifactPolicy == ExistingArtifactPolicy.Reject)
        {
            return new(
                ModDeploymentResultState.ExistingArtifactRequiresAdoption,
                "An existing version.dll requires explicit adoption before the launcher can replace it.");
        }

        Directory.CreateDirectory(stateDirectory);
        var transactionId = Guid.NewGuid().ToString("N");
        var stagePath = Path.Combine(normalizedGameDirectory, $".{ManagedFileName}.{transactionId}.stage");
        var sameVolumeBackupPath = Path.Combine(
            normalizedGameDirectory,
            $".{ManagedFileName}.{transactionId}.rollback");
        var durableBackupPath = Path.Combine(stateDirectory, "rollback", transactionId, ManagedFileName);
        var journal = new ModDeploymentJournal(
            SchemaVersion,
            transactionId,
            ModDeploymentOperation.Deploy,
            ModDeploymentPhase.Planned,
            normalizedGameDirectory,
            artifact with { Sha256 = NormalizeSha256(artifact.Sha256) },
            stagePath,
            sameVolumeBackupPath,
            durableBackupPath,
            hadExistingArtifact,
            previousInstalledState,
            timeProvider.GetUtcNow());

        try
        {
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Planned, cancellationToken);
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Downloading, cancellationToken);

            var download = await downloader.DownloadAsync(artifact.DownloadUri, cancellationToken);
            var downloadFailure = VerifyDownload(download, journal.Artifact);
            if (downloadFailure is not null)
            {
                await PersistPhaseAsync(
                    journal with { Error = downloadFailure.Message },
                    ModDeploymentPhase.Failed,
                    cancellationToken);
                return downloadFailure;
            }

            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Verified, cancellationToken);
            await WriteStageAsync(stagePath, download.Contents, cancellationToken);
            VerifyFile(stagePath, journal.Artifact);
            VerifyAuthenticity(stagePath);
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Staged, cancellationToken);
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Committing, cancellationToken);

            if (hadExistingArtifact)
            {
                File.Move(targetPath, sameVolumeBackupPath);
            }

            File.Move(stagePath, targetPath);
            VerifyFile(targetPath, journal.Artifact);
            VerifyVersion(targetPath, journal.Artifact.ExpectedVersion);

            string? retainedBackupPath = null;
            if (File.Exists(sameVolumeBackupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(durableBackupPath)!);
                File.Move(sameVolumeBackupPath, durableBackupPath);
                retainedBackupPath = durableBackupPath;
            }

            var installedState = new ModInstalledArtifactState(
                SchemaVersion,
                normalizedGameDirectory,
                ManagedFileName,
                journal.Artifact.ExpectedVersion,
                journal.Artifact.Size,
                journal.Artifact.Sha256,
                timeProvider.GetUtcNow(),
                retainedBackupPath);
            WriteJsonAtomically(InstalledStatePath, installedState);
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Committed, cancellationToken);
            return new(ModDeploymentResultState.Succeeded, "The community mod was installed successfully.", installedState);
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
                    ? $"The mod transaction failed and the previous state was restored: {exception.Message}"
                    : $"The mod transaction failed and requires recovery: {exception.Message}");
        }
    }

    public async Task<ModDeploymentResult> UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (isGameRunning())
        {
            return new(ModDeploymentResultState.GameRunning, "Close Star Trek Fleet Command before removing the mod.");
        }

        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            return new(ModDeploymentResultState.Busy, "Another launcher mutation is already active.");
        }

        ModDeploymentJournal? incompleteJournal;
        ModInstalledArtifactState? installedState;
        try
        {
            incompleteJournal = ReadJournal();
            installedState = ReadInstalledState();
        }
        catch (Exception exception) when (IsStateReadFailure(exception))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                $"Launcher deployment state could not be read: {exception.Message}");
        }
        if (incompleteJournal is not null && !IsTerminal(incompleteJournal.Phase))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                "An incomplete mod transaction must be recovered before another mutation can start.");
        }

        if (installedState is null)
        {
            return new(ModDeploymentResultState.Succeeded, "No launcher-managed mod installation was found.");
        }

        var validation = GameInstallValidator.Validate(installedState.GameDirectory);
        if (!validation.IsValid)
        {
            return new(ModDeploymentResultState.InvalidGameTarget, validation.Message);
        }

        var targetPath = Path.Combine(installedState.GameDirectory, ManagedFileName);
        if (!File.Exists(targetPath)
            || !string.Equals(ComputeFileSha256(targetPath), installedState.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ModDeploymentResultState.ManagedArtifactChanged,
                "The installed version.dll no longer matches launcher-managed state; it was not removed.");
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var removedArtifactPath = Path.Combine(
            installedState.GameDirectory,
            $".{ManagedFileName}.{transactionId}.rollback");
        var journal = new ModDeploymentJournal(
            SchemaVersion,
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
            timeProvider.GetUtcNow());

        try
        {
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Planned, cancellationToken);
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Committing, cancellationToken);
            File.Move(targetPath, removedArtifactPath);

            if (!string.IsNullOrWhiteSpace(installedState.PreviousArtifactBackupPath)
                && File.Exists(installedState.PreviousArtifactBackupPath))
            {
                File.Move(installedState.PreviousArtifactBackupPath, targetPath);
            }

            DeleteIfExists(InstalledStatePath);
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.Committed, cancellationToken);
            DeleteIfExists(removedArtifactPath);
            return new(ModDeploymentResultState.Succeeded, "The launcher-managed mod was removed.");
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

    public async Task<ModDeploymentResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (isGameRunning())
        {
            return new(ModDeploymentResultState.GameRunning, "Close Star Trek Fleet Command before recovery.");
        }

        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            return new(ModDeploymentResultState.Busy, "Another launcher mutation is already active.");
        }

        ModDeploymentJournal? journal;
        ModInstalledArtifactState? installedState;
        try
        {
            journal = ReadJournal();
            installedState = ReadInstalledState();
        }
        catch (Exception exception) when (IsStateReadFailure(exception))
        {
            return new(
                ModDeploymentResultState.RecoveryRequired,
                $"Launcher deployment state could not be read: {exception.Message}");
        }
        if (journal is null || IsTerminal(journal.Phase))
        {
            return new(ModDeploymentResultState.Succeeded, "No incomplete mod transaction was found.", installedState);
        }

        var targetPath = Path.Combine(journal.GameDirectory, ManagedFileName);
        var rolledBack = await RollBackAsync(journal, targetPath, cancellationToken);
        return new(
            rolledBack ? ModDeploymentResultState.Succeeded : ModDeploymentResultState.RecoveryRequired,
            rolledBack ? "The incomplete mod transaction was rolled back." : "The incomplete transaction requires manual recovery.",
            ReadInstalledState());
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
            || !TryNormalizeSha256(artifact.Sha256, out _)
            || !artifact.DownloadUri.IsAbsoluteUri
            || artifact.DownloadUri.Scheme != Uri.UriSchemeHttps)
        {
            return new(ModDeploymentResultState.VerificationFailed, "The selected release artifact metadata is invalid.");
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
    {
        if (download.StatusCode != HttpStatusCode.OK)
        {
            return new(
                ModDeploymentResultState.DownloadRejected,
                $"The artifact request returned HTTP {(int)download.StatusCode}.");
        }

        if (download.DeclaredContentLength is not null && download.DeclaredContentLength != artifact.Size)
        {
            return new(ModDeploymentResultState.VerificationFailed, "The HTTP content length does not match the release manifest.");
        }

        if (download.Contents.LongLength != artifact.Size)
        {
            return new(ModDeploymentResultState.VerificationFailed, "The downloaded artifact size does not match the release manifest.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(download.Contents));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(artifact.Sha256)))
        {
            return new(ModDeploymentResultState.VerificationFailed, "The downloaded artifact SHA-256 does not match the release manifest.");
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

    private async Task<bool> RollBackAsync(
        ModDeploymentJournal journal,
        string targetPath,
        CancellationToken cancellationToken)
    {
        try
        {
            journal = await PersistPhaseAsync(journal, ModDeploymentPhase.RollingBack, cancellationToken);
            var backupPath = File.Exists(journal.DurableBackupPath)
                ? journal.DurableBackupPath
                : File.Exists(journal.SameVolumeBackupPath)
                    ? journal.SameVolumeBackupPath
                    : null;

            if (backupPath is not null)
            {
                if (File.Exists(targetPath))
                {
                    if (journal.Operation == ModDeploymentOperation.Uninstall
                        && !string.IsNullOrWhiteSpace(journal.PreviousInstalledState?.PreviousArtifactBackupPath))
                    {
                        var priorBackupPath = journal.PreviousInstalledState.PreviousArtifactBackupPath;
                        Directory.CreateDirectory(Path.GetDirectoryName(priorBackupPath)!);
                        File.Move(targetPath, priorBackupPath, true);
                    }
                    else
                    {
                        File.Delete(targetPath);
                    }
                }
                File.Move(backupPath, targetPath);
            }
            else if (!journal.HadExistingArtifact && File.Exists(targetPath))
            {
                var targetHash = ComputeFileSha256(targetPath);
                if (string.Equals(targetHash, journal.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(targetPath);
                }
            }

            DeleteIfExists(journal.StagePath);
            DeleteIfExists(journal.SameVolumeBackupPath);
            RestoreInstalledState(journal.PreviousInstalledState);
            await PersistPhaseAsync(journal, ModDeploymentPhase.RolledBack, cancellationToken);
            return true;
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

    private void RestoreInstalledState(ModInstalledArtifactState? state)
    {
        if (state is null)
        {
            DeleteIfExists(InstalledStatePath);
        }
        else
        {
            WriteJsonAtomically(InstalledStatePath, state);
        }
    }

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

    private static void VerifyFile(string path, ModReleaseArtifact artifact)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != artifact.Size)
        {
            throw new InvalidDataException("The staged artifact size changed before commit.");
        }
        if (!string.Equals(ComputeFileSha256(path), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The staged artifact SHA-256 changed before commit.");
        }
    }

    private void VerifyVersion(string path, string expectedVersion)
    {
        var actualVersion = versionReader.ReadVersion(path);
        if (!string.Equals(actualVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The installed artifact version '{actualVersion ?? "unreadable"}' does not match '{expectedVersion}'.");
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

    private static bool IsTerminal(ModDeploymentPhase phase) =>
        phase is ModDeploymentPhase.Committed or ModDeploymentPhase.RolledBack or ModDeploymentPhase.Failed;

    private static string NormalizeSha256(string value) =>
        TryNormalizeSha256(value, out var normalized)
            ? normalized
            : throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.", nameof(value));

    private static bool TryNormalizeSha256(string value, out string normalized)
    {
        normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
    }

}
