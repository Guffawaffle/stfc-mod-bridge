using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace STFCCommunityMod.Launcher.Core;

public static class BattleLocalIpcProtocol
{
    public const string Version = "stfc.battle-bridge.local-ipc.v1";
    public const string RuntimeRole = "stfc-mod-runtime";
    public const string IngestOperation = "ingest";
    public const int MaximumHeaderBytes = 4096;

    internal static bool IsPipeNameValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');

    internal static string RequirePipeName(string value, string parameterName = "value")
    {
        if (!IsPipeNameValid(value))
        {
            throw new ArgumentException("Battle local IPC pipe name is invalid.", parameterName);
        }
        return value;
    }
}

public sealed record BattleNamedPipeClientIdentity(
    uint ProcessId,
    string Role,
    string Operation);

public interface IBattleNamedPipeClientAuthorizer
{
    bool IsAuthorized(
        BattleNamedPipeClientIdentity identity,
        string runtimeEvidenceSha256);
}

public sealed record BattleNamedPipeAuthorizedProcess
{
    public BattleNamedPipeAuthorizedProcess(
        uint processId,
        DateTimeOffset processStartUtc,
        string executablePath,
        string runtimeEvidenceSha256)
    {
        ArgumentOutOfRangeException.ThrowIfZero(processId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(processId, unchecked((uint)int.MaxValue));
        if (processStartUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Authorized process start time must be UTC.", nameof(processStartUtc));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var normalizedPath = Path.GetFullPath(executablePath);
        if (!Path.IsPathFullyQualified(normalizedPath))
        {
            throw new ArgumentException("Authorized process path must be absolute.", nameof(executablePath));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeEvidenceSha256);
        if (runtimeEvidenceSha256.Length != 64
            || runtimeEvidenceSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Runtime evidence SHA-256 is invalid.", nameof(runtimeEvidenceSha256));
        }
        ProcessId = processId;
        ProcessStartUtc = processStartUtc;
        ExecutablePath = normalizedPath;
        RuntimeEvidenceSha256 = runtimeEvidenceSha256.ToLowerInvariant();
    }

    public uint ProcessId { get; }

    public DateTimeOffset ProcessStartUtc { get; }

    public string ExecutablePath { get; }

    public string RuntimeEvidenceSha256 { get; }
}

public sealed class ExactProcessBattleNamedPipeClientAuthorizer(
    IEnumerable<BattleNamedPipeAuthorizedProcess> allowedProcesses) : IBattleNamedPipeClientAuthorizer
{
    private readonly FrozenDictionary<uint, BattleNamedPipeAuthorizedProcess> allowedProcesses =
        (allowedProcesses ?? throw new ArgumentNullException(nameof(allowedProcesses)))
        .ToFrozenDictionary(process => process.ProcessId);

    public bool IsAuthorized(
        BattleNamedPipeClientIdentity identity,
        string runtimeEvidenceSha256) =>
        identity.Role == BattleLocalIpcProtocol.RuntimeRole
        && identity.Operation == BattleLocalIpcProtocol.IngestOperation
        && allowedProcesses.TryGetValue(identity.ProcessId, out var expected)
        && string.Equals(
            expected.RuntimeEvidenceSha256,
            runtimeEvidenceSha256,
            StringComparison.Ordinal)
        && Matches(expected);

    private static bool Matches(BattleNamedPipeAuthorizedProcess expected)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)expected.ProcessId));
            return !process.HasExited
                && new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)
                    == expected.ProcessStartUtc
                && string.Equals(
                    Path.GetFullPath(process.MainModule!.FileName),
                    expected.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

public enum BattleLocalIpcState
{
    Inactive,
    Starting,
    Listening,
    Stopping,
    Stopped,
    Failed,
}

public sealed record BattleLocalIpcHealth(
    BattleLocalIpcState State,
    long AcceptedRequests,
    long RejectedRequests,
    int ActiveRequests,
    BattleIngestFailureCode LastFailure,
    string LastTransition);

public sealed record BattleLocalIpcStartResult(
    BattleLocalIpcState State,
    BattleIngestFailureCode FailureCode);

public sealed class BattleNamedPipeIngestHost : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly BattleIngestActivation activation;
    private readonly string pipeName;
    private readonly byte[] credential;
    private readonly IBattleIngestSink sink;
    private readonly IBattleNamedPipeClientAuthorizer authorizer;
    private readonly string runtimeEvidenceSha256;
    private readonly BattleIngestLimits limits;
    private readonly BattleIngestEnvelopeProcessor processor;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim requestSlots;
    private readonly HashSet<Task> handlers = [];
    private readonly HashSet<NamedPipeServerStream> activeConnections = [];
    private CancellationTokenSource? acceptCancellation;
    private CancellationTokenSource? workCancellation;
    private NamedPipeServerStream? waitingServer;
    private Task? acceptLoop;
    private BattleLocalIpcState state = BattleLocalIpcState.Inactive;
    private BattleIngestFailureCode lastFailure;
    private string lastTransition = "inactive";
    private long acceptedRequests;
    private long rejectedRequests;
    private int activeRequests;
    private DateTimeOffset rateWindowStarted;
    private int requestsInWindow;
    private bool startAttempted;
    private bool disposed;

    public BattleNamedPipeIngestHost(
        BattleIngestActivation activation,
        string pipeName,
        string credential,
        IBattleIngestSink sink,
        IBattleNamedPipeClientAuthorizer authorizer,
        string runtimeEvidenceSha256,
        BattleIngestLimits? limits = null,
        TimeProvider? timeProvider = null)
        : this(
            activation,
            pipeName,
            DecodeCredential(credential),
            sink,
            authorizer,
            runtimeEvidenceSha256,
            limits,
            timeProvider)
    {
    }

    internal BattleNamedPipeIngestHost(
        BattleIngestActivation activation,
        string pipeName,
        ReadOnlySpan<byte> credential,
        IBattleIngestSink sink,
        IBattleNamedPipeClientAuthorizer authorizer,
        string runtimeEvidenceSha256,
        BattleIngestLimits? limits = null,
        TimeProvider? timeProvider = null)
        : this(
            activation,
            pipeName,
            CopyCredential(credential),
            sink,
            authorizer,
            runtimeEvidenceSha256,
            limits,
            timeProvider)
    {
    }

    private BattleNamedPipeIngestHost(
        BattleIngestActivation activation,
        string pipeName,
        byte[] credential,
        IBattleIngestSink sink,
        IBattleNamedPipeClientAuthorizer authorizer,
        string runtimeEvidenceSha256,
        BattleIngestLimits? limits,
        TimeProvider? timeProvider)
    {
        try
        {
            this.activation = activation ?? throw new ArgumentNullException(nameof(activation));
            if (!activation.IsReviewedFeatureComposition)
            {
                throw new ArgumentException(
                    "Battle local IPC requires the reviewed capability, policy, and player-intent composition.",
                    nameof(activation));
            }
            this.pipeName = BattleLocalIpcProtocol.RequirePipeName(pipeName, nameof(pipeName));
            this.credential = credential;
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
            this.authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
            this.runtimeEvidenceSha256 = RequireSha256(runtimeEvidenceSha256);
            this.limits = limits ?? BattleIngestLimits.Default;
            this.limits.Validate();
            this.timeProvider = timeProvider ?? TimeProvider.System;
            processor = new(activation, this.limits, this.timeProvider);
            requestSlots = new(this.limits.MaximumConcurrentRequests, this.limits.MaximumConcurrentRequests);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(credential);
            throw;
        }
    }

    public BattleLocalIpcHealth GetHealth()
    {
        lock (sync)
        {
            return new(
                state,
                acceptedRequests,
                rejectedRequests,
                activeRequests,
                lastFailure,
                lastTransition);
        }
    }

    public async Task<BattleLocalIpcStartResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (startAttempted)
            {
                throw new InvalidOperationException("The Battle local IPC host is one-shot.");
            }
            startAttempted = true;
            if (!activation.ShouldListen)
            {
                SetTransition(BattleLocalIpcState.Inactive, "inactive", BattleIngestFailureCode.None);
                return new(BattleLocalIpcState.Inactive, BattleIngestFailureCode.None);
            }
            if (!OperatingSystem.IsWindows())
            {
                SetTransition(BattleLocalIpcState.Failed, "unsupported-platform", BattleIngestFailureCode.StartFailed);
                return new(BattleLocalIpcState.Failed, BattleIngestFailureCode.StartFailed);
            }

            SetTransition(BattleLocalIpcState.Starting, "starting", BattleIngestFailureCode.None);
            acceptCancellation = new();
            workCancellation = new();
            try
            {
                waitingServer = CreateServer(firstInstance: true);
                acceptLoop = AcceptLoopAsync(waitingServer, acceptCancellation.Token, workCancellation.Token);
                SetTransition(BattleLocalIpcState.Listening, "listening", BattleIngestFailureCode.None);
                return new(BattleLocalIpcState.Listening, BattleIngestFailureCode.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                waitingServer?.Dispose();
                waitingServer = null;
                acceptCancellation.Dispose();
                workCancellation.Dispose();
                acceptCancellation = null;
                workCancellation = null;
                SetTransition(BattleLocalIpcState.Failed, "start-failed", BattleIngestFailureCode.StartFailed);
                return new(BattleLocalIpcState.Failed, BattleIngestFailureCode.StartFailed);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state is BattleLocalIpcState.Inactive or BattleLocalIpcState.Stopped)
            {
                return;
            }
            if (state == BattleLocalIpcState.Failed && acceptLoop is null)
            {
                return;
            }
            SetTransition(BattleLocalIpcState.Stopping, "stopping", BattleIngestFailureCode.None);
            acceptCancellation?.Cancel();
            lock (sync)
            {
                waitingServer?.Dispose();
            }
            if (acceptLoop is not null)
            {
                await ObserveExpectedStopAsync(acceptLoop).ConfigureAwait(false);
            }

            Task[] pending;
            lock (sync)
            {
                pending = handlers.ToArray();
            }
            if (pending.Length > 0)
            {
                var all = Task.WhenAll(pending);
                try
                {
                    await all.WaitAsync(limits.ShutdownDrainTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    workCancellation?.Cancel();
                    NamedPipeServerStream[] connections;
                    lock (sync)
                    {
                        connections = activeConnections.ToArray();
                    }
                    foreach (var connection in connections)
                    {
                        connection.Dispose();
                    }
                    await all.ConfigureAwait(false);
                }
            }
            processor.ClearPendingChunks();
            SetTransition(BattleLocalIpcState.Stopped, "stopped", BattleIngestFailureCode.None);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        await StopAsync().ConfigureAwait(false);
        disposed = true;
        acceptCancellation?.Dispose();
        workCancellation?.Dispose();
        CryptographicOperations.ZeroMemory(credential);
        requestSlots.Dispose();
        lifecycleGate.Dispose();
    }

    private async Task AcceptLoopAsync(
        NamedPipeServerStream initial,
        CancellationToken acceptToken,
        CancellationToken workToken)
    {
        var server = initial;
        var slotOwned = false;
        try
        {
            while (!acceptToken.IsCancellationRequested)
            {
                await requestSlots.WaitAsync(acceptToken).ConfigureAwait(false);
                slotOwned = true;
                await server.WaitForConnectionAsync(acceptToken).ConfigureAwait(false);
                lock (sync)
                {
                    waitingServer = null;
                }
                var handler = HandleConnectionAsync(server, workToken);
                TrackHandler(handler);
                slotOwned = false;
                server = CreateServer(firstInstance: false);
                lock (sync)
                {
                    waitingServer = server;
                }
            }
        }
        catch (Exception exception) when (
            acceptToken.IsCancellationRequested
            && exception is OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
        catch
        {
            SetTransition(BattleLocalIpcState.Failed, "listener-failed", BattleIngestFailureCode.ListenerFailed);
            throw;
        }
        finally
        {
            if (slotOwned)
            {
                requestSlots.Release();
            }
            server.Dispose();
            lock (sync)
            {
                if (ReferenceEquals(waitingServer, server))
                {
                    waitingServer = null;
                }
            }
        }
    }

    private void TrackHandler(Task handler)
    {
        lock (sync)
        {
            handlers.Add(handler);
        }
        _ = handler.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (sync)
                {
                    handlers.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream connection,
        CancellationToken hostToken)
    {
        lock (sync)
        {
            activeRequests++;
            activeConnections.Add(connection);
        }
        using (connection)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(hostToken))
        {
            deadline.CancelAfter(limits.RequestTimeout);
            var token = deadline.Token;
            try
            {
                var headerBytes = await ReadFrameAsync(
                    connection,
                    BattleLocalIpcProtocol.MaximumHeaderBytes,
                    token).ConfigureAwait(false);
                if (!TryAdmitRequest())
                {
                    await RejectAsync(connection, BattleIngestFailureCode.RateLimited, token).ConfigureAwait(false);
                    return;
                }
                var header = ParseHeader(headerBytes);
                if (!Authenticate(header.Credential))
                {
                    await RejectAsync(connection, BattleIngestFailureCode.Unauthorized, token).ConfigureAwait(false);
                    return;
                }
                var processId = GetClientProcessId(connection.SafePipeHandle);
                if (!authorizer.IsAuthorized(
                        new(processId, header.Role, header.Operation),
                        runtimeEvidenceSha256))
                {
                    await RejectAsync(connection, BattleIngestFailureCode.Unauthorized, token).ConfigureAwait(false);
                    return;
                }
                await WriteResponseAsync(
                    connection,
                    "ready",
                    0,
                    BattleIngestFailureCode.None,
                    token).ConfigureAwait(false);
                var payload = await ReadFrameAsync(connection, limits.MaximumRequestBytes, token).ConfigureAwait(false);
                var parsed = processor.Parse(payload);
                using (parsed.ProcessingLease)
                {
                    if (parsed.Status == BattleIngestParseStatus.Rejected)
                    {
                        await RejectAsync(connection, parsed.FailureCode, token).ConfigureAwait(false);
                        return;
                    }
                    if (parsed.Status == BattleIngestParseStatus.ChunkPending)
                    {
                        await WriteResponseAsync(connection, "chunk-pending", 0, BattleIngestFailureCode.None, token)
                            .ConfigureAwait(false);
                        return;
                    }

                    BattleIngestCommitResult committed;
                    try
                    {
                        committed = await sink.CommitAsync(parsed.Envelope!, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        await RejectAsync(connection, BattleIngestFailureCode.StorageRejected, token)
                            .ConfigureAwait(false);
                        return;
                    }
                    if (committed.AcceptedRecords < 0
                        || committed.AcceptedRecords > limits.MaximumBatchEvents)
                    {
                        await RejectAsync(connection, BattleIngestFailureCode.StorageRejected, token)
                            .ConfigureAwait(false);
                        return;
                    }
                    lock (sync)
                    {
                        acceptedRequests++;
                        lastFailure = BattleIngestFailureCode.None;
                    }
                    await WriteResponseAsync(
                        connection,
                        "accepted",
                        committed.AcceptedRecords,
                        BattleIngestFailureCode.None,
                        token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                await TryRejectAsync(connection, BattleIngestFailureCode.TimedOut).ConfigureAwait(false);
            }
            catch (FrameTooLargeException)
            {
                await TryRejectAsync(connection, BattleIngestFailureCode.PayloadTooLarge).ConfigureAwait(false);
            }
            catch (UnsupportedLocalIpcProtocolException)
            {
                await TryRejectAsync(connection, BattleIngestFailureCode.UnsupportedProtocol).ConfigureAwait(false);
            }
            catch (UnauthorizedLocalIpcRequestException)
            {
                await TryRejectAsync(connection, BattleIngestFailureCode.Unauthorized).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                await TryRejectAsync(connection, BattleIngestFailureCode.InvalidRequest).ConfigureAwait(false);
            }
            catch
            {
                await TryRejectAsync(connection, BattleIngestFailureCode.StorageRejected).ConfigureAwait(false);
            }
            finally
            {
                lock (sync)
                {
                    activeRequests--;
                    activeConnections.Remove(connection);
                }
                requestSlots.Release();
            }
        }
    }

    private async Task RejectAsync(
        PipeStream connection,
        BattleIngestFailureCode failure,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            rejectedRequests++;
            lastFailure = failure;
        }
        await WriteResponseAsync(connection, "rejected", 0, failure, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryRejectAsync(PipeStream connection, BattleIngestFailureCode failure)
    {
        try
        {
            await RejectAsync(connection, failure, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private NamedPipeServerStream CreateServer(bool firstInstance) =>
        new(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
            | PipeOptions.CurrentUserOnly
            | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            0,
            0);

    private bool Authenticate(string candidate)
    {
        byte[] decoded;
        try
        {
            decoded = DecodeCredential(candidate);
        }
        catch (ArgumentException)
        {
            return false;
        }
        try
        {
            return CryptographicOperations.FixedTimeEquals(credential, decoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private bool TryAdmitRequest()
    {
        lock (sync)
        {
            var now = timeProvider.GetUtcNow();
            if (rateWindowStarted == default
                || now - rateWindowStarted >= limits.RateWindow)
            {
                rateWindowStarted = now;
                requestsInWindow = 0;
            }
            if (requestsInWindow >= limits.RequestsPerWindow)
            {
                return false;
            }
            requestsInWindow++;
            return true;
        }
    }

    private static LocalIpcHeader ParseHeader(ReadOnlyMemory<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes.Span, new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4,
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException("Local IPC header must be an object.");
        }
        string? protocol = null;
        string? role = null;
        string? operation = null;
        string? credential = null;
        var properties = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new InvalidDataException("Local IPC header is malformed.");
            }
            var name = reader.GetString()!;
            if (!properties.Add(name) || !reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new InvalidDataException("Local IPC header properties must be unique strings.");
            }
            var value = reader.GetString()!;
            if (value.Length is 0 or > 160 || value.Any(char.IsControl))
            {
                throw new InvalidDataException("Local IPC header value is invalid.");
            }
            switch (name)
            {
                case "protocolVersion": protocol = value; break;
                case "role": role = value; break;
                case "operation": operation = value; break;
                case "credential": credential = value; break;
                default: throw new InvalidDataException("Local IPC header contains an unknown property.");
            }
        }
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
        {
            throw new InvalidDataException("Local IPC header has trailing content.");
        }
        if (protocol is null || role is null || operation is null || credential is null || properties.Count != 4)
        {
            throw new InvalidDataException("Local IPC header contract is incomplete.");
        }
        if (protocol != BattleLocalIpcProtocol.Version)
        {
            throw new UnsupportedLocalIpcProtocolException();
        }
        if (role != BattleLocalIpcProtocol.RuntimeRole
            || operation != BattleLocalIpcProtocol.IngestOperation)
        {
            throw new UnauthorizedLocalIpcRequestException();
        }
        return new(protocol, role, operation, credential);
    }

    private static async Task<byte[]> ReadFrameAsync(
        PipeStream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length <= 0)
        {
            throw new InvalidDataException("Local IPC frame length is invalid.");
        }
        if (length > maximumBytes)
        {
            throw new FrameTooLargeException();
        }
        var bytes = new byte[length];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await stream.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new InvalidDataException("Local IPC frame ended early.");
            }
            read += count;
        }
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        string status,
        int acceptedRecords,
        BattleIngestFailureCode failure,
        CancellationToken cancellationToken)
    {
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocolVersion = BattleLocalIpcProtocol.Version,
            status,
            acceptedRecords,
            failure = FailureWireValue(failure),
        });
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, response.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] DecodeCredential(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 43)
        {
            throw new ArgumentException("Battle local IPC credential is invalid.", nameof(value));
        }
        var normalized = value.Replace('-', '+').Replace('_', '/') + "=";
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(normalized);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Battle local IPC credential is invalid.", nameof(value), exception);
        }
        if (bytes.Length != 32 || ToBase64Url(bytes) != value)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("Battle local IPC credential is invalid.", nameof(value));
        }
        return bytes;
    }

    private static byte[] CopyCredential(ReadOnlySpan<byte> value)
    {
        if (value.Length != 32)
        {
            throw new ArgumentException("Battle local IPC credential must contain exactly 32 bytes.",
                nameof(value));
        }
        return value.ToArray();
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string RequireSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Runtime evidence SHA-256 is invalid.", nameof(value));
        }
        return value.ToLowerInvariant();
    }

    private static string FailureWireValue(BattleIngestFailureCode failure) =>
        failure switch
        {
            BattleIngestFailureCode.None => "none",
            BattleIngestFailureCode.Unauthorized => "unauthorized",
            BattleIngestFailureCode.InvalidRequest => "invalid-request",
            BattleIngestFailureCode.UnsupportedProtocol => "unsupported-protocol",
            BattleIngestFailureCode.PayloadTooLarge => "payload-too-large",
            BattleIngestFailureCode.RateLimited => "rate-limited",
            BattleIngestFailureCode.Busy => "busy",
            BattleIngestFailureCode.ChunkConflict => "chunk-conflict",
            BattleIngestFailureCode.BatchConflict => "batch-conflict",
            BattleIngestFailureCode.TimedOut => "timed-out",
            BattleIngestFailureCode.StorageRejected => "storage-rejected",
            BattleIngestFailureCode.PortUnavailable => "pipe-unavailable",
            BattleIngestFailureCode.StartFailed => "start-failed",
            BattleIngestFailureCode.ListenerFailed => "listener-failed",
            BattleIngestFailureCode.ShutdownTimedOut => "shutdown-timed-out",
            _ => throw new InvalidOperationException("The Battle local IPC failure code is unsupported."),
        };

    private static uint GetClientProcessId(SafePipeHandle handle)
    {
        if (!GetNamedPipeClientProcessId(handle, out var processId))
        {
            throw new IOException("The local IPC caller identity could not be established.",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
        return processId;
    }

    private void SetTransition(
        BattleLocalIpcState next,
        string transition,
        BattleIngestFailureCode failure)
    {
        lock (sync)
        {
            state = next;
            lastTransition = transition;
            lastFailure = failure;
        }
    }

    private static async Task ObserveExpectedStopAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
    }

    private sealed record LocalIpcHeader(
        string ProtocolVersion,
        string Role,
        string Operation,
        string Credential);

    private sealed class FrameTooLargeException : Exception
    {
    }

    private sealed class UnsupportedLocalIpcProtocolException : Exception
    {
    }

    private sealed class UnauthorizedLocalIpcRequestException : Exception
    {
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);
}
