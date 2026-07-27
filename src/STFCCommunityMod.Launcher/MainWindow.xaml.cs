using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Shell;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher;

public partial class MainWindow : Window
{
    private const int WindowNonClientHitTest = 0x0084;
    private const int HitTestMaximizeButton = 9;

    private LauncherTheme currentTheme;
    private HwndSource? windowSource;

    public MainWindow()
    {
        InitializeComponent();
        currentTheme = LauncherThemeManager.ApplySystemPreference();
        UpdateThemeToggle();
        DataContext = MainWindowViewModel.CreateDefault();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        LauncherThemeManager.ApplyWindowChrome(this, currentTheme);
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource?.AddHook(WindowProcedure);
    }

    protected override void OnClosed(EventArgs e)
    {
        windowSource?.RemoveHook(WindowProcedure);
        windowSource = null;
        base.OnClosed(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMaximizeRestoreButton();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "STFC Community Mod Launcher\nVersion 0.1.0\n\nCommunity-built tools for Star Trek Fleet Command.",
            "About STFC Community Mod Launcher",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
        MaximizeRestoreButton.Content = isMaximized ? "❐" : "□";
        MaximizeRestoreButton.ToolTip = isMaximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(
            MaximizeRestoreButton,
            isMaximized ? "Restore launcher" : "Maximize launcher");
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        _ = windowHandle;
        _ = wordParameter;
        if (message != WindowNonClientHitTest || !IsOverMaximizeRestoreButton(longParameter))
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(HitTestMaximizeButton);
    }

    private bool IsOverMaximizeRestoreButton(IntPtr longParameter)
    {
        var packedPoint = longParameter.ToInt64();
        var screenPoint = new Point(
            unchecked((short)(packedPoint & 0xFFFF)),
            unchecked((short)((packedPoint >> 16) & 0xFFFF)));
        var buttonPoint = MaximizeRestoreButton.PointFromScreen(screenPoint);
        return buttonPoint.X >= 0
            && buttonPoint.Y >= 0
            && buttonPoint.X <= MaximizeRestoreButton.ActualWidth
            && buttonPoint.Y <= MaximizeRestoreButton.ActualHeight;
    }
}
