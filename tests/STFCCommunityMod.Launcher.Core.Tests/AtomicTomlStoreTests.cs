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
}
