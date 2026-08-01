using System.Net;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ModManagementCoordinatorTests
{
    private static readonly byte[] ArtifactContents = [3, 1, 4, 1, 5, 9];

    [TestMethod]
    public void MissingGameSelectionCannotOfferMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var (coordinator, _) = CreateCoordinator(temporaryDirectory);

        var presentation = coordinator.CapturePresentation(null, isGameRunning: false);

        Assert.AreEqual(ModManagementActionKind.None, presentation.ActionKind);
        Assert.IsFalse(presentation.CanExecute);
        Assert.AreEqual("Select a game folder", presentation.Status);
    }

    [TestMethod]
    public void FreshTargetOffersInstallOnlyWhileGameIsClosed()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var (coordinator, _) = CreateCoordinator(temporaryDirectory);

        var ready = coordinator.CapturePresentation(gameDirectory, isGameRunning: false);
        var blocked = coordinator.CapturePresentation(gameDirectory, isGameRunning: true);

        Assert.AreEqual(ModManagementActionKind.Install, ready.ActionKind);
        Assert.AreEqual("Install mod", ready.ActionLabel);
        Assert.IsTrue(ready.CanExecute);
        Assert.IsFalse(blocked.CanExecute);
    }

    [TestMethod]
    public void ExistingManualArtifactOffersExplicitAdoption()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), [8, 6, 7]);
        var (coordinator, _) = CreateCoordinator(temporaryDirectory);

        var presentation = coordinator.CapturePresentation(gameDirectory, isGameRunning: false);

        Assert.AreEqual(ModManagementActionKind.AdoptAndInstall, presentation.ActionKind);
        Assert.AreEqual("Manual install found", presentation.Status);
        Assert.AreEqual("Adopt & update", presentation.ActionLabel);
    }

    [TestMethod]
    public async Task PreparationPinsExactTargetReleaseAndAdoptionPolicy()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), [2, 7, 1, 8]);
        var (coordinator, _) = CreateCoordinator(temporaryDirectory);

        var preparation = await coordinator.PrepareLatestAsync(
            gameDirectory,
            isGameRunning: false);

        Assert.AreEqual(ModOperationPreparationState.Ready, preparation.State);
        Assert.AreEqual(ExistingArtifactPolicy.AdoptAndPreserve, preparation.ExistingArtifactPolicy);
        Assert.AreEqual(Path.GetFullPath(gameDirectory), preparation.GameDirectory);
        Assert.AreEqual("2.1.0-guffa.8", preparation.ReleaseVersion);
        Assert.IsTrue(preparation.Message.Contains("Adopt", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ManagedArtifactShowsVersionAndDetectsUpToDateRelease()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var (coordinator, deploymentService) = CreateCoordinator(temporaryDirectory);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await deploymentService.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);

        var presentation = coordinator.CapturePresentation(gameDirectory, isGameRunning: false);
        var preparation = await coordinator.PrepareLatestAsync(gameDirectory, isGameRunning: false);

        Assert.AreEqual("Installed 2.1.0.8", presentation.Status);
        Assert.AreEqual(LauncherHomeTone.Success, presentation.Tone);
        Assert.AreEqual(ModManagementActionKind.CheckForUpdate, presentation.ActionKind);
        Assert.AreEqual(ModOperationPreparationState.UpToDate, preparation.State);
    }

    [TestMethod]
    public async Task ExternalArtifactChangeFailsClosedAsRepairRequired()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var (coordinator, deploymentService) = CreateCoordinator(temporaryDirectory);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await deploymentService.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        File.WriteAllBytes(targetPath, [0, 0, 0]);

        var presentation = coordinator.CapturePresentation(gameDirectory, isGameRunning: false);

        Assert.AreEqual("Repair required", presentation.Status);
        Assert.AreEqual(LauncherHomeTone.Error, presentation.Tone);
        Assert.IsTrue(presentation.CanExecute);
        Assert.AreEqual(ModManagementActionKind.Repair, presentation.ActionKind);
    }

    [TestMethod]
    public async Task ReadyPreparationExecutesOnlyThroughTransactionService()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var (coordinator, deploymentService) = CreateCoordinator(temporaryDirectory);
        var preparation = await coordinator.PrepareLatestAsync(gameDirectory, isGameRunning: false);

        var result = await coordinator.ExecuteAsync(preparation);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
        Assert.IsNotNull(deploymentService.ReadInstalledState());
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
    }

    private static (ModManagementCoordinator Coordinator, ModDeploymentService DeploymentService) CreateCoordinator(
        TemporaryDirectory temporaryDirectory)
    {
        var deploymentService = new ModDeploymentService(
            temporaryDirectory.CreateDirectory("state"),
            new FakeDownloader(),
            new FakeVersionReader(),
            new FakeAuthenticityVerifier(),
            () => false);
        var discovery = new WindowsReleaseDiscovery(
            new WindowsReleaseManifest(
                1,
                "2.1.0-guffa.8",
                "v2.1.0-guffa.8",
                "stable",
                "active",
                new Version(0, 1, 0),
                new("Guffawaffle/stfc-mod", "0123456789abcdef0123456789abcdef01234567"),
                "none",
                []),
            ReleaseArtifact());
        return (
            new(
                deploymentService,
                new FakeReleaseDiscoveryClient(discovery),
                new Version(0, 1, 0)),
            deploymentService);
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

    private sealed class FakeReleaseDiscoveryClient(WindowsReleaseDiscovery discovery)
        : IWindowsReleaseDiscoveryClient
    {
        public Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
            string channel,
            Version currentLauncherVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(discovery);
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
