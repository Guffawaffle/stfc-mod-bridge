using System.Security.Cryptography;
using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleLifecycleActivationPreparationTests
{
    private const string PipeName = "stfc-mod-bridge.battle.test.v1";
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 4, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void PreparationIsPassiveAndBindsExactPerFeatureCandidatesWithoutSecretMarkerData()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var configurationPath = Path.Combine(temporaryDirectory.Path, "community_patch_settings.toml");
        var source = Encoding.UTF8.GetBytes("# unrelated setting remains\nfuture = \"preserve\"\n");
        var preparation = BattleLifecycleActivationPreparer.Create(
            EligibleSnapshot(),
            [LauncherFeatureIds.BattleCollection],
            new(configurationPath, source),
            PipeName,
            existingLocalTargetReview: null,
            new PassThroughCredentialProtector(),
            new FixedTimeProvider(Now));
        var credentialText = preparation.CredentialCandidate.Lease.EncodeForTomlProjection();

        var candidate = Encoding.UTF8.GetString(preparation.ConfigurationCandidate.Span);
        var markerBytes = BattleLifecycleMarkerCodec.Protect(
            preparation.Marker,
            new PassThroughMarkerProtector());

        Assert.IsFalse(Directory.Exists(stateRoot));
        Assert.IsFalse(File.Exists(configurationPath));
        Assert.AreEqual(BattleLifecycleStage.Prepared, preparation.Marker.Stage);
        CollectionAssert.AreEqual(
            new[] { LauncherFeatureIds.BattleCollection },
            preparation.Marker.AffectedFeatureIds.ToArray());
        Assert.AreEqual(LauncherPlayerFeaturePreference.Enabled,
            preparation.Marker.FeatureTransitions.Single(item =>
                item.FeatureId == LauncherFeatureIds.BattleCollection).After);
        Assert.AreEqual(LauncherPlayerFeaturePreference.Unset,
            preparation.Marker.FeatureTransitions.Single(item =>
                item.FeatureId == LauncherFeatureIds.FleetCollection).After);
        StringAssert.Contains(candidate, "future = \"preserve\"");
        StringAssert.Contains(candidate, "transport = \"named_pipe\"");
        StringAssert.Contains(candidate, $"pipe_name = \"{PipeName}\"");
        StringAssert.Contains(candidate, "battlelogs_realtime = true");
        StringAssert.Contains(candidate, "fleet_runtime = false");
        StringAssert.Contains(candidate, $"token = \"{credentialText}\"");
        Assert.IsFalse(Encoding.UTF8.GetString(markerBytes).Contains(credentialText, StringComparison.Ordinal));
        Assert.AreEqual(
            preparation.ConfigurationCandidate.Length,
            preparation.Marker.Configuration!.CandidateByteCount);
        Assert.AreEqual(
            Hash(preparation.ConfigurationCandidate.Span),
            preparation.Marker.Configuration.CandidateSha256);

        preparation.Dispose();
        Assert.IsTrue(preparation.IsConfigurationZeroedForTest());
        Assert.IsTrue(preparation.CredentialCandidate.Lease.IsZeroedForTest());
    }

    [TestMethod]
    public void ExistingLocalTargetRequiresExplicitReviewBeforePreparation()
    {
        const string source = """
            [sidecar.sync]
            enabled = true
            url = "http://127.0.0.1:43127/api/sidecar/ingest"
            token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            future = "preserve"
            """;

        Assert.ThrowsException<InvalidOperationException>(() =>
            BattleLifecycleActivationPreparer.Create(
                EligibleSnapshot(),
                [LauncherFeatureIds.BattleCollection],
                new("community_patch_settings.toml", Encoding.UTF8.GetBytes(source)),
                PipeName,
                existingLocalTargetReview: null,
                new PassThroughCredentialProtector(),
                new FixedTimeProvider(Now)));

        using var reviewed = BattleLifecycleActivationPreparer.Create(
            EligibleSnapshot(),
            [LauncherFeatureIds.BattleCollection],
            new("community_patch_settings.toml", Encoding.UTF8.GetBytes(source)),
            PipeName,
            existingLocalTargetReview: new(
                Hash(Encoding.UTF8.GetBytes(source)),
                [LauncherFeatureIds.BattleCollection],
                PipeName),
            new PassThroughCredentialProtector(),
            new FixedTimeProvider(Now));
        var candidate = Encoding.UTF8.GetString(reviewed.ConfigurationCandidate.Span);
        Assert.IsFalse(candidate.Contains("url =", StringComparison.Ordinal));
        StringAssert.Contains(candidate, "future = \"preserve\"");
    }

    [TestMethod]
    public void ExistingLocalTargetReviewIsBoundToSourceFeaturesAndPipe()
    {
        const string source = "[sidecar.sync]\nenabled = true\n";
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var configuration = new ConfigurationDocumentSnapshot(
            "community_patch_settings.toml",
            sourceBytes);

        Assert.ThrowsException<InvalidOperationException>(() =>
            BattleLifecycleActivationPreparer.Create(
                EligibleSnapshot(),
                [LauncherFeatureIds.BattleCollection],
                configuration,
                PipeName,
                new(Hash(Encoding.UTF8.GetBytes(source + "# drift")),
                    [LauncherFeatureIds.BattleCollection], PipeName),
                new PassThroughCredentialProtector(),
                new FixedTimeProvider(Now)));
        Assert.ThrowsException<InvalidOperationException>(() =>
            BattleLifecycleActivationPreparer.Create(
                EligibleSnapshot(),
                [LauncherFeatureIds.BattleCollection],
                configuration,
                PipeName,
                new(Hash(sourceBytes), [LauncherFeatureIds.FleetCollection], PipeName),
                new PassThroughCredentialProtector(),
                new FixedTimeProvider(Now)));
        Assert.ThrowsException<InvalidOperationException>(() =>
            BattleLifecycleActivationPreparer.Create(
                EligibleSnapshot(),
                [LauncherFeatureIds.BattleCollection],
                configuration,
                PipeName,
                new(Hash(sourceBytes), [LauncherFeatureIds.BattleCollection], "other.battle.pipe.v1"),
                new PassThroughCredentialProtector(),
                new FixedTimeProvider(Now)));
    }

    [TestMethod]
    public void MissingCapabilityOrAlreadyEnabledPreferenceCannotPrepare()
    {
        var missing = LauncherBattleFeatureComposer.Compose(Plan(
            LauncherCapabilityIds.SidecarIngestV1));
        var enabled = LauncherBattleFeatureComposer.Compose(
            Plan(
                LauncherCapabilityIds.SidecarIngestV1,
                LauncherCapabilityIds.BattleCaptureV1),
            new(
                LauncherPlayerFeaturePreference.Enabled,
                LauncherPlayerFeaturePreference.Unset));
        var otherEnabled = LauncherBattleFeatureComposer.Compose(
            Plan(
                LauncherCapabilityIds.SidecarIngestV1,
                LauncherCapabilityIds.BattleCaptureV1,
                LauncherCapabilityIds.FleetRuntimeSnapshotV1),
            new(
                LauncherPlayerFeaturePreference.Unset,
                LauncherPlayerFeaturePreference.Enabled));
        var configuration = new ConfigurationDocumentSnapshot(
            "community_patch_settings.toml",
            Encoding.UTF8.GetBytes("# empty\n"));

        Assert.ThrowsException<InvalidOperationException>(() =>
            BattleLifecycleActivationPreparer.Create(
                missing,
                [LauncherFeatureIds.BattleCollection],
                configuration,
                PipeName,
                null,
                new PassThroughCredentialProtector(),
                new FixedTimeProvider(Now)));
        Assert.ThrowsException<InvalidOperationException>(() =>
            BattleLifecycleActivationPreparer.Create(
                enabled,
                [LauncherFeatureIds.BattleCollection],
                configuration,
                PipeName,
                null,
                new PassThroughCredentialProtector(),
                new FixedTimeProvider(Now)));
        Assert.ThrowsException<InvalidOperationException>(() =>
            BattleLifecycleActivationPreparer.Create(
                otherEnabled,
                [LauncherFeatureIds.BattleCollection],
                configuration,
                PipeName,
                null,
                new PassThroughCredentialProtector(),
                new FixedTimeProvider(Now)));
    }

    [TestMethod]
    public async Task PersistWritesMarkerThenRuntimeAndExactCandidatesWithoutMutatingAuthoritativeTargets()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var configurationPath = Path.Combine(temporaryDirectory.Path, "community_patch_settings.toml");
        var source = Encoding.UTF8.GetBytes("# baseline remains authoritative\n");
        await File.WriteAllBytesAsync(configurationPath, source);
        var preparation = BattleLifecycleActivationPreparer.Create(
            EligibleSnapshot(),
            [LauncherFeatureIds.BattleCollection, LauncherFeatureIds.FleetCollection],
            new(configurationPath, source),
            PipeName,
            null,
            new PassThroughCredentialProtector(),
            new FixedTimeProvider(Now));
        var journal = new BattleLifecycleJournalStore(stateRoot, new PassThroughMarkerProtector());
        var runtime = new BattleRuntimeLockStore(stateRoot);
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);

        await using var persisted = await BattleLifecycleActivationPreparer.PersistAsync(
            operationLease,
            journal,
            runtime,
            preparation);

        var inspection = journal.Inspect();
        Assert.AreEqual(BattleLifecycleJournalState.Readable, inspection.State);
        Assert.AreEqual(BattleLifecycleStage.Prepared, inspection.Marker!.Stage);
        Assert.IsTrue(File.Exists(journal.MarkerPath));
        Assert.IsTrue(File.Exists(Path.Combine(stateRoot, "battle", BattleRuntimeLockCodec.FileName)));
        Assert.ThrowsException<IOException>(() => File.OpenRead(
            Path.Combine(stateRoot, "battle", BattleRuntimeLockCodec.FileName)).Dispose());
        foreach (var (relativePath, expectedBytes) in preparation.CandidateBytes())
        {
            CollectionAssert.AreEqual(
                expectedBytes.ToArray(),
                await File.ReadAllBytesAsync(Resolve(stateRoot, relativePath)));
        }
        CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(configurationPath));
        Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "battle", BattleIngestCredentialCodec.FileName)));
    }

    [TestMethod]
    public async Task CandidateWriterRejectsAnyByteOrInventoryMismatchBeforeCreatingOperationResidue()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        using var preparation = BattleLifecycleActivationPreparer.Create(
            EligibleSnapshot(),
            [LauncherFeatureIds.BattleCollection],
            new("community_patch_settings.toml", Encoding.UTF8.GetBytes("# empty\n")),
            PipeName,
            null,
            new PassThroughCredentialProtector(),
            new FixedTimeProvider(Now));
        var journal = new BattleLifecycleJournalStore(stateRoot, new PassThroughMarkerProtector());
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        await journal.CreatePreparedAsync(operationLease, preparation.Marker);
        var wrong = preparation.CandidateBytes().ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        wrong[preparation.Marker.Configuration!.CandidateRelativePath] = new byte[] { 1, 2, 3 };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            journal.WritePreparedCandidatesAsync(operationLease, wrong));

        Assert.IsFalse(Directory.Exists(Path.Combine(
            stateRoot,
            "battle",
            "recovery",
            preparation.Marker.OperationId)));
        Assert.AreEqual(BattleLifecycleJournalState.RecoverableResidue, journal.Inspect().State);
    }

    [TestMethod]
    public async Task PreCommitRollbackRequiresReleasedRuntimeAndThenRestoresTheAbsentBaseline()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var configurationPath = Path.Combine(temporaryDirectory.Path, "community_patch_settings.toml");
        var source = Encoding.UTF8.GetBytes("# baseline\n");
        await File.WriteAllBytesAsync(configurationPath, source);
        var preparation = CreatePreparation(configurationPath, source);
        var journal = new BattleLifecycleJournalStore(stateRoot, new PassThroughMarkerProtector());
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        var persisted = await BattleLifecycleActivationPreparer.PersistAsync(
            operationLease,
            journal,
            new(stateRoot),
            preparation);

        var busy = await journal.RollbackPreparedAsync(operationLease, configurationPath);

        Assert.AreEqual(BattleLifecyclePreCommitRecoveryState.Unavailable, busy.State);
        Assert.IsTrue(File.Exists(journal.MarkerPath));
        await persisted.DisposeAsync();
        var recovered = await journal.RollbackPreparedAsync(operationLease, configurationPath);
        Assert.AreEqual(BattleLifecyclePreCommitRecoveryState.Recovered, recovered.State);
        Assert.AreEqual(BattleLifecycleJournalState.Absent, journal.Inspect().State);
        Assert.IsFalse(Directory.Exists(Path.Combine(stateRoot, "battle")));
        CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(configurationPath));
    }

    [TestMethod]
    public async Task TornOwnedCandidateIsRecoverableButForeignEntriesFailClosed()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var configurationPath = Path.Combine(temporaryDirectory.Path, "community_patch_settings.toml");
        var source = Encoding.UTF8.GetBytes("# baseline\n");
        await File.WriteAllBytesAsync(configurationPath, source);
        var preparation = CreatePreparation(configurationPath, source);
        var marker = preparation.Marker;
        var journal = new BattleLifecycleJournalStore(stateRoot, new PassThroughMarkerProtector());
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        var persisted = await BattleLifecycleActivationPreparer.PersistAsync(
            operationLease,
            journal,
            new(stateRoot),
            preparation);
        await persisted.DisposeAsync();
        var tornPath = Resolve(stateRoot, marker.Configuration!.CandidateRelativePath);
        await File.WriteAllBytesAsync(tornPath, [1, 2, 3]);
        var foreignPath = Path.Combine(stateRoot, "battle", "foreign.txt");
        await File.WriteAllTextAsync(foreignPath, "preserve");
        var candidateForeignPath = Path.Combine(
            stateRoot,
            "battle",
            "recovery",
            marker.OperationId,
            "candidate",
            "foreign.txt");
        await File.WriteAllTextAsync(candidateForeignPath, "candidate-preserve");

        var blocked = await journal.RollbackPreparedAsync(operationLease, configurationPath);

        Assert.AreEqual(BattleLifecyclePreCommitRecoveryState.Blocked, blocked.State);
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(foreignPath));
        Assert.AreEqual("candidate-preserve", await File.ReadAllTextAsync(candidateForeignPath));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(tornPath));
        File.Delete(foreignPath);
        File.Delete(candidateForeignPath);
        var recovered = await journal.RollbackPreparedAsync(operationLease, configurationPath);
        Assert.AreEqual(BattleLifecyclePreCommitRecoveryState.Recovered, recovered.State);
        Assert.IsFalse(File.Exists(tornPath));
        CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(configurationPath));
        Assert.AreEqual(
            BattleLifecyclePreCommitRecoveryState.NoOperation,
            (await journal.RollbackPreparedAsync(operationLease, configurationPath)).State);
    }

    [TestMethod]
    public async Task ChangedSourceConfigurationBlocksRollbackWithoutDeletingOwnedState()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var configurationPath = Path.Combine(temporaryDirectory.Path, "community_patch_settings.toml");
        var source = Encoding.UTF8.GetBytes("# baseline\n");
        await File.WriteAllBytesAsync(configurationPath, source);
        var preparation = CreatePreparation(configurationPath, source);
        var marker = preparation.Marker;
        var journal = new BattleLifecycleJournalStore(stateRoot, new PassThroughMarkerProtector());
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        var persisted = await BattleLifecycleActivationPreparer.PersistAsync(
            operationLease,
            journal,
            new(stateRoot),
            preparation);
        await persisted.DisposeAsync();
        await File.WriteAllTextAsync(configurationPath, "# external change\n");

        var blocked = await journal.RollbackPreparedAsync(operationLease, configurationPath);

        Assert.AreEqual(BattleLifecyclePreCommitRecoveryState.Blocked, blocked.State);
        Assert.IsTrue(File.Exists(journal.MarkerPath));
        foreach (var relativePath in CandidatePaths(marker))
        {
            Assert.IsTrue(File.Exists(Resolve(stateRoot, relativePath)));
        }
        await File.WriteAllBytesAsync(configurationPath, source);
        var alternatePath = Path.Combine(temporaryDirectory.Path, "alternate.toml");
        await File.WriteAllBytesAsync(alternatePath, source);
        Assert.AreEqual(
            BattleLifecyclePreCommitRecoveryState.Blocked,
            (await journal.RollbackPreparedAsync(operationLease, alternatePath)).State);
        Assert.IsTrue(File.Exists(journal.MarkerPath));
        Assert.AreEqual(
            BattleLifecyclePreCommitRecoveryState.Recovered,
            (await journal.RollbackPreparedAsync(operationLease, configurationPath)).State);
    }

    [TestMethod]
    public async Task NonDirectoryOperationResidueCannotCauseMarkerOwnershipLoss()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var configurationPath = Path.Combine(temporaryDirectory.Path, "community_patch_settings.toml");
        var source = Encoding.UTF8.GetBytes("# baseline\n");
        await File.WriteAllBytesAsync(configurationPath, source);
        using var preparation = CreatePreparation(configurationPath, source);
        var journal = new BattleLifecycleJournalStore(stateRoot, new PassThroughMarkerProtector());
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        await journal.CreatePreparedAsync(operationLease, preparation.Marker);
        var operationPath = Path.Combine(
            stateRoot,
            "battle",
            "recovery",
            preparation.Marker.OperationId);
        await File.WriteAllTextAsync(operationPath, "foreign");

        var result = await journal.RollbackPreparedAsync(operationLease, configurationPath);

        Assert.AreNotEqual(BattleLifecyclePreCommitRecoveryState.Recovered, result.State);
        Assert.IsTrue(File.Exists(journal.MarkerPath));
        Assert.AreEqual("foreign", await File.ReadAllTextAsync(operationPath));
    }

    [TestMethod]
    public async Task VerifiedManagedBackupAdvancesExactlyAndRemainsAfterPreCommitRollback()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var gameDirectory = Path.Combine(temporaryDirectory.Path, "game");
        Directory.CreateDirectory(gameDirectory);
        await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "prime.exe"), [0x4d, 0x5a]);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var source = Encoding.UTF8.GetBytes("# baseline\n");
        await File.WriteAllBytesAsync(configurationPath, source);
        var preparation = CreatePreparation(configurationPath, source);
        var journal = new BattleLifecycleJournalStore(stateRoot, new PassThroughMarkerProtector());
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        var persisted = await BattleLifecycleActivationPreparer.PersistAsync(
            operationLease,
            journal,
            new(stateRoot),
            preparation);
        var backupStore = new ProviderScopedConfigurationBackupStore(
            stateRoot,
            new PassThroughBackupProtector(),
            new NoOpStorageSecurity(),
            new FixedTimeProvider(Now));
        var coordinator = new BattleLifecycleConfigurationBackupCoordinator(
            stateRoot,
            backupStore,
            new FixedTimeProvider(Now.AddSeconds(1)));
        var installation = new ModInstallationEvidence(
            ModInstallationEvidenceState.ManagedVerified,
            IsGameRunning: false,
            InstalledVersion: "9.0.0",
            InstalledProviderId: "provider-under-test",
            InstalledReleaseChannelId: "stable",
            InstalledRuntimeDistributionId: "windows-x64",
            InstalledSha256: new string('a', 64));

        var result = await coordinator.PrepareVerifiedBackupAsync(
            operationLease,
            journal,
            installation,
            InstalledState(gameDirectory),
            new(configurationPath, source));

        Assert.AreEqual(BattleLifecycleBackupState.Succeeded, result.State);
        Assert.AreEqual(BattleLifecycleStage.BackupVerified, journal.Inspect().Marker!.Stage);
        Assert.AreEqual(result.Receipt!.BackupId, result.Marker!.Configuration!.BackupId);
        CollectionAssert.AreEqual(
            source,
            backupStore.Read(gameDirectory, installation.InstalledProviderId!, result.Receipt.BackupId));
        CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(configurationPath));
        Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "battle", BattleIngestCredentialCodec.FileName)));

        await persisted.DisposeAsync();
        Assert.AreEqual(
            BattleLifecyclePreCommitRecoveryState.Recovered,
            (await journal.RollbackPreparedAsync(operationLease, configurationPath)).State);
        CollectionAssert.AreEqual(
            source,
            backupStore.Read(gameDirectory, installation.InstalledProviderId!, result.Receipt.BackupId));
    }

    [TestMethod]
    public async Task BackupAuthorityFailureDoesNotAdvanceOrCreateBackup()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var gameDirectory = Path.Combine(temporaryDirectory.Path, "game");
        Directory.CreateDirectory(gameDirectory);
        await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "prime.exe"), [0x4d, 0x5a]);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var source = Encoding.UTF8.GetBytes("# baseline\n");
        await File.WriteAllBytesAsync(configurationPath, source);
        var preparation = CreatePreparation(configurationPath, source);
        var journal = new BattleLifecycleJournalStore(stateRoot, new PassThroughMarkerProtector());
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        await using var persisted = await BattleLifecycleActivationPreparer.PersistAsync(
            operationLease,
            journal,
            new(stateRoot),
            preparation);
        var backupStore = new ProviderScopedConfigurationBackupStore(
            stateRoot,
            new PassThroughBackupProtector(),
            new NoOpStorageSecurity(),
            new FixedTimeProvider(Now));
        var coordinator = new BattleLifecycleConfigurationBackupCoordinator(stateRoot, backupStore);
        var running = new ModInstallationEvidence(
            ModInstallationEvidenceState.ManagedVerified,
            IsGameRunning: true,
            InstalledVersion: "9.0.0",
            InstalledProviderId: "provider-under-test",
            InstalledReleaseChannelId: "stable",
            InstalledRuntimeDistributionId: "windows-x64",
            InstalledSha256: new string('a', 64));

        var result = await coordinator.PrepareVerifiedBackupAsync(
            operationLease,
            journal,
            running,
            InstalledState(gameDirectory),
            new(configurationPath, source));

        Assert.AreEqual(BattleLifecycleBackupState.Blocked, result.State);
        Assert.AreEqual(BattleLifecycleStage.Prepared, journal.Inspect().Marker!.Stage);
        Assert.AreEqual(0, backupStore.List(gameDirectory, running.InstalledProviderId!).Count);
    }

    [TestMethod]
    public async Task BackupFailureLeavesQuiescedStateAndExactRetryCanResume()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "state");
        var gameDirectory = Path.Combine(temporaryDirectory.Path, "game");
        Directory.CreateDirectory(gameDirectory);
        await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "prime.exe"), [0x4d, 0x5a]);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var source = Encoding.UTF8.GetBytes("# baseline\n");
        await File.WriteAllBytesAsync(configurationPath, source);
        var preparation = CreatePreparation(configurationPath, source);
        var journal = new BattleLifecycleJournalStore(stateRoot, new PassThroughMarkerProtector());
        await using var operationLease = await new LauncherOperationLock(stateRoot).TryAcquireAsync();
        Assert.IsNotNull(operationLease);
        await using var persisted = await BattleLifecycleActivationPreparer.PersistAsync(
            operationLease,
            journal,
            new(stateRoot),
            preparation);
        var protector = new ToggleBackupProtector { Fail = true };
        var backupStore = new ProviderScopedConfigurationBackupStore(
            stateRoot,
            protector,
            new NoOpStorageSecurity(),
            new FixedTimeProvider(Now));
        var coordinator = new BattleLifecycleConfigurationBackupCoordinator(
            stateRoot,
            backupStore,
            new FixedTimeProvider(Now.AddSeconds(1)));
        var installation = new ModInstallationEvidence(
            ModInstallationEvidenceState.ManagedVerified,
            IsGameRunning: false,
            InstalledVersion: "9.0.0",
            InstalledProviderId: "provider-under-test",
            InstalledReleaseChannelId: "stable",
            InstalledRuntimeDistributionId: "windows-x64",
            InstalledSha256: new string('a', 64));

        var failed = await coordinator.PrepareVerifiedBackupAsync(
            operationLease,
            journal,
            installation,
            InstalledState(gameDirectory),
            new(configurationPath, source));
        Assert.AreEqual(BattleLifecycleBackupState.Blocked, failed.State);
        Assert.AreEqual(BattleLifecycleStage.Quiesced, journal.Inspect().Marker!.Stage);
        protector.Fail = false;

        var resumed = await coordinator.PrepareVerifiedBackupAsync(
            operationLease,
            journal,
            installation,
            InstalledState(gameDirectory),
            new(configurationPath, source));

        Assert.AreEqual(BattleLifecycleBackupState.Succeeded, resumed.State);
        Assert.AreEqual(BattleLifecycleStage.BackupVerified, journal.Inspect().Marker!.Stage);
        Assert.AreEqual(1, backupStore.List(gameDirectory, installation.InstalledProviderId!).Count);
    }

    private static LauncherBattleFeatureSnapshot EligibleSnapshot() =>
        LauncherBattleFeatureComposer.Compose(Plan(
            LauncherCapabilityIds.SidecarIngestV1,
            LauncherCapabilityIds.BattleCaptureV1,
            LauncherCapabilityIds.FleetRuntimeSnapshotV1));

    private static LauncherActivationPlan Plan(params string[] capabilities) =>
        LauncherFeatureResolver.Resolve(
            new LauncherRuntimeProfile(
                LauncherRuntimeManifestDetector.NetnivDistributionId,
                new Version(9, 0),
                "battle-lifecycle-preparation",
                new(1, "battle-lifecycle-preparation"),
                capabilities,
                [new("test", "battle lifecycle preparation")]),
            LauncherFeatureCatalog.All);

    private static BattleLifecycleActivationPreparation CreatePreparation(
        string configurationPath,
        byte[] source) =>
        BattleLifecycleActivationPreparer.Create(
            EligibleSnapshot(),
            [LauncherFeatureIds.BattleCollection],
            new(configurationPath, source),
            PipeName,
            null,
            new PassThroughCredentialProtector(),
            new FixedTimeProvider(Now));

    private static ModInstalledArtifactState InstalledState(string gameDirectory) => new(
        SchemaVersion: 1,
        GameDirectory: gameDirectory,
        FileName: "version.dll",
        Version: "9.0.0",
        Size: 1,
        Sha256: new string('a', 64),
        InstalledAtUtc: Now,
        PreviousArtifactBackupPath: null,
        ProviderId: "provider-under-test",
        ReleaseChannelId: "stable",
        RuntimeDistributionId: "windows-x64");

    private static IEnumerable<string> CandidatePaths(BattleLifecycleMarker marker) =>
        marker.Resources
            .Where(resource => resource.CandidateRelativePath is not null)
            .Select(resource => resource.CandidateRelativePath!)
            .Append(marker.Configuration!.CandidateRelativePath);

    private static string Resolve(string stateRoot, string relativePath) =>
        Path.Combine(stateRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    private sealed class ToggleBackupProtector : IConfigurationBackupProtector
    {
        public bool Fail { get; set; }

        public string SchemeId => "test-toggle";

        public byte[] Protect(byte[] contents) => Fail
            ? throw new CryptographicException("injected")
            : contents.ToArray();

        public byte[] Unprotect(byte[] protectedContents) => protectedContents.ToArray();
    }

    private sealed class NoOpStorageSecurity : IConfigurationBackupStorageSecurity
    {
        public void SecureDirectory(string directory) => Directory.CreateDirectory(directory);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
