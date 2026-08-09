using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace STFCCommunityMod.Launcher.Core;

public enum ReviewedCandidateRecoveryState
{
    Ready,
    Recovered,
    Busy,
    Blocked,
}

public sealed record ReviewedCandidateRecoveryResult(
    ReviewedCandidateRecoveryState State,
    int RecoveredCandidateCount,
    long RecoveredBytes,
    int BlockedCandidateCount,
    string Message)
{
    public bool CanAcquire => State is ReviewedCandidateRecoveryState.Ready or ReviewedCandidateRecoveryState.Recovered;
}

internal enum CandidateMemberStage
{
    Prepared,
    Writing,
    Complete,
}

internal sealed record CandidateFileIdentity(string VolumeSerialNumber, string FileIndex);

internal sealed record CandidateOwnedMember(
    string FileName,
    long ExpectedSize,
    string ExpectedSha256,
    CandidateMemberStage Stage,
    CandidateFileIdentity? FileIdentity);

internal sealed record CandidateOwnershipRecord(
    int SchemaVersion,
    int Revision,
    string ReceiptId,
    string CertificationFingerprint,
    string ProviderId,
    string ChannelId,
    string RuntimeDistributionId,
    CandidateOwnedMember Dll,
    CandidateOwnedMember? RuntimeManifest);

internal interface ICandidateOwnershipProtector
{
    byte[] Protect(byte[] contents);

    byte[] Unprotect(byte[] protectedContents);
}

internal sealed class WindowsDpapiCandidateOwnershipProtector : ICandidateOwnershipProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
        "STFC Mod Bridge reviewed candidate ownership v1");

    public byte[] Protect(byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Reviewed candidate recovery requires Windows current-user protection.");
        }
        return ProtectedData.Protect(contents, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedContents)
    {
        ArgumentNullException.ThrowIfNull(protectedContents);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Reviewed candidate recovery requires Windows current-user protection.");
        }
        return ProtectedData.Unprotect(protectedContents, Entropy, DataProtectionScope.CurrentUser);
    }
}

internal sealed class CandidateAcquisitionLifetime : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly SemaphoreSlim gate;
    private FileStream? stream;

    private CandidateAcquisitionLifetime(SemaphoreSlim gate, FileStream stream)
    {
        this.gate = gate;
        this.stream = stream;
    }

    public static async ValueTask<CandidateAcquisitionLifetime?> TryAcquireAsync(
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        var fullStateDirectory = Path.GetFullPath(stateDirectory);
        Directory.CreateDirectory(fullStateDirectory);
        var gate = ProcessGates.GetOrAdd(fullStateDirectory, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var stream = new FileStream(
                Path.Combine(fullStateDirectory, "candidate-acquisition.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.Asynchronous);
            return new(gate, stream);
        }
        catch (IOException)
        {
            gate.Release();
            return null;
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        var owned = Interlocked.Exchange(ref stream, null);
        if (owned is null)
        {
            return ValueTask.CompletedTask;
        }
        owned.Dispose();
        gate.Release();
        return ValueTask.CompletedTask;
    }
}

internal sealed class CandidateOwnershipStore
{
    internal const string FileName = "ownership.dpapi";
    internal const string NextFileName = "ownership.next.dpapi";
    internal const int MaximumProtectedBytes = 64 * 1024;
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
    };

    private readonly ICandidateOwnershipProtector protector;

    public CandidateOwnershipStore(ICandidateOwnershipProtector protector)
    {
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public static CandidateOwnershipRecord Create(
        string receiptId,
        string certificationFingerprint,
        ModInstallationAttribution attribution,
        ModReleaseArtifact artifact)
    {
        return new(
            SchemaVersion,
            Revision: 0,
            receiptId,
            certificationFingerprint,
            attribution.ProviderId,
            attribution.ReleaseChannelId,
            attribution.RuntimeDistributionId,
            new(
                artifact.FileName,
                artifact.Size,
                artifact.Sha256.ToUpperInvariant(),
                CandidateMemberStage.Prepared,
                FileIdentity: null),
            artifact.RuntimeManifest is null
                ? null
                : new(
                    artifact.RuntimeManifest.FileName,
                    artifact.RuntimeManifest.Size,
                    artifact.RuntimeManifest.Sha256.ToUpperInvariant(),
                    CandidateMemberStage.Prepared,
                    FileIdentity: null));
    }

    public static CandidateOwnershipRecord UpdateDll(
        CandidateOwnershipRecord ownership,
        CandidateMemberStage stage,
        CandidateFileIdentity identity) =>
        ownership with
        {
            Revision = checked(ownership.Revision + 1),
            Dll = ownership.Dll with { Stage = stage, FileIdentity = identity },
        };

    public static CandidateOwnershipRecord UpdateRuntimeManifest(
        CandidateOwnershipRecord ownership,
        CandidateMemberStage stage,
        CandidateFileIdentity identity)
    {
        if (ownership.RuntimeManifest is null)
        {
            throw new InvalidOperationException("The candidate has no runtime-manifest ownership member.");
        }
        return ownership with
        {
            Revision = checked(ownership.Revision + 1),
            RuntimeManifest = ownership.RuntimeManifest with { Stage = stage, FileIdentity = identity },
        };
    }

    public void Save(string candidateDirectory, CandidateOwnershipRecord ownership)
    {
        Validate(ownership, Path.GetFileName(candidateDirectory));
        var protectedBytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(ownership, JsonOptions));
        if (protectedBytes.LongLength is <= 0 or > MaximumProtectedBytes)
        {
            throw new InvalidDataException("Candidate ownership metadata is outside its size bound.");
        }

        var path = Path.Combine(candidateDirectory, FileName);
        var nextPath = Path.Combine(candidateDirectory, NextFileName);
        using (var stream = new FileStream(
            nextPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough))
        {
            stream.Write(protectedBytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(nextPath, path, overwrite: true);
        _ = Load(path);
    }

    public CandidateOwnershipRecord Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists
            || info.Length is <= 0 or > MaximumProtectedBytes
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Candidate ownership metadata is missing or unsafe.");
        }
        byte[] protectedBytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            protectedBytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(protectedBytes);
            if (stream.Length != info.Length)
            {
                throw new InvalidDataException("Candidate ownership metadata changed while it was read.");
            }
        }

        return LoadProtectedBytes(protectedBytes, Path.GetFileName(Path.GetDirectoryName(path)!));
    }

    internal CandidateOwnershipRecord LoadProtectedBytes(byte[] protectedBytes, string directoryName)
    {
        if (protectedBytes.LongLength is <= 0 or > MaximumProtectedBytes)
        {
            throw new InvalidDataException("Candidate ownership metadata is outside its size bound.");
        }
        byte[] bytes;
        try
        {
            bytes = protector.Unprotect(protectedBytes);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("Candidate ownership metadata failed current-user protection validation.", exception);
        }
        try
        {
            RejectDuplicateProperties(bytes);
            var value = JsonSerializer.Deserialize<CandidateOwnershipRecord>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Candidate ownership metadata is empty.");
            Validate(value, directoryName);
            return value;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Candidate ownership metadata is outside its closed schema.", exception);
        }
    }

    private static void Validate(CandidateOwnershipRecord value, string directoryName)
    {
        if (value is null
            || value.SchemaVersion != SchemaVersion
            || value.Revision < 0
            || string.IsNullOrWhiteSpace(value.ReceiptId)
            || value.ReceiptId != directoryName
            || !Guid.TryParseExact(value.ReceiptId, "N", out _)
            || !IsDigest(value.CertificationFingerprint)
            || string.IsNullOrWhiteSpace(value.ProviderId)
            || string.IsNullOrWhiteSpace(value.ChannelId)
            || string.IsNullOrWhiteSpace(value.RuntimeDistributionId))
        {
            throw new InvalidDataException("Candidate ownership metadata has invalid identity fields.");
        }
        ValidateMember(value.Dll, "version.dll", 128L * 1024L * 1024L);
        if (value.RuntimeManifest is not null)
        {
            ValidateMember(
                value.RuntimeManifest,
                ArtifactBoundRuntimeManifestParser.ManagedFileName,
                ArtifactBoundRuntimeManifestParser.MaximumManifestBytes);
        }
    }

    private static void ValidateMember(CandidateOwnedMember? member, string expectedFileName, long maximumSize)
    {
        if (member is null
            || member.FileName != expectedFileName
            || member.ExpectedSize is <= 0 || member.ExpectedSize > maximumSize
            || !IsDigest(member.ExpectedSha256)
            || !Enum.IsDefined(member.Stage)
            || member.Stage == CandidateMemberStage.Prepared && member.FileIdentity is not null
            || member.Stage != CandidateMemberStage.Prepared && !IsFileIdentity(member.FileIdentity))
        {
            throw new InvalidDataException("Candidate ownership metadata has an invalid member identity.");
        }
    }

    private static bool IsFileIdentity(CandidateFileIdentity? identity) =>
        identity is not null
        && identity.VolumeSerialNumber is not null
        && identity.VolumeSerialNumber.Length == 8
        && identity.VolumeSerialNumber.All(Uri.IsHexDigit)
        && identity.FileIndex is not null
        && identity.FileIndex.Length == 16
        && identity.FileIndex.All(Uri.IsHexDigit);

    private static bool IsDigest(string? value) =>
        value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 12 });
        RejectDuplicateProperties(document.RootElement);
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Candidate ownership metadata contains duplicate properties.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }
}

internal sealed class CandidateRecoveryService
{
    internal const int MaximumCandidateDirectories = 4;
    internal const int MaximumEntriesPerCandidate = 4;
    internal const long MaximumAggregateBytes = MaximumCandidateDirectories
        * (128L * 1024L * 1024L
            + ArtifactBoundRuntimeManifestParser.MaximumManifestBytes
            + CandidateOwnershipStore.MaximumProtectedBytes * 2L);

    private readonly string candidateRoot;
    private readonly CandidateOwnershipStore ownershipStore;
    private readonly Func<SafeFileHandle, bool> markDeleteOnClose;
    private readonly Func<string, ValueTask>? beforeRecoveryMemberOpen;

    public CandidateRecoveryService(
        string candidateRoot,
        CandidateOwnershipStore ownershipStore,
        Func<SafeFileHandle, bool> markDeleteOnClose,
        Func<string, ValueTask>? beforeRecoveryMemberOpen = null)
    {
        this.candidateRoot = candidateRoot;
        this.ownershipStore = ownershipStore;
        this.markDeleteOnClose = markDeleteOnClose;
        this.beforeRecoveryMemberOpen = beforeRecoveryMemberOpen;
    }

    public async Task<ReviewedCandidateRecoveryResult> RecoverUnderLifetimeAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(candidateRoot))
        {
            return Ready();
        }
        if ((File.GetAttributes(candidateRoot) & FileAttributes.ReparsePoint) != 0)
        {
            return Blocked(1);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var rootLock = CandidateFileNative.OpenRecoveryDirectoryReadNoFollow(candidateRoot);
                return await RecoverLockedRootAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or Win32Exception)
            {
                return Blocked(1);
            }
        }
        return await RecoverLockedRootAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReviewedCandidateRecoveryResult> RecoverLockedRootAsync(CancellationToken cancellationToken)
    {
        string[] rootEntries;
        try
        {
            rootEntries = Directory.EnumerateFileSystemEntries(candidateRoot)
                .Take(MaximumCandidateDirectories + 1)
                .ToArray();
        }
        catch (IOException)
        {
            return Blocked(1);
        }
        catch (UnauthorizedAccessException)
        {
            return Blocked(1);
        }
        if (rootEntries.Length > MaximumCandidateDirectories)
        {
            return Blocked(rootEntries.Length);
        }

        long aggregateBytes = 0;
        var inventories = new List<CandidateInventory>();
        foreach (var entry in rootEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (IOException)
            {
                return Blocked(rootEntries.Length);
            }
            catch (UnauthorizedAccessException)
            {
                return Blocked(rootEntries.Length);
            }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            {
                return Blocked(rootEntries.Length);
            }
            var inventory = InspectDirectory(entry, ref aggregateBytes);
            inventories.Add(inventory);
            if (aggregateBytes > MaximumAggregateBytes)
            {
                return Blocked(inventories.Count);
            }
        }

        var recoveredCount = 0;
        long recoveredBytes = 0;
        var blockedCount = 0;
        foreach (var inventory in inventories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RecoverDirectoryAsync(inventory).ConfigureAwait(false);
            recoveredBytes += result.RecoveredBytes;
            if (result.Recovered)
            {
                recoveredCount++;
            }
            if (result.Blocked)
            {
                blockedCount++;
            }
        }

        if (blockedCount > 0)
        {
            return new(
                ReviewedCandidateRecoveryState.Blocked,
                recoveredCount,
                recoveredBytes,
                blockedCount,
                "Mod Bridge found an abandoned mod download it cannot safely identify or clean. Close other Bridge windows, then try the mod download again. If it remains blocked, open Diagnostics and copy the report when asking for help; Bridge will leave the files untouched.");
        }
        if (recoveredCount > 0)
        {
            return new(
                ReviewedCandidateRecoveryState.Recovered,
                recoveredCount,
                recoveredBytes,
                0,
                recoveredCount == 1
                    ? "Mod Bridge safely removed one abandoned mod download. You can try again."
                    : $"Mod Bridge safely removed {recoveredCount} abandoned mod downloads. You can try again.");
        }
        return Ready();
    }

    private CandidateInventory InspectDirectory(string directory, ref long aggregateBytes)
    {
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(directory)
                .Take(MaximumEntriesPerCandidate + 1)
                .ToArray();
            if (entries.Length > MaximumEntriesPerCandidate)
            {
                return new(directory, null, entries, Unsafe: true);
            }
            foreach (var entry in entries)
            {
                var info = new FileInfo(entry);
                if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return new(directory, null, entries, Unsafe: true);
                }
                aggregateBytes = checked(aggregateBytes + info.Length);
            }
            var metadataPaths = new[]
            {
                Path.Combine(directory, CandidateOwnershipStore.FileName),
                Path.Combine(directory, CandidateOwnershipStore.NextFileName),
            }.Where(File.Exists).ToArray();
            CandidateOwnershipRecord ownership;
            try
            {
                if (metadataPaths.Length == 0)
                {
                    return new(directory, null, entries, Unsafe: true);
                }
                var records = metadataPaths.Select(ownershipStore.Load).ToArray();
                if (records.Select(value => value.ReceiptId).Distinct(StringComparer.Ordinal).Count() != 1)
                {
                    return new(directory, null, entries, Unsafe: true);
                }
                ownership = records.MaxBy(value => value.Revision)!;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or CryptographicException)
            {
                return new(directory, null, entries, Unsafe: true);
            }
            return new(directory, ownership, entries, Unsafe: false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or OverflowException)
        {
            return new(directory, null, [], Unsafe: true);
        }
    }

    private async Task<DirectoryRecovery> RecoverDirectoryAsync(CandidateInventory inventory)
    {
        if (inventory.Unsafe || inventory.Ownership is null)
        {
            return new(false, true, 0);
        }

        SafeFileHandle? directoryLock = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                directoryLock = CandidateFileNative.OpenRecoveryDirectoryReadDeleteNoFollow(inventory.Directory);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or Win32Exception)
            {
                return new(false, true, 0);
            }
        }

        var lockedOwnership = TryLockOwnership(inventory.Directory);
        if (lockedOwnership is null)
        {
            directoryLock?.Dispose();
            return new(false, true, 0);
        }
        var ownership = lockedOwnership.Ownership;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            CandidateOwnershipStore.FileName,
            CandidateOwnershipStore.NextFileName,
            ownership.Dll.FileName,
        };
        if (ownership.RuntimeManifest is not null)
        {
            allowed.Add(ownership.RuntimeManifest.FileName);
        }
        try
        {
            var foreign = inventory.Entries.Any(entry => !allowed.Contains(Path.GetFileName(entry)));
            var recoveredBytes = 0L;
            var blocked = foreign;

            var memberBlocked = false;
            foreach (var member in new[] { ownership.RuntimeManifest, ownership.Dll })
            {
                if (member is null)
                {
                    continue;
                }
                var path = Path.Combine(inventory.Directory, member.FileName);
                if (!File.Exists(path))
                {
                    continue;
                }
                var recovery = await TryDeleteMemberAsync(path, member).ConfigureAwait(false);
                recoveredBytes += recovery.DeletedBytes;
                memberBlocked |= !recovery.Deleted;
            }

            blocked |= memberBlocked;
            await lockedOwnership.DisposeAsync().ConfigureAwait(false);
            lockedOwnership = null;
            if (!memberBlocked)
            {
                foreach (var metadataName in new[] { CandidateOwnershipStore.NextFileName, CandidateOwnershipStore.FileName })
                {
                    var path = Path.Combine(inventory.Directory, metadataName);
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                    var recovery = await TryDeleteProtectedMetadataAsync(path, ownership.ReceiptId).ConfigureAwait(false);
                    recoveredBytes += recovery.DeletedBytes;
                    blocked |= !recovery.Deleted;
                }
            }

            if (OperatingSystem.IsWindows())
            {
                if (directoryLock is null || !markDeleteOnClose(directoryLock))
                {
                    blocked = true;
                }
                directoryLock?.Dispose();
                directoryLock = null;
            }
            else
            {
                try
                {
                    Directory.Delete(inventory.Directory, recursive: false);
                }
                catch (IOException)
                {
                    blocked = true;
                }
                catch (UnauthorizedAccessException)
                {
                    blocked = true;
                }
            }
            return new(!Directory.Exists(inventory.Directory), blocked, recoveredBytes);
        }
        finally
        {
            if (lockedOwnership is not null)
            {
                await lockedOwnership.DisposeAsync().ConfigureAwait(false);
            }
            directoryLock?.Dispose();
        }
    }

    private LockedOwnership? TryLockOwnership(string directory)
    {
        var streams = new List<FileStream>();
        try
        {
            var records = new List<CandidateOwnershipRecord>();
            foreach (var name in new[] { CandidateOwnershipStore.FileName, CandidateOwnershipStore.NextFileName })
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path))
                {
                    continue;
                }
                var stream = OperatingSystem.IsWindows()
                    ? new FileStream(
                        CandidateFileNative.OpenRecoveryReadNoFollow(path),
                        FileAccess.Read,
                        81920,
                        isAsync: false)
                    : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream);
                if (stream.Length is <= 0 or > CandidateOwnershipStore.MaximumProtectedBytes)
                {
                    throw new InvalidDataException("Candidate ownership metadata is outside its size bound.");
                }
                var bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                stream.Position = 0;
                records.Add(ownershipStore.LoadProtectedBytes(bytes, Path.GetFileName(directory)));
            }
            if (records.Count == 0
                || records.Select(value => value.ReceiptId).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                throw new InvalidDataException("Candidate ownership metadata is ambiguous.");
            }
            return new(records.MaxBy(value => value.Revision)!, streams);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or CryptographicException
            or Win32Exception)
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
            return null;
        }
    }

    private async Task<MemberDeletion> TryDeleteMemberAsync(string path, CandidateOwnedMember member)
    {
        FileStream? stream = null;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return new(false, 0);
            }
            if (beforeRecoveryMemberOpen is not null)
            {
                await beforeRecoveryMemberOpen(path).ConfigureAwait(false);
            }
            stream = ReviewedModArtifactCandidateAcquirer.OpenLockedReadForRecovery(path);
            var preparedEmpty = member.Stage == CandidateMemberStage.Prepared
                && member.FileIdentity is null
                && stream.Length == 0;
            if (!preparedEmpty
                && (member.FileIdentity is null
                    || CandidateFileNative.ReadIdentity(stream.SafeFileHandle) != member.FileIdentity
                    || !MatchesRecoverableContents(stream, member)))
            {
                return new(false, 0);
            }
            var length = stream.Length;
            if (OperatingSystem.IsWindows() && !markDeleteOnClose(stream.SafeFileHandle))
            {
                return new(false, 0);
            }
            await stream.DisposeAsync().ConfigureAwait(false);
            stream = null;
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(path);
            }
            return new(true, length);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or Win32Exception)
        {
            return new(false, 0);
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<MemberDeletion> TryDeleteProtectedMetadataAsync(string path, string expectedReceiptId)
    {
        FileStream? stream = null;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return new(false, 0);
            }
            stream = ReviewedModArtifactCandidateAcquirer.OpenLockedReadForRecovery(path);
            if (stream.Length is <= 0 or > CandidateOwnershipStore.MaximumProtectedBytes)
            {
                return new(false, 0);
            }
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            CandidateOwnershipRecord value;
            try
            {
                value = ownershipStore.LoadProtectedBytes(
                    bytes,
                    Path.GetFileName(Path.GetDirectoryName(path)!));
            }
            catch (Exception exception) when (exception is InvalidDataException
                or CryptographicException
                or JsonException)
            {
                return new(false, 0);
            }
            if (value.ReceiptId != expectedReceiptId)
            {
                return new(false, 0);
            }
            var length = stream.Length;
            if (OperatingSystem.IsWindows() && !markDeleteOnClose(stream.SafeFileHandle))
            {
                return new(false, 0);
            }
            await stream.DisposeAsync().ConfigureAwait(false);
            stream = null;
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(path);
            }
            return new(true, length);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or Win32Exception)
        {
            return new(false, 0);
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool MatchesRecoverableContents(FileStream stream, CandidateOwnedMember member)
    {
        if (stream.Length > member.ExpectedSize)
        {
            return false;
        }
        if (stream.Length < member.ExpectedSize)
        {
            return member.Stage == CandidateMemberStage.Writing;
        }
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(hash),
            Convert.FromHexString(member.ExpectedSha256));
    }

    private static ReviewedCandidateRecoveryResult Ready() => new(
        ReviewedCandidateRecoveryState.Ready,
        0,
        0,
        0,
        "Candidate storage is ready.");

    private static ReviewedCandidateRecoveryResult Blocked(int count) => new(
        ReviewedCandidateRecoveryState.Blocked,
        0,
        0,
        Math.Max(1, count),
        "Mod Bridge found abandoned download data it cannot safely identify. Close other Bridge windows, then try the mod download again. Bridge will not delete unknown or changed files; copy the Diagnostics report when asking for help.");

    private sealed record CandidateInventory(
        string Directory,
        CandidateOwnershipRecord? Ownership,
        IReadOnlyList<string> Entries,
        bool Unsafe);

    private sealed record DirectoryRecovery(bool Recovered, bool Blocked, long RecoveredBytes);

    private sealed record MemberDeletion(bool Deleted, long DeletedBytes);

    private sealed class LockedOwnership(
        CandidateOwnershipRecord ownership,
        IReadOnlyList<FileStream> streams) : IAsyncDisposable
    {
        public CandidateOwnershipRecord Ownership { get; } = ownership;

        public async ValueTask DisposeAsync()
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
