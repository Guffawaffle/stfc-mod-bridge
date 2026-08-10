using System.Security.Cryptography;
using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleLifecycleCommitTests
{
    private const string PipeName = "stfc-mod-bridge.battle.commit-test.v1";
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 5, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task CredentialPromotionAppliesAndVerifiesClosedAcl()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporaryDirectory = new TemporaryDirectory();
        var bytes = RandomNumberGenerator.GetBytes(64);
        var identity = new BattleLifecycleFileIdentity(
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var store = new BattleIngestCredentialStore(
            temporaryDirectory.Path,
            new PassThroughCredentialProtector());

        await using var promotion = await store.CreateNewAsync(bytes, identity);
        Assert.IsTrue(promotion.Matches(identity));
        Assert.ThrowsException<IOException>(() =>
        {
            using var _ = File.OpenRead(store.Path);
        });
        await promotion.CommitAsync();
        Assert.IsTrue(store.MatchesProtectedIdentity(identity));
    }

    [TestMethod]
    public async Task ExactCommitPromotesAllAuthoritiesAndPreservesUnrelatedPreferences()
    {
        await using var fixture = await Fixture.CreateAsync();
        var candidate = fixture.Prepared.ConfigurationCandidate.ToArray();

        var result = await fixture.CreateCoordinator().CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);

        Assert.AreEqual(BattleLifecycleCommitState.Succeeded, result.State, result.Code);
        Assert.AreEqual(BattleLifecycleStage.CommitVerified, fixture.Journal.Inspect().Marker!.Stage);
        CollectionAssert.AreEqual(candidate, await File.ReadAllBytesAsync(fixture.Configuration.Path));
        var credential = fixture.CredentialStore.Load();
        Assert.AreEqual(BattleCredentialLoadState.Readable, credential.State);
        Assert.AreEqual(PipeName, credential.Lease!.Metadata.PipeName);
        credential.Lease.Dispose();
        var preferences = fixture.Preferences.Load();
        Assert.AreEqual(true, preferences.SettingsSearchVisible);
        Assert.AreEqual(LauncherColorMode.Dark, preferences.ColorMode);
        Assert.AreEqual(LauncherLaunchTarget.PrimeExecutable, preferences.LaunchTarget);
        Assert.AreEqual(true, preferences.ProviderSwitchReviewAcknowledged);
        Assert.AreEqual(
            new LauncherBattlePreferences(
                LauncherPlayerFeaturePreference.Enabled,
                LauncherPlayerFeaturePreference.Unset),
            preferences.EffectiveBattlePreferences);
        Assert.AreEqual(1, fixture.BackupStore.List(fixture.GameDirectory, "provider-under-test").Count);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task OwnedFailureCompensatesExactlyAndRetryRollsForward(int failurePointValue)
    {
        await using var fixture = await Fixture.CreateAsync();
        var failurePoint = (BattleLifecycleCommitCheckpoint)failurePointValue;
        var beforePreferences = File.ReadAllBytes(Path.Combine(fixture.StateRoot, "ui-preferences.json"));
        var failing = fixture.CreateCoordinator(checkpoint =>
            checkpoint == failurePoint
                ? ValueTask.FromException(new IOException("injected commit failure"))
                : ValueTask.CompletedTask);

        var failed = await failing.CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);

        Assert.AreEqual(BattleLifecycleCommitState.Compensated, failed.State);
        Assert.AreEqual(BattleLifecycleStage.CommitStarted, fixture.Journal.Inspect().Marker!.Stage);
        Assert.AreEqual(BattleCredentialLoadState.Absent, fixture.CredentialStore.Load().State);
        CollectionAssert.AreEqual(
            fixture.Configuration.Contents,
            await File.ReadAllBytesAsync(fixture.Configuration.Path));
        CollectionAssert.AreEqual(
            beforePreferences,
            await File.ReadAllBytesAsync(Path.Combine(fixture.StateRoot, "ui-preferences.json")));

        var retried = await fixture.CreateCoordinator().CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);

        Assert.AreEqual(BattleLifecycleCommitState.Succeeded, retried.State, retried.Code);
        Assert.AreEqual(BattleLifecycleStage.CommitVerified, fixture.Journal.Inspect().Marker!.Stage);
    }

    [TestMethod]
    public async Task ForeignConfigurationFailsBeforeCommitStartedWithoutMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var foreign = Encoding.UTF8.GetBytes("# external change\nfuture = true\n");
        await File.WriteAllBytesAsync(fixture.Configuration.Path, foreign);
        var preferencesBefore = File.ReadAllBytes(Path.Combine(fixture.StateRoot, "ui-preferences.json"));

        var result = await fixture.CreateCoordinator().CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);

        Assert.AreEqual(BattleLifecycleCommitState.Blocked, result.State);
        Assert.AreEqual(BattleLifecycleStage.BackupVerified, fixture.Journal.Inspect().Marker!.Stage);
        Assert.AreEqual(BattleCredentialLoadState.Absent, fixture.CredentialStore.Load().State);
        CollectionAssert.AreEqual(foreign, await File.ReadAllBytesAsync(fixture.Configuration.Path));
        CollectionAssert.AreEqual(
            preferencesBefore,
            await File.ReadAllBytesAsync(Path.Combine(fixture.StateRoot, "ui-preferences.json")));
    }

    [TestMethod]
    public async Task InvalidPreferenceDocumentFailsClosedBeforeCommitStarted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preferencesPath = Path.Combine(fixture.StateRoot, "ui-preferences.json");
        var invalid = Encoding.UTF8.GetBytes("{\"schemaVersion\":5,\"schemaVersion\":5}");
        await File.WriteAllBytesAsync(preferencesPath, invalid);

        var result = await fixture.CreateCoordinator().CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);

        Assert.AreEqual(BattleLifecycleCommitState.Blocked, result.State);
        Assert.AreEqual(BattleLifecycleStage.BackupVerified, fixture.Journal.Inspect().Marker!.Stage);
        CollectionAssert.AreEqual(invalid, await File.ReadAllBytesAsync(preferencesPath));
        Assert.AreEqual(BattleCredentialLoadState.Absent, fixture.CredentialStore.Load().State);
        CollectionAssert.AreEqual(
            fixture.Configuration.Contents,
            await File.ReadAllBytesAsync(fixture.Configuration.Path));
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task CommitStartedExactMixedStatesRollForwardWithoutASecondAuthority(int appliedCount)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdvanceCommitStartedAsync();
        await fixture.ApplyCredentialAsync();
        if (appliedCount >= 2)
        {
            var write = await new AtomicTomlStore(retainAdjacentBackup: false).SaveDocumentAsync(
                fixture.Configuration.Path,
                fixture.Configuration.Contents,
                fixture.Prepared.ConfigurationCandidate.ToArray());
            Assert.AreEqual(AtomicTomlWriteState.Succeeded, write.State);
        }
        if (appliedCount >= 3)
        {
            Assert.IsTrue(fixture.Preferences.TrySaveBattlePreferences(
                LauncherBattlePreferences.Default,
                new(
                    LauncherPlayerFeaturePreference.Enabled,
                    LauncherPlayerFeaturePreference.Unset)));
        }

        var result = await fixture.CreateCoordinator().CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);

        Assert.AreEqual(BattleLifecycleCommitState.Succeeded, result.State, result.Code);
        Assert.AreEqual(BattleLifecycleStage.CommitVerified, fixture.Journal.Inspect().Marker!.Stage);
        CollectionAssert.AreEqual(
            fixture.Prepared.ConfigurationCandidate.ToArray(),
            await File.ReadAllBytesAsync(fixture.Configuration.Path));
        Assert.AreEqual(
            LauncherPlayerFeaturePreference.Enabled,
            fixture.Preferences.Load().EffectiveBattlePreferences.BattleCollection);
        Assert.AreEqual(BattleCredentialLoadState.Readable, fixture.CredentialStore.Load().State);
    }

    [TestMethod]
    public async Task CompensationPreservesForeignConfigurationAndReleasesExactCredentialHandle()
    {
        await using var fixture = await Fixture.CreateAsync();
        var foreign = Encoding.UTF8.GetBytes("# concurrent external write\nfuture = false\n");
        async ValueTask MutateAtConfiguration(BattleLifecycleCommitCheckpoint checkpoint)
        {
            if (checkpoint != BattleLifecycleCommitCheckpoint.ConfigurationPromoted) return;
            await File.WriteAllBytesAsync(fixture.Configuration.Path, foreign);
            throw new IOException("injected post-write collision");
        }

        var result = await fixture.CreateCoordinator(MutateAtConfiguration).CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);

        Assert.AreEqual(BattleLifecycleCommitState.Unavailable, result.State);
        Assert.AreEqual(BattleLifecycleStage.CommitStarted, fixture.Journal.Inspect().Marker!.Stage);
        CollectionAssert.AreEqual(foreign, await File.ReadAllBytesAsync(fixture.Configuration.Path));
        var credential = fixture.CredentialStore.Load();
        Assert.AreEqual(BattleCredentialLoadState.Readable, credential.State);
        credential.Lease!.Dispose();
        Assert.AreEqual(
            LauncherBattlePreferences.Default,
            fixture.Preferences.Load().EffectiveBattlePreferences);
    }

    [TestMethod]
    public async Task ChangedProtectedBackupBlocksBeforeCommitStarted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var payload = Directory.EnumerateFiles(
                Path.Combine(fixture.StateRoot, "configuration-backups"),
                "configuration.protected",
                SearchOption.AllDirectories)
            .Single();
        await File.WriteAllBytesAsync(payload, [1, 2, 3, 4]);

        var result = await fixture.CreateCoordinator().CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);

        Assert.AreEqual(BattleLifecycleCommitState.Blocked, result.State);
        Assert.AreEqual(BattleLifecycleStage.BackupVerified, fixture.Journal.Inspect().Marker!.Stage);
        Assert.AreEqual(BattleCredentialLoadState.Absent, fixture.CredentialStore.Load().State);
        CollectionAssert.AreEqual(
            fixture.Configuration.Contents,
            await File.ReadAllBytesAsync(fixture.Configuration.Path));
    }

    [TestMethod]
    public async Task RunningGameEvidenceBlocksBeforeCommitStarted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var running = Installation() with { IsGameRunning = true };

        var result = await fixture.CreateCoordinator().CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            running,
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);

        Assert.AreEqual(BattleLifecycleCommitState.Blocked, result.State);
        Assert.AreEqual(BattleLifecycleStage.BackupVerified, fixture.Journal.Inspect().Marker!.Stage);
        Assert.AreEqual(BattleCredentialLoadState.Absent, fixture.CredentialStore.Load().State);
    }

    [TestMethod]
    public async Task CommitVerifiedMarkerFailurePreservesAllAfterAndExactSuccessorRecovery()
    {
        var fail = true;
        await using var fixture = await Fixture.CreateAsync(marker =>
        {
            if (fail && marker.Stage == BattleLifecycleStage.CommitVerified)
            {
                throw new IOException("injected marker replace failure");
            }
            return ValueTask.CompletedTask;
        });

        var result = await fixture.CreateCoordinator().CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);

        Assert.AreEqual(BattleLifecycleCommitState.Unavailable, result.State);
        var interrupted = fixture.Journal.Inspect();
        Assert.AreEqual(BattleLifecycleJournalState.RecoverableSuccessor, interrupted.State);
        Assert.AreEqual(BattleLifecycleStage.CommitStarted, interrupted.Marker!.Stage);
        Assert.AreEqual(BattleLifecycleStage.CommitVerified, interrupted.Successor!.Stage);
        Assert.AreEqual(BattleCredentialLoadState.Readable, fixture.CredentialStore.Load().State);
        CollectionAssert.AreEqual(
            fixture.Prepared.ConfigurationCandidate.ToArray(),
            await File.ReadAllBytesAsync(fixture.Configuration.Path));
        Assert.AreEqual(
            LauncherPlayerFeaturePreference.Enabled,
            fixture.Preferences.Load().EffectiveBattlePreferences.BattleCollection);

        fail = false;
        var recovered = await fixture.Journal.RecoverAsync(fixture.OperationLease);
        Assert.AreEqual(BattleLifecycleStage.CommitVerified, recovered.Marker!.Stage);
    }

    [TestMethod]
    public async Task CredentialAclFailureRemovesTheExactNewFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var bytes = RandomNumberGenerator.GetBytes(64);
        var identity = new BattleLifecycleFileIdentity(
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var store = new BattleIngestCredentialStore(
            temporaryDirectory.Path,
            new PassThroughCredentialProtector(),
            new ThrowingCredentialStorageSecurity());

        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(async () =>
            await store.CreateNewAsync(bytes, identity));

        Assert.IsFalse(File.Exists(store.Path));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory temporaryDirectory;

        private Fixture(
            TemporaryDirectory temporaryDirectory,
            string stateRoot,
            string gameDirectory,
            ConfigurationDocumentSnapshot configuration,
            LauncherOperationLease operationLease,
            BattleLifecycleJournalStore journal,
            BattleLifecyclePreparedActivation prepared,
            ProviderScopedConfigurationBackupStore backupStore,
            JsonLauncherUiPreferencesStore preferences,
            BattleIngestCredentialStore credentialStore)
        {
            this.temporaryDirectory = temporaryDirectory;
            StateRoot = stateRoot;
            GameDirectory = gameDirectory;
            Configuration = configuration;
            OperationLease = operationLease;
            Journal = journal;
            Prepared = prepared;
            BackupStore = backupStore;
            Preferences = preferences;
            CredentialStore = credentialStore;
        }

        public string StateRoot { get; }

        public string GameDirectory { get; }

        public ConfigurationDocumentSnapshot Configuration { get; }

        public LauncherOperationLease OperationLease { get; }

        public BattleLifecycleJournalStore Journal { get; }

        public BattleLifecyclePreparedActivation Prepared { get; }

        public ProviderScopedConfigurationBackupStore BackupStore { get; }

        public JsonLauncherUiPreferencesStore Preferences { get; }

        public BattleIngestCredentialStore CredentialStore { get; }

        public static async Task<Fixture> CreateAsync(
            Func<BattleLifecycleMarker, ValueTask>? beforeMarkerReplace = null)
        {
            var temporaryDirectory = new TemporaryDirectory();
            try
            {
                var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
                var gameDirectory = Path.Combine(temporaryDirectory.Path, "game");
                Directory.CreateDirectory(gameDirectory);
                await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "prime.exe"), [0x4d, 0x5a]);
                var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
                var source = Encoding.UTF8.GetBytes("# baseline\nfuture = \"preserve\"\n");
                await File.WriteAllBytesAsync(configurationPath, source);
                var configuration = new ConfigurationDocumentSnapshot(configurationPath, source);
                var preparation = BattleLifecycleActivationPreparer.Create(
                    EligibleSnapshot(),
                    [LauncherFeatureIds.BattleCollection],
                    configuration,
                    PipeName,
                    existingLocalTargetReview: null,
                    new PassThroughCredentialProtector(),
                    new FixedTimeProvider(Now));
                var journal = new BattleLifecycleJournalStore(
                    stateRoot,
                    new PassThroughMarkerProtector(),
                    beforeReplace: beforeMarkerReplace);
                var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync()
                    ?? throw new InvalidOperationException("The test operation lease was unavailable.");
                var prepared = await BattleLifecycleActivationPreparer.PersistAsync(
                    operationLease,
                    journal,
                    new(stateRoot),
                    preparation);
                var backupStore = new ProviderScopedConfigurationBackupStore(
                    stateRoot,
                    new PassThroughBackupProtector(),
                    new NoOpStorageSecurity(),
                    new FixedTimeProvider(Now));
                var backup = await new BattleLifecycleConfigurationBackupCoordinator(
                        stateRoot,
                        backupStore,
                        new FixedTimeProvider(Now.AddSeconds(1)))
                    .PrepareVerifiedBackupAsync(
                        operationLease,
                        journal,
                        Installation(),
                        InstalledState(gameDirectory),
                        configuration);
                if (backup.State != BattleLifecycleBackupState.Succeeded)
                {
                    throw new InvalidOperationException("The test backup was not prepared.");
                }
                var preferences = new JsonLauncherUiPreferencesStore(stateRoot);
                preferences.Save(new(
                    true,
                    LauncherColorMode.Dark,
                    LauncherLaunchTarget.PrimeExecutable,
                    true,
                    LauncherBattlePreferences.Default));
                return new(
                    temporaryDirectory,
                    stateRoot,
                    gameDirectory,
                    configuration,
                    operationLease,
                    journal,
                    prepared,
                    backupStore,
                    preferences,
                    new(stateRoot, new PassThroughCredentialProtector()));
            }
            catch
            {
                temporaryDirectory.Dispose();
                throw;
            }
        }

        public BattleLifecycleCommitCoordinator CreateCoordinator(
            Func<BattleLifecycleCommitCheckpoint, ValueTask>? checkpoint = null) =>
            new(
                StateRoot,
                CredentialStore,
                BackupStore,
                Preferences,
                new AtomicTomlStore(retainAdjacentBackup: false),
                new FixedTimeProvider(Now.AddSeconds(2)),
                checkpoint);

        public async Task AdvanceCommitStartedAsync()
        {
            var marker = Journal.Inspect().Marker
                ?? throw new InvalidOperationException("The test marker is absent.");
            await Journal.AdvanceAsync(
                OperationLease,
                marker with
                {
                    Stage = BattleLifecycleStage.CommitStarted,
                    UpdatedAtUtc = Now.AddSeconds(2),
                });
        }

        public async Task ApplyCredentialAsync()
        {
            var marker = Journal.Inspect().Marker
                ?? throw new InvalidOperationException("The test marker is absent.");
            var credential = marker.Credential
                ?? throw new InvalidOperationException("The test credential binding is absent.");
            await using var promotion = await CredentialStore.CreateNewAsync(
                Prepared.ProtectedCredentialCandidate,
                new(credential.ProtectedByteCount, credential.ProtectedSha256));
            await promotion.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Prepared.DisposeAsync();
            await OperationLease.DisposeAsync();
            temporaryDirectory.Dispose();
        }
    }

    private static LauncherBattleFeatureSnapshot EligibleSnapshot() =>
        LauncherBattleFeatureComposer.Compose(LauncherFeatureResolver.Resolve(
            new LauncherRuntimeProfile(
                LauncherRuntimeManifestDetector.NetnivDistributionId,
                new Version(9, 0),
                "battle-lifecycle-commit",
                new(1, "battle-lifecycle-commit"),
                [
                    LauncherCapabilityIds.SidecarIngestV1,
                    LauncherCapabilityIds.BattleCaptureV1,
                    LauncherCapabilityIds.FleetRuntimeSnapshotV1,
                ],
                [new("test", "battle lifecycle commit")]),
            LauncherFeatureCatalog.All));

    private static ModInstallationEvidence Installation() => new(
        ModInstallationEvidenceState.ManagedVerified,
        IsGameRunning: false,
        InstalledVersion: "9.0.0",
        InstalledProviderId: "provider-under-test",
        InstalledReleaseChannelId: "stable",
        InstalledRuntimeDistributionId: "windows-x64",
        InstalledSha256: new string('a', 64));

    private static ModInstalledArtifactState InstalledState(string gameDirectory) => new(
        1,
        gameDirectory,
        "version.dll",
        "9.0.0",
        1,
        new string('a', 64),
        Now,
        null,
        "provider-under-test",
        "stable",
        "windows-x64");

    private sealed class PassThroughCredentialProtector : IBattleCredentialProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.ToArray();
    }

    private sealed class PassThroughMarkerProtector : IBattleLifecycleMarkerProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.ToArray();
    }

    private sealed class PassThroughBackupProtector : IConfigurationBackupProtector
    {
        public string SchemeId => "test-pass-through";

        public byte[] Protect(byte[] contents) => contents.ToArray();

        public byte[] Unprotect(byte[] protectedContents) => protectedContents.ToArray();
    }

    private sealed class NoOpStorageSecurity : IConfigurationBackupStorageSecurity
    {
        public void SecureDirectory(string directory) => Directory.CreateDirectory(directory);
    }

    private sealed class ThrowingCredentialStorageSecurity : IBattleCredentialStorageSecurity
    {
        public void SecureFile(FileStream stream) =>
            throw new UnauthorizedAccessException("injected ACL failure");
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
