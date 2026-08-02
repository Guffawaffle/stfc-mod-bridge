using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherAboutCatalogTests
{
    [TestMethod]
    public void RepositoryCatalogCoversEveryProductionPackageReference()
    {
        var root = RepositoryRoot();
        using var stream = File.OpenRead(
            Path.Combine(root, "docs", "windows-launcher", "about-content.v1.json"));
        var catalog = LauncherAboutCatalogLoader.Load(stream);
        var inventoryIds = catalog.DependencyInventory
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var packageIds = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants("PackageReference"))
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .ToArray();

        Assert.IsTrue(packageIds.Length > 0);
        foreach (var packageId in packageIds)
        {
            Assert.IsTrue(
                inventoryIds.Contains(packageId),
                $"Production package '{packageId}' must be represented in the About notice inventory.");
        }

        var noticeIds = catalog.ThirdPartyNotices
            .Select(notice => notice.Id)
            .ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(
            catalog.DependencyInventory
                .Concat(catalog.AssetInventory)
                .Where(item => item.AttributionStatus == "required")
                .All(item => item.NoticeId is not null && noticeIds.Contains(item.NoticeId)));
    }

    [TestMethod]
    public void MissingRequiredNoticeFailsClosed()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "docs", "windows-launcher", "about-content.v1.json"));
        var document = JsonNode.Parse(source)?.AsObject();
        Assert.IsNotNull(document);
        var inventory = document["dependencyInventory"]?.AsArray();
        Assert.IsNotNull(inventory);
        inventory[0]!["noticeId"] = null;
        using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes(document.ToJsonString()));

        var exception = Assert.ThrowsException<InvalidDataException>(
            () => LauncherAboutCatalogLoader.Load(stream));

        StringAssert.Contains(exception.Message, "has no notice ID");
    }

    [TestMethod]
    public void CatalogKeepsProviderRecognitionSeparateFromProductApproval()
    {
        using var stream = File.OpenRead(
            Path.Combine(
                RepositoryRoot(),
                "docs",
                "windows-launcher",
                "about-content.v1.json"));
        var catalog = LauncherAboutCatalogLoader.Load(stream);

        StringAssert.Contains(catalog.GameAcknowledgement, "independently developed");
        StringAssert.Contains(catalog.GameAcknowledgement, "does not extend");
        StringAssert.Contains(catalog.LegalReviewStatus, "issue #30");
        Assert.IsTrue(catalog.Contributors.Any(item => item.Name.Contains("NetniV", StringComparison.Ordinal)));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate the launcher repository root.");
        return directory.FullName;
    }
}
