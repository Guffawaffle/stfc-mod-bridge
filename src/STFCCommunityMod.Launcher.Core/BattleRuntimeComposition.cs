using System.Collections.Frozen;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

internal enum BattleRuntimeCompositionState
{
    Dormant,
    Starting,
    Running,
    Stopping,
    Failed,
    Disposed,
}

internal sealed record BattleRuntimeCompositionHealth(
    BattleRuntimeCompositionState State,
    IReadOnlySet<string> AcceptedKinds,
    BattleIngestFailureCode LastFailure,
    string LastTransition);

internal interface IBattleRuntimeProvisioningFactory
{
    ValueTask<BattleRuntimeProvisioningLease> OpenAsync(
        LauncherBattleFeatureSnapshot features,
        CancellationToken cancellationToken);
}

/// <summary>
/// Exact already-provisioned resources supplied by the reviewed lifecycle
/// transaction. This lease does not discover paths, create credentials, open a
/// store, or infer feature eligibility.
/// </summary>
internal sealed class BattleRuntimeProvisioningLease : IAsyncDisposable
{
    private readonly byte[] credential;
    private readonly IAsyncDisposable lifetime;
    private readonly SemaphoreSlim disposeGate = new(1, 1);
    private bool disposed;

    public BattleRuntimeProvisioningLease(
        string pipeName,
        ReadOnlySpan<byte> credential,
        string runtimeEvidenceSha256,
        IBattleNamedPipeClientAuthorizer authorizer,
        IBattleIngestSink? battleSink,
        IBattleIngestSink? fleetSink,
        IAsyncDisposable lifetime)
    {
        var normalizedPipeName = BattleLocalIpcProtocol.RequirePipeName(pipeName, nameof(pipeName));
        if (credential.Length != 32)
        {
            throw new ArgumentException("The provisioned Battle credential must contain exactly 32 bytes.",
                nameof(credential));
        }
        var normalizedRuntimeEvidence = RequireSha256(runtimeEvidenceSha256);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(lifetime);

        PipeName = normalizedPipeName;
        RuntimeEvidenceSha256 = normalizedRuntimeEvidence;
        Authorizer = authorizer;
        BattleSink = battleSink;
        FleetSink = fleetSink;
        this.lifetime = lifetime;
        this.credential = credential.ToArray();
    }

    public string PipeName { get; }

    public string RuntimeEvidenceSha256 { get; }

    public IBattleNamedPipeClientAuthorizer Authorizer { get; }

    public IBattleIngestSink? BattleSink { get; }

    public IBattleIngestSink? FleetSink { get; }

    internal ReadOnlySpan<byte> Credential
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return credential;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }
            await lifetime.DisposeAsync().ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(credential);
            disposed = true;
        }
        finally
        {
            disposeGate.Release();
        }
    }

    private static string RequireSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The provisioned runtime-evidence SHA-256 is invalid.", nameof(value));
        }
        return value.ToLowerInvariant();
    }
}

/// <summary>
/// Serializes one process-owned Battle runtime composition. The coordinator is
/// deliberately not registered by launcher startup until the lifecycle owner
/// can supply an exact provisioned lease.
/// </summary>
internal sealed class BattleRuntimeCompositionCoordinator : IAsyncDisposable
{
    private static readonly IReadOnlySet<string> EmptyKinds =
        Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);

    private readonly object sync = new();
    private readonly IBattleRuntimeProvisioningFactory provisioningFactory;
    private readonly BattleIngestLimits limits;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private BattleRuntimeProvisioningLease? provisioning;
    private BattleNamedPipeIngestHost? host;
    private IReadOnlySet<string> acceptedKinds = EmptyKinds;
    private BattleRuntimeCompositionState state = BattleRuntimeCompositionState.Dormant;
    private BattleIngestFailureCode lastFailure;
    private string lastTransition = "dormant";
    private bool disposeRequested;

    public BattleRuntimeCompositionCoordinator(
        IBattleRuntimeProvisioningFactory provisioningFactory,
        BattleIngestLimits? limits = null,
        TimeProvider? timeProvider = null)
    {
        this.provisioningFactory =
            provisioningFactory ?? throw new ArgumentNullException(nameof(provisioningFactory));
        this.limits = limits ?? BattleIngestLimits.Default;
        this.limits.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public BattleRuntimeCompositionHealth GetHealth()
    {
        lock (sync)
        {
            return new(state, acceptedKinds, lastFailure, lastTransition);
        }
    }

    public async Task RecomposeAsync(
        LauncherBattleFeatureSnapshot features,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(features);
        var activation = BattleIngestActivation.Resolve(features);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            var nextKinds = activation.AcceptedKinds;
            if (SetEquals(acceptedKinds, nextKinds)
                && state is BattleRuntimeCompositionState.Dormant or BattleRuntimeCompositionState.Running)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await StopCurrentAsync("recomposing").ConfigureAwait(false);
            if (!activation.ShouldListen)
            {
                SetTransition(
                    BattleRuntimeCompositionState.Dormant,
                    EmptyKinds,
                    BattleIngestFailureCode.None,
                    "dormant");
                return;
            }

            SetTransition(
                BattleRuntimeCompositionState.Starting,
                EmptyKinds,
                BattleIngestFailureCode.None,
                "starting");
            BattleRuntimeProvisioningLease? opened = null;
            BattleNamedPipeIngestHost? startingHost = null;
            try
            {
                opened = await provisioningFactory.OpenAsync(features, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Battle lifecycle returned no provisioned runtime lease.");
                var sink = SelectSink(opened, activation);
                startingHost = new(
                    activation,
                    opened.PipeName,
                    opened.Credential,
                    sink,
                    opened.Authorizer,
                    opened.RuntimeEvidenceSha256,
                    limits,
                    timeProvider);
                var started = await startingHost.StartAsync(cancellationToken).ConfigureAwait(false);
                if (started.State != BattleLocalIpcState.Listening)
                {
                    throw new InvalidOperationException(
                        $"The provisioned Battle local IPC host did not start: {started.FailureCode}.");
                }

                provisioning = opened;
                host = startingHost;
                opened = null;
                startingHost = null;
                SetTransition(
                    BattleRuntimeCompositionState.Running,
                    nextKinds,
                    BattleIngestFailureCode.None,
                    "running");
            }
            catch (OperationCanceledException exception)
            {
                var cleanupFailure = await CleanupFailedStartAsync(
                    startingHost,
                    opened).ConfigureAwait(false);
                if (cleanupFailure is not null)
                {
                    SetTransition(
                        BattleRuntimeCompositionState.Failed,
                        host is null ? EmptyKinds : nextKinds,
                        BattleIngestFailureCode.ShutdownTimedOut,
                        "start-cleanup-failed");
                    throw new AggregateException(exception, cleanupFailure);
                }
                SetTransition(
                    BattleRuntimeCompositionState.Dormant,
                    EmptyKinds,
                    BattleIngestFailureCode.None,
                    "canceled");
                throw;
            }
            catch (Exception exception)
            {
                var cleanupFailure = await CleanupFailedStartAsync(
                    startingHost,
                    opened).ConfigureAwait(false);
                SetTransition(
                    BattleRuntimeCompositionState.Failed,
                    host is null ? EmptyKinds : nextKinds,
                    cleanupFailure is null
                        ? BattleIngestFailureCode.StartFailed
                        : BattleIngestFailureCode.ShutdownTimedOut,
                    cleanupFailure is null ? "start-failed" : "start-cleanup-failed");
                if (cleanupFailure is not null)
                {
                    throw new AggregateException(exception, cleanupFailure);
                }
                throw;
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
            if (state == BattleRuntimeCompositionState.Disposed)
            {
                return;
            }
            disposeRequested = true;
            await StopCurrentAsync("disposing").ConfigureAwait(false);
            SetTransition(
                BattleRuntimeCompositionState.Disposed,
                EmptyKinds,
                BattleIngestFailureCode.None,
                "disposed");
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task StopCurrentAsync(string transition)
    {
        var stoppingHost = host;
        var stoppingProvisioning = provisioning;
        if (stoppingHost is null && stoppingProvisioning is null)
        {
            return;
        }

        SetTransition(
            BattleRuntimeCompositionState.Stopping,
            acceptedKinds,
            BattleIngestFailureCode.None,
            transition);
        Exception? failure = null;
        try
        {
            if (stoppingHost is not null)
            {
                await stoppingHost.DisposeAsync().ConfigureAwait(false);
                host = null;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        if (failure is null)
        {
            try
            {
                if (stoppingProvisioning is not null)
                {
                    await stoppingProvisioning.DisposeAsync().ConfigureAwait(false);
                    provisioning = null;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }
        if (failure is not null)
        {
            var possiblyAcceptedKinds = host is null ? EmptyKinds : acceptedKinds;
            SetTransition(
                BattleRuntimeCompositionState.Failed,
                possiblyAcceptedKinds,
                BattleIngestFailureCode.ShutdownTimedOut,
                "stop-failed");
            throw failure;
        }
    }

    private async Task<Exception?> CleanupFailedStartAsync(
        BattleNamedPipeIngestHost? startingHost,
        BattleRuntimeProvisioningLease? opened)
    {
        Exception? failure = null;
        if (startingHost is not null)
        {
            try
            {
                await startingHost.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                host = startingHost;
                provisioning = opened;
                failure = exception;
            }
        }
        if (failure is null && opened is not null)
        {
            try
            {
                await opened.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                provisioning = opened;
                failure = exception;
            }
        }
        return failure;
    }

    private static IBattleIngestSink SelectSink(
        BattleRuntimeProvisioningLease opened,
        BattleIngestActivation activation)
    {
        var battle = activation.Accepts(BattleIngestProtocol.BattleEventsKind);
        var fleet = activation.Accepts(BattleIngestProtocol.FleetRuntimeKind);
        if (battle && opened.BattleSink is null)
        {
            throw new InvalidOperationException("Battle collection is enabled without its provisioned repository sink.");
        }
        if (fleet && opened.FleetSink is null)
        {
            throw new InvalidOperationException("Fleet collection is enabled without its provisioned runtime sink.");
        }
        return (battle, fleet) switch
        {
            (true, true) => new BattleIngestSinkRouter(opened.BattleSink!, opened.FleetSink!),
            (true, false) => opened.BattleSink!,
            (false, true) => opened.FleetSink!,
            _ => throw new InvalidOperationException("The provisioned Battle runtime has no accepted ingest family."),
        };
    }

    private static bool SetEquals(IReadOnlySet<string> left, IReadOnlySet<string> right) =>
        left.Count == right.Count && left.All(right.Contains);

    private void SetTransition(
        BattleRuntimeCompositionState next,
        IReadOnlySet<string> kinds,
        BattleIngestFailureCode failure,
        string transition)
    {
        lock (sync)
        {
            state = next;
            acceptedKinds = kinds.ToFrozenSet(StringComparer.Ordinal);
            lastFailure = failure;
            lastTransition = transition;
        }
    }
}
