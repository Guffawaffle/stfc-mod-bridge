using System.Net;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class GameLaunchHandoffTests
{
    private static readonly byte[] ArtifactContents = [2, 7, 1, 8, 2, 8];

    [TestMethod]
    public async Task HealthyManagedInstallLaunchesPrimeDirectly()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);

        var presentation = fixture.Coordinator.CapturePresentation(gameDirectory, LauncherLaunchTarget.PrimeExecutable);
        var result = await fixture.Coordinator.LaunchAsync(gameDirectory, LauncherLaunchTarget.PrimeExecutable);

        Assert.AreEqual("Launch prime.exe", presentation.ActionLabel);
        Assert.IsTrue(presentation.CanExecute);
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.AreEqual(1, fixture.GameService.StartCount);
        Assert.AreEqual(0, fixture.ScopelyService.StartCount);
    }

    [TestMethod]
    public async Task ScopelyLauncherIsIndependentOfGameFolderAndGameProcess()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CreateFixture(temporaryDirectory, isGameRunning: true);

        var presentation = fixture.Coordinator.CapturePresentation(null, LauncherLaunchTarget.ScopelyLauncher);
        var result = await fixture.Coordinator.LaunchAsync(null, LauncherLaunchTarget.ScopelyLauncher);

        Assert.IsTrue(presentation.CanExecute);
        Assert.AreEqual("Open Scopely launcher", presentation.ActionLabel);
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.AreEqual(1, fixture.ScopelyService.StartCount);
        Assert.AreEqual(0, fixture.GameService.StartCount);
    }

    [TestMethod]
    public async Task RunningGameBlocksPrimeWithoutBlockingScopely()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory, isGameRunning: true);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);

        var prime = fixture.Coordinator.CapturePresentation(gameDirectory, LauncherLaunchTarget.PrimeExecutable);
        var scopely = fixture.Coordinator.CapturePresentation(gameDirectory, LauncherLaunchTarget.ScopelyLauncher);

        Assert.AreEqual("Running", prime.Status);
        Assert.IsFalse(prime.CanExecute);
        Assert.IsTrue(scopely.CanExecute);
    }

    [TestMethod]
    public async Task MissingTargetsReportDiagnosticRecoveryWithoutStartingAnything()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory, gameAvailable: false, scopelyAvailable: false);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);

        var prime = fixture.Coordinator.CapturePresentation(gameDirectory, LauncherLaunchTarget.PrimeExecutable);
        var scopely = fixture.Coordinator.CapturePresentation(gameDirectory, LauncherLaunchTarget.ScopelyLauncher);

        Assert.IsFalse(prime.CanExecute);
        Assert.IsFalse(scopely.CanExecute);
        StringAssert.Contains(prime.AutomationName, "Diagnostics");
        StringAssert.Contains(scopely.AutomationName, "Diagnostics");
        Assert.AreEqual(0, fixture.GameService.StartCount);
        Assert.AreEqual(0, fixture.ScopelyService.StartCount);
    }

    [TestMethod]
    public async Task LaunchFailuresAreReportedThroughTheSelectedTarget()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CreateFixture(temporaryDirectory, scopelyFailure: new IOException("blocked"));

        var result = await fixture.Coordinator.LaunchAsync(null, LauncherLaunchTarget.ScopelyLauncher);

        Assert.AreEqual(GameLaunchHandoffState.Failed, result.State);
        StringAssert.Contains(result.Message, "blocked");
        Assert.AreEqual(1, fixture.ScopelyService.StartCount);
    }

    [TestMethod]
    public async Task PrimeLaunchReevaluatesHealthAfterStarting()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);
        fixture.GameService.OnStart = () => File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), [0, 0, 0]);

        var result = await fixture.Coordinator.LaunchAsync(gameDirectory, LauncherLaunchTarget.PrimeExecutable);

        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.AreEqual("Repair required", result.Presentation.Status);
    }

    private static Fixture CreateFixture(
        TemporaryDirectory temporaryDirectory,
        bool gameAvailable = true,
        bool scopelyAvailable = true,
        bool isGameRunning = false,
        Exception? scopelyFailure = null)
    {
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var deploymentService = new ModDeploymentService(
            stateDirectory,
            new FakeDownloader(),
            new FakeVersionReader(),
            new FakeAuthenticityVerifier(),
            () => false);
        var gameService = new FakeGameExecutableLaunchService(gameAvailable);
        var scopelyService = new FakeOfficialLauncherService(scopelyAvailable, scopelyFailure);
        var coordinator = new GameLaunchHandoffCoordinator(
            stateDirectory,
            deploymentService,
            gameService,
            scopelyService,
            new FakeGameProcessInspector(isGameRunning));
        return new(coordinator, deploymentService, gameService, scopelyService);
    }

    private static async Task InstallManagedArtifactAsync(ModDeploymentService deploymentService, string gameDirectory)
    {
        var result = await deploymentService.DeployAsync(gameDirectory, ReleaseArtifact(), ExistingArtifactPolicy.Reject);
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
        FakeGameExecutableLaunchService GameService,
        FakeOfficialLauncherService ScopelyService);

    private sealed class FakeGameProcessInspector(bool isRunning) : IGameProcessInspector
    {
        public bool IsGameRunning() => isRunning;
    }

    private sealed class FakeGameExecutableLaunchService(bool isAvailable) : IGameExecutableLaunchService
    {
        public int StartCount { get; private set; }

        public Action? OnStart { get; set; }

        public bool IsAvailable(string gameDirectory) => isAvailable;

        public Task StartAsync(string gameDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            OnStart?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOfficialLauncherService(bool isAvailable, Exception? failure) : IOfficialLauncherService
    {
        public bool IsAvailable { get; } = isAvailable;

        public int StartCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }

    private sealed class FakeDownloader : IModArtifactDownloader
    {
        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken) =>
            Task.FromResult(new ModArtifactDownload(HttpStatusCode.OK, ArtifactContents, ArtifactContents.LongLength));
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
