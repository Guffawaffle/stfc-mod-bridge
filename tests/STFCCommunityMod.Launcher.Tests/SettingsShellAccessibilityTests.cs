using System.Xml.Linq;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class SettingsShellAccessibilityTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";
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

    [TestMethod]
    public void OpenSearchReclaimsDecorativeTitleBarSpaceAtMinimumWidth()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");

        AssertSearchCollapseTrigger(document, "ProductTitleText");
        AssertSearchCollapseTrigger(document, "SettingsDiagnosticsTitleBarButton");
    }

    [TestMethod]
    public void HelpFlyoutClosesThroughItsUnloadContract()
    {
        var document = LoadXaml(
            "src/STFCCommunityMod.Launcher/Controls/HelpFlyoutButton.xaml");
        var root = document.Root;
        Assert.IsNotNull(root);

        Assert.AreEqual(
            "HelpFlyoutButton_Unloaded",
            (string?)root.Attribute("Unloaded"));
    }

    [TestMethod]
    public void DefaultActionUsesAccessTextMnemonic()
    {
        var document = LoadXaml(
            "src/STFCCommunityMod.Launcher/Controls/SettingsRowActions.xaml");
        var accessText = document.Descendants(Presentation + "AccessText")
            .SingleOrDefault(element => (string?)element.Attribute("Text") == "_Default");

        Assert.IsNotNull(accessText);
    }

    [TestMethod]
    public void PatchGatePublishesWarningStateAndKeyboardActions()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Views/SettingsView.xaml");
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(Automation + "AutomationProperties.Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        CollectionAssert.Contains(names, "Patch editing safety warning");
        CollectionAssert.Contains(names, "Enable patch editing");
        CollectionAssert.Contains(names, "Lock patch editing");
        CollectionAssert.Contains(names, "Read-only patch value summary");

        var accessTextValues = document.Descendants(Presentation + "AccessText")
            .Select(element => (string?)element.Attribute("Text"))
            .ToArray();
        CollectionAssert.Contains(accessTextValues, "_Enable patch editing");
        CollectionAssert.Contains(accessTextValues, "_Lock patch editing");

        var enable = document.Descendants(Presentation + "Button")
            .Single(
                element =>
                    (string?)element.Attribute(Automation + "AutomationProperties.Name")
                    == "Enable patch editing");
        Assert.AreEqual(
            "{Binding WarningText}",
            (string?)enable.Attribute(Automation + "AutomationProperties.HelpText"));
        Assert.AreEqual(
            "{Binding ElementName=PatchEditingWarning}",
            (string?)enable.Attribute(Automation + "AutomationProperties.LabeledBy"));
    }

    private static void AssertSearchCollapseTrigger(XDocument document, string elementName)
    {
        var element = document.Descendants()
            .Single(item => (string?)item.Attribute(Xaml + "Name") == elementName);
        var trigger = element.Descendants(Presentation + "DataTrigger")
            .SingleOrDefault(
                item =>
                    (string?)item.Attribute("Binding")
                        == "{Binding DataContext.IsSearchVisible, ElementName=SettingsWorkspace}"
                    && (string?)item.Attribute("Value") == "True");

        Assert.IsNotNull(trigger, $"{elementName} must react to open settings search.");
        Assert.IsTrue(
            trigger.Elements(Presentation + "Setter").Any(
                setter =>
                    (string?)setter.Attribute("Property") == "Visibility"
                    && (string?)setter.Attribute("Value") == "Collapsed"),
            $"{elementName} must collapse while settings search is open.");
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
