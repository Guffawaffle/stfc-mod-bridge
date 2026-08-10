using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STFCCommunityMod.Launcher.Core;

internal enum BattleRuntimeLockState
{
    Running,
    Clean,
}

internal enum BattleRuntimeLockAcquisitionState
{
    Acquired,
    Absent,
    Busy,
    RecoveryRequired,
    Invalid,
    Unavailable,
}

internal sealed record BattleRuntimeLockAcquisitionResult(
    BattleRuntimeLockAcquisitionState State,
    BattleRuntimeLockLease? Lease,
    BattleRuntimeLockRecord? PreviousRecord,
    string Code);

internal sealed record BattleRuntimeLockRecord(
    string OwnerId,
    BattleRuntimeLockState State,
    int ProcessId,
    string ProcessStartNonce,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? LastCleanCloseAtUtc);

internal static class BattleRuntimeLockCodec
{
    internal const string Schema = "stfc.battle-runtime-lock.v1";
    internal const string FileName = "runtime.lock";
    internal const int MaximumBytes = 4 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    public static byte[] Encode(BattleRuntimeLockRecord record)
    {
        Validate(record);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new LockDocument(
                Schema,
                record.OwnerId,
                State(record.State),
                record.ProcessId,
                record.ProcessStartNonce,
                FormatTimestamp(record.StartedAtUtc),
                record.LastCleanCloseAtUtc is null
                    ? null
                    : FormatTimestamp(record.LastCleanCloseAtUtc.Value)),
            JsonOptions);
        if (bytes.Length is <= 0 or > MaximumBytes)
        {
            throw Invalid();
        }
        return bytes;
    }

    public static BattleRuntimeLockRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes)
        {
            throw Invalid();
        }
        LockDocument document;
        try
        {
            RejectDuplicateProperties(bytes);
            document = JsonSerializer.Deserialize<LockDocument>(bytes, JsonOptions) ?? throw Invalid();
        }
        catch (JsonException exception)
        {
            throw Invalid(exception);
        }
        if (document.Schema != Schema
            || document.OwnerId is null
            || document.State is null
            || document.ProcessStartNonce is null
            || document.StartedAtUtc is null)
        {
            throw Invalid();
        }
        var record = new BattleRuntimeLockRecord(
            document.OwnerId,
            ParseState(document.State),
            document.ProcessId,
            document.ProcessStartNonce,
            ParseTimestamp(document.StartedAtUtc),
            document.LastCleanCloseAtUtc is null
                ? null
                : ParseTimestamp(document.LastCleanCloseAtUtc));
        Validate(record);
        var canonical = Encode(record);
        try
        {
            if (!bytes.SequenceEqual(canonical))
            {
                throw Invalid();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
        return record;
    }

    private static void Validate(BattleRuntimeLockRecord record)
    {
        if (!IsLowerHex(record.OwnerId, 32)
            || !Enum.IsDefined(record.State)
            || record.ProcessId <= 0
            || !IsLowerHex(record.ProcessStartNonce, 32)
            || record.StartedAtUtc.Offset != TimeSpan.Zero
            || record.LastCleanCloseAtUtc is { Offset: var offset } && offset != TimeSpan.Zero
            || record.LastCleanCloseAtUtc < record.StartedAtUtc
            || record.State == BattleRuntimeLockState.Running && record.LastCleanCloseAtUtc is not null
            || record.State == BattleRuntimeLockState.Clean && record.LastCleanCloseAtUtc is null)
        {
            throw Invalid();
        }
    }

    private static string State(BattleRuntimeLockState value) => value switch
    {
        BattleRuntimeLockState.Running => "running",
        BattleRuntimeLockState.Clean => "clean",
        _ => throw Invalid(),
    };

    private static BattleRuntimeLockState ParseState(string value) => value switch
    {
        "running" => BattleRuntimeLockState.Running,
        "clean" => BattleRuntimeLockState.Clean,
        _ => throw Invalid(),
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero
            || FormatTimestamp(parsed) != value)
        {
            throw Invalid();
        }
        return parsed;
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 3,
        });
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName
                && !names.Add(reader.GetString() ?? throw Invalid()))
            {
                throw Invalid();
            }
        }
    }

    private static InvalidDataException Invalid(Exception? inner = null) =>
        new("The Battle runtime lock record is invalid.", inner);

    private sealed record LockDocument(
        string? Schema,
        string? OwnerId,
        string? State,
        int ProcessId,
        string? ProcessStartNonce,
        string? StartedAtUtc,
        string? LastCleanCloseAtUtc);
}

internal sealed class BattleRuntimeLockLease : IAsyncDisposable
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly FileStream stream;
    private BattleRuntimeLockRecord record;
    private int disposed;

    internal BattleRuntimeLockLease(FileStream stream, BattleRuntimeLockRecord record)
    {
        this.stream = stream;
        this.record = record;
    }

    public BattleRuntimeLockRecord Record
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return record;
        }
    }

    public async Task MarkCleanAsync(
        DateTimeOffset cleanCloseAtUtc,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (record.State != BattleRuntimeLockState.Running)
            {
                throw new InvalidOperationException("The Battle runtime lock is already clean.");
            }
            var clean = record with
            {
                State = BattleRuntimeLockState.Clean,
                LastCleanCloseAtUtc = cleanCloseAtUtc,
            };
            var bytes = BattleRuntimeLockCodec.Encode(clean);
            try
            {
                stream.Position = 0;
                stream.SetLength(0);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                stream.Position = 0;
                var verified = new byte[bytes.Length];
                stream.ReadExactly(verified);
                try
                {
                    if (!bytes.AsSpan().SequenceEqual(verified)
                        || BattleRuntimeLockCodec.Decode(verified) != clean)
                    {
                        throw new InvalidDataException("The clean Battle runtime lock did not verify.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(verified);
                }
                record = clean;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }
}

internal sealed class BattleRuntimeLockStore
{
    private const string DeleteMarkerFileName = "battle-delete-v1.dpapi";
    private const string DeleteSuccessorFileName = "battle-delete-v1.dpapi.next";
    private readonly string stateRoot;
    private readonly string battleRoot;
    private readonly string path;

    public BattleRuntimeLockStore(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        this.stateRoot = Path.GetFullPath(stateRoot);
        battleRoot = Path.Combine(this.stateRoot, "battle");
        path = Path.Combine(battleRoot, BattleRuntimeLockCodec.FileName);
    }

    /// <summary>
    /// Takes process ownership of an already-provisioned Battle state. This is
    /// an explicit mutation beneath the existing root operation lease; it never
    /// bootstraps the Battle directory and refuses every active recovery owner.
    /// </summary>
    public async Task<BattleRuntimeLockAcquisitionResult> TryAcquireExistingAsync(
        LauncherOperationLease operationLease,
        BattleLifecycleJournalStore journal,
        BattleRuntimeLockRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(record);
        using var operationScope = operationLease.RetainFor(stateRoot);
        if (!PathEquals(journal.StateRoot, stateRoot))
        {
            throw new InvalidOperationException("The Battle journal belongs to a different state root.");
        }
        if (record.State != BattleRuntimeLockState.Running || record.ProcessId != Environment.ProcessId)
        {
            throw new InvalidOperationException(
                "A Battle runtime owner must begin in the current process and running state.");
        }

        var journalInspection = journal.Inspect();
        if (journalInspection.State == BattleLifecycleJournalState.Unavailable)
        {
            return Result(BattleRuntimeLockAcquisitionState.Unavailable, "battle-runtime-owner-unavailable");
        }
        if (journalInspection.State != BattleLifecycleJournalState.Absent)
        {
            return Result(
                BattleRuntimeLockAcquisitionState.RecoveryRequired,
                "battle-runtime-owner-recovery-required");
        }

        try
        {
            if (HasDeleteRecoveryState())
            {
                return Result(
                    BattleRuntimeLockAcquisitionState.RecoveryRequired,
                    "battle-runtime-owner-delete-recovery-required");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result(BattleRuntimeLockAcquisitionState.Unavailable, "battle-runtime-owner-unavailable");
        }

        try
        {
            var attributes = File.GetAttributes(battleRoot);
            if (!attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Result(BattleRuntimeLockAcquisitionState.Invalid, "battle-runtime-owner-invalid");
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Result(BattleRuntimeLockAcquisitionState.Absent, "battle-runtime-owner-absent");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result(BattleRuntimeLockAcquisitionState.Unavailable, "battle-runtime-owner-unavailable");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = BattleRuntimeLockCodec.Encode(record);
        FileStream? stream = null;
        try
        {
            using var battleHandle = OpenDirectoryNoFollow(battleRoot);
            BattleRuntimeLockRecord previous;
            try
            {
                stream = OpenExistingRuntimeNoFollow(path);
                if (stream.Length is <= 0 or > BattleRuntimeLockCodec.MaximumBytes)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    stream = null;
                    return Result(BattleRuntimeLockAcquisitionState.Invalid, "battle-runtime-owner-invalid");
                }
                var previousBytes = new byte[checked((int)stream.Length)];
                try
                {
                    stream.ReadExactly(previousBytes);
                    previous = BattleRuntimeLockCodec.Decode(previousBytes);
                }
                catch (InvalidDataException)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    stream = null;
                    return Result(BattleRuntimeLockAcquisitionState.Invalid, "battle-runtime-owner-invalid");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(previousBytes);
                }
            }
            catch (Exception exception) when (IsMissing(exception))
            {
                return Result(BattleRuntimeLockAcquisitionState.Absent, "battle-runtime-owner-absent");
            }

            WriteAndVerify(stream, record, bytes);
            var lease = new BattleRuntimeLockLease(stream, record);
            stream = null;
            return new(
                BattleRuntimeLockAcquisitionState.Acquired,
                lease,
                previous,
                previous.State == BattleRuntimeLockState.Running
                    ? "battle-runtime-owner-acquired-after-unclean"
                    : "battle-runtime-owner-acquired-after-clean");
        }
        catch (Exception exception) when (IsSharingViolation(exception))
        {
            return Result(BattleRuntimeLockAcquisitionState.Busy, "battle-runtime-owner-busy");
        }
        catch (InvalidDataException)
        {
            return Result(BattleRuntimeLockAcquisitionState.Invalid, "battle-runtime-owner-invalid");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return Result(BattleRuntimeLockAcquisitionState.Unavailable, "battle-runtime-owner-unavailable");
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<BattleRuntimeLockLease> CreateBoundRunningAsync(
        LauncherOperationLease operationLease,
        BattleLifecycleJournalStore journal,
        BattleRuntimeLockRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(record);
        using var operationScope = operationLease.RetainFor(stateRoot);
        if (!string.Equals(
                journal.StateRoot,
                stateRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Battle journal belongs to a different state root.");
        }
        if (record.State != BattleRuntimeLockState.Running)
        {
            throw new InvalidOperationException("A new Battle runtime lock must begin in the running state.");
        }
        if (record.ProcessId != Environment.ProcessId)
        {
            throw new InvalidOperationException("A new Battle runtime lock must identify the current process.");
        }
        var inspection = journal.Inspect();
        if (inspection.State is not (
                BattleLifecycleJournalState.Readable
                or BattleLifecycleJournalState.RecoverableResidue)
            || inspection.Marker is not { Stage: BattleLifecycleStage.Prepared } marker
            || marker.OwnerId != record.OwnerId)
        {
            throw new InvalidOperationException("A matching prepared Battle marker is required before runtime ownership.");
        }
        var bytes = BattleRuntimeLockCodec.Encode(record);
        try
        {
            var identity = new BattleLifecycleFileIdentity(
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            var transition = marker.Resources.SingleOrDefault(resource => resource.Role == "runtime-lock");
            if (transition?.PrimaryRelativePath != $"battle/{BattleRuntimeLockCodec.FileName}"
                || transition.Before is not null
                || transition.CandidateRelativePath is not null
                || transition.After != identity)
            {
                throw new InvalidOperationException("The prepared marker does not bind the exact runtime lock.");
            }
            using var battleHandle = OpenDirectoryNoFollow(battleRoot);
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            try
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                stream.Position = 0;
                var verified = new byte[bytes.Length];
                stream.ReadExactly(verified);
                try
                {
                    if (!bytes.AsSpan().SequenceEqual(verified)
                        || BattleRuntimeLockCodec.Decode(verified) != record)
                    {
                        throw new InvalidDataException("The running Battle runtime lock did not verify.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(verified);
                }
                return new(stream, record);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static IDisposable OpenDirectoryNoFollow(string path) => OperatingSystem.IsWindows()
        ? CandidateFileNative.OpenRecoveryDirectoryReadNoFollow(path)
        : NoopDisposable.Instance;

    private bool HasDeleteRecoveryState() =>
        EntryExists(Path.Combine(stateRoot, DeleteMarkerFileName))
        || EntryExists(Path.Combine(stateRoot, DeleteSuccessorFileName))
        || Directory.EnumerateDirectories(stateRoot, "battle.delete.*", SearchOption.TopDirectoryOnly)
            .Take(1)
            .Any();

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (IsMissing(exception))
        {
            return false;
        }
    }

    private static FileStream OpenExistingRuntimeNoFollow(string path) => OperatingSystem.IsWindows()
        ? new(
            CandidateFileNative.OpenRuntimeLockReadWriteNoFollow(path),
            FileAccess.ReadWrite,
            4096,
            isAsync: true)
        : new(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);

    private static void WriteAndVerify(
        FileStream stream,
        BattleRuntimeLockRecord record,
        byte[] bytes)
    {
        stream.Position = 0;
        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        stream.Position = 0;
        var verified = new byte[bytes.Length];
        try
        {
            stream.ReadExactly(verified);
            if (!bytes.AsSpan().SequenceEqual(verified)
                || BattleRuntimeLockCodec.Decode(verified) != record)
            {
                throw new InvalidDataException("The running Battle runtime lock did not verify.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verified);
        }
    }

    private static bool IsMissing(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException
        || exception is Win32Exception { NativeErrorCode: 2 or 3 };

    private static bool IsSharingViolation(Exception exception) =>
        exception is IOException ioException
            && (ioException.HResult & 0xffff) is 32 or 33
        || exception is Win32Exception { NativeErrorCode: 32 or 33 };

    private static BattleRuntimeLockAcquisitionResult Result(
        BattleRuntimeLockAcquisitionState state,
        string code) => new(state, null, null, code);

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
