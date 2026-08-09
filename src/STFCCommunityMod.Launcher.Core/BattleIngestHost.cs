using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace STFCCommunityMod.Launcher.Core;

/// <summary>
/// Owns the narrow authenticated Windows loopback transport only. The injected
/// sink must honor cancellation and complete or roll back its transaction before
/// returning. The host always joins that work before shutdown completes.
/// </summary>
public sealed class BattleIngestHost : IAsyncDisposable
{
    public const int DefaultPort = 43127;
    private const int MaximumDedupeEntries = 2048;
    private readonly BattleIngestActivation activation;
    private readonly BattleIngestLimits limits;
    private readonly IBattleIngestSink sink;
    private readonly byte[] expectedToken;
    private readonly int configuredPort;
    private readonly TimeProvider timeProvider;
    private readonly BattleIngestEnvelopeProcessor processor;
    private readonly SemaphoreSlim requests;
    private readonly SemaphoreSlim lifecycleOperations = new(1, 1);
    private readonly object lifecycleGate = new();
    private readonly object handlerGate = new();
    private readonly object rateGate = new();
    private readonly object queueGate = new();
    private readonly object dedupeGate = new();
    private readonly object healthGate = new();
    private readonly HashSet<Task> handlers = [];
    private readonly Dictionary<BatchIdentity, DedupeEntry> dedupe = [];
    private long dedupeSequence;
    private DateTimeOffset rateWindowStarted;
    private int rateWindowCount;
    private Channel<IngestWorkItem>? queue;
    private CancellationTokenSource? workerCancellation;
    private Task? worker;
    private HttpListener? listener;
    private Task? acceptLoop;
    private Task? stopTask;
    private HostLifecycle lifecycle = HostLifecycle.Created;
    private int queuedBatches;
    private long queuedBytes;
    private long acceptedBatches;
    private long duplicateBatches;
    private long rejectedRequests;
    private int boundPort;
    private BattleIngestFailureCode lastFailure;
    private BattleIngestListenerState listenerState = BattleIngestListenerState.Inactive;
    private string lastTransition = "inactive";

    internal Func<HttpListener, Task<HttpListenerContext>> AcceptContextAsync { private get; set; } =
        owner => owner.GetContextAsync();

    public BattleIngestHost(
        BattleIngestActivation activation,
        string capabilityToken,
        IBattleIngestSink sink,
        int port = DefaultPort,
        BattleIngestLimits? limits = null,
        TimeProvider? timeProvider = null)
    {
        this.activation = activation ?? throw new ArgumentNullException(nameof(activation));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.limits = limits ?? BattleIngestLimits.Default;
        this.limits.Validate();
        if (port is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        configuredPort = port;
        expectedToken = DecodeCredential(capabilityToken);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        processor = new(activation, this.limits, this.timeProvider);
        requests = new(this.limits.MaximumConcurrentRequests);
        rateWindowStarted = this.timeProvider.GetUtcNow();
    }

    public BattleIngestHealthSnapshot GetHealth()
    {
        var chunks = processor.PendingChunks;
        int batches;
        long bytes;
        lock (queueGate)
        {
            batches = queuedBatches;
            bytes = queuedBytes;
        }
        lock (healthGate)
        {
            return new(
                listenerState,
                boundPort,
                Interlocked.Read(ref acceptedBatches),
                Interlocked.Read(ref duplicateBatches),
                Interlocked.Read(ref rejectedRequests),
                chunks.Groups,
                chunks.Bytes,
                batches,
                bytes,
                lastFailure,
                lastTransition);
        }
    }

    public async Task<BattleIngestStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (lifecycleGate)
        {
            if (lifecycle == HostLifecycle.Disposed)
            {
                throw new InvalidOperationException("The Battle ingest host is one-shot and cannot be restarted.");
            }
        }
        await lifecycleOperations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleOperations.Release();
        }
    }

    private async Task<BattleIngestStartResult> StartCoreAsync()
    {
        lock (lifecycleGate)
        {
            if (lifecycle != HostLifecycle.Created)
            {
                throw new InvalidOperationException("The Battle ingest host is one-shot and cannot be restarted.");
            }
            lifecycle = HostLifecycle.Starting;
        }

        if (!activation.ShouldListen)
        {
            lock (lifecycleGate)
            {
                lifecycle = HostLifecycle.Stopped;
            }
            SetTransition(BattleIngestListenerState.Inactive, "inactive", BattleIngestFailureCode.None);
            return new BattleIngestStartResult(
                BattleIngestStartStatus.Inactive,
                0,
                BattleIngestFailureCode.None);
        }

        SetTransition(BattleIngestListenerState.Starting, "starting", BattleIngestFailureCode.None);
        CreateWorker();
        var candidate = new HttpListener();
        candidate.Prefixes.Add($"http://127.0.0.1:{configuredPort}/");
        try
        {
            candidate.Start();
        }
        catch (HttpListenerException exception)
        {
            candidate.Close();
            await StopFailedStartWorkerAsync().ConfigureAwait(false);
            var collision = IsAddressCollision(exception);
            lock (lifecycleGate)
            {
                lifecycle = HostLifecycle.Failed;
            }
            SetTransition(
                collision ? BattleIngestListenerState.PortUnavailable : BattleIngestListenerState.Failed,
                collision ? "port-unavailable" : "start-failed",
                collision ? BattleIngestFailureCode.PortUnavailable : BattleIngestFailureCode.StartFailed);
            return new BattleIngestStartResult(
                collision ? BattleIngestStartStatus.PortUnavailable : BattleIngestStartStatus.Failed,
                0,
                collision ? BattleIngestFailureCode.PortUnavailable : BattleIngestFailureCode.StartFailed);
        }
        catch
        {
            candidate.Close();
            await StopFailedStartWorkerAsync().ConfigureAwait(false);
            lock (lifecycleGate)
            {
                lifecycle = HostLifecycle.Failed;
            }
            SetTransition(
                BattleIngestListenerState.Failed,
                "start-failed",
                BattleIngestFailureCode.StartFailed);
            return new BattleIngestStartResult(
                BattleIngestStartStatus.Failed,
                0,
                BattleIngestFailureCode.StartFailed);
        }

        listener = candidate;
        boundPort = configuredPort;
        acceptLoop = Task.Run(() => RunAcceptLoopAsync(candidate), CancellationToken.None);
        lock (lifecycleGate)
        {
            lifecycle = HostLifecycle.Started;
        }
        SetTransition(BattleIngestListenerState.Listening, "listening", BattleIngestFailureCode.None);
        return new BattleIngestStartResult(
            BattleIngestStartStatus.Started,
            boundPort,
            BattleIngestFailureCode.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task shared;
        await lifecycleOperations.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (lifecycleGate)
            {
                if (lifecycle == HostLifecycle.Disposed)
                {
                    return;
                }
                if (stopTask is null)
                {
                    stopTask = StopCoreAsync(cancellationToken);
                }
                shared = stopTask;
            }
        }
        finally
        {
            lifecycleOperations.Release();
        }
        await shared.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (lifecycleGate)
            {
                lifecycle = HostLifecycle.Disposed;
            }
            workerCancellation?.Dispose();
            requests.Dispose();
            CryptographicOperations.ZeroMemory(expectedToken);
        }
    }

    private async Task RunAcceptLoopAsync(HttpListener owner)
    {
        try
        {
            while (owner.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await AcceptContextAsync(owner).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    (exception is HttpListenerException or ObjectDisposedException)
                    && IsStoppingOrStopped())
                {
                    break;
                }
                if (!TryConsumeRate())
                {
                    context.Response.Headers["Retry-After"] = "1";
                    await RejectAndCloseAsync(
                            context.Response,
                            429,
                            BattleIngestFailureCode.RateLimited,
                            "rate-limited")
                        .ConfigureAwait(false);
                    continue;
                }
                if (!requests.Wait(0))
                {
                    await RejectAndCloseAsync(
                            context.Response,
                            503,
                            BattleIngestFailureCode.Busy,
                            "busy")
                        .ConfigureAwait(false);
                    continue;
                }
                var handler = HandleContextAsync(context);
                lock (handlerGate)
                {
                    handlers.Add(handler);
                }
                _ = handler.ContinueWith(
                    completed =>
                    {
                        _ = completed.Exception;
                        lock (handlerGate)
                        {
                            handlers.Remove(completed);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch
        {
            SetTransition(
                BattleIngestListenerState.Failed,
                "listener-failed",
                BattleIngestFailureCode.ListenerFailed);
            try
            {
                owner.Stop();
            }
            catch (Exception exception) when (
                exception is HttpListenerException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            if (request.Url is null
                || request.Url.AbsolutePath != BattleIngestProtocol.Route
                || !string.IsNullOrEmpty(request.Url.Query))
            {
                await WriteResponseAsync(context.Response, 404, false, "not-found").ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod != "POST")
            {
                await WriteResponseAsync(context.Response, 405, false, "method-not-allowed").ConfigureAwait(false);
                return;
            }
            using var deadline = new CancellationTokenSource(limits.RequestTimeout);
            if (GetHealth().ListenerState != BattleIngestListenerState.Listening)
            {
                await RejectAsync(context.Response, 503, BattleIngestFailureCode.Busy, "not-listening")
                    .ConfigureAwait(false);
                return;
            }
            if (request.RemoteEndPoint is null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address))
            {
                await RejectAsync(context.Response, 403, BattleIngestFailureCode.Unauthorized, "forbidden")
                    .ConfigureAwait(false);
                return;
            }
            if (!IsAuthorized(request))
            {
                await RejectAsync(context.Response, 401, BattleIngestFailureCode.Unauthorized, "unauthorized")
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                var body = await ReadBodyAsync(request, deadline.Token).ConfigureAwait(false);
                var parsed = processor.Parse(body);
                if (parsed.Status == BattleIngestParseStatus.Rejected)
                {
                    await RejectParseAsync(context.Response, parsed.FailureCode).ConfigureAwait(false);
                    return;
                }
                if (parsed.Status == BattleIngestParseStatus.ChunkPending)
                {
                    await WriteResponseAsync(
                            context.Response,
                            202,
                            true,
                            "chunk-pending",
                            parsed.ReceivedChunks,
                            parsed.ChunkCount)
                        .ConfigureAwait(false);
                    return;
                }
                await CommitAsync(
                        context.Response,
                        parsed.Envelope!,
                        parsed.ProcessingLease,
                        deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (BattleIngestBodyTooLargeException)
            {
                await SafeRejectAsync(
                    context.Response, 413, BattleIngestFailureCode.PayloadTooLarge, "payload-too-large");
            }
            catch (InvalidDataException)
            {
                await SafeRejectAsync(context.Response, 400, BattleIngestFailureCode.InvalidRequest, "invalid-request");
            }
            catch (OperationCanceledException)
            {
                await SafeRejectAsync(context.Response, 408, BattleIngestFailureCode.TimedOut, "timed-out");
            }
        }
        catch (Exception exception) when (
            exception is HttpListenerException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            requests.Release();
            try
            {
                context.Response.Close();
            }
            catch (Exception exception) when (
                exception is HttpListenerException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    private bool IsStoppingOrStopped()
    {
        lock (lifecycleGate)
        {
            return lifecycle is HostLifecycle.Stopping or HostLifecycle.Stopped or HostLifecycle.Disposed;
        }
    }

    private async Task CommitAsync(
        HttpListenerResponse response,
        BattleIngestEnvelope envelope,
        IDisposable? processingLease,
        CancellationToken cancellationToken)
    {
        var locallyOwnedLease = processingLease;
        try
        {
            var hash = SHA256.HashData(envelope.ExactEnvelopeBytes.Span);
            var identity = new BatchIdentity(envelope.Source, envelope.SessionId, envelope.BatchId);
            IngestWorkItem? work = null;
            Task<WorkResult> completion;
            lock (dedupeGate)
            {
                PruneDedupeLocked();
                if (dedupe.TryGetValue(identity, out var existing))
                {
                    if (!CryptographicOperations.FixedTimeEquals(hash, existing.Hash))
                    {
                        completion = Task.FromResult(
                            new WorkResult(false, false, 0, BattleIngestFailureCode.BatchConflict));
                    }
                    else if (existing.Committed)
                    {
                        completion = Task.FromResult(new WorkResult(true, true, 0, BattleIngestFailureCode.None));
                    }
                    else
                    {
                        completion = existing.Completion.Task;
                    }
                }
                else
                {
                    var source = new TaskCompletionSource<WorkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var entry = new DedupeEntry(hash, source, false, ++dedupeSequence);
                    dedupe.Add(identity, entry);
                    work = new(identity, envelope, source, locallyOwnedLease, cancellationToken);
                    locallyOwnedLease = null;
                    completion = source.Task;
                }
            }

            if (work is not null && !TryQueue(work))
            {
                lock (dedupeGate)
                {
                    if (dedupe.TryGetValue(identity, out var entry)
                        && ReferenceEquals(entry.Completion, work.Completion))
                    {
                        dedupe.Remove(identity);
                    }
                }
                work.ProcessingLease?.Dispose();
                work.Completion.TrySetResult(new(false, false, 0, BattleIngestFailureCode.Busy));
            }

            var result = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                var (status, publicCode) = result.FailureCode switch
                {
                    BattleIngestFailureCode.BatchConflict => (409, "batch-conflict"),
                    BattleIngestFailureCode.TimedOut => (408, "timed-out"),
                    BattleIngestFailureCode.StorageRejected => (503, "storage-rejected"),
                    _ => (503, "busy"),
                };
                await RejectAsync(
                        response,
                        status,
                        result.FailureCode,
                        publicCode)
                    .ConfigureAwait(false);
                return;
            }
            if (result.Duplicate)
            {
                Interlocked.Increment(ref duplicateBatches);
            }
            await WriteResponseAsync(
                    response,
                    202,
                    true,
                    result.Duplicate ? "duplicate" : "committed",
                    result.AcceptedRecords)
                .ConfigureAwait(false);
        }
        finally
        {
            locallyOwnedLease?.Dispose();
        }
    }

    private void CreateWorker()
    {
        queue = Channel.CreateBounded<IngestWorkItem>(new BoundedChannelOptions(limits.MaximumQueuedBatches)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        workerCancellation = new();
        worker = Task.Run(
            () => RunWorkerAsync(queue.Reader, workerCancellation.Token),
            CancellationToken.None);
    }

    private bool TryQueue(IngestWorkItem work)
    {
        var bytes = work.Envelope.ExactEnvelopeBytes.Length;
        lock (queueGate)
        {
            if (queuedBatches >= limits.MaximumQueuedBatches
                || queuedBytes + bytes > limits.MaximumQueuedBytes)
            {
                return false;
            }
            ++queuedBatches;
            queuedBytes += bytes;
        }
        if (queue!.Writer.TryWrite(work))
        {
            return true;
        }
        ReleaseQueued(bytes);
        return false;
    }

    private async Task RunWorkerAsync(ChannelReader<IngestWorkItem> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var work in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                WorkResult result;
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        work.Deadline,
                        cancellationToken);
                    var committed = await sink.CommitAsync(work.Envelope, linked.Token).ConfigureAwait(false);
                    result = new(true, false, committed.AcceptedRecords, BattleIngestFailureCode.None);
                    Interlocked.Increment(ref acceptedBatches);
                    lock (dedupeGate)
                    {
                        if (dedupe.TryGetValue(work.Identity, out var entry))
                        {
                            dedupe[work.Identity] = entry with { Committed = true };
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    result = new(false, false, 0, BattleIngestFailureCode.TimedOut);
                    RemoveFailedDedupe(work.Identity);
                }
                catch
                {
                    result = new(false, false, 0, BattleIngestFailureCode.StorageRejected);
                    RemoveFailedDedupe(work.Identity);
                }
                finally
                {
                    work.ProcessingLease?.Dispose();
                    ReleaseQueued(work.Envelope.ExactEnvelopeBytes.Length);
                }
                work.Completion.TrySetResult(result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            while (reader.TryRead(out var abandoned))
            {
                RemoveFailedDedupe(abandoned.Identity);
                abandoned.ProcessingLease?.Dispose();
                ReleaseQueued(abandoned.Envelope.ExactEnvelopeBytes.Length);
                abandoned.Completion.TrySetResult(
                    new(false, false, 0, BattleIngestFailureCode.TimedOut));
            }
        }
    }

    private async Task StopCoreAsync(CancellationToken callerCancellation)
    {
        HttpListener? owner;
        lock (lifecycleGate)
        {
            if (lifecycle is HostLifecycle.Created or HostLifecycle.Failed or HostLifecycle.Stopped)
            {
                lifecycle = HostLifecycle.Stopped;
                SetTransition(BattleIngestListenerState.Stopped, "stopped", BattleIngestFailureCode.None);
                return;
            }
            lifecycle = HostLifecycle.Stopping;
            owner = listener;
        }
        SetTransition(BattleIngestListenerState.Stopping, "stopping", BattleIngestFailureCode.None);
        processor.ClearPendingChunks();
        try
        {
            owner!.Stop();
        }
        catch (Exception exception) when (
            exception is HttpListenerException or ObjectDisposedException)
        {
        }
        if (acceptLoop is not null)
        {
            await acceptLoop.ConfigureAwait(false);
        }
        Task[] activeHandlers;
        lock (handlerGate)
        {
            activeHandlers = handlers.ToArray();
        }
        var handlersComplete = Task.WhenAll(activeHandlers);
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        drain.CancelAfter(limits.ShutdownDrainTimeout);
        var graceful = true;
        try
        {
            await handlersComplete.WaitAsync(drain.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            graceful = false;
            SetTransition(
                BattleIngestListenerState.Stopping,
                "shutdown-cancellation-requested",
                callerCancellation.IsCancellationRequested
                    ? BattleIngestFailureCode.TimedOut
                    : BattleIngestFailureCode.ShutdownTimedOut);
            workerCancellation!.Cancel();
        }
        finally
        {
            queue!.Writer.TryComplete();
        }

        await worker!.ConfigureAwait(false);
        await handlersComplete.ConfigureAwait(false);
        processor.ClearPendingChunks();
        owner!.Close();
        listener = null;
        boundPort = 0;
        lock (lifecycleGate)
        {
            lifecycle = HostLifecycle.Stopped;
        }
        SetTransition(
            graceful ? BattleIngestListenerState.Stopped : BattleIngestListenerState.Failed,
            graceful ? "stopped" : "shutdown-drain-exceeded",
            graceful ? BattleIngestFailureCode.None : BattleIngestFailureCode.ShutdownTimedOut);
    }

    private async Task StopFailedStartWorkerAsync()
    {
        queue!.Writer.TryComplete();
        workerCancellation!.Cancel();
        await worker!.ConfigureAwait(false);
        queue = null;
        worker = null;
        workerCancellation.Dispose();
        workerCancellation = null;
    }

    private void RemoveFailedDedupe(BatchIdentity identity)
    {
        lock (dedupeGate)
        {
            dedupe.Remove(identity);
        }
    }

    private void ReleaseQueued(int bytes)
    {
        lock (queueGate)
        {
            --queuedBatches;
            queuedBytes -= bytes;
        }
    }

    private void PruneDedupeLocked()
    {
        while (dedupe.Count >= MaximumDedupeEntries)
        {
            var oldest = dedupe
                .Where(pair => pair.Value.Committed)
                .OrderBy(pair => pair.Value.Sequence)
                .FirstOrDefault();
            if (oldest.Key is null)
            {
                break;
            }
            dedupe.Remove(oldest.Key);
        }
    }

    private bool TryConsumeRate()
    {
        lock (rateGate)
        {
            var now = timeProvider.GetUtcNow();
            if (now - rateWindowStarted >= limits.RateWindow)
            {
                rateWindowStarted = now;
                rateWindowCount = 0;
            }
            if (rateWindowCount >= limits.RequestsPerWindow)
            {
                return false;
            }
            ++rateWindowCount;
            return true;
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var sync = request.Headers.GetValues(BattleIngestProtocol.CompatibilityTokenHeader);
        var authorization = request.Headers.GetValues("Authorization");
        string? presented = null;
        if (sync?.Length == 1 && authorization is null)
        {
            presented = sync[0];
        }
        else if (authorization?.Length == 1
            && sync is null
            && authorization[0].StartsWith("Bearer ", StringComparison.Ordinal))
        {
            presented = authorization[0][7..];
        }
        if (presented is null || presented.Length != expectedToken.Length)
        {
            return false;
        }
        Span<byte> bytes = stackalloc byte[43];
        var count = Encoding.UTF8.GetByteCount(presented);
        return count == expectedToken.Length
            && Encoding.UTF8.GetBytes(presented, bytes) == expectedToken.Length
            && CryptographicOperations.FixedTimeEquals(bytes[..count], expectedToken);
    }

    private async Task<byte[]> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > limits.MaximumRequestBytes)
        {
            throw new BattleIngestBodyTooLargeException();
        }
        if (request.ContentType is null
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !string.Equals(mediaType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The request content type is unsupported.");
        }
        using var stream = new MemoryStream(
            request.ContentLength64 is > 0 and <= int.MaxValue ? (int)request.ContentLength64 : 0);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await request.InputStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (stream.Length + read > limits.MaximumRequestBytes)
            {
                throw new BattleIngestBodyTooLargeException();
            }
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (stream.Length == 0)
        {
            throw new InvalidDataException("The request body is empty.");
        }
        return stream.ToArray();
    }

    private Task RejectParseAsync(HttpListenerResponse response, BattleIngestFailureCode code)
    {
        var status = code switch
        {
            BattleIngestFailureCode.PayloadTooLarge => 413,
            BattleIngestFailureCode.ChunkConflict => 409,
            BattleIngestFailureCode.Busy => 503,
            BattleIngestFailureCode.UnsupportedProtocol => 422,
            _ => 400,
        };
        return RejectAsync(response, status, code, FailureName(code));
    }

    private async Task SafeRejectAsync(
        HttpListenerResponse response,
        int status,
        BattleIngestFailureCode code,
        string publicCode)
    {
        try
        {
            await RejectAsync(response, status, code, publicCode).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpListenerException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task RejectAndCloseAsync(
        HttpListenerResponse response,
        int status,
        BattleIngestFailureCode code,
        string publicCode)
    {
        try
        {
            await RejectAsync(response, status, code, publicCode).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpListenerException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception exception) when (
                exception is HttpListenerException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    private Task RejectAsync(
        HttpListenerResponse response,
        int status,
        BattleIngestFailureCode code,
        string publicCode)
    {
        Interlocked.Increment(ref rejectedRequests);
        lock (healthGate)
        {
            lastFailure = code;
        }
        return WriteResponseAsync(response, status, false, publicCode);
    }

    private static async Task WriteResponseAsync(
        HttpListenerResponse response,
        int status,
        bool ok,
        string code,
        int count = 0,
        int total = 0)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        await JsonSerializer.SerializeAsync(
            response.OutputStream,
            new BattleIngestResponse(ok, BattleIngestProtocol.Version, code, count, total))
            .ConfigureAwait(false);
    }

    private void SetTransition(
        BattleIngestListenerState state,
        string transition,
        BattleIngestFailureCode failure)
    {
        lock (healthGate)
        {
            listenerState = state;
            lastTransition = transition;
            lastFailure = failure;
        }
    }

    private static bool IsAddressCollision(HttpListenerException exception) =>
        exception.ErrorCode is 32 or 183 or 10048;

    private static byte[] DecodeCredential(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 43
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "The ingest credential must be an unpadded base64url encoding of 32 bytes.",
                nameof(value));
        }
        var decoded = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
        if (decoded.Length != 32)
        {
            throw new ArgumentException("The ingest credential must contain 32 bytes.", nameof(value));
        }
        CryptographicOperations.ZeroMemory(decoded);
        return Encoding.UTF8.GetBytes(value);
    }

    private static string FailureName(BattleIngestFailureCode code) =>
        code switch
        {
            BattleIngestFailureCode.InvalidRequest => "invalid-request",
            BattleIngestFailureCode.UnsupportedProtocol => "unsupported-protocol",
            BattleIngestFailureCode.PayloadTooLarge => "payload-too-large",
            BattleIngestFailureCode.Busy => "busy",
            BattleIngestFailureCode.ChunkConflict => "chunk-conflict",
            _ => "rejected",
        };

    private sealed record BattleIngestResponse(
        bool Ok,
        string ProtocolVersion,
        string Code,
        int Count,
        int Total);

    private sealed record BatchIdentity(string Source, string SessionId, string BatchId);

    private sealed record WorkResult(
        bool Succeeded,
        bool Duplicate,
        int AcceptedRecords,
        BattleIngestFailureCode FailureCode);

    private sealed record DedupeEntry(
        byte[] Hash,
        TaskCompletionSource<WorkResult> Completion,
        bool Committed,
        long Sequence);

    private sealed record IngestWorkItem(
        BatchIdentity Identity,
        BattleIngestEnvelope Envelope,
        TaskCompletionSource<WorkResult> Completion,
        IDisposable? ProcessingLease,
        CancellationToken Deadline);

    private enum HostLifecycle
    {
        Created,
        Starting,
        Started,
        Stopping,
        Stopped,
        Failed,
        Disposed,
    }

    private sealed class BattleIngestBodyTooLargeException : Exception;
}
