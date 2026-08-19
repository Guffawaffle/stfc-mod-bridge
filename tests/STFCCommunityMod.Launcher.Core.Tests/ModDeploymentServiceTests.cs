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

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
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
    public async Task SignedReleaseProductVersionMustMatchTheManifestTag()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var artifact = ReleaseArtifact() with
        {
            ExpectedVersion = "2.1.0.0",
            ExpectedProductVersion = "v2.1.0-guffa.10",
        };
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            versionReader: new FakeProductVersionReader("2.1.0.0", "v2.1.0-guffa.10"));

        var result = await service.DeployAsync(
            gameDirectory,
            artifact,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.AreEqual(
            "v2.1.0-guffa.10",
            service.ReadInstalledState(gameDirectory)?.ReleaseProductVersion);
    }

    [TestMethod]
    public async Task SignedReleaseProductVersionMismatchRollsBack()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var artifact = ReleaseArtifact() with
        {
            ExpectedVersion = "2.1.0.0",
            ExpectedProductVersion = "v2.1.0-guffa.10",
        };
        var targetInstalled = false;
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            versionReader: new FakeProductVersionReader("2.1.0.0", "v2.1.0-guffa.9"),
            afterFileCheckpoint: (checkpoint, _) =>
            {
                targetInstalled |= checkpoint == ModDeploymentFileCheckpoint.TargetDllInstalled;
                return ValueTask.CompletedTask;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            artifact,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State, result.Message);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        Assert.IsFalse(targetInstalled, "A DLL with the wrong signed ProductVersion reached the live path.");
    }

    [TestMethod]
    public async Task EqualReleaseFloorRequiresTheAcceptedTagAndArtifactDigest()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var acceptedBytes = new byte[] { 10, 10, 10 };
        var accepted = ReleaseArtifact(acceptedBytes, "2.1.0.0") with
        {
            ExpectedProductVersion = "v2.1.0-guffa.10",
        };
        var installed = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, acceptedBytes, acceptedBytes.LongLength),
            versionReader: new FakeProductVersionReader("2.1.0.0", accepted.ExpectedProductVersion));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installed.DeployAsync(gameDirectory, accepted, ExistingArtifactPolicy.Reject)).State);

        foreach (var candidateProductVersion in new[]
                 {
                     "v2.1.0-guffa.10",
                     "v2.1.0-guffa.rc10",
                 })
        {
            var replacementBytes = new byte[] { 10, 10, 11 };
            var replacement = ReleaseArtifact(replacementBytes, "2.1.0.0") with
            {
                ExpectedProductVersion = candidateProductVersion,
            };
            var downloader = new FakeDownloader(
                new ModArtifactDownload(HttpStatusCode.OK, replacementBytes, replacementBytes.LongLength));
            var service = CreateService(
                temporaryDirectory,
                downloader,
                versionReader: new FakeProductVersionReader("2.1.0.0", candidateProductVersion));

            var result = await service.DeployAsync(
                gameDirectory,
                replacement,
                ExistingArtifactPolicy.Reject);

            Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State, result.Message);
            StringAssert.Contains(
                result.Message,
                candidateProductVersion.EndsWith("rc10", StringComparison.Ordinal)
                    ? "older than"
                    : "exactly match");
            Assert.AreEqual(0, downloader.CallCount);
            CollectionAssert.AreEqual(acceptedBytes, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        }
    }

    [DataTestMethod]
    [DataRow("v2.1.0.beta.99")]
    [DataRow("v2.1.0-rc.99")]
    public async Task FinalReleaseFloorRejectsSameCorePrereleaseRegardlessOfIteration(
        string prereleaseProductVersion)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var finalBytes = new byte[] { 6, 0, 0 };
        var final = ReleaseArtifact(finalBytes, "2.1.0.0") with
        {
            ExpectedProductVersion = "v2.1.0",
        };
        var installer = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, finalBytes, finalBytes.LongLength),
            versionReader: new FakeProductVersionReader(final.ExpectedVersion, final.ExpectedProductVersion));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installer.DeployAsync(
                gameDirectory,
                final,
                ExistingArtifactPolicy.Reject)).State);
        var prereleaseBytes = new byte[] { 6, 0, 99 };
        var prerelease = ReleaseArtifact(prereleaseBytes, "2.1.0.0") with
        {
            ExpectedProductVersion = prereleaseProductVersion,
        };
        var downloader = new FakeDownloader(
            new ModArtifactDownload(
                HttpStatusCode.OK,
                prereleaseBytes,
                prereleaseBytes.LongLength));
        var service = CreateService(
            temporaryDirectory,
            downloader,
            versionReader: new FakeProductVersionReader(
                prerelease.ExpectedVersion,
                prereleaseProductVersion));

        var result = await service.DeployAsync(
            gameDirectory,
            prerelease,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State, result.Message);
        StringAssert.Contains(result.Message, "older than");
        Assert.AreEqual(0, downloader.CallCount);
        CollectionAssert.AreEqual(finalBytes, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task AmbiguousReleaseFamilyCannotCrossTheRetainedFloor()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var alphaBytes = new byte[] { 6, 0, 1 };
        var alpha = ReleaseArtifact(alphaBytes, "2.1.0.0") with
        {
            ExpectedProductVersion = "v2.1.0.alpha.1",
        };
        var installer = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, alphaBytes, alphaBytes.LongLength),
            versionReader: new FakeProductVersionReader(alpha.ExpectedVersion, alpha.ExpectedProductVersion));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installer.DeployAsync(
                gameDirectory,
                alpha,
                ExistingArtifactPolicy.Reject)).State);
        var betaBytes = new byte[] { 6, 0, 2 };
        var beta = ReleaseArtifact(betaBytes, "2.1.0.0") with
        {
            ExpectedProductVersion = "v2.1.0.beta.2",
        };
        var downloader = new FakeDownloader(
            new ModArtifactDownload(HttpStatusCode.OK, betaBytes, betaBytes.LongLength));
        var service = CreateService(
            temporaryDirectory,
            downloader,
            versionReader: new FakeProductVersionReader(beta.ExpectedVersion, beta.ExpectedProductVersion));

        var result = await service.DeployAsync(
            gameDirectory,
            beta,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State, result.Message);
        StringAssert.Contains(result.Message, "cannot be safely ordered");
        Assert.AreEqual(0, downloader.CallCount);
        CollectionAssert.AreEqual(alphaBytes, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task DeploymentLeaseRejectsAStaleCandidateBelowTheRetainedReleaseFloor()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var newestBytes = new byte[] { 1, 2, 3, 12 };
        var newest = ReleaseArtifact(newestBytes, "2.1.0.0") with
        {
            ExpectedProductVersion = "v2.1.0-guffa.12",
        };
        var newestService = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, newestBytes, newestBytes.LongLength),
            versionReader: new FakeProductVersionReader("2.1.0.0", newest.ExpectedProductVersion));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await newestService.DeployAsync(
                gameDirectory,
                newest,
                ExistingArtifactPolicy.Reject)).State);
        var olderBytes = new byte[] { 1, 2, 3, 11 };
        var older = ReleaseArtifact(olderBytes, "2.1.0.0") with
        {
            ExpectedProductVersion = "v2.1.0-guffa.11",
        };
        var olderDownloader = new FakeDownloader(
            new ModArtifactDownload(HttpStatusCode.OK, olderBytes, olderBytes.LongLength));
        var staleService = CreateService(
            temporaryDirectory,
            olderDownloader,
            versionReader: new FakeProductVersionReader("2.1.0.0", older.ExpectedProductVersion));

        var result = await staleService.DeployAsync(
            gameDirectory,
            older,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State, result.Message);
        Assert.AreEqual(0, olderDownloader.CallCount);
        CollectionAssert.AreEqual(newestBytes, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual(
            newest.ExpectedProductVersion,
            staleService.ReadInstalledState(gameDirectory)?.ReleaseProductVersion);
    }

    [TestMethod]
    public async Task ProviderRoundTripRetainsIndependentReleaseFloor()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var guffTenBytes = new byte[] { 10, 10, 10 };
        var guffTen = ReleaseArtifact(guffTenBytes, "2.1.0.0") with
        {
            ExpectedProductVersion = "v2.1.0-guffa.10",
        };
        var guffTenService = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, guffTenBytes, guffTenBytes.LongLength),
            versionReader: new FakeProductVersionReader("2.1.0.0", guffTen.ExpectedProductVersion));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await guffTenService.DeployAsync(
                gameDirectory,
                guffTen,
                ExistingArtifactPolicy.Reject)).State);

        var netnivBytes = new byte[] { 1, 1, 4 };
        var netniv = ReleaseArtifact(netnivBytes, "1.1.4.0");
        var netnivCertification = ReviewedCertification(
            new("netniv", "stable", "netniv.stfc-community-mod"),
            "v1.1.4",
            netniv);
        var netnivService = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, netnivBytes, netnivBytes.LongLength),
            versionReader: new FakeVersionReader(netniv.ExpectedVersion),
            installationAttribution: new("netniv", "stable", "netniv.stfc-community-mod"),
            reviewedCertifications: [netnivCertification]);
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await netnivService.DeployAsync(
                gameDirectory,
                netniv,
                ExistingArtifactPolicy.Reject)).State);

        var guffNineBytes = new byte[] { 9, 9, 9 };
        var guffNine = ReleaseArtifact(guffNineBytes, "2.1.0.0") with
        {
            ExpectedProductVersion = "v2.1.0-guffa.9",
        };
        var guffNineDownloader = new FakeDownloader(
            new ModArtifactDownload(HttpStatusCode.OK, guffNineBytes, guffNineBytes.LongLength));
        var guffNineService = CreateService(
            temporaryDirectory,
            guffNineDownloader,
            versionReader: new FakeProductVersionReader("2.1.0.0", guffNine.ExpectedProductVersion));

        var blocked = await guffNineService.DeployAsync(
            gameDirectory,
            guffNine,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, blocked.State, blocked.Message);
        Assert.AreEqual(0, guffNineDownloader.CallCount);
        CollectionAssert.AreEqual(netnivBytes, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        var retained = guffNineService.ReadInstalledState(gameDirectory)!;
        Assert.AreEqual("netniv", retained.ProviderId);
        Assert.AreEqual(
            "v2.1.0-guffa.10",
            retained.ReleaseHighWaterMarks!.Single(mark => mark.ProviderId == "guffawaffle")
                .ReleaseProductVersion);
        Assert.AreEqual(
            guffTen.Sha256,
            retained.ReleaseHighWaterMarks!.Single(mark => mark.ProviderId == "guffawaffle")
                .AcceptedArtifactSha256);

        var equalTagDifferentBytes = guffTen with
        {
            Size = guffNineBytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(guffNineBytes)),
        };
        var equalReplayDownloader = new FakeDownloader(
            new ModArtifactDownload(HttpStatusCode.OK, guffNineBytes, guffNineBytes.LongLength));
        var equalReplay = CreateService(
            temporaryDirectory,
            equalReplayDownloader,
            versionReader: new FakeProductVersionReader("2.1.0.0", guffTen.ExpectedProductVersion));
        Assert.AreEqual(
            ModDeploymentResultState.VerificationFailed,
            (await equalReplay.DeployAsync(
                gameDirectory,
                equalTagDifferentBytes,
                ExistingArtifactPolicy.Reject)).State);
        Assert.AreEqual(0, equalReplayDownloader.CallCount);

        var returnToTen = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, guffTenBytes, guffTenBytes.LongLength),
            versionReader: new FakeProductVersionReader("2.1.0.0", guffTen.ExpectedProductVersion));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await returnToTen.DeployAsync(
                gameDirectory,
                guffTen,
                ExistingArtifactPolicy.Reject)).State);
    }

    [TestMethod]
    public async Task ExactReviewedLegacyReceiptProjectsReleaseFloorWithoutPassiveRewrite()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var legacyArtifact = ReleaseArtifact() with { ExpectedVersion = "2.1.0.0" };
        var legacyService = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            versionReader: new FakeVersionReader(legacyArtifact.ExpectedVersion));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await legacyService.DeployAsync(
                gameDirectory,
                legacyArtifact,
                ExistingArtifactPolicy.Reject)).State);
        var persisted = File.ReadAllBytes(legacyService.InstalledStatePath);
        var certification = new ReviewedReleaseCertification(
            "guffawaffle",
            "stable",
            "guffawaffle.windows",
            "Guffawaffle/STFC-Community-Mod",
            "v2.1.0-guffa.9",
            "2.1.0-guffa.9",
            new string('A', 40),
            "version.dll",
            ArtifactContents.LongLength,
            ReleaseArtifact().Sha256,
            "version.dll",
            ArtifactContents.LongLength,
            ReleaseArtifact().Sha256,
            legacyArtifact.ExpectedVersion,
            DateTimeOffset.UtcNow);
        var olderBytes = new byte[] { 8, 8, 8 };
        var older = ReleaseArtifact(olderBytes, "2.1.0.0") with
        {
            ExpectedProductVersion = "v2.1.0-guffa.8",
        };
        var downloader = new FakeDownloader(
            new ModArtifactDownload(HttpStatusCode.OK, olderBytes, olderBytes.LongLength));
        var service = CreateService(
            temporaryDirectory,
            downloader,
            versionReader: new FakeProductVersionReader("2.1.0.0", older.ExpectedProductVersion),
            reviewedCertifications: [certification]);

        Assert.AreEqual("v2.1.0-guffa.9", service.ReadReleaseProductVersionFloor(gameDirectory));
        CollectionAssert.AreEqual(persisted, File.ReadAllBytes(service.InstalledStatePath));
        var result = await service.DeployAsync(gameDirectory, older, ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State, result.Message);
        Assert.AreEqual(0, downloader.CallCount);
        CollectionAssert.AreEqual(persisted, File.ReadAllBytes(service.InstalledStatePath));
    }

    [TestMethod]
    public async Task UnclassifiedLegacyReceiptBlocksByteDifferentReplacementBeforeDownload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var legacyService = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await legacyService.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject)).State);
        Assert.IsNull(legacyService.ReadInstalledState(gameDirectory)!.ReleaseProductVersion);
        var persisted = File.ReadAllBytes(legacyService.InstalledStatePath);
        var candidateBytes = new byte[] { 7, 7, 7 };
        var downloader = new FakeDownloader(
            new ModArtifactDownload(HttpStatusCode.OK, candidateBytes, candidateBytes.LongLength));
        var service = CreateService(temporaryDirectory, downloader);
        var candidate = ReleaseArtifact(candidateBytes, "2.1.0.0") with
        {
            ExpectedProductVersion = "v2.1.0-guffa.7",
        };

        var result = await service.DeployAsync(
            gameDirectory,
            candidate,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State, result.Message);
        StringAssert.Contains(result.Message, "predates signed release-order receipts");
        Assert.AreEqual(0, downloader.CallCount);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        CollectionAssert.AreEqual(persisted, File.ReadAllBytes(service.InstalledStatePath));
    }

    [TestMethod]
    public async Task CleanTargetRollbackDoesNotPublishAnAbsentDllOwnershipReceipt()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var committedReceiptCount = 0;
        var service = new ModDeploymentService(
            temporaryDirectory.CreateDirectory("state"),
            new FakeDownloader(SuccessfulDownload()),
            new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            new FakeAuthenticityVerifier(true),
            _ => false,
            DefaultAttribution(),
            timeProvider: null,
            afterPhasePersisted: null,
            reviewedCertification: null,
            afterFileCheckpoint: (checkpoint, _) => checkpoint == ModDeploymentFileCheckpoint.TargetDllInstalled
                ? ValueTask.FromException(new IOException("Injected post-move failure."))
                : ValueTask.CompletedTask,
            afterArtifactCommitted: (_, _) =>
            {
                committedReceiptCount++;
                return true;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State, result.Message);
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual(0, committedReceiptCount);
    }

    [TestMethod]
    public async Task SameByteDllAppearingAfterPriorBackupIsPreservedDuringRollback()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var priorBytes = new byte[] { 0x50, 0x52, 0x49, 0x4f, 0x52 };
        File.WriteAllBytes(targetPath, priorBytes);
        CandidateFileIdentity? externalIdentity = null;
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterFileCheckpoint: (checkpoint, _) =>
            {
                if (checkpoint == ModDeploymentFileCheckpoint.PriorDllBackedUp)
                {
                    File.WriteAllBytes(targetPath, ArtifactContents);
                    using var exact = ExactFileMutation.Open(targetPath);
                    externalIdentity = exact.Identity;
                }
                return ValueTask.CompletedTask;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.AdoptAndPreserve);

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
        Assert.IsNotNull(externalIdentity);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(targetPath));
        using (var exact = ExactFileMutation.Open(targetPath))
        {
            Assert.AreEqual(externalIdentity, exact.Identity);
        }
        var journal = service.ReadJournal()!;
        Assert.AreEqual(ModDeploymentPhase.RollingBack, journal.Phase);
        CollectionAssert.AreEqual(priorBytes, File.ReadAllBytes(journal.SameVolumeBackupPath));
    }

    [TestMethod]
    public async Task SameByteDllAppearingOnCleanTargetIsPreservedDuringRollback()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        CandidateFileIdentity? externalIdentity = null;
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterPhasePersisted: (phase, _) =>
            {
                if (phase == ModDeploymentPhase.Committing)
                {
                    File.WriteAllBytes(targetPath, ArtifactContents);
                    using var exact = ExactFileMutation.Open(targetPath);
                    externalIdentity = exact.Identity;
                }
                return ValueTask.CompletedTask;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
        Assert.IsNotNull(externalIdentity);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(targetPath));
        using (var exact = ExactFileMutation.Open(targetPath))
        {
            Assert.AreEqual(externalIdentity, exact.Identity);
        }
        Assert.AreEqual(ModDeploymentPhase.RollingBack, service.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task FreshRecoveryPreservesSameByteReplacementOfPersistedTargetIdentity()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var crashing = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterFileCheckpoint: (checkpoint, _) => checkpoint == ModDeploymentFileCheckpoint.TargetDllInstalled
                ? ValueTask.FromException(new SimulatedProcessTerminationException(checkpoint))
                : ValueTask.CompletedTask);

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() =>
            crashing.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.Reject));
        var persisted = crashing.ReadJournal()!;
        Assert.AreEqual(2, persisted.SchemaVersion);
        Assert.IsNotNull(persisted.TargetArtifactFileIdentity);
        File.Delete(targetPath);
        File.WriteAllBytes(targetPath, ArtifactContents);
        CandidateFileIdentity externalIdentity;
        using (var exact = ExactFileMutation.Open(targetPath))
        {
            externalIdentity = exact.Identity;
        }

        var recovery = CreateService(temporaryDirectory, SuccessfulDownload());
        var result = await recovery.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(targetPath));
        using (var exact = ExactFileMutation.Open(targetPath))
        {
            Assert.AreEqual(externalIdentity, exact.Identity);
        }
        Assert.AreEqual(ModDeploymentPhase.RollingBack, recovery.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task CleanTargetRollbackDeletesAnExactReadOnlyTransactionDll()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var injected = false;
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterPhasePersisted: (phase, _) =>
            {
                if (!injected && phase == ModDeploymentPhase.CleanupPending)
                {
                    injected = true;
                    File.SetAttributes(
                        targetPath,
                        File.GetAttributes(targetPath) | FileAttributes.ReadOnly);
                    return ValueTask.FromException(new IOException("Injected cleanup-boundary failure."));
                }
                return ValueTask.CompletedTask;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State, result.Message);
        Assert.IsTrue(injected);
        Assert.IsFalse(File.Exists(targetPath));
        Assert.AreEqual(ModDeploymentResultState.Succeeded, (await service.RecoverAsync()).State);
    }

    [TestMethod]
    public async Task ArtifactCommitReceiptRetainsTheExactStagedIdentity()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        ExactFileRevision? committedRevision = null;
        var service = new ModDeploymentService(
            temporaryDirectory.CreateDirectory("state"),
            new FakeDownloader(SuccessfulDownload()),
            new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            new FakeAuthenticityVerifier(true),
            _ => false,
            DefaultAttribution(),
            timeProvider: null,
            afterPhasePersisted: null,
            reviewedCertification: null,
            afterFileCheckpoint: null,
            afterArtifactCommitted: (_, stagedRevision) =>
            {
                committedRevision = stagedRevision;
                return false;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
        Assert.IsNotNull(committedRevision);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(targetPath));
        using (var exact = ExactFileMutation.Open(targetPath))
        {
            Assert.IsTrue(committedRevision.Matches(exact.CaptureRevision()));
        }
        File.Delete(targetPath);
        File.WriteAllBytes(targetPath, ArtifactContents);
        CandidateFileIdentity externalIdentity;
        using (var exact = ExactFileMutation.Open(targetPath))
        {
            externalIdentity = exact.Identity;
        }
        Assert.IsNull(service.ReadInstalledState(gameDirectory));
        var journal = service.ReadJournal()!;
        Assert.AreEqual(ModDeploymentPhase.Committing, journal.Phase);
        Assert.IsTrue(journal.PreserveLiveArtifactDuringRecovery);

        var recovery = await service.RecoverAsync();
        var coordinatedRecovery = await service.RollBackCoordinatedAsync(journal.TransactionId);

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, recovery.State, recovery.Message);
        Assert.AreEqual(
            ModDeploymentResultState.RecoveryRequired,
            coordinatedRecovery.State,
            coordinatedRecovery.Message);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(targetPath));
        using (var exact = ExactFileMutation.Open(targetPath))
        {
            Assert.AreEqual(externalIdentity, exact.Identity);
        }
        Assert.IsNull(service.ReadInstalledState(gameDirectory));
    }

    [TestMethod]
    public async Task ExactArtifactOwnershipConfirmationClearsRecoveryQuarantineOnCommit()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        ExactFileRevision? committedRevision = null;
        var service = new ModDeploymentService(
            temporaryDirectory.CreateDirectory("state"),
            new FakeDownloader(SuccessfulDownload()),
            new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            new FakeAuthenticityVerifier(true),
            _ => false,
            DefaultAttribution(),
            timeProvider: null,
            afterPhasePersisted: null,
            reviewedCertification: null,
            afterFileCheckpoint: null,
            afterArtifactCommitted: (_, stagedRevision) =>
            {
                committedRevision = stagedRevision;
                return true;
            });

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.IsNotNull(committedRevision);
        using (var exact = ExactFileMutation.Open(Path.Combine(gameDirectory, "version.dll")))
        {
            Assert.IsTrue(committedRevision.Matches(exact.CaptureRevision()));
        }
        Assert.IsFalse(service.ReadJournal()!.PreserveLiveArtifactDuringRecovery);
        Assert.IsNotNull(service.ReadInstalledState(gameDirectory));
    }

    [TestMethod]
    public async Task ImmediateDeploymentFailureCannotReplaceTheLockedCommittedArtifact()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var replacementWasBlocked = false;
        var service = new ModDeploymentService(
            temporaryDirectory.CreateDirectory("state"),
            new FakeDownloader(SuccessfulDownload()),
            new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            new FakeAuthenticityVerifier(true),
            _ => false,
            DefaultAttribution(),
            timeProvider: null,
            afterPhasePersisted: null,
            reviewedCertification: null,
            afterFileCheckpoint: (checkpoint, _) =>
            {
                if (checkpoint != ModDeploymentFileCheckpoint.TargetDllInstalled)
                {
                    return ValueTask.CompletedTask;
                }
                try
                {
                    File.Delete(targetPath);
                }
                catch (IOException)
                {
                    replacementWasBlocked = true;
                }
                return ValueTask.FromException(new IOException("Injected post-promotion failure."));
            },
            afterArtifactCommitted: (_, _) => true);

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);
        Assert.AreEqual(ModDeploymentResultState.FailedAndRolledBack, result.State, result.Message);
        Assert.IsTrue(replacementWasBlocked);
        Assert.IsFalse(File.Exists(targetPath));
        Assert.IsFalse(service.ReadJournal()!.PreserveLiveArtifactDuringRecovery);
        Assert.IsNull(service.ReadInstalledState(gameDirectory));

        var recovery = await service.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.Succeeded, recovery.State, recovery.Message);
    }

    [TestMethod]
    public async Task RollingBackCheckpointReplacementIsPreservedByExactOwnershipQuarantine()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        CandidateFileIdentity? externalIdentity = null;
        var service = new ModDeploymentService(
            temporaryDirectory.CreateDirectory("state"),
            new FakeDownloader(SuccessfulDownload()),
            new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            new FakeAuthenticityVerifier(true),
            _ => false,
            DefaultAttribution(),
            timeProvider: null,
            afterPhasePersisted: (phase, _) =>
            {
                if (phase == ModDeploymentPhase.RollingBack)
                {
                    File.Delete(targetPath);
                    File.WriteAllBytes(targetPath, ArtifactContents);
                    using var exact = ExactFileMutation.Open(targetPath);
                    externalIdentity = exact.Identity;
                }
                return ValueTask.CompletedTask;
            },
            reviewedCertification: null,
            afterFileCheckpoint: (checkpoint, _) => checkpoint == ModDeploymentFileCheckpoint.TargetDllInstalled
                ? ValueTask.FromException(new IOException("Injected post-move failure."))
                : ValueTask.CompletedTask,
            afterArtifactCommitted: (_, _) => true);

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
        Assert.IsNotNull(externalIdentity);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(targetPath));
        using (var exact = ExactFileMutation.Open(targetPath))
        {
            Assert.AreEqual(externalIdentity, exact.Identity);
        }
        Assert.IsTrue(service.ReadJournal()!.PreserveLiveArtifactDuringRecovery);
        Assert.IsNull(service.ReadInstalledState(gameDirectory));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExactQuarantinePreservesLiveDllWhenRollbackBackupChanges(bool replaceBackup)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        File.WriteAllBytes(targetPath, [0x50, 0x52, 0x49, 0x4f, 0x52]);
        CandidateFileIdentity? committedIdentity = null;
        CandidateFileIdentity? replacementBackupIdentity = null;
        string? rollbackPath = null;
        ModDeploymentService? service = null;
        service = new ModDeploymentService(
            temporaryDirectory.CreateDirectory("state"),
            new FakeDownloader(SuccessfulDownload()),
            new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            new FakeAuthenticityVerifier(true),
            _ => false,
            DefaultAttribution(),
            timeProvider: null,
            afterPhasePersisted: (phase, _) =>
            {
                if (phase == ModDeploymentPhase.RollingBack)
                {
                    rollbackPath = service!.ReadJournal()!.SameVolumeBackupPath;
                    var rollbackBytes = File.ReadAllBytes(rollbackPath);
                    File.Delete(rollbackPath);
                    if (replaceBackup)
                    {
                        File.WriteAllBytes(rollbackPath, rollbackBytes);
                        using var exact = ExactFileMutation.Open(rollbackPath);
                        replacementBackupIdentity = exact.Identity;
                    }
                }
                return ValueTask.CompletedTask;
            },
            reviewedCertification: null,
            afterFileCheckpoint: (checkpoint, _) =>
            {
                if (checkpoint != ModDeploymentFileCheckpoint.TargetDllInstalled)
                {
                    return ValueTask.CompletedTask;
                }
                return ValueTask.FromException(new IOException("Injected post-move failure."));
            },
            afterArtifactCommitted: (_, _) => true);

        var result = await service.DeployAsync(
            gameDirectory,
            ReleaseArtifact(),
            ExistingArtifactPolicy.AdoptAndPreserve);

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
        using (var exact = ExactFileMutation.Open(targetPath))
        {
            committedIdentity = exact.Identity;
        }
        Assert.IsNotNull(committedIdentity);
        CollectionAssert.AreEqual(ArtifactContents, File.ReadAllBytes(targetPath));
        using (var exact = ExactFileMutation.Open(targetPath))
        {
            Assert.AreEqual(committedIdentity, exact.Identity);
        }
        Assert.IsNotNull(rollbackPath);
        Assert.AreEqual(replaceBackup, File.Exists(rollbackPath));
        if (replaceBackup)
        {
            using var exact = ExactFileMutation.Open(rollbackPath);
            Assert.AreEqual(replacementBackupIdentity, exact.Identity);
        }
        Assert.IsTrue(service.ReadJournal()!.PreserveLiveArtifactDuringRecovery);
        Assert.IsNull(service.ReadInstalledState(gameDirectory));
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

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("v999999999999999999.1.1-guffa.1")]
    public void RegistryRejectsMalformedReleaseHighWaterVersions(string? releaseProductVersion)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        var state = InstalledState(gameDirectory) with
        {
            ReleaseHighWaterMarks =
            [
                new(
                    "netniv",
                    "stable",
                    "netniv.stfc-community-mod",
                    releaseProductVersion!,
                    ArtifactContents.LongLength,
                    ReleaseArtifact().Sha256),
            ],
        };
        File.WriteAllText(
            service.InstalledStatePath,
            JsonSerializer.Serialize(
                new ModInstalledArtifactRegistry(2, [state], []),
                JournalJsonOptions));

        Assert.ThrowsException<InvalidDataException>(() => service.ReadInstalledStates());
        var health = new ModInstallationInspector(
            service,
            new SystemModInstallationFileSystem()).Capture(gameDirectory, isGameRunning: false);
        Assert.AreEqual(ModInstallationEvidenceState.Unavailable, health.State);
    }

    [TestMethod]
    public void RegistryRejectsAConflictingActiveTupleHighWaterMark()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        var state = InstalledState(gameDirectory) with
        {
            ReleaseProductVersion = "v2.1.0-guffa.9",
            ReleaseHighWaterMarks =
            [
                new(
                    "guffawaffle",
                    "stable",
                    "guffawaffle.windows",
                    "v2.1.0-guffa.10",
                    ArtifactContents.LongLength,
                    ReleaseArtifact().Sha256),
            ],
        };
        File.WriteAllText(
            service.InstalledStatePath,
            JsonSerializer.Serialize(
                new ModInstalledArtifactRegistry(2, [state], []),
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
        var initialArtifact = ReleaseArtifact() with
        {
            ExpectedProductVersion = "v2.1.0-guffa.8",
        };
        var firstService = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            versionReader: new FakeProductVersionReader(
                initialArtifact.ExpectedVersion,
                initialArtifact.ExpectedProductVersion));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await firstService.DeployAsync(
                gameDirectory,
                initialArtifact,
                ExistingArtifactPolicy.Reject)).State);
        var updatedContents = new byte[] { 9, 8, 7, 6 };
        var updatedArtifact = new ModReleaseArtifact(
            new Uri("https://example.invalid/version.dll"),
            "version.dll",
            updatedContents.LongLength,
            Convert.ToHexString(SHA256.HashData(updatedContents)),
            "2.1.0.9")
        {
            ExpectedProductVersion = "v2.1.0-guffa.9",
        };
        var updateService = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, updatedContents, updatedContents.LongLength),
            versionReader: new FakeProductVersionReader(
                updatedArtifact.ExpectedVersion,
                updatedArtifact.ExpectedProductVersion));

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
        var initialArtifact = ReleaseArtifact() with
        {
            ExpectedProductVersion = "v2.1.0-guffa.8",
        };
        var installService = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            versionReader: new FakeProductVersionReader(
                initialArtifact.ExpectedVersion,
                initialArtifact.ExpectedProductVersion));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await installService.DeployAsync(
                gameDirectory,
                initialArtifact,
                ExistingArtifactPolicy.AdoptAndPreserve)).State);

        var updatedContents = new byte[] { 9, 8, 7, 6 };
        var updatedArtifact = new ModReleaseArtifact(
            new Uri("https://example.invalid/version.dll"),
            "version.dll",
            updatedContents.LongLength,
            Convert.ToHexString(SHA256.HashData(updatedContents)),
            "2.1.0.9")
        {
            ExpectedProductVersion = "v2.1.0-guffa.9",
        };
        var updateService = CreateService(
            temporaryDirectory,
            new ModArtifactDownload(HttpStatusCode.OK, updatedContents, updatedContents.LongLength),
            versionReader: new FakeProductVersionReader(
                updatedArtifact.ExpectedVersion,
                updatedArtifact.ExpectedProductVersion));
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
        var sourceArtifact = ReleaseArtifact() with
        {
            ExpectedProductVersion = "v2.1.0-guffa.8",
        };
        var sourceService = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            versionReader: new FakeProductVersionReader(
                sourceArtifact.ExpectedVersion,
                sourceArtifact.ExpectedProductVersion),
            installationAttribution: new("guffawaffle", "stable", "guffawaffle.windows"));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await sourceService.DeployAsync(
                gameDirectory,
                sourceArtifact,
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
        var sourceArtifact = ReleaseArtifact() with
        {
            ExpectedProductVersion = "v2.1.0-guffa.8",
        };
        var sourceService = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            versionReader: new FakeProductVersionReader(
                sourceArtifact.ExpectedVersion,
                sourceArtifact.ExpectedProductVersion));
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await sourceService.DeployAsync(
                gameDirectory,
                sourceArtifact,
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
        var previousLastWriteTimeUtc = new DateTime(2026, 8, 18, 12, 34, 56, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(targetPath, previousLastWriteTimeUtc);
        var previousAttributes = File.GetAttributes(targetPath) | FileAttributes.ReadOnly;
        File.SetAttributes(targetPath, previousAttributes);
        var service = CreateService(temporaryDirectory, SuccessfulDownload());
        Assert.AreEqual(
            ModDeploymentResultState.Succeeded,
            (await service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.AdoptAndPreserve)).State);
        var backupPath = service.ReadInstalledState(gameDirectory)!.PreviousArtifactBackupPath!;
        Assert.AreEqual(previousLastWriteTimeUtc, File.GetLastWriteTimeUtc(backupPath));
        Assert.AreEqual(previousAttributes, File.GetAttributes(backupPath));

        var result = await service.UninstallAsync(gameDirectory);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State);
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(targetPath));
        Assert.AreEqual(previousLastWriteTimeUtc, File.GetLastWriteTimeUtc(targetPath));
        Assert.AreEqual(previousAttributes, File.GetAttributes(targetPath));
        Assert.IsNull(service.ReadInstalledState());
        File.SetAttributes(targetPath, previousAttributes & ~FileAttributes.ReadOnly);
    }

    [TestMethod]
    public async Task InterruptedDurableBackupMetadataIsRebuiltFromTheExactSourceDuringRecovery()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var previous = new byte[] { 7, 7, 7, 7 };
        var expectedTime = new DateTime(2026, 8, 18, 11, 22, 33, DateTimeKind.Utc);
        File.WriteAllBytes(targetPath, previous);
        File.SetLastWriteTimeUtc(targetPath, expectedTime);
        var expectedAttributes = File.GetAttributes(targetPath);
        var interrupted = false;
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterDurableCopyBytesFlushed: (_, _, _) =>
            {
                if (!interrupted)
                {
                    interrupted = true;
                    throw new SimulatedProcessTerminationException(
                        ModDeploymentFileCheckpoint.DurableDllBackupCopyStarted);
                }
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() =>
            service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.AdoptAndPreserve));

        var recovery = CreateService(temporaryDirectory, SuccessfulDownload());
        var result = await recovery.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(targetPath));
        Assert.AreEqual(expectedTime, File.GetLastWriteTimeUtc(targetPath));
        Assert.AreEqual(expectedAttributes, File.GetAttributes(targetPath));
        Assert.IsNull(recovery.ReadInstalledState(gameDirectory));
        Assert.IsFalse(Directory.EnumerateFiles(gameDirectory, "*.stage").Any());
        Assert.IsFalse(Directory.EnumerateFiles(gameDirectory, "*.rollback").Any());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecoveryPreservesAnExternallyChangedIncompleteCopyStage(bool replaceFile)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        File.WriteAllBytes(targetPath, [6, 6, 6, 6]);
        string? interruptedStagePath = null;
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterDurableCopyBytesFlushed: (_, destination, _) =>
            {
                interruptedStagePath = destination;
                throw new SimulatedProcessTerminationException(
                    ModDeploymentFileCheckpoint.DurableDllBackupCopyStarted);
            });
        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() =>
            service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.AdoptAndPreserve));
        Assert.IsNotNull(interruptedStagePath);
        var external = new byte[] { 9, 8, 7, 6, 5 };
        if (replaceFile)
        {
            File.Delete(interruptedStagePath);
        }
        File.WriteAllBytes(interruptedStagePath, external);

        var recovery = CreateService(temporaryDirectory, SuccessfulDownload());
        var result = await recovery.RecoverAsync();

        if (replaceFile)
        {
            Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
            CollectionAssert.AreEqual(external, File.ReadAllBytes(interruptedStagePath));
            StringAssert.Contains(recovery.ReadJournal()!.Error!, "replaced");
        }
        else
        {
            Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
            Assert.IsFalse(File.Exists(interruptedStagePath));
            CollectionAssert.AreEqual(new byte[] { 6, 6, 6, 6 }, File.ReadAllBytes(targetPath));
        }
    }

    [TestMethod]
    public async Task RecoveryRebuildsABridgeOwnedStageAfterAMidCopyInterruption()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var previous = Enumerable.Range(0, 200_000)
            .Select(index => checked((byte)(index % 251)))
            .ToArray();
        File.WriteAllBytes(targetPath, previous);
        string? interruptedStagePath = null;
        var interrupted = false;
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterDurableCopyChunkWritten: (_, destination, written, _) =>
            {
                if (!interrupted)
                {
                    interrupted = true;
                    interruptedStagePath = destination;
                    Assert.IsTrue(written < previous.LongLength);
                    throw new SimulatedProcessTerminationException(
                        ModDeploymentFileCheckpoint.DurableDllBackupCopyStarted);
                }
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() =>
            service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.AdoptAndPreserve));
        Assert.IsNotNull(interruptedStagePath);
        Assert.IsTrue(File.Exists(interruptedStagePath));

        var recovery = CreateService(temporaryDirectory, SuccessfulDownload());
        var result = await recovery.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(targetPath));
        Assert.IsFalse(File.Exists(interruptedStagePath));
        Assert.IsNull(recovery.ReadInstalledState(gameDirectory));
    }

    [TestMethod]
    public async Task RecoveryDiscardsACorruptOrphanCopyStageReceiptWhenTheStageIsAbsent()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var previous = new byte[] { 6, 5, 4, 3 };
        File.WriteAllBytes(targetPath, previous);
        string? stagePath = null;
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterDurableCopyBytesFlushed: (_, destination, _) =>
            {
                stagePath = destination;
                throw new SimulatedProcessTerminationException(
                    ModDeploymentFileCheckpoint.DurableDllBackupCopyStarted);
            });
        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() =>
            service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.AdoptAndPreserve));
        Assert.IsNotNull(stagePath);
        File.Delete(stagePath);
        var ownershipDirectory = Path.Combine(
            temporaryDirectory.Path,
            "state",
            "copy-stage-ownership");
        var receiptPath = Directory.EnumerateFiles(ownershipDirectory, "*.json").Single();
        File.WriteAllText(receiptPath, "{ corrupt");

        var recovery = CreateService(temporaryDirectory, SuccessfulDownload());
        var result = await recovery.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(targetPath));
        Assert.IsFalse(File.Exists(receiptPath));
    }

    [TestMethod]
    public async Task RecoveryPreservesAnExactDuplicateThatReplacedACompletedCopyStage()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var previous = new byte[] { 4, 3, 2, 1 };
        File.WriteAllBytes(targetPath, previous);
        string? replacedStagePath = null;
        var service = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterDurableCopyCompleted: (_, destination, _) =>
            {
                replacedStagePath = destination;
                var contents = File.ReadAllBytes(destination);
                var attributes = File.GetAttributes(destination);
                var lastWriteTime = File.GetLastWriteTimeUtc(destination);
                File.Delete(destination);
                File.WriteAllBytes(destination, contents);
                File.SetLastWriteTimeUtc(destination, lastWriteTime);
                File.SetAttributes(destination, attributes);
                throw new SimulatedProcessTerminationException(
                    ModDeploymentFileCheckpoint.DurableDllBackupCopyStarted);
            });

        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() =>
            service.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.AdoptAndPreserve));
        Assert.IsNotNull(replacedStagePath);

        var recovery = CreateService(temporaryDirectory, SuccessfulDownload());
        var result = await recovery.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.RecoveryRequired, result.State, result.Message);
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(replacedStagePath));
        StringAssert.Contains(recovery.ReadJournal()!.Error!, "replaced");
    }

    [TestMethod]
    public async Task RecoveryAfterReadOnlyRollbackRestoreIsIdempotent()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var targetPath = Path.Combine(gameDirectory, "version.dll");
        var previous = new byte[] { 4, 4, 4, 4 };
        File.WriteAllBytes(targetPath, previous);
        var expectedTime = new DateTime(2026, 8, 18, 10, 20, 30, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(targetPath, expectedTime);
        var expectedAttributes = File.GetAttributes(targetPath) | FileAttributes.ReadOnly;
        File.SetAttributes(targetPath, expectedAttributes);
        var firstStop = true;
        var deploy = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterFileCheckpoint: (checkpoint, _) =>
            {
                if (firstStop && checkpoint == ModDeploymentFileCheckpoint.DurableDllBackupPromoted)
                {
                    firstStop = false;
                    throw new SimulatedProcessTerminationException(checkpoint);
                }
                return ValueTask.CompletedTask;
            });
        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(() =>
            deploy.DeployAsync(
                gameDirectory,
                ReleaseArtifact(),
                ExistingArtifactPolicy.AdoptAndPreserve));
        var secondStop = true;
        var firstRecovery = CreateService(
            temporaryDirectory,
            SuccessfulDownload(),
            afterFileCheckpoint: (checkpoint, _) =>
            {
                if (secondStop && checkpoint == ModDeploymentFileCheckpoint.RollbackDllRestored)
                {
                    secondStop = false;
                    throw new SimulatedProcessTerminationException(checkpoint);
                }
                return ValueTask.CompletedTask;
            });
        await Assert.ThrowsExceptionAsync<SimulatedProcessTerminationException>(
            () => firstRecovery.RecoverAsync());
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(targetPath));
        Assert.AreEqual(expectedAttributes, File.GetAttributes(targetPath));

        var recovery = CreateService(temporaryDirectory, SuccessfulDownload());
        var result = await recovery.RecoverAsync();

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(targetPath));
        Assert.AreEqual(expectedTime, File.GetLastWriteTimeUtc(targetPath));
        Assert.AreEqual(expectedAttributes, File.GetAttributes(targetPath));
        Assert.IsNull(recovery.ReadInstalledState(gameDirectory));
        Assert.IsFalse(Directory.EnumerateFiles(gameDirectory, "*.stage").Any());
        Assert.IsFalse(Directory.EnumerateFiles(gameDirectory, "*.rollback").Any());
        File.SetAttributes(targetPath, expectedAttributes & ~FileAttributes.ReadOnly);
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
    public void LegacyJournalCannotCarryV2TargetFileIdentity()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var transactionId = Guid.NewGuid().ToString("N");
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
            DateTimeOffset.UtcNow,
            TargetArtifactFileIdentity: new("1234ABCD", "0123456789ABCDEF"));
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

        Assert.ThrowsException<InvalidDataException>(() => service.ReadJournal());
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
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
        ModInstallationAttribution? installationAttribution = null,
        IEnumerable<ReviewedReleaseCertification>? reviewedCertifications = null,
        Func<string, string, CancellationToken, ValueTask>? afterDurableCopyBytesFlushed = null,
        Func<string, string, long, CancellationToken, ValueTask>? afterDurableCopyChunkWritten = null,
        Func<string, string, CancellationToken, ValueTask>? afterDurableCopyCompleted = null,
        Func<ModDeploymentFileCheckpoint, CancellationToken, ValueTask>? afterFileCheckpoint = null) =>
        CreateService(
            temporaryDirectory,
            new FakeDownloader(download),
            isGameRunning,
            versionReader,
            authenticityVerifier,
            afterPhasePersisted,
            installationAttribution,
            reviewedCertifications,
            afterDurableCopyBytesFlushed,
            afterDurableCopyChunkWritten,
            afterDurableCopyCompleted,
            afterFileCheckpoint);

    private static ModDeploymentService CreateService(
        TemporaryDirectory temporaryDirectory,
        IModArtifactDownloader downloader,
        Func<string, bool>? isGameRunning = null,
        IModArtifactVersionReader? versionReader = null,
        IModArtifactAuthenticityVerifier? authenticityVerifier = null,
        Func<ModDeploymentPhase, CancellationToken, ValueTask>? afterPhasePersisted = null,
        ModInstallationAttribution? installationAttribution = null,
        IEnumerable<ReviewedReleaseCertification>? reviewedCertifications = null,
        Func<string, string, CancellationToken, ValueTask>? afterDurableCopyBytesFlushed = null,
        Func<string, string, long, CancellationToken, ValueTask>? afterDurableCopyChunkWritten = null,
        Func<string, string, CancellationToken, ValueTask>? afterDurableCopyCompleted = null,
        Func<ModDeploymentFileCheckpoint, CancellationToken, ValueTask>? afterFileCheckpoint = null) =>
        new ModDeploymentService(
            temporaryDirectory.CreateDirectory("state"),
            downloader,
            versionReader ?? new FakeVersionReader(ReleaseArtifact().ExpectedVersion),
            authenticityVerifier ?? new FakeAuthenticityVerifier(true),
            isGameRunning ?? (_ => false),
            installationAttribution ?? DefaultAttribution(),
            timeProvider: null,
            afterPhasePersisted,
            reviewedCertification: null,
            afterFileCheckpoint,
            reviewedCertifications: reviewedCertifications,
            afterDurableCopyBytesFlushed: afterDurableCopyBytesFlushed,
            afterDurableCopyChunkWritten: afterDurableCopyChunkWritten,
            afterDurableCopyCompleted: afterDurableCopyCompleted);

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

    private static ReviewedReleaseCertification ReviewedCertification(
        ModInstallationAttribution attribution,
        string tag,
        ModReleaseArtifact artifact) => new(
            attribution.ProviderId,
            attribution.ReleaseChannelId,
            attribution.RuntimeDistributionId,
            "example/repository",
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

    private sealed class FakeProductVersionReader(string? version, string? productVersion)
        : IModArtifactProductVersionReader
    {
        public string? ReadVersion(string artifactPath) => version;

        public string? ReadProductVersion(string artifactPath) => productVersion;
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
