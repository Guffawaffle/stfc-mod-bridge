using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleLifecycleCommitTests
{
    private const string PipeName = "stfc-mod-bridge.battle.commit-test.v1";
    private const string CrashStageEnvironment = "STFC_BATTLE_LIFECYCLE_CRASH_STAGE";
    private const string CrashRootEnvironment = "STFC_BATTLE_LIFECYCLE_CRASH_ROOT";
    private const string CrashReadyEnvironment = "STFC_BATTLE_LIFECYCLE_CRASH_READY";
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
        var credential = fixture.CredentialStore.Load();
        Assert.AreEqual(BattleCredentialLoadState.Readable, credential.State);
        credential.Lease!.Dispose();
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
        var credential = fixture.CredentialStore.Load();
        Assert.AreEqual(BattleCredentialLoadState.Readable, credential.State);
        credential.Lease!.Dispose();
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

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task CommitStartedCrashRecoversEveryExactBeforeAfterCombination(int appliedCount)
    {
        await using var fixture = await Fixture.CreateAsync();
        var candidate = fixture.Prepared.ConfigurationCandidate.ToArray();
        await fixture.AdvanceCommitStartedAsync();
        if (appliedCount >= 1) await fixture.ApplyCredentialAsync();
        if (appliedCount >= 2)
        {
            var write = await new AtomicTomlStore(retainAdjacentBackup: false).SaveDocumentAsync(
                fixture.Configuration.Path,
                fixture.Configuration.Contents,
                candidate);
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
        await fixture.Prepared.DisposeAsync();

        var result = await fixture.CreateRecoveryCoordinator().RecoverAsync(
            fixture.OperationLease,
            fixture.Journal,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration.Path);

        Assert.AreEqual(BattleLifecycleTerminalRecoveryState.Recovered, result.State, result.Code);
        Assert.IsTrue(result.RequiresSessionRecomposition);
        Assert.AreEqual(BattleLifecycleJournalState.Absent, fixture.Journal.Inspect().State);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.StateRoot, "battle", BattleRuntimeLockCodec.FileName)));
        CollectionAssert.AreEqual(candidate, await File.ReadAllBytesAsync(fixture.Configuration.Path));
        var credential = fixture.CredentialStore.Load();
        Assert.AreEqual(BattleCredentialLoadState.Readable, credential.State);
        credential.Lease!.Dispose();
        Assert.AreEqual(
            LauncherPlayerFeaturePreference.Enabled,
            fixture.Preferences.Load().EffectiveBattlePreferences.BattleCollection);
    }

    [TestMethod]
    public async Task ChangedRecoveryCandidateBlocksWithoutAuthoritativeMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdvanceCommitStartedAsync();
        await fixture.Prepared.DisposeAsync();
        var marker = fixture.Journal.Inspect().Marker!;
        var candidatePath = Path.Combine(
            fixture.StateRoot,
            marker.Configuration!.CandidateRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var changed = Encoding.UTF8.GetBytes("# changed recovery candidate\n");
        await File.WriteAllBytesAsync(candidatePath, changed);

        var result = await fixture.CreateRecoveryCoordinator().RecoverAsync(
            fixture.OperationLease,
            fixture.Journal,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration.Path);

        Assert.AreEqual(BattleLifecycleTerminalRecoveryState.Blocked, result.State);
        CollectionAssert.AreEqual(changed, await File.ReadAllBytesAsync(candidatePath));
        CollectionAssert.AreEqual(
            fixture.Configuration.Contents,
            await File.ReadAllBytesAsync(fixture.Configuration.Path));
        Assert.AreEqual(BattleCredentialLoadState.Absent, fixture.CredentialStore.Load().State);
        Assert.AreEqual(LauncherBattlePreferences.Default, fixture.Preferences.Load().EffectiveBattlePreferences);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingOrChangedRuntimeLockBlocksCommitRecoveryWithoutGuessing(bool changed)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdvanceCommitStartedAsync();
        await fixture.Prepared.DisposeAsync();
        var runtimePath = Path.Combine(fixture.StateRoot, "battle", BattleRuntimeLockCodec.FileName);
        if (changed)
        {
            await File.WriteAllBytesAsync(runtimePath, Encoding.UTF8.GetBytes("foreign runtime lock"));
        }
        else
        {
            File.Delete(runtimePath);
        }

        var result = await fixture.CreateRecoveryCoordinator().RecoverAsync(
            fixture.OperationLease,
            fixture.Journal,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration.Path);

        Assert.AreEqual(BattleLifecycleTerminalRecoveryState.Blocked, result.State);
        Assert.AreEqual(BattleLifecycleStage.CommitStarted, fixture.Journal.Inspect().Marker!.Stage);
        Assert.AreEqual(changed, File.Exists(runtimePath));
        if (changed)
        {
            CollectionAssert.AreEqual(
                Encoding.UTF8.GetBytes("foreign runtime lock"),
                await File.ReadAllBytesAsync(runtimePath));
        }
        Assert.AreEqual(BattleCredentialLoadState.Absent, fixture.CredentialStore.Load().State);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task CleanupInterruptionRetainsMarkerAndExactRetryFinishes(int failurePointValue)
    {
        var failurePoint = (BattleLifecycleCleanupCheckpoint)failurePointValue;
        await using var fixture = await Fixture.CreateAsync();
        var committed = await fixture.CreateCoordinator().CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);
        Assert.AreEqual(BattleLifecycleCommitState.Succeeded, committed.State);
        await fixture.Prepared.DisposeAsync();
        var failed = await fixture.CreateRecoveryCoordinator(checkpoint =>
                checkpoint == failurePoint
                    ? ValueTask.FromException(new IOException("injected cleanup interruption"))
                    : ValueTask.CompletedTask)
            .RecoverAsync(
                fixture.OperationLease,
                fixture.Journal,
                Installation(),
                InstalledState(fixture.GameDirectory),
                fixture.Configuration.Path);

        Assert.AreEqual(BattleLifecycleTerminalRecoveryState.Unavailable, failed.State);
        Assert.AreEqual(BattleLifecycleStage.CleanupPending, fixture.Journal.Inspect().Marker!.Stage);

        var retried = await fixture.CreateRecoveryCoordinator().RecoverAsync(
            fixture.OperationLease,
            fixture.Journal,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration.Path);
        Assert.AreEqual(BattleLifecycleTerminalRecoveryState.Recovered, retried.State, retried.Code);
        Assert.AreEqual(BattleLifecycleJournalState.Absent, fixture.Journal.Inspect().State);
    }

    [TestMethod]
    public async Task ChangedCommittedPreferenceBlocksMarkerLastCleanup()
    {
        await using var fixture = await Fixture.CreateAsync();
        var committed = await fixture.CreateCoordinator().CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);
        Assert.AreEqual(BattleLifecycleCommitState.Succeeded, committed.State);
        await fixture.Prepared.DisposeAsync();
        Assert.IsTrue(fixture.Preferences.TrySaveBattlePreferences(
            new(
                LauncherPlayerFeaturePreference.Enabled,
                LauncherPlayerFeaturePreference.Unset),
            LauncherBattlePreferences.Default));

        var result = await fixture.CreateRecoveryCoordinator().RecoverAsync(
            fixture.OperationLease,
            fixture.Journal,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration.Path);

        Assert.AreEqual(BattleLifecycleTerminalRecoveryState.Blocked, result.State);
        Assert.AreEqual(BattleLifecycleStage.CommitVerified, fixture.Journal.Inspect().Marker!.Stage);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.StateRoot, "battle", BattleRuntimeLockCodec.FileName)));
    }

    [TestMethod]
    public async Task UnknownCleanupEntryIsPreservedBeforeAnyOwnedDeletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var committed = await fixture.CreateCoordinator().CommitAsync(
            fixture.OperationLease,
            fixture.Journal,
            fixture.Prepared,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration);
        Assert.AreEqual(BattleLifecycleCommitState.Succeeded, committed.State);
        await fixture.Prepared.DisposeAsync();
        var marker = fixture.Journal.Inspect().Marker!;
        var candidateDirectory = Path.Combine(
            fixture.StateRoot,
            "battle",
            "recovery",
            marker.OperationId,
            "candidate");
        var unknownPath = Path.Combine(candidateDirectory, "foreign.bin");
        var unknown = Encoding.UTF8.GetBytes("foreign cleanup evidence");
        await File.WriteAllBytesAsync(unknownPath, unknown);
        var credentialCandidate = marker.Resources.Single(item => item.Role == "ingest-credential");
        var credentialCandidatePath = Path.Combine(
            fixture.StateRoot,
            credentialCandidate.CandidateRelativePath!.Replace('/', Path.DirectorySeparatorChar));

        var result = await fixture.CreateRecoveryCoordinator().RecoverAsync(
            fixture.OperationLease,
            fixture.Journal,
            Installation(),
            InstalledState(fixture.GameDirectory),
            fixture.Configuration.Path);

        Assert.AreEqual(BattleLifecycleTerminalRecoveryState.Blocked, result.State);
        CollectionAssert.AreEqual(unknown, await File.ReadAllBytesAsync(unknownPath));
        Assert.IsTrue(File.Exists(credentialCandidatePath));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.StateRoot, "battle", BattleRuntimeLockCodec.FileName)));
    }

    [DataTestMethod]
    [DataRow("commit-started-marker")]
    [DataRow("credential-promoted")]
    [DataRow("configuration-promoted")]
    [DataRow("preferences-promoted")]
    [DataRow("commit-verified-marker")]
    [DataRow("cleanup-candidates")]
    [DataRow("cleanup-runtime")]
    [DataRow("cleanup-marker")]
    public async Task HardCrashAtEveryTerminalStageRecoversFromFreshOwnership(string crashStage)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporaryDirectory = new TemporaryDirectory();
        var readyPath = Path.Combine(temporaryDirectory.Path, "ready");
        using var child = StartCrashProbe(crashStage, temporaryDirectory.Path, readyPath);
        try
        {
            await WaitForCrashProbeAsync(child, readyPath);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

            var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
            var gameDirectory = Path.Combine(temporaryDirectory.Path, "game");
            var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
            await using var operationLease = await AcquireOperationLeaseAfterCrashAsync(stateRoot);
            var journal = new BattleLifecycleJournalStore(
                stateRoot,
                new PassThroughMarkerProtector());
            var backupStore = new ProviderScopedConfigurationBackupStore(
                stateRoot,
                new PassThroughBackupProtector(),
                new NoOpStorageSecurity(),
                new FixedTimeProvider(Now));
            var commit = new BattleLifecycleCommitCoordinator(
                stateRoot,
                new(stateRoot, new PassThroughCredentialProtector()),
                backupStore,
                new JsonLauncherUiPreferencesStore(stateRoot),
                new AtomicTomlStore(retainAdjacentBackup: false),
                new FixedTimeProvider(Now.AddSeconds(4)));
            var result = await new BattleLifecycleTerminalRecoveryCoordinator(
                    stateRoot,
                    commit,
                    new FixedTimeProvider(Now.AddSeconds(5)))
                .RecoverAsync(
                    operationLease,
                    journal,
                    Installation(),
                    InstalledState(gameDirectory),
                    configurationPath);

            Assert.AreEqual(BattleLifecycleTerminalRecoveryState.Recovered, result.State, result.Code);
            Assert.AreEqual(BattleLifecycleJournalState.Absent, journal.Inspect().State);
            Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "battle", BattleRuntimeLockCodec.FileName)));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    [TestMethod]
    public async Task BattleLifecycleHardCrashProbe()
    {
        var crashStage = Environment.GetEnvironmentVariable(CrashStageEnvironment);
        if (string.IsNullOrWhiteSpace(crashStage)) return;
        var root = Environment.GetEnvironmentVariable(CrashRootEnvironment)
            ?? throw new InvalidOperationException("The crash-probe root is absent.");
        var ready = Environment.GetEnvironmentVariable(CrashReadyEnvironment)
            ?? throw new InvalidOperationException("The crash-probe ready path is absent.");

        async ValueTask BlockAsync(string current)
        {
            if (current != crashStage) return;
            await File.WriteAllTextAsync(ready, current);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        await using var fixture = await Fixture.CreateAsync(
            marker => BlockAsync(marker.Stage switch
            {
                BattleLifecycleStage.CommitStarted => "commit-started-marker",
                BattleLifecycleStage.CommitVerified => "commit-verified-marker",
                _ => string.Empty,
            }),
            root);
        if (crashStage.StartsWith("cleanup-", StringComparison.Ordinal))
        {
            var committed = await fixture.CreateCoordinator().CommitAsync(
                fixture.OperationLease,
                fixture.Journal,
                fixture.Prepared,
                Installation(),
                InstalledState(fixture.GameDirectory),
                fixture.Configuration);
            Assert.AreEqual(BattleLifecycleCommitState.Succeeded, committed.State);
            await fixture.Prepared.DisposeAsync();
            _ = await fixture.CreateRecoveryCoordinator(checkpoint => BlockAsync(checkpoint switch
            {
                BattleLifecycleCleanupCheckpoint.CandidatesDeleted => "cleanup-candidates",
                BattleLifecycleCleanupCheckpoint.RuntimeLockDeleted => "cleanup-runtime",
                BattleLifecycleCleanupCheckpoint.MarkerDeleting => "cleanup-marker",
                _ => string.Empty,
            }))
                .RecoverAsync(
                    fixture.OperationLease,
                    fixture.Journal,
                    Installation(),
                    InstalledState(fixture.GameDirectory),
                    fixture.Configuration.Path);
        }
        else
        {
            _ = await fixture.CreateCoordinator(checkpoint => BlockAsync(checkpoint switch
            {
                BattleLifecycleCommitCheckpoint.CredentialPromoted => "credential-promoted",
                BattleLifecycleCommitCheckpoint.ConfigurationPromoted => "configuration-promoted",
                BattleLifecycleCommitCheckpoint.PreferencesPromoted => "preferences-promoted",
                _ => string.Empty,
            }))
                .CommitAsync(
                    fixture.OperationLease,
                    fixture.Journal,
                    fixture.Prepared,
                    Installation(),
                    InstalledState(fixture.GameDirectory),
                    fixture.Configuration);
        }
        Assert.Fail("The crash probe passed its requested blocking checkpoint.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory? temporaryDirectory;

        private Fixture(
            TemporaryDirectory? temporaryDirectory,
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
            Func<BattleLifecycleMarker, ValueTask>? beforeMarkerReplace = null,
            string? existingRoot = null)
        {
            var temporaryDirectory = existingRoot is null ? new TemporaryDirectory() : null;
            try
            {
                var root = existingRoot ?? temporaryDirectory!.Path;
                Directory.CreateDirectory(root);
                var stateRoot = Path.Combine(root, "state");
                var gameDirectory = Path.Combine(root, "game");
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
                temporaryDirectory?.Dispose();
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

        public BattleLifecycleTerminalRecoveryCoordinator CreateRecoveryCoordinator(
            Func<BattleLifecycleCleanupCheckpoint, ValueTask>? checkpoint = null) =>
            new(
                StateRoot,
                CreateCoordinator(),
                new FixedTimeProvider(Now.AddSeconds(3)),
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
            temporaryDirectory?.Dispose();
        }
    }

    private static Process StartCrashProbe(string crashStage, string root, string readyPath)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(BattleLifecycleCommitTests).Assembly.Location);
        start.ArgumentList.Add(
            "--Tests:STFCCommunityMod.Launcher.Core.Tests.BattleLifecycleCommitTests.BattleLifecycleHardCrashProbe");
        start.Environment[CrashStageEnvironment] = crashStage;
        start.Environment[CrashRootEnvironment] = root;
        start.Environment[CrashReadyEnvironment] = readyPath;
        return Process.Start(start) ?? throw new AssertFailedException("The Battle crash probe did not start.");
    }

    private static async Task WaitForCrashProbeAsync(Process child, string readyPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(readyPath)) return;
            if (child.HasExited)
            {
                var output = await child.StandardOutput.ReadToEndAsync();
                var error = await child.StandardError.ReadToEndAsync();
                throw new AssertFailedException(
                    $"The Battle crash probe exited before its checkpoint ({child.ExitCode}). {output} {error}");
            }
            await Task.Delay(25);
        }
        throw new AssertFailedException("The Battle crash probe did not reach its checkpoint.");
    }

    private static async Task<LauncherOperationLease> AcquireOperationLeaseAfterCrashAsync(string stateRoot)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var lease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
            if (lease is not null) return lease;
            await Task.Delay(50);
        }
        throw new AssertFailedException("The crash probe did not release the root operation lease.");
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
