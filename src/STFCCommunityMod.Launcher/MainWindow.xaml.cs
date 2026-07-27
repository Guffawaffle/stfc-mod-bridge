using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher;

public partial class MainWindow : Window
{
    private LauncherTheme currentTheme;

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
}
