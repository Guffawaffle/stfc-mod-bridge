using System.Diagnostics;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class GameInstallationCoordinatorTests
{
    [TestMethod]
    public async Task CheckAndUpdateBindTheExactDisplayedInstallationAndCheckedVersion()
    {
        using var directory = new TemporaryDirectory();
        var game = Path.Combine(directory.Path, "chosen-game");
        var transport = new RecordingTransport(request => new(true,
            Installation: new(request.GameDirectory!, 267, "ready", "idle", AvailableVersion: 267)));
        var coordinator = new GameInstallationCoordinator(directory.Path, transport);
        await coordinator.CheckAsync(game);
        await coordinator.UpdateAsync(game, 267);
        Assert.AreEqual("check-game-update", transport.Requests[0].Operation);
        Assert.AreEqual("update-game", transport.Requests[1].Operation);
        Assert.AreEqual(game, transport.Requests[1].GameDirectory);
        Assert.AreEqual(267, transport.Requests[1].ExpectedVersion);
        Assert.IsNull(transport.Requests[1].Id);
    }

    [TestMethod]
    public async Task BridgeOperationContentionRefusesBeforeCallingNativeMutation()
    {
        using var directory = new TemporaryDirectory();
        var transport = new RecordingTransport(_ => throw new AssertFailedException("Native mutation was called."));
        await using var held = await new LauncherOperationLock(directory.Path).TryAcquireAsync();
        var result = await new GameInstallationCoordinator(directory.Path, transport).UpdateAsync(directory.Path, 267);
        Assert.IsFalse(result.Ok);
        Assert.AreEqual("bridge-operation-busy", result.Error!.Code);
        Assert.AreEqual(0, transport.Requests.Count);
    }

    [TestMethod]
    public async Task StartedWorkerRetainsOperationLeaseAcrossCallerCancellationAndReportsNativeProgress()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var progressObserved = new ManualResetEventSlim();
        var transport = new RecordingTransport(request =>
        {
            if (request.Operation == "update-game")
            {
                started.Set();
                if (!finish.Wait(TimeSpan.FromSeconds(10))) throw new AssertFailedException("Test worker was not released.");
                return new(true, Installation: new(request.GameDirectory!, 267, "ready", "committed"));
            }
            return new(true, Installation: new(request.GameDirectory!, 221, "updating", "downloading",
                DownloadedBytes: 123, DownloadBytes: 1000));
        });
        var progress = new InlineProgress(observed =>
        {
            if (observed.DownloadedBytes == 123) progressObserved.Set();
        });
        var update = new GameInstallationCoordinator(directory.Path, transport)
            .UpdateAsync(directory.Path, 267, progress, cancellation.Token);
        try
        {
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
            await using var conflicting = await new LauncherOperationLock(directory.Path).TryAcquireAsync();
            Assert.IsNull(conflicting);
            Assert.IsTrue(progressObserved.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(update.IsCompleted);
        }
        finally { finish.Set(); }
        Assert.IsTrue((await update).Ok);
        await using var after = await new LauncherOperationLock(directory.Path).TryAcquireAsync();
        Assert.IsNotNull(after);
    }

    [TestMethod]
    public async Task MismatchedInstallationEvidenceIsRejectedAndRecoveryDoesNotRequirePrimeToExist()
    {
        using var directory = new TemporaryDirectory();
        var requested = Path.Combine(directory.Path, "interrupted-game");
        var transport = new RecordingTransport(request => new(true,
            Installation: new(request.Operation == "recover-game-update" ? request.GameDirectory! : directory.Path,
                221, "ready", "idle")));
        var coordinator = new GameInstallationCoordinator(directory.Path, transport);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => coordinator.CheckAsync(requested));
        Assert.IsTrue((await coordinator.RecoverAsync(requested)).Ok);
        Assert.AreEqual(requested, transport.Requests.Last().GameDirectory);
    }

    [TestMethod]
    public async Task NativeCanonicalEvidenceAcceptsAnAncestorJunctionForStatusCheckUpdateAndRecovery()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows physical directory identity qualification.");
        using var directory = new TemporaryDirectory();
        var parent = Path.Combine(directory.Path, "physical");
        var game = Path.Combine(parent, "game");
        Directory.CreateDirectory(game);
        var alias = Path.Combine(directory.Path, "alias");
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", alias, parent }) start.ArgumentList.Add(argument);
        using var junction = Process.Start(start) ?? throw new AssertFailedException("Cannot create the junction fixture.");
        await junction.WaitForExitAsync();
        Assert.AreEqual(0, junction.ExitCode, await junction.StandardError.ReadToEndAsync());
        try
        {
            var selected = Path.Combine(alias, "game");
            var transport = new RecordingTransport(_ => new(true,
                Installation: new(game, 267, "ready", "idle", AvailableVersion: 267)));
            var coordinator = new GameInstallationCoordinator(directory.Path, transport);
            Assert.IsTrue((await coordinator.ReadStatusAsync(selected)).Ok);
            Assert.IsTrue((await coordinator.CheckAsync(selected)).Ok);
            Assert.IsTrue((await coordinator.UpdateAsync(selected, 267)).Ok);
            Assert.IsTrue((await coordinator.RecoverAsync(selected)).Ok);
            Assert.IsTrue(transport.Requests.All(request => request.GameDirectory == selected));
            var other = Path.Combine(directory.Path, "other-game");
            Directory.CreateDirectory(other);
            Assert.IsFalse(GameInstallationCoordinator.SameDirectory(selected, other));
            Assert.IsFalse(GameInstallationCoordinator.SameDirectory(selected, Path.Combine(directory.Path, "missing")));
        }
        finally { Directory.Delete(alias, recursive: false); }
    }

    private sealed class RecordingTransport(Func<ProfileCatalogRequest, ProfileCatalogResponse> request) : IProfileCatalogTransport
    {
        public List<ProfileCatalogRequest> Requests { get; } = [];
        public ProfileCatalogResponse Request(ProfileCatalogRequest value)
        {
            lock (Requests) Requests.Add(value);
            return request(value);
        }
    }
    private sealed class InlineProgress(Action<GameInstallationSnapshot> report) : IProgress<GameInstallationSnapshot>
    {
        public void Report(GameInstallationSnapshot value) => report(value);
    }
}
