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
    public void SwitchRequiresExactConfirmationAndPreviewsUnknownCapabilities()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            directory.Path);

        var preview = service.Preview("netniv", null, configurationPath);

        Assert.IsTrue(preview.HasUnknownCompatibility);
        Assert.IsTrue(preview.Concerns.Any(concern =>
            concern.CapabilityId == LauncherProviderCapabilityIds.ArtifactTrust
            && concern.Kind == LauncherProviderCompatibilityKind.Unknown));
        Assert.ThrowsException<InvalidOperationException>(
            () => service.Execute(preview, "NetniV"));
        Assert.IsNull(store.Load());
    }

    [TestMethod]
    public void ConfirmedSwitchBacksUpBytesAndPersistsOnlySelection()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var original = File.ReadAllBytes(configurationPath);
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            directory.Path);
        var preview = service.Preview("netniv", "stable", configurationPath);

        var result = service.Execute(preview, preview.ConfirmationText);

        Assert.AreEqual(new LauncherProviderSelection("netniv", "stable"), store.Load());
        Assert.IsNotNull(result.ConfigurationBackupPath);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(result.ConfigurationBackupPath));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        StringAssert.Contains(result.Message, "Restart");
    }

    [TestMethod]
    public void ConfigurationChangeAfterPreviewAbortsBeforeSelectionMutation()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            directory.Path);
        var preview = service.Preview("netniv", null, configurationPath);
        File.AppendAllText(configurationPath, "changed = true\n");

        Assert.ThrowsException<InvalidOperationException>(
            () => service.Execute(preview, preview.ConfirmationText));

        Assert.IsNull(store.Load());
        Assert.IsFalse(Directory.Exists(Path.Combine(directory.Path, "provider-switch-backups")));
    }

    [TestMethod]
    public void SelectionWriteFailureRollsBackEffectiveSource()
    {
        using var directory = new TemporaryDirectory();
        var configurationPath = WriteConfiguration(directory.Path);
        var store = new WriteThenFailSelectionStore();
        var service = new LauncherProviderSourceSwitchService(
            LauncherDistributionProviderTests.LoadFixtureCatalog(),
            store,
            directory.Path);
        var preview = service.Preview("netniv", null, configurationPath);
        store.FailNextSave = true;

        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => service.Execute(preview, preview.ConfirmationText));

        Assert.IsNull(store.Load());
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

    private static string WriteConfiguration(string directory)
    {
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
