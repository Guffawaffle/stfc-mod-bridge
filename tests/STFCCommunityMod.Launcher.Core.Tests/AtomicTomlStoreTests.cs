using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class AtomicTomlStoreTests
{
    [TestMethod]
    public async Task SetOverrideReplacesAtomicallyAndMaintainsBackup()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original = "[settings]\nenabled = false\n";
        await File.WriteAllTextAsync(configPath, original);
        var store = new AtomicTomlStore();

        var result = await store.SetOverrideAsync(configPath, "settings.enabled", "true");

        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
        Assert.AreEqual("[settings]\nenabled = true\n", await File.ReadAllTextAsync(configPath));
        Assert.AreEqual(original, await File.ReadAllTextAsync(configPath + ".bak"));
    }

    [TestMethod]
    public async Task InjectedFailureBeforeReplaceLeavesOriginalIntactAndCleansTemp()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original = "[settings]\nenabled = false\n";
        await File.WriteAllTextAsync(configPath, original);
        var store = new AtomicTomlStore(
            (_, _, _) => ValueTask.FromException(new IOException("Injected before replacement.")));

        var result = await store.SetOverrideAsync(configPath, "settings.enabled", "true");

        Assert.AreEqual(AtomicTomlWriteState.IoFailure, result.State);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configPath));
        Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task MissingSelectionIsExplicitAndDoesNotTouchDisk()
    {
        var store = new AtomicTomlStore();

        var result = await store.SetOverrideAsync(null, "settings.enabled", "true");

        Assert.AreEqual(AtomicTomlWriteState.NoConfigurationSelected, result.State);
    }

    [TestMethod]
    public async Task InvalidDuplicateInputIsNeverOverwritten()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original = "[settings]\nenabled = false\nenabled = true\n";
        await File.WriteAllTextAsync(configPath, original);
        var store = new AtomicTomlStore();

        var result = await store.SetOverrideAsync(configPath, "settings.enabled", "true");

        Assert.AreEqual(AtomicTomlWriteState.Invalid, result.State);
        Assert.AreEqual(SparseTomlErrorCode.DuplicateTarget, result.ValidationError?.Code);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configPath));
        Assert.IsFalse(File.Exists(configPath + ".bak"));
    }

    [TestMethod]
    public async Task SuccessfulReplacementRefreshesAnExistingBackup()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        await File.WriteAllTextAsync(configPath, "[settings]\nenabled = false\n");
        await File.WriteAllTextAsync(configPath + ".bak", "stale backup");
        var store = new AtomicTomlStore();

        var result = await store.SetOverrideAsync(configPath, "settings.enabled", "true");

        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
        Assert.AreEqual("[settings]\nenabled = false\n", await File.ReadAllTextAsync(configPath + ".bak"));
    }

    [TestMethod]
    public async Task InvalidStatementInputIsNeverOverwritten()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original = "[settings]\nthis is not TOML\nenabled = false\n";
        await File.WriteAllTextAsync(configPath, original);
        var store = new AtomicTomlStore();

        var result = await store.SetOverrideAsync(configPath, "settings.enabled", "true");

        Assert.AreEqual(AtomicTomlWriteState.Invalid, result.State);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configPath));
        Assert.IsFalse(File.Exists(configPath + ".bak"));
    }

    [TestMethod]
    public async Task ExternalChangeBeforeReplaceWinsAndReturnsConflict()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        await File.WriteAllTextAsync(configPath, "[settings]\nenabled = false\n");
        const string externalChange = "[settings]\nenabled = false\nexternal = \"keep me\"\n";
        var store = new AtomicTomlStore(
            async (_, destination, cancellationToken) =>
            {
                await File.WriteAllTextAsync(destination, externalChange, cancellationToken);
            });

        var result = await store.SetOverrideAsync(configPath, "settings.enabled", "true");

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        Assert.AreEqual(externalChange, await File.ReadAllTextAsync(configPath));
        Assert.IsFalse(File.Exists(configPath + ".bak"));
        Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task StoreInstancesSerializeWritesToTheSameNormalizedPath()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        await File.WriteAllTextAsync(configPath, "[settings]\nvalue = \"initial\"\n");
        var firstEnteredReplaceHook = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEnteredReplaceHook = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStore = new AtomicTomlStore(
            async (_, _, cancellationToken) =>
            {
                firstEnteredReplaceHook.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            });
        var secondStore = new AtomicTomlStore(
            (_, _, _) =>
            {
                secondEnteredReplaceHook.SetResult();
                return ValueTask.CompletedTask;
            });

        var firstWrite = firstStore.SetOverrideAsync(configPath, "settings.value", "\"first\"");
        await firstEnteredReplaceHook.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var alternateSpelling = OperatingSystem.IsWindows()
            ? configPath.ToUpperInvariant()
            : configPath;
        var secondWrite = secondStore.SetOverrideAsync(
            alternateSpelling,
            "settings.value",
            "\"second\"");

        Assert.IsFalse(secondWrite.IsCompleted);
        Assert.IsFalse(secondEnteredReplaceHook.Task.IsCompleted);

        releaseFirst.SetResult();
        var firstResult = await firstWrite.WaitAsync(TimeSpan.FromSeconds(5));
        var secondResult = await secondWrite.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(AtomicTomlWriteState.Succeeded, firstResult.State, firstResult.Error);
        Assert.AreEqual(AtomicTomlWriteState.Succeeded, secondResult.State, secondResult.Error);
        Assert.IsTrue(secondEnteredReplaceHook.Task.IsCompleted);
        Assert.AreEqual("[settings]\nvalue = \"second\"\n", await File.ReadAllTextAsync(configPath));
    }

    [TestMethod]
    public async Task SaveDocumentWritesOneBatchAgainstTheExpectedBaseline()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var original = System.Text.Encoding.UTF8.GetBytes(
            "[graphics]\nfree_resize = true\nallow_cursor = true\n");
        var updated = System.Text.Encoding.UTF8.GetBytes(
            "[graphics]\nfree_resize = false\nallow_cursor = false\n");
        await File.WriteAllBytesAsync(configPath, original);
        var store = new AtomicTomlStore();

        var result = await store.SaveDocumentAsync(configPath, original, updated);

        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
        CollectionAssert.AreEqual(updated, await File.ReadAllBytesAsync(configPath));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(configPath + ".bak"));
    }

    [TestMethod]
    public async Task SaveDocumentPreservesAnExternalEditMadeAfterSessionLoad()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var original = System.Text.Encoding.UTF8.GetBytes("[graphics]\nfree_resize = true\n");
        var launcherUpdate = System.Text.Encoding.UTF8.GetBytes("[graphics]\nfree_resize = false\n");
        const string externalUpdate = "[graphics]\nfree_resize = true\n# player edit\n";
        await File.WriteAllBytesAsync(configPath, original);
        await File.WriteAllTextAsync(configPath, externalUpdate);
        var store = new AtomicTomlStore();

        var result = await store.SaveDocumentAsync(configPath, original, launcherUpdate);

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        Assert.AreEqual(externalUpdate, await File.ReadAllTextAsync(configPath));
        Assert.IsFalse(File.Exists(configPath + ".bak"));
    }

    [TestMethod]
    public async Task SaveDocumentReportsDisappearanceAsAConflict()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var original = System.Text.Encoding.UTF8.GetBytes("[graphics]\nfree_resize = true\n");
        var launcherUpdate = System.Text.Encoding.UTF8.GetBytes("[graphics]\nfree_resize = false\n");
        await File.WriteAllBytesAsync(configPath, original);
        File.Delete(configPath);
        var store = new AtomicTomlStore();

        var result = await store.SaveDocumentAsync(configPath, original, launcherUpdate);

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        Assert.IsFalse(File.Exists(configPath));
        Assert.IsFalse(File.Exists(configPath + ".bak"));
    }

    [TestMethod]
    public async Task VerifiedBackupStoreCanObserveTheDurableStagingBoundary()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var original = Encoding.UTF8.GetBytes("[settings]\nenabled = false\n");
        var updated = Encoding.UTF8.GetBytes("[settings]\nenabled = true\n");
        await File.WriteAllBytesAsync(configPath, original);
        var receipt = new ConfigurationBackupReceipt(
            "backup-id",
            "installation-id",
            "guffawaffle",
            null,
            new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
            ConfigurationDocumentRevision.FromContents(original).Sha256,
            "settings-save",
            "guffawaffle/stable");
        var mutationBackup = new RecordingMutationBackup(receipt);
        var stagingObservations = 0;
        var store = new AtomicTomlStore(
            mutationBackup,
            (temporaryPath, targetPath, _) =>
            {
                stagingObservations++;
                Assert.IsTrue(File.Exists(temporaryPath));
                Assert.AreEqual(configPath, targetPath);
                return ValueTask.CompletedTask;
            });

        var result = await store.SaveDocumentAsync(configPath, original, updated);

        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
        Assert.AreEqual(1, stagingObservations);
        Assert.AreEqual(1, mutationBackup.CallCount);
        Assert.AreEqual(receipt, result.BackupReceipt);
        CollectionAssert.AreEqual(updated, await File.ReadAllBytesAsync(configPath));
        Assert.IsFalse(File.Exists(configPath + ".bak"));
    }

    [TestMethod]
    public async Task VerifiedBackupReceiptSurvivesPostBackupConflict()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var original = Encoding.UTF8.GetBytes("[settings]\nenabled = false\n");
        var updated = Encoding.UTF8.GetBytes("[settings]\nenabled = true\n");
        var external = Encoding.UTF8.GetBytes(
            "[settings]\nenabled = false\nexternal = \"preserve\"\n");
        await File.WriteAllBytesAsync(configPath, original);
        var receipt = new ConfigurationBackupReceipt(
            "backup-id",
            "installation-id",
            "guffawaffle",
            null,
            new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero),
            ConfigurationDocumentRevision.FromContents(original).Sha256,
            "configuration-migration",
            "guffawaffle/stable");
        var mutationBackup = new ReplacingMutationBackup(receipt, external);
        var store = new AtomicTomlStore(mutationBackup);

        var result = await store.SaveDocumentAsync(configPath, original, updated);

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State);
        Assert.AreEqual(receipt, result.BackupReceipt);
        CollectionAssert.AreEqual(original, mutationBackup.ExpectedContents);
        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(configPath));
        Assert.IsFalse(File.Exists(configPath + ".bak"));
        Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task MismatchedBackupReceiptPreventsReplacement()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var original = Encoding.UTF8.GetBytes("[settings]\nenabled = false\n");
        var updated = Encoding.UTF8.GetBytes("[settings]\nenabled = true\n");
        await File.WriteAllBytesAsync(configPath, original);
        var receipt = new ConfigurationBackupReceipt(
            "backup-id",
            "installation-id",
            "guffawaffle",
            null,
            new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero),
            "NOT-THE-SOURCE-HASH",
            "configuration-migration",
            null);
        var store = new AtomicTomlStore(
            new ReplacingMutationBackup(receipt, original));

        var result = await store.SaveDocumentAsync(configPath, original, updated);

        Assert.AreEqual(AtomicTomlWriteState.IoFailure, result.State);
        Assert.IsNull(result.BackupReceipt);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(configPath));
        Assert.IsFalse(File.Exists(configPath + ".bak"));
    }

    private sealed class ReplacingMutationBackup(
        ConfigurationBackupReceipt receipt,
        byte[] replacement) : IConfigurationMutationBackup
    {
        public byte[]? ExpectedContents { get; private set; }

        public async ValueTask<ConfigurationBackupReceipt> BeforeReplaceAsync(
            string configurationPath,
            byte[] expectedContents,
            CancellationToken cancellationToken)
        {
            ExpectedContents = [.. expectedContents];
            await File.WriteAllBytesAsync(
                configurationPath,
                replacement,
                cancellationToken);
            return receipt;
        }
    }

    private sealed class RecordingMutationBackup(ConfigurationBackupReceipt receipt)
        : IConfigurationMutationBackup
    {
        public int CallCount { get; private set; }

        public ValueTask<ConfigurationBackupReceipt> BeforeReplaceAsync(
            string configurationPath,
            byte[] expectedContents,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(receipt);
        }
    }
}
