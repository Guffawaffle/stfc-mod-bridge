using System.Xml.Linq;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class SettingsShellAccessibilityTests
{
    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [TestMethod]
    public void SearchClearAndClosePublishDistinctUiAutomationContracts()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(Automation + "AutomationProperties.Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        CollectionAssert.Contains(names, "Open settings search");
        CollectionAssert.Contains(names, "Clear settings search query");
        CollectionAssert.Contains(names, "Close settings search");
    }

    [TestMethod]
    public void HelpFlyoutPublishesNameAndHelpToUiAutomation()
    {
        var document = LoadXaml(
            "src/STFCCommunityMod.Launcher/Controls/HelpFlyoutButton.xaml");
        var root = document.Root;
        Assert.IsNotNull(root);

        Assert.AreEqual(
            "{Binding AutomationName, ElementName=Root}",
            (string?)root.Attribute(Automation + "AutomationProperties.Name"));
        Assert.AreEqual(
            "{Binding HelpText, ElementName=Root}",
            (string?)root.Attribute(Automation + "AutomationProperties.HelpText"));
    }

    private static XDocument LoadXaml(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate the launcher repository root.");
        return XDocument.Load(Path.Combine(directory.FullName, relativePath));
    }
}
