using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherAboutCatalogTests
{
    [TestMethod]
    public void RepositoryCatalogExactlyCoversResolvedPublishDependenciesAndExplicitBundledInputs()
    {
        var root = RepositoryRoot();
        using var stream = File.OpenRead(
            Path.Combine(root, "docs", "windows-launcher", "about-content.v1.json"));
        var catalog = LauncherAboutCatalogLoader.Load(stream);
        var projects = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToArray();
        var resolvedDependencies = projects
            .SelectMany(ReadResolvedPublishDependencies)
            .ToHashSet(StringComparer.Ordinal);
        var inventoriedDependencies = catalog.DependencyInventory
            .Where(item => item.EvidenceKind is "resolved-package" or "runtime-pack")
            .Select(DependencyKey)
            .ToHashSet(StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(
            resolvedDependencies.ToArray(),
            inventoriedDependencies.ToArray());

        var explicitInputs = projects
            .SelectMany(ReadExplicitBundledInputs)
            .ToHashSet(StringComparer.Ordinal);
        var inventoriedInputs = catalog.AssetInventory
            .Where(item => item.EvidenceKind == "project-input")
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(explicitInputs.ToArray(), inventoriedInputs.ToArray());

        var noticeIds = catalog.ThirdPartyNotices
            .Select(notice => notice.Id)
            .ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(
            catalog.DependencyInventory
                .Concat(catalog.AssetInventory)
                .Where(item => item.AttributionStatus == "required")
                .All(item => item.NoticeId is not null && noticeIds.Contains(item.NoticeId)));
        StringAssert.Contains(catalog.NoticeCoverageStatus, "does not claim legal completeness");
        StringAssert.Contains(catalog.NoticeCoverageStatus, "issue #30");
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
        Assert.IsTrue(catalog.Contributors.Any(item => item.Name == "NetniV"));
        var tashcan = catalog.Contributors.Single(item => item.Name == "Tashcan");
        Assert.IsNull(tashcan.Url);
        Assert.IsFalse(tashcan.HasUrl);
    }

    [TestMethod]
    public void UnknownAttributionStatusFailsClosed()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "docs", "windows-launcher", "about-content.v1.json"));
        var document = JsonNode.Parse(source)?.AsObject();
        Assert.IsNotNull(document);
        document["assetInventory"]!.AsArray()[0]!["attributionStatus"] = "assumed-clear";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));

        var exception = Assert.ThrowsException<InvalidDataException>(
            () => LauncherAboutCatalogLoader.Load(stream));

        StringAssert.Contains(exception.Message, "unsupported attribution status");
    }

    private static IEnumerable<string> ReadResolvedPublishDependencies(string projectPath)
    {
        var assetsPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");
        Assert.IsTrue(File.Exists(assetsPath), $"Restore manifest missing for {projectPath}.");
        var assets = JsonNode.Parse(File.ReadAllText(assetsPath))!.AsObject();
        foreach (var target in assets["targets"]!.AsObject().Select(item => item.Value!.AsObject()))
        {
            foreach (var library in target)
            {
                var definition = library.Value!.AsObject();
                var hasRuntimePayload = definition.ContainsKey("runtime")
                    || definition.ContainsKey("runtimeTargets")
                    || definition.ContainsKey("native")
                    || definition.ContainsKey("resource");
                if (definition["type"]?.GetValue<string>() != "package" || !hasRuntimePayload)
                {
                    continue;
                }
                var separator = library.Key.LastIndexOf('/');
                Assert.IsTrue(separator > 0, $"Malformed package identity {library.Key}.");
                yield return $"resolved-package|{library.Key[..separator]}|{library.Key[(separator + 1)..]}";
            }
        }

        foreach (var framework in assets["project"]!["frameworks"]!.AsObject())
        {
            foreach (var download in framework.Value!["downloadDependencies"]?.AsArray() ?? [])
            {
                var range = download!["version"]!.GetValue<string>();
                var bounds = range.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries);
                Assert.AreEqual(2, bounds.Length, $"Unexpected runtime-pack version range {range}.");
                Assert.AreEqual(bounds[0], bounds[1], $"Runtime-pack version must be exact: {range}.");
                yield return $"runtime-pack|{download["name"]!.GetValue<string>()}|{bounds[0]}";
            }
        }
    }

    private static IEnumerable<string> ReadExplicitBundledInputs(string projectPath)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var document = XDocument.Load(projectPath);
        foreach (var element in document.Descendants()
                     .Where(element => element.Name.LocalName is "Resource" or "Content" or "EmbeddedResource"))
        {
            var include = (string?)element.Attribute("Include");
            if (!string.IsNullOrWhiteSpace(include))
            {
                yield return $"{projectName}|{element.Name.LocalName}|{include.Replace('\\', '/')}";
            }
        }
        foreach (var element in document.Descendants()
                     .Where(element => element.Name.LocalName is "ApplicationIcon" or "ApplicationManifest"))
        {
            if (!string.IsNullOrWhiteSpace(element.Value))
            {
                yield return $"{projectName}|{element.Name.LocalName}|{element.Value.Replace('\\', '/')}";
            }
        }
    }

    private static string DependencyKey(LauncherNoticeInventoryItem item) =>
        $"{item.EvidenceKind}|{item.Id}|{item.Version}";

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
