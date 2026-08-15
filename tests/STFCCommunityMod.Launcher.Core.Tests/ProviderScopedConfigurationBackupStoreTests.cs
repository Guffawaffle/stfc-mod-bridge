using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ProviderScopedConfigurationBackupStoreTests
{
    private const string CrashStageEnvironment = "STFC_BRIDGE_CONFIGURATION_BACKUP_CRASH_STAGE";
    private const string CrashRootEnvironment = "STFC_BRIDGE_CONFIGURATION_BACKUP_CRASH_ROOT";
    private const string CrashReadyEnvironment = "STFC_BRIDGE_CONFIGURATION_BACKUP_CRASH_READY";

    [TestMethod]
    public async Task ProviderHistoriesAreIndependentAndRestoreExactBytes()
    {
        using var directory = new TemporaryDirectory();
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory, "game");
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var protector = new ReversingProtector();
        var store = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            protector,
            new NoOpStorageSecurity());
        var guffawaffleContents = Encoding.UTF8.GetBytes("provider = 'guffawaffle'\n");
        var netnivContents = Encoding.UTF8.GetBytes("provider = 'netniv'\r\n");

        var guffawaffle = await store.CreateAsync(new(
            gameDirectory,
            "guffawaffle",
            configurationPath,
            guffawaffleContents,
            "settings-save"));
        var netniv = await store.CreateAsync(new(
            gameDirectory,
            "netniv",
            configurationPath,
            netnivContents,
            "settings-save"));

        Assert.AreEqual(1, store.List(gameDirectory, "guffawaffle").Count);
        Assert.AreEqual(1, store.List(gameDirectory, "netniv").Count);
        CollectionAssert.AreEqual(
            guffawaffleContents,
            store.Read(gameDirectory, "guffawaffle", guffawaffle.BackupId));
        CollectionAssert.AreEqual(
            netnivContents,
            store.Read(gameDirectory, "netniv", netniv.BackupId));
    }

    [TestMethod]
    public async Task SixthBackupPrunesOnlyOldestInSameProviderPartition()
    {
        using var directory = new TemporaryDirectory();
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory, "game");
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var clock = new IncrementingTimeProvider();
        var store = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity(),
            clock);
        var guffawaffleReceipts = new List<ConfigurationBackupReceipt>();
        var netnivReceipts = new List<ConfigurationBackupReceipt>();
        for (var index = 0; index < 6; index++)
        {
            guffawaffleReceipts.Add(await CreateAsync(store, gameDirectory, configurationPath, "guffawaffle", index));
            netnivReceipts.Add(await CreateAsync(store, gameDirectory, configurationPath, "netniv", index));
        }

        var guffawaffle = store.List(gameDirectory, "guffawaffle");
        var netniv = store.List(gameDirectory, "netniv");

        Assert.AreEqual(ProviderScopedConfigurationBackupStore.DefaultRetentionCount, guffawaffle.Count);
        Assert.AreEqual(ProviderScopedConfigurationBackupStore.DefaultRetentionCount, netniv.Count);
        Assert.IsFalse(guffawaffle.Any(receipt => receipt.BackupId == guffawaffleReceipts[0].BackupId));
        Assert.IsFalse(netniv.Any(receipt => receipt.BackupId == netnivReceipts[0].BackupId));
        Assert.IsTrue(guffawaffle.All(receipt => receipt.ProviderId == "guffawaffle"));
        Assert.IsTrue(netniv.All(receipt => receipt.ProviderId == "netniv"));
    }

    [TestMethod]
    public async Task PinnedHistoryEntrySurvivesPreRestoreBackupPruning()
    {
        using var directory = new TemporaryDirectory();
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory, "game");
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var store = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity(),
            new IncrementingTimeProvider());
        var receipts = new List<ConfigurationBackupReceipt>();
        for (var index = 0; index < 5; index++)
        {
            receipts.Add(await CreateAsync(
                store,
                gameDirectory,
                configurationPath,
                "guffawaffle",
                index));
        }

        var preRestore = await store.CreateAsync(new(
            gameDirectory,
            "guffawaffle",
            configurationPath,
            Encoding.UTF8.GetBytes("value = 'live-before-restore'\n"),
            "manual-restore",
            ReleaseIdentity: $"configuration-history-restore/{Guid.NewGuid():N}",
            PinnedBackupId: receipts[0].BackupId));

        var retained = store.List(gameDirectory, "guffawaffle");

        Assert.AreEqual(ProviderScopedConfigurationBackupStore.DefaultRetentionCount, retained.Count);
        Assert.IsTrue(retained.Any(receipt => receipt.BackupId == receipts[0].BackupId));
        Assert.IsTrue(retained.Any(receipt => receipt.BackupId == preRestore.BackupId));
        Assert.IsFalse(retained.Any(receipt => receipt.BackupId == receipts[1].BackupId));
    }

    [TestMethod]
    public async Task PinnedRestoreSourceNeverDisplacesNewRollbackBackupAtMinimumRetention()
    {
        using var directory = new TemporaryDirectory();
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory, "game");
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var store = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity(),
            new IncrementingTimeProvider(),
            retentionCount: 1);
        var source = await CreateAsync(
            store,
            gameDirectory,
            configurationPath,
            "guffawaffle",
            1);
        var rollbackContents = Encoding.UTF8.GetBytes("value = 'live-before-restore'\n");

        var rollback = await store.CreateAsync(new(
            gameDirectory,
            "guffawaffle",
            configurationPath,
            rollbackContents,
            "manual-restore",
            ReleaseIdentity: $"configuration-history-restore/{Guid.NewGuid():N}",
            PinnedBackupId: source.BackupId));

        var retained = store.List(gameDirectory, "guffawaffle");
        Assert.AreEqual(2, retained.Count);
        Assert.IsTrue(retained.Any(receipt => receipt.BackupId == source.BackupId));
        Assert.IsTrue(retained.Any(receipt => receipt.BackupId == rollback.BackupId));
        CollectionAssert.AreEqual(
            rollbackContents,
            store.Read(gameDirectory, "guffawaffle", rollback.BackupId));
    }

    [DataTestMethod]
    [DataRow("PayloadDurable")]
    [DataRow("ManifestDurable")]
    [DataRow("Published")]
    [DataRow("BeforePruneDelete")]
    [DataRow("AfterPruneDelete")]
    public async Task HardCrashDuringSixthBackupPreservesNewestRecoverableHistory(string crashStage)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = new TemporaryDirectory();
        var readyPath = Path.Combine(directory.Path, "ready");
        using var child = StartCrashProbe(crashStage, directory.Path, readyPath);
        try
        {
            await WaitForCrashProbeAsync(child, readyPath);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

            var stateDirectory = Path.Combine(directory.Path, "state");
            var gameDirectory = Path.Combine(directory.Path, "game");
            var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
            var store = new ProviderScopedConfigurationBackupStore(
                stateDirectory,
                new ReversingProtector(),
                new NoOpStorageSecurity());
            var guffawaffle = store.List(gameDirectory, "guffawaffle");
            var expected = crashStage switch
            {
                "PayloadDurable" or "ManifestDurable" => Enumerable.Range(0, 5),
                "Published" or "BeforePruneDelete" => Enumerable.Range(0, 6),
                "AfterPruneDelete" => Enumerable.Range(1, 5),
                _ => throw new InvalidOperationException($"Unknown crash stage '{crashStage}'."),
            };
            var recoveredValues = guffawaffle
                .Select(receipt => Encoding.UTF8.GetString(store.Read(
                    gameDirectory,
                    "guffawaffle",
                    receipt.BackupId)))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(
                expected.Select(index => $"value = {index}\n").ToArray(),
                recoveredValues);
            var other = store.List(gameDirectory, "netniv").Single();
            CollectionAssert.AreEqual(
                Encoding.UTF8.GetBytes("other = true\n"),
                store.Read(gameDirectory, "netniv", other.BackupId));
            Assert.IsTrue(guffawaffle.Count >= ProviderScopedConfigurationBackupStore.DefaultRetentionCount);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }

    [TestMethod]
    public async Task ProviderConfigurationBackupHardCrashProbe()
    {
        var configuredStage = Environment.GetEnvironmentVariable(CrashStageEnvironment);
        if (string.IsNullOrWhiteSpace(configuredStage))
        {
            return;
        }
        var crashStage = Enum.Parse<ProviderConfigurationBackupCheckpoint>(configuredStage);
        var root = Environment.GetEnvironmentVariable(CrashRootEnvironment)
            ?? throw new InvalidOperationException("The configuration-backup crash root is absent.");
        var readyPath = Environment.GetEnvironmentVariable(CrashReadyEnvironment)
            ?? throw new InvalidOperationException("The configuration-backup crash ready path is absent.");
        var stateDirectory = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        var gameDirectory = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var armed = false;
        async ValueTask Checkpoint(
            ProviderConfigurationBackupCheckpoint current,
            CancellationToken cancellationToken)
        {
            if (!armed || current != crashStage)
            {
                return;
            }
            await File.WriteAllTextAsync(readyPath, current.ToString(), cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        var store = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity(),
            new IncrementingTimeProvider(),
            ProviderScopedConfigurationBackupStore.DefaultRetentionCount,
            Checkpoint);
        _ = await store.CreateAsync(new(
            gameDirectory,
            "netniv",
            configurationPath,
            Encoding.UTF8.GetBytes("other = true\n"),
            "settings-save"));
        for (var index = 0; index < 5; index++)
        {
            _ = await CreateAsync(store, gameDirectory, configurationPath, "guffawaffle", index);
        }
        armed = true;
        _ = await CreateAsync(store, gameDirectory, configurationPath, "guffawaffle", 5);
        Assert.Fail($"Configuration-backup crash probe passed stage '{configuredStage}'.");
    }

    [TestMethod]
    public async Task RestoredReceiptIsDurableIdempotentAndRetainsExactPayload()
    {
        using var directory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(directory, "game");
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var contents = Encoding.UTF8.GetBytes("# protected history\r\n");
        var store = new ProviderScopedConfigurationBackupStore(
            directory.CreateDirectory("state"),
            new ReversingProtector(),
            new NoOpStorageSecurity(),
            new IncrementingTimeProvider());
        var receipt = await store.CreateAsync(new(
            gameDirectory,
            "guffawaffle",
            configurationPath,
            contents,
            "settings-save"));
        var transactionId = Guid.NewGuid().ToString("N");

        var restored = await store.MarkRestoredAsync(
            gameDirectory,
            "guffawaffle",
            receipt.BackupId,
            transactionId);
        var repeated = await store.MarkRestoredAsync(
            gameDirectory,
            "guffawaffle",
            receipt.BackupId,
            transactionId);

        Assert.IsTrue(restored.WasRestored);
        Assert.IsNotNull(restored.RestoredAtUtc);
        Assert.AreEqual(transactionId, restored.RestoreTransactionId);
        Assert.AreEqual(restored, repeated);
        Assert.AreEqual(restored, store.List(gameDirectory, "guffawaffle").Single());
        CollectionAssert.AreEqual(
            contents,
            store.Read(gameDirectory, "guffawaffle", receipt.BackupId));
    }

    [TestMethod]
    public async Task LegacyManifestCanBeReadAndIsUpgradedWhenMarkedRestored()
    {
        using var directory = new TemporaryDirectory();
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory, "game");
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var store = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity());
        var receipt = await store.CreateAsync(new(
            gameDirectory,
            "guffawaffle",
            configurationPath,
            Encoding.UTF8.GetBytes("legacy = true\n"),
            "settings-save"));
        var manifestPath = Path.Combine(
            stateDirectory,
            "configuration-backups",
            receipt.InstallationId,
            receipt.ProviderId,
            receipt.BackupId,
            "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["schemaVersion"] = 1;
        manifest.Remove("restoredAtUtc");
        manifest.Remove("restoreTransactionId");
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        Assert.IsFalse(store.List(gameDirectory, "guffawaffle").Single().WasRestored);
        var restored = await store.MarkRestoredAsync(
            gameDirectory,
            "guffawaffle",
            receipt.BackupId,
            Guid.NewGuid().ToString("N"));

        Assert.IsTrue(restored.WasRestored);
        var upgraded = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        Assert.AreEqual(2, upgraded["schemaVersion"]!.GetValue<int>());
        Assert.IsNotNull(upgraded["restoredAtUtc"]);
        Assert.IsNotNull(upgraded["restoreTransactionId"]);
    }

    [TestMethod]
    public async Task SameProviderIsPartitionedByValidatedInstallation()
    {
        using var directory = new TemporaryDirectory();
        var stateDirectory = directory.CreateDirectory("state");
        var firstGame = CreateGameDirectory(directory, "game-one");
        var secondGame = CreateGameDirectory(directory, "game-two");
        var store = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity());

        await CreateAsync(
            store,
            firstGame,
            Path.Combine(firstGame, "community_patch_settings.toml"),
            "guffawaffle",
            1);

        Assert.AreEqual(1, store.List(firstGame, "guffawaffle").Count);
        Assert.AreEqual(0, store.List(secondGame, "guffawaffle").Count);
    }

    [TestMethod]
    public async Task ConfigurationOutsideValidatedInstallationIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(directory, "game");
        var store = new ProviderScopedConfigurationBackupStore(
            directory.CreateDirectory("state"),
            new ReversingProtector(),
            new NoOpStorageSecurity());

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.CreateAsync(new(
            gameDirectory,
            "guffawaffle",
            Path.Combine(directory.Path, "community_patch_settings.toml"),
            Encoding.UTF8.GetBytes("safe = true\n"),
            "settings-save")));
    }

    [TestMethod]
    public void WindowsDpapiProtectorRoundTripsExactBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var contents = Encoding.UTF8.GetBytes("token = 'secret-value'\r\n");
        var protector = new WindowsDpapiConfigurationBackupProtector();

        var protectedContents = protector.Protect(contents);
        var restored = protector.Unprotect(protectedContents);

        CollectionAssert.AreNotEqual(contents, protectedContents);
        CollectionAssert.AreEqual(contents, restored);
    }

    [TestMethod]
    public async Task RepositorySaveCapturesSourceProviderBytesBeforeReplacement()
    {
        using var directory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(directory, "game");
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes("[graphics]\r\nfree_resize = true\r\n");
        var updated = Encoding.UTF8.GetBytes("[graphics]\r\nfree_resize = false\r\n");
        await File.WriteAllBytesAsync(configurationPath, original);
        var store = new ProviderScopedConfigurationBackupStore(
            directory.CreateDirectory("state"),
            new ReversingProtector(),
            new NoOpStorageSecurity());
        var repository = new TomlConfigurationRepository(
            mutationBackup: new ProviderScopedConfigurationMutationBackup(
                store,
                "guffawaffle",
                "guffawaffle/stable"));

        var result = await repository.CommitDocumentAsync(new(
            configurationPath,
            ConfigurationDocumentRevision.FromContents(original),
            original,
            updated));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNotNull(result.BackupReceipt);
        var receipt = store.List(gameDirectory, "guffawaffle").Single();
        Assert.AreEqual(receipt, result.BackupReceipt);
        Assert.AreEqual("configuration-save", receipt.Reason);
        Assert.AreEqual(
            ConfigurationDocumentRevision.FromContents(original).Sha256,
            receipt.ContentSha256);
        CollectionAssert.AreEqual(
            original,
            store.Read(gameDirectory, "guffawaffle", receipt.BackupId));
        CollectionAssert.AreEqual(updated, await File.ReadAllBytesAsync(configurationPath));
        Assert.IsFalse(File.Exists(configurationPath + ".bak"));
    }

    [TestMethod]
    public async Task BackupFailurePreventsConfigurationReplacement()
    {
        using var directory = new TemporaryDirectory();
        var gameDirectory = CreateGameDirectory(directory, "game");
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes("[graphics]\nfree_resize = true\n");
        var updated = Encoding.UTF8.GetBytes("[graphics]\nfree_resize = false\n");
        await File.WriteAllBytesAsync(configurationPath, original);
        var store = new ProviderScopedConfigurationBackupStore(
            directory.CreateDirectory("state"),
            new FailingProtector(),
            new NoOpStorageSecurity());
        var repository = new TomlConfigurationRepository(
            mutationBackup: new ProviderScopedConfigurationMutationBackup(
                store,
                "guffawaffle"));

        var result = await repository.CommitDocumentAsync(new(
            configurationPath,
            ConfigurationDocumentRevision.FromContents(original),
            original,
            updated));

        Assert.AreEqual(AtomicTomlWriteState.IoFailure, result.State);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(configurationPath));
        Assert.IsFalse(File.Exists(configurationPath + ".bak"));
    }

    private static Task<ConfigurationBackupReceipt> CreateAsync(
        ProviderScopedConfigurationBackupStore store,
        string gameDirectory,
        string configurationPath,
        string providerId,
        int index) =>
        store.CreateAsync(new(
            gameDirectory,
            providerId,
            configurationPath,
            Encoding.UTF8.GetBytes($"value = {index}\n"),
            "settings-save"));

    private static Process StartCrashProbe(string crashStage, string root, string readyPath)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(ProviderScopedConfigurationBackupStoreTests).Assembly.Location);
        start.ArgumentList.Add(
            "--Tests:STFCCommunityMod.Launcher.Core.Tests."
            + "ProviderScopedConfigurationBackupStoreTests.ProviderConfigurationBackupHardCrashProbe");
        start.Environment[CrashStageEnvironment] = crashStage;
        start.Environment[CrashRootEnvironment] = root;
        start.Environment[CrashReadyEnvironment] = readyPath;
        return Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the configuration-backup crash probe.");
    }

    private static async Task WaitForCrashProbeAsync(Process child, string readyPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(readyPath))
        {
            if (child.HasExited)
            {
                var output = await child.StandardOutput.ReadToEndAsync();
                var error = await child.StandardError.ReadToEndAsync();
                Assert.Fail(
                    $"Configuration-backup crash probe exited before its hold point. "
                    + $"Output: {output} Error: {error}");
            }
            await Task.Delay(50, timeout.Token);
        }
    }

    private static string CreateGameDirectory(TemporaryDirectory directory, string name)
    {
        var gameDirectory = directory.CreateDirectory(name);
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

    private sealed class FailingProtector : IConfigurationBackupProtector
    {
        public string SchemeId => "test-failure-v1";

        public byte[] Protect(byte[] contents) =>
            throw new CryptographicException("Injected protection failure.");

        public byte[] Unprotect(byte[] protectedContents) =>
            throw new InvalidOperationException();
    }

    private sealed class IncrementingTimeProvider : TimeProvider
    {
        private DateTimeOffset current = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            var result = current;
            current = current.AddSeconds(1);
            return result;
        }
    }
}
