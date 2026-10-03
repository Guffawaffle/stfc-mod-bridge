namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class GameInstallationLaunchCustodyTests
{
    [TestMethod]
    public async Task OrdinaryGameCustodyIsRetainedUntilThatChildExits()
    {
        var child = new TrackedChild();
        var lease = new TrackedLease();
        GameInstallationLaunchCustody.Retain(child, lease);
        Assert.IsFalse(lease.Released.Task.IsCompleted);
        Assert.AreEqual(0, child.DisposeCount);
        child.Exit.TrySetResult();
        await lease.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, child.DisposeCount);
        Assert.AreEqual(1, lease.DisposeCount);
    }

    [TestMethod]
    public async Task FailedExitObservationDoesNotReleaseInstallationAccessEarly()
    {
        var child = new TrackedChild();
        var lease = new TrackedLease();
        GameInstallationLaunchCustody.Retain(child, lease);
        child.Exit.TrySetException(new InvalidOperationException("Exit observation failed"));
        await child.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, lease.DisposeCount);
        Assert.AreEqual(0, child.DisposeCount);
    }

    private sealed class TrackedChild : IGameExecutableProcess
    {
        public TaskCompletionSource Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }
        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            try { await Exit.Task.WaitAsync(cancellationToken); }
            finally { Observed.TrySetResult(); }
        }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
    private sealed class TrackedLease : IDisposable
    {
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }
        public void Dispose() { DisposeCount++; Released.TrySetResult(); }
    }
}
