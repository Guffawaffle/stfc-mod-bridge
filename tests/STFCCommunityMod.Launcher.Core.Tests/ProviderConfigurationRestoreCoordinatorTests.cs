using System.Text;
using System.Text.Json;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ProviderConfigurationRestoreCoordinatorTests
{
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

    private static RestoreContext CreateContext(TemporaryDirectory directory)
    {
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = directory.CreateDirectory("game");
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
            inspector);
        return new(
            stateDirectory,
            gameDirectory,
            configurationPath,
            store,
            coordinator,
            inspector,
            selectedPath);
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
