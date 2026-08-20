using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using STFCCommunityMod.Launcher.Controls;
using STFCCommunityMod.Launcher.Views;

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
        Assert.AreEqual("40", values["SettingsNumericUnitColumnWidth"]);
        Assert.AreEqual("44", values["SettingsRowPrimaryActionColumnWidth"]);
    }

    [TestMethod]
    public void NumericColumnGeometryResourcesLoadAsGridLengths()
    {
        RunInSta(
            () =>
            {
                var application = Application.Current ?? new App();
                if (!application.Resources.Contains("SettingsNumericUnitColumnWidth"))
                {
                    ((App)application).InitializeComponent();
                }

                Assert.IsInstanceOfType(
                    application.Resources["SettingsNumericUnitColumnWidth"],
                    typeof(GridLength));
                Assert.IsInstanceOfType(
                    application.Resources["SettingsRowPrimaryActionColumnWidth"],
                    typeof(GridLength));
            });
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
        Assert.AreEqual("{StaticResource SettingsNumericUnitColumnWidth}", columns[1]);
        Assert.AreEqual("{StaticResource SettingsRowPrimaryActionColumnWidth}", columns[2]);
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

    [TestMethod]
    public void ReleaseSourceMetadataIsBoundedAndRetainsItsFullAccessibleIdentity()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Views/SettingsView.xaml");
        var metadata = document.Descendants(Presentation + "StackPanel")
            .Single(element => GetName(element) == "ReleaseSourceMetadata");
        var identity = document.Descendants(Presentation + "TextBlock")
            .Single(element => GetName(element) == "ReleaseSourceIdentityText");

        Assert.AreEqual("240", (string?)metadata.Attribute("MaxWidth"));
        Assert.AreEqual("240", (string?)identity.Attribute("MaxWidth"));
        Assert.AreEqual("Wrap", (string?)identity.Attribute("TextWrapping"));
        Assert.AreEqual("CharacterEllipsis", (string?)identity.Attribute("TextTrimming"));
        Assert.AreEqual("{Binding SourceIdentity}", (string?)identity.Attribute("ToolTip"));
        Assert.AreEqual(
            "{Binding SourceIdentity, StringFormat='Release source, {0}'}",
            (string?)identity.Attribute(Automation + "AutomationProperties.Name"));
        Assert.AreEqual(
            "{Binding SourceIdentity}",
            (string?)identity.Attribute(Automation + "AutomationProperties.HelpText"));
    }

    [TestMethod]
    public void LongReleaseSourceIdentityStaysInsideMinimumSettingsWorkspace()
    {
        RunInSta(
            () =>
            {
                EnsureApplicationResources();
                var view = new SettingsView
                {
                    DataContext = new SettingsHeaderLayoutModel(),
                };
                view.Measure(new Size(960, 620));
                view.Arrange(new Rect(0, 0, 960, 620));
                view.UpdateLayout();

                var heading = (FrameworkElement)view.FindName("SettingsWorkspaceHeading");
                var metadata = (FrameworkElement)view.FindName("ReleaseSourceMetadata");
                var identity = (TextBlock)view.FindName("ReleaseSourceIdentityText");
                var headingBounds = BoundsWithin(heading, view);
                var metadataBounds = BoundsWithin(metadata, view);
                var identityBounds = BoundsWithin(identity, view);

                Assert.IsTrue(metadata.ActualWidth <= 240);
                Assert.IsTrue(identity.ActualWidth <= 240);
                Assert.IsTrue(
                    headingBounds.Right <= metadataBounds.Left,
                    $"Heading right {headingBounds.Right} overlaps metadata left {metadataBounds.Left}.");
                Assert.IsTrue(metadataBounds.Left >= 200);
                Assert.IsTrue(metadataBounds.Right <= view.ActualWidth);
                Assert.IsTrue(identityBounds.Right <= view.ActualWidth);
            });
    }

    [TestMethod]
    public void CleanupDialogUsesAResponsiveWideViewportWithFixedActions()
    {
        var application = LoadXaml("src/STFCCommunityMod.Launcher/App.xaml");
        var dialogStyle = application.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "InAppDialogStyle");
        var template = dialogStyle.Descendants(Presentation + "ControlTemplate").Single();
        var shell = template.Descendants(Presentation + "Border").First();
        var rows = shell.Descendants(Presentation + "Grid.RowDefinitions")
            .First()
            .Elements(Presentation + "RowDefinition")
            .Select(element => (string?)element.Attribute("Height"))
            .ToArray();
        var presenter = template.Descendants(Presentation + "ContentPresenter").Single();

        Assert.AreEqual("{TemplateBinding DialogWidth}", (string?)shell.Attribute("MaxWidth"));
        Assert.IsNull(shell.Attribute("Width"));
        Assert.AreEqual("Stretch", (string?)shell.Attribute("HorizontalAlignment"));
        Assert.AreEqual("{TemplateBinding DialogMaxHeight}", (string?)shell.Attribute("MaxHeight"));
        Assert.AreEqual("Auto,*,Auto", string.Join(',', rows));
        Assert.AreEqual("Stretch", (string?)presenter.Attribute("VerticalAlignment"));

        var window = LoadXaml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var cleanup = window.Descendants()
            .Single(element => GetName(element) == "ConfigurationCleanupDialog");
        var content = cleanup.Elements(Presentation + "Grid").Single();
        var binding = cleanup.Descendants(Presentation + "TextBlock")
            .Single(element => GetName(element) == "ConfigurationCleanupBinding");
        var scroller = cleanup.Descendants(Presentation + "ScrollViewer").Single();
        var scrollBarStyle = scroller.Descendants(Presentation + "Style").Single();

        Assert.AreEqual("760", (string?)cleanup.Attribute("DialogWidth"));
        Assert.AreEqual("680", (string?)cleanup.Attribute("DialogMaxHeight"));
        Assert.IsNull(content.Attribute("MinWidth"));
        Assert.IsNull(content.Attribute("MaxHeight"));
        Assert.AreEqual("CharacterEllipsis", (string?)binding.Attribute("TextTrimming"));
        Assert.AreEqual("NoWrap", (string?)binding.Attribute("TextWrapping"));
        Assert.AreEqual("Disabled", (string?)scroller.Attribute("HorizontalScrollBarVisibility"));
        Assert.AreEqual("Auto", (string?)scroller.Attribute("VerticalScrollBarVisibility"));
        Assert.AreEqual(
            "{StaticResource DiagnosticsScrollBarStyle}",
            (string?)scrollBarStyle.Attribute("BasedOn"));
        Assert.AreEqual("{x:Type ScrollBar}", (string?)scrollBarStyle.Attribute("TargetType"));
    }

    [TestMethod]
    [DataRow(720d)]
    [DataRow(480d)]
    public void WideDialogClampsInsideSupportedWorkspaceAndKeepsActionsVisible(double hostWidth)
    {
        RunInSta(
            () =>
            {
                EnsureApplicationResources();
                var operations = new StackPanel();
                for (var index = 0; index < 84; index++)
                {
                    operations.Children.Add(new TextBlock
                    {
                        Text = $"Move alias {index} to a deliberately long canonical setting name",
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                var cancel = new Button { Content = "Cancel" };
                var apply = new Button { Content = "Apply reviewed cleanup" };
                var actions = new WrapPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, apply },
                };
                var content = new Grid();
                content.RowDefinitions.Add(new() { Height = GridLength.Auto });
                content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
                content.RowDefinitions.Add(new() { Height = GridLength.Auto });
                content.Children.Add(new TextBlock
                {
                    Text = "Review configuration cleanup",
                    TextWrapping = TextWrapping.Wrap,
                });
                var scroller = new ScrollViewer
                {
                    Content = operations,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
                Grid.SetRow(scroller, 1);
                content.Children.Add(scroller);
                Grid.SetRow(actions, 2);
                content.Children.Add(actions);
                var dialog = new InAppDialog
                {
                    Style = (Style)Application.Current.Resources["InAppDialogStyle"],
                    DialogTitle = "Review configuration cleanup",
                    DialogWidth = 760,
                    DialogMaxHeight = 680,
                    Content = content,
                    IsOpen = true,
                };
                var host = new Grid();
                host.Children.Add(dialog);
                host.Measure(new Size(hostWidth, 480));
                host.Arrange(new Rect(0, 0, hostWidth, 480));
                host.UpdateLayout();

                var shell = VisualDescendants<Border>(dialog).First();
                var shellBounds = BoundsWithin(shell, host);
                Assert.IsTrue(shellBounds.Left >= 0, $"Dialog left edge was {shellBounds.Left}.");
                Assert.IsTrue(shellBounds.Right <= hostWidth, $"Dialog right edge was {shellBounds.Right}.");
                Assert.IsTrue(shellBounds.Top >= 0, $"Dialog top edge was {shellBounds.Top}.");
                Assert.IsTrue(shellBounds.Bottom <= 480, $"Dialog bottom edge was {shellBounds.Bottom}.");
                foreach (var button in VisualDescendants<Button>(dialog))
                {
                    var bounds = BoundsWithin(button, host);
                    Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= hostWidth);
                    Assert.IsTrue(bounds.Top >= 0 && bounds.Bottom <= 480);
                }
            });
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

    private static void EnsureApplicationResources()
    {
        var application = Application.Current ?? new App();
        if (!application.Resources.Contains("InAppDialogStyle"))
        {
            ((App)application).InitializeComponent();
        }
    }

    private static Rect BoundsWithin(FrameworkElement element, Visual ancestor)
    {
        var origin = element.TransformToAncestor(ancestor).Transform(new Point());
        return new(origin, new Size(element.ActualWidth, element.ActualHeight));
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in VisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class SettingsHeaderLayoutModel
    {
        public bool IsSettingsListVisible { get; } = true;

        public bool IsGeneralSelected { get; } = true;

        public bool IsAdvancedSelected { get; }

        public string WorkspaceTitle { get; } = "General";

        public string WorkspaceDescription { get; } = "Core mod behavior and ordinary preferences.";

        public string VisibleItemsSummary { get; } =
            "7 settings shown · Changes are staged until you save.";

        public string SourceIdentity { get; } =
            "Guffawaffle Community Mod stable release source with an intentionally very long identity";
    }

    private static void RunInSta(Action action)
    {
        var originalWindir = Environment.GetEnvironmentVariable(
            "WINDIR",
            EnvironmentVariableTarget.Process);
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
                        action();
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The resource realization test timed out.");

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(originalWindir))
            {
                Environment.SetEnvironmentVariable(
                    "WINDIR",
                    null,
                    EnvironmentVariableTarget.Process);
            }
        }
    }
}
