using System.Text;
using System.Security.Cryptography;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ProviderScopedConfigurationBackupStoreTests
{
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
