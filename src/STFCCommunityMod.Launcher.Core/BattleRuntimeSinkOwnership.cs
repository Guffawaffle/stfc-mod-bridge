using System.Runtime.CompilerServices;

namespace STFCCommunityMod.Launcher.Core;

/// <summary>
/// Single-use owner for the exact Battle/Fleet sinks supplied to one runtime
/// provisioning handoff. The claim, not a parallel arbitrary lifetime, is the
/// only object allowed to dispose sinks after ownership transfers.
/// </summary>
internal sealed class BattleRuntimeSinkOwner : IAsyncDisposable
{
    private static readonly object RegistrationSync = new();
    private static readonly ConditionalWeakTable<IBattleIngestSink, SinkRegistration> Registrations = new();

    private enum OwnershipState
    {
        Available,
        Claimed,
        UnclaimedCleanupPending,
        ClaimedCleanupPending,
        Disposed,
    }

    private readonly object sync = new();
    private readonly SemaphoreSlim cleanupGate = new(1, 1);
    private IBattleIngestSink? battleSink;
    private IBattleIngestSink? fleetSink;
    private OwnershipState state;

    public BattleRuntimeSinkOwner(
        IBattleIngestSink? battleSink,
        IBattleIngestSink? fleetSink)
    {
        if (battleSink is null && fleetSink is null)
        {
            throw new ArgumentException("At least one runtime sink must be owned.", nameof(battleSink));
        }
        if (ReferenceEquals(battleSink, fleetSink)
            || battleSink is not null && !CanDispose(battleSink)
            || fleetSink is not null && !CanDispose(fleetSink))
        {
            throw new ArgumentException("Runtime sinks must be distinct and explicitly disposable.");
        }
        this.battleSink = battleSink;
        this.fleetSink = fleetSink;
        Register(battleSink, fleetSink);
    }

    public IBattleIngestSink? BattleSink
    {
        get
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(state == OwnershipState.Disposed, this);
                if (state != OwnershipState.Available)
                {
                    throw new InvalidOperationException("The runtime sink owner is no longer available.");
                }
                return battleSink;
            }
        }
    }

    public IBattleIngestSink? FleetSink
    {
        get
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(state == OwnershipState.Disposed, this);
                if (state != OwnershipState.Available)
                {
                    throw new InvalidOperationException("The runtime sink owner is no longer available.");
                }
                return fleetSink;
            }
        }
    }

    public BattleRuntimeSinkClaim Claim()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(state == OwnershipState.Disposed, this);
            if (state != OwnershipState.Available)
            {
                throw new InvalidOperationException("The runtime sink owner is no longer available.");
            }
            state = OwnershipState.Claimed;
            return new(this, battleSink, fleetSink);
        }
    }

    public ValueTask DisposeAsync() => CleanupAsync(claimedOwner: false);

    private async ValueTask CleanupAsync(bool claimedOwner)
    {
        await cleanupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                if (state == OwnershipState.Disposed)
                {
                    return;
                }
                var allowed = claimedOwner
                    ? state is OwnershipState.Claimed or OwnershipState.ClaimedCleanupPending
                    : state is OwnershipState.Available or OwnershipState.UnclaimedCleanupPending;
                if (!allowed)
                {
                    throw new InvalidOperationException(
                        "Only the current runtime sink owner can perform cleanup.");
                }
                state = claimedOwner
                    ? OwnershipState.ClaimedCleanupPending
                    : OwnershipState.UnclaimedCleanupPending;
            }

            if (fleetSink is not null)
            {
                await DisposeSinkAsync(fleetSink).ConfigureAwait(false);
                fleetSink = null;
            }
            if (battleSink is not null)
            {
                await DisposeSinkAsync(battleSink).ConfigureAwait(false);
                battleSink = null;
            }
            lock (sync)
            {
                state = OwnershipState.Disposed;
            }
        }
        finally
        {
            cleanupGate.Release();
        }
    }

    private static bool CanDispose(IBattleIngestSink sink) =>
        sink is IAsyncDisposable or IDisposable;

    private static ValueTask DisposeSinkAsync(IBattleIngestSink sink)
    {
        if (sink is IAsyncDisposable asyncDisposable)
        {
            return asyncDisposable.DisposeAsync();
        }
        ((IDisposable)sink).Dispose();
        return ValueTask.CompletedTask;
    }

    private void ReturnFailedClaimCleanupToOwner()
    {
        lock (sync)
        {
            if (state != OwnershipState.ClaimedCleanupPending)
            {
                throw new InvalidOperationException("The runtime sink claim is not awaiting cleanup.");
            }
            state = OwnershipState.UnclaimedCleanupPending;
        }
    }

    private static void Register(IBattleIngestSink? battleSink, IBattleIngestSink? fleetSink)
    {
        lock (RegistrationSync)
        {
            if (battleSink is not null && Registrations.TryGetValue(battleSink, out _)
                || fleetSink is not null && Registrations.TryGetValue(fleetSink, out _))
            {
                throw new InvalidOperationException("A runtime sink already has an exact owner.");
            }
            if (battleSink is not null)
            {
                Registrations.Add(battleSink, new());
            }
            try
            {
                if (fleetSink is not null)
                {
                    Registrations.Add(fleetSink, new());
                }
            }
            catch
            {
                if (battleSink is not null)
                {
                    Registrations.Remove(battleSink);
                }
                throw;
            }
        }
    }

    private sealed class SinkRegistration;

    internal sealed class BattleRuntimeSinkClaim : IAsyncDisposable
    {
        private readonly BattleRuntimeSinkOwner owner;

        internal BattleRuntimeSinkClaim(
            BattleRuntimeSinkOwner owner,
            IBattleIngestSink? battleSink,
            IBattleIngestSink? fleetSink)
        {
            this.owner = owner;
            BattleSink = battleSink;
            FleetSink = fleetSink;
        }

        public IBattleIngestSink? BattleSink { get; }

        public IBattleIngestSink? FleetSink { get; }

        public ValueTask DisposeAsync() => owner.CleanupAsync(claimedOwner: true);

        internal async ValueTask DisposeAfterFailedTransferAsync()
        {
            try
            {
                await owner.CleanupAsync(claimedOwner: true).ConfigureAwait(false);
            }
            catch
            {
                owner.ReturnFailedClaimCleanupToOwner();
                throw;
            }
        }
    }
}
