using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ModDeploymentServiceTests
{
    private static readonly byte[] ArtifactContents = [0x53, 0x54, 0x46, 0x43, 0x2d, 0x4d, 0x4f, 0x44];
    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [TestMethod]
    public async Task VerifiedArtifactCommitsAndRecordsManagedState()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            installationAttribution: new("guffawaffle", "stable", "guffawaffle.windows"));

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
        Assert.AreEqual("Community Mod installed successfully.", result.Message);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual(ModDeploymentPhase.Committed, service.ReadJournal()!.Phase);
        var installedState = service.ReadInstalledState()!;
        Assert.AreEqual(ReleaseArtifact().Sha256, installedState.Sha256);
        Assert.AreEqual("guffawaffle", installedState.ProviderId);
        Assert.AreEqual("stable", installedState.ReleaseChannelId);
        Assert.AreEqual("guffawaffle.windows", installedState.RuntimeDistributionId);
        Assert.IsFalse(Directory.EnumerateFiles(gameDirectory, "*.stage").Any());
        Assert.IsFalse(Directory.EnumerateFiles(gameDirectory, "*.rollback").Any());
    }

    [TestMethod]
    public void UnattributedInstalledStateFailsClosed()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var legacyState = new
        {
            schemaVersion = 1,
            gameDirectory = Path.GetFullPath(gameDirectory),
            fileName = "version.dll",
            version = "2.1.0.8",
            size = ArtifactContents.LongLength,
            sha256 = ReleaseArtifact().Sha256,
            installedAtUtc = DateTimeOffset.UtcNow,
            previousArtifactBackupPath = (string?)null,
        };
        File.WriteAllText(
            Path.Combine(stateDirectory, "installed-mod.json"),
            JsonSerializer.Serialize(legacyState, JournalJsonOptions));
        var service = new ModDeploymentService(
            stateDirectory,
            new FakeDownloader(SuccessfulDownload()),
            new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            new FakeAuthenticityVerifier(true),
            _ => false,
            DefaultAttribution());

        Assert.ThrowsException<InvalidDataException>(() => service.ReadInstalledState());
    }

    [TestMethod]
    public void LegacyReceiptProjectsToItsRecordedInstallationWithoutPassiveRewrite()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var managedGameDirectory = CreateGameDirectory(temporaryDirectory, "managed-game");
        var selectedGameDirectory = CreateGameDirectory(temporaryDirectory, "selected-game");
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        var state = InstalledState(managedGameDirectory);
        var original = JsonSerializer.SerializeToUtf8Bytes(state, JournalJsonOptions);
        File.WriteAllBytes(service.InstalledStatePath, original);

        var selected = service.ReadInstalledState(selectedGameDirectory);
        var managed = service.ReadInstalledState(managedGameDirectory);

        Assert.IsNull(selected);
        Assert.AreEqual(state, managed);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(service.InstalledStatePath));
    }

    [TestMethod]
    public void RegistryRejectsDuplicateCanonicalInstallationPaths()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        var state = InstalledState(gameDirectory);
        var duplicate = state with
        {
            GameDirectory = gameDirectory + Path.DirectorySeparatorChar,
        };
        File.WriteAllText(
            service.InstalledStatePath,
            JsonSerializer.Serialize(
                new ModInstalledArtifactRegistry(2, [state, duplicate], []),
                JournalJsonOptions));

        Assert.ThrowsException<InvalidDataException>(() => service.ReadInstalledStates());
    }

    [TestMethod]
    public void RegistryRejectsDetachmentIdsThatDifferOnlyByCase()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        var backupPath = Path.Combine(
            temporaryDirectory.Path,
            "state",
            "rollback",
            "detached",
            "version.dll");
        var detachmentId = Guid.NewGuid().ToString("N");
        var detached = new ModDetachedAdoptionBackupState(
            detachmentId,
            gameDirectory,
            DateTimeOffset.UtcNow,
            "guffawaffle",
            "stable",
            "guffawaffle.windows",
            backupPath,
            new(1, ReleaseArtifact().Sha256),
            PreviousRuntimeManifestBackupPath: null,
            PreviousRuntimeManifestBackupIdentity: null);
        File.WriteAllText(
            service.InstalledStatePath,
            JsonSerializer.Serialize(
                new ModInstalledArtifactRegistry(
                    2,
                    [],
                    [detached, detached with { DetachmentId = detachmentId.ToUpperInvariant() }]),
                JournalJsonOptions));

        Assert.ThrowsException<InvalidDataException>(() => service.ReadInstalledStates());
    }

    [TestMethod]
    public async Task InstallAndRemoveSecondInstallationPreserveChangedFirstInstallation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var firstGameDirectory = CreateGameDirectory(temporaryDirectory, "first-game");
        var secondGameDirectory = CreateGameDirectory(temporaryDirectory, "second-game");
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                firstGameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        var firstReceipt = service.ReadInstalledState(firstGameDirectory)!;
        var changedFirstBytes = new byte[] { 0x44, 0x45, 0x56 };
        File.WriteAllBytes(Path.Combine(firstGameDirectory, "version.dll"), changedFirstBytes);

        var secondInstall = await service.DeployAsync(
            secondGameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, secondInstall.State, secondInstall.Message);
        Assert.AreEqual(firstReceipt, service.ReadInstalledState(firstGameDirectory));
        Assert.IsNotNull(service.ReadInstalledState(secondGameDirectory));
        Assert.AreEqual(2, service.ReadInstalledStates().Count);
        CollectionAssert.AreEqual(
            changedFirstBytes,
            File.ReadAllBytes(Path.Combine(firstGameDirectory, "version.dll")));

        var secondRemoval = await service.UninstallAsync(secondGameDirectory);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, secondRemoval.State, secondRemoval.Message);
        Assert.AreEqual(firstReceipt, service.ReadInstalledState(firstGameDirectory));
        Assert.IsNull(service.ReadInstalledState(secondGameDirectory));
        Assert.AreEqual(1, service.ReadInstalledStates().Count);
        CollectionAssert.AreEqual(
            changedFirstBytes,
            File.ReadAllBytes(Path.Combine(firstGameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task RunningGameDeniesMutationBeforeDownload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var downloader = new FakeDownloader(SuccessfulDownload());
        var service = CreateService(temporaryDirectory, downloader, isGameRunning: _ => true);

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.GameRunning, result.State);
        Assert.AreEqual(0, downloader.CallCount);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task GameStartingWhileDeploymentAcquiresLeaseBlocksBeforeDownload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var downloader = new FakeDownloader(SuccessfulDownload());
        var checks = 0;
        var service = CreateService(
            temporaryDirectory,
            downloader,
            isGameRunning: _ => ++checks > 1);

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.GameRunning, result.State);
        Assert.AreEqual(0, downloader.CallCount);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task ExistingArtifactRequiresExplicitAdoption()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var existing = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), existing);
        var downloader = new FakeDownloader(SuccessfulDownload());
        var service = CreateService(temporaryDirectory, downloader);

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.ExistingArtifactRequiresAdoption, result.State);
        Assert.AreEqual(0, downloader.CallCount);
        CollectionAssert.AreEqual(existing, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task AdoptionRetainsRecoverablePreviousArtifact()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var existing = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), existing);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.AdoptAndPreserve);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
        var backupPath = service.ReadInstalledState()!.PreviousArtifactBackupPath;
        Assert.IsNotNull(backupPath);
        Assert.IsTrue(File.Exists(backupPath));
        CollectionAssert.AreEqual(existing, File.ReadAllBytes(backupPath));
    }

    [TestMethod]
    public async Task LauncherManagedUpdateDoesNotRequireReadoption()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var firstService = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await firstService.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        var updatedContents = new byte[] { 9, 8, 7, 6 };
        var updatedArtifact = new ModReleaseArtifact(
            new Uri("https://example.invalid/version.dll"),
            "version.dll",
            updatedContents.LongLength,
            Convert.ToHexString(SHA256.HashData(updatedContents)),
            "2.1.0.9");
        var updateService = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, updatedContents, updatedContents.LongLength),
            versionReader: new FakeVersionReader(updatedArtifact.ExpectedVersion));

        var result = await updateService.DeployAsync(
            gameDirectory,
            updatedArtifact,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
        CollectionAssert.AreEqual(updatedContents, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        Assert.IsNull(updateService.ReadInstalledState()!.PreviousArtifactBackupPath);
    }

    [TestMethod]
    public async Task ManagedUpdatePreservesOriginalAdoptedArtifactForUninstall()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var originalManualArtifact = new byte[] { 1, 3, 3, 7 };
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), originalManualArtifact);
        var installService = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installService.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.AdoptAndPreserve)).State);

        var updatedContents = new byte[] { 9, 8, 7, 6 };
        var updatedArtifact = new ModReleaseArtifact(
            new Uri("https://example.invalid/version.dll"),
            "version.dll",
            updatedContents.LongLength,
            Convert.ToHexString(SHA256.HashData(updatedContents)),
            "2.1.0.9");
        var updateService = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, updatedContents, updatedContents.LongLength),
            versionReader: new FakeVersionReader(updatedArtifact.ExpectedVersion));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await updateService.DeployAsync(
                gameDirectory,
                updatedArtifact,
                ExistingArtifactPolicy.Reject)).State);

        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await updateService.UninstallAsync(gameDirectory)).State);
        CollectionAssert.AreEqual(
            originalManualArtifact,
            File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task ExplicitRepairReplacesChangedManagedArtifactTransactionally()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), [4, 2]);

        var result = await service.RepairAsync(gameDirectory, ReleaseArtifact());

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
    }

    [DataTestMethod]
    [DataRow(HttpStatusCode.NotFound, null, ModDeploymentResultState.DownloadRejected)]
    [DataRow(HttpStatusCode.OK, 9L, ModDeploymentResultState.VerificationFailed)]
    public async Task HttpAndDeclaredSizeFailuresDoNotReachTheTarget(
        HttpStatusCode statusCode,
        long? declaredLength,
        ModDeploymentResultState expectedState)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var service = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(statusCode, ArtifactContents, declaredLength));

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(expectedState, result.State);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual(ModDeploymentPhase.Failed, service.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task HashMismatchFailsClosed()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        var artifact = ReleaseArtifact() with { Sha256 = new string('0', 64) };

        var result = await service.DeployAsync(
            gameDirectory,
            artifact,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task ActualSizeMismatchFailsClosed()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var service = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, [.. ArtifactContents, 0xFF]));

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task VersionMismatchRestoresAdoptedArtifact()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var existing = new byte[] { 7, 8, 9 };
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), existing);
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            versionReader: new FakeVersionReader("unexpected"));

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.AdoptAndPreserve);

        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State);
        CollectionAssert.AreEqual(existing, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual(ModDeploymentPhase.RolledBack, service.ReadJournal()!.Phase);
        Assert.IsNull(service.ReadInstalledState());
    }

    [DataTestMethod]
    [DataRow(ModDeploymentPhase.Planned)]
    [DataRow(ModDeploymentPhase.Downloading)]
    [DataRow(ModDeploymentPhase.Verified)]
    [DataRow(ModDeploymentPhase.Staged)]
    [DataRow(ModDeploymentPhase.Committing)]
    [DataRow(ModDeploymentPhase.CleanupPending)]
    public async Task FaultAtEveryPersistedBoundaryRestoresPreviousArtifact(ModDeploymentPhase faultPhase)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var existing = new byte[] { 0xBA, 0xC0 };
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), existing);
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterPhasePersisted: (phase, _) =>
            {
                if (phase == faultPhase)
                {
                    throw new InjectedDeploymentFaultException(phase);
                }
                return ValueTask.CompletedTask;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.AdoptAndPreserve);

        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State);
        CollectionAssert.AreEqual(existing, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual(ModDeploymentPhase.RolledBack, service.ReadJournal()!.Phase);
        Assert.IsFalse(Directory.EnumerateFiles(gameDirectory, "*.stage").Any());
        Assert.IsFalse(Directory.EnumerateFiles(gameDirectory, "*.rollback").Any());
    }

    [TestMethod]
    public async Task CoordinatedParticipantFailureRestoresManagedArtifactAndState()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var sourceService = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            installationAttribution: new("guffawaffle", "stable", "guffawaffle.windows"));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await sourceService.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        var targetContents = new byte[] { 0x4e, 0x45, 0x54, 0x4e, 0x49, 0x56 };
        var targetArtifact = ReleaseArtifact(targetContents, "1.1.5.1");
        var targetService = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, targetContents, targetContents.LongLength),
            versionReader: new FakeVersionReader(targetArtifact.ExpectedVersion),
            installationAttribution: new("netniv", "stable", "netniv.stfc-community-mod"));
        var participant = new FakeCommitParticipant(failCommit: true);

        var result = await targetService.DeployCoordinatedAsync(
            gameDirectory,
            targetArtifact,
            ExistingArtifactPolicy.Reject,
            Guid.NewGuid().ToString("N"),
            participant);

        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual("guffawaffle", targetService.ReadInstalledState()!.ProviderId);
        Assert.AreEqual(1, participant.CommitCount);
        Assert.AreEqual(1, participant.RollbackCount);
        Assert.IsFalse(Directory.EnumerateFiles(gameDirectory, "*.rollback").Any());
    }

    [TestMethod]
    public async Task DeploymentFinalizationFailureLeavesCommittedStateForRecovery()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var sourceService = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await sourceService.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        var targetContents = new byte[] { 0x4e, 0x45, 0x54, 0x4e, 0x49, 0x56 };
        var targetArtifact = ReleaseArtifact(targetContents, "1.1.5.1");
        var targetService = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, targetContents, targetContents.LongLength),
            versionReader: new FakeVersionReader(targetArtifact.ExpectedVersion),
            afterPhasePersisted: (phase, _) => phase == ModDeploymentPhase.Committed
                ? ValueTask.FromException(new InjectedDeploymentFaultException(phase))
                : ValueTask.CompletedTask,
            installationAttribution: new("netniv", "stable", "netniv.stfc-community-mod"));
        var participant = new FakeCommitParticipant();

        var result = await targetService.DeployCoordinatedAsync(
            gameDirectory,
            targetArtifact,
            ExistingArtifactPolicy.Reject,
            Guid.NewGuid().ToString("N"),
            participant);

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State);
        CollectionAssert.AreEqual(targetContents, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual("netniv", targetService.ReadInstalledState()!.ProviderId);
        Assert.AreEqual(1, participant.CommitCount);
        Assert.AreEqual(0, participant.RollbackCount);
        Assert.AreEqual(ModDeploymentPhase.CleanupPending, targetService.ReadJournal()!.Phase);
        var recoveryService = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, targetContents, targetContents.LongLength),
            versionReader: new FakeVersionReader(targetArtifact.ExpectedVersion),
            installationAttribution: new("netniv", "stable", "netniv.stfc-community-mod"));
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await recoveryService.RecoverAsync()).State);
        Assert.AreEqual(ModDeploymentPhase.Committed, recoveryService.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task RecoveryForSecondInstallationPreservesFirstInstallationReceipt()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var firstGameDirectory = CreateGameDirectory(temporaryDirectory, "first-game");
        var secondGameDirectory = CreateGameDirectory(temporaryDirectory, "second-game");
        var firstService = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await firstService.DeployAsync(
                firstGameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        var firstReceipt = firstService.ReadInstalledState(firstGameDirectory);
        var faultingService = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterPhasePersisted: (phase, _) => phase == ModDeploymentPhase.Committed
                ? ValueTask.FromException(new InjectedDeploymentFaultException(phase))
                : ValueTask.CompletedTask);

        var interrupted = await faultingService.DeployAsync(
            secondGameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, interrupted.State);
        Assert.AreEqual(firstReceipt, faultingService.ReadInstalledState(firstGameDirectory));
        Assert.IsNotNull(faultingService.ReadInstalledState(secondGameDirectory));
        var recoveryService = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await recoveryService.RecoverAsync()).State);
        Assert.AreEqual(firstReceipt, recoveryService.ReadInstalledState(firstGameDirectory));
        Assert.IsNotNull(recoveryService.ReadInstalledState(secondGameDirectory));
        Assert.AreEqual(2, recoveryService.ReadInstalledStates().Count);
    }

    [TestMethod]
    public async Task ConcurrentMutationIsRejectedWhileDownloadIsActive()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var downloader = new BlockingDownloader(SuccessfulDownload());
        var service = CreateService(temporaryDirectory, downloader);

        var first = service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);
        await downloader.Entered.Task;

        var second = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Busy, second.State);
        downloader.Release.SetResult();
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await first).State);
    }

    [TestMethod]
    public async Task UninstallRemovesOnlyLauncherManagedArtifact()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var unrelatedPath = Path.Combine(gameDirectory, "keep-me.txt");
        File.WriteAllText(unrelatedPath, "user-owned");
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);

        var result = await service.UninstallAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
        Assert.IsTrue(result.Changed);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual("user-owned", File.ReadAllText(unrelatedPath));
        Assert.IsNull(service.ReadInstalledState());
        Assert.AreEqual(ModDeploymentOperation.Uninstall, service.ReadJournal()!.Operation);
        Assert.AreEqual(ModDeploymentPhase.Committed, service.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task UninstallRestoresArtifactPreservedDuringAdoption()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var previous = new byte[] { 4, 2 };
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        File.WriteAllBytes(targetPath, previous);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.AdoptAndPreserve)).State);

        var result = await service.UninstallAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(targetPath));
        Assert.IsNull(service.ReadInstalledState());
    }

    [TestMethod]
    public async Task UninstallRefusesArtifactChangedOutsideLauncher()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        File.WriteAllBytes(targetPath, [9, 9, 9]);

        var result = await service.UninstallAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.ManagedArtifactChanged, result.State);
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, File.ReadAllBytes(targetPath));
        Assert.IsNotNull(service.ReadInstalledState());
    }

    [TestMethod]
    public async Task StopManagingChangedInstallationPreservesEveryGameFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var unrelatedPath = Path.Combine(gameDirectory, "keep-me.toml");
        File.WriteAllText(unrelatedPath, "user-owned");
        var isGameRunning = false;
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            isGameRunning: _ => isGameRunning);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        var externallyChanged = new byte[] { 9, 8, 7, 6 };
        File.WriteAllBytes(targetPath, externallyChanged);
        isGameRunning = true;

        var result = await service.StopManagingAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.IsTrue(result.Changed);
        Assert.IsNull(service.ReadInstalledState(gameDirectory));
        CollectionAssert.AreEqual(externallyChanged, File.ReadAllBytes(targetPath));
        Assert.AreEqual("user-owned", File.ReadAllText(unrelatedPath));
    }

    [TestMethod]
    public async Task StopManagingMalformedAbsolutePathReturnsInvalidTarget()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        var malformedPath = $"{Path.GetPathRoot(temporaryDirectory.Path)}invalid\0path";

        var result = await service.StopManagingAsync(malformedPath);

        Assert.AreEqual(ModDeploymentResultState.InvalidGameTarget, result.State);
        Assert.IsFalse(result.Changed);
    }

    [TestMethod]
    public async Task DirectRepairRejectsArtifactThatDoesNotMatchReceipt()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        var externalBytes = new byte[] { 9, 8, 7 };
        File.WriteAllBytes(targetPath, externalBytes);
        var differentArtifact = ReleaseArtifact([1, 2, 3, 4], "3.0.0.0");

        var result = await service.RepairAsync(gameDirectory, differentArtifact);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State);
        StringAssert.Contains(result.Message, "exact artifact");
        CollectionAssert.AreEqual(externalBytes, File.ReadAllBytes(targetPath));
        Assert.AreEqual(ReleaseArtifact().Sha256, service.ReadInstalledState(gameDirectory)!.Sha256);
    }

    [TestMethod]
    public async Task StopManagingMissingInstallationPreservesAnotherReceipt()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var firstGameDirectory = CreateGameDirectory(temporaryDirectory, "first-game");
        var secondGameDirectory = CreateGameDirectory(temporaryDirectory, "second-game");
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                firstGameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                secondGameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        var firstReceipt = service.ReadInstalledState(firstGameDirectory);
        File.Delete(Path.Combine(secondGameDirectory, "version.dll"));

        var result = await service.StopManagingAsync(secondGameDirectory);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.AreEqual(firstReceipt, service.ReadInstalledState(firstGameDirectory));
        Assert.IsNull(service.ReadInstalledState(secondGameDirectory));
        Assert.AreEqual(1, service.ReadInstalledStates().Count);
        Assert.IsFalse(File.Exists(Path.Combine(secondGameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task StopManagingAdoptedInstallationRetainsRecoveryReceiptAndBackup()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var adopted = new byte[] { 4, 2, 4, 2 };
        File.WriteAllBytes(targetPath, adopted);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.AdoptAndPreserve)).State);
        var backupPath = service.ReadInstalledState(gameDirectory)!.PreviousArtifactBackupPath!;

        var result = await service.StopManagingAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.IsNull(service.ReadInstalledState(gameDirectory));
        Assert.IsTrue(File.Exists(backupPath));
        CollectionAssert.AreEqual(adopted, File.ReadAllBytes(backupPath));
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(targetPath));
        var registry = JsonSerializer.Deserialize<ModInstalledArtifactRegistry>(
            File.ReadAllBytes(service.InstalledStatePath),
            JournalJsonOptions)!;
        Assert.AreEqual(0, registry.Installations.Count);
        Assert.AreEqual(1, registry.DetachedAdoptionBackups!.Count);
        Assert.AreEqual(Path.GetFullPath(gameDirectory), registry.DetachedAdoptionBackups[0].GameDirectory);
        Assert.AreEqual(backupPath, registry.DetachedAdoptionBackups[0].PreviousArtifactBackupPath);
    }

    [TestMethod]
    public async Task UninstallFinalizationFailureLeavesRemovalCommittedForRecovery()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var previous = new byte[] { 8, 6, 7, 5, 3, 0, 9 };
        File.WriteAllBytes(targetPath, previous);
        var installService = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installService.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.AdoptAndPreserve)).State);
        var priorBackupPath = installService.ReadInstalledState()!.PreviousArtifactBackupPath!;
        var uninstallService = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterPhasePersisted: (phase, _) =>
            {
                if (phase == ModDeploymentPhase.Committed)
                {
                    throw new InjectedDeploymentFaultException(phase);
                }
                return ValueTask.CompletedTask;
            });

        var result = await uninstallService.UninstallAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State);
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(targetPath));
        Assert.IsFalse(File.Exists(priorBackupPath));
        Assert.IsNull(uninstallService.ReadInstalledState());
        Assert.AreEqual(ModDeploymentPhase.CleanupPending, uninstallService.ReadJournal()!.Phase);
        var recoveryService = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await recoveryService.RecoverAsync()).State);
        Assert.AreEqual(ModDeploymentPhase.Committed, recoveryService.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task LegacyIncompleteAdoptionWithoutIdentityFailsClosed()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var previous = new byte[] { 1, 3, 3, 7 };
        File.WriteAllBytes(targetPath, ArtifactContents);
        var transactionId = Guid.NewGuid().ToString("N");
        var sameVolumeBackupPath = Path.Combine(gameDirectory, $".version.dll.{transactionId}.rollback");
        File.WriteAllBytes(sameVolumeBackupPath, previous);
        var journal = new ModDeploymentJournal(
            1,
            transactionId,
            ModDeploymentOperation.Deploy,
            ModDeploymentPhase.Committing,
            gameDirectory,
            ReleaseArtifact(),
            Path.Combine(gameDirectory, $".version.dll.{transactionId}.stage"),
            sameVolumeBackupPath,
            Path.Combine(stateDirectory, "rollback", transactionId, "version.dll"),
            true,
            null,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(stateDirectory, "deployment-journal.json"),
            JsonSerializer.Serialize(journal, JournalJsonOptions));
        var service = new ModDeploymentService(
            stateDirectory,
            new FakeDownloader(SuccessfulDownload()),
            new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            new FakeAuthenticityVerifier(true),
            _ => false,
            DefaultAttribution());

        var result = await service.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State);
        Assert.IsFalse(result.Changed);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(targetPath));
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(sameVolumeBackupPath));
        Assert.AreEqual(ModDeploymentPhase.RollingBack, service.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task LegacyInstalledAdoptionReceiptIsMigratedDuringDirectUninstall()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        var managedPath = Path.Combine(gameDirectory, "version.dll");
        var adopted = new byte[] { 4, 4, 2, 1 };
        var adoptedPath = Path.Combine(
            temporaryDirectory.CreateDirectory("state"),
            "rollback",
            "legacy-adopted-version.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(adoptedPath)!);
        File.WriteAllBytes(adoptedPath, adopted);
        File.WriteAllBytes(managedPath, ArtifactContents);
        var oldState = new ModInstalledArtifactState(
            1,
            gameDirectory,
            "version.dll",
            ReleaseArtifact().ExpectedVersion,
            ArtifactContents.LongLength,
            Convert.ToHexString(SHA256.HashData(ArtifactContents)),
            DateTimeOffset.UtcNow,
            adoptedPath,
            "guffawaffle",
            "stable",
            "guffawaffle.windows");
        File.WriteAllText(
            service.InstalledStatePath,
            JsonSerializer.Serialize(oldState, JournalJsonOptions));

        var result = await service.UninstallAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
        CollectionAssert.AreEqual(adopted, File.ReadAllBytes(managedPath));
        Assert.IsFalse(File.Exists(adoptedPath));
        Assert.IsNull(service.ReadInstalledState());
    }

    [TestMethod]
    public async Task NullArtifactJournalFailsClosedWithoutMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var existing = new byte[] { 6, 6, 6 };
        File.WriteAllBytes(targetPath, existing);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        File.WriteAllText(
            service.JournalPath,
            "{\"schemaVersion\":1,\"artifact\":null}");

        var result = await service.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State);
        Assert.IsFalse(result.Changed);
        CollectionAssert.AreEqual(existing, File.ReadAllBytes(targetPath));
    }

    [TestMethod]
    public async Task SuccessfulMaintenanceNoOpsReportNoChange()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());

        var uninstall = await service.UninstallAsync(gameDirectory);
        var recovery = await service.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.Succeeded, uninstall.State);
        Assert.IsFalse(uninstall.Changed);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, recovery.State);
        Assert.IsFalse(recovery.Changed);
    }

    [TestMethod]
    public async Task RecoveryRejectsJournalPathsOutsideTransactionBoundary()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var outsidePath = Path.Combine(temporaryDirectory.Path, "outside.stage");
        File.WriteAllBytes(outsidePath, [4, 2]);
        var transactionId = Guid.NewGuid().ToString("N");
        var journal = new ModDeploymentJournal(
            1,
            transactionId,
            ModDeploymentOperation.Deploy,
            ModDeploymentPhase.Staged,
            gameDirectory,
            ReleaseArtifact(),
            outsidePath,
            Path.Combine(gameDirectory, $".version.dll.{transactionId}.rollback"),
            Path.Combine(stateDirectory, "rollback", transactionId, "version.dll"),
            false,
            null,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(stateDirectory, "deployment-journal.json"),
            JsonSerializer.Serialize(journal, JournalJsonOptions));
        var service = new ModDeploymentService(
            stateDirectory,
            new FakeDownloader(SuccessfulDownload()),
            new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            new FakeAuthenticityVerifier(true),
            _ => false,
            DefaultAttribution());

        var result = await service.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State);
        CollectionAssert.AreEqual(new byte[] { 4, 2 }, File.ReadAllBytes(outsidePath));
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task InvalidGameTargetIsRejectedBeforeDownload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = temporaryDirectory.CreateDirectory("not-game");
        var downloader = new FakeDownloader(SuccessfulDownload());
        var service = CreateService(temporaryDirectory, downloader);

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.InvalidGameTarget, result.State);
        Assert.AreEqual(0, downloader.CallCount);
    }

    [TestMethod]
    public async Task CorruptPersistedStateFailsClosedBeforeMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        File.WriteAllText(Path.Combine(stateDirectory, "deployment-journal.json"), "{not-json");
        var downloader = new FakeDownloader(SuccessfulDownload());
        var service = new ModDeploymentService(
            stateDirectory,
            downloader,
            new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            new FakeAuthenticityVerifier(true),
            _ => false,
            DefaultAttribution());

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State);
        Assert.AreEqual(0, downloader.CallCount);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task HttpDownloaderStopsBeforeOversizedContentIsRead()
    {
        using var client = new HttpClient(new StaticResponseHandler(new byte[] { 1, 2, 3, 4, 5 }));
        var downloader = new HttpModArtifactDownloader(client, maximumDownloadSize: 4);

        var result = await downloader.DownloadAsync(
            new Uri("https://example.invalid/version.dll"),
            CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        Assert.AreEqual(5L, result.DeclaredContentLength);
        Assert.AreEqual(0, result.Contents.Length);
    }

    [TestMethod]
    public async Task UntrustedAuthenticodeArtifactNeverReachesTarget()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            authenticityVerifier: new FakeAuthenticityVerifier(false));

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual(ModDeploymentPhase.RolledBack, service.ReadJournal()!.Phase);
    }

    private static ModInstalledArtifactState InstalledState(string gameDirectory) => new(
        1,
        Path.GetFullPath(gameDirectory),
        "version.dll",
        ReleaseArtifact().ExpectedVersion,
        ReleaseArtifact().Size,
        ReleaseArtifact().Sha256,
        DateTimeOffset.UtcNow,
        PreviousArtifactBackupPath: null,
        "guffawaffle",
        "stable",
        "guffawaffle.windows");

    private static string CreateGameDirectory(
        TemporaryDirectory temporaryDirectory,
        string name = "game")
    {
        var gameDirectory = temporaryDirectory.CreateDirectory(name);
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        return gameDirectory;
    }

    private static ModDeploymentService CreateService(
        TemporaryDirectory temporaryDirectory,
        ModArtifactDownload download,
        Func<string, bool>? isGameRunning = null,
        IModArtifactVersionReader? versionReader = null,
        IModArtifactAuthenticityVerifier? authenticityVerifier = null,
        Func<ModDeploymentPhase, CancellationToken, ValueTask>? afterPhasePersisted = null,
        ModInstallationAttribution? installationAttribution = null) =>
        CreateService(
            temporaryDirectory,
            new FakeDownloader(download),
            isGameRunning,
            versionReader,
            authenticityVerifier,
            afterPhasePersisted,
            installationAttribution);

    private static ModDeploymentService CreateService(
        TemporaryDirectory temporaryDirectory,
        IModArtifactDownloader downloader,
        Func<string, bool>? isGameRunning = null,
        IModArtifactVersionReader? versionReader = null,
        IModArtifactAuthenticityVerifier? authenticityVerifier = null,
        Func<ModDeploymentPhase, CancellationToken, ValueTask>? afterPhasePersisted = null,
        ModInstallationAttribution? installationAttribution = null) =>
        new(
            temporaryDirectory.CreateDirectory("state"),
            downloader,
            versionReader ?? new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            authenticityVerifier ?? new FakeAuthenticityVerifier(true),
            isGameRunning ?? (_ => false),
            installationAttribution ?? DefaultAttribution(),
            afterPhasePersisted: afterPhasePersisted);

    private static ModInstallationAttribution DefaultAttribution() =>
        new("guffawaffle", "stable", "guffawaffle.windows");

    private static ModReleaseArtifact ReleaseArtifact() => new(
        new Uri("https://example.invalid/version.dll"),
        "version.dll",
        ArtifactContents.LongLength,
        Convert.ToHexString(SHA256.HashData(ArtifactContents)),
        "2.1.0.8");

    private static ModReleaseArtifact ReleaseArtifact(byte[] contents, string version) => new(
        new Uri("https://example.invalid/version.dll"),
        "version.dll",
        contents.LongLength,
        Convert.ToHexString(SHA256.HashData(contents)),
        version);

    private static ModArtifactDownload SuccessfulDownload() => new(
        HttpStatusCode.OK,
        ArtifactContents,
        ArtifactContents.LongLength);

    private sealed class FakeDownloader(ModArtifactDownload result) : IModArtifactDownloader
    {
        public int CallCount { get; private set; }

        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingDownloader(ModArtifactDownload result) : IModArtifactDownloader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class FakeCommitParticipant(bool failCommit = false) : IModDeploymentCommitParticipant
    {
        public int CommitCount { get; private set; }

        public int RollbackCount { get; private set; }

        public Task BeginAsync(
            ModDeploymentCommitContext context,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            CommitCount++;
            return failCommit
                ? Task.FromException(new InvalidOperationException("Injected participant commit failure."))
                : Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RollBackAsync(CancellationToken cancellationToken)
        {
            RollbackCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVersionReader(string? version) : IModArtifactVersionReader
    {
        public string? ReadVersion(string artifactPath) => version;
    }

    private sealed class FakeAuthenticityVerifier(bool trusted) : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) =>
            new(trusted, trusted ? "trusted test artifact" : "untrusted test artifact");
    }

    private sealed class InjectedDeploymentFaultException(ModDeploymentPhase phase)
        : Exception($"Injected failure after {phase}.");

    private sealed class StaticResponseHandler(byte[] contents) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(contents),
            });
    }
}
