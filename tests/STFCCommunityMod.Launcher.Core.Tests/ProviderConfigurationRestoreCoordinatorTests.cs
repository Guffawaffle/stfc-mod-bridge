using System.Diagnostics;
using System.Text;
using System.Text.Json;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ProviderConfigurationRestoreCoordinatorTests
{
    private const string CrashStageEnvironment = "STFC_BRIDGE_CONFIGURATION_RESTORE_CRASH_STAGE";
    private const string CrashRootEnvironment = "STFC_BRIDGE_CONFIGURATION_RESTORE_CRASH_ROOT";
    private const string CrashReadyEnvironment = "STFC_BRIDGE_CONFIGURATION_RESTORE_CRASH_READY";
    private static readonly JsonSerializerOptions JournalJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task HistoryContainsOnlyVerifiedActiveProviderMetadata()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var activeContents = Encoding.UTF8.GetBytes("# active provider secret-marker\r\n");
        var otherContents = Encoding.UTF8.GetBytes("# other provider secret-marker\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, Encoding.UTF8.GetBytes("# live\n"));
        var active = await CreateBackupAsync(context, "guffawaffle", activeContents);
        await CreateBackupAsync(context, "netniv", otherContents);

        var history = context.Coordinator.LoadHistory();
        var serialized = JsonSerializer.Serialize(history);

        Assert.AreEqual(1, history.Count);
        Assert.AreEqual(active, history[0].Receipt);
        Assert.AreEqual(context.ConfigurationPath, history[0].DestinationPath);
        Assert.AreEqual(ProviderConfigurationCompatibilityState.Compatible, history[0].CompatibilityState);
        Assert.AreEqual("guffawaffle", history[0].Receipt.ProviderId);
        Assert.IsFalse(serialized.Contains("active provider secret-marker", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("other provider secret-marker", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RestoreCommitsExactBytesBacksUpLiveRevisionAndMarksReceipt()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var desired = Encoding.UTF8.GetBytes("# saved history\r\n");
        var baseline = Encoding.UTF8.GetBytes("# live before restore\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, baseline);
        var selected = await CreateBackupAsync(context, "guffawaffle", desired);
        var otherProvider = await CreateBackupAsync(
            context,
            "netniv",
            Encoding.UTF8.GetBytes("# netniv remains independent\n"));
        var preview = context.Coordinator.Preview(selected.BackupId);

        var result = await context.Coordinator.ExecuteAsync(preview, "guffawaffle");

        Assert.AreEqual(ProviderConfigurationRestoreResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(desired, await File.ReadAllBytesAsync(context.ConfigurationPath));
        Assert.IsNotNull(result.PreRestoreBackup);
        Assert.AreEqual("manual-restore", result.PreRestoreBackup.Reason);
        Assert.AreEqual(
            $"configuration-history-restore/{preview.TransactionId}",
            result.PreRestoreBackup.ReleaseIdentity);
        CollectionAssert.AreEqual(
            baseline,
            context.Store.Read(
                context.GameDirectory,
                "guffawaffle",
                result.PreRestoreBackup.BackupId));
        Assert.IsNotNull(result.RestoredBackup);
        Assert.IsTrue(result.RestoredBackup.WasRestored);
        Assert.AreEqual(preview.TransactionId, result.RestoredBackup.RestoreTransactionId);
        Assert.IsNotNull(result.RestoredBackup.RestoredAtUtc);
        Assert.IsFalse(
            context.Store.List(context.GameDirectory, "netniv").Single().WasRestored);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("# netniv remains independent\n"),
            context.Store.Read(context.GameDirectory, "netniv", otherProvider.BackupId));
        Assert.AreEqual(
            ProviderConfigurationRestorePhase.Completed,
            context.Coordinator.ReadJournal()!.Phase);
    }

    [TestMethod]
    public async Task ChangedLiveRevisionRejectsReviewedRestoreBeforeMutation()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var original = Encoding.UTF8.GetBytes("# original live\n");
        var externallyChanged = Encoding.UTF8.GetBytes("# external writer won\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, original);
        var selected = await CreateBackupAsync(
            context,
            "guffawaffle",
            Encoding.UTF8.GetBytes("# saved revision\n"));
        var preview = context.Coordinator.Preview(selected.BackupId);
        await File.WriteAllBytesAsync(context.ConfigurationPath, externallyChanged);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => context.Coordinator.ExecuteAsync(preview, "guffawaffle"));

        CollectionAssert.AreEqual(externallyChanged, await File.ReadAllBytesAsync(context.ConfigurationPath));
        Assert.AreEqual(1, context.Store.List(context.GameDirectory, "guffawaffle").Count);
        Assert.IsNull(context.Coordinator.ReadJournal());
    }

    [TestMethod]
    public async Task ChangingSelectedInstallationAfterReviewInvalidatesRestoreTarget()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var original = Encoding.UTF8.GetBytes("# original installation live\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, original);
        var selected = await CreateBackupAsync(
            context,
            "guffawaffle",
            Encoding.UTF8.GetBytes("# original installation history\n"));
        var preview = context.Coordinator.Preview(selected.BackupId);
        var otherGame = directory.CreateDirectory("other-game");
        TemporaryDirectory.CreateFile(otherGame, "prime.exe");
        var otherConfiguration = Path.Combine(otherGame, "community_patch_settings.toml");
        var otherContents = Encoding.UTF8.GetBytes("# newly selected installation\n");
        await File.WriteAllBytesAsync(otherConfiguration, otherContents);
        context.SelectedPath.Path = otherConfiguration;

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => context.Coordinator.ExecuteAsync(preview, "guffawaffle"));

        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(context.ConfigurationPath));
        CollectionAssert.AreEqual(otherContents, await File.ReadAllBytesAsync(otherConfiguration));
        Assert.IsNull(context.Coordinator.ReadJournal());
    }

    [TestMethod]
    public async Task BackupFromAnotherProviderCannotBePreviewedOrRestored()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        await File.WriteAllBytesAsync(context.ConfigurationPath, Encoding.UTF8.GetBytes("# live\n"));
        var other = await CreateBackupAsync(
            context,
            "netniv",
            Encoding.UTF8.GetBytes("# other provider\n"));

        Assert.ThrowsException<InvalidOperationException>(
            () => context.Coordinator.Preview(other.BackupId));

        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("# live\n"),
            await File.ReadAllBytesAsync(context.ConfigurationPath));
        Assert.IsNull(context.Coordinator.ReadJournal());
    }

    [TestMethod]
    public async Task GlobalMutationLeaseReturnsBusyWithoutBackupOrMutation()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var baseline = Encoding.UTF8.GetBytes("# live\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, baseline);
        var selected = await CreateBackupAsync(
            context,
            "guffawaffle",
            Encoding.UTF8.GetBytes("# history\n"));
        var preview = context.Coordinator.Preview(selected.BackupId);
        await using var lease = await new LauncherOperationLock(context.StateDirectory).TryAcquireAsync();
        Assert.IsNotNull(lease);

        var result = await context.Coordinator.ExecuteAsync(preview, "guffawaffle");

        Assert.AreEqual(ProviderConfigurationRestoreResultState.Busy, result.State);
        CollectionAssert.AreEqual(baseline, await File.ReadAllBytesAsync(context.ConfigurationPath));
        Assert.AreEqual(1, context.Store.List(context.GameDirectory, "guffawaffle").Count);
        Assert.IsNull(context.Coordinator.ReadJournal());
    }

    [TestMethod]
    public async Task RunningSelectedInstallationBlocksRestoreBeforeJournal()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        await File.WriteAllBytesAsync(context.ConfigurationPath, Encoding.UTF8.GetBytes("# live\n"));
        var selected = await CreateBackupAsync(
            context,
            "guffawaffle",
            Encoding.UTF8.GetBytes("# history\n"));
        context.ProcessInspector.State = GameProcessInspectionState.RunningTarget;

        Assert.ThrowsException<InvalidOperationException>(
            () => context.Coordinator.Preview(selected.BackupId));

        Assert.IsNull(context.Coordinator.ReadJournal());
    }

    [TestMethod]
    public async Task ForgedPreviewIsRecomputedAndRejectedBeforeMutation()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var baseline = Encoding.UTF8.GetBytes("# live\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, baseline);
        var selected = await CreateBackupAsync(
            context,
            "guffawaffle",
            Encoding.UTF8.GetBytes("# history\n"));
        var preview = context.Coordinator.Preview(selected.BackupId);
        var forged = preview with
        {
            CompatibilitySummary = "Everything is fine.",
            ConfirmationText = string.Empty,
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => context.Coordinator.ExecuteAsync(forged, string.Empty));

        CollectionAssert.AreEqual(baseline, await File.ReadAllBytesAsync(context.ConfigurationPath));
        Assert.AreEqual(1, context.Store.List(context.GameDirectory, "guffawaffle").Count);
        Assert.IsNull(context.Coordinator.ReadJournal());
    }

    [TestMethod]
    public async Task RecoveryFinishesReceiptWhenSelectedBytesWonBeforeTermination()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var baseline = Encoding.UTF8.GetBytes("# live baseline\n");
        var desired = Encoding.UTF8.GetBytes("# selected history\r\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, baseline);
        var selected = await CreateBackupAsync(context, "guffawaffle", desired);
        var preview = context.Coordinator.Preview(selected.BackupId);
        var preRestore = await CreatePreRestoreBackupAsync(context, preview, baseline);
        await File.WriteAllBytesAsync(context.ConfigurationPath, desired);
        WriteJournal(context, preview, preRestore: null);

        var result = await context.Coordinator.RecoverAsync();

        Assert.AreEqual(ProviderConfigurationRestoreResultState.Succeeded, result.State, result.Message);
        Assert.AreEqual(preRestore.BackupId, result.PreRestoreBackup!.BackupId);
        Assert.IsTrue(result.RestoredBackup!.WasRestored);
        Assert.AreEqual(preview.TransactionId, result.RestoredBackup.RestoreTransactionId);
        Assert.AreEqual(
            ProviderConfigurationRestorePhase.Completed,
            context.Coordinator.ReadJournal()!.Phase);
        CollectionAssert.AreEqual(desired, await File.ReadAllBytesAsync(context.ConfigurationPath));
    }

    [TestMethod]
    public async Task RecoveryClassifiesUntouchedBaselineAsNoChange()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var baseline = Encoding.UTF8.GetBytes("# live baseline\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, baseline);
        var selected = await CreateBackupAsync(
            context,
            "guffawaffle",
            Encoding.UTF8.GetBytes("# selected history\n"));
        var preview = context.Coordinator.Preview(selected.BackupId);
        WriteJournal(context, preview, preRestore: null);

        var result = await context.Coordinator.RecoverAsync();

        Assert.AreEqual(ProviderConfigurationRestoreResultState.NoIncompleteRestore, result.State);
        Assert.AreEqual(
            ProviderConfigurationRestorePhase.Failed,
            context.Coordinator.ReadJournal()!.Phase);
        Assert.IsFalse(context.Store.List(context.GameDirectory, "guffawaffle").Single().WasRestored);
        CollectionAssert.AreEqual(baseline, await File.ReadAllBytesAsync(context.ConfigurationPath));
    }

    [TestMethod]
    public async Task RecoveryPreservesUnclassifiedThirdPartyBytes()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var baseline = Encoding.UTF8.GetBytes("# live baseline\n");
        var thirdParty = Encoding.UTF8.GetBytes("# changed after termination\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, baseline);
        var selected = await CreateBackupAsync(
            context,
            "guffawaffle",
            Encoding.UTF8.GetBytes("# selected history\n"));
        var preview = context.Coordinator.Preview(selected.BackupId);
        WriteJournal(context, preview, preRestore: null);
        await File.WriteAllBytesAsync(context.ConfigurationPath, thirdParty);

        var result = await context.Coordinator.RecoverAsync();

        Assert.AreEqual(ProviderConfigurationRestoreResultState.RecoveryRequired, result.State);
        Assert.AreEqual(
            ProviderConfigurationRestorePhase.RecoveryRequired,
            context.Coordinator.ReadJournal()!.Phase);
        Assert.IsFalse(context.Store.List(context.GameDirectory, "guffawaffle").Single().WasRestored);
        CollectionAssert.AreEqual(thirdParty, await File.ReadAllBytesAsync(context.ConfigurationPath));
    }

    [TestMethod]
    public async Task RecoveryRejectsForgedPreRestoreReceiptWithoutFalseCompletion()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var baseline = Encoding.UTF8.GetBytes("# live baseline\n");
        var desired = Encoding.UTF8.GetBytes("# selected history\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, baseline);
        var selected = await CreateBackupAsync(context, "guffawaffle", desired);
        var preview = context.Coordinator.Preview(selected.BackupId);
        var preRestore = await CreatePreRestoreBackupAsync(context, preview, baseline);
        await File.WriteAllBytesAsync(context.ConfigurationPath, desired);
        WriteJournal(
            context,
            preview,
            preRestore with { BackupId = selected.BackupId });

        var result = await context.Coordinator.RecoverAsync();

        Assert.AreEqual(ProviderConfigurationRestoreResultState.RecoveryRequired, result.State);
        Assert.IsFalse(
            context.Store.List(context.GameDirectory, "guffawaffle")
                .Single(receipt => receipt.BackupId == selected.BackupId)
                .WasRestored);
        Assert.AreEqual(
            ProviderConfigurationRestorePhase.RecoveryRequired,
            context.Coordinator.ReadJournal()!.Phase);
        CollectionAssert.AreEqual(desired, await File.ReadAllBytesAsync(context.ConfigurationPath));
    }

    [TestMethod]
    public async Task RecoveryRejectsChangedSourceReceiptWithoutFalseCompletion()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var baseline = Encoding.UTF8.GetBytes("# live baseline\n");
        var desired = Encoding.UTF8.GetBytes("# selected history\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, baseline);
        var selected = await CreateBackupAsync(context, "guffawaffle", desired);
        var preview = context.Coordinator.Preview(selected.BackupId);
        _ = await CreatePreRestoreBackupAsync(context, preview, baseline);
        await File.WriteAllBytesAsync(context.ConfigurationPath, desired);
        WriteJournal(
            context,
            preview with
            {
                Backup = selected with { Reason = "forged-reason" },
            },
            preRestore: null);

        var result = await context.Coordinator.RecoverAsync();

        Assert.AreEqual(ProviderConfigurationRestoreResultState.RecoveryRequired, result.State);
        Assert.IsFalse(
            context.Store.List(context.GameDirectory, "guffawaffle")
                .Single(receipt => receipt.BackupId == selected.BackupId)
                .WasRestored);
        CollectionAssert.AreEqual(desired, await File.ReadAllBytesAsync(context.ConfigurationPath));
    }

    [TestMethod]
    public async Task RecoveryAcceptsDuplicateExactPreRestoreReceiptsFromSafeRetry()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var baseline = Encoding.UTF8.GetBytes("# live baseline\n");
        var desired = Encoding.UTF8.GetBytes("# selected history\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, baseline);
        var selected = await CreateBackupAsync(context, "guffawaffle", desired);
        var preview = context.Coordinator.Preview(selected.BackupId);
        _ = await CreatePreRestoreBackupAsync(context, preview, baseline);
        _ = await CreatePreRestoreBackupAsync(context, preview, baseline);
        await File.WriteAllBytesAsync(context.ConfigurationPath, desired);
        WriteJournal(context, preview, preRestore: null);

        var result = await context.Coordinator.RecoverAsync();

        Assert.AreEqual(ProviderConfigurationRestoreResultState.Succeeded, result.State, result.Message);
        Assert.IsNotNull(result.PreRestoreBackup);
        CollectionAssert.AreEqual(
            baseline,
            context.Store.Read(
                context.GameDirectory,
                "guffawaffle",
                result.PreRestoreBackup.BackupId));
        Assert.IsTrue(result.RestoredBackup!.WasRestored);
    }

    [TestMethod]
    public async Task RecoveryAcceptsSourceReceiptAlreadyMarkedByInterruptedTransaction()
    {
        using var directory = new TemporaryDirectory();
        var context = CreateContext(directory);
        var baseline = Encoding.UTF8.GetBytes("# live baseline\n");
        var desired = Encoding.UTF8.GetBytes("# selected history\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, baseline);
        var selected = await CreateBackupAsync(context, "guffawaffle", desired);
        var preview = context.Coordinator.Preview(selected.BackupId);
        var preRestore = await CreatePreRestoreBackupAsync(context, preview, baseline);
        await File.WriteAllBytesAsync(context.ConfigurationPath, desired);
        _ = await context.Store.MarkRestoredAsync(
            context.GameDirectory,
            "guffawaffle",
            selected.BackupId,
            preview.TransactionId);
        WriteJournal(
            context,
            preview,
            preRestore,
            ProviderConfigurationRestorePhase.BackupMarkedRestored);

        var result = await context.Coordinator.RecoverAsync();

        Assert.AreEqual(ProviderConfigurationRestoreResultState.Succeeded, result.State, result.Message);
        Assert.AreEqual(preview.TransactionId, result.RestoredBackup!.RestoreTransactionId);
        Assert.AreEqual(
            ProviderConfigurationRestorePhase.Completed,
            context.Coordinator.ReadJournal()!.Phase);
    }

    [DataTestMethod]
    [DataRow("Prepared")]
    [DataRow("ConfigurationCommitted")]
    [DataRow("BackupMarkedRestored")]
    [DataRow("Completed")]
    public async Task HardCrashAtEveryRestoreBoundaryRecoversWithoutFalseResult(string crashStage)
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
            var stateDirectory = Path.Combine(directory.Path, "state");
            await using var competingLease = await new LauncherOperationLock(stateDirectory)
                .TryAcquireAsync();
            Assert.IsNull(
                competingLease,
                $"Restore stage '{crashStage}' released the root mutation lease before its terminal boundary.");
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

            var context = CreateContext(directory.Path);
            var result = await context.Coordinator.RecoverAsync();
            var committed = crashStage != "Prepared";
            Assert.AreEqual(
                committed
                    ? crashStage == "Completed"
                        ? ProviderConfigurationRestoreResultState.NoIncompleteRestore
                        : ProviderConfigurationRestoreResultState.Succeeded
                    : ProviderConfigurationRestoreResultState.NoIncompleteRestore,
                result.State,
                result.Message);
            CollectionAssert.AreEqual(
                Encoding.UTF8.GetBytes(committed ? "# selected history\r\n" : "# live baseline\n"),
                await File.ReadAllBytesAsync(context.ConfigurationPath));
            var history = context.Store.List(context.GameDirectory, "guffawaffle");
            Assert.AreEqual(ProviderScopedConfigurationBackupStore.DefaultRetentionCount, history.Count);
            var desiredRevision = ConfigurationDocumentRevision.FromContents(
                Encoding.UTF8.GetBytes("# selected history\r\n"));
            var selected = history.Single(receipt => string.Equals(
                receipt.ContentSha256,
                desiredRevision.Sha256,
                StringComparison.Ordinal));
            Assert.AreEqual(committed, selected.WasRestored);
            if (committed)
            {
                var preRestore = history
                    .Single(receipt => string.Equals(receipt.Reason, "manual-restore", StringComparison.Ordinal));
                CollectionAssert.AreEqual(
                    Encoding.UTF8.GetBytes("# live baseline\n"),
                    context.Store.Read(
                        context.GameDirectory,
                        "guffawaffle",
                        preRestore.BackupId));
            }
            Assert.AreEqual(
                committed
                    ? ProviderConfigurationRestorePhase.Completed
                    : ProviderConfigurationRestorePhase.Failed,
                context.Coordinator.ReadJournal()!.Phase);
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
    public async Task ProviderConfigurationRestoreHardCrashProbe()
    {
        var configuredStage = Environment.GetEnvironmentVariable(CrashStageEnvironment);
        if (string.IsNullOrWhiteSpace(configuredStage))
        {
            return;
        }
        var crashStage = Enum.Parse<ProviderConfigurationRestorePhase>(configuredStage);
        var root = Environment.GetEnvironmentVariable(CrashRootEnvironment)
            ?? throw new InvalidOperationException("The configuration-restore crash root is absent.");
        var readyPath = Environment.GetEnvironmentVariable(CrashReadyEnvironment)
            ?? throw new InvalidOperationException("The configuration-restore crash ready path is absent.");
        async ValueTask Checkpoint(
            ProviderConfigurationRestorePhase current,
            CancellationToken cancellationToken)
        {
            if (current != crashStage)
            {
                return;
            }
            await File.WriteAllTextAsync(readyPath, current.ToString(), cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        var context = CreateContext(root, Checkpoint);
        var baseline = Encoding.UTF8.GetBytes("# live baseline\n");
        var desired = Encoding.UTF8.GetBytes("# selected history\r\n");
        await File.WriteAllBytesAsync(context.ConfigurationPath, baseline);
        var selected = await CreateBackupAsync(context, "guffawaffle", desired);
        for (var index = 1; index < ProviderScopedConfigurationBackupStore.DefaultRetentionCount; index++)
        {
            _ = await CreateBackupAsync(
                context,
                "guffawaffle",
                Encoding.UTF8.GetBytes($"# alternate history {index}\n"));
        }
        var preview = context.Coordinator.Preview(selected.BackupId);
        _ = await context.Coordinator.ExecuteAsync(preview, "guffawaffle");
        Assert.Fail($"Configuration-restore crash probe passed stage '{configuredStage}'.");
    }

    private static RestoreContext CreateContext(TemporaryDirectory directory)
        => CreateContext(directory.Path);

    private static RestoreContext CreateContext(
        string root,
        Func<ProviderConfigurationRestorePhase, CancellationToken, ValueTask>? checkpoint = null)
    {
        var stateDirectory = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        var gameDirectory = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var catalog = LauncherDistributionProviderTests.LoadFixtureCatalog();
        var selection = new LauncherProviderSelection("guffawaffle", "stable");
        var selectionStore = new JsonLauncherProviderSelectionStore(stateDirectory);
        selectionStore.Save(selection);
        var schema = LauncherConfigurationSchemaLoader.LoadFile(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Configuration",
            "config-schema.guffawaffle.v1.json"));
        var evidence = LauncherConfigurationDiagnosisEvidence.Supported(
            selection.ProviderId,
            selection.ReleaseChannelId,
            schema);
        var store = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity());
        var inspector = new MutableProcessInspector();
        var selectedPath = new MutablePathProvider { Path = configurationPath };
        var coordinator = new ProviderConfigurationRestoreCoordinator(
            store,
            catalog,
            selectionStore,
            selection,
            evidence,
            stateDirectory,
            () => selectedPath.Path,
            inspector,
            timeProvider: null,
            checkpoint);
        return new(
            stateDirectory,
            gameDirectory,
            configurationPath,
            store,
            coordinator,
            inspector,
            selectedPath);
    }

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
        start.ArgumentList.Add(typeof(ProviderConfigurationRestoreCoordinatorTests).Assembly.Location);
        start.ArgumentList.Add(
            "--Tests:STFCCommunityMod.Launcher.Core.Tests."
            + "ProviderConfigurationRestoreCoordinatorTests.ProviderConfigurationRestoreHardCrashProbe");
        start.Environment[CrashStageEnvironment] = crashStage;
        start.Environment[CrashRootEnvironment] = root;
        start.Environment[CrashReadyEnvironment] = readyPath;
        return Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the configuration-restore crash probe.");
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
                    $"Configuration-restore crash probe exited before its hold point. "
                    + $"Output: {output} Error: {error}");
            }
            await Task.Delay(50, timeout.Token);
        }
    }

    private static Task<ConfigurationBackupReceipt> CreateBackupAsync(
        RestoreContext context,
        string providerId,
        byte[] contents) =>
        context.Store.CreateAsync(new(
            context.GameDirectory,
            providerId,
            context.ConfigurationPath,
            contents,
            "settings-save",
            ReleaseIdentity: $"{providerId}/stable"));

    private static Task<ConfigurationBackupReceipt> CreatePreRestoreBackupAsync(
        RestoreContext context,
        ProviderConfigurationRestorePreview preview,
        byte[] contents) =>
        context.Store.CreateAsync(new(
            context.GameDirectory,
            preview.Selection.ProviderId,
            context.ConfigurationPath,
            contents,
            "manual-restore",
            ReleaseIdentity: $"configuration-history-restore/{preview.TransactionId}",
            PinnedBackupId: preview.Backup.BackupId));

    private static void WriteJournal(
        RestoreContext context,
        ProviderConfigurationRestorePreview preview,
        ConfigurationBackupReceipt? preRestore,
        ProviderConfigurationRestorePhase phase = ProviderConfigurationRestorePhase.Prepared)
    {
        var journal = new ProviderConfigurationRestoreJournal(
            1,
            phase,
            preview,
            preRestore,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(context.StateDirectory, "configuration-restore-journal.json"),
            JsonSerializer.Serialize(journal, JournalJsonOptions));
    }

    private sealed record RestoreContext(
        string StateDirectory,
        string GameDirectory,
        string ConfigurationPath,
        ProviderScopedConfigurationBackupStore Store,
        ProviderConfigurationRestoreCoordinator Coordinator,
        MutableProcessInspector ProcessInspector,
        MutablePathProvider SelectedPath);

    private sealed class MutablePathProvider
    {
        public string? Path { get; set; }
    }

    private sealed class MutableProcessInspector : IGameProcessInspector
    {
        public GameProcessInspectionState State { get; set; } = GameProcessInspectionState.NotRunning;

        public GameProcessInspectionState Inspect(string gameDirectory) => State;
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
}
