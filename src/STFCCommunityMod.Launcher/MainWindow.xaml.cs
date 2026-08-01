using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using STFCCommunityMod.Launcher.Controls;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.Services;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher;

public partial class MainWindow : Window, IDisposable, ILauncherShellRefreshTarget
{
    private const double HomeWidth = 680;
    private const double HomeHeight = 680;
    private const double SettingsWidth = 1120;
    private const double SettingsHeight = 740;
    private const string GuffawaffleSchemaResource =
        "STFCCommunityMod.Launcher.Schemas.Guffawaffle.v1.json";
    private static readonly IReadOnlyList<ColorModeChoice> ColorModeChoices =
    [
        new(LauncherColorMode.System, "System", AppIconKind.SystemAppearance),
        new(LauncherColorMode.Light, "Light", AppIconKind.LightAppearance),
        new(LauncherColorMode.Dark, "Dark", AppIconKind.DarkAppearance),
    ];

    private LauncherTheme currentTheme;
    private readonly IGameProcessStateMonitor processStateMonitor;
    private readonly HttpClient httpClient;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly LauncherStartupComposition startupComposition;
    private readonly LauncherShellLifecycleController shellLifecycleController;
    private readonly JsonLauncherUiPreferencesStore uiPreferencesStore;
    private RelayCommand? openRawTomlCommand;
    private SettingsViewModel? settingsViewModel;
    private LauncherColorMode selectedColorMode = LauncherColorMode.System;
    private bool isDisposed;
    private bool isSettingsWorkspaceOpen;
    private bool isSettingsWorkspaceInitialized;
    private bool isColorModeSelectorReady;
    private ModOperationPreparation? pendingModOperation;

    public MainWindow()
        : this(
            new WindowsGameProcessStateMonitor(),
            LauncherStartupComposition.CreateDefault())
    {
    }

    internal MainWindow(
        IGameProcessStateMonitor processStateMonitor,
        LauncherStartupComposition startupComposition)
    {
        this.processStateMonitor =
            processStateMonitor ?? throw new ArgumentNullException(nameof(processStateMonitor));
        this.startupComposition =
            startupComposition ?? throw new ArgumentNullException(nameof(startupComposition));
        httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        shellLifecycleController = new(this);
        InitializeComponent();
        uiPreferencesStore = new JsonLauncherUiPreferencesStore(
            PerUserInstallLayout.FromCurrentUser().StateDirectory);
        selectedColorMode = uiPreferencesStore.Load().ColorMode;
        currentTheme = LauncherThemeManager.ApplyColorMode(selectedColorMode);
        ColorModeSelector.ItemsSource = ColorModeChoices;
        ColorModeSelector.SelectedValue = selectedColorMode;
        isColorModeSelectorReady = true;
        UpdateColorModeSelectorAccessibility();
        DataContext = MainWindowViewModel.CreateDefault(httpClient);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        LauncherThemeManager.ApplyWindowChrome(this, currentTheme);
        processStateMonitor.StateChanged += ProcessStateMonitor_StateChanged;
        if (processStateMonitor.TryStart(new WindowInteropHelper(this).Handle))
        {
            shellLifecycleController.HandleStartup();
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
        lifetimeCancellation.Cancel();
        processStateMonitor.StateChanged -= ProcessStateMonitor_StateChanged;
        processStateMonitor.Dispose();
        httpClient.Dispose();
        lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMaximizeRestoreButton();
    }

    private void SettingsNavigationButton_Click(object sender, RoutedEventArgs e)
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
            shellLifecycleController.HandleGameInstallationChanged();
        }
    }

    private async void ModActionButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        pendingModOperation = await viewModel.PrepareModOperationAsync(lifetimeCancellation.Token);
        if (isDisposed)
        {
            return;
        }
        if (pendingModOperation is null
            || pendingModOperation.State != ModOperationPreparationState.Ready)
        {
            return;
        }

        ModOperationDialog.DialogTitle = pendingModOperation.ExistingArtifactPolicy
            == ExistingArtifactPolicy.AdoptAndPreserve
            ? "Adopt and update community mod?"
            : "Install community mod?";
        ModOperationSummary.Text = pendingModOperation.Message;
        ModOperationTarget.Text = pendingModOperation.GameDirectory;
        ConfirmModOperationButton.Content = pendingModOperation.ExistingArtifactPolicy
            == ExistingArtifactPolicy.AdoptAndPreserve
            ? "_Adopt and install"
            : "_Install";
        ModOperationDialog.IsOpen = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => ConfirmModOperationButton.Focus());
    }

    private async void ConfirmModOperationButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (pendingModOperation is null
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var preparation = pendingModOperation;
        pendingModOperation = null;
        ModOperationDialog.IsOpen = false;
        await viewModel.ExecuteModOperationAsync(preparation, lifetimeCancellation.Token);
    }

    private void ColorModeSelector_SelectionChanged(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!isColorModeSelectorReady
            || ColorModeSelector.SelectedValue is not LauncherColorMode colorMode
            || colorMode == selectedColorMode)
        {
            return;
        }

        selectedColorMode = colorMode;
        currentTheme = LauncherThemeManager.ApplyColorMode(selectedColorMode);
        LauncherThemeManager.ApplyWindowChrome(this, currentTheme);
        SaveColorModePreference();
        UpdateColorModeSelectorAccessibility();
    }

    private void SettingsSearchToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsWorkspace.FocusSearchBoxWhenVisible();
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

    private void SaveColorModePreference()
    {
        try
        {
            var preferences = uiPreferencesStore.Load();
            uiPreferencesStore.Save(
                preferences with { ColorMode = selectedColorMode });
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            // UI preferences are best-effort and must never block the launcher.
        }
    }

    private void UpdateColorModeSelectorAccessibility()
    {
        var resolvedMode = currentTheme == LauncherTheme.Light ? "Light" : "Dark";
        var helpText = selectedColorMode == LauncherColorMode.System
            ? $"System follows the Windows app theme, currently {resolvedMode}."
            : $"{selectedColorMode} is selected instead of the Windows app theme.";
        ColorModeSelector.ToolTip = helpText;
        AutomationProperties.SetName(
            ColorModeSelector,
            $"Launcher color mode, {selectedColorMode}");
        AutomationProperties.SetHelpText(ColorModeSelector, helpText);
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
        HomeSettingsTitleBarButton.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        SettingsHomeTitleBarButton.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchToggleButton.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        ColorModeSelector.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;

        MinWidth = isOpen ? SettingsWidth : 560;
        MinHeight = isOpen ? 680 : 500;
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
                GetConfigurationFilePath,
                startupComposition.SettingsLayout,
                startupComposition.SettingsDiagnostics,
                uiPreferencesStore: uiPreferencesStore);
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

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            shellLifecycleController.HandleGameProcessChanged);
    }

    void ILauncherShellRefreshTarget.RefreshHome()
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Refresh();
        }
    }

    void ILauncherShellRefreshTarget.RefreshConfigurationAvailability()
    {
        openRawTomlCommand?.NotifyCanExecuteChanged();
    }

    void ILauncherShellRefreshTarget.ReloadConfigurationDocument()
    {
        settingsViewModel?.ReloadConfiguration();
    }
}

internal sealed record ColorModeChoice(
    LauncherColorMode Mode,
    string Label,
    AppIconKind Icon);
