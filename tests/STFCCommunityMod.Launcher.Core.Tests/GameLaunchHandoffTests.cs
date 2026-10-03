using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class GameLaunchHandoffTests
{
    private static readonly byte[] ArtifactContents = [2, 7, 1, 8, 2, 8];
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task NamedProfileLaunchUsesSharedCoordinatorAndWaitsForReadiness()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var game = CreateGameDirectory(temporaryDirectory);
        ProfileRuntimeFixture.WriteProfileDll(Path.Combine(game, "version.dll"));
        var profile = NamedProfile(game);
        var transport = new NamedCatalog(profile);
        var store = new NativeLauncherProfilesStore(temporaryDirectory.CreateDirectory("state"), transport);
        await store.SelectAsync(profile.Id);
        var fixture = CreateFixture(temporaryDirectory, profileStore: store);
        var result = await fixture.Coordinator.LaunchProfileAsync(profile, allowUnverifiedProxy: true);
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.AreEqual(0, fixture.GameService.StartCount);
        var request = transport.Requests.Single(request => request.Operation == "launch");
        Assert.AreEqual(profile.Id, request.Id);
        Assert.AreEqual(game, request.GameDirectory);
        Assert.AreEqual(profile.Revision, request.ExpectedRevision);
        StringAssert.Contains(result.Message, "isolated preferences");
    }

    [TestMethod]
    public async Task DifferentNamedProfileMayLaunchFromAnAlreadyRunningExecutable()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var game = CreateGameDirectory(temporaryDirectory);
        ProfileRuntimeFixture.WriteProfileDll(Path.Combine(game, "version.dll"));
        var profile = NamedProfile(game);
        var transport = new NamedCatalog(profile);
        var store = new NativeLauncherProfilesStore(temporaryDirectory.CreateDirectory("state"), transport);
        await store.SelectAsync(profile.Id);
        var fixture = CreateFixture(temporaryDirectory, isGameRunning: true, profileStore: store);
        var result = await fixture.Coordinator.LaunchProfileAsync(profile, allowUnverifiedProxy: true);
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.AreEqual(1, transport.Requests.Count(request => request.Operation == "launch"));
    }

    [TestMethod]
    public async Task SharedCoordinatorRefusalNeverClaimsReadinessOrStartsThroughDefaultService()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var game = CreateGameDirectory(temporaryDirectory);
        ProfileRuntimeFixture.WriteProfileDll(Path.Combine(game, "version.dll"));
        var profile = NamedProfile(game);
        var transport = new NamedCatalog(profile) { LaunchResult = new(false,
            Error: new("profile_in_use", "Profile is already running")) };
        var store = new NativeLauncherProfilesStore(temporaryDirectory.CreateDirectory("state"), transport);
        await store.SelectAsync(profile.Id);
        var result = await CreateFixture(temporaryDirectory, profileStore: store).Coordinator
            .LaunchProfileAsync(profile, allowUnverifiedProxy: true);
        Assert.AreEqual(GameLaunchHandoffState.Failed, result.State);
        Assert.IsFalse(result.Changed);
        StringAssert.Contains(result.Message, "already running");
    }

    [TestMethod]
    public async Task SpawnedButUnreadyGameIsReportedWithoutSilentKillOrRetry()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var game = CreateGameDirectory(temporaryDirectory);
        ProfileRuntimeFixture.WriteProfileDll(Path.Combine(game, "version.dll"));
        var profile = NamedProfile(game);
        var transport = new NamedCatalog(profile) { LaunchResult = new(false,
            Error: new("readiness_timeout", "Isolation was not confirmed"), ProcessId: 123) };
        var store = new NativeLauncherProfilesStore(temporaryDirectory.CreateDirectory("state"), transport);
        await store.SelectAsync(profile.Id);
        var result = await CreateFixture(temporaryDirectory, profileStore: store).Coordinator
            .LaunchProfileAsync(profile, allowUnverifiedProxy: true);
        Assert.AreEqual(GameLaunchHandoffState.Failed, result.State);
        Assert.IsTrue(result.Changed);
        StringAssert.Contains(result.Message, "123 may still be running");
    }

    [TestMethod]
    public async Task DefaultLaunchRejectsASelectionChangedInAnotherWindow()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var displayed = CreateGameDirectory(temporaryDirectory, "displayed-default");
        var current = CreateGameDirectory(temporaryDirectory, "current-default");
        var fixture = CreateFixture(temporaryDirectory);
        var state = Path.GetDirectoryName(fixture.DeploymentService.JournalPath)!;
        new JsonGameInstallSelectionStore(state).Save(current);

        var result = await fixture.Coordinator.LaunchAsync(displayed, LauncherLaunchTarget.PrimeExecutable);

        Assert.AreEqual(GameLaunchHandoffState.Blocked, result.State);
        Assert.AreEqual(0, fixture.GameService.StartCount);
    }

    [TestMethod]
    public async Task NamedLaunchRejectsChangedUiSelection()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var game = CreateGameDirectory(temporaryDirectory);
        ProfileRuntimeFixture.WriteProfileDll(Path.Combine(game, "version.dll"));
        var profile = NamedProfile(game);
        var transport = new NamedCatalog(profile);
        var store = new NativeLauncherProfilesStore(temporaryDirectory.CreateDirectory("state"), transport);
        await store.SelectAsync(profile.Id);
        await store.SelectAsync(null);
        var fixture = CreateFixture(temporaryDirectory, profileStore: store);
        var result = await fixture.Coordinator.LaunchProfileAsync(profile, allowUnverifiedProxy: true);
        Assert.AreEqual(GameLaunchHandoffState.Blocked, result.State);
        Assert.AreEqual(0, fixture.GameService.StartCount);
        Assert.AreEqual(0, transport.Requests.Count(request => request.Operation == "launch"));
    }

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
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(1, fixture.GameService.StartCount);
        Assert.AreEqual(0, fixture.ScopelyService.StartCount);
    }

    [TestMethod]
    public async Task MissingProxyLaunchesPrimeWithoutTheCommunityMod()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);

        var presentation = fixture.Coordinator.CapturePresentation(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable);
        var result = await fixture.Coordinator.LaunchAsync(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable);

        Assert.AreEqual("Ready without mod", presentation.Status);
        Assert.IsTrue(presentation.CanExecute);
        Assert.IsFalse(presentation.RequiresUserOverride);
        StringAssert.Contains(presentation.Reason, "without the community mod");
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.AreEqual(1, fixture.GameService.StartCount);
    }

    [TestMethod]
    public async Task MissingProxyIgnoresMalformedStaleInstalledState()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);
        await File.WriteAllTextAsync(
            fixture.DeploymentService.InstalledStatePath,
            "{\"gameDirectory\":");

        var presentation = fixture.Coordinator.CapturePresentation(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable);
        var result = await fixture.Coordinator.LaunchAsync(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable);

        Assert.AreEqual("Ready without mod", presentation.Status);
        Assert.IsTrue(presentation.CanExecute);
        Assert.IsFalse(presentation.RequiresUserOverride);
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.AreEqual(1, fixture.GameService.StartCount);
    }

    [TestMethod]
    public async Task ManagedRecordForAnotherGameRootDoesNotBlockUnmoddedLaunch()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var managedGameDirectory = CreateGameDirectory(temporaryDirectory, "managed-game");
        var selectedGameDirectory = CreateGameDirectory(temporaryDirectory, "selected-game");
        var fixture = CreateFixture(temporaryDirectory);
        await InstallManagedArtifactAsync(fixture.DeploymentService, managedGameDirectory);

        var presentation = fixture.Coordinator.CapturePresentation(
            selectedGameDirectory,
            LauncherLaunchTarget.PrimeExecutable);
        var result = await fixture.Coordinator.LaunchAsync(
            selectedGameDirectory,
            LauncherLaunchTarget.PrimeExecutable);

        Assert.AreEqual("Ready without mod", presentation.Status);
        Assert.IsFalse(presentation.RequiresUserOverride);
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.AreEqual(1, fixture.GameService.StartCount);
    }

    [TestMethod]
    public async Task ChangedManagedProxyRequiresFreshExplicitApproval()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);
        await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "version.dll"), [9, 9, 9]);

        var presentation = fixture.Coordinator.CapturePresentation(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable);
        var refused = await fixture.Coordinator.LaunchAsync(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable);

        Assert.AreEqual("Mod needs attention", presentation.Status);
        Assert.IsTrue(presentation.CanExecute);
        Assert.IsTrue(presentation.RequiresUserOverride);
        StringAssert.Contains(presentation.Reason, "cannot vouch");
        Assert.AreEqual(GameLaunchHandoffState.Blocked, refused.State);
        Assert.AreEqual(0, fixture.GameService.StartCount);

        var approved = await fixture.Coordinator.LaunchAsync(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable,
            allowUnverifiedProxy: true);

        Assert.AreEqual(GameLaunchHandoffState.Completed, approved.State);
        Assert.AreEqual(1, fixture.GameService.StartCount);
        CollectionAssert.AreEqual(
            new byte[] { 9, 9, 9 },
            await File.ReadAllBytesAsync(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task UnmanagedProxyRequiresApprovalWithoutBecomingManagedOrTrusted()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var proxyPath = Path.Combine(gameDirectory, "version.dll");
        await File.WriteAllBytesAsync(proxyPath, [4, 2]);
        var fixture = CreateFixture(temporaryDirectory);

        var presentation = fixture.Coordinator.CapturePresentation(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable);
        var result = await fixture.Coordinator.LaunchAsync(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable,
            allowUnverifiedProxy: true);

        Assert.IsTrue(presentation.CanExecute);
        Assert.IsTrue(presentation.RequiresUserOverride);
        StringAssert.Contains(presentation.Reason, "did not install or record");
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        CollectionAssert.AreEqual(new byte[] { 4, 2 }, await File.ReadAllBytesAsync(proxyPath));
        Assert.IsNull(fixture.DeploymentService.ReadInstalledState());
    }

    [TestMethod]
    public async Task ScopelyLauncherIsIndependentOfGameFolderAndGameProcess()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CreateFixture(temporaryDirectory, isGameRunning: true);

        var presentation = fixture.Coordinator.CapturePresentation(null, LauncherLaunchTarget.ScopelyLauncher);
        var launchTask = fixture.Coordinator.LaunchAsync(null, LauncherLaunchTarget.ScopelyLauncher);
        await fixture.ScopelyService.WaitUntilStartedAsync();
        fixture.ScopelyService.CompleteExit();
        var result = await launchTask;

        Assert.IsTrue(presentation.CanExecute);
        Assert.AreEqual("Open Scopely launcher", presentation.ActionLabel);
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(1, fixture.ScopelyService.StartCount);
        Assert.AreEqual(0, fixture.GameService.StartCount);
    }

    [TestMethod]
    public async Task ScopelyLauncherStillOpensWhenPersistedGameFolderIsUnavailable()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CreateFixture(temporaryDirectory);
        var state = Path.GetDirectoryName(fixture.DeploymentService.JournalPath)!;
        var unavailableGame = Path.Combine(temporaryDirectory.Path, "moved-game");
        new JsonGameInstallSelectionStore(state).Save(unavailableGame);

        var presentation = fixture.Coordinator.CapturePresentation(null, LauncherLaunchTarget.ScopelyLauncher);
        var launchTask = fixture.Coordinator.LaunchAsync(null, LauncherLaunchTarget.ScopelyLauncher);
        await fixture.ScopelyService.WaitUntilStartedAsync();
        fixture.ScopelyService.CompleteExit();
        var result = await launchTask;

        Assert.IsTrue(presentation.CanExecute);
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.AreEqual(1, fixture.ScopelyService.StartCount);
        Assert.AreEqual(0, fixture.GameService.StartCount);
    }

    [TestMethod]
    public async Task ScopelyLauncherStillOpensWhenSavedGameSelectionIsUnreadable()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CreateFixture(temporaryDirectory);
        var state = Path.GetDirectoryName(fixture.DeploymentService.JournalPath)!;
        File.WriteAllText(Path.Combine(state, "install-selection.json"), "{ broken json");

        var presentation = fixture.Coordinator.CapturePresentation(null, LauncherLaunchTarget.ScopelyLauncher);
        var launchTask = fixture.Coordinator.LaunchAsync(null, LauncherLaunchTarget.ScopelyLauncher);
        await fixture.ScopelyService.WaitUntilStartedAsync();
        fixture.ScopelyService.CompleteExit();
        var result = await launchTask;

        Assert.IsTrue(presentation.CanExecute);
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.AreEqual(1, fixture.ScopelyService.StartCount);
        Assert.AreEqual(0, fixture.GameService.StartCount);
    }

    [TestMethod]
    public void IncompleteDeploymentBlocksScopelyWhenGameTargetIsKnown()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);
        var transactionId = Guid.NewGuid().ToString("N");
        var stateDirectory = Path.GetDirectoryName(fixture.DeploymentService.JournalPath)!;
        var journal = new ModDeploymentJournal(
            1,
            transactionId,
            ModDeploymentOperation.Deploy,
            ModDeploymentPhase.Committing,
            gameDirectory,
            ReleaseArtifact(),
            Path.Combine(gameDirectory, $".version.dll.{transactionId}.stage"),
            Path.Combine(gameDirectory, $".version.dll.{transactionId}.rollback"),
            Path.Combine(stateDirectory, "rollback", transactionId, "version.dll"),
            HadExistingArtifact: false,
            PreviousInstalledState: null,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            fixture.DeploymentService.JournalPath,
            JsonSerializer.Serialize(journal, WebJsonOptions));

        var presentation = fixture.Coordinator.CapturePresentation(
            null,
            LauncherLaunchTarget.ScopelyLauncher);

        Assert.IsFalse(presentation.CanExecute);
        Assert.AreEqual("Recovery required", presentation.Status);
        Assert.AreEqual(LauncherLaunchRecoveryAction.RecoverModTransaction, presentation.NextAction);
    }

    [TestMethod]
    public void MalformedDeploymentJournalBlocksEveryLaunchTargetWithoutThrowing()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);
        File.WriteAllText(fixture.DeploymentService.JournalPath, "{\"phase\":");

        var prime = fixture.Coordinator.CapturePresentation(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable);
        var scopely = fixture.Coordinator.CapturePresentation(
            null,
            LauncherLaunchTarget.ScopelyLauncher);

        Assert.IsFalse(prime.CanExecute);
        Assert.IsFalse(scopely.CanExecute);
        Assert.AreEqual("Recovery required", prime.Status);
        Assert.AreEqual("Recovery required", scopely.Status);
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
    public async Task UnattributablePrimeBlocksLaunchAsAttentionWithoutStartingAnything()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(
            temporaryDirectory,
            gameProcessState: GameProcessInspectionState.Unattributable);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);

        var presentation = fixture.Coordinator.CapturePresentation(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable);
        var result = await fixture.Coordinator.LaunchAsync(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable);

        Assert.AreEqual("Needs attention", presentation.Status);
        Assert.AreEqual(LauncherHomeTone.Warning, presentation.Tone);
        Assert.IsFalse(presentation.CanExecute);
        StringAssert.Contains(presentation.Reason, "could not be attributed safely");
        Assert.AreEqual(LauncherLaunchRecoveryAction.CloseRunningGame, presentation.NextAction);
        Assert.AreEqual(GameLaunchHandoffState.Blocked, result.State);
        Assert.AreEqual(0, fixture.GameService.StartCount);
        Assert.AreEqual(0, fixture.ScopelyService.StartCount);
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
        Assert.AreEqual(LauncherLaunchRecoveryAction.SelectGameFolder, prime.NextAction);
        Assert.AreEqual(LauncherLaunchRecoveryAction.InstallOrRepairScopelyLauncher, scopely.NextAction);
        Assert.IsFalse(string.IsNullOrWhiteSpace(prime.Reason));
        Assert.IsFalse(string.IsNullOrWhiteSpace(scopely.Reason));
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
        Assert.AreEqual("Mod needs attention", result.Presentation.Status);
        Assert.IsTrue(result.Presentation.RequiresUserOverride);
    }

    [TestMethod]
    public async Task ScopelyLaunchHoldsOperationLockUntilTrackedProcessExits()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);

        var launchTask = fixture.Coordinator.LaunchAsync(null, LauncherLaunchTarget.ScopelyLauncher);
        await fixture.ScopelyService.WaitUntilStartedAsync();
        var concurrentDeployment = await fixture.DeploymentService.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Busy, concurrentDeployment.State);
        fixture.ScopelyService.CompleteExit();
        Assert.AreEqual(GameLaunchHandoffState.Completed, (await launchTask).State);
    }

    [TestMethod]
    public async Task ScopelyAvailabilityIsRevalidatedInsideOperationLock()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CreateFixture(temporaryDirectory, scopelyAvailabilityReadsBeforeMissing: 1);

        var result = await fixture.Coordinator.LaunchAsync(null, LauncherLaunchTarget.ScopelyLauncher);

        Assert.AreEqual(GameLaunchHandoffState.Blocked, result.State);
        Assert.AreEqual(0, fixture.ScopelyService.StartCount);
        Assert.AreEqual(
            LauncherLaunchRecoveryAction.InstallOrRepairScopelyLauncher,
            result.Presentation.NextAction);
    }

    [TestMethod]
    public async Task AlreadyRunningScopelyLauncherReportsTruthfulNoChange()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CreateFixture(
            temporaryDirectory,
            scopelyStartKind: OfficialLauncherStartKind.ReusedRunning);

        var launchTask = fixture.Coordinator.LaunchAsync(null, LauncherLaunchTarget.ScopelyLauncher);
        await fixture.ScopelyService.WaitUntilStartedAsync();
        fixture.ScopelyService.CompleteExit();
        var result = await launchTask;

        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.IsFalse(result.Changed);
        StringAssert.Contains(result.Message, "already running");
        StringAssert.Contains(result.Message, "no new process");
    }

    [TestMethod]
    public async Task WindowsServiceSurfacesReusedLauncherAndRetainsExactExistingLifetime()
    {
        var existing = new RecordingOfficialLauncherProcess();
        var activation = new RecordingOfficialLauncherProcess();
        var activationCount = 0;
        var service = new WindowsOfficialLauncherService(
            () => true,
            () => existing,
            () =>
            {
                activationCount++;
                return activation;
            });

        var result = await service.StartAsync(CancellationToken.None);

        Assert.AreEqual(OfficialLauncherStartKind.ReusedRunning, result.Kind);
        Assert.IsFalse(result.Changed);
        Assert.AreSame(existing, result.Process);
        Assert.AreEqual(1, activationCount, "The executable must still be invoked to surface its existing UI.");
        Assert.AreEqual(1, activation.DisposeCount, "The newly returned activation handle must be released immediately.");
        Assert.AreEqual(0, existing.DisposeCount, "The exact existing process remains the handoff lifetime boundary.");

        await result.Process.DisposeAsync();
        Assert.AreEqual(1, existing.DisposeCount);
    }

    [TestMethod]
    public async Task DeploymentReadFailureProjectsStablePathFreeRecoveryReason()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory, "sensitive-player-folder");
        var fixture = CreateFixture(temporaryDirectory);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);
        var artifactPath = Path.Combine(gameDirectory, "version.dll");
        using var exclusiveLock = new FileStream(
            artifactPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var presentation = fixture.Coordinator.CapturePresentation(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable);

        StringAssert.Contains(presentation.Reason, "could not verify");
        Assert.AreEqual(LauncherLaunchRecoveryAction.OpenDiagnostics, presentation.NextAction);
        Assert.IsTrue(presentation.CanExecute);
        Assert.IsTrue(presentation.RequiresUserOverride);
        Assert.IsFalse(presentation.Reason.Contains(gameDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(presentation.AutomationName.Contains(gameDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(presentation.AutomationName.Contains("sensitive-player-folder", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task CapturedInstallationEvidenceAvoidsRepeatArtifactValidationForReadOnlyProjection()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var fixture = CreateFixture(temporaryDirectory);
        await InstallManagedArtifactAsync(fixture.DeploymentService, gameDirectory);
        var artifactPath = Path.Combine(gameDirectory, "version.dll");
        using var exclusiveLock = new FileStream(
            artifactPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var capturedInstallation = new ModInstallationEvidence(
            ModInstallationEvidenceState.ManagedVerified,
            IsGameRunning: false,
            InstalledVersion: "2.1.0.8",
            InstalledSha256: ReleaseArtifact().Sha256);

        var presentation = fixture.Coordinator.CapturePresentation(
            gameDirectory,
            LauncherLaunchTarget.PrimeExecutable,
            capturedInstallation);

        Assert.IsTrue(presentation.CanExecute);
        Assert.AreEqual("Ready to play", presentation.Status);
    }

    private static Fixture CreateFixture(
        TemporaryDirectory temporaryDirectory,
        bool gameAvailable = true,
        bool scopelyAvailable = true,
        bool isGameRunning = false,
        Exception? scopelyFailure = null,
        OfficialLauncherStartKind scopelyStartKind = OfficialLauncherStartKind.StartedNew,
        int? scopelyAvailabilityReadsBeforeMissing = null,
        GameProcessInspectionState? gameProcessState = null,
        NativeLauncherProfilesStore? profileStore = null)
    {
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var deploymentService = new ModDeploymentService(
            stateDirectory,
            new FakeDownloader(),
            new FakeVersionReader(),
            new FakeAuthenticityVerifier(),
            _ => false,
            new("guffawaffle", "stable", "guffawaffle.windows"));
        var gameService = new FakeGameExecutableLaunchService(gameAvailable);
        var scopelyService = new FakeOfficialLauncherService(
            scopelyAvailable,
            scopelyFailure,
            scopelyStartKind,
            scopelyAvailabilityReadsBeforeMissing);
        var coordinator = new GameLaunchHandoffCoordinator(
            stateDirectory,
            deploymentService,
            gameService,
            scopelyService,
            new FakeGameProcessInspector(
                gameProcessState
                    ?? (isGameRunning
                        ? GameProcessInspectionState.RunningTarget
                        : GameProcessInspectionState.NotRunning)),
            profileStore ?? new NativeLauncherProfilesStore(stateDirectory, new NamedCatalog(NamedProfile(string.Empty))));
        return new(coordinator, deploymentService, gameService, scopelyService);
    }

    private static async Task InstallManagedArtifactAsync(ModDeploymentService deploymentService, string gameDirectory)
    {
        var result = await deploymentService.DeployAsync(gameDirectory, ReleaseArtifact(), ExistingArtifactPolicy.Reject);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
    }

    private static string CreateGameDirectory(
        TemporaryDirectory temporaryDirectory,
        string directoryName = "game")
    {
        var gameDirectory = temporaryDirectory.CreateDirectory(directoryName);
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        return gameDirectory;
    }

    private static ModReleaseArtifact ReleaseArtifact() => new(
        new Uri("https://example.invalid/version.dll"),
        "version.dll",
        ArtifactContents.LongLength,
        Convert.ToHexString(SHA256.HashData(ArtifactContents)),
        "2.1.0.8");

    private static LauncherProfile NamedProfile(string game) => new(
        "0123456789abcdef0123456789abcdef", "Science", game, Revision: "current-profile");

    [TestMethod]
    public async Task ExplicitDefaultLaunchUsesOrdinaryPreferencesWithoutIsolationProbe()
    {
        using var temporary = new TemporaryDirectory();
        var game = CreateGameDirectory(temporary);
        var store = new NativeLauncherProfilesStore(temporary.CreateDirectory("state"), new NamedCatalog(NamedProfile(game)));
        var profile = store.EnsureDefault();
        await store.SelectAsync(profile.Id);
        var fixture = CreateFixture(temporary, profileStore: store);
        var result = await fixture.Coordinator.LaunchProfileAsync(profile, defaultGameDirectory: game);
        Assert.AreEqual(GameLaunchHandoffState.Completed, result.State);
        Assert.AreEqual(1, fixture.GameService.StartCount);
        Assert.AreEqual(game, fixture.GameService.LastGameDirectory);
        Assert.AreEqual(0, fixture.GameService.LastArguments!.Count);
    }

    [TestMethod]
    public async Task PhysicalDefaultLaunchResolvesJunctionWithoutChangingSavedProfileBinding()
    {
        using var temporary = new TemporaryDirectory();
        var parent = temporary.CreateDirectory("installation-parent");
        var game = Path.Combine(parent, "game");
        Directory.CreateDirectory(Path.Combine(game, "prime_Data"));
        foreach (var file in new[] { "prime.exe", "GameAssembly.dll", "UnityPlayer.dll" })
            File.WriteAllBytes(Path.Combine(game, file), [1, 2, 3]);
        File.WriteAllText(Path.Combine(game, ".version"), "&game=221");
        var alias = Path.Combine(temporary.CreateDirectory("aliases"), "installation");
        var script = $"$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '{alias.Replace("'", "''")}' -Target '{parent.Replace("'", "''")}' | Out-Null";
        var start = new System.Diagnostics.ProcessStartInfo(@"C:\Program Files\PowerShell\7\pwsh.exe")
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-EncodedCommand",
            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        using var helper = System.Diagnostics.Process.Start(start)!;
        await helper.WaitForExitAsync(); Assert.AreEqual(0, helper.ExitCode);
        try
        {
            var store = new NativeLauncherProfilesStore(temporary.CreateDirectory("ui"),
                NativeProfileCatalogIntegrationTests.Transport(), temporary.CreateDirectory("catalog"));
            var profile = store.EnsureDefault(); await store.SelectAsync(profile.Id);
            var fixture = CreateFixture(temporary, profileStore: store);
            var result = await fixture.Coordinator.LaunchProfileAsync(profile,
                defaultGameDirectory: Path.Combine(alias, "game"));
            Assert.AreEqual(GameLaunchHandoffState.Completed, result.State, result.Message);
            Assert.IsTrue(GameDirectoryIdentity.SameLocation(game, fixture.GameService.LastGameDirectory!));
            Assert.AreEqual("", store.Load().Snapshot!.SelectedProfile!.PreferredInstallationId);
            Assert.AreEqual("", store.Load().Snapshot!.SelectedProfile!.GameDirectory);
        }
        finally { Directory.Delete(alias); }
    }

    [TestMethod]
    public async Task DefaultRechecksTheResolvedTargetUnderCustodyBeforeStarting()
    {
        using var temporary = new TemporaryDirectory();
        var initiallyChecked = CreateGameDirectory(temporary, "initial");
        var resolved = CreateGameDirectory(temporary, "resolved");
        File.WriteAllBytes(Path.Combine(resolved, "version.dll"), [9, 9, 9]);
        var transport = new NamedCatalog(NamedProfile(initiallyChecked)) { RegisteredGameDirectory = resolved };
        var store = new NativeLauncherProfilesStore(temporary.CreateDirectory("state"), transport);
        var profile = store.EnsureDefault(); await store.SelectAsync(profile.Id);
        var fixture = CreateFixture(temporary, profileStore: store);
        var result = await fixture.Coordinator.LaunchProfileAsync(profile, defaultGameDirectory: initiallyChecked);
        Assert.AreEqual(GameLaunchHandoffState.Blocked, result.State, result.Message);
        Assert.IsTrue(result.Presentation.RequiresUserOverride);
        Assert.AreEqual(0, fixture.GameService.StartCount);
        Assert.AreEqual(1, transport.Requests.Count(request => request.Operation == "register-installation"));
    }

    [TestMethod]
    public async Task DefaultCannotLaunchBesideAnUnattributedRunningTarget()
    {
        using var temporary = new TemporaryDirectory();
        var game = CreateGameDirectory(temporary);
        var store = new NativeLauncherProfilesStore(temporary.CreateDirectory("state"), new NamedCatalog(NamedProfile(game)));
        var profile = store.EnsureDefault();
        await store.SelectAsync(profile.Id);
        var fixture = CreateFixture(temporary, gameProcessState: GameProcessInspectionState.RunningTarget, profileStore: store);
        var result = await fixture.Coordinator.LaunchProfileAsync(profile, defaultGameDirectory: game);
        Assert.AreEqual(GameLaunchHandoffState.Blocked, result.State);
        Assert.AreEqual(0, fixture.GameService.StartCount);
    }

    [TestMethod]
    public void SessionFocusRejectsWrongExecutableAndDoesNotInferOrdinaryProfileFromCatalog()
    {
        using var temporary = new TemporaryDirectory();
        var game = CreateGameDirectory(temporary);
        var fixture = CreateFixture(temporary);
        var profile = NamedProfile(game);
        var incorrect = new ProfileSession(profile.Id, Environment.ProcessId, "ready", game,
            ProcessStartUtcTicks: 1, ExecutablePath: Path.Combine(game, "prime.exe"));
        Assert.AreEqual(0, fixture.Coordinator.CaptureSessions(profile, [incorrect]).Count);
        Assert.AreEqual(GameLaunchHandoffState.Blocked, fixture.Coordinator.FocusSession(profile, incorrect).State);
        var builtIn = profile with { Kind = "windows-user", BuiltIn = true };
        Assert.AreEqual(0, fixture.Coordinator.CaptureSessions(builtIn, [incorrect with { Readiness = "ordinary" }]).Count);
    }

    private sealed class NamedCatalog(LauncherProfile profile) : IProfileCatalogTransport, IProfileInstallationLeaseTransport
    {
        private RegisteredGameInstallation? installation;
        public string? RegisteredGameDirectory { get; init; }
        public List<ProfileCatalogRequest> Requests { get; } = [];
        public ProfileCatalogResponse LaunchResult { get; set; } = new(true, Profile: profile,
            ProcessId: 123, Readiness: "ready");
        public IDisposable AcquireInstallationLease(ProfileCatalogRequest request) => new NoopLease();
        private sealed class NoopLease : IDisposable { public void Dispose() { } }
        public ProfileCatalogResponse Request(ProfileCatalogRequest request)
        {
            Requests.Add(request);
            if (request.Operation == "register-installation")
            {
                installation = new("cccccccccccccccccccccccccccccccc", request.Name!, RegisteredGameDirectory ?? request.GameDirectory!,
                    new string('e', 64), "available", "synthetic-registration");
                return new(true, RegisteredInstallation: installation);
            }
            if (request.Operation == "installation-paths") return new(true, RegisteredInstallation: installation);
            var builtIn = new LauncherProfile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Default", "", Revision: "default-revision",
                Kind: "windows-user", OwnerUserId: "S-1-5-21-test", BuiltIn: true);
            if (request.Operation is "ensure-default" or "resolve-default") return new(true, Profile: builtIn);
            return request.Operation == "launch" ? LaunchResult
                : new(true, Profile: profile, Profiles: [builtIn, profile], Revision: "current-catalog");
        }
    }

    private sealed record Fixture(
        GameLaunchHandoffCoordinator Coordinator,
        ModDeploymentService DeploymentService,
        FakeGameExecutableLaunchService GameService,
        FakeOfficialLauncherService ScopelyService);

    private sealed class FakeGameProcessInspector(GameProcessInspectionState state) : IGameProcessInspector
    {
        public GameProcessInspectionState Inspect(string gameDirectory) => state;
    }

    private sealed class FakeGameExecutableLaunchService(bool isAvailable) : IGameExecutableLaunchService
    {
        public int StartCount { get; private set; }

        public string? LastGameDirectory { get; private set; }

        public IReadOnlyList<string>? LastArguments { get; private set; }

        public Action? OnStart { get; set; }

        public bool IsAvailable(string gameDirectory) => isAvailable;

        public Task<IGameExecutableProcess> StartAsync(string gameDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            LastGameDirectory = gameDirectory;
            LastArguments = arguments;
            OnStart?.Invoke();
            return Task.FromResult<IGameExecutableProcess>(new CompletedGameProcess());
        }
        private sealed class CompletedGameProcess : IGameExecutableProcess
        {
            public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeOfficialLauncherService(
        bool isAvailable,
        Exception? failure,
        OfficialLauncherStartKind startKind,
        int? availabilityReadsBeforeMissing) : IOfficialLauncherService
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int availabilityReadCount;

        public bool IsAvailable =>
            isAvailable
            && (availabilityReadsBeforeMissing is null
                || availabilityReadCount++ < availabilityReadsBeforeMissing.Value);

        public int StartCount { get; private set; }

        public Task<OfficialLauncherStartResult> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            started.TrySetResult();
            return failure is null
                ? Task.FromResult(
                    new OfficialLauncherStartResult(
                        startKind,
                        new FakeOfficialLauncherProcess(exited.Task)))
                : Task.FromException<OfficialLauncherStartResult>(failure);
        }

        public Task WaitUntilStartedAsync() => started.Task;

        public void CompleteExit() => exited.TrySetResult();

        private sealed class FakeOfficialLauncherProcess(Task exitTask) : IOfficialLauncherProcess
        {
            public Task WaitForExitAsync(CancellationToken cancellationToken) => exitTask.WaitAsync(cancellationToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOfficialLauncherProcess : IOfficialLauncherProcess
    {
        public int DisposeCount { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
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
