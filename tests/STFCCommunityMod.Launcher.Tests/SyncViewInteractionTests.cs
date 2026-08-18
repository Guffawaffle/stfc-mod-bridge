using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;
using STFCCommunityMod.Launcher.Views;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class SyncViewInteractionTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [TestMethod]
    public void ReloadedViewRestoresItsViewModelSubscription()
    {
        var originalWindir = Environment.GetEnvironmentVariable("WINDIR", EnvironmentVariableTarget.Process);
        if (string.IsNullOrWhiteSpace(originalWindir))
        {
            Environment.SetEnvironmentVariable(
                "WINDIR",
                Environment.GetEnvironmentVariable("SystemRoot", EnvironmentVariableTarget.Process),
                EnvironmentVariableTarget.Process);
        }

        using var fixture = TemporarySyncFixture.Create();
        var viewModel = new SyncWorkspaceViewModel(
            () => fixture.ConfigurationPath,
            new TomlConfigurationRepository());
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

                        var view = new SyncView { DataContext = viewModel };
                        var subscriptionField = typeof(SyncView).GetField(
                            "subscribedViewModel",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        Assert.IsNotNull(subscriptionField);

                        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                        Assert.AreSame(viewModel, subscriptionField.GetValue(view));

                        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                        Assert.IsNull(subscriptionField.GetValue(view));

                        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                        Assert.AreSame(
                            viewModel,
                            subscriptionField.GetValue(view),
                            "Returning to Data Sync must restore focus and tab-overflow change handling.");
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The WPF lifecycle test timed out.");

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

    [TestMethod]
    public void DataSyncLayoutKeepsOnePageVerticalScrollerAndAHiddenTabOffset()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Views/SyncView.xaml");
        var scrollViewers = document.Descendants(Presentation + "ScrollViewer").ToArray();
        var page = Named(scrollViewers, "PageScrollViewer");
        var tabs = Named(scrollViewers, "DestinationTabScrollViewer");

        Assert.AreEqual("Auto", (string?)page.Attribute("VerticalScrollBarVisibility"));
        Assert.AreEqual("Disabled", (string?)page.Attribute("HorizontalScrollBarVisibility"));
        Assert.AreEqual("Disabled", (string?)tabs.Attribute("VerticalScrollBarVisibility"));
        Assert.AreEqual("Hidden", (string?)tabs.Attribute("HorizontalScrollBarVisibility"));
        Assert.AreEqual(
            1,
            scrollViewers.Count(element =>
                (string?)element.Attribute("VerticalScrollBarVisibility") == "Auto"),
            "Data Sync must expose one page-level vertical scrolling surface.");
        Assert.IsFalse(
            scrollViewers.Any(element =>
                (string?)element.Attribute("HorizontalScrollBarVisibility") is "Auto" or "Visible"),
            "No Data Sync surface may expose a native horizontal scrollbar.");
    }

    [TestMethod]
    public void DataSyncMinimumWidthLeavesRoomForTheWizardAndResponsiveRows()
    {
        Assert.AreEqual(960d, MainWindow.SettingsMinWidth);
        Assert.AreEqual(620d, MainWindow.SettingsMinHeight);

        var settings = LoadXaml("src/STFCCommunityMod.Launcher/Views/SettingsView.xaml");
        var settingsGrid = settings.Root!.Element(Presentation + "Grid");
        var navigationWidth = double.Parse(
            (string)settingsGrid!
                .Element(Presentation + "Grid.ColumnDefinitions")!
                .Elements(Presentation + "ColumnDefinition")
                .First()
                .Attribute("Width")!,
            System.Globalization.CultureInfo.InvariantCulture);
        var sync = LoadXaml("src/STFCCommunityMod.Launcher/Views/SyncView.xaml");
        var wizardWidth = double.Parse(
            (string)Named(sync.Descendants(Presentation + "Border"), "WizardDialog").Attribute("Width")!,
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.IsTrue(
            wizardWidth <= MainWindow.SettingsMinWidth - navigationWidth,
            "The Add destination wizard must fit beside navigation at the supported minimum width.");
    }

    [TestMethod]
    public void DataSyncFooterKeepsBlockedSaveReasonAndRecoveryVisibleAndAccessible()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Views/SyncView.xaml");
        var blocker = Named(document.Descendants(Presentation + "TextBlock"), "SyncSaveBlockerText");
        var recovery = Named(document.Descendants(Presentation + "Button"), "SyncSaveRecoveryButton");
        var legacy = Named(document.Descendants(Presentation + "CheckBox"), "LegacyMigrationApproval");
        var page = Named(document.Descendants(Presentation + "ScrollViewer"), "PageScrollViewer");
        var blockerBorder = blocker.Ancestors(Presentation + "Border").FirstOrDefault();

        Assert.IsNotNull(blockerBorder);
        Assert.AreEqual("{Binding SaveAvailability}", (string?)blocker.Attribute("Text"));
        Assert.AreEqual("Wrap", (string?)blocker.Attribute("TextWrapping"));
        Assert.AreEqual("Polite", (string?)blocker.Attribute(Automation + "AutomationProperties.LiveSetting"));
        Assert.AreEqual(
            "{Binding IsSaveBlocked, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)blockerBorder.Attribute("Visibility"));
        Assert.AreEqual("{Binding SaveRecoveryCommand}", (string?)recovery.Attribute("Command"));
        Assert.AreEqual(
            "{Binding SaveState.RecoveryActionLabel}",
            (string?)recovery.Attribute(Automation + "AutomationProperties.Name"));
        Assert.AreEqual(
            "{Binding SaveAvailability}",
            (string?)recovery.Attribute(Automation + "AutomationProperties.HelpText"));
        Assert.AreEqual(
            "Approve moving the older sync setup into a named destination",
            (string?)legacy.Attribute(Automation + "AutomationProperties.Name"));
        Assert.AreEqual("{Binding CanEdit}", (string?)page.Attribute("IsEnabled"));
    }

    [TestMethod]
    public void WpfDoesNotDeclareASecondFeedCapabilityMap()
    {
        var sync = LoadXaml("src/STFCCommunityMod.Launcher/Views/SyncView.xaml");
        var literalText = sync.Descendants()
            .Attributes()
            .Where(attribute => attribute.Name.LocalName is "Text" or "Content")
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var feed in SyncTargetTypeCatalog.Feeds.Values)
        {
            Assert.IsFalse(
                literalText.Contains(feed.DisplayName),
                $"Feed '{feed.DisplayName}' must be projected from SyncTargetTypeCatalog, not duplicated in XAML.");
        }
    }

    [TestMethod]
    public void DataSyncUsesSharedHelpAndClickableDestinationActions()
    {
        var sync = LoadXaml("src/STFCCommunityMod.Launcher/Views/SyncView.xaml");
        var help = sync.Descendants().Single(element =>
            element.Name.LocalName == "HelpFlyoutButton"
            && (string?)element.Attribute("AutomationName") == "About Data Sync editing");
        var actions = Named(sync.Descendants(Presentation + "Button"), "DestinationActionsButton");

        Assert.IsNotNull(help);
        Assert.AreEqual("DestinationActionsButton_Click", (string?)actions.Attribute("Click"));
        Assert.IsTrue(actions.Descendants(Presentation + "MenuItem").Any(item =>
            (string?)item.Attribute("Command") == "{Binding RemoveUnsupportedCapabilitiesCommand}"));
    }

    private static XElement Named(IEnumerable<XElement> elements, string name) =>
        elements.Single(element => (string?)element.Attribute(Xaml + "Name") == name);

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

    private sealed class TemporarySyncFixture : IDisposable
    {
        private TemporarySyncFixture(string directory)
        {
            DirectoryPath = directory;
            ConfigurationPath = Path.Combine(directory, "community_patch_settings.toml");
        }

        public string DirectoryPath { get; }
        public string ConfigurationPath { get; }

        public static TemporarySyncFixture Create()
        {
            var fixture = new TemporarySyncFixture(
                Path.Combine(Path.GetTempPath(), "stfc-launcher-sync-view-tests", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(fixture.DirectoryPath);
            File.WriteAllText(
                fixture.ConfigurationPath,
                "# disposable lifecycle fixture\n",
                new UTF8Encoding(false));
            return fixture;
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, true);
            }
        }
    }
}
