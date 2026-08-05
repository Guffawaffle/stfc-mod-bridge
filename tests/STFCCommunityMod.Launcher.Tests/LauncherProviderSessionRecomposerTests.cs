using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class LauncherProviderSessionRecomposerTests
{
    [TestMethod]
    public void SuccessfulRecompositionReplacesAndDisposesThePreviousSession()
    {
        var catalog = BundledLauncherProviderCatalog.Load();
        var initial = Resolve(catalog, "guffawaffle", "stable");
        using var recomposer = new LauncherProviderSessionRecomposer<TestSession>(
            catalog,
            initial,
            resolution => new(resolution.Selection.ProviderId));
        var previous = recomposer.Current;

        var current = recomposer.Recompose(new("netniv", "stable"));

        Assert.AreEqual("netniv", current.ProviderId);
        Assert.AreSame(current, recomposer.Current);
        Assert.AreEqual("netniv", recomposer.CurrentResolution.Provider?.Id);
        Assert.IsTrue(previous.IsDisposed);
        Assert.IsFalse(recomposer.HasPendingRecomposition);
    }

    [TestMethod]
    public void FailedRecompositionKeepsThePreviousSessionAndCanRetryInProcess()
    {
        var catalog = BundledLauncherProviderCatalog.Load();
        var shouldFail = true;
        using var recomposer = new LauncherProviderSessionRecomposer<TestSession>(
            catalog,
            Resolve(catalog, "guffawaffle", "stable"),
            resolution => resolution.Selection.ProviderId == "netniv" && shouldFail
                ? throw new InvalidOperationException("synthetic composition failure")
                : new(resolution.Selection.ProviderId));
        var previous = recomposer.Current;

        Assert.ThrowsException<InvalidOperationException>(
            () => recomposer.Recompose(new("netniv", "stable")));

        Assert.AreSame(previous, recomposer.Current);
        Assert.IsFalse(previous.IsDisposed);
        Assert.IsTrue(recomposer.HasPendingRecomposition);

        shouldFail = false;
        var retried = recomposer.Retry();

        Assert.AreEqual("netniv", retried.ProviderId);
        Assert.IsTrue(previous.IsDisposed);
        Assert.IsFalse(recomposer.HasPendingRecomposition);
    }

    [TestMethod]
    public void ReverseSwitchIsAvailableImmediatelyAfterACompletedSwap()
    {
        var catalog = BundledLauncherProviderCatalog.Load();
        using var recomposer = new LauncherProviderSessionRecomposer<TestSession>(
            catalog,
            Resolve(catalog, "guffawaffle", "stable"),
            resolution => new(resolution.Selection.ProviderId));

        var netniv = recomposer.Recompose(new("netniv", "stable"));
        var guffawaffle = recomposer.Recompose(new("guffawaffle", "stable"));

        Assert.IsTrue(netniv.IsDisposed);
        Assert.AreEqual("guffawaffle", guffawaffle.ProviderId);
        Assert.AreSame(guffawaffle, recomposer.Current);
    }

    [TestMethod]
    public async Task ConcurrentRecompositionIsRejectedWithoutReplacingTheActiveSession()
    {
        var catalog = BundledLauncherProviderCatalog.Load();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var recomposer = new LauncherProviderSessionRecomposer<TestSession>(
            catalog,
            Resolve(catalog, "guffawaffle", "stable"),
            resolution =>
            {
                if (resolution.Selection.ProviderId == "netniv")
                {
                    started.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                }
                return new(resolution.Selection.ProviderId);
            });

        var first = Task.Run(() => recomposer.Recompose(new("netniv", "stable")));
        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var exception = Assert.ThrowsException<InvalidOperationException>(
                () => recomposer.Recompose(new("guffawaffle", "stable")));
            StringAssert.Contains(exception.Message, "already active");
        }
        finally
        {
            release.Set();
        }

        var current = await first;
        Assert.AreSame(current, recomposer.Current);
        Assert.AreEqual("netniv", current.ProviderId);
    }

    [TestMethod]
    public async Task DisposalDuringRecompositionDisposesBothSessions()
    {
        var catalog = BundledLauncherProviderCatalog.Load();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var created = new List<TestSession>();
        var recomposer = new LauncherProviderSessionRecomposer<TestSession>(
            catalog,
            Resolve(catalog, "guffawaffle", "stable"),
            resolution =>
            {
                if (resolution.Selection.ProviderId == "netniv")
                {
                    started.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                }
                var session = new TestSession(resolution.Selection.ProviderId);
                created.Add(session);
                return session;
            });

        var recomposition = Task.Run(() => recomposer.Recompose(new("netniv", "stable")));
        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
        recomposer.Dispose();
        release.Set();

        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await recomposition);
        Assert.AreEqual(2, created.Count);
        Assert.IsTrue(created.All(session => session.IsDisposed));
    }

    private static LauncherProviderSelectionResolution Resolve(
        LauncherDistributionProviderCatalog catalog,
        string providerId,
        string releaseChannelId) =>
        LauncherProviderSelectionResolver.Resolve(
            catalog,
            new LauncherProviderSelection(providerId, releaseChannelId));

    private sealed class TestSession(string providerId) : IDisposable
    {
        public string ProviderId { get; } = providerId;

        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
