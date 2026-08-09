using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ReviewedModArtifactCandidateTests
{
    private static readonly byte[] DllBytes = Encoding.UTF8.GetBytes("exact reviewed candidate DLL");
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [TestMethod]
    public async Task DllOnlyCandidateIsLockedSingleUseAndDeploymentDoesNotDownloadAgain()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var downloader = new CountingDownloader(fixture.Downloads);
        var acquirer = CreateAcquirer(temporaryDirectory, fixture, downloader);
        var lease = await acquirer.AcquireAsync(fixture.Artifact);
        var candidateDirectory = lease.CandidateDirectory;
        var candidatePath = Path.Combine(candidateDirectory, "version.dll");

        Assert.AreEqual(1, downloader.CallCount);
        Assert.AreEqual(fixture.Artifact, lease.Receipt.Artifact);
        Assert.AreEqual(fixture.Attribution, lease.Receipt.InstallationAttribution);
        AssertCannotOpenForWrite(candidatePath);
        AssertCannotDelete(candidatePath);

        var deployment = CreateDeployment(temporaryDirectory, fixture, new ThrowingDownloader());
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var result = await deployment.DeployCandidateAsync(
            gameDirectory,
            lease,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(DllBytes, File.ReadAllBytes(Path.Combine(gameDirectory, "version.dll")));
        Assert.AreEqual(1, downloader.CallCount);
        Assert.IsFalse(Directory.Exists(candidateDirectory));

        var replay = await deployment.DeployCandidateAsync(
            gameDirectory,
            lease,
            ExistingArtifactPolicy.Reject);
        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, replay.State);
    }

    [TestMethod]
    public async Task ExactPairCandidateCommitsTheAcquiredCompanionWithoutRedownload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: true);
        var downloader = new CountingDownloader(fixture.Downloads);
        var lease = await CreateAcquirer(temporaryDirectory, fixture, downloader)
            .AcquireAsync(fixture.Artifact);
        Assert.IsNotNull(lease.Receipt.RuntimeManifestIdentity);
        Assert.IsNotNull(lease.Receipt.RuntimeActivation);

        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var result = await CreateDeployment(temporaryDirectory, fixture, new ThrowingDownloader())
            .DeployCandidateAsync(gameDirectory, lease, ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.AreEqual(2, downloader.CallCount);
        CollectionAssert.AreEqual(
            fixture.RuntimeManifestBytes,
            File.ReadAllBytes(Path.Combine(
                gameDirectory,
                ArtifactBoundRuntimeManifestParser.ManagedFileName)));
    }

    [TestMethod]
    public async Task StaleCertificationRejectsReceiptBeforeJournalOrLiveMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var lease = await CreateAcquirer(
            temporaryDirectory,
            fixture,
            new CountingDownloader(fixture.Downloads)).AcquireAsync(fixture.Artifact);
        var candidateDirectory = lease.CandidateDirectory;
        var stale = fixture with
        {
            Certification = fixture.Certification with
            {
                ObservedAtUtc = fixture.Certification.ObservedAtUtc.AddSeconds(1),
            },
        };
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var deployment = CreateDeployment(temporaryDirectory, stale, new ThrowingDownloader());

        var result = await deployment.DeployCandidateAsync(
            gameDirectory,
            lease,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State);
        Assert.IsFalse(File.Exists(deployment.JournalPath));
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        Assert.IsFalse(Directory.Exists(candidateDirectory));
    }

    [TestMethod]
    public async Task CandidateCannotCrossInstallationAttributionAuthority()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var lease = await CreateAcquirer(
            temporaryDirectory,
            fixture,
            new CountingDownloader(fixture.Downloads)).AcquireAsync(fixture.Artifact);
        var otherAttribution = fixture with
        {
            Attribution = fixture.Attribution with { ReleaseChannelId = "preview" },
        };
        var deployment = CreateDeployment(temporaryDirectory, otherAttribution, new ThrowingDownloader());

        var result = await deployment.DeployCandidateAsync(
            CreateGameDirectory(temporaryDirectory),
            lease,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State);
        Assert.IsFalse(File.Exists(deployment.JournalPath));
    }

    [TestMethod]
    public async Task CoordinateMismatchAndCancellationCleanOnlyOwnedCandidateResidue()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var counting = new CountingDownloader(fixture.Downloads);
        var acquirer = CreateAcquirer(temporaryDirectory, fixture, counting);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => acquirer.AcquireAsync(
            fixture.Artifact with { DownloadUri = new Uri("https://example.invalid/version.dll") }));
        Assert.AreEqual(0, counting.CallCount);

        var blocking = new CancelingDownloader();
        var cancelingAcquirer = CreateAcquirer(temporaryDirectory, fixture, blocking);
        using var cancellation = new CancellationTokenSource();
        var pending = cancelingAcquirer.AcquireAsync(fixture.Artifact, cancellation.Token);
        await blocking.Entered.Task;
        var candidateRoot = Path.Combine(temporaryDirectory.Path, "state", "artifact-candidates");
        var candidateDirectory = Directory.GetDirectories(candidateRoot).Single();
        var foreignPath = Path.Combine(candidateDirectory, "foreign.txt");
        File.WriteAllText(foreignPath, "not candidate-owned");
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => pending);
        Assert.IsTrue(File.Exists(foreignPath));
        Assert.IsFalse(File.Exists(Path.Combine(candidateDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task CoordinatedCandidateUsesSameReceiptAndParticipantBoundary()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var downloader = new CountingDownloader(fixture.Downloads);
        var lease = await CreateAcquirer(temporaryDirectory, fixture, downloader)
            .AcquireAsync(fixture.Artifact);
        var participant = new RecordingParticipant();

        var result = await CreateDeployment(temporaryDirectory, fixture, new ThrowingDownloader())
            .DeployCandidateCoordinatedAsync(
                CreateGameDirectory(temporaryDirectory),
                lease,
                ExistingArtifactPolicy.Reject,
                Guid.NewGuid().ToString("N"),
                participant);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.AreEqual(1, downloader.CallCount);
        Assert.AreEqual(1, participant.BeginCount);
        Assert.AreEqual(1, participant.CommitCount);
        Assert.AreEqual(1, participant.CompleteCount);
    }

    [TestMethod]
    public async Task DisposeWithoutConsumptionDeletesOnlyTheLockedCandidate()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var lease = await CreateAcquirer(
            temporaryDirectory,
            fixture,
            new CountingDownloader(fixture.Downloads)).AcquireAsync(fixture.Artifact);
        var directory = lease.CandidateDirectory;

        await lease.DisposeAsync();

        Assert.IsFalse(Directory.Exists(directory));
        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task FailureAfterDllLockDeletesExactDllButPreservesForeignSibling()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: true);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var downloader = new FailOnCompanionDownloader(
            fixture.Downloads[fixture.Artifact.DownloadUri],
            Path.Combine(stateDirectory, "artifact-candidates"));
        var acquirer = new ReviewedModArtifactCandidateAcquirer(
            stateDirectory,
            downloader,
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            fixture.Attribution,
            fixture.Certification);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => acquirer.AcquireAsync(fixture.Artifact));

        Assert.IsNotNull(downloader.CandidateDirectory);
        Assert.IsFalse(File.Exists(Path.Combine(downloader.CandidateDirectory, "version.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(downloader.CandidateDirectory, "foreign.txt")));
    }

    [TestMethod]
    public async Task CancellationWithOpenPartialFileDeletesThatExactHandle()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquirer = new ReviewedModArtifactCandidateAcquirer(
            stateDirectory,
            new CountingDownloader(fixture.Downloads),
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            fixture.Attribution,
            fixture.Certification,
            async (path, token) =>
            {
                opened.TrySetResult(path);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        using var cancellation = new CancellationTokenSource();
        var pending = acquirer.AcquireAsync(fixture.Artifact, cancellation.Token);
        var candidatePath = await opened.Task;

        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => pending);
        Assert.IsFalse(File.Exists(candidatePath));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(candidatePath)));
    }

    [TestMethod]
    public async Task PathVerifierFailureDeletesCompletedDllAndPreservesForeignSibling()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var verifier = new RejectingVerifierWithForeignSibling();
        var acquirer = new ReviewedModArtifactCandidateAcquirer(
            stateDirectory,
            new CountingDownloader(fixture.Downloads),
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            verifier,
            fixture.Attribution,
            fixture.Certification);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => acquirer.AcquireAsync(fixture.Artifact));

        Assert.IsNotNull(verifier.CandidateDirectory);
        Assert.IsFalse(File.Exists(Path.Combine(verifier.CandidateDirectory, "version.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(verifier.CandidateDirectory, "foreign.txt")));
    }

    [TestMethod]
    public async Task CleanupFailureStopsBeforeJournalLiveMutationAndCoordinatedParticipant()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var deleteMarker = new DelayedDeleteMarker(failures: 2);
        var lease = await new ReviewedModArtifactCandidateAcquirer(
            stateDirectory,
            new CountingDownloader(fixture.Downloads),
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            fixture.Attribution,
            fixture.Certification,
            afterCandidateFileOpened: null,
            markCandidateDelete: deleteMarker.Mark).AcquireAsync(fixture.Artifact);
        var candidateDirectory = lease.CandidateDirectory;
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var phases = new List<ModDeploymentPhase>();
        var participant = new RecordingParticipant();
        var deployment = new ModDeploymentService(
            stateDirectory,
            new ThrowingDownloader(),
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            _ => false,
            fixture.Attribution,
            afterPhasePersisted: (phase, _) =>
            {
                phases.Add(phase);
                return ValueTask.CompletedTask;
            },
            reviewedCertification: fixture.Certification);

        var result = await deployment.DeployCandidateCoordinatedAsync(
            gameDirectory,
            lease,
            ExistingArtifactPolicy.Reject,
            Guid.NewGuid().ToString("N"),
            participant);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State);
        Assert.AreEqual(0, phases.Count);
        Assert.AreEqual(0, participant.BeginCount);
        Assert.IsFalse(File.Exists(deployment.JournalPath));
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        AssertCannotDelete(Path.Combine(candidateDirectory, "version.dll"));
        await lease.DisposeAsync();
        Assert.IsFalse(Directory.Exists(candidateDirectory));
    }

    [TestMethod]
    public async Task CandidateIsDeletedBeforePlannedPhasePersists()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var lease = await new ReviewedModArtifactCandidateAcquirer(
            stateDirectory,
            new CountingDownloader(fixture.Downloads),
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            fixture.Attribution,
            fixture.Certification).AcquireAsync(fixture.Artifact);
        var candidateDirectory = lease.CandidateDirectory;
        var candidateWasGoneAtPlanned = false;
        var deployment = new ModDeploymentService(
            stateDirectory,
            new ThrowingDownloader(),
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            _ => false,
            fixture.Attribution,
            afterPhasePersisted: (phase, _) =>
            {
                if (phase == ModDeploymentPhase.Planned)
                {
                    candidateWasGoneAtPlanned = !Directory.Exists(candidateDirectory);
                }
                return ValueTask.CompletedTask;
            },
            reviewedCertification: fixture.Certification);

        var result = await deployment.DeployCandidateAsync(
            CreateGameDirectory(temporaryDirectory),
            lease,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.IsTrue(candidateWasGoneAtPlanned);
    }

    [TestMethod]
    public async Task EarlyReturnCleanupFailureRemainsATypedPreMutationResult()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var deleteMarker = new DelayedDeleteMarker(failures: 1);
        var lease = await new ReviewedModArtifactCandidateAcquirer(
            stateDirectory,
            new CountingDownloader(fixture.Downloads),
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            fixture.Attribution,
            fixture.Certification,
            afterCandidateFileOpened: null,
            markCandidateDelete: deleteMarker.Mark).AcquireAsync(fixture.Artifact);
        var candidateDirectory = lease.CandidateDirectory;
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var deployment = new ModDeploymentService(
            stateDirectory,
            new ThrowingDownloader(),
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            _ => true,
            fixture.Attribution,
            reviewedCertification: fixture.Certification);

        var result = await deployment.DeployCandidateAsync(
            gameDirectory,
            lease,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State);
        StringAssert.Contains(result.Message, "could not be cleaned safely");
        Assert.IsFalse(File.Exists(deployment.JournalPath));
        Assert.IsFalse(File.Exists(Path.Combine(gameDirectory, "version.dll")));
        await lease.DisposeAsync();
        Assert.IsFalse(Directory.Exists(candidateDirectory));
    }

    [TestMethod]
    public async Task CleanupFailureDoesNotMigrateLegacyInstalledStateBytes()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var deleteMarker = new DelayedDeleteMarker(failures: 2);
        var lease = await new ReviewedModArtifactCandidateAcquirer(
            stateDirectory,
            new CountingDownloader(fixture.Downloads),
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            fixture.Attribution,
            fixture.Certification,
            afterCandidateFileOpened: null,
            markCandidateDelete: deleteMarker.Mark).AcquireAsync(fixture.Artifact);
        var candidateDirectory = lease.CandidateDirectory;
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        File.WriteAllBytes(Path.Combine(gameDirectory, "version.dll"), DllBytes);
        var backupPath = Path.Combine(stateDirectory, "rollback", "legacy-version.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.WriteAllBytes(backupPath, Encoding.UTF8.GetBytes("legacy adopted DLL"));
        var installedStatePath = Path.Combine(stateDirectory, "installed-mod.json");
        var legacyState = new ModInstalledArtifactState(
            1,
            gameDirectory,
            "version.dll",
            fixture.Artifact.ExpectedVersion,
            fixture.Artifact.Size,
            fixture.Artifact.Sha256,
            DateTimeOffset.Parse("2026-08-09T00:00:00.0000000+00:00", CultureInfo.InvariantCulture),
            backupPath,
            fixture.Attribution.ProviderId,
            fixture.Attribution.ReleaseChannelId,
            fixture.Attribution.RuntimeDistributionId);
        var originalStateBytes = JsonSerializer.SerializeToUtf8Bytes(legacyState, StateJsonOptions);
        File.WriteAllBytes(installedStatePath, originalStateBytes);
        var deployment = new ModDeploymentService(
            stateDirectory,
            new ThrowingDownloader(),
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            _ => false,
            fixture.Attribution,
            reviewedCertification: fixture.Certification);

        var result = await deployment.DeployCandidateAsync(
            gameDirectory,
            lease,
            ExistingArtifactPolicy.Reject);

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, result.State);
        CollectionAssert.AreEqual(originalStateBytes, File.ReadAllBytes(installedStatePath));
        Assert.IsFalse(File.Exists(deployment.JournalPath));
        await lease.DisposeAsync();
        Assert.IsFalse(Directory.Exists(candidateDirectory));
    }

    [TestMethod]
    public async Task DoubleSubmitAndConcurrentDisposeCannotStealTheWinningClaim()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = await new ReviewedModArtifactCandidateAcquirer(
            stateDirectory,
            new CountingDownloader(fixture.Downloads),
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            fixture.Attribution,
            fixture.Certification,
            afterCandidateFileOpened: null,
            markCandidateDelete: null,
            afterDeploymentClaimed: async _ =>
            {
                claimed.TrySetResult();
                await release.Task;
            }).AcquireAsync(fixture.Artifact);
        var candidateDirectory = lease.CandidateDirectory;
        var candidatePath = Path.Combine(candidateDirectory, "version.dll");
        var deployment = CreateDeployment(temporaryDirectory, fixture, new ThrowingDownloader());
        var gameDirectory = CreateGameDirectory(temporaryDirectory);
        var winner = deployment.DeployCandidateAsync(
            gameDirectory,
            lease,
            ExistingArtifactPolicy.Reject);
        await claimed.Task;

        var loser = await deployment.DeployCandidateAsync(
            gameDirectory,
            lease,
            ExistingArtifactPolicy.Reject);
        var disposeFailure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => lease.DisposeAsync().AsTask());

        Assert.AreEqual(ModDeploymentResultState.VerificationFailed, loser.State);
        StringAssert.Contains(loser.Message, "already claimed");
        StringAssert.Contains(disposeFailure.Message, "active deployment");
        AssertCannotOpenForWrite(candidatePath);
        AssertCannotDelete(candidatePath);

        release.TrySetResult();
        var result = await winner;
        Assert.AreEqual(ModDeploymentResultState.Succeeded, result.State, result.Message);
        Assert.IsFalse(Directory.Exists(candidateDirectory));
    }

    [TestMethod]
    public async Task AcquisitionCleanupFailureRetainsExactHandleForBoundedRetry()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var verifier = new RejectingVerifierWithForeignSibling();
        var deleteMarker = new DelayedDeleteMarker(failures: 2);
        var downloader = new CountingDownloader(fixture.Downloads);
        var acquirer = new ReviewedModArtifactCandidateAcquirer(
            stateDirectory,
            downloader,
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            verifier,
            fixture.Attribution,
            fixture.Certification,
            afterCandidateFileOpened: null,
            markCandidateDelete: deleteMarker.Mark);

        await Assert.ThrowsExceptionAsync<AggregateException>(() => acquirer.AcquireAsync(fixture.Artifact));

        Assert.IsTrue(acquirer.HasPendingCleanup);
        Assert.IsNotNull(verifier.CandidateDirectory);
        var candidatePath = Path.Combine(verifier.CandidateDirectory, "version.dll");
        AssertCannotOpenForWrite(candidatePath);
        AssertCannotDelete(candidatePath);

        await Assert.ThrowsExceptionAsync<IOException>(() => acquirer.AcquireAsync(fixture.Artifact));
        Assert.AreEqual(1, downloader.CallCount, "Pending exact cleanup must block a second download.");
        Assert.IsTrue(acquirer.HasPendingCleanup);

        await acquirer.RetryPendingCleanupAsync();

        Assert.IsFalse(acquirer.HasPendingCleanup);
        Assert.IsFalse(File.Exists(candidatePath));
        Assert.IsTrue(File.Exists(Path.Combine(verifier.CandidateDirectory, "foreign.txt")));
        await acquirer.DisposeAsync();
        await acquirer.DisposeAsync();
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => acquirer.AcquireAsync(fixture.Artifact));
    }

    [TestMethod]
    public void ConstructionIsPassiveAndCreatesNoCandidateRoot()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = CandidateFixture.Create(includeRuntimeManifest: false);
        var stateDirectory = Path.Combine(temporaryDirectory.Path, "never-created-state");

        _ = new ReviewedModArtifactCandidateAcquirer(
            stateDirectory,
            new ThrowingDownloader(),
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            fixture.Attribution,
            fixture.Certification);

        Assert.IsFalse(Directory.Exists(stateDirectory));
    }

    [TestMethod]
    public async Task WrongSourceOversizeAndDuplicateManifestFailClosedBeforeLeaseIssuance()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = CandidateFixture.Create(includeRuntimeManifest: true);
        var downloader = new CountingDownloader(pair.Downloads);
        var acquirer = CreateAcquirer(temporaryDirectory, pair, downloader);
        var wrongSource = pair.Artifact with
        {
            RuntimeManifest = pair.Artifact.RuntimeManifest! with
            {
                ExpectedSourceRevision = new string('f', 40),
            },
        };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => acquirer.AcquireAsync(wrongSource));
        Assert.AreEqual(0, downloader.CallCount);

        var oversized = CandidateFixture.Create(includeRuntimeManifest: false);
        var tooLarge = 128L * 1024L * 1024L + 1;
        oversized = oversized with
        {
            Artifact = oversized.Artifact with { Size = tooLarge },
            Certification = oversized.Certification with { PayloadSize = tooLarge },
        };
        var oversizedDownloader = new CountingDownloader(oversized.Downloads);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            CreateAcquirer(temporaryDirectory, oversized, oversizedDownloader).AcquireAsync(oversized.Artifact));
        Assert.AreEqual(0, oversizedDownloader.CallCount);

        var duplicateBytes = Encoding.UTF8.GetBytes("{\"manifestSchema\":1,\"manifestSchema\":1}");
        var duplicateHash = Sha256(duplicateBytes);
        var duplicateRuntime = pair.Artifact.RuntimeManifest! with
        {
            Size = duplicateBytes.LongLength,
            Sha256 = duplicateHash,
        };
        var duplicate = pair with
        {
            Artifact = pair.Artifact with { RuntimeManifest = duplicateRuntime },
            RuntimeManifestBytes = duplicateBytes,
            Certification = pair.Certification with
            {
                RuntimeManifest = new(
                    duplicateRuntime.FileName,
                    duplicateRuntime.Size,
                    duplicateRuntime.Sha256),
            },
            Downloads = new Dictionary<Uri, ModArtifactDownload>
            {
                [pair.Artifact.DownloadUri] = Download(DllBytes),
                [duplicateRuntime.DownloadUri] = Download(duplicateBytes),
            },
        };
        var duplicateAcquirer = CreateAcquirer(
            temporaryDirectory,
            duplicate,
            new CountingDownloader(duplicate.Downloads));

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            duplicateAcquirer.AcquireAsync(duplicate.Artifact));
    }

    private static ReviewedModArtifactCandidateAcquirer CreateAcquirer(
        TemporaryDirectory temporaryDirectory,
        CandidateFixture fixture,
        IModArtifactDownloader downloader) =>
        new(
            temporaryDirectory.CreateDirectory("state"),
            downloader,
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            fixture.Attribution,
            fixture.Certification);

    private static ModDeploymentService CreateDeployment(
        TemporaryDirectory temporaryDirectory,
        CandidateFixture fixture,
        IModArtifactDownloader downloader) =>
        new(
            temporaryDirectory.CreateDirectory("state"),
            downloader,
            new StaticVersionReader(fixture.Artifact.ExpectedVersion),
            new TrustedVerifier(),
            _ => false,
            fixture.Attribution,
            reviewedCertification: fixture.Certification);

    private static string CreateGameDirectory(TemporaryDirectory temporaryDirectory)
    {
        var path = temporaryDirectory.CreateDirectory($"game-{Guid.NewGuid():N}");
        File.WriteAllBytes(Path.Combine(path, "prime.exe"), [0x4d, 0x5a]);
        return path;
    }

    private static void AssertCannotOpenForWrite(string path)
    {
        try
        {
            using var stream = File.OpenWrite(path);
            Assert.Fail("The locked reviewed candidate could be reopened for write.");
        }
        catch (IOException)
        {
        }
    }

    private static void AssertCannotDelete(string path)
    {
        try
        {
            File.Delete(path);
            Assert.Fail("The locked reviewed candidate could be deleted or replaced by path.");
        }
        catch (IOException)
        {
        }
    }

    private sealed record CandidateFixture(
        ModReleaseArtifact Artifact,
        byte[] RuntimeManifestBytes,
        ReviewedReleaseCertification Certification,
        ModInstallationAttribution Attribution,
        IReadOnlyDictionary<Uri, ModArtifactDownload> Downloads)
    {
        private const string Repository = "Guffawaffle/stfc-mod";
        private const string Tag = "v2.1.0-guffa.8";
        private const string SourceCommit = "0123456789abcdef0123456789abcdef01234567";
        private const string Distribution = "guffawaffle.stfc-community-mod";
        private static readonly string[] IngestPayloadKinds =
            ["battle.events", "fleet.runtime", "transport.chunk"];

        public static CandidateFixture Create(bool includeRuntimeManifest)
        {
            var dllHash = Sha256(DllBytes);
            var manifestBytes = includeRuntimeManifest
                ? RuntimeManifest(dllHash)
                : [];
            var manifestHash = Sha256(manifestBytes);
            var runtime = includeRuntimeManifest
                ? new ModRuntimeManifestArtifact(
                    new Uri($"https://github.com/{Repository}/releases/download/{Tag}/"
                        + ArtifactBoundRuntimeManifestParser.ManagedFileName),
                    ArtifactBoundRuntimeManifestParser.ManagedFileName,
                    manifestBytes.LongLength,
                    manifestHash,
                    SourceCommit,
                    Repository,
                    Tag)
                : null;
            var artifact = new ModReleaseArtifact(
                new Uri($"https://github.com/{Repository}/releases/download/{Tag}/version.dll"),
                "version.dll",
                DllBytes.LongLength,
                dllHash,
                "2.1.0.8",
                runtime);
            var certification = new ReviewedReleaseCertification(
                "guffawaffle",
                "stable",
                Distribution,
                Repository,
                Tag,
                "2.1.0-guffa.8",
                SourceCommit,
                "version.dll",
                DllBytes.LongLength,
                dllHash,
                "version.dll",
                DllBytes.LongLength,
                dllHash,
                "2.1.0.8",
                DateTimeOffset.Parse("2026-08-09T00:00:00.0000000+00:00", CultureInfo.InvariantCulture),
                runtime is null ? null : new(runtime.FileName, runtime.Size, runtime.Sha256));
            var downloads = new Dictionary<Uri, ModArtifactDownload>
            {
                [artifact.DownloadUri] = Download(DllBytes),
            };
            if (runtime is not null)
            {
                downloads[runtime.DownloadUri] = Download(manifestBytes);
            }
            return new(
                artifact,
                manifestBytes,
                certification,
                new("guffawaffle", "stable", Distribution),
                downloads);
        }

        private static byte[] RuntimeManifest(string dllHash) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifestSchema = 1,
            distributionId = Distribution,
            runtimeVersion = "2.1.0.8",
            sourceRevision = SourceCommit,
            capabilities = new[]
            {
                LauncherCapabilityIds.PrincipalSettingsTaxonomyV1,
                LauncherCapabilityIds.SidecarIngestV1,
                LauncherCapabilityIds.BattleCaptureV1,
                LauncherCapabilityIds.FleetRuntimeSnapshotV1,
            },
            settingsCatalog = new { schemaVersion = 1, revision = "guffawaffle-taxonomy-2026-07-29" },
            producerContract = new
            {
                schema = "stfc.battle-bridge.producer-capabilities.v1",
                capabilityEvidencePin = new
                {
                    schema = "stfc.battle-bridge.capability-evidence-pin.v1",
                    sha256 = new string('a', 64),
                },
                runtimeCapabilities = new object[]
                {
                    new
                    {
                        id = LauncherCapabilityIds.SidecarIngestV1,
                        schema = "stfc.sidecar.ingest.v1",
                        evidenceStatus = "payload-fixture-only",
                        payloadKinds = IngestPayloadKinds,
                    },
                    new
                    {
                        id = LauncherCapabilityIds.BattleCaptureV1,
                        schema = "stfc.battle.capture.v1",
                        evidenceStatus = "payload-fixture-only",
                        envelopeKind = "battle.events",
                    },
                    new
                    {
                        id = LauncherCapabilityIds.FleetRuntimeSnapshotV1,
                        schema = "stfc.fleet.runtime_snapshot.v1",
                        evidenceStatus = "payload-fixture-only",
                        envelopeKind = "fleet.runtime",
                    },
                },
                artifact = new { fileName = "version.dll", size = DllBytes.LongLength, sha256 = dllHash },
                compatibilityEvidenceOnly = true,
                operationalActivation = "requires-bridge-transactional-binding",
            },
        });
    }

    private static ModArtifactDownload Download(byte[] bytes) =>
        new(HttpStatusCode.OK, bytes, bytes.LongLength);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class CountingDownloader(IReadOnlyDictionary<Uri, ModArtifactDownload> downloads)
        : IModArtifactDownloader
    {
        public int CallCount { get; private set; }

        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(downloads[uri]);
        }
    }

    private sealed class ThrowingDownloader : IModArtifactDownloader
    {
        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Deployment attempted a second download.");
    }

    private sealed class CancelingDownloader : IModArtifactDownloader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertFailedException("Canceled download unexpectedly resumed.");
        }
    }

    private sealed class StaticVersionReader(string version) : IModArtifactVersionReader
    {
        public string? ReadVersion(string artifactPath)
        {
            using var stream = File.OpenRead(artifactPath);
            Assert.IsTrue(stream.ReadByte() >= 0);
            return version;
        }
    }

    private sealed class TrustedVerifier : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath)
        {
            using var stream = File.OpenRead(artifactPath);
            Assert.IsTrue(stream.Length > 0);
            return new(true, "trusted test DLL");
        }
    }

    private sealed class RejectingVerifierWithForeignSibling : IModArtifactAuthenticityVerifier
    {
        public string? CandidateDirectory { get; private set; }

        public ModArtifactAuthenticityResult Verify(string artifactPath)
        {
            using (var stream = File.OpenRead(artifactPath))
            {
                Assert.IsTrue(stream.Length > 0);
            }
            CandidateDirectory = Path.GetDirectoryName(artifactPath)!;
            File.WriteAllText(Path.Combine(CandidateDirectory, "foreign.txt"), "foreign");
            return new(false, "injected verifier rejection");
        }
    }

    private sealed class DelayedDeleteMarker(int failures)
    {
        private int calls;

        public bool Mark(SafeFileHandle handle) => Interlocked.Increment(ref calls) <= failures
            ? false
            : CandidateFileNative.TryMarkDeleteOnClose(handle);
    }

    private sealed class FailOnCompanionDownloader(
        ModArtifactDownload dll,
        string candidateRoot) : IModArtifactDownloader
    {
        private int calls;

        public string? CandidateDirectory { get; private set; }

        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return Task.FromResult(dll);
            }
            CandidateDirectory = Directory.GetDirectories(candidateRoot).Single();
            File.WriteAllText(Path.Combine(CandidateDirectory, "foreign.txt"), "foreign");
            return Task.FromException<ModArtifactDownload>(
                new InvalidDataException("Injected companion failure."));
        }
    }

    private sealed class RecordingParticipant : IModDeploymentCommitParticipant
    {
        public int BeginCount { get; private set; }
        public int CommitCount { get; private set; }
        public int CompleteCount { get; private set; }

        public Task BeginAsync(ModDeploymentCommitContext context, CancellationToken cancellationToken)
        {
            BeginCount++;
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            CommitCount++;
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            CompleteCount++;
            return Task.CompletedTask;
        }

        public Task RollBackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
