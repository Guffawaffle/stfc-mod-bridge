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
}

internal enum AtomicTomlMutationBoundary
{
    TemporaryWrite,
    Promotion,
    TemporaryDelete,
}

internal interface IAtomicTomlMutationAdmission
{
    ValueTask AdmitAsync(
        AtomicTomlMutationBoundary boundary,
        string temporaryPath,
        string destinationPath,
        CancellationToken cancellationToken);

    void TemporaryCreated(
        string temporaryPath,
        string destinationPath,
        long expectedSize,
        string expectedSha256)
    {
    }

    void TemporaryRemoved(string temporaryPath)
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
                () => temporaryCreated = true,
                cancellationToken).ConfigureAwait(false);
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

            File.Replace(temporaryPath, fullPath, backupPath, ignoreMetadataErrors: true);
            temporaryCreated = false;
            NotifyTemporaryRemoved(temporaryPath);
            return new(
                AtomicTomlWriteState.Succeeded,
                backupPath,
                BackupReceipt: backupReceipt);
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
                    fullPath).ConfigureAwait(false);
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
                () => temporaryCreated = true,
                cancellationToken).ConfigureAwait(false);
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
                File.Move(temporaryPath, fullPath);
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
                or NotSupportedException)
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
                    fullPath).ConfigureAwait(false);
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
            await WriteDurablyAsync(
                temporaryPath,
                fullPath,
                edit.Contents,
                () => temporaryCreated = true,
                cancellationToken).ConfigureAwait(false);
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

            File.Replace(temporaryPath, fullPath, backupPath, ignoreMetadataErrors: true);
            temporaryCreated = false;
            NotifyTemporaryRemoved(temporaryPath);
            return new(
                AtomicTomlWriteState.Succeeded,
                backupPath,
                BackupReceipt: backupReceipt);
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
                    fullPath).ConfigureAwait(false);
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
        Action created,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        created();
        mutationAdmission?.TemporaryCreated(
            path,
            destinationPath,
            contents.LongLength,
            Convert.ToHexString(SHA256.HashData(contents)));
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
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

    private async ValueTask TryCleanupTemporaryAsync(
        bool temporaryCreated,
        string temporaryPath,
        string destinationPath)
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
            await AdmitMutationAsync(
                AtomicTomlMutationBoundary.TemporaryDelete,
                temporaryPath,
                destinationPath,
                CancellationToken.None).ConfigureAwait(false);
            File.Delete(temporaryPath);
            NotifyTemporaryRemoved(temporaryPath);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException
                or OperationCanceledException)
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
