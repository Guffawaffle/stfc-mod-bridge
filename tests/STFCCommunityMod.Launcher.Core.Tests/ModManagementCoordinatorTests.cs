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
        Assert.AreEqual("Install", ready.ActionLabel);
        Assert.IsTrue(ready.CanExecute);
        Assert.IsFalse(blocked.CanExecute);
    }

    [TestMethod]
    public void ExistingManualArtifactOffersExplicitUpdateCheck()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), [8, 6, 7]);
        var (coordinator, _) = CreateCoordinator(temporaryDirectory);

        var presentation = coordinator.CapturePresentation(gameDirectory, isGameRunning: false);

        Assert.AreEqual(ModManagementActionKind.UpdateManualInstallation, presentation.ActionKind);
        Assert.AreEqual("Manual installation detected", presentation.Status);
        Assert.AreEqual("Update mod", presentation.ActionLabel);
    }

    [TestMethod]
    public async Task PreparationPinsExactTargetReleaseAndPreservationPolicy()
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
        Assert.IsFalse(preparation.IsAdoptionOnly);
        StringAssert.StartsWith(preparation.Message, "Update the existing installation to");
    }

    [TestMethod]
    public async Task ManualCurrentArtifactStillRequiresExplicitAdoption()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), ArtifactContents);
        var (coordinator, _) = CreateCoordinator(temporaryDirectory);

        var preparation = await coordinator.PrepareLatestAsync(
            gameDirectory,
            isGameRunning: false);

        Assert.AreEqual(ModOperationPreparationState.Ready, preparation.State);
        Assert.AreEqual(
            ModManagementActionKind.UpdateManualInstallation,
            preparation.ActionKind);
        Assert.AreEqual(
            ExistingArtifactPolicy.AdoptAndPreserve,
            preparation.ExistingArtifactPolicy);
        Assert.IsTrue(preparation.IsAdoptionOnly);
        StringAssert.Contains(preparation.Message, "already installed");
        StringAssert.Contains(preparation.Message, "Let Mod Bridge manage it");
    }

    [TestMethod]
    public async Task PreparationPassesExactResolvedReleaseChannelToDiscovery()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var deploymentService = CreateDeploymentService(temporaryDirectory);
        var discoveryClient = new RecordingReleaseDiscoveryClient(ReleaseDiscovery());
        var coordinator = new ModManagementCoordinator(
            deploymentService,
            discoveryClient,
            new Version(0, 1, 0),
            "preview");

        _ = await coordinator.PrepareLatestAsync(gameDirectory, isGameRunning: false);

        Assert.AreEqual("preview", discoveryClient.LastChannel);
    }

    [TestMethod]
    public async Task ExecutionRejectsPreparationBoundToAnotherProvider()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var (coordinator, _) = CreateCoordinator(temporaryDirectory);
        var preparation = new ModOperationPreparation(
            ModOperationPreparationState.Ready,
            "Ready",
            temporaryDirectory.Path,
            "2.1.0-guffa.8",
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject,
            ModManagementActionKind.Install,
            "netniv");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.ExecuteAsync(preparation));
    }

    [TestMethod]
    public void UnresolvedProviderReasonDisablesProviderBoundMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var coordinator = new ModManagementCoordinator(
            CreateDeploymentService(temporaryDirectory),
            new FakeReleaseDiscoveryClient(ReleaseDiscovery()),
            new Version(0, 1, 0),
            providerUnavailableReason: "Selected provider was withdrawn.");

        var presentation = coordinator.CapturePresentation(gameDirectory, isGameRunning: false);

        Assert.AreEqual(ModManagementActionKind.None, presentation.ActionKind);
        Assert.IsFalse(presentation.CanExecute);
        Assert.AreEqual("Not installed", presentation.Status);
        StringAssert.Contains(presentation.AutomationName, "withdrawn");
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
    public async Task ManagedSignedReleaseRejectsAnOlderAdvertisedRelease()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var attribution = new ModInstallationAttribution(
            "guffawaffle",
            "stable",
            "guffawaffle.windows");
        var deploymentService = new ModDeploymentService(
            temporaryDirectory.CreateDirectory("state"),
            new FakeDownloader(),
            new FakeVersionReader("v2.1.0-guffa.10"),
            new FakeAuthenticityVerifier(),
            _ => false,
            attribution);
        var installed = ReleaseArtifact() with
        {
            ExpectedProductVersion = "v2.1.0-guffa.10",
        };
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await deploymentService.DeployAsync(
                gameDirectory,
                installed,
                ExistingArtifactPolicy.Reject)).State);
        var healthService = new LauncherHealthService(
            new ModInstallationInspector(
                deploymentService,
                new SystemModInstallationFileSystem()),
            new("guffawaffle", "stable", "guffawaffle.windows", true, string.Empty));
        var coordinator = new ModManagementCoordinator(
            deploymentService,
            new FakeReleaseDiscoveryClient(OlderSignedReleaseDiscovery()),
            new Version(0, 1, 0),
            healthService: healthService);

        var preparation = await coordinator.PrepareLatestAsync(
            gameDirectory,
            isGameRunning: false);

        Assert.AreEqual(ModOperationPreparationState.MutationBlocked, preparation.State);
        StringAssert.Contains(preparation.Message, "older than");
        StringAssert.Contains(preparation.Message, "v2.1.0-guffa.10");
        Assert.AreEqual(
            ModUpdateEvidenceState.Unknown,
            coordinator.CaptureHealth(gameDirectory, false).UpdateAvailability,
            "An older signed release must not be recorded as an available update.");
        Assert.AreEqual(
            "v2.1.0-guffa.10",
            deploymentService.ReadInstalledState(gameDirectory)?.ReleaseProductVersion);
        CollectionAssert.AreEqual(
            ArtifactContents,
            File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task ProviderSwitchPreparationRejectsReleaseBelowRetainedProviderFloor()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var guffAttribution = new ModInstallationAttribution(
            "guffawaffle",
            "stable",
            "guffawaffle.windows");
        var guffTenService = new ModDeploymentService(
            stateDirectory,
            new FakeDownloader(),
            new FakeVersionReader("v2.1.0-guffa.10"),
            new FakeAuthenticityVerifier(),
            _ => false,
            guffAttribution);
        var guffTen = ReleaseArtifact() with
        {
            ExpectedProductVersion = "v2.1.0-guffa.10",
        };
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await guffTenService.DeployAsync(
                gameDirectory,
                guffTen,
                ExistingArtifactPolicy.Reject)).State);

        var netnivAttribution = new ModInstallationAttribution(
            "netniv",
            "stable",
            "netniv.stfc-community-mod");
        var netnivArtifact = ReleaseArtifact() with
        {
            ExpectedProductVersion = "v1.1.4",
        };
        var netnivService = new ModDeploymentService(
            stateDirectory,
            new FakeDownloader(),
            new FakeVersionReader(netnivArtifact.ExpectedProductVersion),
            new FakeAuthenticityVerifier(),
            _ => false,
            netnivAttribution);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await netnivService.DeployAsync(
                gameDirectory,
                netnivArtifact,
                ExistingArtifactPolicy.Reject)).State);
        var sourceInstallation = new LauncherHealthService(
                new ModInstallationInspector(
                    netnivService,
                    new SystemModInstallationFileSystem()),
                new("netniv", "stable", "netniv.stfc-community-mod", true, string.Empty))
            .Capture(gameDirectory, isGameRunning: false)
            .Installation;
        var targetService = new ModDeploymentService(
            stateDirectory,
            new FakeDownloader(),
            new FakeVersionReader("v2.1.0-guffa.9"),
            new FakeAuthenticityVerifier(),
            _ => false,
            guffAttribution);
        var targetHealth = new LauncherHealthService(
            new ModInstallationInspector(
                targetService,
                new SystemModInstallationFileSystem()),
            new("guffawaffle", "stable", "guffawaffle.windows", true, string.Empty));
        var coordinator = new ModManagementCoordinator(
            targetService,
            new FakeReleaseDiscoveryClient(OlderSignedReleaseDiscovery()),
            new Version(0, 1, 0),
            healthService: targetHealth);

        var preparation = await coordinator.PrepareProviderSwitchTargetAsync(
            gameDirectory,
            isGameRunning: false,
            sourceInstallation);

        Assert.AreEqual(ModOperationPreparationState.MutationBlocked, preparation.State);
        StringAssert.Contains(preparation.Message, "v2.1.0-guffa.10");
        Assert.AreEqual("netniv", targetService.ReadInstalledState(gameDirectory)?.ProviderId);
        CollectionAssert.AreEqual(
            ArtifactContents,
            File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task ExactReviewedNetnivReleaseIsUpToDateDespiteHavingNoEmbeddedProductVersion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var attribution = new ModInstallationAttribution(
            "netniv",
            "stable",
            "netniv.stfc-community-mod");
        var certification = ReviewedCertification(
            attribution,
            "v1.1.4",
            ReleaseArtifact());
        var deploymentService = new ModDeploymentService(
            temporaryDirectory.CreateDirectory("state"),
            new FakeDownloader(),
            new FakeVersionReader(),
            new FakeAuthenticityVerifier(),
            _ => false,
            attribution,
            reviewedCertifications: [certification]);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await deploymentService.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        var healthService = new LauncherHealthService(
            new ModInstallationInspector(
                deploymentService,
                new SystemModInstallationFileSystem()),
            new("netniv", "stable", "netniv.stfc-community-mod", true, string.Empty));
        var coordinator = new ModManagementCoordinator(
            deploymentService,
            new FakeReleaseDiscoveryClient(ReleaseDiscovery()),
            new Version(0, 1, 0),
            healthService: healthService);

        var preparation = await coordinator.PrepareLatestAsync(gameDirectory, isGameRunning: false);

        Assert.AreEqual(ModOperationPreparationState.UpToDate, preparation.State, preparation.Message);
        Assert.AreEqual("v1.1.4", deploymentService.ReadInstalledState(gameDirectory)!.ReleaseProductVersion);
    }

    [TestMethod]
    public async Task ExactReviewedNetnivReleaseCanRepairWithoutEmbeddedProductVersion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var attribution = new ModInstallationAttribution(
            "netniv",
            "stable",
            "netniv.stfc-community-mod");
        var artifact = ReleaseArtifact();
        var certification = ReviewedCertification(attribution, "v1.1.4", artifact);
        var deploymentService = new ModDeploymentService(
            temporaryDirectory.CreateDirectory("state"),
            new FakeDownloader(),
            new FakeVersionReader(),
            new FakeAuthenticityVerifier(),
            _ => false,
            attribution,
            reviewedCertifications: [certification]);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await deploymentService.DeployAsync(
                gameDirectory,
                artifact,
                ExistingArtifactPolicy.Reject)).State);
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), [0, 0, 0]);
        var coordinator = new ModManagementCoordinator(
            deploymentService,
            new FakeReleaseDiscoveryClient(ReleaseDiscovery()),
            new Version(0, 1, 0),
            healthService: new LauncherHealthService(
                new ModInstallationInspector(
                    deploymentService,
                    new SystemModInstallationFileSystem()),
                new("netniv", "stable", "netniv.stfc-community-mod", true, string.Empty)));

        var preparation = await coordinator.PrepareLatestAsync(gameDirectory, isGameRunning: false);
        var result = await coordinator.ExecuteAsync(preparation);

        Assert.AreEqual(ModOperationPreparationState.Ready, preparation.State, preparation.Message);
        Assert.AreEqual(ModManagementActionKind.Repair, preparation.ActionKind);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task ExactReviewedNetnivReleaseCanReturnAfterAProviderRoundTrip()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var netnivAttribution = new ModInstallationAttribution(
            "netniv",
            "stable",
            "netniv.stfc-community-mod");
        var certification = ReviewedCertification(
            netnivAttribution,
            "v1.1.4",
            ReleaseArtifact());
        var netnivService = new ModDeploymentService(
            stateDirectory,
            new FakeDownloader(),
            new FakeVersionReader(),
            new FakeAuthenticityVerifier(),
            _ => false,
            netnivAttribution,
            reviewedCertifications: [certification]);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await netnivService.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        var guffAttribution = new ModInstallationAttribution(
            "guffawaffle",
            "stable",
            "guffawaffle.windows");
        var guffArtifact = ReleaseArtifact() with
        {
            ExpectedProductVersion = "v2.1.0-guffa.10",
        };
        var guffService = new ModDeploymentService(
            stateDirectory,
            new FakeDownloader(),
            new FakeVersionReader(guffArtifact.ExpectedProductVersion),
            new FakeAuthenticityVerifier(),
            _ => false,
            guffAttribution,
            reviewedCertifications: [certification]);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await guffService.DeployAsync(
                gameDirectory,
                guffArtifact,
                ExistingArtifactPolicy.Reject)).State);
        var coordinator = new ModManagementCoordinator(
            netnivService,
            new FakeReleaseDiscoveryClient(ReleaseDiscovery()),
            new Version(0, 1, 0),
            healthService: new LauncherHealthService(
                new ModInstallationInspector(
                    netnivService,
                    new SystemModInstallationFileSystem()),
                new("netniv", "stable", "netniv.stfc-community-mod", true, string.Empty)));
        var source = new ModInstallationEvidence(
            ModInstallationEvidenceState.ManagedVerified,
            IsGameRunning: false,
            InstalledVersion: guffArtifact.ExpectedVersion,
            InstalledProviderId: "guffawaffle",
            InstalledReleaseChannelId: "stable",
            InstalledRuntimeDistributionId: "guffawaffle.windows",
            InstalledSha256: guffArtifact.Sha256);

        var preparation = await coordinator.PrepareProviderSwitchTargetAsync(
            gameDirectory,
            isGameRunning: false,
            source);

        Assert.AreEqual(ModOperationPreparationState.Ready, preparation.State, preparation.Message);
    }

    [TestMethod]
    public async Task RunningVerifiedInstallationCanCheckButCannotPrepareMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var attribution = new ModInstallationAttribution("guffawaffle", "stable", "guffawaffle.windows");
        var installedArtifact = ReleaseArtifact() with
        {
            ExpectedProductVersion = "v2.1.0-guffa.8",
        };
        var deploymentService = new ModDeploymentService(
            temporaryDirectory.CreateDirectory("state"),
            new FakeDownloader(),
            new FakeVersionReader(installedArtifact.ExpectedProductVersion),
            new FakeAuthenticityVerifier(),
            _ => false,
            attribution);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await deploymentService.DeployAsync(
                gameDirectory,
                installedArtifact,
                ExistingArtifactPolicy.Reject)).State);
        var healthService = new LauncherHealthService(
            new ModInstallationInspector(deploymentService, new SystemModInstallationFileSystem()),
            new("guffawaffle", "stable", "guffawaffle.windows", true, string.Empty));
        var coordinator = new ModManagementCoordinator(
            deploymentService,
            new FakeReleaseDiscoveryClient(UpdatedReleaseDiscovery()),
            new Version(0, 1, 0),
            healthService: healthService);

        var presentation = coordinator.CapturePresentation(gameDirectory, isGameRunning: true);
        var preparation = await coordinator.PrepareLatestAsync(gameDirectory, isGameRunning: true);

        Assert.AreEqual(LauncherHomeTone.Success, presentation.Tone);
        Assert.AreEqual("Installed 2.1.0.8", presentation.Status);
        Assert.AreEqual("Update mod", presentation.ActionLabel);
        Assert.IsTrue(presentation.CanExecute);
        Assert.AreEqual(ModOperationPreparationState.MutationBlocked, preparation.State);
        StringAssert.Contains(preparation.Message, "Close Star Trek Fleet Command");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.ExecuteAsync(preparation));
    }

    [TestMethod]
    public async Task ReleaseCheckRecordsIdentityBoundUpdateObservation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var attribution = new ModInstallationAttribution("guffawaffle", "stable", "guffawaffle.windows");
        var deploymentService = CreateDeploymentService(temporaryDirectory, attribution);
        var healthService = new LauncherHealthService(
            new ModInstallationInspector(deploymentService, new SystemModInstallationFileSystem()),
            new("guffawaffle", "stable", "guffawaffle.windows", true, string.Empty));
        var coordinator = new ModManagementCoordinator(
            deploymentService,
            new FakeReleaseDiscoveryClient(ReleaseDiscovery()),
            new Version(0, 1, 0),
            healthService: healthService);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await deploymentService.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        Assert.AreEqual(
            ModUpdateEvidenceState.Unknown,
            coordinator.CaptureHealth(gameDirectory, false).UpdateAvailability);

        _ = await coordinator.PrepareLatestAsync(gameDirectory, isGameRunning: false);

        Assert.AreEqual(
            ModUpdateEvidenceState.UpToDate,
            coordinator.CaptureHealth(gameDirectory, false).UpdateAvailability);
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
    public async Task RepairIsBlockedWhenLatestReleaseDoesNotMatchRecordedReceipt()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var attribution = new ModInstallationAttribution("guffawaffle", "stable", "guffawaffle.windows");
        var deploymentService = CreateDeploymentService(temporaryDirectory, attribution);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await deploymentService.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        File.WriteAllBytes(targetPath, [0, 0, 0]);
        var healthService = new LauncherHealthService(
            new ModInstallationInspector(deploymentService, new SystemModInstallationFileSystem()),
            new("guffawaffle", "stable", "guffawaffle.windows", true, string.Empty));
        var coordinator = new ModManagementCoordinator(
            deploymentService,
            new FakeReleaseDiscoveryClient(UpdatedReleaseDiscovery()),
            new Version(0, 1, 0),
            healthService: healthService);

        var preparation = await coordinator.PrepareLatestAsync(gameDirectory, isGameRunning: false);

        Assert.AreEqual(ModOperationPreparationState.MutationBlocked, preparation.State);
        StringAssert.Contains(preparation.Message, "exact release recorded");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.ExecuteAsync(preparation));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0 }, File.ReadAllBytes(targetPath));
    }

    [TestMethod]
    public async Task RepairUsesExactReleaseRecordedBySelectedInstallation()
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

        var preparation = await coordinator.PrepareLatestAsync(gameDirectory, isGameRunning: false);
        var result = await coordinator.ExecuteAsync(preparation);

        Assert.AreEqual(ModOperationPreparationState.Ready, preparation.State);
        Assert.AreEqual(ModManagementActionKind.Repair, preparation.ActionKind);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(targetPath));
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
        var attribution = new ModInstallationAttribution("guffawaffle", "stable", "guffawaffle.windows");
        var deploymentService = CreateDeploymentService(temporaryDirectory, attribution);
        var healthService = new LauncherHealthService(
            new ModInstallationInspector(deploymentService, new SystemModInstallationFileSystem()),
            new("guffawaffle", "stable", "guffawaffle.windows", true, string.Empty));
        return (
            new(
                deploymentService,
                new FakeReleaseDiscoveryClient(ReleaseDiscovery()),
                new Version(0, 1, 0),
                healthService: healthService),
            deploymentService);
    }

    private static ModDeploymentService CreateDeploymentService(
        TemporaryDirectory temporaryDirectory,
        ModInstallationAttribution? installationAttribution = null) =>
        new(
            temporaryDirectory.CreateDirectory("state"),
            new FakeDownloader(),
            new FakeVersionReader(),
            new FakeAuthenticityVerifier(),
            _ => false,
            installationAttribution ?? new("guffawaffle", "stable", "guffawaffle.windows"));

    private static WindowsReleaseDiscovery ReleaseDiscovery() =>
        new(
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

    private static WindowsReleaseDiscovery UpdatedReleaseDiscovery()
    {
        byte[] updatedContents = [2, 7, 1, 8, 2, 8];
        return new(
            new WindowsReleaseManifest(
                1,
                "2.1.0-guffa.9",
                "v2.1.0-guffa.9",
                "stable",
                "active",
                new Version(0, 1, 0),
                new("Guffawaffle/stfc-mod", "1123456789abcdef0123456789abcdef01234567"),
                "none",
                []),
            new(
                new Uri("https://example.invalid/updated-version.dll"),
                "version.dll",
                updatedContents.LongLength,
                Convert.ToHexString(SHA256.HashData(updatedContents)),
                "2.1.0.9",
                ExpectedProductVersion: "v2.1.0-guffa.9"));
    }

    private static WindowsReleaseDiscovery OlderSignedReleaseDiscovery()
    {
        byte[] olderContents = [2, 1, 0, 9];
        return new(
            new WindowsReleaseManifest(
                1,
                "2.1.0-guffa.9",
                "v2.1.0-guffa.9",
                "stable",
                "active",
                new Version(0, 1, 0),
                new("Guffawaffle/stfc-mod", "1123456789abcdef0123456789abcdef01234567"),
                "none",
                []),
            new(
                new Uri("https://example.invalid/older-version.dll"),
                "version.dll",
                olderContents.LongLength,
                Convert.ToHexString(SHA256.HashData(olderContents)),
                "2.1.0.8",
                ExpectedProductVersion: "v2.1.0-guffa.9"));
    }

    private static ReviewedReleaseCertification ReviewedCertification(
        ModInstallationAttribution attribution,
        string tag,
        ModReleaseArtifact artifact) => new(
            attribution.ProviderId,
            attribution.ReleaseChannelId,
            attribution.RuntimeDistributionId,
            "netniV/stfc-mod",
            tag,
            tag.TrimStart('v'),
            new string('A', 40),
            artifact.FileName,
            artifact.Size,
            artifact.Sha256,
            artifact.FileName,
            artifact.Size,
            artifact.Sha256,
            artifact.ExpectedVersion,
            DateTimeOffset.UtcNow);

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

    private sealed class RecordingReleaseDiscoveryClient(WindowsReleaseDiscovery discovery)
        : IWindowsReleaseDiscoveryClient
    {
        public string? LastChannel { get; private set; }

        public Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
            string channel,
            Version currentLauncherVersion,
            CancellationToken cancellationToken = default)
        {
            _ = currentLauncherVersion;
            _ = cancellationToken;
            LastChannel = channel;
            return Task.FromResult(discovery);
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

    private sealed class FakeVersionReader(string? productVersion = null)
        : IModArtifactProductVersionReader
    {
        public string? ReadVersion(string artifactPath) => "2.1.0.8";

        public string? ReadProductVersion(string artifactPath) => productVersion;
    }

    private sealed class FakeAuthenticityVerifier : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) => new(true, "trusted test artifact");
    }
}
