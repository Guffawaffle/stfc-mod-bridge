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
        var service = CreateService(temporaryDirectory, SuccessfulDownload());

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual(ModDeploymentPhase.Committed, service.ReadJournal()!.Phase);
        Assert.AreEqual(ReleaseArtifact().Sha256, service.ReadInstalledState()!.Sha256);
        Assert.IsFalse(Directory.EnumerateFiles(gameDirectory, "*.stage").Any());
        Assert.IsFalse(Directory.EnumerateFiles(gameDirectory, "*.rollback").Any());
    }

    [TestMethod]
    public async Task RunningGameDeniesMutationBeforeDownload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var downloader = new FakeDownloader(SuccessfulDownload());
        var service = CreateService(temporaryDirectory, downloader, isGameRunning: () => true);

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

        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await updateService.UninstallAsync()).State);
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
    [DataRow(ModDeploymentPhase.Committed)]
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

        var result = await service.UninstallAsync();

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

        var result = await service.UninstallAsync();

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

        var result = await service.UninstallAsync();

        Assert.AreEqual(ModDeploymentResultState.ManagedArtifactChanged, result.State);
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, File.ReadAllBytes(targetPath));
        Assert.IsNotNull(service.ReadInstalledState());
    }

    [TestMethod]
    public async Task FailedUninstallRestoresManagedAndPreviouslyAdoptedArtifacts()
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

        var result = await uninstallService.UninstallAsync();

        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(targetPath));
        Assert.IsTrue(File.Exists(priorBackupPath));
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(priorBackupPath));
        Assert.IsNotNull(uninstallService.ReadInstalledState());
    }

    [TestMethod]
    public async Task StartupRecoveryRestoresBackupFromIncompleteCommit()
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
            () => false);

        var result = await service.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
        Assert.IsTrue(result.Changed);
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(targetPath));
        Assert.AreEqual(ModDeploymentPhase.RolledBack, service.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task SuccessfulMaintenanceNoOpsReportNoChange()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var service = CreateService(temporaryDirectory, SuccessfulDownload());

        var uninstall = await service.UninstallAsync();
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
            () => false);

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
            () => false);

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

    private static string CreateGameDirectory(TemporaryDirectory temporaryDirectory)
    {
        var gameDirectory = temporaryDirectory.CreateDirectory("game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        return gameDirectory;
    }

    private static ModDeploymentService CreateService(
        TemporaryDirectory temporaryDirectory,
        ModArtifactDownload download,
        Func<bool>? isGameRunning = null,
        IModArtifactVersionReader? versionReader = null,
        IModArtifactAuthenticityVerifier? authenticityVerifier = null,
        Func<ModDeploymentPhase, CancellationToken, ValueTask>? afterPhasePersisted = null) =>
        CreateService(
            temporaryDirectory,
            new FakeDownloader(download),
            isGameRunning,
            versionReader,
            authenticityVerifier,
            afterPhasePersisted);

    private static ModDeploymentService CreateService(
        TemporaryDirectory temporaryDirectory,
        IModArtifactDownloader downloader,
        Func<bool>? isGameRunning = null,
        IModArtifactVersionReader? versionReader = null,
        IModArtifactAuthenticityVerifier? authenticityVerifier = null,
        Func<ModDeploymentPhase, CancellationToken, ValueTask>? afterPhasePersisted = null) =>
        new(
            temporaryDirectory.CreateDirectory("state"),
            downloader,
            versionReader ?? new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            authenticityVerifier ?? new FakeAuthenticityVerifier(true),
            isGameRunning ?? (() => false),
            afterPhasePersisted: afterPhasePersisted);

    private static ModReleaseArtifact ReleaseArtifact() => new(
        new Uri("https://example.invalid/version.dll"),
        "version.dll",
        ArtifactContents.LongLength,
        Convert.ToHexString(SHA256.HashData(ArtifactContents)),
        "2.1.0.8");

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
