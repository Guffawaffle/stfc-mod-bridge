using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using STFCCommunityMod.Launcher.Behaviors;
using STFCCommunityMod.Launcher.Views;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class KeepVisibleAboveFooterBehaviorTests
{
    private static readonly TimeSpan WpfConstructionTimeout = TimeSpan.FromSeconds(30);
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Behaviors =
        "clr-namespace:STFCCommunityMod.Launcher.Behaviors";

    [TestMethod]
    public void FullyVisibleAnchorDoesNotMove()
    {
        var target = KeepVisibleAboveFooterBehavior.CalculateTargetOffset(
            currentOffset: 200,
            scrollableHeight: 1000,
            viewportHeight: 300,
            anchorTop: 40,
            anchorHeight: 44);

        Assert.AreEqual(200, target);
    }

    [TestMethod]
    public void BottomClippedAnchorMovesOnlyTheMissingDistance()
    {
        var target = KeepVisibleAboveFooterBehavior.CalculateTargetOffset(
            currentOffset: 200,
            scrollableHeight: 1000,
            viewportHeight: 300,
            anchorTop: 270,
            anchorHeight: 44);

        Assert.AreEqual(226, target);
    }

    [TestMethod]
    public void TopClippedAnchorMovesOnlyTheMissingDistance()
    {
        var target = KeepVisibleAboveFooterBehavior.CalculateTargetOffset(
            currentOffset: 200,
            scrollableHeight: 1000,
            viewportHeight: 300,
            anchorTop: 5,
            anchorHeight: 44);

        Assert.AreEqual(193, target);
    }

    [TestMethod]
    public void AdjustmentClampsAtBothScrollBoundaries()
    {
        var top = KeepVisibleAboveFooterBehavior.CalculateTargetOffset(
            currentOffset: 3,
            scrollableHeight: 1000,
            viewportHeight: 300,
            anchorTop: 0,
            anchorHeight: 44);
        var bottom = KeepVisibleAboveFooterBehavior.CalculateTargetOffset(
            currentOffset: 990,
            scrollableHeight: 1000,
            viewportHeight: 300,
            anchorTop: 280,
            anchorHeight: 44);

        Assert.AreEqual(0, top);
        Assert.AreEqual(1000, bottom);
    }

    [TestMethod]
    public void ConstrainedViewportRetainsTheSafeMargin()
    {
        var target = KeepVisibleAboveFooterBehavior.CalculateTargetOffset(
            currentOffset: 100,
            scrollableHeight: 1000,
            viewportHeight: 80,
            anchorTop: 50,
            anchorHeight: 44);

        Assert.AreEqual(126, target);
    }

    [DataTestMethod]
    [DataRow(false, true, true)]
    [DataRow(true, true, false)]
    [DataRow(true, false, false)]
    [DataRow(false, false, false)]
    public void AdjustmentRunsOnlyWhenFooterFirstAppears(
        bool wasVisible,
        bool isVisible,
        bool expected)
    {
        Assert.AreEqual(
            expected,
            KeepVisibleAboveFooterBehavior.ShouldAdjustForFooterTransition(wasVisible, isVisible));
    }

    [TestMethod]
    public void SettingsListDeclaresTheReusableFooterRelationship()
    {
        var document = LoadXaml("src/STFCCommunityMod.Launcher/Views/SettingsView.xaml");
        var settingsList = document.Descendants(Presentation + "ListBox")
            .Single(element => GetName(element) == "SettingsList");
        var footer = document.Descendants(Presentation + "Border")
            .Single(element => GetName(element) == "SettingsFooter");

        Assert.AreEqual(
            "{Binding ElementName=SettingsFooter}",
            (string?)settingsList.Attribute(Behaviors + "KeepVisibleAboveFooterBehavior.Footer"));
        Assert.AreEqual(
            "{Binding IsSettingsFooterVisible, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)footer.Attribute("Visibility"));
    }

    [TestMethod]
    public void SettingsViewConstructsWithItsFooterRelationshipResolved()
    {
        RunInSta(
            () =>
            {
                var application = Application.Current ?? new App();
                if (application is App app)
                {
                    app.InitializeComponent();
                }

                var view = new SettingsView();
                view.Measure(new Size(960, 620));
                view.Arrange(new Rect(0, 0, 960, 620));
                view.UpdateLayout();
                var list = (ListBox)view.FindName("SettingsList");
                var footer = (FrameworkElement)view.FindName("SettingsFooter");

                Assert.AreSame(footer, KeepVisibleAboveFooterBehavior.GetFooter(list));
            });
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

    private static void RunInSta(Action action)
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
                        action();
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(WpfConstructionTimeout), "The WPF construction test timed out.");

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
}
