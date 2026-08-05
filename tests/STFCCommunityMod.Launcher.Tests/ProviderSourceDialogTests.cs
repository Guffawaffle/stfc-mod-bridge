using System.Xml.Linq;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ProviderSourceDialogTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [TestMethod]
    public void ProviderPresentationUsesCatalogCopyWithoutInternalCapabilityIds()
    {
        var catalog = BundledLauncherProviderCatalog.Load();

        foreach (var provider in catalog.Providers.Values)
        {
            var description = LauncherProviderPresentation.Describe(provider);

            StringAssert.Contains(description, provider.DefaultReleaseChannel.DisplayName);
            StringAssert.Contains(description, provider.Description);
            Assert.IsFalse(description.Contains(provider.GetType().FullName!, StringComparison.Ordinal));
            foreach (var capabilityId in LauncherProviderCapabilityIds.ContractCapabilities)
            {
                Assert.IsFalse(description.Contains(capabilityId, StringComparison.Ordinal));
            }
        }
    }

    [TestMethod]
    public void ProviderSelectorProjectsDisplayNameAndHasOneDismissalAction()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var dialog = document.Descendants()
            .Single(element => GetName(element) == "ProviderSwitchDialog");
        var selector = dialog.Descendants(Presentation + "ComboBox")
            .Single(element => GetName(element) == "ProviderSourceSelector");
        var displayBinding = selector.Descendants(Presentation + "TextBlock")
            .SelectMany(element => element.Attributes())
            .Single(attribute => attribute.Name.LocalName == "Text");

        Assert.AreEqual("Id", (string?)selector.Attribute("SelectedValuePath"));
        Assert.AreEqual("{Binding DisplayName}", displayBinding.Value);
        Assert.IsFalse(dialog.Descendants(Presentation + "Button")
            .Any(button => string.Equals((string?)button.Attribute("Content"), "_Cancel", StringComparison.Ordinal)));
        Assert.IsNull(dialog.Elements().Single().Attribute("Width"));
        Assert.IsFalse(dialog.Value.Contains("restart Mod Bridge", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(document.Descendants(Presentation + "Button")
            .Any(button => GetName(button) == "RetryProviderRecompositionButton"));
    }

    [TestMethod]
    public void ProviderSwitchFlowNeverRequestsABridgeRestart()
    {
        var sources = new[]
        {
            "src/STFCCommunityMod.Launcher/MainWindow.xaml",
            "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs",
            "src/STFCCommunityMod.Launcher.Core/LauncherProviderSelection.cs",
            "src/STFCCommunityMod.Launcher.Core/LauncherProviderSwitchCoordinator.cs",
        };

        foreach (var source in sources)
        {
            var text = File.ReadAllText(Path.Combine(RepositoryRoot(), source));
            Assert.IsFalse(
                text.Contains("restart Mod Bridge", StringComparison.OrdinalIgnoreCase),
                $"Provider-switch source '{source}' must recompose in-process.");
        }
    }

    private static XDocument LoadXaml(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), relativePath));

    private static string? GetName(XElement element) =>
        element.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == "Name")?.Value;

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "STFCCommunityMod.Launcher.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
