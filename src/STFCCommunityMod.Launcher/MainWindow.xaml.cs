using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using STFCCommunityMod.Launcher.Services;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher;

public partial class MainWindow : Window, IDisposable
{
    private LauncherTheme currentTheme;
    private readonly IGameProcessStateMonitor processStateMonitor;
    private bool isDisposed;

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
        }
    }
}
