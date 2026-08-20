using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;
using STFCCommunityMod.Launcher.Views;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ProviderConfigurationHistoryViewModelTests
{
    private static readonly JsonSerializerOptions JournalJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [TestMethod]
    public async Task ProductionSettingsProjectionAddsHistoryBeforeAboutAndRefreshesIt()
    {
        using var directory = new TestDirectory();
        var context = await CreateContextAsync(directory);
        var command = new TestCommand();
        var layout = new PrincipalCatalogSettingsLayoutProvider();
        var settings = new SettingsViewModel(
            context.ConfigurationCatalog,
            command,
            command,
            () => context.ConfigurationPath,
            layout,
            new("Guffawaffle test", "Active", "Test fixture", layout.DisplayName),
            configurationHistoryCoordinator: context.Coordinator);

        CollectionAssert.AreEqual(
            new[]
            {
                LauncherSettingsSection.ConfigurationHistory,
                LauncherSettingsSection.About,
            },
            settings.Sections.TakeLast(2).Select(section => section.Id).ToArray());
        settings.Sections.Single(
                section => section.Id == LauncherSettingsSection.ConfigurationHistory)
            .SelectCommand.Execute(null);
        await WaitUntilAsync(() =>
            settings.ConfigurationHistory is { IsBusy: false, Entries.Count: 1 });

        Assert.IsTrue(settings.IsConfigurationHistorySelected);
        Assert.IsFalse(settings.IsSettingsListVisible);
        Assert.AreEqual(
            context.Backup.BackupId,
            settings.ConfigurationHistory!.Entries.Single().Entry.Receipt.BackupId);
    }

    [TestMethod]
    public async Task ReviewShowsMetadataOnlyAndRequiresCleanDraftsAndExactProviderId()
    {
        using var directory = new TestDirectory();
        var context = await CreateContextAsync(directory);
        var hasDrafts = false;
        var restored = false;
        var history = new ProviderConfigurationHistoryViewModel(
            context.Coordinator,
            "guffawaffle",
            "Guffawaffle",
            () => hasDrafts,
            () => restored = true);
        await history.RefreshAsync();
        var entry = history.Entries.Single();

        Assert.IsTrue(entry.ReviewCommand.CanExecute(null));
        hasDrafts = true;
        history.NotifySiblingDraftStateChanged();
        Assert.IsFalse(entry.ReviewCommand.CanExecute(null));
        hasDrafts = false;
        history.NotifySiblingDraftStateChanged();
        entry.ReviewCommand.Execute(null);

        Assert.IsNotNull(history.SelectedReview);
        Assert.AreEqual(context.ConfigurationPath, history.SelectedReview.DestinationPath);
        Assert.AreEqual("guffawaffle", history.SelectedReview.Preview.ConfirmationText);
        Assert.AreEqual(
            context.Backup.ContentSha256[..12],
            history.SelectedReview.HashShort);
        Assert.IsFalse(entry.AutomationName.Contains("secret-history-value", StringComparison.Ordinal));
        history.ConfirmationText = "Guffawaffle";
        Assert.IsFalse(history.RestoreCommand.CanExecute(null));
        history.ConfirmationText = "guffawaffle";
        Assert.IsTrue(history.RestoreCommand.CanExecute(null));

        await history.RestoreAsync();

        Assert.IsTrue(restored);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("# secret-history-value\r\n"),
            await File.ReadAllBytesAsync(context.ConfigurationPath));
        Assert.IsFalse(history.OperationStatus.Contains("secret-history-value", StringComparison.Ordinal));
        Assert.IsTrue(
            context.Store.List(context.GameDirectory, "guffawaffle")
                .Single(receipt => receipt.BackupId == context.Backup.BackupId)
                .WasRestored);
    }

    [TestMethod]
    public async Task SuccessfulRefreshClearsEarlierUnavailableStatus()
    {
        using var directory = new TestDirectory();
        var context = await CreateContextAsync(directory);
        var history = new ProviderConfigurationHistoryViewModel(
            context.Coordinator,
            "guffawaffle",
            "Guffawaffle",
            () => false,
            () => { });
        context.SelectedConfigurationPath.Value = null;

        await history.RefreshAsync();

        StringAssert.StartsWith(history.OperationStatus, "Configuration history is unavailable:");
        context.SelectedConfigurationPath.Value = context.ConfigurationPath;

        await history.RefreshAsync();

        Assert.AreEqual(string.Empty, history.OperationStatus);
        Assert.AreEqual(1, history.Entries.Count);
    }

    [TestMethod]
    public async Task SuccessfulRecoveryReloadsSiblingSettingsWorkspace()
    {
        using var directory = new TestDirectory();
        var context = await CreateContextAsync(directory);
        var reloaded = false;
        var history = new ProviderConfigurationHistoryViewModel(
            context.Coordinator,
            "guffawaffle",
            "Guffawaffle",
            () => false,
            () => reloaded = true);
        var baseline = await File.ReadAllBytesAsync(context.ConfigurationPath);
        var desired = context.Store.Read(
            context.GameDirectory,
            "guffawaffle",
            context.Backup.BackupId);
        var preview = context.Coordinator.Preview(context.Backup.BackupId);
        _ = await context.Store.CreateAsync(new(
            context.GameDirectory,
            preview.Selection.ProviderId,
            context.ConfigurationPath,
            baseline,
            "manual-restore",
            ReleaseIdentity: $"configuration-history-restore/{preview.TransactionId}",
            PinnedBackupId: preview.Backup.BackupId));
        await File.WriteAllBytesAsync(context.ConfigurationPath, desired);
        var journal = new ProviderConfigurationRestoreJournal(
            1,
            ProviderConfigurationRestorePhase.Prepared,
            preview,
            PreRestoreBackup: null,
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(
            Path.Combine(context.StateDirectory, "configuration-restore-journal.json"),
            JsonSerializer.Serialize(journal, JournalJsonOptions));

        await history.RefreshAsync();

        Assert.IsTrue(reloaded);
        StringAssert.StartsWith(history.OperationStatus, "Finished the interrupted configuration restore");
        Assert.IsTrue(
            history.Entries.Single(entry =>
                    entry.Entry.Receipt.BackupId == context.Backup.BackupId)
                .Entry.Receipt.WasRestored);
    }

    [TestMethod]
    public void HistoryReviewExposesAccessibleConfirmationAndExactDestinationWithoutPayloadBinding()
    {
        var path = FindRepositoryFile(
            "src",
            "STFCCommunityMod.Launcher",
            "Views",
            "SettingsView.xaml");
        var source = File.ReadAllText(path);
        var document = XDocument.Parse(source);

        Assert.IsTrue(source.Contains("ConfigurationHistory.SelectedReview.DestinationPath", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("ConfigurationHistory.SelectedReview.Contents", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("ConfigurationHistory.Entries.Contents", StringComparison.Ordinal));
        var confirmation = document.Descendants(Presentation + "TextBox").Single(element =>
            (string?)element.Attribute(Automation + "AutomationProperties.Name")
                == "Type provider ID to confirm configuration restore");
        Assert.AreEqual(
            "{Binding ConfigurationHistory.ConfirmationText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
            (string?)confirmation.Attribute("Text"));
        Assert.IsTrue(document.Descendants(Presentation + "Button").Any(element =>
            (string?)element.Attribute(Automation + "AutomationProperties.Name")
                == "Restore the reviewed configuration history entry"));
        Assert.IsTrue(document.Descendants(Presentation + "TextBlock").Any(element =>
            ((string?)element.Attribute(Automation + "AutomationProperties.Name"))?.Contains(
                "Exact configuration restore destination",
                StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public void HistoryTemplateMaterializesReadOnlyMetadataWithoutABindingException()
    {
        RunInSta(
            () =>
            {
                using var directory = new TestDirectory();
                var context = CreateContextAsync(directory).GetAwaiter().GetResult();
                var command = new TestCommand();
                var layout = new PrincipalCatalogSettingsLayoutProvider();
                var settings = new SettingsViewModel(
                    context.ConfigurationCatalog,
                    command,
                    command,
                    () => context.ConfigurationPath,
                    layout,
                    new("Guffawaffle test", "Active", "Test fixture", layout.DisplayName),
                    configurationHistoryCoordinator: context.Coordinator);
                settings.ConfigurationHistory!.RefreshAsync().GetAwaiter().GetResult();
                settings.Sections.Single(section =>
                        section.Id == LauncherSettingsSection.ConfigurationHistory)
                    .SelectCommand.Execute(null);
                settings.ConfigurationHistory.RefreshAsync().GetAwaiter().GetResult();

                var application = Application.Current ?? new App();
                if (application is App app && !application.Resources.Contains("SurfaceMutedBrush"))
                {
                    app.InitializeComponent();
                }

                var view = new SettingsView { DataContext = settings };
                view.Measure(new Size(960, 620));
                view.Arrange(new Rect(0, 0, 960, 620));
                view.UpdateLayout();

                var entries = (ItemsControl)view.FindName("ConfigurationHistoryEntries");
                Assert.AreEqual(1, entries.Items.Count);
                var renderedText = Descendants<TextBlock>(entries)
                    .Select(text => text.Inlines.Count > 0
                        ? string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text))
                        : text.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();
                var entry = settings.ConfigurationHistory.Entries.Single();
                Assert.IsTrue(renderedText.Any(text =>
                    text.Contains(entry.ProviderDisplayName, StringComparison.Ordinal)
                    && text.Contains(entry.CreatedAtText, StringComparison.Ordinal)),
                    $"Rendered History text: {string.Join(" | ", renderedText)}");
            });
    }

    [TestMethod]
    public void EveryDataBoundRunUsesOneWayPresentation()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src",
            "STFCCommunityMod.Launcher",
            "Views",
            "SettingsView.xaml"));
        var bindings = document.Descendants(Presentation + "Run")
            .Select(run => (string?)run.Attribute("Text"))
            .Where(text => text?.StartsWith("{Binding ", StringComparison.Ordinal) == true)
            .Cast<string>()
            .ToArray();

        Assert.IsTrue(bindings.Length >= 4);
        Assert.IsTrue(bindings.All(binding =>
            binding.Contains("Mode=OneWay", StringComparison.Ordinal)));
    }

    private static async Task<HistoryContext> CreateContextAsync(TestDirectory directory)
    {
        var stateDirectory = directory.CreateDirectory("state");
        var gameDirectory = directory.CreateDirectory("game");
        await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "prime.exe"), []);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");
        await File.WriteAllBytesAsync(
            configurationPath,
            Encoding.UTF8.GetBytes("# current live configuration\n"));
        var providerCatalog = BundledLauncherProviderCatalog.Load();
        var provider = providerCatalog.GetProvider("guffawaffle");
        var selection = new LauncherProviderSelection("guffawaffle", "stable");
        var selectionStore = new JsonLauncherProviderSelectionStore(stateDirectory);
        selectionStore.Save(selection);
        var configurationCatalog = BundledLauncherProviderCatalog.LoadConfigurationCatalog(provider);
        var store = new ProviderScopedConfigurationBackupStore(
            stateDirectory,
            new ReversingProtector(),
            new NoOpStorageSecurity());
        var backup = await store.CreateAsync(new(
            gameDirectory,
            selection.ProviderId,
            configurationPath,
            Encoding.UTF8.GetBytes("# secret-history-value\r\n"),
            "settings-save",
            ReleaseIdentity: "guffawaffle/stable"));
        var selectedConfigurationPath = new MutablePath(configurationPath);
        var coordinator = new ProviderConfigurationRestoreCoordinator(
            store,
            providerCatalog,
            selectionStore,
            selection,
            LauncherConfigurationDiagnosisEvidence.Supported(
                selection.ProviderId,
                selection.ReleaseChannelId,
                configurationCatalog),
            stateDirectory,
            () => selectedConfigurationPath.Value,
            new StoppedGameInspector());
        return new(
            stateDirectory,
            gameDirectory,
            configurationPath,
            configurationCatalog,
            store,
            backup,
            coordinator,
            selectedConfigurationPath);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, System.IO.Path.Combine);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new FileNotFoundException(
            $"Could not find repository file '{System.IO.Path.Combine(relativeParts)}'.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
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
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "The History template test timed out.");

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

    private sealed record HistoryContext(
        string StateDirectory,
        string GameDirectory,
        string ConfigurationPath,
        LauncherConfigurationCatalog ConfigurationCatalog,
        ProviderScopedConfigurationBackupStore Store,
        ConfigurationBackupReceipt Backup,
        ProviderConfigurationRestoreCoordinator Coordinator,
        MutablePath SelectedConfigurationPath);

    private sealed class MutablePath(string? value)
    {
        public string? Value { get; set; } = value;
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"stfc-history-view-model-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class ReversingProtector : IConfigurationBackupProtector
    {
        public string SchemeId => "test-reverse-v1";

        public byte[] Protect(byte[] contents) => [.. contents.Reverse()];

        public byte[] Unprotect(byte[] protectedContents) => [.. protectedContents.Reverse()];
    }

    private sealed class NoOpStorageSecurity : IConfigurationBackupStorageSecurity
    {
        public void SecureDirectory(string directory) => Directory.CreateDirectory(directory);
    }

    private sealed class StoppedGameInspector : IGameProcessInspector
    {
        public GameProcessInspectionState Inspect(string gameDirectory) =>
            GameProcessInspectionState.NotRunning;
    }

    private sealed class TestCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }
}
