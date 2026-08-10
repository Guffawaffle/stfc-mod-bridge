using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleLifecycleRuntimeProvisioningTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private const string EvidenceSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [TestMethod]
    public async Task MarkerLastReceiptOpensOnceAndOwnsCleanShutdown()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        var factory = await fixture.CreateFactoryAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            fixture.RuntimeLease.MarkCleanAsync(Started.AddMinutes(2)));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await fixture.RuntimeLease.DisposeAsync());

        var provisioning = await factory.OpenAsync(fixture.CommittedFeatures, CancellationToken.None);

        Assert.AreEqual(fixture.Credential.Metadata.PipeName, provisioning.PipeName);
        Assert.AreEqual(EvidenceSha256, provisioning.RuntimeEvidenceSha256);
        Assert.AreSame(fixture.BattleSink, provisioning.BattleSink);
        Assert.IsNull(provisioning.FleetSink);
        CollectionAssert.AreEqual(
            fixture.Credential.Credential.ToArray(),
            provisioning.Credential.ToArray());
        using (var client = new NamedPipeClientStream(
                   ".",
                   provisioning.PipeName,
                   PipeDirection.InOut,
                   PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await Assert.ThrowsExceptionAsync<TimeoutException>(() => client.ConnectAsync(75));
        }
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await factory.OpenAsync(fixture.CommittedFeatures, CancellationToken.None));

        await factory.DisposeAsync();
        Assert.AreEqual(0, fixture.BattleSink.DisposeCount);
        Assert.AreEqual(BattleRuntimeLockState.Running, fixture.RuntimeLease.Record.State);

        await provisioning.DisposeAsync();

        Assert.AreEqual(1, fixture.BattleSink.DisposeCount);
        Assert.IsFalse(fixture.Credential.IsZeroedForTest());
        var persisted = BattleRuntimeLockCodec.Decode(File.ReadAllBytes(fixture.RuntimePath));
        Assert.AreEqual(BattleRuntimeLockState.Clean, persisted.State);
        Assert.AreEqual(Started.AddMinutes(5), persisted.LastCleanCloseAtUtc);
    }

    [TestMethod]
    public async Task WrongSnapshotDoesNotConsumeTheSingleOwnerHandoff()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        var factory = await fixture.CreateFactoryAsync();
        var wrong = LauncherBattleFeatureComposer.Compose(
            fixture.RuntimeActivation.ActivationPlan,
            new(
                LauncherPlayerFeaturePreference.Disabled,
                LauncherPlayerFeaturePreference.Disabled));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await factory.OpenAsync(wrong, CancellationToken.None));

        var provisioning = await factory.OpenAsync(fixture.CommittedFeatures, CancellationToken.None);
        await provisioning.DisposeAsync();
        Assert.AreEqual(1, fixture.BattleSink.DisposeCount);
    }

    [TestMethod]
    public async Task ConcurrentClaimHasExactlyOneOwner()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        var factory = await fixture.CreateFactoryAsync();

        var attempts = await Task.WhenAll(
            Claim(factory, fixture.CommittedFeatures),
            Claim(factory, fixture.CommittedFeatures));

        Assert.AreEqual(1, attempts.Count(result => result.Lease is not null));
        Assert.AreEqual(1, attempts.Count(result => result.Failure is InvalidOperationException));
        await attempts.Single(result => result.Lease is not null).Lease!.DisposeAsync();
        Assert.AreEqual(1, fixture.BattleSink.DisposeCount);
    }

    [TestMethod]
    public async Task ExactRuntimeReceiptCannotBackTwoProvisioningFactories()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        var first = await fixture.CreateFactoryAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await fixture.CreateFactoryAsync());

        var provisioning = await first.OpenAsync(fixture.CommittedFeatures, CancellationToken.None);
        await provisioning.DisposeAsync();
        Assert.AreEqual(1, fixture.BattleSink.DisposeCount);
    }

    [TestMethod]
    public async Task FailedSecondFactoryRetainsItsExactSinkCleanupForRetry()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        var first = await fixture.CreateFactoryAsync();
        var losingSink = new RecordingSink(failuresRemaining: 1);
        await using var losingOwner = new BattleRuntimeSinkOwner(losingSink, null);

        await Assert.ThrowsExceptionAsync<IOException>(async () =>
            await fixture.CreateFactoryAsync(sinkOwner: losingOwner));
        Assert.AreEqual(1, losingSink.DisposeCount);
        await losingOwner.DisposeAsync();
        Assert.AreEqual(2, losingSink.DisposeCount);

        var provisioning = await first.OpenAsync(fixture.CommittedFeatures, CancellationToken.None);
        await provisioning.DisposeAsync();
        Assert.AreEqual(1, fixture.BattleSink.DisposeCount);
    }

    [TestMethod]
    public async Task CleanupFailureRetainsUnclaimedOwnershipForExplicitRetry()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync(sinkCleanupFailures: 1);
        var factory = await fixture.CreateFactoryAsync();

        await Assert.ThrowsExceptionAsync<IOException>(async () => await factory.DisposeAsync());

        Assert.AreEqual(1, fixture.BattleSink.DisposeCount);
        Assert.AreEqual(BattleRuntimeLockState.Running, fixture.RuntimeLease.Record.State);
        Assert.IsFalse(fixture.Credential.IsZeroedForTest());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await factory.OpenAsync(fixture.CommittedFeatures, CancellationToken.None));

        await factory.DisposeAsync();

        Assert.AreEqual(2, fixture.BattleSink.DisposeCount);
        Assert.IsFalse(fixture.Credential.IsZeroedForTest());
        Assert.AreEqual(
            BattleRuntimeLockState.Clean,
            BattleRuntimeLockCodec.Decode(File.ReadAllBytes(fixture.RuntimePath)).State);
    }

    [TestMethod]
    public async Task ExtraFamilyAuthorityAndChangedCredentialFailBeforeOwnershipTransfer()
    {
        await using var extra = await ProvisioningFixture.CreateAsync();
        await using var extraOwner = new BattleRuntimeSinkOwner(
            new RecordingSink(),
            new RecordingSink());
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await extra.CreateFactoryAsync(sinkOwner: extraOwner));
        Assert.AreEqual(BattleRuntimeLockState.Running, extra.RuntimeLease.Record.State);
        Assert.IsFalse(extra.Credential.IsZeroedForTest());

        await using var changed = await ProvisioningFixture.CreateAsync();
        using var replacement = BattleIngestCredentialCodec.CreateCandidate(
            changed.Credential.Metadata.PipeName,
            0,
            Started,
            Started,
            BattleCredentialRotationReason.Initial,
            new PassThroughCredentialProtector());
        File.WriteAllBytes(changed.CredentialPath, replacement.ProtectedBytes.ToArray());
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await changed.CreateFactoryAsync());
        Assert.AreEqual(0, changed.BattleSink.DisposeCount);
        Assert.IsFalse(changed.Credential.IsZeroedForTest());

        await using var evidence = await ProvisioningFixture.CreateAsync();
        var exactProcess = evidence.RuntimeClient.Receipt!;
        var wrongEvidence = new BattleRuntimeClientReceiptResult(
            BattleRuntimeClientReceiptState.Ready,
            new(
                exactProcess.ProcessId,
                exactProcess.ProcessStartUtc,
                exactProcess.ExecutablePath,
                new string('b', 64)),
            "battle-runtime-client-ready");
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await evidence.CreateFactoryAsync(runtimeClient: wrongEvidence));
        Assert.AreEqual(BattleRuntimeLockState.Running, evidence.RuntimeLease.Record.State);
    }

    private static async Task<ClaimResult> Claim(
        BattleLifecycleRuntimeProvisioningFactory factory,
        LauncherBattleFeatureSnapshot features)
    {
        try
        {
            return new(await factory.OpenAsync(features, CancellationToken.None), null);
        }
        catch (Exception exception)
        {
            return new(null, exception);
        }
    }

    private sealed record ClaimResult(
        BattleRuntimeProvisioningLease? Lease,
        Exception? Failure);

    private sealed class ProvisioningFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory temporaryDirectory;
        private readonly LauncherOperationLease operationLease;
        private readonly BattleCredentialCandidate credentialCandidate;
        private bool runtimeDisposed;

        private ProvisioningFixture(
            TemporaryDirectory temporaryDirectory,
            LauncherOperationLease operationLease,
            BattleCredentialCandidate credentialCandidate,
            BattleLifecycleRuntimeHandoffReceipt cleanupReceipt,
            BattleIngestCredentialStore credentialStore,
            ReviewedRuntimeActivation runtimeActivation,
            LauncherBattleFeatureSnapshot committedFeatures,
            BattleRuntimeClientReceiptResult runtimeClient,
            RecordingSink battleSink)
        {
            this.temporaryDirectory = temporaryDirectory;
            this.operationLease = operationLease;
            this.credentialCandidate = credentialCandidate;
            CleanupReceipt = cleanupReceipt;
            CredentialStore = credentialStore;
            RuntimeActivation = runtimeActivation;
            CommittedFeatures = committedFeatures;
            RuntimeClient = runtimeClient;
            BattleSink = battleSink;
            SinkOwner = new(battleSink, null);
        }

        public BattleLifecycleRuntimeHandoffReceipt CleanupReceipt { get; }

        public BattleRuntimeLockLease RuntimeLease => CleanupReceipt.RuntimeLease;

        public BattleCredentialLease Credential => credentialCandidate.Lease;

        public BattleIngestCredentialStore CredentialStore { get; }

        public ReviewedRuntimeActivation RuntimeActivation { get; }

        public LauncherBattleFeatureSnapshot CommittedFeatures { get; }

        public BattleRuntimeClientReceiptResult RuntimeClient { get; }

        public RecordingSink BattleSink { get; }

        public BattleRuntimeSinkOwner SinkOwner { get; }

        public string RuntimePath => CleanupReceipt.RuntimePath;

        public string CredentialPath => CredentialStore.Path;

        public static async Task<ProvisioningFixture> CreateAsync(int sinkCleanupFailures = 0)
        {
            var temporaryDirectory = new TemporaryDirectory();
            try
            {
                var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
                var markerProtector = new PassThroughMarkerProtector();
                var credentialProtector = new PassThroughCredentialProtector();
                var journal = new BattleLifecycleJournalStore(stateRoot, markerProtector);
                var runtimeStore = new BattleRuntimeLockStore(stateRoot);
                var credentialStore = new BattleIngestCredentialStore(stateRoot, credentialProtector);
                var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync()
                    ?? throw new AssertFailedException("The fixture could not acquire the operation lease.");
                var runtimeRecord = new BattleRuntimeLockRecord(
                    new string('1', 32),
                    BattleRuntimeLockState.Running,
                    Environment.ProcessId,
                    new string('7', 32),
                    Started,
                    null);
                var credentialCandidate = BattleIngestCredentialCodec.CreateCandidate(
                    $"stfc-battle-runtime-{Guid.NewGuid():N}",
                    0,
                    Started,
                    Started,
                    BattleCredentialRotationReason.Initial,
                    credentialProtector);
                var prepared = Marker(runtimeRecord, credentialCandidate.Lease.Metadata, BattleLifecycleStage.Prepared);
                await journal.CreatePreparedAsync(operationLease, prepared);
                var runtimeLease = await runtimeStore.CreateBoundRunningAsync(
                    operationLease,
                    journal,
                    runtimeRecord);
                Directory.CreateDirectory(Path.GetDirectoryName(credentialStore.Path)!);
                File.WriteAllBytes(credentialStore.Path, credentialCandidate.ProtectedBytes.ToArray());
                var cleanup = Marker(
                    runtimeRecord,
                    credentialCandidate.Lease.Metadata,
                    BattleLifecycleStage.CleanupPending);
                File.WriteAllBytes(
                    journal.MarkerPath,
                    BattleLifecycleMarkerCodec.Protect(cleanup, markerProtector));
                var cleanupReceipt = await journal.DeleteCommittedArtifactsRetainingRuntimeAsync(
                    operationLease,
                    cleanup,
                    runtimeLease);
                var activation = BuildRuntimeActivation();
                var committedFeatures = LauncherBattleFeatureComposer.Compose(
                    activation.ActivationPlan,
                    new(
                        LauncherPlayerFeaturePreference.Enabled,
                        LauncherPlayerFeaturePreference.Disabled));
                var runtimeClient = CurrentProcessReceipt();
                return new(
                    temporaryDirectory,
                    operationLease,
                    credentialCandidate,
                    cleanupReceipt,
                    credentialStore,
                    activation,
                    committedFeatures,
                    runtimeClient,
                    new RecordingSink(sinkCleanupFailures));
            }
            catch
            {
                temporaryDirectory.Dispose();
                throw;
            }
        }

        public async ValueTask<BattleLifecycleRuntimeProvisioningFactory> CreateFactoryAsync(
            BattleRuntimeSinkOwner? sinkOwner = null,
            BattleRuntimeClientReceiptResult? runtimeClient = null)
        {
            return await BattleLifecycleRuntimeProvisioningFactory.CreateAsync(
                CleanupReceipt,
                RuntimeActivation,
                CommittedFeatures,
                CredentialStore,
                runtimeClient ?? RuntimeClient,
                sinkOwner ?? SinkOwner,
                new FixedTimeProvider(Started.AddMinutes(5)));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!runtimeDisposed)
                {
                    try
                    {
                        await RuntimeLease.DisposeAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    runtimeDisposed = true;
                }
                try
                {
                    await SinkOwner.DisposeAsync();
                }
                catch (InvalidOperationException)
                {
                }
                credentialCandidate.Dispose();
                await operationLease.DisposeAsync();
            }
            finally
            {
                temporaryDirectory.Dispose();
            }
        }

        private static BattleLifecycleMarker Marker(
            BattleRuntimeLockRecord runtime,
            BattleCredentialMetadata credential,
            BattleLifecycleStage stage)
        {
            var runtimeBytes = BattleRuntimeLockCodec.Encode(runtime);
            var runtimeIdentity = Identity(runtimeBytes);
            var candidatePrefix = $"battle/recovery/{new string('a', 32)}/candidate";
            var backupId = stage >= BattleLifecycleStage.BackupVerified ? "fixture-backup" : null;
            return new(
                new string('a', 32),
                BattleLifecycleOperationKind.FeatureActivation,
                runtime.OwnerId,
                stage,
                [LauncherFeatureIds.BattleCollection],
                [
                    new(
                        "ingest-credential",
                        $"battle/{BattleIngestCredentialCodec.FileName}",
                        null,
                        $"{candidatePrefix}/{BattleIngestCredentialCodec.FileName}.next",
                        new(credential.ProtectedByteCount, credential.ProtectedSha256)),
                    new("runtime-lock", "battle/runtime.lock", null, null, runtimeIdentity),
                ],
                new(
                    credential.Generation,
                    credential.ProtectedByteCount,
                    credential.ProtectedSha256),
                new(
                    new string('2', 64),
                    new string('3', 64),
                    2,
                    new string('2', 64),
                    $"{candidatePrefix}/community_patch_settings.toml.next",
                    2,
                    new string('4', 64),
                    new string('5', 64),
                    backupId,
                    backupId is null ? null : new string('6', 64)),
                [
                    new(
                        LauncherFeatureIds.BattleCollection,
                        LauncherPlayerFeaturePreference.Unset,
                        LauncherPlayerFeaturePreference.Enabled),
                    new(
                        LauncherFeatureIds.FleetCollection,
                        LauncherPlayerFeaturePreference.Disabled,
                        LauncherPlayerFeaturePreference.Disabled),
                ],
                false,
                true,
                Started,
                stage == BattleLifecycleStage.Prepared ? Started : Started.AddMinutes(1),
                BattleLifecycleActivationPreparer.ImplementationVersion,
                true,
                true);
        }

        private static BattleLifecycleFileIdentity Identity(ReadOnlySpan<byte> bytes) =>
            new(
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

        private static ReviewedRuntimeActivation BuildRuntimeActivation()
        {
            var profile = new LauncherRuntimeProfile(
                "test.runtime",
                new Version(1, 0),
                "test-source",
                null,
                [
                    LauncherCapabilityIds.SidecarIngestV1,
                    LauncherCapabilityIds.BattleCaptureV1,
                    LauncherCapabilityIds.FleetRuntimeSnapshotV1,
                ],
                [new("test", "Lifecycle provisioning fixture")]);
            return new(
                EvidenceSha256,
                profile,
                LauncherFeatureResolver.Resolve(profile, LauncherFeatureCatalog.All));
        }

        private static BattleRuntimeClientReceiptResult CurrentProcessReceipt()
        {
            using var process = Process.GetCurrentProcess();
            var path = process.MainModule?.FileName ?? Environment.ProcessPath
                ?? throw new AssertFailedException("The test process path is unavailable.");
            return new(
                BattleRuntimeClientReceiptState.Ready,
                new(
                    unchecked((uint)process.Id),
                    new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                    path,
                    EvidenceSha256),
                "battle-runtime-client-ready");
        }
    }

    private sealed class RecordingSink(int failuresRemaining = 0) : IBattleIngestSink, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask<BattleIngestCommitResult> CommitAsync(
            BattleIngestEnvelope envelope,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BattleIngestCommitResult(envelope.ExactEventBytes.Count));

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (failuresRemaining-- > 0)
            {
                return ValueTask.FromException(new IOException("fixture cleanup failure"));
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PassThroughMarkerProtector : IBattleLifecycleMarkerProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.ToArray();
    }

    private sealed class PassThroughCredentialProtector : IBattleCredentialProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.ToArray();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"stfc-battle-runtime-provisioning-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
