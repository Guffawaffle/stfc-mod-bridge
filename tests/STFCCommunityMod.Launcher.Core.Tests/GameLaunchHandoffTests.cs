using System.Net;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class GameLaunchHandoffTests
{
    private static readonly byte[] ArtifactContents = [2, 7, 1, 8, 2, 8];

    [TestMethod]
    public void ModdedAndUnsupportedUnmoddedStatesAreExplicit()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);

        var modded = fixture.Coordinator.CapturePresentation(gameDirectory, GameLaunchMode.Modded);
        var unmodded = fixture.Coordinator.CapturePresentation(gameDirectory, GameLaunchMode.Unmodded);

        Assert.AreEqual("Mod required", modded.Status);
        Assert.AreEqual("Unmodded unavailable", unmodded.Status);
        Assert.IsFalse(modded.CanExecute);
        Assert.IsFalse(unmodded.CanExecute);
        Assert.IsTrue(unmodded.AutomationName.Contains("cannot safely disable", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HealthyManagedInstallIsLaunchableWithoutReleaseDiscovery()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);

        var presentation = fixture.Coordinator.CapturePresentation(gameDirectory);

        Assert.AreEqual("Ready to play", presentation.Status);
        Assert.AreEqual("Launch game", presentation.ActionLabel);
        Assert.IsTrue(presentation.CanExecute);
        Assert.AreEqual(0, fixture.LauncherService.StartCount);
    }

    [TestMethod]
    public async Task OfficialLauncherMustBeAvailable()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory, launcherAvailable: false);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);

        var presentation = fixture.Coordinator.CapturePresentation(gameDirectory);

        Assert.AreEqual("Official launcher needed", presentation.Status);
        Assert.IsFalse(presentation.CanExecute);
    }

    [TestMethod]
    public async Task LaunchHandoffHoldsDeploymentLockUntilOfficialLauncherExits()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);

        var launchTask = fixture.Coordinator.LaunchAsync(gameDirectory);
        await fixture.LauncherService.WaitUntilStartedAsync();
        var concurrentDeployment = await fixture.DeploymentService.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Busy, concurrentDeployment.State);
        fixture.LauncherService.CompleteExit();
        var result = await launchTask;
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
    }

    [TestMethod]
    public async Task OfficialLauncherExitReevaluatesGameAndModHealth()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);

        var launchTask = fixture.Coordinator.LaunchAsync(gameDirectory);
        await fixture.LauncherService.WaitUntilStartedAsync();
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), [0, 0, 0]);
        fixture.LauncherService.CompleteExit();
        var result = await launchTask;

        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.AreEqual("Repair required", result.Presentation.Status);
        Assert.IsFalse(result.Presentation.CanExecute);
    }

    [TestMethod]
    public async Task RunningGameBlocksAnotherLaunch()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory, isGameRunning: true);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);

        var presentation = fixture.Coordinator.CapturePresentation(gameDirectory);
        var result = await fixture.Coordinator.LaunchAsync(gameDirectory);

        Assert.AreEqual("Running", presentation.Status);
        Assert.AreEqual(GameLaunchHandoffState.Blocked, result.State);
        Assert.AreEqual(0, fixture.LauncherService.StartCount);
    }

    private static Fixture CreateFixture(
        TemporaryDirectory temporaryDirectory,
        bool launcherAvailable = true,
        bool isGameRunning = false)
    {
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var deploymentService = new ModDeploymentService(
            stateDirectory,
            new FakeDownloader(),
            new FakeVersionReader(),
            new FakeAuthenticityVerifier(),
            () => false);
        var launcherService = new FakeOfficialLauncherService(launcherAvailable);
        var coordinator = new GameLaunchHandoffCoordinator(
            stateDirectory,
            deploymentService,
            launcherService,
            new FakeGameProcessInspector(isGameRunning));
        return new(coordinator, deploymentService, launcherService);
    }

    private static async Task InstallManagedArtifactAsync(
        ModDeploymentService deploymentService,
        string gameDirectory)
    {
        var result = await deploymentService.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
    }

    private static string CreateGameDirectory(TemporaryDirectory temporaryDirectory)
    {
        var gameDirectory = temporaryDirectory.CreateDirectory("game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        return gameDirectory;
    }

    private static ModReleaseArtifact ReleaseArtifact() => new(
        new Uri("https://example.invalid/version.dll"),
        "version.dll",
        ArtifactContents.LongLength,
        Convert.ToHexString(SHA256.HashData(ArtifactContents)),
        "2.1.0.8");

    private sealed record Fixture(
        GameLaunchHandoffCoordinator Coordinator,
        ModDeploymentService DeploymentService,
        FakeOfficialLauncherService LauncherService);

    private sealed class FakeGameProcessInspector(bool isRunning) : IGameProcessInspector
    {
        public bool IsGameRunning() => isRunning;
    }

    private sealed class FakeOfficialLauncherService(bool isAvailable) : IOfficialLauncherService
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable { get; } = isAvailable;

        public int StartCount { get; private set; }

        public Task<IOfficialLauncherProcess> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            started.TrySetResult();
            return Task.FromResult<IOfficialLauncherProcess>(new FakeProcess(exited.Task));
        }

        public Task WaitUntilStartedAsync() => started.Task;

        public void CompleteExit() => exited.TrySetResult();

        private sealed class FakeProcess(Task exitTask) : IOfficialLauncherProcess
        {
            public Task WaitForExitAsync(CancellationToken cancellationToken) => exitTask.WaitAsync(cancellationToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDownloader : IModArtifactDownloader
    {
        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken) =>
            Task.FromResult(new ModArtifactDownload(
                HttpStatusCode.OK,
                ArtifactContents,
                ArtifactContents.LongLength));
    }

    private sealed class FakeVersionReader : IModArtifactVersionReader
    {
        public string? ReadVersion(string artifactPath) => "2.1.0.8";
    }

    private sealed class FakeAuthenticityVerifier : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) => new(true, "trusted test artifact");
    }
}
