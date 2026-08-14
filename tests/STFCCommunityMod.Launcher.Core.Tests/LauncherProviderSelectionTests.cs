using System.Text;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherProviderSelectionTests
{
    [TestMethod]
    public void SelectionRoundTripsInLauncherStateWithoutTouchingToml()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = Path.Combine(directory.Path, "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes("# keep me\n[unknown.future]\nvalue = 'exact'\n");
        File.WriteAllBytes(configurationPath, original);
        var store = new JsonLauncherProviderSelectionStore(directory.Path);

        store.Save(new("netniv", "stable"));

        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), store.Load());
        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        Assert.IsTrue(File.Exists(Path.Combine(directory.Path, "provider-selection.json")));
    }

    [TestMethod]
    public void UnknownPersistedProviderDoesNotFallBackToDefault()
    {
        var catalog = LauncherDistributionProviderTests.LoadFixtureCatalog();

        var resolution = LauncherProviderSelectionResolver.Resolve(
            catalog,
            new("removed-provider", "stable"));

        Assert.AreEqual(LauncherProviderSelectionResolutionState.UnknownProvider, resolution.State);
        Assert.IsFalse(resolution.IsResolved);
        Assert.IsNull(resolution.Provider);
        StringAssert.Contains(resolution.Message, "not present");
    }

    [TestMethod]
    public void UnresolvedSelectionRestrictsProviderActionsButKeepsRecoveryAvailable()
    {
        var resolution = LauncherProviderSelectionResolver.Resolve(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            new("withdrawn-provider", "stable"));

        var access = LauncherProviderShellAccess.From(resolution);

        Assert.IsFalse(access.CanUseProviderBoundModActions);
        Assert.IsFalse(access.CanEditProviderSettings);
        Assert.IsTrue(access.CanChangeProvider);
        StringAssert.Contains(access.RestrictionReason, "withdrawn-provider");
    }

    [DataTestMethod]
    [DataRow("NetniV")]
    [DataRow("netniv ")]
    [DataRow("")]
    public async Task SwitchRequiresExactStableIdConfirmationAndPreviewsOnlyRemainingUnknownCapabilities(
        string incorrectConfirmation)
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("guffawaffle", "stable"));
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            directory.Path,
            ExactConfigurationEvidence());

        var preview = service.Preview("netniv", null, configurationPath);

        Assert.IsTrue(preview.HasUnknownCompatibility);
        Assert.IsTrue(preview.Concerns.Any(concern =>
            concern.CapabilityId == LauncherProviderCapabilityIds.ConfigurationMigration
            && concern.Kind == LauncherProviderCompatibilityKind.Warning
            && concern.Message.Contains("exact bytes", StringComparison.Ordinal)));
        Assert.IsTrue(preview.Concerns.Any(concern =>
            concern.CapabilityId == LauncherProviderCapabilityIds.ConfigurationCatalog
            && concern.Kind == LauncherProviderCompatibilityKind.Compatible
            && concern.Message.Contains(
                "netniv.configuration.stable-1.1.4",
                StringComparison.Ordinal)));
        Assert.IsNotNull(preview.SourceConfigurationAnalysis);
        Assert.IsNotNull(preview.TargetConfigurationAnalysis);
        Assert.AreEqual(
            preview.ConfigurationSha256,
            preview.TargetConfigurationAnalysis.Binding.Revision.Sha256);
        Assert.AreEqual(
            "netniv.configuration.stable-1.1.4",
            preview.TargetConfigurationAnalysis.CatalogIdentity?.CatalogId);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.ExecuteAsync(preview, incorrectConfirmation));
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), store.Load());
    }

    [TestMethod]
    public void ParserInvalidTargetIsRejectedBeforeBackupOrSelectionMutation()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = Path.Combine(directory.Path, "community_patch_settings.toml");
        File.WriteAllText(configurationPath, "[graphics]\nfree_resize = true\nfree_resize = false\n");
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("guffawaffle", "stable"));
        var backupStore = CreateBackupStore(directory.Path);
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            backupStore,
            null,
            ExactConfigurationEvidence());

        var exception = Assert.ThrowsException<InvalidDataException>(
            () => service.Preview("netniv", "stable", configurationPath));

        StringAssert.Contains(exception.Message, "conservative TOML parser");
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), store.Load());
        Assert.AreEqual(0, backupStore.List(directory.Path, "guffawaffle").Count);
    }

    [TestMethod]
    public void CatalogInvalidTargetValueIsRejectedBeforeBackupOrSelectionMutation()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = Path.Combine(directory.Path, "community_patch_settings.toml");
        File.WriteAllText(configurationPath, "[graphics]\nfree_resize = \"not-a-boolean\"\n");
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("guffawaffle", "stable"));
        var backupStore = CreateBackupStore(directory.Path);
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            backupStore,
            null,
            ExactConfigurationEvidence());

        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => service.Preview("netniv", "stable", configurationPath));

        StringAssert.Contains(exception.Message, "CONFIG_VALUE_INVALID");
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), store.Load());
        Assert.AreEqual(0, backupStore.List(directory.Path, "guffawaffle").Count);
    }

    [TestMethod]
    public async Task CatalogEvidenceChangeAfterPreviewAbortsBeforeBackup()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("guffawaffle", "stable"));
        var backupStore = CreateBackupStore(directory.Path);
        var exactEvidence = ExactConfigurationEvidence();
        var targetEvidenceAvailable = true;
        LauncherConfigurationDiagnosisEvidence Resolve(LauncherProviderSelection selection) =>
            selection.ProviderId == "netniv" && !targetEvidenceAvailable
                ? LauncherConfigurationDiagnosisEvidence.Unavailable(
                    selection.ProviderId,
                    selection.ReleaseChannelId,
                    LauncherProviderCapabilityStatus.Unknown)
                : exactEvidence(selection);
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            backupStore,
            null,
            Resolve);
        var preview = service.Preview("netniv", "stable", configurationPath);
        targetEvidenceAvailable = false;

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.ExecuteAsync(preview, preview.ConfirmationText));

        StringAssert.Contains(exception.Message, "target configuration catalog");
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), store.Load());
        Assert.AreEqual(0, backupStore.List(directory.Path, "guffawaffle").Count);
    }

    [TestMethod]
    public async Task CatalogEvidenceChangeAfterPreparationAbortsBeforeCommitMutation()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var original = File.ReadAllBytes(configurationPath);
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("guffawaffle", "stable"));
        var backupStore = CreateBackupStore(directory.Path);
        var exactEvidence = ExactConfigurationEvidence();
        var targetEvidenceAvailable = true;
        LauncherConfigurationDiagnosisEvidence Resolve(LauncherProviderSelection selection) =>
            selection.ProviderId == "netniv" && !targetEvidenceAvailable
                ? LauncherConfigurationDiagnosisEvidence.Unavailable(
                    selection.ProviderId,
                    selection.ReleaseChannelId,
                    LauncherProviderCapabilityStatus.Unknown)
                : exactEvidence(selection);
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            backupStore,
            null,
            Resolve);
        var preview = service.Preview("netniv", "stable", configurationPath);
        var prepared = await service.PrepareAsync(
            preview,
            preview.ConfirmationText,
            CancellationToken.None);
        targetEvidenceAvailable = false;

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.CommitAsync(prepared, CancellationToken.None));

        StringAssert.Contains(exception.Message, "target configuration catalog");
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), store.Load());
        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        Assert.AreEqual(1, backupStore.List(directory.Path, "guffawaffle").Count);
    }

    [TestMethod]
    public void NonDefaultReleaseChannelResolvesWithoutFallingBack()
    {
        var resolution = LauncherProviderSelectionResolver.Resolve(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            new("guffawaffle", "preview"));

        Assert.IsTrue(resolution.IsResolved);
        Assert.AreEqual("preview", resolution.ReleaseChannel?.Id);
        Assert.AreEqual("Guffawaffle/stfc-mod", resolution.ReleaseChannel?.Repository);
    }

    [TestMethod]
    public async Task ConfirmedSwitchBacksUpBytesAndPersistsOnlySelection()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var original = File.ReadAllBytes(configurationPath);
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("guffawaffle", "stable"));
        var backupStore = CreateBackupStore(directory.Path);
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            backupStore,
            null);
        var preview = service.Preview("netniv", "stable", configurationPath);

        var result = await service.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), store.Load());
        Assert.IsNotNull(result.ConfigurationBackup);
        Assert.AreEqual("guffawaffle", result.ConfigurationBackup.ProviderId);
        Assert.AreEqual("netniv", result.ConfigurationBackup.TargetProviderId);
        CollectionAssert.AreEqual(
            original,
            backupStore.Read(
                directory.Path,
                "guffawaffle",
                result.ConfigurationBackup.BackupId));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        StringAssert.Contains(result.Message, "Selected NetniV");
        Assert.IsFalse(result.Message.Contains("restart", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ProviderRoundTripRestoresEachProvidersLatestToml()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var guffawaffleContents = File.ReadAllBytes(configurationPath);
        var netnivContents = Encoding.UTF8.GetBytes(
            "# netniv profile\r\n[graphics]\r\nfree_resize = false\r\n");
        var selectionStore = new JsonLauncherProviderSelectionStore(directory.Path);
        selectionStore.Save(new("guffawaffle", "stable"));
        var backupStore = CreateBackupStore(directory.Path);
        await backupStore.CreateAsync(new(
            directory.Path,
            "netniv",
            configurationPath,
            netnivContents,
            "test-seed"));
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            selectionStore,
            backupStore,
            null);

        var toNetniv = service.Preview("netniv", "stable", configurationPath);
        Assert.AreEqual(
            LauncherProviderSwitchConfigurationKind.RestoreProviderHistory,
            toNetniv.ConfigurationKind);
        await service.ExecuteAsync(toNetniv, toNetniv.ConfirmationText);
        CollectionAssert.AreEqual(netnivContents, File.ReadAllBytes(configurationPath));

        var toGuffawaffle = service.Preview("guffawaffle", "stable", configurationPath);
        Assert.AreEqual(
            LauncherProviderSwitchConfigurationKind.RestoreProviderHistory,
            toGuffawaffle.ConfigurationKind);
        await service.ExecuteAsync(toGuffawaffle, toGuffawaffle.ConfirmationText);

        CollectionAssert.AreEqual(guffawaffleContents, File.ReadAllBytes(configurationPath));
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), selectionStore.Load());
        Assert.IsTrue(backupStore.List(directory.Path, "guffawaffle").Count >= 1);
        Assert.IsTrue(backupStore.List(directory.Path, "netniv").Count >= 2);
        Assert.IsFalse(File.Exists(configurationPath + ".bak"));
    }

    [TestMethod]
    public async Task ConfigurationChangeAfterPreviewAbortsBeforeSelectionMutation()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("guffawaffle", "stable"));
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            directory.Path);
        var preview = service.Preview("netniv", null, configurationPath);
        File.AppendAllText(configurationPath, "changed = true\n");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.ExecuteAsync(preview, preview.ConfirmationText));

        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), store.Load());
        Assert.IsFalse(Directory.Exists(Path.Combine(directory.Path, "provider-switch-backups")));
    }

    [TestMethod]
    public async Task ConfigurationChangeAfterBackupAbortsBeforeSelectionMutation()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("guffawaffle", "stable"));
        var backupStore = CreateBackupStore(directory.Path);
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            backupStore,
            _ => File.AppendAllText(configurationPath, "changed_during_backup = true\n"));
        var preview = service.Preview("netniv", null, configurationPath);

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.ExecuteAsync(preview, preview.ConfirmationText));

        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), store.Load());
        StringAssert.Contains(exception.Message, "while its provider-switch backup");
        Assert.AreEqual(1, backupStore.List(directory.Path, "guffawaffle").Count);
    }

    [TestMethod]
    public void ExplicitMissingConfigurationPathIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("guffawaffle", "stable"));
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            directory.Path);

        Assert.ThrowsException<FileNotFoundException>(
            () => service.Preview(
                "netniv",
                null,
                Path.Combine(directory.Path, "missing.toml")));
    }

    [TestMethod]
    public async Task UnknownPersistedSelectionCanRecoverToKnownProvider()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("withdrawn-provider", "stable"));
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            directory.Path);

        var preview = service.Preview("guffawaffle", "stable", configurationPath);
        var result = await service.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.AreEqual(
            LauncherProviderSelectionResolutionState.UnknownProvider,
            preview.SourceResolutionState);
        Assert.IsTrue(preview.HasUnknownCompatibility);
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), result.Selection);
        Assert.AreEqual(result.Selection, store.Load());
    }

    [TestMethod]
    public async Task CorruptPersistedSelectionCanRecoverToKnownProvider()
    {
        using var directory = new TemporaryDirectory();
        var selectionPath = Path.Combine(directory.Path, "provider-selection.json");
        File.WriteAllText(selectionPath, "{ definitely-not-json");
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            directory.Path);

        var preview = service.Preview("guffawaffle", "stable", null);
        var result = await service.ExecuteAsync(preview, preview.ConfirmationText);

        Assert.AreEqual(
            LauncherProviderSelectionResolutionState.InvalidSelection,
            preview.SourceResolutionState);
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), result.Selection);
        Assert.AreEqual(result.Selection, store.Load());
    }

    [TestMethod]
    public async Task SelectionWriteFailureRollsBackEffectiveSource()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var store = new WriteThenFailSelectionStore();
        store.Save(new("guffawaffle", "stable"));
        var backupStore = CreateBackupStore(directory.Path);
        await backupStore.CreateAsync(new(
            directory.Path,
            "netniv",
            configurationPath,
            Encoding.UTF8.GetBytes("[graphics]\nfree_resize = false\n"),
            "test-seed"));
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            backupStore,
            null);
        var preview = service.Preview("netniv", null, configurationPath);
        store.FailNextSave = true;

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.ExecuteAsync(preview, preview.ConfirmationText));

        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "stable"), store.Load());
        StringAssert.Contains(exception.Message, "rolled back");
        CollectionAssert.AreEqual(
            File.ReadAllBytes(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Fixtures",
                    "Providers",
                    "source-switch-unknown-content.v1.toml")),
            File.ReadAllBytes(configurationPath));
    }

    [TestMethod]
    public void SelectionDocumentRejectsUnknownFields()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "provider-selection.json"),
            """
            {
              "schemaVersion": 1,
              "providerId": "guffawaffle",
              "releaseChannelId": "stable",
              "modTomlOverride": true
            }
            """);
        var store = new JsonLauncherProviderSelectionStore(directory.Path);

        Assert.ThrowsException<InvalidDataException>(() => store.Load());
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

    private static string WriteConfiguration(string directory)
    {
        TemporaryDirectory.CreateFile(directory, "prime.exe");
        var path = Path.Combine(directory, "community_patch_settings.toml");
        File.Copy(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "Providers",
                "source-switch-unknown-content.v1.toml"),
            path);
        return path;
    }

    private static ProviderScopedConfigurationBackupStore CreateBackupStore(string stateDirectory) =>
        new(stateDirectory, new ReversingProtector(), new NoOpStorageSecurity());

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

    private sealed class WriteThenFailSelectionStore : ILauncherProviderSelectionStore
    {
        private LauncherProviderSelection? selection;

        public bool FailNextSave { get; set; }

        public LauncherProviderSelection? Load() => selection;

        public void Save(LauncherProviderSelection value)
        {
            selection = value;
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("Injected provider-selection write failure.");
            }
        }

        public void Clear() => selection = null;
    }
}
