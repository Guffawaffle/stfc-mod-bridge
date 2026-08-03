using System.Xml.Linq;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class SettingsControlGeometryTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [TestMethod]
    public void NumericGeometryUsesNamedReusableTokens()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/App.xaml");
        var values = document.Descendants()
            .Where(element => element.Attribute(Xaml + "Key") is not null)
            .ToDictionary(
                element => (string)element.Attribute(Xaml + "Key")!,
                element => element.Value,
                StringComparer.Ordinal);

        Assert.AreEqual("84", values["SettingsNumericInputWidthCompact"]);
        Assert.AreEqual("112", values["SettingsNumericInputWidthStandard"]);
        Assert.AreEqual("144", values["SettingsNumericInputWidthWide"]);
        Assert.AreEqual("176", values["SettingsNumericSliderWidth"]);
        Assert.AreEqual("40", values["SettingsNumericUnitWidth"]);
        Assert.AreEqual("44", values["SettingsRowPrimaryActionWidth"]);
    }

    [TestMethod]
    public void BoundedNumericRowsKeepSliderUnitAndActionGeometryStable()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Views/SettingsView.xaml");
        var bounded = document.Descendants(Presentation + "WrapPanel")
            .Single(element => GetName(element) == "BoundedNumericEditor");
        var slider = bounded.Elements(Presentation + "Slider").Single();
        var cluster = bounded.Elements(Presentation + "Grid")
            .Single(element => GetName(element) == "BoundedNumericValueCluster");
        var columns = cluster.Element(Presentation + "Grid.ColumnDefinitions")!
            .Elements(Presentation + "ColumnDefinition")
            .Select(element => (string?)element.Attribute("Width"))
            .ToArray();
        var unit = cluster.Elements(Presentation + "TextBlock").Single();

        Assert.AreEqual(
            "{StaticResource SettingsNumericSliderWidth}",
            (string?)slider.Attribute("Width"));
        Assert.AreEqual(
            "{Binding HasNumericSlider, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)bounded.Attribute("Visibility"));
        Assert.AreEqual(3, columns.Length);
        Assert.AreEqual("Auto", columns[0]);
        Assert.AreEqual("{StaticResource SettingsNumericUnitWidth}", columns[1]);
        Assert.AreEqual("{StaticResource SettingsRowPrimaryActionWidth}", columns[2]);
        Assert.AreEqual("{Binding Unit}", (string?)unit.Attribute("Text"));
        Assert.IsNull(unit.Attribute("Visibility"));
    }

    [TestMethod]
    public void NumericRowsReflowBoundedControlsAndKeepTextOnlyControlsIntentional()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Views/SettingsView.xaml");
        var bounded = document.Descendants(Presentation + "WrapPanel")
            .Single(element => GetName(element) == "BoundedNumericEditor");
        var textOnly = document.Descendants(Presentation + "Grid")
            .Single(element => GetName(element) == "NumericTextOnlyEditor");

        Assert.AreEqual("Stretch", (string?)bounded.Attribute("HorizontalAlignment"));
        Assert.AreEqual("Right", (string?)textOnly.Attribute("HorizontalAlignment"));
        Assert.AreEqual(
            "{Binding IsNumericTextOnly, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)textOnly.Attribute("Visibility"));
    }

    [TestMethod]
    public void HelpAndRestoreShareOnePixelIdenticalPrimaryActionSlot()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Controls/SettingsRowActions.xaml");
        var slot = document.Descendants(Presentation + "Grid")
            .Single(element =>
                (string?)element.Attribute("Width") == "{StaticResource SettingsRowPrimaryActionWidth}");
        var help = slot.Elements().Single(element => element.Name.LocalName == "HelpFlyoutButton");
        var restore = slot.Elements(Presentation + "Button").Single();

        Assert.AreEqual(
            "{StaticResource SettingsRowPrimaryActionWidth}",
            (string?)slot.Attribute("Height"));
        Assert.AreEqual((string?)slot.Attribute("Width"), (string?)help.Attribute("Width"));
        Assert.AreEqual((string?)slot.Attribute("Height"), (string?)help.Attribute("Height"));
        Assert.AreEqual((string?)slot.Attribute("Width"), (string?)restore.Attribute("Width"));
        Assert.AreEqual((string?)slot.Attribute("Height"), (string?)restore.Attribute("Height"));
        Assert.AreEqual(
            "{Binding RevertDraftAutomationName}",
            (string?)restore.Attribute(Automation + "AutomationProperties.Name"));
        Assert.AreEqual(
            "{Binding RevertDraftAutomationHelp}",
            (string?)restore.Attribute(Automation + "AutomationProperties.HelpText"));
        AssertDirtySwap(help, dirtyVisibility: "Collapsed");
        AssertDirtySwap(restore, dirtyVisibility: "Visible");
    }

    private static void AssertDirtySwap(XElement control, string dirtyVisibility)
    {
        var style = control.Elements().Single(element => element.Name.LocalName.EndsWith(".Style", StringComparison.Ordinal));
        var trigger = style.Descendants(Presentation + "DataTrigger")
            .Single(element => (string?)element.Attribute("Binding") == "{Binding IsDirty}");
        var setter = trigger.Elements(Presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == "Visibility");

        Assert.AreEqual("True", (string?)trigger.Attribute("Value"));
        Assert.AreEqual(dirtyVisibility, (string?)setter.Attribute("Value"));
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
