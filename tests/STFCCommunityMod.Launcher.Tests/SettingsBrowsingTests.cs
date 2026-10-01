using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using STFCCommunityMod.Launcher.Controls;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;
using STFCCommunityMod.Launcher.Views;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class SettingsBrowsingTests
{
    [TestMethod]
    [DataRow(LauncherColorMode.Dark)]
    [DataRow(LauncherColorMode.Light)]
    public void UnsupportedConfigurationKeepsActualListScrollableThemedAndHelpEnabled(LauncherColorMode mode)
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            LauncherThemeManager.ApplyColorMode(mode);
            var view = new SettingsView { DataContext = fixture.ViewModel };
            Layout(view);
            Assert.AreEqual(((SolidColorBrush)Application.Current.Resources["WindowBackgroundBrush"]).Color,
                ((SolidColorBrush)view.Background).Color, "The rendered view must use the selected palette.");
            var list = (ListBox)view.FindName("SettingsList");
            Assert.IsTrue(list.IsEnabled, "The read-only list must remain interactive.");
            var scroll = (ScrollViewer)list.Template.FindName("PART_ScrollViewer", list);
            Assert.IsNotNull(scroll);
            Assert.IsTrue(scroll.IsEnabled);
            Assert.IsTrue(scroll.ScrollableHeight > 0, "The actual Graphics viewport must have scrollable content.");
            scroll.ScrollToVerticalOffset(200);
            Layout(view);
            Assert.IsTrue(scroll.VerticalOffset > 0, "Blocked editing must not block scrolling.");
            var surface = (Border)VisualTreeHelper.GetChild(list, 0);
            Assert.AreEqual(0, ((SolidColorBrush)surface.Background).Color.A,
                "The realized list template must not paint a system-white disabled surface.");
            Assert.IsTrue(Descendants<HelpFlyoutButton>(list).Any(help => help.IsEnabled));
            var editors = Descendants<TextBox>(list).ToArray();
            Assert.IsTrue(editors.Length > 0);
            var realizedEditors = 0;
            foreach (var editor in editors)
            {
                Assert.IsFalse(editor.IsEnabled);
                if (VisualTreeHelper.GetChildrenCount(editor) == 0) continue;
                realizedEditors++;
                var border = (Border)VisualTreeHelper.GetChild(editor, 0);
                Assert.AreEqual(((SolidColorBrush)editor.Background).Color, ((SolidColorBrush)border.Background).Color);
            }
            Assert.IsTrue(realizedEditors > 0, "The visible numeric editors must have realized their templates.");
            Assert.IsFalse(fixture.ViewModel.SaveCommand.CanExecute(null));
            var bitmap = new RenderTargetBitmap(960, 620, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var output = Path.Combine(RepositoryRoot(), "artifacts", $"settings-browse-{mode}.png");
            using (var stream = File.Create(output)) encoder.Save(stream);
            CollectionAssert.AreEqual(fixture.OriginalBytes, File.ReadAllBytes(fixture.Path));
        });
    }

    [TestMethod]
    public void UnsupportedDataSyncKeepsItsScrollerAndHelpEnabledWhileFormsRemainBlocked()
    {
        RunSta(() =>
        {
            using var fixture = new Fixture();
            var view = new SyncView { DataContext = fixture.ViewModel.SyncWorkspace };
            Layout(view);
            var scroll = (ScrollViewer)view.FindName("PageScrollViewer");
            Assert.IsTrue(scroll.IsEnabled);
            Assert.IsTrue(Descendants<HelpFlyoutButton>(view).Any(help => help.IsEnabled));
            foreach (var editor in Descendants<TextBox>(view).Where(editor => editor.IsVisible))
                Assert.IsFalse(editor.IsEnabled, "Read-only Sync forms must stay blocked.");
            Assert.IsFalse(fixture.ViewModel.SyncWorkspace.OpenAddDestinationCommand.CanExecute(null));
            Assert.IsFalse(fixture.ViewModel.SyncWorkspace.SaveCommand.CanExecute(null));
            CollectionAssert.AreEqual(fixture.OriginalBytes, File.ReadAllBytes(fixture.Path));
        });
    }

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(960, 620));
        view.Arrange(new Rect(0, 0, 960, 620));
        view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        view.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static void RunSta(Action action)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINDIR")))
            Environment.SetEnvironmentVariable("WINDIR", Environment.GetEnvironmentVariable("SystemRoot"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new App();
                if (!app.Resources.Contains("InAppDialogStyle")) ((App)app).InitializeComponent();
                action();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "WPF browsing regression timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bridge-readonly-" + Guid.NewGuid().ToString("N") + ".toml");
        public byte[] OriginalBytes { get; } = Encoding.UTF8.GetBytes("# unsupported fixture\n\"key.with.dot\" = \"private-sentinel\"\n");
        public SettingsViewModel ViewModel { get; }
        public Fixture()
        {
            File.WriteAllBytes(Path, OriginalBytes);
            using var schema = typeof(SettingsViewModel).Assembly.GetManifestResourceStream("STFCCommunityMod.Launcher.Schemas.Guffawaffle.v1.json")!;
            var catalog = LauncherConfigurationSchemaLoader.Load(schema);
            var layout = new PrincipalCatalogSettingsLayoutProvider();
            var command = new TestCommand();
            ViewModel = new(catalog, command, command, () => Path, layout,
                new("Guffawaffle test", "Active", "Test fixture", layout.DisplayName));
            ViewModel.Sections.Single(section => section.Id == LauncherSettingsSection.Graphics).SelectCommand.Execute(null);
        }
        public void Dispose() => File.Delete(Path);
    }

    private sealed class TestCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
