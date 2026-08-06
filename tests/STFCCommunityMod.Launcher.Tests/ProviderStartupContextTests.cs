using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ProviderStartupContextTests
{
    [TestMethod]
    public void FreshLauncherDefaultsToNetnivStableWithoutPersistingSelection()
    {
        using var directory = new TemporaryStateDirectory();

        var context = BundledLauncherProviderCatalog.LoadStartupContext(directory.Path);

        Assert.AreEqual(LauncherProviderSelectionResolutionState.Defaulted, context.Selection.State);
        Assert.AreEqual("netniv", context.Selection.Provider?.Id);
        Assert.AreEqual("stable", context.Selection.ReleaseChannel?.Id);
        Assert.IsFalse(File.Exists(Path.Combine(directory.Path, "provider-selection.json")));
    }

    [TestMethod]
    public void ExistingExplicitGuffawaffleSelectionIsPreserved()
    {
        using var directory = new TemporaryStateDirectory();
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("guffawaffle", "preview"));

        var context = BundledLauncherProviderCatalog.LoadStartupContext(directory.Path);

        Assert.AreEqual(LauncherProviderSelectionResolutionState.Selected, context.Selection.State);
        Assert.AreEqual("guffawaffle", context.Selection.Provider?.Id);
        Assert.AreEqual("preview", context.Selection.ReleaseChannel?.Id);
        Assert.AreEqual(new LauncherProviderSelection("guffawaffle", "preview"), store.Load());
    }

    [TestMethod]
    public void BundledKnownArtifactCatalogUsesResolvedProviderIdentities()
    {
        var providerCatalog = BundledLauncherProviderCatalog.Load();
        var artifacts = BundledLauncherProviderCatalog.LoadKnownWindowsArtifacts(providerCatalog);

        Assert.AreEqual(3, artifacts.Count);
        var netnivStable = artifacts.Find(
            "020C975FD2391DF1814897B9D5F03A55443F99367EA6ACC4065AF7E240D9547A",
            19630080);
        Assert.IsNotNull(netnivStable);
        Assert.AreEqual("netniv", netnivStable.ProviderId);
        Assert.AreEqual("stable", netnivStable.TrackId);
    }

    [TestMethod]
    public void KnownArtifactCatalogRejectsProviderRuntimeMismatch()
    {
        var providerCatalog = BundledLauncherProviderCatalog.Load();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """
            {
              "schemaVersion": 1,
              "artifacts": [{
                "providerId": "netniv",
                "runtimeDistributionId": "guffawaffle.stfc-community-mod",
                "trackId": "stable",
                "version": "1.1.4",
                "size": 42,
                "sha256": "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
                "sourceReference": "github-release:v1.1.4",
                "observedAtUtc": "2026-07-19T15:55:25.0000000+00:00"
              }]
            }
            """));

        Assert.ThrowsException<InvalidDataException>(
            () => KnownModArtifactCatalogLoader.Load(stream, providerCatalog));
    }

    [TestMethod]
    public void SupportedConfigurationCatalogIsBoundToItsOwningProviderIdentity()
    {
        var providerCatalog = BundledLauncherProviderCatalog.Load();

        foreach (var provider in providerCatalog.Providers.Values.Where(provider =>
                     provider.ConfigurationSchema.Status == LauncherProviderCapabilityStatus.Supported))
        {
            var configurationCatalog = BundledLauncherProviderCatalog.LoadConfigurationCatalog(provider);

            Assert.AreEqual(
                provider.Id,
                configurationCatalog.Source.StableId,
                $"Provider '{provider.Id}' must not project another provider's settings or Data Sync catalog.");
        }
    }

    [TestMethod]
    public void NetnivConfigurationResolvesOnlyTheExactReviewedStableCatalog()
    {
        var provider = BundledLauncherProviderCatalog.Load().GetProvider("netniv");

        var catalog = BundledLauncherProviderCatalog.LoadConfigurationCatalog(provider);

        Assert.AreEqual("netniv", catalog.Source.StableId);
        Assert.AreEqual("netniv.configuration.stable-1.1.4", catalog.Identity.CatalogId);
        Assert.AreEqual("1.1.4", catalog.Identity.ReleaseVersion);
        Assert.AreEqual("d912611fa1eca49fc54f363bdf8377dfebf8def0", catalog.Identity.SourceCommit);
    }

    [TestMethod]
    public void CorruptSelectionProducesRestrictedRecoveryContextInsteadOfThrowing()
    {
        using var directory = new TemporaryStateDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "provider-selection.json"),
            "{ corrupt-selection");

        var context = BundledLauncherProviderCatalog.LoadStartupContext(directory.Path);

        Assert.AreEqual(
            LauncherProviderSelectionResolutionState.InvalidSelection,
            context.Selection.State);
        Assert.IsFalse(context.Selection.IsResolved);
        Assert.IsNull(context.Selection.Provider);
        StringAssert.Contains(context.Selection.Message, "unreadable");
        Assert.AreEqual("netniv", context.Catalog.DefaultProviderId);
    }

    [TestMethod]
    public void WithdrawnSelectionProducesRestrictedRecoveryContextInsteadOfFallback()
    {
        using var directory = new TemporaryStateDirectory();
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("withdrawn-provider", "stable"));

        var context = BundledLauncherProviderCatalog.LoadStartupContext(directory.Path);

        Assert.AreEqual(
            LauncherProviderSelectionResolutionState.UnknownProvider,
            context.Selection.State);
        Assert.IsFalse(context.Selection.IsResolved);
        Assert.AreEqual("withdrawn-provider", context.Selection.Selection.ProviderId);
        StringAssert.Contains(context.Selection.Message, "not present");
    }

    private sealed class TemporaryStateDirectory : IDisposable
    {
        public TemporaryStateDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "stfc-launcher-provider-startup-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

}
