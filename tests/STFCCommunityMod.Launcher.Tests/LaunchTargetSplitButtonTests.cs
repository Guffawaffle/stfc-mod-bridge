using System.Xml.Linq;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class LaunchTargetSplitButtonTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [TestMethod]
    public void CompoundControlExposesDistinctPrimaryAndMenuButtons()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Controls/LaunchTargetSplitButton.xaml");
        var buttons = document.Descendants(Presentation + "Button").ToArray();
        var primary = buttons.Single(element => GetName(element) == "PrimaryButton");
        var menu = buttons.Single(element => GetName(element) == "MenuButton");

        Assert.AreEqual("44", (string?)primary.Attribute("MinHeight"));
        Assert.AreEqual("44", (string?)menu.Attribute("Width"));
        Assert.AreEqual("{Binding PrimaryCommand, ElementName=Root}", (string?)primary.Attribute("Command"));
        Assert.AreEqual("Choose game launch target", (string?)menu.Attribute(Automation + "AutomationProperties.Name"));
        Assert.AreEqual("MenuButton_Click", (string?)menu.Attribute("Click"));
    }

    [TestMethod]
    public void PopupIsLeftAlignedAndOffersExactTargetCopyWithSelectionState()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Controls/LaunchTargetSplitButton.xaml");
        var popup = document.Descendants(Presentation + "Popup").Single();
        var text = document.Descendants(Presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(value => value is not null)
            .ToArray();

        Assert.AreEqual("Bottom", (string?)popup.Attribute("Placement"));
        Assert.AreEqual("{Binding ElementName=Root}", (string?)popup.Attribute("PlacementTarget"));
        CollectionAssert.Contains(text, "Launch prime.exe");
        CollectionAssert.Contains(text, "Open Scopely launcher");
        Assert.AreEqual(2, text.Count(value => value == "✓"));
        Assert.IsTrue(
            document.Descendants(Presentation + "TextBlock")
                .Any(element => (string?)element.Attribute("Text")
                    == "{Binding PrimeChoiceStatus, ElementName=Root}"));
    }

    [TestMethod]
    public void KeyboardImplementationHandlesNavigationDismissalAndFocusReturn()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src/STFCCommunityMod.Launcher/Controls/LaunchTargetSplitButton.xaml.cs"));

        StringAssert.Contains(source, "Key.Escape");
        StringAssert.Contains(source, "Key.Down");
        StringAssert.Contains(source, "Key.Up");
        StringAssert.Contains(source, "Key.Home");
        StringAssert.Contains(source, "Key.End");
        StringAssert.Contains(source, "Keyboard.Focus(MenuButton)");
    }

    [TestMethod]
    public void HomeFeedbackRaisesLiveRegionChangesForLaunchTransitions()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var feedback = document.Descendants(Presentation + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "{Binding HomeOperationFeedback}");

        Assert.IsTrue(
            feedback.Attributes().Any(attribute =>
                attribute.Name.LocalName == "LiveRegionBehavior.Announcement"
                && attribute.Value == "{Binding HomeOperationFeedback}"));
    }

    [TestMethod]
    public void ChoiceAvailabilityProjectsStructuredCoreReasonAndNextAction()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src/STFCCommunityMod.Launcher/ViewModels/MainWindowViewModel.cs"));

        StringAssert.Contains(source, "choice.Reason");
        StringAssert.Contains(source, "choice.NextActionLabel");
        Assert.IsFalse(source.Contains("See Diagnostics", StringComparison.Ordinal));
    }

    private static XDocument LoadXaml(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), relativePath));

    private static string? GetName(XElement element) =>
        element.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == "Name")?.Value;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the launcher repository root.");
    }
}
