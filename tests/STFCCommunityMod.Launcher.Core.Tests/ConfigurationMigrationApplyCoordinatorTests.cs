using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ConfigurationMigrationApplyCoordinatorTests
{
    [TestMethod]
    public async Task SuccessfulApplyReturnsVerifiedReceiptAndPostCommitDiagnosis()
    {
        using var directory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(
            gameDirectory,
            "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes(
            "[shortcuts]\nset_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        await File.WriteAllBytesAsync(configurationPath, original);
        var baseline = new ConfigurationDocumentSnapshot(configurationPath, original);
        var (plan, evidence) = PlanAliasMigration(baseline);
        var backupStore = new ProviderScopedConfigurationBackupStore(
            directory.CreateDirectory("state"),
            new ReversingProtector(),
            new NoOpStorageSecurity());
        var repository = new TomlConfigurationRepository(
            mutationBackup: new ProviderScopedConfigurationMutationBackup(
                backupStore,
                "guffawaffle",
                "guffawaffle/stable",
                "configuration-migration"));
        var coordinator = new ConfigurationMigrationApplyCoordinator(repository);

        var result = await coordinator.ApplyAsync(new(baseline, plan, evidence));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State);
        Assert.IsNotNull(result.CommittedSnapshot);
        Assert.IsNotNull(result.BackupReceipt);
        Assert.IsNotNull(result.ResultingDiagnosis);
        Assert.AreEqual("configuration-migration", result.BackupReceipt.Reason);
        Assert.AreEqual(
            ConfigurationDocumentRevision.FromContents(original).Sha256,
            result.BackupReceipt.ContentSha256);
        Assert.AreEqual(
            result.CommittedSnapshot.Revision,
            result.ResultingDiagnosis.Binding.Revision);
        Assert.AreEqual(
            result.BackupReceipt,
            backupStore.List(gameDirectory, "guffawaffle").Single());
        CollectionAssert.AreEqual(
            original,
            backupStore.Read(
                gameDirectory,
                "guffawaffle",
                result.BackupReceipt.BackupId));
        var committed = await File.ReadAllTextAsync(configurationPath);
        StringAssert.Contains(committed, "hotkeys_disable");
        StringAssert.Contains(committed, "[input.bindings]");
        Assert.IsFalse(committed.Contains("set_hotkeys_disable =", StringComparison.Ordinal));
        Assert.IsFalse(
            result.ResultingDiagnosis.Findings.Any(
                finding => finding.RemediationId is not null
                    && plan.Binding!.RemediationIds.Contains(
                        finding.RemediationId,
                        StringComparer.Ordinal)));
        Assert.IsFalse(File.Exists(configurationPath + ".bak"));
    }

    [TestMethod]
    public async Task ImmediateRecheckPreservesExternalChangeWithoutCreatingBackup()
    {
        using var directory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(
            gameDirectory,
            "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes(
            "[shortcuts]\nset_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        var external = Encoding.UTF8.GetBytes(
            "[shortcuts]\nset_hotkeys_disable = \"CTRL-ALT-MINUS\"\n# external edit\n");
        await File.WriteAllBytesAsync(configurationPath, original);
        var baseline = new ConfigurationDocumentSnapshot(configurationPath, original);
        var (plan, evidence) = PlanAliasMigration(baseline);
        await File.WriteAllBytesAsync(configurationPath, external);
        var backupStore = new ProviderScopedConfigurationBackupStore(
            directory.CreateDirectory("state"),
            new ReversingProtector(),
            new NoOpStorageSecurity());
        var repository = new TomlConfigurationRepository(
            mutationBackup: new ProviderScopedConfigurationMutationBackup(
                backupStore,
                "guffawaffle",
                reason: "configuration-migration"));
        var coordinator = new ConfigurationMigrationApplyCoordinator(repository);

        var result = await coordinator.ApplyAsync(new(baseline, plan, evidence));

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        Assert.IsNull(result.BackupReceipt);
        Assert.IsNull(result.CommittedSnapshot);
        Assert.IsNull(result.ResultingDiagnosis);
        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(configurationPath));
        Assert.AreEqual(0, backupStore.List(gameDirectory, "guffawaffle").Count);
    }

    [TestMethod]
    public async Task ActiveProviderChangeRejectsReviewedCleanupWithoutBackupOrWrite()
    {
        using var directory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(
            gameDirectory,
            "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes(
            "[shortcuts]\nset_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        await File.WriteAllBytesAsync(configurationPath, original);
        var baseline = new ConfigurationDocumentSnapshot(configurationPath, original);
        var (plan, reviewedEvidence) = PlanAliasMigration(baseline);
        var currentEvidence = LauncherConfigurationDiagnosisEvidence.Unavailable(
            "netniv",
            "stable",
            LauncherProviderCapabilityStatus.Unsupported);
        var backupStore = new ProviderScopedConfigurationBackupStore(
            directory.CreateDirectory("state"),
            new ReversingProtector(),
            new NoOpStorageSecurity());
        var repository = new TomlConfigurationRepository(
            mutationBackup: new ProviderScopedConfigurationMutationBackup(
                backupStore,
                "guffawaffle",
                reason: "configuration-migration"));

        var result = await new ConfigurationMigrationApplyCoordinator(repository).ApplyAsync(
            new(
                baseline,
                plan,
                reviewedEvidence,
                currentEvidence,
                configurationPath));

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        StringAssert.Contains(result.Error, "provider");
        Assert.IsNull(result.BackupReceipt);
        Assert.IsNull(result.CommittedSnapshot);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(configurationPath));
        Assert.AreEqual(0, backupStore.List(gameDirectory, "guffawaffle").Count);
    }

    [TestMethod]
    public async Task OperationLeaseSpansAuthorityCaptureBackupAndCommit()
    {
        using var directory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(
            gameDirectory,
            "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes(
            "[shortcuts]\nset_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        await File.WriteAllBytesAsync(configurationPath, original);
        var baseline = new ConfigurationDocumentSnapshot(configurationPath, original);
        var (plan, evidence) = PlanAliasMigration(baseline);
        var stateDirectory = directory.CreateDirectory("state");
        var backupStore = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity());
        var backupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBackup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new TomlConfigurationRepository(
            mutationBackup: new BlockingMutationBackup(
                new ProviderScopedConfigurationMutationBackup(
                    backupStore,
                    "guffawaffle",
                    reason: "configuration-migration"),
                backupStarted,
                releaseBackup));
        var operationLock = new LauncherOperationLock(stateDirectory);
        var applyTask = new ConfigurationMigrationApplyCoordinator(repository)
            .ApplyUnderOperationLockAsync(
                operationLock,
                new(baseline, plan, evidence),
                () => new(configurationPath, evidence));

        await backupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var competingLease = await new LauncherOperationLock(stateDirectory)
            .TryAcquireAsync();
        Assert.IsNull(
            competingLease,
            "The global operation lease must remain held through the backup/write boundary.");

        releaseBackup.TrySetResult();
        var result = await applyTask;

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNotNull(result.BackupReceipt);
    }

    [TestMethod]
    public async Task BusyOperationLockCreatesNoBackupAndDoesNotCaptureStaleAuthority()
    {
        using var directory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(
            gameDirectory,
            "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes(
            "[shortcuts]\nset_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        await File.WriteAllBytesAsync(configurationPath, original);
        var baseline = new ConfigurationDocumentSnapshot(configurationPath, original);
        var (plan, evidence) = PlanAliasMigration(baseline);
        var stateDirectory = directory.CreateDirectory("state");
        var backupStore = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity());
        var repository = new TomlConfigurationRepository(
            mutationBackup: new ProviderScopedConfigurationMutationBackup(
                backupStore,
                "guffawaffle",
                reason: "configuration-migration"));
        var operationLock = new LauncherOperationLock(stateDirectory);
        await using var heldLease = await operationLock.TryAcquireAsync();
        Assert.IsNotNull(heldLease);
        var authorityCaptured = false;

        var result = await new ConfigurationMigrationApplyCoordinator(repository)
            .ApplyUnderOperationLockAsync(
                operationLock,
                new(baseline, plan, evidence),
                () =>
                {
                    authorityCaptured = true;
                    return new(configurationPath, evidence);
                });

        Assert.AreEqual(AtomicTomlWriteState.Busy, result.State);
        Assert.IsFalse(authorityCaptured);
        Assert.AreEqual(0, backupStore.List(gameDirectory, "guffawaffle").Count);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(configurationPath));
        Assert.IsFalse(Directory.EnumerateFiles(gameDirectory).Any(
            path => Path.GetFileName(path).Contains(".tmp", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task PostBackupConflictReturnsReceiptAndPreservesExternalChange()
    {
        using var directory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(
            gameDirectory,
            "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes(
            "[shortcuts]\nset_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        var external = Encoding.UTF8.GetBytes(
            "[shortcuts]\nset_hotkeys_disable = \"CTRL-ALT-MINUS\"\n# late external edit\n");
        await File.WriteAllBytesAsync(configurationPath, original);
        var baseline = new ConfigurationDocumentSnapshot(configurationPath, original);
        var (plan, evidence) = PlanAliasMigration(baseline);
        var backupStore = new ProviderScopedConfigurationBackupStore(
            directory.CreateDirectory("state"),
            new ReversingProtector(),
            new NoOpStorageSecurity());
        var providerBackup = new ProviderScopedConfigurationMutationBackup(
            backupStore,
            "guffawaffle",
            reason: "configuration-migration");
        var repository = new TomlConfigurationRepository(
            mutationBackup: new ReplacingAfterBackupMutationBackup(
                providerBackup,
                external));

        var result = await new ConfigurationMigrationApplyCoordinator(repository)
            .ApplyAsync(new(baseline, plan, evidence));

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        Assert.IsNotNull(result.BackupReceipt);
        Assert.IsNull(result.CommittedSnapshot);
        Assert.IsNull(result.ResultingDiagnosis);
        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(configurationPath));
        CollectionAssert.AreEqual(
            original,
            backupStore.Read(
                gameDirectory,
                "guffawaffle",
                result.BackupReceipt.BackupId));
    }

    [TestMethod]
    public async Task NoChangePlanReturnsCurrentDiagnosisWithoutBackup()
    {
        using var directory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(
            gameDirectory,
            "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes("# no overrides\n");
        await File.WriteAllBytesAsync(configurationPath, original);
        var baseline = new ConfigurationDocumentSnapshot(configurationPath, original);
        var catalog = LoadCatalog();
        var evidence = LauncherConfigurationDiagnosisEvidence.Supported(
            "guffawaffle",
            "stable",
            catalog);
        var diagnosis = new ConfigurationHealthAnalyzer().Analyze(baseline, evidence);
        var plan = new ConfigurationMigrationPlanner().Plan(
            baseline,
            evidence,
            diagnosis,
            []);
        var backupStore = new ProviderScopedConfigurationBackupStore(
            directory.CreateDirectory("state"),
            new ReversingProtector(),
            new NoOpStorageSecurity());
        var repository = new TomlConfigurationRepository(
            mutationBackup: new ProviderScopedConfigurationMutationBackup(
                backupStore,
                "guffawaffle",
                reason: "configuration-migration"));

        var result = await new ConfigurationMigrationApplyCoordinator(repository)
            .ApplyAsync(new(baseline, plan, evidence));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(AtomicTomlWriteState.NoChange, result.State);
        Assert.IsNull(result.BackupReceipt);
        Assert.AreEqual(baseline.Revision, result.ResultingDiagnosis!.Binding.Revision);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(configurationPath));
        Assert.AreEqual(0, backupStore.List(gameDirectory, "guffawaffle").Count);
    }

    [TestMethod]
    public async Task MutationIsRejectedWhenRepositoryCannotReturnVerifiedReceipt()
    {
        using var directory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(
            gameDirectory,
            "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes(
            "[shortcuts]\nset_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        await File.WriteAllBytesAsync(configurationPath, original);
        var baseline = new ConfigurationDocumentSnapshot(configurationPath, original);
        var (plan, evidence) = PlanAliasMigration(baseline);

        var result = await new ConfigurationMigrationApplyCoordinator(
            new TomlConfigurationRepository()).ApplyAsync(new(baseline, plan, evidence));

        Assert.AreEqual(AtomicTomlWriteState.Invalid, result.State);
        StringAssert.Contains(result.Error, "verified backup receipt");
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(configurationPath));
        Assert.IsFalse(File.Exists(configurationPath + ".bak"));
    }

    private static (
        ConfigurationMigrationPlanResult Plan,
        LauncherConfigurationDiagnosisEvidence Evidence) PlanAliasMigration(
            ConfigurationDocumentSnapshot snapshot)
    {
        var catalog = LoadCatalog();
        var evidence = LauncherConfigurationDiagnosisEvidence.Supported(
            "guffawaffle",
            "stable",
            catalog);
        var diagnosis = new ConfigurationHealthAnalyzer().Analyze(snapshot, evidence);
        var remediationId = diagnosis.Findings.Single(
            finding => finding.Code == "CONFIG_ALIAS_PRESENT").RemediationId;
        Assert.IsFalse(string.IsNullOrWhiteSpace(remediationId));
        var plan = new ConfigurationMigrationPlanner().Plan(
            snapshot,
            evidence,
            diagnosis,
            [remediationId!]);
        Assert.AreEqual(ConfigurationMigrationPlanState.Ready, plan.State, plan.Message);
        return (plan, evidence);
    }

    private static LauncherConfigurationCatalog LoadCatalog()
    {
        var schemaPath = FindRepositoryFile(
            "docs",
            "windows-launcher",
            "config-schema.guffawaffle.v1.json");
        return LauncherConfigurationSchemaLoader.LoadFile(schemaPath);
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail($"Could not find repository file '{Path.Combine(relativeParts)}'.");
        return string.Empty;
    }

    private static string CreateGameDirectory(TemporaryDirectory directory)
    {
        var gameDirectory = directory.CreateDirectory("game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        return gameDirectory;
    }

    private sealed class ReversingProtector : IConfigurationBackupProtector
    {
        public string SchemeId => "test-reverse-v1";

        public byte[] Protect(byte[] contents) => [.. contents.Reverse()];

        public byte[] Unprotect(byte[] protectedContents) => [.. protectedContents.Reverse()];
    }

    private sealed class NoOpStorageSecurity : IConfigurationBackupStorageSecurity
    {
        public void SecureDirectory(string directory) => Directory.CreateDirectory(directory);
    }

    private sealed class ReplacingAfterBackupMutationBackup(
        IConfigurationMutationBackup inner,
        byte[] replacement) : IConfigurationMutationBackup
    {
        public async ValueTask<ConfigurationBackupReceipt> BeforeReplaceAsync(
            string configurationPath,
            byte[] expectedContents,
            CancellationToken cancellationToken)
        {
            var receipt = await inner.BeforeReplaceAsync(
                configurationPath,
                expectedContents,
                cancellationToken);
            await File.WriteAllBytesAsync(
                configurationPath,
                replacement,
                cancellationToken);
            return receipt;
        }
    }

    private sealed class BlockingMutationBackup(
        IConfigurationMutationBackup inner,
        TaskCompletionSource backupStarted,
        TaskCompletionSource releaseBackup) : IConfigurationMutationBackup
    {
        public async ValueTask<ConfigurationBackupReceipt> BeforeReplaceAsync(
            string configurationPath,
            byte[] expectedContents,
            CancellationToken cancellationToken)
        {
            backupStarted.TrySetResult();
            await releaseBackup.Task.WaitAsync(cancellationToken);
            return await inner.BeforeReplaceAsync(
                configurationPath,
                expectedContents,
                cancellationToken);
        }
    }
}
