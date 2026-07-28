using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.Services;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher;

public partial class MainWindow : Window, IDisposable
{
    private const double HomeWidth = 680;
    private const double HomeHeight = 590;
    private const double SettingsWidth = 1040;
    private const double SettingsHeight = 740;
    private const string GuffawaffleSchemaResource =
        "STFCCommunityMod.Launcher.Schemas.Guffawaffle.v1.json";

    private LauncherTheme currentTheme;
    private readonly IGameProcessStateMonitor processStateMonitor;
    private RelayCommand? openRawTomlCommand;
    private SettingsViewModel? settingsViewModel;
    private bool isDisposed;
    private bool isSettingsWorkspaceOpen;
    private bool isSettingsWorkspaceInitialized;

    public MainWindow()
        : this(new WindowsGameProcessStateMonitor())
    {
    }

    internal MainWindow(IGameProcessStateMonitor processStateMonitor)
    {
        this.processStateMonitor = processStateMonitor;
        InitializeComponent();
        currentTheme = LauncherThemeManager.ApplySystemPreference();
        UpdateThemeToggle();
        DataContext = MainWindowViewModel.CreateDefault();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        LauncherThemeManager.ApplyWindowChrome(this, currentTheme);
        processStateMonitor.StateChanged += ProcessStateMonitor_StateChanged;
        if (processStateMonitor.TryStart(new WindowInteropHelper(this).Handle))
        {
            RefreshEnvironment();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        processStateMonitor.StateChanged -= ProcessStateMonitor_StateChanged;
        processStateMonitor.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMaximizeRestoreButton();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        AboutDialog.IsOpen = true;
    }

    private void WorkspaceNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!isSettingsWorkspaceOpen && !EnsureSettingsWorkspaceInitialized())
        {
            return;
        }

        SetSettingsWorkspaceOpen(!isSettingsWorkspaceOpen);
    }

    private void ChooseGameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select the STFC game folder that contains prime.exe",
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(viewModel.InitialBrowseDirectory))
        {
            dialog.InitialDirectory = viewModel.InitialBrowseDirectory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            viewModel.ConfirmManualSelection(dialog.FolderName);
            openRawTomlCommand?.NotifyCanExecuteChanged();
            settingsViewModel?.ReloadConfiguration();
        }
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        currentTheme = LauncherThemeManager.Toggle(currentTheme);
        LauncherThemeManager.ApplyWindowChrome(this, currentTheme);
        UpdateThemeToggle();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void UpdateThemeToggle()
    {
        var switchingToLight = currentTheme == LauncherTheme.Dark;
        ThemeToggleButton.Content = switchingToLight ? "Light" : "Dark";
        ThemeToggleButton.ToolTip = switchingToLight
            ? "Switch to light theme"
            : "Switch to dark theme";
        AutomationProperties.SetName(
            ThemeToggleButton,
            switchingToLight
                ? "Switch launcher to light theme"
                : "Switch launcher to dark theme");
    }

    private void UpdateMaximizeRestoreButton()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = isMaximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = isMaximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeRestoreButton.ToolTip = isMaximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(
            MaximizeRestoreButton,
            isMaximized ? "Restore launcher" : "Maximize launcher");
    }

    private void SetSettingsWorkspaceOpen(bool isOpen)
    {
        isSettingsWorkspaceOpen = isOpen;
        HomeWorkspace.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        SettingsWorkspace.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        HomeActions.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        AboutButton.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        RefreshStatusButton.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceNavigationButton.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceNavigationButton.Content = "_Settings";
        AutomationProperties.SetName(
            WorkspaceNavigationButton,
            "Open launcher settings");

        MinWidth = isOpen ? 820 : 560;
        MinHeight = isOpen ? 620 : 500;
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        Width = Math.Min(
            isOpen ? Math.Max(ActualWidth, SettingsWidth) : HomeWidth,
            SystemParameters.WorkArea.Width);
        Height = Math.Min(
            isOpen ? Math.Max(ActualHeight, SettingsHeight) : HomeHeight,
            SystemParameters.WorkArea.Height);
    }

    private bool EnsureSettingsWorkspaceInitialized()
    {
        if (isSettingsWorkspaceInitialized)
        {
            return true;
        }

        try
        {
            using var schemaStream = typeof(MainWindow).Assembly.GetManifestResourceStream(
                GuffawaffleSchemaResource);
            if (schemaStream is null)
            {
                throw new LauncherConfigurationSchemaException(
                    "The packaged Guffawaffle configuration catalog is missing.");
            }

            var catalog = LauncherConfigurationSchemaLoader.Load(schemaStream);
            openRawTomlCommand = new RelayCommand(OpenRawConfiguration, CanOpenRawConfiguration);
            settingsViewModel = new SettingsViewModel(
                catalog,
                new RelayCommand(() => SetSettingsWorkspaceOpen(false)),
                openRawTomlCommand,
                GetConfigurationFilePath);
            SettingsWorkspace.DataContext = settingsViewModel;
            isSettingsWorkspaceInitialized = true;
            return true;
        }
        catch (Exception exception)
        {
            SettingsUnavailableMessage.Text = exception.Message;
            SettingsUnavailableDialog.IsOpen = true;
            return false;
        }
    }

    private bool CanOpenRawConfiguration()
    {
        return TryGetConfigurationFilePath(out var path) && File.Exists(path);
    }

    private void OpenRawConfiguration()
    {
        if (!TryGetConfigurationFilePath(out var path) || !File.Exists(path))
        {
            SettingsUnavailableMessage.Text =
                "Select a valid game folder with an existing community_patch_settings.toml first.";
            SettingsUnavailableDialog.IsOpen = true;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            SettingsUnavailableMessage.Text =
                "Windows could not open the active TOML file. Check the default app for .toml files.";
            SettingsUnavailableDialog.IsOpen = true;
        }
    }

    private bool TryGetConfigurationFilePath(out string path)
    {
        if (DataContext is MainWindowViewModel { ConfigurationFilePath: { } configurationFilePath })
        {
            path = configurationFilePath;
            return true;
        }

        path = string.Empty;
        return false;
    }

    private string? GetConfigurationFilePath() =>
        TryGetConfigurationFilePath(out var path) ? path : null;

    private void ProcessStateMonitor_StateChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshEnvironment);
    }

    private void RefreshEnvironment()
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Refresh();
            openRawTomlCommand?.NotifyCanExecuteChanged();
            settingsViewModel?.ReloadConfiguration();
        }
    }
}
