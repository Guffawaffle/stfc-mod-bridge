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
            Assert.IsFalse(description.Contains("capabilities supported", StringComparison.OrdinalIgnoreCase));
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
    public void ProviderReviewHasBoundedScrollingAndAStickySingleAction()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var dialog = document.Descendants()
            .Single(element => GetName(element) == "ProviderSwitchDialog");
        var contentGrid = dialog.Elements(Presentation + "Grid").Single();
        var scroller = contentGrid.Elements(Presentation + "ScrollViewer").Single();
        var action = contentGrid.Elements(Presentation + "Button")
            .Single(button => GetName(button) == "ProviderSwitchActionButton");

        Assert.IsNotNull(contentGrid.Attribute("MaxHeight"));
        Assert.AreEqual("Auto", (string?)scroller.Attribute("VerticalScrollBarVisibility"));
        Assert.AreEqual("0", (string?)scroller.Attribute("Grid.Row"));
        Assert.AreEqual("1", (string?)action.Attribute("Grid.Row"));
        Assert.AreEqual("Switch community mod source", AutomationName(action));
        Assert.IsFalse(dialog.Descendants(Presentation + "TextBox").Any());
        Assert.AreEqual(1, dialog.Descendants(Presentation + "Button").Count());
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

    private static string? AutomationName(XElement element) =>
        element.Attributes().SingleOrDefault(
            attribute => attribute.Name.LocalName == "AutomationProperties.Name"
                && attribute.Name.NamespaceName.Contains("automation", StringComparison.OrdinalIgnoreCase))?.Value;

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
