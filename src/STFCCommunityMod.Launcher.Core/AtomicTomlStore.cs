using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

public enum AtomicTomlWriteState
{
    Succeeded,
    NoChange,
    NoConfigurationSelected,
    Invalid,
    Conflict,
    IoFailure,
    Busy,
}

public sealed record AtomicTomlWriteResult(
    AtomicTomlWriteState State,
    string? BackupPath = null,
    SparseTomlError? ValidationError = null,
    string? Error = null,
    ConfigurationBackupReceipt? BackupReceipt = null)
{
    public bool IsSuccess => State is AtomicTomlWriteState.Succeeded or AtomicTomlWriteState.NoChange;

    public string? Warning { get; init; }
}

internal enum AtomicTomlMutationBoundary
{
    TemporaryWrite,
    Promotion,
    TemporaryDelete,
}

internal enum AtomicTomlTemporaryRole
{
    WriteStage,
    Rollback,
}

internal interface IAtomicTomlMutationAdmission
{
    ValueTask AdmitAsync(
        AtomicTomlMutationBoundary boundary,
        string temporaryPath,
        string destinationPath,
        CancellationToken cancellationToken);

    void TemporaryPreparing(
        AtomicTomlTemporaryRole role,
        string temporaryPath,
        string destinationPath,
        long expectedSize,
        string expectedSha256,
        bool deletionAllowed,
        string? committedDestinationSha256,
        CandidateFileIdentity? expectedIdentity = null,
        FileAttributes? expectedAttributes = null,
        long? expectedLastWriteTimeUtcTicks = null,
        FileAttributes? committedDestinationAttributes = null,
        long? committedDestinationLastWriteTimeUtcTicks = null)
    {
    }


    void TemporaryCreated(
        AtomicTomlTemporaryRole role,
        string temporaryPath,
        CandidateFileIdentity identity)
    {
    }

    void TemporaryCompleted(
        AtomicTomlTemporaryRole role,
        string temporaryPath,
        long actualSize,
        string actualSha256,
        bool deletionAllowed)
    {
    }

    void TemporaryRemoved(string temporaryPath)
    {
    }

    void VerifyCommitAllowed(
        AtomicTomlMutationBoundary boundary,
        string temporaryPath,
        string destinationPath)
    {
    }
}

public sealed class AtomicTomlStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private readonly Func<string, string, CancellationToken, ValueTask>? beforeReplace;
    private readonly IConfigurationMutationBackup? mutationBackup;
    private readonly IAtomicTomlMutationAdmission? mutationAdmission;
    private readonly bool retainAdjacentBackup;

    public AtomicTomlStore(
        Func<string, string, CancellationToken, ValueTask>? beforeReplace = null,
        bool retainAdjacentBackup = true)
        : this(beforeReplace, retainAdjacentBackup, mutationAdmission: null)
    {
    }

    internal AtomicTomlStore(
        Func<string, string, CancellationToken, ValueTask>? beforeReplace,
        bool retainAdjacentBackup,
        IAtomicTomlMutationAdmission? mutationAdmission)
    {
        this.beforeReplace = beforeReplace;
        this.retainAdjacentBackup = retainAdjacentBackup;
        this.mutationAdmission = mutationAdmission;
    }

    public AtomicTomlStore(
        IConfigurationMutationBackup mutationBackup,
        bool retainAdjacentBackup = false)
        : this(
            mutationBackup,
            beforeReplace: null,
            retainAdjacentBackup: retainAdjacentBackup,
            mutationAdmission: null)
    {
    }

    internal AtomicTomlStore(
        IConfigurationMutationBackup mutationBackup,
        Func<string, string, CancellationToken, ValueTask>? beforeReplace,
        bool retainAdjacentBackup = false,
        IAtomicTomlMutationAdmission? mutationAdmission = null)
    {
        this.mutationBackup = mutationBackup ?? throw new ArgumentNullException(nameof(mutationBackup));
        this.beforeReplace = beforeReplace;
        this.retainAdjacentBackup = retainAdjacentBackup;
        this.mutationAdmission = mutationAdmission;
    }

    public bool ProducesVerifiedBackupReceipt => mutationBackup is not null;

    public Task<AtomicTomlWriteResult> SetOverrideAsync(
        string? configurationPath,
        string canonicalPath,
        string renderedTomlValue,
        CancellationToken cancellationToken = default) =>
        TransformAsync(
            configurationPath,
            document => document.SetOverride(canonicalPath, renderedTomlValue),
            cancellationToken);

    public Task<AtomicTomlWriteResult> RemoveOverrideAsync(
        string? configurationPath,
        string canonicalPath,
        CancellationToken cancellationToken = default) =>
        TransformAsync(
            configurationPath,
            document => document.RemoveOverride(canonicalPath),
            cancellationToken);

    public async Task<AtomicTomlWriteResult> SaveDocumentAsync(
        string? configurationPath,
        byte[] expectedContents,
        byte[] updatedContents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedContents);
        ArgumentNullException.ThrowIfNull(updatedContents);
        expectedContents = [.. expectedContents];
        updatedContents = [.. updatedContents];

        var expectedValidation = ValidateDocument(expectedContents);
        if (expectedValidation is not null)
        {
            return new(
                AtomicTomlWriteState.Invalid,
                ValidationError: expectedValidation);
        }

        var updatedValidation = ValidateDocument(updatedContents);
        if (updatedValidation is not null)
        {
            return new(
                AtomicTomlWriteState.Invalid,
                ValidationError: updatedValidation);
        }

        if (expectedContents.AsSpan().SequenceEqual(updatedContents))
        {
            return new(AtomicTomlWriteState.NoChange);
        }

        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return new(AtomicTomlWriteState.NoConfigurationSelected);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(configurationPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new(AtomicTomlWriteState.IoFailure, Error: exception.Message);
        }

        var parentDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parentDirectory))
        {
            return new(
                AtomicTomlWriteState.IoFailure,
                Error: "The configuration path does not have a parent directory.");
        }

        var temporaryPath = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var backupPath = retainAdjacentBackup ? fullPath + ".bak" : null;
        var pathGate = PathGates.GetOrAdd(fullPath, static _ => new(1, 1));
        var gateHeld = false;
        var temporaryCreated = false;
        var temporaryCompleted = false;
        CandidateFileIdentity? temporaryIdentity = null;
        ConfigurationBackupReceipt? backupReceipt = null;

        try
        {
            await pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            cancellationToken.ThrowIfCancellationRequested();

            byte[] current;
            try
            {
                current = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error: "The configuration disappeared after the editing session began.");
            }

            if (!current.AsSpan().SequenceEqual(expectedContents))
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error: "The configuration changed after the editing session began; the external changes were preserved.");
            }

            await AdmitMutationAsync(
                AtomicTomlMutationBoundary.TemporaryWrite,
                temporaryPath,
                fullPath,
                cancellationToken).ConfigureAwait(false);
            await WriteDurablyAsync(
                temporaryPath,
                fullPath,
                updatedContents,
                identity =>
                {
                    temporaryCreated = true;
                    temporaryIdentity = identity;
                },
                cancellationToken).ConfigureAwait(false);
            temporaryCompleted = true;
            if (beforeReplace is not null)
            {
                await beforeReplace(temporaryPath, fullPath, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                current = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error: "The configuration disappeared before the atomic replacement.");
            }

            if (!current.AsSpan().SequenceEqual(expectedContents))
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error: "The configuration changed before it could be saved; the external changes were preserved.");
            }

            if (mutationBackup is not null)
            {
                backupReceipt = await CreateVerifiedBackupAsync(
                    mutationBackup,
                    fullPath,
                    expectedContents,
                    cancellationToken).ConfigureAwait(false);
            }

            await AdmitMutationAsync(
                AtomicTomlMutationBoundary.Promotion,
                temporaryPath,
                fullPath,
                cancellationToken).ConfigureAwait(false);
            try
            {
                current = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error: "The configuration disappeared after its verified backup was created.",
                    BackupReceipt: backupReceipt);
            }

            if (!current.AsSpan().SequenceEqual(expectedContents))
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error:
                        "The configuration changed after its verified backup was created; "
                        + "the external changes were preserved.",
                    BackupReceipt: backupReceipt);
            }

            var commit = await CommitReplacementAsync(
                temporaryPath,
                fullPath,
                expectedContents,
                updatedContents,
                backupPath,
                temporaryIdentity).ConfigureAwait(false);
            temporaryCreated = !commit.Promoted;
            if (commit.Conflict)
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    BackupPath: commit.RecoveryPath,
                    Error: commit.Error,
                    BackupReceipt: backupReceipt);
            }
            return new AtomicTomlWriteResult(
                AtomicTomlWriteState.Succeeded,
                commit.RecoveryPath ?? backupPath,
                BackupReceipt: backupReceipt)
            {
                Warning = commit.Warning,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidDataException
                or CryptographicException)
        {
            return new(
                AtomicTomlWriteState.IoFailure,
                backupPath is not null && File.Exists(backupPath) ? backupPath : null,
                Error: exception.Message,
                BackupReceipt: backupReceipt);
        }
        finally
        {
            try
            {
                await TryCleanupTemporaryAsync(
                    temporaryCreated,
                    temporaryPath,
                    fullPath,
                    updatedContents,
                    temporaryIdentity,
                    temporaryCompleted).ConfigureAwait(false);
            }
            finally
            {
                if (gateHeld)
                {
                    pathGate.Release();
                }
            }
        }
    }

    public async Task<AtomicTomlWriteResult> CreateDocumentAsync(
        string? configurationPath,
        byte[] contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        contents = [.. contents];
        var validation = ValidateDocument(contents);
        if (validation is not null)
        {
            return new(AtomicTomlWriteState.Invalid, ValidationError: validation);
        }
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return new(AtomicTomlWriteState.NoConfigurationSelected);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(configurationPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new(AtomicTomlWriteState.IoFailure, Error: exception.Message);
        }

        var parentDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parentDirectory))
        {
            return new(
                AtomicTomlWriteState.IoFailure,
                Error: "The configuration path does not have a parent directory.");
        }

        var temporaryPath = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var pathGate = PathGates.GetOrAdd(fullPath, static _ => new(1, 1));
        var gateHeld = false;
        var temporaryCreated = false;
        var temporaryCompleted = false;
        CandidateFileIdentity? temporaryIdentity = null;
        try
        {
            await pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPath))
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error: "The configuration was created outside Mod Bridge after the editing session began.");
            }

            await AdmitMutationAsync(
                AtomicTomlMutationBoundary.TemporaryWrite,
                temporaryPath,
                fullPath,
                cancellationToken).ConfigureAwait(false);
            await WriteDurablyAsync(
                temporaryPath,
                fullPath,
                contents,
                identity =>
                {
                    temporaryCreated = true;
                    temporaryIdentity = identity;
                },
                cancellationToken).ConfigureAwait(false);
            temporaryCompleted = true;
            if (beforeReplace is not null)
            {
                await beforeReplace(temporaryPath, fullPath, cancellationToken).ConfigureAwait(false);
            }
            await AdmitMutationAsync(
                AtomicTomlMutationBoundary.Promotion,
                temporaryPath,
                fullPath,
                cancellationToken).ConfigureAwait(false);
            if (File.Exists(fullPath))
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error: "The configuration was created outside Mod Bridge before the first save completed.");
            }

            try
            {
                using var stage = ExactFileMutation.Open(temporaryPath);
                if (temporaryIdentity is null || stage.Identity != temporaryIdentity)
                {
                    throw new InvalidDataException(
                        "The staged configuration identity changed before its first save and was preserved.");
                }
                EnsureRevisionContents(
                    stage.CaptureRevision(),
                    contents,
                    "The staged configuration changed before its first save and was preserved.");
                mutationAdmission?.VerifyCommitAllowed(
                    AtomicTomlMutationBoundary.Promotion,
                    temporaryPath,
                    fullPath);
                if (File.Exists(fullPath))
                {
                    return new(
                        AtomicTomlWriteState.Conflict,
                        Error: "The configuration was created outside Mod Bridge at the final promotion boundary.");
                }
                stage.MoveNoReplace(fullPath);
                temporaryCreated = false;
                NotifyTemporaryRemoved(temporaryPath);
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error: "The configuration was created outside Mod Bridge before the first save committed.");
            }
            return new(AtomicTomlWriteState.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidDataException)
        {
            return new(AtomicTomlWriteState.IoFailure, Error: exception.Message);
        }
        finally
        {
            try
            {
                await TryCleanupTemporaryAsync(
                    temporaryCreated,
                    temporaryPath,
                    fullPath,
                    contents,
                    temporaryIdentity,
                    temporaryCompleted).ConfigureAwait(false);
            }
            finally
            {
                if (gateHeld)
                {
                    pathGate.Release();
                }
            }
        }
    }

    private async Task<AtomicTomlWriteResult> TransformAsync(
        string? configurationPath,
        Func<SparseTomlDocument, SparseTomlEditResult> transform,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return new(AtomicTomlWriteState.NoConfigurationSelected);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(configurationPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new(AtomicTomlWriteState.IoFailure, Error: exception.Message);
        }

        var parentDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parentDirectory))
        {
            return new(
                AtomicTomlWriteState.IoFailure,
                Error: "The configuration path does not have a parent directory.");
        }

        var temporaryPath = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var backupPath = retainAdjacentBackup ? fullPath + ".bak" : null;
        var pathGate = PathGates.GetOrAdd(fullPath, static _ => new(1, 1));
        var gateHeld = false;
        var temporaryCreated = false;
        var temporaryCompleted = false;
        CandidateFileIdentity? temporaryIdentity = null;
        byte[]? temporaryContents = null;
        ConfigurationBackupReceipt? backupReceipt = null;

        try
        {
            await pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            cancellationToken.ThrowIfCancellationRequested();
            var original = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var originalSnapshot = DestinationSnapshot.Capture(fullPath, original);
            var load = SparseTomlDocument.Load(original, out var document);
            if (!load.IsValid || document is null)
            {
                return new(
                    AtomicTomlWriteState.Invalid,
                    ValidationError: load.Error);
            }

            var edit = transform(document);
            if (!edit.IsValid || edit.Contents is null)
            {
                return new(
                    AtomicTomlWriteState.Invalid,
                    ValidationError: edit.Error);
            }

            if (!edit.Changed)
            {
                return new(AtomicTomlWriteState.NoChange);
            }

            var transformedLoad = SparseTomlDocument.Load(edit.Contents, out var transformedDocument);
            if (!transformedLoad.IsValid || transformedDocument is null)
            {
                return new(
                    AtomicTomlWriteState.Invalid,
                    ValidationError: transformedLoad.Error);
            }

            var transformedValidation = transformedDocument.ValidateForMutation();
            if (!transformedValidation.IsValid)
            {
                return new(
                    AtomicTomlWriteState.Invalid,
                    ValidationError: transformedValidation.Error);
            }

            await AdmitMutationAsync(
                AtomicTomlMutationBoundary.TemporaryWrite,
                temporaryPath,
                fullPath,
                cancellationToken).ConfigureAwait(false);
            temporaryContents = edit.Contents;
            await WriteDurablyAsync(
                temporaryPath,
                fullPath,
                edit.Contents,
                identity =>
                {
                    temporaryCreated = true;
                    temporaryIdentity = identity;
                },
                cancellationToken).ConfigureAwait(false);
            temporaryCompleted = true;
            if (beforeReplace is not null)
            {
                await beforeReplace(temporaryPath, fullPath, cancellationToken).ConfigureAwait(false);
            }

            DestinationSnapshot currentSnapshot;
            try
            {
                var current = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                currentSnapshot = DestinationSnapshot.Capture(fullPath, current);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error: "The configuration disappeared before the atomic replacement.");
            }

            if (!originalSnapshot.Matches(currentSnapshot))
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error: "The configuration changed after it was read; the external changes were preserved.");
            }

            if (mutationBackup is not null)
            {
                backupReceipt = await CreateVerifiedBackupAsync(
                    mutationBackup,
                    fullPath,
                    original,
                    cancellationToken).ConfigureAwait(false);
            }

            await AdmitMutationAsync(
                AtomicTomlMutationBoundary.Promotion,
                temporaryPath,
                fullPath,
                cancellationToken).ConfigureAwait(false);
            try
            {
                var current = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                currentSnapshot = DestinationSnapshot.Capture(fullPath, current);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error: "The configuration disappeared after its verified backup was created.",
                    BackupReceipt: backupReceipt);
            }

            if (!originalSnapshot.Matches(currentSnapshot))
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    Error:
                        "The configuration changed after its verified backup was created; "
                        + "the external changes were preserved.",
                    BackupReceipt: backupReceipt);
            }

            var commit = await CommitReplacementAsync(
                temporaryPath,
                fullPath,
                original,
                edit.Contents,
                backupPath,
                temporaryIdentity).ConfigureAwait(false);
            temporaryCreated = !commit.Promoted;
            if (commit.Conflict)
            {
                return new(
                    AtomicTomlWriteState.Conflict,
                    BackupPath: commit.RecoveryPath,
                    Error: commit.Error,
                    BackupReceipt: backupReceipt);
            }
            return new AtomicTomlWriteResult(
                AtomicTomlWriteState.Succeeded,
                commit.RecoveryPath ?? backupPath,
                BackupReceipt: backupReceipt)
            {
                Warning = commit.Warning,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidDataException
                or CryptographicException)
        {
            return new(
                AtomicTomlWriteState.IoFailure,
                backupPath is not null && File.Exists(backupPath) ? backupPath : null,
                Error: exception.Message,
                BackupReceipt: backupReceipt);
        }
        finally
        {
            try
            {
                await TryCleanupTemporaryAsync(
                    temporaryCreated,
                    temporaryPath,
                    fullPath,
                    temporaryContents ?? [],
                    temporaryIdentity,
                    temporaryCompleted).ConfigureAwait(false);
            }
            finally
            {
                if (gateHeld)
                {
                    pathGate.Release();
                }
            }
        }
    }

    private async Task WriteDurablyAsync(
        string path,
        string destinationPath,
        byte[] contents,
        Action<CandidateFileIdentity> created,
        CancellationToken cancellationToken)
    {
        mutationAdmission?.TemporaryPreparing(
            AtomicTomlTemporaryRole.WriteStage,
            path,
            destinationPath,
            contents.LongLength,
            Convert.ToHexString(SHA256.HashData(contents)),
            deletionAllowed: true,
            committedDestinationSha256: null);
        var opened = false;
        try
        {
            mutationAdmission?.VerifyCommitAllowed(
                AtomicTomlMutationBoundary.TemporaryWrite,
                path,
                destinationPath);
            await using (var stream = new FileStream(
                             path,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                opened = true;
                var identity = CandidateFileNative.ReadIdentity(stream.SafeFileHandle);
                created(identity);
                mutationAdmission?.TemporaryCreated(
                    AtomicTomlTemporaryRole.WriteStage,
                    path,
                    identity);
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            mutationAdmission?.TemporaryCompleted(
                AtomicTomlTemporaryRole.WriteStage,
                path,
                contents.LongLength,
                Convert.ToHexString(SHA256.HashData(contents)),
                deletionAllowed: true);
        }
        catch
        {
            if (!opened)
            {
                NotifyTemporaryRemoved(path);
            }
            throw;
        }
    }

    private ValueTask AdmitMutationAsync(
        AtomicTomlMutationBoundary boundary,
        string temporaryPath,
        string destinationPath,
        CancellationToken cancellationToken) =>
        mutationAdmission is null
            ? ValueTask.CompletedTask
            : mutationAdmission.AdmitAsync(
                boundary,
                temporaryPath,
                destinationPath,
                cancellationToken);

    private async Task<ReplacementCommitResult> CommitReplacementAsync(
        string temporaryPath,
        string destinationPath,
        byte[] expectedDestination,
        byte[] intendedContents,
        string? retainedBackupPath,
        CandidateFileIdentity? expectedTemporaryIdentity)
    {
        var rollbackPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.rollback");
        using var stage = ExactFileMutation.Open(temporaryPath);
        if (expectedTemporaryIdentity is null || stage.Identity != expectedTemporaryIdentity)
        {
            throw new InvalidDataException(
                "The staged configuration identity changed before atomic promotion and was preserved.");
        }
        EnsureRevisionContents(
            stage.CaptureRevision(),
            intendedContents,
            "The staged configuration changed before atomic promotion and was preserved.");

        ExactFileMutation source;
        try
        {
            source = ExactFileMutation.Open(destinationPath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(
                Promoted: false,
                Conflict: true,
                "The configuration disappeared immediately before atomic promotion.");
        }
        using (source)
        {
            var sourceRevision = source.CaptureRevision();
            EnsureRevisionContents(
                sourceRevision,
                expectedDestination,
                "The configuration changed immediately before atomic promotion; the external changes were preserved.");
            var stageRevision = stage.CaptureRevision();
            mutationAdmission?.TemporaryPreparing(
                AtomicTomlTemporaryRole.Rollback,
                rollbackPath,
                destinationPath,
                sourceRevision.Length,
                sourceRevision.Sha256,
                deletionAllowed: true,
                committedDestinationSha256: stageRevision.Sha256,
                expectedIdentity: sourceRevision.Identity,
                expectedAttributes: sourceRevision.Attributes,
                expectedLastWriteTimeUtcTicks: sourceRevision.LastWriteTimeUtcTicks,
                committedDestinationAttributes: stageRevision.Attributes,
                committedDestinationLastWriteTimeUtcTicks: stageRevision.LastWriteTimeUtcTicks);

            mutationAdmission?.VerifyCommitAllowed(
                AtomicTomlMutationBoundary.Promotion,
                temporaryPath,
                destinationPath);
            if (File.Exists(rollbackPath))
            {
                NotifyTemporaryRemoved(rollbackPath);
                throw new IOException("The transaction rollback path already exists.");
            }

            source.MoveNoReplace(rollbackPath);
            source.Dispose();
            try
            {
                stage.MoveNoReplace(destinationPath);
            }
            catch (IOException)
            {
                if (!File.Exists(destinationPath))
                {
                    using var rollbackToRestore = ExactFileMutation.Open(rollbackPath);
                    if (rollbackToRestore.Identity == sourceRevision.Identity)
                    {
                        mutationAdmission?.VerifyCommitAllowed(
                            AtomicTomlMutationBoundary.Promotion,
                            rollbackPath,
                            destinationPath);
                        rollbackToRestore.MoveNoReplace(destinationPath);
                        NotifyTemporaryRemoved(rollbackPath);
                    }
                }
                return new(
                    Promoted: false,
                    Conflict: true,
                    "The configuration changed at the atomic promotion boundary; every revision was preserved.",
                    RecoveryPath: File.Exists(rollbackPath) ? rollbackPath : null);
            }

            NotifyTemporaryRemoved(temporaryPath);
            try
            {
                using (var rollback = ExactFileMutation.Open(rollbackPath))
                {
                    if (rollback.Identity != sourceRevision.Identity
                        || !rollback.CaptureRevision().Matches(sourceRevision))
                    {
                        throw new InvalidDataException(
                            "The transaction rollback identity changed and was preserved.");
                    }
                }
                mutationAdmission?.TemporaryCreated(
                    AtomicTomlTemporaryRole.Rollback,
                    rollbackPath,
                    sourceRevision.Identity);
                mutationAdmission?.TemporaryCompleted(
                    AtomicTomlTemporaryRole.Rollback,
                    rollbackPath,
                    sourceRevision.Length,
                    sourceRevision.Sha256,
                    deletionAllowed: true);

                using var rollbackForCleanup = ExactFileMutation.Open(rollbackPath);
                if (!rollbackForCleanup.CaptureRevision().Matches(sourceRevision))
                {
                    throw new InvalidDataException(
                        "The completed transaction rollback changed and was preserved.");
                }
                if (retainedBackupPath is not null)
                {
                    ArchiveRollback(rollbackForCleanup, retainedBackupPath);
                    NotifyTemporaryRemoved(rollbackPath);
                }
                else
                {
                    await AdmitMutationAsync(
                        AtomicTomlMutationBoundary.TemporaryDelete,
                        rollbackPath,
                        destinationPath,
                        CancellationToken.None).ConfigureAwait(false);
                    mutationAdmission?.VerifyCommitAllowed(
                        AtomicTomlMutationBoundary.TemporaryDelete,
                        rollbackPath,
                        destinationPath);
                    rollbackForCleanup.DeleteExact();
                    NotifyTemporaryRemoved(rollbackPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or NotSupportedException
                    or CryptographicException)
            {
                return new(
                    Promoted: true,
                    Conflict: false,
                    Error: null,
                    RecoveryPath: File.Exists(rollbackPath) ? rollbackPath : null,
                    Warning:
                        "The configuration was saved, but rollback cleanup could not be verified; recovery evidence was retained.");
            }
        }
        return new(Promoted: true, Conflict: false, Error: null);
    }

    private static void ArchiveRollback(
        ExactFileMutation rollback,
        string retainedBackupPath)
    {
        if (!File.Exists(retainedBackupPath))
        {
            rollback.MoveNoReplace(retainedBackupPath);
            return;
        }

        var previousPath = retainedBackupPath + $".{Guid.NewGuid():N}.previous";
        using var previous = ExactFileMutation.Open(retainedBackupPath);
        var previousIdentity = previous.Identity;
        previous.MoveNoReplace(previousPath);
        previous.Dispose();
        try
        {
            rollback.MoveNoReplace(retainedBackupPath);
            using var previousToDelete = ExactFileMutation.Open(previousPath);
            if (previousToDelete.Identity != previousIdentity)
            {
                throw new InvalidDataException(
                    "The previous backup identity changed and was preserved.");
            }
            previousToDelete.DeleteExact();
        }
        catch
        {
            if (!File.Exists(retainedBackupPath))
            {
                using var previousToRestore = ExactFileMutation.Open(previousPath);
                if (previousToRestore.Identity == previousIdentity)
                {
                    previousToRestore.MoveNoReplace(retainedBackupPath);
                }
            }
            throw;
        }
    }

    private static void EnsureRevisionContents(
        ExactFileRevision revision,
        byte[] expectedContents,
        string failureMessage)
    {
        if (revision.Length != expectedContents.LongLength
            || !string.Equals(
                revision.Sha256,
                Convert.ToHexString(SHA256.HashData(expectedContents)),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(failureMessage);
        }
    }

    private async ValueTask TryCleanupTemporaryAsync(
        bool temporaryCreated,
        string temporaryPath,
        string destinationPath,
        byte[] expectedContents,
        CandidateFileIdentity? expectedIdentity,
        bool temporaryCompleted)
    {
        try
        {
            if (!temporaryCreated)
            {
                return;
            }
            if (!File.Exists(temporaryPath))
            {
                NotifyTemporaryRemoved(temporaryPath);
                return;
            }
            using var temporary = ExactFileMutation.Open(temporaryPath);
            if (expectedIdentity is null || temporary.Identity != expectedIdentity)
            {
                return;
            }
            if (temporaryCompleted)
            {
                EnsureRevisionContents(
                    temporary.CaptureRevision(),
                    expectedContents,
                    "The completed temporary changed before cleanup and was preserved.");
            }
            await AdmitMutationAsync(
                AtomicTomlMutationBoundary.TemporaryDelete,
                temporaryPath,
                destinationPath,
                CancellationToken.None).ConfigureAwait(false);
            mutationAdmission?.VerifyCommitAllowed(
                AtomicTomlMutationBoundary.TemporaryDelete,
                temporaryPath,
                destinationPath);
            temporary.DeleteExact();
            NotifyTemporaryRemoved(temporaryPath);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or InvalidDataException
                or NotSupportedException
                or OperationCanceledException
                or System.ComponentModel.Win32Exception)
        {
            // Best effort only: never let temporary-file cleanup obscure the primary result.
        }
    }

    private void NotifyTemporaryRemoved(string temporaryPath)
    {
        try
        {
            mutationAdmission?.TemporaryRemoved(temporaryPath);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {
            // The temporary path is already absent. A stale external receipt is safe to retry.
        }
    }

    private sealed record ReplacementCommitResult(
        bool Promoted,
        bool Conflict,
        string? Error,
        string? RecoveryPath = null,
        string? Warning = null);

    private static async ValueTask<ConfigurationBackupReceipt> CreateVerifiedBackupAsync(
        IConfigurationMutationBackup mutationBackup,
        string configurationPath,
        byte[] expectedContents,
        CancellationToken cancellationToken)
    {
        var sourceContents = expectedContents.ToArray();
        var receipt = await mutationBackup.BeforeReplaceAsync(
            configurationPath,
            sourceContents,
            cancellationToken).ConfigureAwait(false);
        if (receipt is null
            || !string.Equals(
                receipt.ContentSha256,
                ConfigurationDocumentRevision.FromContents(expectedContents).Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The configuration backup receipt does not identify the exact source bytes.");
        }

        return receipt;
    }

    private static SparseTomlError? ValidateDocument(byte[] contents)
    {
        var load = SparseTomlDocument.Load(contents, out var document);
        if (!load.IsValid || document is null)
        {
            return load.Error;
        }

        var validation = document.ValidateForMutation();
        return validation.IsValid ? null : validation.Error;
    }

    private sealed record DestinationSnapshot(
        long Length,
        DateTime LastWriteTimeUtc,
        byte[] Sha256)
    {
        public static DestinationSnapshot Capture(string path, byte[] contents) =>
            new(
                contents.LongLength,
                File.GetLastWriteTimeUtc(path),
                SHA256.HashData(contents));

        public bool Matches(DestinationSnapshot other) =>
            Length == other.Length
            && LastWriteTimeUtc == other.LastWriteTimeUtc
            && CryptographicOperations.FixedTimeEquals(Sha256, other.Sha256);
    }
}
