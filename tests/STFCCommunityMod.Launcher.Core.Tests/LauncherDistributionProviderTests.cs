using System.Text;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherDistributionProviderTests
{
    [TestMethod]
    public void NeutralFixturesResolveBothProvidersFromStableIds()
    {
        var catalog = LoadFixtureCatalog();

        Assert.AreEqual("guffawaffle", catalog.DefaultProviderId);
        Assert.AreEqual(2, catalog.Providers.Count);
        var guffawaffle = catalog.GetProvider("guffawaffle");
        var netniv = catalog.GetProvider("netniv");
        Assert.AreEqual("Guffawaffle/stfc-mod", guffawaffle.DefaultReleaseChannel.Repository);
        Assert.AreEqual("netniV/stfc-mod", netniv.DefaultReleaseChannel.Repository);
        Assert.AreEqual(
            LauncherProviderReleaseDiscoveryKind.GitHubReleaseAsset,
            netniv.DefaultReleaseChannel.DiscoveryKind);
        Assert.AreEqual("stfc-community-mod.zip", netniv.DefaultReleaseChannel.ArtifactAssetName);
    }

    [TestMethod]
    public void DisplayNameDoesNotDefineProviderIdentity()
    {
        var contents = File.ReadAllText(FixturePath("guffawaffle-provider-pack.v1.json"));
        using var stream = JsonStream(contents.Replace(
            "\"displayName\": \"Guffawaffle\"",
            "\"displayName\": \"Any localized title\"",
            StringComparison.Ordinal));

        var provider = LauncherDistributionProviderCatalogLoader.LoadPack(stream);

        Assert.AreEqual("guffawaffle", provider.Id);
        Assert.AreEqual("Any localized title", provider.DisplayName);
    }

    [TestMethod]
    public void MissingCapabilityFailsClosedAsUnknown()
    {
        var contents = File.ReadAllText(FixturePath("netniv-provider-pack.v1.json"));
        using var stream = JsonStream(contents.Replace(
            "      { \"id\": \"settings.catalog\", \"status\": \"unknown\" },\r\n",
            string.Empty,
            StringComparison.Ordinal).Replace(
            "      { \"id\": \"settings.catalog\", \"status\": \"unknown\" },\n",
            string.Empty,
            StringComparison.Ordinal));

        var provider = LauncherDistributionProviderCatalogLoader.LoadPack(stream);

        Assert.AreEqual(
            LauncherProviderCapabilityStatus.Unknown,
            provider.GetCapabilityStatus(LauncherProviderCapabilityIds.ConfigurationCatalog));
    }

    [TestMethod]
    public void UnknownNetnivTrustAndConfigurationRemainVisibleAndFailClosed()
    {
        var netniv = LoadFixtureCatalog().GetProvider("netniv");

        Assert.AreEqual(
            LauncherProviderCapabilityStatus.Unknown,
            netniv.GetCapabilityStatus(LauncherProviderCapabilityIds.ArtifactTrust));
        Assert.AreEqual(LauncherProviderCapabilityStatus.Unknown, netniv.ConfigurationSchema.Status);
        Assert.IsFalse(netniv.CanAuthenticateWindowsArtifact);
        Assert.IsFalse(netniv.CanUseManifestReleaseDiscovery);
        StringAssert.Contains(netniv.CapabilitySummary, "mod.artifact-trust: unknown");
    }

    [TestMethod]
    public void ProviderCannotRedefineLauncherSelfUpdateAuthority()
    {
        var netniv = LoadFixtureCatalog().GetProvider("netniv");

        Assert.AreNotEqual(
            netniv.DefaultReleaseChannel.Repository,
            LauncherSelfUpdateAuthority.ReleaseRepository);
        Assert.AreEqual("Joseph Gustavson", LauncherSelfUpdateAuthority.WindowsArtifactPublisher);
    }

    [TestMethod]
    public void ProviderBindingUsesExactNonDefaultChannelCoordinates()
    {
        var contents = File.ReadAllText(FixturePath("guffawaffle-provider-pack.v1.json"));
        var previewIndex = contents.IndexOf("\"id\": \"preview\"", StringComparison.Ordinal);
        Assert.IsTrue(previewIndex > 0);
        var repositoryIndex = contents.IndexOf(
            "Guffawaffle/stfc-mod",
            previewIndex,
            StringComparison.Ordinal);
        Assert.IsTrue(repositoryIndex > previewIndex);
        contents = contents.Remove(repositoryIndex, "Guffawaffle/stfc-mod".Length)
            .Insert(repositoryIndex, "PreviewOwner/stfc-mod-preview");
        using var stream = JsonStream(contents);
        var provider = LauncherDistributionProviderCatalogLoader.LoadPack(stream);
        var preview = provider.ReleaseChannels["preview"];

        var binding = LauncherProviderModBinding.Resolve(provider, preview);

        Assert.IsTrue(binding.IsAvailable);
        Assert.AreEqual("preview", binding.ReleaseChannelId);
        Assert.AreEqual("PreviewOwner/stfc-mod-preview", binding.Repository);
        Assert.AreEqual("stfc-community-mod-release-manifest.json", binding.ManifestAssetName);
    }

    [TestMethod]
    public void PortableProviderPackSchemaIsVersioned()
    {
        using var stream = File.OpenRead(FixturePath("provider-pack.schema.v1.json"));
        using var document = System.Text.Json.JsonDocument.Parse(stream);

        Assert.AreEqual(
            "https://json-schema.org/draft/2020-12/schema",
            document.RootElement.GetProperty("$schema").GetString());
        Assert.AreEqual(
            "STFC Mod Control provider pack v1",
            document.RootElement.GetProperty("title").GetString());
    }

    [TestMethod]
    public void SupportedCapabilityWithoutEvidenceIsRejected()
    {
        var contents = File.ReadAllText(FixturePath("netniv-provider-pack.v1.json"));
        using var stream = JsonStream(contents.Replace(
            "{ \"id\": \"mod.artifact-trust\", \"status\": \"unknown\" }",
            "{ \"id\": \"mod.artifact-trust\", \"status\": \"supported\" }",
            StringComparison.Ordinal));

        Assert.ThrowsException<InvalidDataException>(
            () => LauncherDistributionProviderCatalogLoader.LoadPack(stream));
    }

    [DataTestMethod]
    [DataRow("\"schemaVersion\": 1", "\"schemaVersion\": 2")]
    [DataRow("\"id\": \"guffawaffle\"", "\"id\": \"Not Stable\"")]
    [DataRow("\"repository\": \"Guffawaffle/stfc-mod\"", "\"repository\": \"not-a-repository\"")]
    [DataRow("\"manifestAssetName\": \"stfc-community-mod-release-manifest.json\"", "\"manifestAssetName\": \"../manifest.json\"")]
    public void InvalidPackIdentityFailsClosed(string oldValue, string newValue)
    {
        var contents = File.ReadAllText(FixturePath("guffawaffle-provider-pack.v1.json"));
        using var stream = JsonStream(contents.Replace(oldValue, newValue, StringComparison.Ordinal));

        Assert.ThrowsException<InvalidDataException>(
            () => LauncherDistributionProviderCatalogLoader.LoadPack(stream));
    }

    [TestMethod]
    public void UnknownProviderPropertyFailsClosed()
    {
        var contents = File.ReadAllText(FixturePath("guffawaffle-provider-pack.v1.json"));
        using var stream = JsonStream(contents.Replace(
            "\"id\": \"guffawaffle\"",
            "\"id\": \"guffawaffle\", \"surprise\": true",
            StringComparison.Ordinal));

        Assert.ThrowsException<InvalidDataException>(
            () => LauncherDistributionProviderCatalogLoader.LoadPack(stream));
    }

    internal static LauncherDistributionProviderCatalog LoadFixtureCatalog()
    {
        using var index = File.OpenRead(FixturePath("bundled-provider-catalog.v1.json"));
        return LauncherDistributionProviderCatalogLoader.Load(
            index,
            resourceName => resourceName switch
            {
                "STFCCommunityMod.Launcher.ProviderPacks.Guffawaffle.v1.json" =>
                    File.OpenRead(FixturePath("guffawaffle-provider-pack.v1.json")),
                "STFCCommunityMod.Launcher.ProviderPacks.Netniv.v1.json" =>
                    File.OpenRead(FixturePath("netniv-provider-pack.v1.json")),
                _ => null,
            });
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Providers", fileName);

    private static MemoryStream JsonStream(string json) =>
        new(Encoding.UTF8.GetBytes(json), writable: false);
}
