using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleRuntimeSinkOwnershipTests
{
    [TestMethod]
    public void ConstructionRequiresDistinctDisposableSinks()
    {
        Assert.ThrowsException<ArgumentException>(() => new BattleRuntimeSinkOwner(null, null));
        Assert.ThrowsException<ArgumentException>(() => new BattleRuntimeSinkOwner(new NonDisposableSink(), null));
        var shared = new RecordingAsyncSink("shared", []);
        Assert.ThrowsException<ArgumentException>(() => new BattleRuntimeSinkOwner(shared, shared));
    }

    [TestMethod]
    public async Task ExactSinkCannotBeRegisteredToTwoOwners()
    {
        var sink = new RecordingAsyncSink("battle", []);
        await using var owner = new BattleRuntimeSinkOwner(sink, null);

        Assert.ThrowsException<InvalidOperationException>(() => new BattleRuntimeSinkOwner(sink, null));

        await owner.DisposeAsync();
        Assert.ThrowsException<InvalidOperationException>(() => new BattleRuntimeSinkOwner(sink, null));
        Assert.AreEqual(1, sink.DisposeCount);
    }

    [TestMethod]
    public async Task ClaimedOwnerDisposesExactSinksInReverseOrderOnce()
    {
        var order = new List<string>();
        var battle = new RecordingSyncSink("battle", order);
        var fleet = new RecordingAsyncSink("fleet", order);
        await using var owner = new BattleRuntimeSinkOwner(battle, fleet);
        var claim = owner.Claim();

        Assert.AreSame(battle, claim.BattleSink);
        Assert.AreSame(fleet, claim.FleetSink);
        Assert.ThrowsException<InvalidOperationException>(() => owner.Claim());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await owner.DisposeAsync());

        await claim.DisposeAsync();
        await claim.DisposeAsync();
        Assert.AreEqual("fleet,battle", string.Join(',', order));
        Assert.AreEqual(1, battle.DisposeCount);
        Assert.AreEqual(1, fleet.DisposeCount);
    }

    [TestMethod]
    public async Task CleanupFailureRetainsClaimForExactRetry()
    {
        var order = new List<string>();
        var battle = new RecordingAsyncSink("battle", order);
        var fleet = new RecordingAsyncSink("fleet", order, failuresRemaining: 1);
        await using var owner = new BattleRuntimeSinkOwner(battle, fleet);
        var claim = owner.Claim();

        await Assert.ThrowsExceptionAsync<IOException>(async () => await claim.DisposeAsync());
        Assert.AreEqual(1, fleet.DisposeCount);
        Assert.AreEqual(0, battle.DisposeCount);
        Assert.ThrowsException<InvalidOperationException>(() => owner.Claim());
        Assert.ThrowsException<InvalidOperationException>(() => new BattleRuntimeSinkOwner(fleet, null));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await owner.DisposeAsync());

        await claim.DisposeAsync();
        Assert.AreEqual("fleet,fleet,battle", string.Join(',', order));
        Assert.AreEqual(2, fleet.DisposeCount);
        Assert.AreEqual(1, battle.DisposeCount);
        Assert.ThrowsException<InvalidOperationException>(() => new BattleRuntimeSinkOwner(fleet, null));
    }

    [TestMethod]
    public async Task ConcurrentClaimHasOneWinnerAndOneExactCleanupOwner()
    {
        var sink = new RecordingAsyncSink("battle", []);
        await using var owner = new BattleRuntimeSinkOwner(sink, null);
        var attempts = await Task.WhenAll(
            Task.Run(() => Claim(owner)),
            Task.Run(() => Claim(owner)));

        var winner = attempts.Single(result => result.Claim is not null).Claim!;
        Assert.AreEqual(1, attempts.Count(result => result.Failure is InvalidOperationException));
        await winner.DisposeAsync();
        Assert.AreEqual(1, sink.DisposeCount);
    }

    private static ClaimResult Claim(BattleRuntimeSinkOwner owner)
    {
        try
        {
            return new(owner.Claim(), null);
        }
        catch (Exception exception)
        {
            return new(null, exception);
        }
    }

    private sealed record ClaimResult(
        BattleRuntimeSinkOwner.BattleRuntimeSinkClaim? Claim,
        Exception? Failure);

    private sealed class NonDisposableSink : IBattleIngestSink
    {
        public ValueTask<BattleIngestCommitResult> CommitAsync(
            BattleIngestEnvelope envelope,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BattleIngestCommitResult(0));
    }

    private sealed class RecordingAsyncSink(
        string name,
        List<string> order,
        int failuresRemaining = 0) : IBattleIngestSink, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask<BattleIngestCommitResult> CommitAsync(
            BattleIngestEnvelope envelope,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BattleIngestCommitResult(0));

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            order.Add(name);
            return failuresRemaining-- > 0
                ? ValueTask.FromException(new IOException("fixture cleanup failure"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSyncSink(
        string name,
        List<string> order) : IBattleIngestSink, IDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask<BattleIngestCommitResult> CommitAsync(
            BattleIngestEnvelope envelope,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BattleIngestCommitResult(0));

        public void Dispose()
        {
            DisposeCount++;
            order.Add(name);
        }
    }
}
