using System.Runtime.ExceptionServices;
using System.Windows;
using System.Xml.Linq;
using STFCCommunityMod.Launcher.Controls;

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
        Assert.IsTrue(menu.Descendants().Any(element =>
            element.Name.LocalName == "AppIcon"
            && (string?)element.Attribute("Kind") == "ChevronDown"));
        Assert.IsFalse(menu.Descendants(Presentation + "TextBlock")
            .Any(element => (string?)element.Attribute("Text") == "⌄"));
    }

    [TestMethod]
    public void PopupIsRightOwnedCompactAndOffersExactTargetCopyWithSelectionState()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Controls/LaunchTargetSplitButton.xaml");
        var popup = document.Descendants(Presentation + "Popup").Single();
        var text = document.Descendants(Presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(value => value is not null)
            .ToArray();

        Assert.AreEqual("Custom", (string?)popup.Attribute("Placement"));
        Assert.AreEqual("PlaceChoicePopup", (string?)popup.Attribute("CustomPopupPlacementCallback"));
        Assert.AreEqual("{Binding ElementName=MenuButton}", (string?)popup.Attribute("PlacementTarget"));
        var popupSurface = document.Descendants(Presentation + "Border")
            .Single(element => GetName(element) == "ChoiceMenuSurface");
        Assert.AreEqual("320", (string?)popupSurface.Attribute("Width"));
        CollectionAssert.Contains(text, "Launch prime.exe");
        CollectionAssert.Contains(text, "Open Scopely launcher");
        Assert.AreEqual(2, text.Count(value => value == "✓"));
        Assert.IsTrue(
            document.Descendants(Presentation + "TextBlock")
                .Any(element => (string?)element.Attribute("Text")
                    == "{Binding PrimeChoiceStatus, ElementName=Root}"));
        var choiceButtons = document.Descendants(Presentation + "Button")
            .Where(element => GetName(element) is "PrimeChoiceButton" or "ScopelyChoiceButton")
            .ToArray();
        Assert.IsTrue(choiceButtons.All(element =>
            (string?)element.Attribute("Style") == "{StaticResource LaunchChoiceStyle}"));
        Assert.IsTrue(choiceButtons.All(element => element.Attribute("BorderThickness") is null));
        Assert.AreEqual(0, popupSurface.Descendants(Presentation + "Border").Count());
    }

    [TestMethod]
    public void SharedButtonTemplateHonorsRequestedContentAlignment()
    {
        var app = LoadXaml("src/STFCCommunityMod.Launcher/App.xaml");
        var secondaryStyle = app.Descendants(Presentation + "Style")
            .Single(style => style.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" && attribute.Value == "SecondaryButtonStyle"));
        var presenter = secondaryStyle.Descendants(Presentation + "ContentPresenter").Single();

        Assert.AreEqual(
            "{TemplateBinding HorizontalContentAlignment}",
            (string?)presenter.Attribute("HorizontalAlignment"));
        Assert.AreEqual(
            "{TemplateBinding VerticalContentAlignment}",
            (string?)presenter.Attribute("VerticalAlignment"));
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

    [TestMethod]
    public void HomeUsesResponsiveSemanticSectionsWithNaturalActionMeasurement()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var workspace = document.Descendants(Presentation + "StackPanel")
            .Single(element => GetName(element) == "HomeWorkspace");
        var statusSurface = document.Descendants(Presentation + "Border")
            .Single(element => GetName(element) == "HomeStatusSurface");
        var gameSection = statusSurface.Descendants(Presentation + "Grid")
            .Single(element => GetName(element) == "GameStatusSection");
        var modSection = statusSurface.Descendants(Presentation + "Grid")
            .Single(element => GetName(element) == "CommunityModStatusSection");

        Assert.IsNull(workspace.Attribute("Width"));
        Assert.AreEqual("680", (string?)workspace.Attribute("MaxWidth"));
        Assert.AreEqual("Stretch", (string?)workspace.Attribute("HorizontalAlignment"));
        Assert.IsTrue(HasText(gameSection, "Star Trek Fleet Command"));
        Assert.IsTrue(HasText(modSection, "Community Mod"));
        Assert.IsTrue(gameSection.Descendants(Presentation + "TextBlock")
            .Any(element => (string?)element.Attribute("Text") == "{Binding GameSectionStatus}"));
        Assert.IsTrue(modSection.Descendants(Presentation + "TextBlock")
            .Any(element => (string?)element.Attribute("Text") == "{Binding ModStatus}"));
        Assert.IsTrue(gameSection.Descendants(Presentation + "Button")
            .Any(element => (string?)element.Attribute("Click") == "ChooseGameFolderButton_Click"));
        Assert.IsTrue(gameSection.Descendants()
            .Any(element => element.Name.LocalName == "LaunchTargetSplitButton"));
        Assert.IsTrue(modSection.Descendants(Presentation + "Button")
            .Any(element => (string?)element.Attribute("Click") == "ModActionButton_Click"));
        Assert.IsTrue(modSection.Descendants(Presentation + "Button")
            .Any(element => GetName(element) == "ReleaseSourceButton"));
        Assert.IsTrue(gameSection.Descendants(Presentation + "Button")
            .Concat(modSection.Descendants(Presentation + "Button"))
            .All(element => element.Attribute("Width") is null));
    }

    [TestMethod]
    public void RefreshLivesInStatusHeaderAndProviderMetadataHasNoSourcePrefix()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var refresh = document.Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute("Command") == "{Binding RefreshCommand}");
        var source = document.Descendants(Presentation + "Button")
            .Single(element => GetName(element) == "ReleaseSourceButton");

        Assert.AreEqual("36", (string?)refresh.Attribute("Width"));
        Assert.AreEqual("{Binding ModSourceMetadata}", (string?)source.Attribute("Content"));
        Assert.AreEqual(
            "{Binding ModSourceMetadata, StringFormat=Community mod release source: {0}}",
            (string?)source.Attribute(Automation + "AutomationProperties.Name"));
        StringAssert.Contains(
            (string?)source.Attribute(Automation + "AutomationProperties.HelpText"),
            "Detected installed lineage");
        Assert.IsFalse(document.ToString().Contains("Source: {", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HomeUsesModControlIdentityAndAnnouncesGameStatusOnce()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var productTitle = document.Descendants(Presentation + "TextBlock")
            .Single(element => GetName(element) == "ProductTitleText");
        var gameSection = document.Descendants(Presentation + "Grid")
            .Single(element => GetName(element) == "GameStatusSection");
        var gameStatusBindings = gameSection.Descendants()
            .Where(element =>
                (string?)element.Attribute(Automation + "AutomationProperties.Name")
                == "{Binding GameSectionStatus}")
            .ToArray();
        var decorativeGlyph = gameSection.Descendants(Presentation + "Viewbox")
            .Single(element => GetName(element) == "GameStatusGlyph");

        Assert.AreEqual("STFC Mod Control", (string?)productTitle.Attribute("Text"));
        Assert.AreEqual(1, gameStatusBindings.Length);
        Assert.IsNull(decorativeGlyph.Attribute(Automation + "AutomationProperties.Name"));
        Assert.IsFalse(decorativeGlyph.Descendants().Any(element =>
            element.Attribute(Automation + "AutomationProperties.Name") is not null));
        Assert.IsFalse(gameSection.Descendants(Presentation + "TextBlock")
            .Any(element => (string?)element.Attribute("Text") == "{Binding GameFolderIcon}"));
    }

    [TestMethod]
    public void PopupPlacementAlignsRightAndFallsBackAboveItsOwner()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src/STFCCommunityMod.Launcher/Controls/LaunchTargetSplitButton.xaml.cs"));

        StringAssert.Contains(source, "targetSize.Width - popupSize.Width");
        StringAssert.Contains(source, "targetSize.Height + 4");
        StringAssert.Contains(source, "-popupSize.Height - 4");
    }

    [TestMethod]
    public void ControlBamlConstructsWithCustomPopupPlacementCallback()
    {
        var originalWindir = Environment.GetEnvironmentVariable("WINDIR", EnvironmentVariableTarget.Process);
        if (string.IsNullOrWhiteSpace(originalWindir))
        {
            Environment.SetEnvironmentVariable(
                "WINDIR",
                Environment.GetEnvironmentVariable("SystemRoot", EnvironmentVariableTarget.Process),
                EnvironmentVariableTarget.Process);
        }

        Exception? failure = null;
        try
        {
            var thread = new Thread(
                () =>
                {
                    try
                    {
                        var application = Application.Current ?? new App();
                        if (application is App app)
                        {
                            app.InitializeComponent();
                        }

                        _ = new LaunchTargetSplitButton();
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The WPF construction test timed out.");

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(originalWindir))
            {
                Environment.SetEnvironmentVariable("WINDIR", null, EnvironmentVariableTarget.Process);
            }
        }
    }

    private static XDocument LoadXaml(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), relativePath));

    private static string? GetName(XElement element) =>
        element.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == "Name")?.Value;

    private static bool HasText(XElement element, string text) =>
        element.Descendants(Presentation + "TextBlock")
            .Any(candidate => (string?)candidate.Attribute("Text") == text);

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
