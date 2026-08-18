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
    public async Task MutationAdmissionRejectsBeforeTemporaryWriteWithoutTouchingDisk()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original = "[settings]\nenabled = false\n";
        await File.WriteAllTextAsync(configPath, original);
        var admission = new RecordingMutationAdmission(
            AtomicTomlMutationBoundary.TemporaryWrite);
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: true,
            mutationAdmission: admission);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.SetOverrideAsync(configPath, "settings.enabled", "true"));

        CollectionAssert.AreEqual(
            new[] { AtomicTomlMutationBoundary.TemporaryWrite },
            admission.Boundaries);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configPath));
        Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task MutationAdmissionRechecksPromotionAndTemporaryCleanup()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original = "[settings]\nenabled = false\n";
        await File.WriteAllTextAsync(configPath, original);
        var admission = new RecordingMutationAdmission(
            AtomicTomlMutationBoundary.Promotion);
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: true,
            mutationAdmission: admission);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.SetOverrideAsync(configPath, "settings.enabled", "true"));

        CollectionAssert.AreEqual(
            new[]
            {
                AtomicTomlMutationBoundary.TemporaryWrite,
                AtomicTomlMutationBoundary.Promotion,
                AtomicTomlMutationBoundary.TemporaryDelete,
            },
            admission.Boundaries);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configPath));
        Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory.Path, "*.tmp").Length);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExternalChangeDuringPromotionAdmissionWins(bool useDocumentSave)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var original = Encoding.UTF8.GetBytes("[settings]\nenabled = false\n");
        var updated = Encoding.UTF8.GetBytes("[settings]\nenabled = true\n");
        var external = Encoding.UTF8.GetBytes(
            "[settings]\nenabled = false\nexternal = \"preserve\"\n");
        await File.WriteAllBytesAsync(configPath, original);
        var admission = new CallbackMutationAdmission
        {
            OnAdmit = (boundary, _, destination) =>
            {
                if (boundary == AtomicTomlMutationBoundary.Promotion)
                {
                    File.WriteAllBytes(destination, external);
                }
            },
        };
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: true,
            mutationAdmission: admission);

        var result = useDocumentSave
            ? await store.SaveDocumentAsync(configPath, original, updated)
            : await store.SetOverrideAsync(configPath, "settings.enabled", "true");

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State, result.Error);
        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(configPath));
        Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task ExternalCreateDuringPromotionAdmissionWins()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var intended = Encoding.UTF8.GetBytes("[settings]\nenabled = true\n");
        var external = Encoding.UTF8.GetBytes("[settings]\nexternal = \"preserve\"\n");
        var admission = new CallbackMutationAdmission
        {
            OnAdmit = (boundary, _, destination) =>
            {
                if (boundary == AtomicTomlMutationBoundary.Promotion)
                {
                    File.WriteAllBytes(destination, external);
                }
            },
        };
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: true,
            mutationAdmission: admission);

        var result = await store.CreateDocumentAsync(configPath, intended);

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State, result.Error);
        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(configPath));
        Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory.Path, "*.tmp").Length);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task AlteredStageIsNeverPromotedOrDeleted(int operation)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var original = Encoding.UTF8.GetBytes("[settings]\nenabled = false\n");
        var updated = Encoding.UTF8.GetBytes("[settings]\nenabled = true\n");
        var alteredStage = Encoding.UTF8.GetBytes("external stage bytes");
        if (operation != 2)
        {
            await File.WriteAllBytesAsync(configPath, original);
        }
        var admission = new CallbackMutationAdmission
        {
            OnAdmit = (boundary, temporary, _) =>
            {
                if (boundary == AtomicTomlMutationBoundary.Promotion)
                {
                    File.WriteAllBytes(temporary, alteredStage);
                }
            },
        };
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: true,
            mutationAdmission: admission);

        var result = operation switch
        {
            0 => await store.SaveDocumentAsync(configPath, original, updated),
            1 => await store.SetOverrideAsync(configPath, "settings.enabled", "true"),
            _ => await store.CreateDocumentAsync(configPath, updated),
        };

        Assert.AreEqual(AtomicTomlWriteState.IoFailure, result.State, result.Error);
        if (operation == 2)
        {
            Assert.IsFalse(File.Exists(configPath));
        }
        else
        {
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(configPath));
        }
        var retainedStage = Directory.GetFiles(temporaryDirectory.Path, "*.tmp").Single();
        CollectionAssert.AreEqual(alteredStage, await File.ReadAllBytesAsync(retainedStage));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LateDestinationChangeAtFinalAdmissionIsRestored(bool useDocumentSave)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        var original = Encoding.UTF8.GetBytes("[settings]\nenabled = false\n");
        var updated = Encoding.UTF8.GetBytes("[settings]\nenabled = true\n");
        var external = Encoding.UTF8.GetBytes(
            "[settings]\nenabled = false\nexternal = \"late\"\n");
        await File.WriteAllBytesAsync(configPath, original);
        var injected = false;
        var admission = new CallbackMutationAdmission
        {
            OnVerifyCommit = (boundary, _, destination) =>
            {
                if (!injected && boundary == AtomicTomlMutationBoundary.Promotion)
                {
                    injected = true;
                    File.WriteAllBytes(destination, external);
                }
            },
        };
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: false,
            mutationAdmission: admission);

        var result = useDocumentSave
            ? await store.SaveDocumentAsync(configPath, original, updated)
            : await store.SetOverrideAsync(configPath, "settings.enabled", "true");

        Assert.AreEqual(AtomicTomlWriteState.Conflict, result.State, result.Error);
        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(configPath));
    }

    [TestMethod]
    public async Task FinalAdmissionRejectsBeforePromotionAndReleasesGate()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original = "[settings]\nenabled = false\n";
        await File.WriteAllTextAsync(configPath, original);
        var reject = true;
        var admission = new CallbackMutationAdmission
        {
            OnVerifyCommit = (boundary, _, _) =>
            {
                if (reject && boundary == AtomicTomlMutationBoundary.Promotion)
                {
                    throw new InvalidOperationException("Injected final-admission rejection.");
                }
            },
        };
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: false,
            mutationAdmission: admission);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.SetOverrideAsync(configPath, "settings.enabled", "true"));
        Assert.AreEqual(original, await File.ReadAllTextAsync(configPath));
        reject = false;

        var retry = await store.SetOverrideAsync(
                configPath,
                "settings.enabled",
                "true")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(AtomicTomlWriteState.Succeeded, retry.State, retry.Error);
    }

    [TestMethod]
    public async Task ReceiptPreparationFailureCreatesNoGameStage()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original = "[settings]\nenabled = false\n";
        await File.WriteAllTextAsync(configPath, original);
        var admission = new CallbackMutationAdmission
        {
            OnPreparing = (role, _, _, _, _, _, _) =>
            {
                if (role == AtomicTomlTemporaryRole.WriteStage)
                {
                    throw new IOException("Injected receipt-persistence failure.");
                }
            },
        };
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: false,
            mutationAdmission: admission);

        var result = await store.SetOverrideAsync(
            configPath,
            "settings.enabled",
            "true");

        Assert.AreEqual(AtomicTomlWriteState.IoFailure, result.State);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configPath));
        Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task ExternallyCreatedTemporaryPathIsNeverDeleted()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original = "[settings]\nenabled = false\n";
        var sentinel = Encoding.UTF8.GetBytes("external temporary sentinel");
        string? temporaryPath = null;
        await File.WriteAllTextAsync(configPath, original);
        var admission = new CallbackMutationAdmission
        {
            OnAdmit = (boundary, temporary, _) =>
            {
                if (boundary == AtomicTomlMutationBoundary.TemporaryWrite)
                {
                    temporaryPath = temporary;
                    File.WriteAllBytes(temporary, sentinel);
                }
            },
        };
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: true,
            mutationAdmission: admission);

        var result = await store.SetOverrideAsync(
            configPath,
            "settings.enabled",
            "true");

        Assert.AreEqual(AtomicTomlWriteState.IoFailure, result.State);
        Assert.IsNotNull(temporaryPath);
        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(temporaryPath));
    }

    [TestMethod]
    public async Task CleanupAdmissionFailureNeverLeaksThePerPathGate()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original = "[settings]\nenabled = false\n";
        await File.WriteAllTextAsync(configPath, original);
        var reject = true;
        var admission = new CallbackMutationAdmission
        {
            OnAdmit = (boundary, _, _) =>
            {
                if (reject && boundary is AtomicTomlMutationBoundary.Promotion
                    or AtomicTomlMutationBoundary.TemporaryDelete)
                {
                    throw new InvalidOperationException("Injected admission failure.");
                }
            },
        };
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: true,
            mutationAdmission: admission);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.SetOverrideAsync(configPath, "settings.enabled", "true"));
        reject = false;
        var retry = await store.SetOverrideAsync(
                configPath,
                "settings.enabled",
                "true")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(AtomicTomlWriteState.Succeeded, retry.State, retry.Error);
    }

    [TestMethod]
    public async Task CancellationAfterTemporaryCreationStillCleansAndReleasesGate()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configPath = Path.Combine(temporaryDirectory.Path, "settings.toml");
        const string original = "[settings]\nenabled = false\n";
        await File.WriteAllTextAsync(configPath, original);
        using var cancellation = new CancellationTokenSource();
        var cancelOnce = true;
        var admission = new CallbackMutationAdmission
        {
            OnCreated = _ =>
            {
                if (cancelOnce)
                {
                    cancelOnce = false;
                    cancellation.Cancel();
                }
            },
        };
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: true,
            mutationAdmission: admission);

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() =>
            store.SetOverrideAsync(
                configPath,
                "settings.enabled",
                "true",
                cancellation.Token));
        Assert.AreEqual(1, Directory.GetFiles(temporaryDirectory.Path, "*.tmp").Length);

        var retry = await store.SetOverrideAsync(
                configPath,
                "settings.enabled",
                "true")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(AtomicTomlWriteState.Succeeded, retry.State, retry.Error);
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

    private sealed class RecordingMutationAdmission(AtomicTomlMutationBoundary rejectOnce)
        : IAtomicTomlMutationAdmission
    {
        private bool rejected;

        public List<AtomicTomlMutationBoundary> Boundaries { get; } = [];

        public ValueTask AdmitAsync(
            AtomicTomlMutationBoundary boundary,
            string temporaryPath,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            Boundaries.Add(boundary);
            if (!rejected && boundary == rejectOnce)
            {
                rejected = true;
                throw new InvalidOperationException("Injected mutation-admission rejection.");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CallbackMutationAdmission : IAtomicTomlMutationAdmission
    {
        public Action<AtomicTomlMutationBoundary, string, string>? OnAdmit { get; init; }

        public Action<AtomicTomlTemporaryRole, string, string, long, string, bool, string?>? OnPreparing { get; init; }

        public Action<string>? OnCreated { get; init; }

        public Action<string, long, string, bool>? OnCompleted { get; init; }

        public Action<string>? OnRemoved { get; init; }

        public Action<AtomicTomlMutationBoundary, string, string>? OnVerifyCommit { get; init; }

        public ValueTask AdmitAsync(
            AtomicTomlMutationBoundary boundary,
            string temporaryPath,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnAdmit?.Invoke(boundary, temporaryPath, destinationPath);
            return ValueTask.CompletedTask;
        }

        public void TemporaryPreparing(
            AtomicTomlTemporaryRole role,
            string temporaryPath,
            string destinationPath,
            long expectedSize,
            string expectedSha256,
            bool deletionAllowed,
            string? committedDestinationSha256) =>
            OnPreparing?.Invoke(
                role,
                temporaryPath,
                destinationPath,
                expectedSize,
                expectedSha256,
                deletionAllowed,
                committedDestinationSha256);

        public void TemporaryCreated(
            AtomicTomlTemporaryRole role,
            string temporaryPath) =>
            OnCreated?.Invoke(temporaryPath);

        public void TemporaryCompleted(
            AtomicTomlTemporaryRole role,
            string temporaryPath,
            long actualSize,
            string actualSha256,
            bool deletionAllowed) =>
            OnCompleted?.Invoke(
                temporaryPath,
                actualSize,
                actualSha256,
                deletionAllowed);

        public void TemporaryRemoved(string temporaryPath) =>
            OnRemoved?.Invoke(temporaryPath);

        public void VerifyCommitAllowed(
            AtomicTomlMutationBoundary boundary,
            string temporaryPath,
            string destinationPath) =>
            OnVerifyCommit?.Invoke(boundary, temporaryPath, destinationPath);
    }
}
