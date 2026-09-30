using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class GameInstallationViewModelTests
{
    [TestMethod]
    public async Task CheckedVersionAndStoppedInstallationAreBothRequiredBeforeUpdate()
    {
        using var fixture = new Fixture();
        fixture.State = "running";
        await fixture.ViewModel.CheckAsync();
        StringAssert.Contains(fixture.ViewModel.Status, "221 → 267");
        Assert.IsFalse(fixture.ViewModel.CanUpdate);
        fixture.State = "ready";
        await fixture.ViewModel.RefreshStatusAsync();
        Assert.IsTrue(fixture.ViewModel.CanUpdate);
        await fixture.ViewModel.UpdateAsync();
        Assert.AreEqual(267, fixture.LastMutation!.ExpectedVersion);
        Assert.AreEqual(fixture.Directory, fixture.LastMutation.GameDirectory);
        Assert.IsFalse(fixture.ViewModel.CanUpdate);
        Assert.AreEqual(1, fixture.Completed);
    }

    [TestMethod]
    public async Task ChoosingAnotherInstallationInvalidatesThePreviouslyCheckedUpdate()
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.CheckAsync();
        Assert.IsTrue(fixture.ViewModel.CanUpdate);
        fixture.SelectedDirectory = Path.Combine(fixture.Directory, "different-game");
        fixture.ViewModel.SetTarget(fixture.SelectedDirectory);
        Assert.IsFalse(fixture.ViewModel.CanUpdate);
        await fixture.ViewModel.UpdateAsync();
        Assert.IsNull(fixture.LastMutation);
        Assert.AreEqual(fixture.SelectedDirectory, fixture.ViewModel.Target);
    }

    [TestMethod]
    public async Task UnfinishedJournalExposesRecoveryIndependentlyOfModLaunchReadiness()
    {
        using var fixture = new Fixture();
        fixture.State = "recovery-required";
        fixture.RequiresRecovery = true;
        await fixture.ViewModel.RefreshStatusAsync();
        Assert.IsTrue(fixture.ViewModel.CanRecover);
        Assert.IsFalse(fixture.ViewModel.CanUpdate);
        await fixture.ViewModel.RecoverAsync();
        Assert.AreEqual("recover-game-update", fixture.LastMutation!.Operation);
        Assert.AreEqual(fixture.Directory, fixture.LastMutation.GameDirectory);
    }

    [TestMethod]
    public async Task RestartAfterExecutableBackupStillOffersRecoveryAtTheSavedConfirmedInstallation()
    {
        using var fixture = new Fixture();
        var game = Path.Combine(fixture.Directory, "chosen-game");
        System.IO.Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "prime.exe"), "synthetic executable");
        var state = Path.Combine(fixture.Directory, "selection");
        new GameInstallDiscovery(new JsonGameInstallSelectionStore(state), []).ConfirmManualSelection(game);
        File.Move(Path.Combine(game, "prime.exe"), Path.Combine(game, "backup.exe"));
        var captured = new LauncherEnvironmentProbe(new StoppedInspector(),
            PerUserInstallLayout.FromLocalApplicationData(fixture.Directory),
            new GameInstallDiscovery(new JsonGameInstallSelectionStore(state), [])).Capture();
        Assert.IsNull(captured.SelectedGameDirectory);
        fixture.SelectedDirectory = captured.ConfirmedGameInstallationDirectory!;
        fixture.State = "recovery-required";
        fixture.RequiresRecovery = true;
        await fixture.ViewModel.RefreshStatusAsync();
        Assert.IsTrue(fixture.ViewModel.CanRecover);
        Assert.IsFalse(fixture.ViewModel.CanUpdate);
        await fixture.ViewModel.RecoverAsync();
        Assert.AreEqual(game, fixture.LastMutation!.GameDirectory);
        Assert.AreEqual("recover-game-update", fixture.LastMutation.Operation);
        Assert.AreEqual(1, fixture.Completed);
        Assert.IsFalse(File.Exists(Path.Combine(game, "prime.exe")), "The transport seam proves Bridge routing, not native journal rollback.");
    }

    [TestMethod]
    public async Task ActualGameProgressControlBindsReadOnlyTelemetryWithoutStartupFailure()
    {
        using var fixture = new Fixture { ProgressPercent = 37.5 };
        await fixture.ViewModel.RefreshStatusAsync();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
            directory = directory.Parent;
        Assert.IsNotNull(directory, "Could not locate the launcher repository.");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var progress = XDocument.Load(Path.Combine(directory.FullName, "src/STFCCommunityMod.Launcher/MainWindow.xaml"))
            .Descendants(presentation + "ProgressBar")
            .Single(element => ((string?)element.Attribute("Value"))?.Contains("GameClient.ProgressPercent", StringComparison.Ordinal) == true);
        var controlXaml = new XElement(presentation + "Grid", new XAttribute(XNamespace.Xmlns + "x", xaml),
            new XElement(presentation + "Grid.Resources",
                new XElement(presentation + "BooleanToVisibilityConverter", new XAttribute(xaml + "Key", "BooleanToVisibilityConverter"))),
            new XElement(progress)).ToString();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var root = (Grid)XamlReader.Parse(controlXaml);
                root.DataContext = new { GameClient = fixture.ViewModel };
                root.Measure(new Size(400, 40));
                root.Arrange(new Rect(0, 0, 400, 40));
                root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                var bar = (ProgressBar)root.Children[0];
                Assert.AreEqual(37.5, bar.Value, "The actual control must read updater telemetry.");
                Assert.AreEqual(Visibility.Visible, bar.Visibility);
                fixture.ViewModel.SetTarget(null);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                Assert.AreEqual(0d, bar.Value, "Target invalidation must clear displayed progress.");
                Assert.AreEqual(Visibility.Collapsed, bar.Visibility);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The actual progress binding test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class StoppedInspector : IGameProcessInspector
    {
        public GameProcessInspectionState Inspect(string gameDirectory) => GameProcessInspectionState.NotRunning;
    }

    private sealed class Fixture : IDisposable, IProfileCatalogTransport
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "bridge-game-ui-" + Guid.NewGuid().ToString("N"));
        public string SelectedDirectory { get; set; }
        public string State { get; set; } = "ready";
        public bool RequiresRecovery { get; set; }
        public double? ProgressPercent { get; set; }
        public ProfileCatalogRequest? LastMutation { get; private set; }
        public int Completed { get; private set; }
        public GameInstallationViewModel ViewModel { get; }
        public Fixture()
        {
            SelectedDirectory = Directory;
            ViewModel = new(new GameInstallationCoordinator(Directory, this), () => SelectedDirectory, () => true, () => Completed++);
            ViewModel.SetTarget(SelectedDirectory);
        }
        public ProfileCatalogResponse Request(ProfileCatalogRequest request)
        {
            if (request.Operation is "update-game" or "recover-game-update") LastMutation = request;
            return new(true, Installation: new(request.GameDirectory!, 221, State, "idle", 267, true,
                ProgressPercent: ProgressPercent, RequiresRecovery: RequiresRecovery));
        }
        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
