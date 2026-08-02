using System.Text;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherDistributionProviderTests
{
    [TestMethod]
    public void CatalogResolvesDefaultProviderFromStableId()
    {
        using var stream = JsonStream(Catalog());

        var catalog = LauncherDistributionProviderCatalogLoader.Load(stream);

        Assert.AreEqual("guffawaffle", catalog.DefaultProviderId);
        Assert.AreEqual("Guffawaffle", catalog.DefaultProvider.DisplayName);
        Assert.AreEqual("Guffawaffle/stfc-mod", catalog.DefaultProvider.ModReleaseRepository);
        Assert.AreEqual(
            "guffawaffle.stfc-community-mod",
            catalog.DefaultProvider.RuntimeDistributionId);
    }

    [TestMethod]
    public void DisplayNameDoesNotDefineProviderIdentity()
    {
        using var stream = JsonStream(Catalog().Replace(
            "\"displayName\": \"Guffawaffle\"",
            "\"displayName\": \"Any localized title\"",
            StringComparison.Ordinal));

        var catalog = LauncherDistributionProviderCatalogLoader.Load(stream);

        Assert.AreEqual("guffawaffle", catalog.DefaultProvider.Id);
        Assert.AreEqual("Any localized title", catalog.DefaultProvider.DisplayName);
    }

    [DataTestMethod]
    [DataRow("\"schemaVersion\": 1", "\"schemaVersion\": 2")]
    [DataRow("\"defaultProviderId\": \"guffawaffle\"", "\"defaultProviderId\": \"missing\"")]
    [DataRow("\"modReleaseRepository\": \"Guffawaffle/stfc-mod\"", "\"modReleaseRepository\": \"not-a-repository\"")]
    [DataRow("\"modReleaseManifestAssetName\": \"stfc-community-mod-release-manifest.json\"", "\"modReleaseManifestAssetName\": \"../manifest.json\"")]
    public void InvalidCatalogIdentityFailsClosed(string oldValue, string newValue)
    {
        using var stream = JsonStream(Catalog().Replace(oldValue, newValue, StringComparison.Ordinal));

        Assert.ThrowsException<InvalidDataException>(
            () => LauncherDistributionProviderCatalogLoader.Load(stream));
    }

    [TestMethod]
    public void UnknownProviderPropertyFailsClosed()
    {
        using var stream = JsonStream(Catalog().Replace(
            "\"id\": \"guffawaffle\"",
            "\"id\": \"guffawaffle\", \"surprise\": true",
            StringComparison.Ordinal));

        Assert.ThrowsException<InvalidDataException>(
            () => LauncherDistributionProviderCatalogLoader.Load(stream));
    }

    private static MemoryStream JsonStream(string json) =>
        new(Encoding.UTF8.GetBytes(json), writable: false);

    private static string Catalog() =>
        """
        {
          "schemaVersion": 1,
          "defaultProviderId": "guffawaffle",
          "providers": [
            {
              "id": "guffawaffle",
              "displayName": "Guffawaffle",
              "runtimeDistributionId": "guffawaffle.stfc-community-mod",
              "modReleaseRepository": "Guffawaffle/stfc-mod",
              "modReleaseManifestAssetName": "stfc-community-mod-release-manifest.json",
              "configurationSchemaResourceName": "Schemas.Guffawaffle.v1.json",
              "runtimeManifestResourceName": "RuntimeManifests.Guffawaffle.v1.json",
              "windowsArtifactPublisher": "Joseph Gustavson"
            }
          ]
        }
        """;
}
