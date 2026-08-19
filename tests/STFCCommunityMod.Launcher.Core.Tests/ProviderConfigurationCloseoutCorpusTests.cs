using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ProviderConfigurationCloseoutCorpusTests
{
    [TestMethod]
    public async Task PublicBackupStoreRetainsNewestFiveExactSavesForEachProvider()
    {
        using var directory = new TemporaryDirectory();
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        var store = CreateBackupStore(stateDirectory, new IncrementingTimeProvider());
        var expected = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
        var receiptIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var providerId in new[] { "guffawaffle", "netniv" })
        {
            var saves = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            expected.Add(providerId, saves);
            var providerReceiptIds = new List<string>();
            receiptIds.Add(providerId, providerReceiptIds);
            for (var index = 0; index < 6; index++)
            {
                var contents = ConfigurationCorpus(providerId, index);
                var receipt = await store.CreateAsync(new(
                    gameDirectory,
                    providerId,
                    configurationPath,
                    contents,
                    "closeout-save"));
                saves.Add(receipt.BackupId, contents);
                providerReceiptIds.Add(receipt.BackupId);
            }
        }

        foreach (var providerId in expected.Keys)
        {
            var retained = store.List(gameDirectory, providerId);
            Assert.AreEqual(5, retained.Count, providerId);
            Assert.IsTrue(retained.All(receipt => receipt.ProviderId == providerId));

            var prunedId = receiptIds[providerId][0];
            Assert.IsFalse(retained.Any(receipt => receipt.BackupId == prunedId));
            CollectionAssert.AreEqual(
                receiptIds[providerId].Skip(1).Reverse().ToArray(),
                retained.Select(receipt => receipt.BackupId).ToArray(),
                $"{providerId} history was not returned newest-first.");
            foreach (var receipt in retained)
            {
                CollectionAssert.AreEqual(
                    expected[providerId][receipt.BackupId],
                    store.Read(gameDirectory, providerId, receipt.BackupId),
                    $"{providerId}/{receipt.BackupId} did not round-trip byte-exactly.");
            }
        }
    }

    [TestMethod]
    public async Task PublicSwitchBoundaryAcceptsExpectedMissingConfigurationWithoutCreatingIt()
    {
        using var context = await CreateSwitchContextAsync(configurationContents: null);

        var preview = await context.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            context.GameDirectory,
            isGameRunning: false,
            context.ConfigurationPath);
        var result = await context.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.AreEqual(false, preview.Configuration.ConfigurationExisted);
        Assert.AreEqual(LauncherProviderSwitchConfigurationKind.None, preview.Configuration.ConfigurationKind);
        Assert.IsNotNull(preview.Artifact);
        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), result.Selection);
        Assert.AreEqual("netniv", result.InstalledArtifact!.ProviderId);
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.Completed,
            context.Coordinator.ReadJournal()!.Phase);
        Assert.IsFalse(File.Exists(context.ConfigurationPath));
        Assert.AreEqual(0, context.BackupStore.List(context.GameDirectory, "guffawaffle").Count);
    }

    [TestMethod]
    public async Task PublicSwitchBoundaryBlocksParserInvalidConfigurationBeforeMutation()
    {
        using var context = await CreateSwitchContextAsync(
            "[graphics]\nfree_resize = true\nfree_resize = false\n"u8.ToArray());

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => context.Coordinator.PreviewAsync(
                "netniv",
                "stable",
                context.GameDirectory,
                isGameRunning: false,
                context.ConfigurationPath));

        StringAssert.Contains(exception.Message, "conservative TOML parser");
        context.AssertUnchanged();
    }

    [TestMethod]
    public async Task PublicSwitchBoundaryWarnsAndPreservesCatalogInvalidConfiguration()
    {
        var original = "[graphics]\nfree_resize = \"not-a-boolean\"\n"u8.ToArray();
        using var context = await CreateSwitchContextAsync(original);

        var preview = await context.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            context.GameDirectory,
            isGameRunning: false,
            context.ConfigurationPath);
        var result = await context.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.IsTrue(preview.Configuration.Concerns.Any(concern =>
            concern.Kind == LauncherProviderCompatibilityKind.Warning
            && concern.Message.Contains("ignore invalid overrides", StringComparison.Ordinal)));
        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), result.Selection);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(context.ConfigurationPath));
        Assert.AreEqual(1, context.BackupStore.List(context.GameDirectory, "guffawaffle").Count);
    }

    [TestMethod]
    public async Task PublicSwitchBoundaryRejectsStaleReviewedRevisionBeforeMutation()
    {
        using var context = await CreateSwitchContextAsync("[graphics]\nfree_resize = true\n"u8.ToArray());
        var preview = await context.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            context.GameDirectory,
            isGameRunning: false,
            context.ConfigurationPath);
        var external = "# external writer\r\n[graphics]\r\nfree_resize = false\r\n"u8.ToArray();
        await File.WriteAllBytesAsync(context.ConfigurationPath, external);

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => context.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        StringAssert.Contains(exception.Message, "Review the provider switch again");
        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(context.ConfigurationPath));
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), context.SelectionStore.Load());
        Assert.AreEqual(0, context.BackupStore.List(context.GameDirectory, "guffawaffle").Count);
        Assert.IsNull(context.Coordinator.ReadJournal());
        CollectionAssert.AreEqual(
            context.SourceArtifactContents,
            File.ReadAllBytes(Path.Combine(context.GameDirectory, "version.dll")));
    }

    [TestMethod]
    public async Task PublicSwitchBoundaryBlocksConflictingCanonicalAndAliasValuesBeforeMutation()
    {
        using var context = await CreateSwitchContextAsync(
            ConflictingConfiguration(),
            sourceProviderId: "netniv");

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => context.Coordinator.PreviewAsync(
                "guffawaffle",
                "stable",
                context.GameDirectory,
                isGameRunning: false,
                context.ConfigurationPath));

        StringAssert.Contains(exception.Message, "CONFIG_CANONICAL_ALIAS_CONFLICT");
        context.AssertUnchanged();
    }

    [TestMethod]
    public async Task PublicSwitchBoundaryWarnsAndPreservesUnsupportedContentByteExactly()
    {
        var unsupported = UnsupportedConfiguration();
        using var context = await CreateSwitchContextAsync(unsupported);

        var preview = await context.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            context.GameDirectory,
            isGameRunning: false,
            context.ConfigurationPath);
        var result = await context.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.AreEqual(
            LauncherProviderSwitchConfigurationKind.PreserveCurrent,
            preview.Configuration.ConfigurationKind);
        Assert.IsTrue(
            preview.Configuration.TargetConfigurationAnalysis!.FindingCounts
                .GetValueOrDefault("CONFIG_UNKNOWN_KEY") > 0);
        Assert.IsTrue(preview.Configuration.Concerns.Any(concern =>
            concern.Kind == LauncherProviderCompatibilityKind.Warning));
        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), result.Selection);
        Assert.AreEqual("netniv", result.InstalledArtifact!.ProviderId);
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.Completed,
            context.Coordinator.ReadJournal()!.Phase);
        CollectionAssert.AreEqual(unsupported, await File.ReadAllBytesAsync(context.ConfigurationPath));
        var sourceBackup = context.BackupStore.List(context.GameDirectory, "guffawaffle").Single();
        CollectionAssert.AreEqual(
            unsupported,
            context.BackupStore.Read(context.GameDirectory, "guffawaffle", sourceBackup.BackupId));
    }

    [TestMethod]
    public async Task PublicSwitchBoundaryWarnsAndPreservesWhenTargetCatalogIsUnsupported()
    {
        var contents = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var exactEvidence = ExactConfigurationEvidence();
        using var context = await CreateSwitchContextAsync(
            contents,
            configurationEvidence: selection => selection.ProviderId == "netniv"
                ? LauncherConfigurationDiagnosisEvidence.Unavailable(
                    selection.ProviderId,
                    selection.ReleaseChannelId,
                    LauncherProviderCapabilityStatus.Unsupported)
                : exactEvidence(selection));

        var preview = await context.Coordinator.PreviewAsync(
            "netniv",
            "stable",
            context.GameDirectory,
            isGameRunning: false,
            context.ConfigurationPath);
        var result = await context.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.AreEqual(
            LauncherProviderCapabilityStatus.Unsupported,
            preview.Configuration.TargetConfigurationAnalysis!.CatalogStatus);
        Assert.IsTrue(preview.Configuration.Concerns.Any(concern =>
            concern.Kind == LauncherProviderCompatibilityKind.Warning
            && concern.Message.Contains("No exact", StringComparison.Ordinal)));
        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), result.Selection);
        Assert.AreEqual("netniv", result.InstalledArtifact!.ProviderId);
        Assert.AreEqual(
            LauncherProviderAtomicSwitchPhase.Completed,
            context.Coordinator.ReadJournal()!.Phase);
        CollectionAssert.AreEqual(contents, await File.ReadAllBytesAsync(context.ConfigurationPath));
    }

    [TestMethod]
    public void PublicRestoreBoundaryBlocksMissingLiveConfigurationBeforeJournalMutation()
    {
        using var context = CreateRestoreContext(liveContents: null);
        var source = context.CreateBackup("[graphics]\nfree_resize = false\n"u8.ToArray());

        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => context.Coordinator.Preview(source.BackupId));

        StringAssert.Contains(exception.Message, "No active TOML");
        Assert.IsNull(context.Coordinator.ReadJournal());
        Assert.IsFalse(File.Exists(context.ConfigurationPath));
    }

    [TestMethod]
    public void PublicRestoreBoundaryBlocksParserInvalidHistoryBeforeJournalMutation()
    {
        using var context = CreateRestoreContext("[graphics]\nfree_resize = true\n"u8.ToArray());
        var source = context.CreateBackup(
            "[graphics]\nfree_resize = true\nfree_resize = false\n"u8.ToArray());

        var history = context.Coordinator.LoadHistory().Single();
        Assert.AreEqual(ProviderConfigurationCompatibilityState.Blocked, history.CompatibilityState);
        Assert.IsFalse(history.CanRestore);
        Assert.ThrowsException<InvalidOperationException>(
            () => context.Coordinator.Preview(source.BackupId));
        context.AssertUnchanged();
    }

    [TestMethod]
    public async Task PublicRestoreBoundaryWarnsAndRestoresCatalogInvalidHistoryByteExactly()
    {
        using var context = CreateRestoreContext("[graphics]\nfree_resize = true\n"u8.ToArray());
        var desired = "[graphics]\nfree_resize = \"not-a-boolean\"\n"u8.ToArray();
        var source = context.CreateBackup(desired);

        var history = context.Coordinator.LoadHistory().Single();
        Assert.AreEqual(ProviderConfigurationCompatibilityState.Attention, history.CompatibilityState);
        Assert.IsTrue(history.CanRestore);
        var preview = context.Coordinator.Preview(source.BackupId);
        var result = await context.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.AreEqual(ProviderConfigurationRestoreResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(desired, await File.ReadAllBytesAsync(context.ConfigurationPath));
    }

    [TestMethod]
    public async Task PublicRestoreBoundaryRejectsStaleReviewedRevisionBeforeJournalMutation()
    {
        using var context = CreateRestoreContext("[graphics]\nfree_resize = true\n"u8.ToArray());
        var source = context.CreateBackup("[graphics]\nfree_resize = false\n"u8.ToArray());
        var preview = context.Coordinator.Preview(source.BackupId);
        var external = "# external writer\r\n[graphics]\r\nfree_resize = true\r\n"u8.ToArray();
        await File.WriteAllBytesAsync(context.ConfigurationPath, external);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => context.Coordinator.ExecuteAsync(preview, preview.ConfirmationText));

        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(context.ConfigurationPath));
        Assert.IsNull(context.Coordinator.ReadJournal());
        Assert.AreEqual(1, context.BackupStore.List(context.GameDirectory, "guffawaffle").Count);
    }

    [TestMethod]
    public void PublicRestoreBoundaryBlocksConflictingHistoryBeforeJournalMutation()
    {
        using var context = CreateRestoreContext("[graphics]\nfree_resize = true\n"u8.ToArray());
        var source = context.CreateBackup(ConflictingConfiguration());

        var history = context.Coordinator.LoadHistory().Single();
        Assert.AreEqual(ProviderConfigurationCompatibilityState.Blocked, history.CompatibilityState);
        StringAssert.Contains(history.CompatibilitySummary, "blocking compatibility");
        Assert.ThrowsException<InvalidOperationException>(
            () => context.Coordinator.Preview(source.BackupId));
        context.AssertUnchanged();
    }

    [TestMethod]
    public async Task PublicRestoreBoundaryWarnsAndRestoresUnsupportedContentByteExactly()
    {
        var live = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var unsupported = UnsupportedConfiguration();
        using var context = CreateRestoreContext(live);
        var source = context.CreateBackup(unsupported);

        var history = context.Coordinator.LoadHistory().Single();
        Assert.AreEqual(ProviderConfigurationCompatibilityState.Attention, history.CompatibilityState);
        Assert.IsTrue(history.CanRestore);
        var preview = context.Coordinator.Preview(source.BackupId);
        var result = await context.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.AreEqual(ProviderConfigurationRestoreResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(unsupported, await File.ReadAllBytesAsync(context.ConfigurationPath));
        CollectionAssert.AreEqual(
            live,
            context.BackupStore.Read(
                context.GameDirectory,
                "guffawaffle",
                result.PreRestoreBackup!.BackupId));
    }

    [TestMethod]
    public async Task PublicRestoreBoundaryWarnsAndRestoresWhenCatalogIsUnsupported()
    {
        var live = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var desired = "# exact bytes under unsupported evidence\r\n[graphics]\r\nfree_resize = false\r\n"u8.ToArray();
        var unsupportedEvidence = LauncherConfigurationDiagnosisEvidence.Unavailable(
            "guffawaffle",
            "stable",
            LauncherProviderCapabilityStatus.Unsupported);
        using var context = CreateRestoreContext(live, unsupportedEvidence);
        var source = context.CreateBackup(desired);

        var history = context.Coordinator.LoadHistory().Single();
        Assert.AreEqual(ProviderConfigurationCompatibilityState.Unknown, history.CompatibilityState);
        Assert.IsTrue(history.CanRestore);
        StringAssert.Contains(history.CompatibilitySummary, "evidence is unavailable");
        var preview = context.Coordinator.Preview(source.BackupId);
        var result = await context.Coordinator.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.AreEqual(ProviderConfigurationRestoreResultState.Succeeded, result.State, result.Message);
        CollectionAssert.AreEqual(desired, await File.ReadAllBytesAsync(context.ConfigurationPath));
    }

    private static async Task<SwitchContext> CreateSwitchContextAsync(
        byte[]? configurationContents,
        string sourceProviderId = "guffawaffle",
        Func<LauncherProviderSelection, LauncherConfigurationDiagnosisEvidence>?
            configurationEvidence = null)
    {
        var directory = new TemporaryDirectory();
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        if (configurationContents is not null)
        {
            File.WriteAllBytes(configurationPath, configurationContents);
        }
        var selectionStore = new JsonLauncherProviderSelectionStore(stateDirectory);
        selectionStore.Save(new(sourceProviderId, "stable"));
        var catalog = LauncherDistributionProviderTests.LoadFixtureCatalog();
        var service = new LauncherProviderSourceSwitchService(
            catalog,
            selectionStore,
            stateDirectory,
            configurationEvidence ?? ExactConfigurationEvidence());
        var guffawaffleArtifact = Artifact("guffawaffle-artifact"u8.ToArray(), "2.1.0.8");
        var netnivArtifact = Artifact("netniv-artifact"u8.ToArray(), "1.1.5.1");
        var guffawaffleDeployment = Deployment(
            stateDirectory,
            "guffawaffle-artifact"u8.ToArray(),
            guffawaffleArtifact.ExpectedVersion,
            new("guffawaffle", "stable", "guffawaffle.windows"));
        var netnivDeployment = Deployment(
            stateDirectory,
            "netniv-artifact"u8.ToArray(),
            netnivArtifact.ExpectedVersion,
            new("netniv", "stable", "netniv.stfc-community-mod"));
        var sourceDeployment = sourceProviderId == "guffawaffle"
            ? guffawaffleDeployment
            : netnivDeployment;
        var sourceArtifact = sourceProviderId == "guffawaffle"
            ? guffawaffleArtifact
            : netnivArtifact;
        var installation = await sourceDeployment.DeployAsync(
            gameDirectory,
            sourceArtifact,
            ExistingArtifactPolicy.Reject);
        Assert.AreEqual(ModDeploymentResultState.Succeeded, installation.State, installation.Message);
        var coordinator = new LauncherProviderAtomicSwitchCoordinator(
            service,
            [
                new(
                    "guffawaffle",
                    Management(
                        guffawaffleDeployment,
                        guffawaffleArtifact,
                        "guffawaffle",
                        "guffawaffle.windows")),
                new(
                    "netniv",
                    Management(
                        netnivDeployment,
                        netnivArtifact,
                        "netniv",
                        "netniv.stfc-community-mod")),
            ],
            stateDirectory);
        return new(
            directory,
            stateDirectory,
            gameDirectory,
            configurationPath,
            configurationContents,
            sourceProviderId,
            sourceProviderId == "guffawaffle"
                ? "guffawaffle-artifact"u8.ToArray()
                : "netniv-artifact"u8.ToArray(),
            selectionStore,
            coordinator,
            new ProviderScopedConfigurationBackupStore(stateDirectory));
    }

    private static RestoreContext CreateRestoreContext(
        byte[]? liveContents,
        LauncherConfigurationDiagnosisEvidence? diagnosisEvidence = null)
    {
        var directory = new TemporaryDirectory();
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = CreateGameDirectory(directory);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        if (liveContents is not null)
        {
            File.WriteAllBytes(configurationPath, liveContents);
        }
        var selection = new LauncherProviderSelection("guffawaffle", "stable");
        var selectionStore = new JsonLauncherProviderSelectionStore(stateDirectory);
        selectionStore.Save(selection);
        var backupStore = CreateBackupStore(stateDirectory);
        var coordinator = new ProviderConfigurationRestoreCoordinator(
            backupStore,
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            selectionStore,
            selection,
            diagnosisEvidence ?? ExactConfigurationEvidence()(selection),
            stateDirectory,
            () => configurationPath,
            new NotRunningProcessInspector());
        return new(
            directory,
            stateDirectory,
            gameDirectory,
            configurationPath,
            liveContents,
            backupStore,
            coordinator);
    }

    private static Func<LauncherProviderSelection, LauncherConfigurationDiagnosisEvidence>
        ExactConfigurationEvidence()
    {
        var guffawaffleCatalog = LauncherConfigurationSchemaLoader.LoadFile(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "Configuration",
                "config-schema.guffawaffle.v1.json"));
        using var netnivSchema = File.OpenRead(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "Configuration",
                "configuration-schema-set.netniv.v1.json"));
        var netnivCatalog = LauncherConfigurationSchemaSetLoader.Load(
            netnivSchema,
            new(
                "netniv",
                "stable",
                "1.1.4",
                "d912611fa1eca49fc54f363bdf8377dfebf8def0"));
        return selection => selection.ProviderId switch
        {
            "guffawaffle" => LauncherConfigurationDiagnosisEvidence.Supported(
                selection.ProviderId,
                selection.ReleaseChannelId,
                guffawaffleCatalog),
            "netniv" => LauncherConfigurationDiagnosisEvidence.Supported(
                selection.ProviderId,
                selection.ReleaseChannelId,
                netnivCatalog),
            _ => LauncherConfigurationDiagnosisEvidence.Unavailable(
                selection.ProviderId,
                selection.ReleaseChannelId,
                LauncherProviderCapabilityStatus.Unknown),
        };
    }

    private static ProviderScopedConfigurationBackupStore CreateBackupStore(
        string stateDirectory,
        TimeProvider? timeProvider = null) =>
        new(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity(),
            timeProvider);

    private static ModManagementCoordinator Management(
        ModDeploymentService deployment,
        ModReleaseArtifact artifact,
        string providerId,
        string runtimeDistributionId) =>
        new(
            deployment,
            new FakeReleaseDiscoveryClient(artifact),
            new Version(0, 1, 0),
            healthService: new LauncherHealthService(
                new ModInstallationInspector(
                    deployment,
                    new SystemModInstallationFileSystem()),
                new(
                    providerId,
                    "stable",
                    runtimeDistributionId,
                    CanMutate: true,
                    UnavailableReason: string.Empty)));

    private static ModDeploymentService Deployment(
        string stateDirectory,
        byte[] contents,
        string version,
        ModInstallationAttribution attribution) =>
        new(
            stateDirectory,
            new FakeDownloader(contents),
            new FakeVersionReader(version),
            new FakeAuthenticityVerifier(),
            _ => false,
            attribution);

    private static ModReleaseArtifact Artifact(byte[] contents, string version) =>
        new(
            new Uri("https://example.invalid/version.dll"),
            "version.dll",
            contents.LongLength,
            Convert.ToHexString(SHA256.HashData(contents)),
            version,
            ExpectedProductVersion: ProductVersion(version));

    private static string ProductVersion(string version) => version switch
    {
        "2.1.0.8" => "v2.1.0-guffa.8",
        "2.1.0.9" => "v2.1.0-guffa.9",
        "1.1.5.1" => "v1.1.5",
        _ => $"v{version}",
    };

    private static string CreateGameDirectory(TemporaryDirectory directory)
    {
        var gameDirectory = directory.CreateDirectory("game");
        TemporaryDirectory.CreateFile(gameDirectory, "prime.exe");
        return gameDirectory;
    }

    private static byte[] ConfigurationCorpus(string providerId, int index)
    {
        var newline = index % 2 == 0 ? "\r\n" : "\n";
        return Encoding.UTF8.GetBytes(
            $"# {providerId} save {index}: café/艦隊{newline}"
            + $"[future_{index}]{newline}"
            + $"private_endpoint = \"https://private.invalid/{providerId}/{index}?token=secret-{index}\"{newline}"
            + $"ordered = [3, 1, 2]   # preserve spacing{newline}");
    }

    private static byte[] ConflictingConfiguration() =>
        Encoding.UTF8.GetBytes(
            "input.bindings.hotkeys_disable = \"CTRL-ALT-MINUS\"\n"
            + "shortcuts.set_hotkeys_disable = \"CTRL-ALT-PLUS\"\n");

    private static byte[] UnsupportedConfiguration() =>
        Encoding.UTF8.GetBytes(
            "# preserve unknown content byte-for-byte — café/艦隊\r\n"
            + "[future.provider]\r\n"
            + "private_endpoint = \"https://private.invalid/token-secret\"   # keep spacing\r\n");

    private sealed record SwitchContext(
        TemporaryDirectory Directory,
        string StateDirectory,
        string GameDirectory,
        string ConfigurationPath,
        byte[]? OriginalContents,
        string SourceProviderId,
        byte[] SourceArtifactContents,
        JsonLauncherProviderSelectionStore SelectionStore,
        LauncherProviderAtomicSwitchCoordinator Coordinator,
        ProviderScopedConfigurationBackupStore BackupStore) : IDisposable
    {
        public void AssertUnchanged()
        {
            Assert.AreEqual(new LauncherProviderSelection(SourceProviderId, "stable"), SelectionStore.Load());
            Assert.AreEqual(0, BackupStore.List(GameDirectory, SourceProviderId).Count);
            Assert.IsNull(Coordinator.ReadJournal());
            CollectionAssert.AreEqual(
                SourceArtifactContents,
                File.ReadAllBytes(Path.Combine(GameDirectory, "version.dll")));
            if (OriginalContents is null)
            {
                Assert.IsFalse(File.Exists(ConfigurationPath));
            }
            else
            {
                CollectionAssert.AreEqual(OriginalContents, File.ReadAllBytes(ConfigurationPath));
            }
        }

        public void Dispose() => Directory.Dispose();
    }

    private sealed record RestoreContext(
        TemporaryDirectory Directory,
        string StateDirectory,
        string GameDirectory,
        string ConfigurationPath,
        byte[]? OriginalContents,
        ProviderScopedConfigurationBackupStore BackupStore,
        ProviderConfigurationRestoreCoordinator Coordinator) : IDisposable
    {
        public ConfigurationBackupReceipt CreateBackup(byte[] contents) =>
            BackupStore.CreateAsync(new(
                    GameDirectory,
                    "guffawaffle",
                    ConfigurationPath,
                    contents,
                    "closeout-seed"))
                .GetAwaiter()
                .GetResult();

        public void AssertUnchanged()
        {
            Assert.IsNull(Coordinator.ReadJournal());
            if (OriginalContents is null)
            {
                Assert.IsFalse(File.Exists(ConfigurationPath));
            }
            else
            {
                CollectionAssert.AreEqual(OriginalContents, File.ReadAllBytes(ConfigurationPath));
            }
        }

        public void Dispose() => Directory.Dispose();
    }

    private sealed class ReversingProtector : IConfigurationBackupProtector
    {
        public string SchemeId => "closeout-reverse-v1";

        public byte[] Protect(byte[] contents) => [.. contents.Reverse()];

        public byte[] Unprotect(byte[] protectedContents) => [.. protectedContents.Reverse()];
    }

    private sealed class NoOpStorageSecurity : IConfigurationBackupStorageSecurity
    {
        public void SecureDirectory(string directory) => Directory.CreateDirectory(directory);
    }

    private sealed class IncrementingTimeProvider : TimeProvider
    {
        private long ticks;

        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero)
                .AddTicks(Interlocked.Increment(ref ticks));
    }

    private sealed class NotRunningProcessInspector : IGameProcessInspector
    {
        public GameProcessInspectionState Inspect(string gameDirectory) =>
            GameProcessInspectionState.NotRunning;
    }

    private sealed class FakeReleaseDiscoveryClient(ModReleaseArtifact artifact)
        : IWindowsReleaseDiscoveryClient
    {
        public Task<WindowsReleaseDiscovery> DiscoverLatestAsync(
            string channel,
            Version currentLauncherVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsReleaseDiscovery(
                new(
                    1,
                    artifact.ExpectedVersion,
                    $"v{artifact.ExpectedVersion}",
                    channel,
                    "active",
                    currentLauncherVersion,
                    new("example/repository", new string('0', 40)),
                    "none",
                    []),
                artifact));
    }

    private sealed class FakeDownloader(byte[] contents) : IModArtifactDownloader
    {
        public Task<ModArtifactDownload> DownloadAsync(Uri uri, CancellationToken cancellationToken) =>
            Task.FromResult(new ModArtifactDownload(HttpStatusCode.OK, contents, contents.LongLength));
    }

    private sealed class FakeVersionReader(string version) : IModArtifactProductVersionReader
    {
        public string? ReadVersion(string artifactPath) => version;

        public string? ReadProductVersion(string artifactPath) => ProductVersion(version);
    }

    private sealed class FakeAuthenticityVerifier : IModArtifactAuthenticityVerifier
    {
        public ModArtifactAuthenticityResult Verify(string artifactPath) =>
            new(true, "trusted closeout fixture artifact");
    }
}
