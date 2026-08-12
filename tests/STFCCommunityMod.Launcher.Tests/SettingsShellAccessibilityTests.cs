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
        var popup = document.Descendants(Presentation + "Popup").Single();
        Assert.AreEqual("True", (string?)popup.Attribute("StaysOpen"));
    }

    [TestMethod]
    public void HelpFlyoutAllowsPointerTransferIntoItsPopup()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/Controls/HelpFlyoutButton.xaml.cs"));

        StringAssert.Contains(source, "TimeSpan.FromMilliseconds(250)");
        StringAssert.Contains(source, "closeTimer.Stop();");
        StringAssert.Contains(source, "closeTimer.Start();");
    }

    [TestMethod]
    public void HelpFlyoutIsTransientAndClosesWhenItsWindowDeactivates()
    {
        var document = LoadXaml(
            "src/STFCCommunityMod.Launcher/Controls/HelpFlyoutButton.xaml");
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/Controls/HelpFlyoutButton.xaml.cs"));

        var root = document.Root;
        Assert.IsNotNull(root);
        Assert.AreEqual("HelpFlyoutButton_Loaded", (string?)root.Attribute("Loaded"));
        Assert.IsFalse(source.Contains("IsPinned", StringComparison.Ordinal));
        StringAssert.Contains(source, "previous.CloseFromPeer();");
        StringAssert.Contains(source, "ownerWindow.Deactivated += OwnerWindow_Deactivated;");
        StringAssert.Contains(source, "ownerWindow.Deactivated -= OwnerWindow_Deactivated;");
        StringAssert.Contains(source, "CloseFromPeer();");
    }

    [TestMethod]
    public void SettingRowsDoNotExposeDefaultAsAPeerAction()
    {
        var document = LoadXaml(
            "src/STFCCommunityMod.Launcher/Controls/SettingsRowActions.xaml");

        Assert.IsFalse(document.Descendants().Any(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName is "Text" or "Content"
                && attribute.Value.TrimStart('_').Equals("Default", StringComparison.OrdinalIgnoreCase))));
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
            "{Binding EnableAutomationHelp}",
            (string?)enable.Attribute(Automation + "AutomationProperties.HelpText"));
        Assert.AreEqual(
            "{Binding ElementName=PatchEditingWarning}",
            (string?)enable.Attribute(Automation + "AutomationProperties.LabeledBy"));
    }

    [TestMethod]
    public void DiagnosticsUsesDedicatedStructuredAccessibleWorkspace()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(Automation + "AutomationProperties.Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        CollectionAssert.Contains(names, "Mod Bridge Diagnostics workspace");
        CollectionAssert.Contains(names, "Re-run Diagnostics checks");
        CollectionAssert.Contains(names, "Open detected game folder");
        CollectionAssert.Contains(names, "Open community mod logs folder");
        CollectionAssert.Contains(names, "Copy the displayed redacted diagnostic summary");
        CollectionAssert.Contains(names, "Review eligible configuration cleanup");
        CollectionAssert.Contains(names, "Apply selected reviewed configuration cleanup");
        CollectionAssert.Contains(names, "Cancel configuration cleanup without changing the file");
        CollectionAssert.Contains(names, "Review local unredacted effective configuration export");
        CollectionAssert.Contains(names, "Confirm unredacted effective configuration export");
        CollectionAssert.Contains(names, "Show raw redacted diagnostic JSON");
        CollectionAssert.Contains(names, "Retry exact reviewed candidate recovery");
        CollectionAssert.Contains(names, "Review removal of the Mod Bridge-managed community mod");

        Assert.IsFalse(
            document.Descendants()
                .Any(element => (string?)element.Attribute(Xaml + "Name") == "DiagnosticsDialog"));
    }

    [TestMethod]
    public void ConfigurationCleanupAndEffectiveExportKeepDistinctSafetyContracts()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var cleanup = document.Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ConfigurationCleanupDialog");
        var export = document.Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "EffectiveConfigurationExportDialog");
        var cleanupText = string.Join(" ", cleanup.Descendants().Attributes("Text").Select(attribute => attribute.Value));
        var exportText = string.Join(" ", export.Descendants().Attributes("Text").Select(attribute => attribute.Value));
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));

        StringAssert.Contains(source, "unknown content and values remain untouched");
        StringAssert.Contains(cleanupText, "creates and verifies a protected provider-scoped backup");
        StringAssert.Contains(cleanupText, "Mod Bridge does not restart");
        StringAssert.Contains(exportText, "intentionally unredacted");
        StringAssert.Contains(exportText, "Nothing is uploaded automatically");
        Assert.IsTrue(cleanup.Descendants(Presentation + "Button").Any(button =>
            (string?)button.Attribute("Click") == "CancelConfigurationCleanupButton_Click"));
        Assert.IsTrue(cleanup.Descendants(Presentation + "Button").Any(button =>
            (string?)button.Attribute("Click") == "ConfirmConfigurationCleanupButton_Click"));
    }

    [TestMethod]
    public void DiagnosticTechnicalReportUsesOneWayBinding()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var report = document.Descendants(Presentation + "TextBox")
            .Single(element =>
                (string?)element.Attribute(Automation + "AutomationProperties.Name")
                    == "Exact redacted diagnostic JSON preview");

        Assert.AreEqual(
            "{Binding DiagnosticTechnicalReport, Mode=OneWay}",
            (string?)report.Attribute("Text"));
    }

    [TestMethod]
    public void DiagnosticStatusBadgesShareFixedCenteredGeometryAcrossWrappedRows()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var badgeStyle = document.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "DiagnosticsStatusBadgeStyle");
        var textStyle = document.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "DiagnosticsStatusBadgeTextStyle");
        var badge = document.Descendants(Presentation + "Border")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "DiagnosticStatusBadge");
        var statusText = document.Descendants(Presentation + "TextBlock")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "DiagnosticStatusText");
        var title = badge.Parent!
            .Elements(Presentation + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "{Binding Name}");

        AssertStyleSetter(badgeStyle, "Width", "112");
        AssertStyleSetter(badgeStyle, "Height", "24");
        AssertStyleSetter(badgeStyle, "Padding", "8,0");
        AssertStyleSetter(badgeStyle, "VerticalAlignment", "Center");
        AssertStyleSetter(textStyle, "HorizontalAlignment", "Stretch");
        AssertStyleSetter(textStyle, "VerticalAlignment", "Center");
        AssertStyleSetter(textStyle, "LineHeight", "16");
        AssertStyleSetter(textStyle, "LineStackingStrategy", "BlockLineHeight");
        AssertStyleSetter(textStyle, "TextAlignment", "Center");

        Assert.AreEqual(
            "{StaticResource DiagnosticsStatusBadgeStyle}",
            (string?)badge.Attribute("Style"));
        Assert.AreEqual(
            "{StaticResource DiagnosticsStatusBadgeTextStyle}",
            (string?)statusText.Attribute("Style"));
        Assert.AreEqual("Wrap", (string?)title.Attribute("TextWrapping"));

        var geometryProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "Height",
            "HorizontalAlignment",
            "Margin",
            "MinHeight",
            "MinWidth",
            "Padding",
            "VerticalAlignment",
            "Width",
        };
        Assert.IsFalse(
            textStyle.Descendants(Presentation + "DataTrigger")
                .Descendants(Presentation + "Setter")
                .Any(setter => geometryProperties.Contains((string?)setter.Attribute("Property") ?? string.Empty)),
            "Semantic status triggers may change color, not badge geometry.");
    }

    [TestMethod]
    public void SettingsRequiresPresentModButNotHealthyModProvenance()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/STFCCommunityMod.Launcher/MainWindow.xaml.cs"));
        var method = Slice(
            source,
            "private bool EnsureSettingsWorkspaceInitialized()",
            "private SettingsViewModel CreateSettingsViewModel");

        StringAssert.Contains(method, "HasUnsafeModDeploymentTransaction");
        StringAssert.Contains(method, "Path.Combine(viewModel.SelectedGameDirectory, \"version.dll\")");
        StringAssert.Contains(method, "Community Mod is not installed");
        Assert.IsTrue(
            method.IndexOf("version.dll", StringComparison.Ordinal)
            < method.IndexOf("if (isSettingsWorkspaceInitialized)", StringComparison.Ordinal),
            "An initialized Settings workspace must not bypass a later mod removal.");
        Assert.IsFalse(
            method.Contains("ManagedVerified", StringComparison.Ordinal),
            "A present proxy must not require managed provenance before Settings can open.");
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

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        return source[start..end];
    }

    private static void AssertStyleSetter(XElement style, string property, string value)
    {
        Assert.IsTrue(
            style.Elements(Presentation + "Setter").Any(
                setter =>
                    (string?)setter.Attribute("Property") == property
                    && (string?)setter.Attribute("Value") == value),
            $"The shared style must set {property} to {value}.");
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

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the launcher repository root.");
    }
}
