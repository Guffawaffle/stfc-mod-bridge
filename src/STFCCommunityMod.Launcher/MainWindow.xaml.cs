using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
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
    internal const double SettingsMinWidth = 960;
    internal const double SettingsMinHeight = 620;
    private const double SettingsWidth = 1120;
    private const double SettingsHeight = 740;
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
    private readonly LauncherDistributionProvider distributionProvider;
    private readonly LauncherDistributionProviderCatalog distributionProviderCatalog;
    private readonly LauncherProviderSelectionResolution providerSelectionResolution;
    private readonly LauncherProviderShellAccess providerShellAccess;
    private readonly LauncherProviderReleaseChannel distributionReleaseChannel;
    private readonly LauncherProviderSourceSwitchService providerSourceSwitchService;
    private readonly LauncherStartupComposition startupComposition;
    private readonly LauncherShellLifecycleController shellLifecycleController;
    private readonly JsonLauncherUiPreferencesStore uiPreferencesStore;
    private readonly WorkspaceFocusTransition diagnosticsFocusTransition = new();
    private RelayCommand? openRawTomlCommand;
    private SettingsViewModel? settingsViewModel;
    private LauncherColorMode selectedColorMode = LauncherColorMode.System;
    private bool isDisposed;
    private bool isSettingsWorkspaceOpen;
    private bool isSettingsWorkspaceInitialized;
    private bool isColorModeSelectorReady;
    private ModOperationPreparation? pendingModOperation;
    private LauncherDiagnosticPreview? diagnosticPreview;
    private MaintenanceAction pendingMaintenanceAction;
    private LauncherUpdatePreparation? pendingLauncherUpdate;
    private LauncherProviderSwitchPreview? pendingProviderSwitch;
    private LauncherProviderSelection? providerSelectionPendingRestart;

    public MainWindow()
        : this(
            new WindowsGameProcessStateMonitor(),
            BundledLauncherProviderCatalog.LoadStartupContext(
                PerUserInstallLayout.FromCurrentUser().StateDirectory))
    {
    }

    internal MainWindow(
        IGameProcessStateMonitor processStateMonitor,
        LauncherDistributionProvider distributionProvider)
        : this(
            processStateMonitor,
            CreateInjectedProviderContext(distributionProvider))
    {
    }

    private MainWindow(
        IGameProcessStateMonitor processStateMonitor,
        LauncherProviderStartupContext providerContext)
    {
        this.processStateMonitor =
            processStateMonitor ?? throw new ArgumentNullException(nameof(processStateMonitor));
        ArgumentNullException.ThrowIfNull(providerContext);
        distributionProviderCatalog = providerContext.Catalog;
        providerSelectionResolution = providerContext.Selection;
        providerShellAccess = LauncherProviderShellAccess.From(providerSelectionResolution);
        distributionProvider = providerSelectionResolution.Provider
            ?? distributionProviderCatalog.DefaultProvider;
        distributionReleaseChannel = providerSelectionResolution.ReleaseChannel
            ?? distributionProvider.DefaultReleaseChannel;
        providerSourceSwitchService = new(
            distributionProviderCatalog,
            providerContext.SelectionStore,
            PerUserInstallLayout.FromCurrentUser().StateDirectory);
        startupComposition = LauncherStartupComposition.Create(distributionProvider);
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
        DataContext = MainWindowViewModel.CreateDefault(
            httpClient,
            distributionProvider,
            distributionReleaseChannel,
            providerShellAccess.CanUseProviderBoundModActions
                ? null
                : providerShellAccess.RestrictionReason,
            uiPreferencesStore);
        ReleaseSourceButton.Content = providerSelectionResolution.IsResolved
            ? $"Source: {distributionProvider.DisplayName} · {distributionReleaseChannel.DisplayName}"
            : "Source needs attention";
        if (!providerShellAccess.CanEditProviderSettings)
        {
            HomeSettingsTitleBarButton.IsEnabled = false;
            HomeSettingsTitleBarButton.ToolTip = providerShellAccess.RestrictionReason;
            ProviderRecoveryMessage.Text =
                $"Release source needs attention. {providerShellAccess.RestrictionReason} "
                + "Choose Source needs attention to select a known provider.";
            ProviderRecoveryBanner.Visibility = Visibility.Visible;
        }
    }

    private static LauncherProviderStartupContext CreateInjectedProviderContext(
        LauncherDistributionProvider distributionProvider)
    {
        ArgumentNullException.ThrowIfNull(distributionProvider);
        var catalog = BundledLauncherProviderCatalog.Load();
        var stateDirectory = PerUserInstallLayout.FromCurrentUser().StateDirectory;
        var store = new JsonLauncherProviderSelectionStore(stateDirectory);
        var selection = new LauncherProviderSelection(
            distributionProvider.Id,
            distributionProvider.DefaultReleaseChannelId);
        return new(
            catalog,
            store,
            LauncherProviderSelectionResolver.Resolve(catalog, selection));
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

        if (viewModel.ModActionKind == ModManagementActionKind.Recover)
        {
            ShowMaintenanceConfirmation(MaintenanceAction.Recover, viewModel);
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

        ModOperationDialog.DialogTitle = pendingModOperation.ActionKind switch
        {
            ModManagementActionKind.AdoptAndInstall => "Adopt and update community mod?",
            ModManagementActionKind.Repair => "Repair community mod?",
            ModManagementActionKind.CheckForUpdate => "Update community mod?",
            _ => "Install community mod?",
        };
        ModOperationSummary.Text = pendingModOperation.Message;
        ModOperationTarget.Text = pendingModOperation.GameDirectory;
        ConfirmModOperationButton.Content = pendingModOperation.ActionKind switch
        {
            ModManagementActionKind.AdoptAndInstall => "_Adopt and install",
            ModManagementActionKind.Repair => "_Repair",
            ModManagementActionKind.CheckForUpdate => "_Update",
            _ => "_Install",
        };
        ModOperationDialog.IsOpen = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => ConfirmModOperationButton.Focus());
    }

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            var focusReturnTarget = sender as IInputElement ?? Keyboard.FocusedElement;
            diagnosticPreview = viewModel.BuildDiagnosticPreview();
            SetDiagnosticsWorkspaceOpen(true);
            diagnosticsFocusTransition.Enter(
                () => ScheduleFocus(RefreshDiagnosticsButton),
                () => ScheduleFocus(focusReturnTarget ?? SettingsDiagnosticsTitleBarButton));
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            SettingsUnavailableMessage.Text = $"Diagnostics could not be prepared: {exception.Message}";
            SettingsUnavailableDialog.IsOpen = true;
        }
    }

    private void RefreshDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Refresh();
            diagnosticPreview = viewModel.BuildDiagnosticPreview();
            viewModel.ReportDiagnosticAction(true, "Diagnostics checks refreshed from current local evidence.");
        }
    }

    private void OpenGameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OpenGameFolder();
        }
    }

    private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OpenLogsFolder();
        }
    }

    private void DiagnosticsHomeButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SetDiagnosticsWorkspaceOpen(false);
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (diagnosticPreview is not null)
        {
            try
            {
                Clipboard.SetText(diagnosticPreview.RedactedSummary);
                if (DataContext is MainWindowViewModel viewModel)
                {
                    viewModel.ReportDiagnosticAction(
                        true,
                        "Copied the displayed redacted summary. Nothing was uploaded.");
                }
            }
            catch (System.Runtime.InteropServices.ExternalException exception)
            {
                if (DataContext is MainWindowViewModel viewModel)
                {
                    viewModel.ReportDiagnosticAction(
                        false,
                        $"Windows could not access the clipboard: {exception.Message}");
                }
            }
        }
    }

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (diagnosticPreview is null || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export redacted Mod Control diagnostics",
            FileName = $"stfc-mod-control-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            DefaultExt = ".json",
            Filter = "JSON diagnostics (*.json)|*.json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            await MainWindowViewModel.ExportDiagnosticsAsync(
                diagnosticPreview,
                dialog.FileName,
                lifetimeCancellation.Token);
            viewModel.ReportDiagnosticAction(
                true,
                "Saved the exact previewed redacted report. Nothing was uploaded.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            viewModel.ReportDiagnosticAction(false, $"The diagnostic export failed: {exception.Message}");
        }
    }

    private void DiagnosticsRecoverButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is MainWindowViewModel viewModel && viewModel.CanRecoverMod)
        {
            SetDiagnosticsWorkspaceOpen(false);
            ShowMaintenanceConfirmation(MaintenanceAction.Recover, viewModel);
        }
    }

    private void DiagnosticsUninstallButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is MainWindowViewModel viewModel && viewModel.CanUninstallMod)
        {
            SetDiagnosticsWorkspaceOpen(false);
            ShowMaintenanceConfirmation(MaintenanceAction.Uninstall, viewModel);
        }
    }

    private async void CheckLauncherUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        pendingLauncherUpdate = await viewModel.PrepareLauncherUpdateAsync(lifetimeCancellation.Token);
        if (pendingLauncherUpdate is null
            || pendingLauncherUpdate.State != LauncherUpdatePreparationState.Ready)
        {
            return;
        }
        SetDiagnosticsWorkspaceOpen(false);
        LauncherUpdateSummary.Text = pendingLauncherUpdate.Message;
        LauncherUpdateTarget.Text = pendingLauncherUpdate.TargetDirectory;
        LauncherUpdateDialog.IsOpen = true;
    }

    private void ConfirmLauncherUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (pendingLauncherUpdate is null)
        {
            return;
        }
        var preparation = pendingLauncherUpdate;
        pendingLauncherUpdate = null;
        LauncherUpdateDialog.IsOpen = false;
        try
        {
            MainWindowViewModel.StartLauncherUpdate(preparation);
            Application.Current.Shutdown();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            SettingsUnavailableMessage.Text = $"The update helper could not start: {exception.Message}";
            SettingsUnavailableDialog.IsOpen = true;
        }
    }

    private void ShowMaintenanceConfirmation(MaintenanceAction action, MainWindowViewModel viewModel)
    {
        var canStart = action == MaintenanceAction.Recover
            ? viewModel.CanRecoverMod
            : viewModel.CanUninstallMod;
        if (!canStart || viewModel.SelectedGameDirectory is null)
        {
            return;
        }
        pendingMaintenanceAction = action;
        MaintenanceDialog.DialogTitle = action == MaintenanceAction.Recover
            ? "Recover mod transaction?"
            : "Remove Mod Control-managed mod?";
        MaintenanceSummary.Text = action == MaintenanceAction.Recover
            ? "Roll back the incomplete transaction using its persisted journal. Only version.dll and transaction-scoped allowlisted files can change."
            : "Remove Mod Control-managed version.dll. If you explicitly adopted a previous manual DLL, its preserved bytes will be restored. Configuration and unrelated files remain untouched.";
        MaintenanceTarget.Text = viewModel.SelectedGameDirectory;
        ConfirmMaintenanceButton.Content = action == MaintenanceAction.Recover ? "_Recover" : "_Remove mod";
        MaintenanceDialog.IsOpen = true;
    }

    private async void ConfirmMaintenanceButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not MainWindowViewModel viewModel
            || pendingMaintenanceAction == MaintenanceAction.None)
        {
            return;
        }
        var action = pendingMaintenanceAction;
        pendingMaintenanceAction = MaintenanceAction.None;
        MaintenanceDialog.IsOpen = false;
        if (action == MaintenanceAction.Recover)
        {
            await viewModel.RecoverModAsync(lifetimeCancellation.Token);
        }
        else
        {
            await viewModel.UninstallModAsync(lifetimeCancellation.Token);
        }
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

    private void ReleaseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        pendingProviderSwitch = null;
        ProviderSourceSelector.IsEnabled = true;
        ProviderSourceSelector.ItemsSource = distributionProviderCatalog.Providers.Values
            .OrderBy(provider => provider.DisplayName, StringComparer.Ordinal)
            .ToArray();
        ProviderSourceSelector.SelectedValue = providerSelectionPendingRestart?.ProviderId
            ?? (providerSelectionResolution.IsResolved ? distributionProvider.Id : null);
        ProviderSourceSelector.IsEnabled = providerSelectionPendingRestart is null;
        ReviewProviderSwitchButton.IsEnabled = false;
        ConfirmProviderSwitchButton.IsEnabled = false;
        ProviderSwitchConfirmationPanel.Visibility = Visibility.Collapsed;
        ProviderSwitchConfirmationInput.Text = string.Empty;
        ProviderSwitchPreviewText.Text = providerSelectionPendingRestart is null
            ? "Choose another provider, then review compatibility before switching."
            : "The selected source is saved. Restart Mod Control before reviewing another switch.";
        UpdateProviderCapabilityText();
        ProviderSwitchDialog.IsOpen = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => ProviderSourceSelector.Focus());
    }

    private void ProviderSourceSelector_SelectionChanged(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        pendingProviderSwitch = null;
        ConfirmProviderSwitchButton.IsEnabled = false;
        ProviderSwitchConfirmationPanel.Visibility = Visibility.Collapsed;
        ProviderSwitchConfirmationInput.Text = string.Empty;
        ReviewProviderSwitchButton.IsEnabled =
            ProviderSourceSelector.SelectedItem is LauncherDistributionProvider provider
            && (!providerSelectionResolution.IsResolved
                || !string.Equals(provider.Id, distributionProvider.Id, StringComparison.Ordinal));
        ProviderSwitchPreviewText.Text = ReviewProviderSwitchButton.IsEnabled
            ? "Review the compatibility evidence and backup boundary before switching."
            : "This provider is active for the current Mod Control process.";
        UpdateProviderCapabilityText();
    }

    private void ReviewProviderSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ProviderSourceSelector.SelectedItem is not LauncherDistributionProvider targetProvider)
        {
            return;
        }
        if (settingsViewModel?.HasPendingChanges == true)
        {
            ProviderSwitchPreviewText.Text =
                "Save or discard staged mod-setting changes before reviewing a provider switch.";
            ConfirmProviderSwitchButton.IsEnabled = false;
            return;
        }
        try
        {
            pendingProviderSwitch = providerSourceSwitchService.Preview(
                targetProvider.Id,
                targetProvider.DefaultReleaseChannelId,
                GetConfigurationFilePath());
            var concerns = string.Join(
                Environment.NewLine,
                pendingProviderSwitch.Concerns.Select(
                    concern => $"• {concern.Kind}: {concern.Message}"));
            var backup = pendingProviderSwitch.ConfigurationPath is null
                ? "No configuration file is currently selected, so no TOML backup is needed."
                : "The exact TOML bytes will be copied to Mod Control-owned rollback storage before the selection changes.";
            ProviderSwitchPreviewText.Text =
                $"{pendingProviderSwitch.SourceDisplayName} → {pendingProviderSwitch.TargetDisplayName}"
                + Environment.NewLine
                + Environment.NewLine
                + concerns
                + Environment.NewLine
                + Environment.NewLine
                + backup;
            ConfirmProviderSwitchButton.Content = $"_Switch to {targetProvider.DisplayName}";
            ProviderSwitchConfirmationPrompt.Text =
                $"Type {pendingProviderSwitch.ConfirmationText} to confirm";
            ProviderSwitchConfirmationInput.Text = string.Empty;
            ProviderSwitchConfirmationPanel.Visibility = Visibility.Visible;
            ConfirmProviderSwitchButton.IsEnabled = false;
            ProviderSwitchConfirmationInput.Focus();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or KeyNotFoundException)
        {
            pendingProviderSwitch = null;
            ConfirmProviderSwitchButton.IsEnabled = false;
            ProviderSwitchConfirmationPanel.Visibility = Visibility.Collapsed;
            ProviderSwitchPreviewText.Text = $"The provider switch could not be prepared: {exception.Message}";
        }
    }

    private void ProviderSwitchConfirmationInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ConfirmProviderSwitchButton is null)
        {
            return;
        }
        ConfirmProviderSwitchButton.IsEnabled = pendingProviderSwitch is not null
            && string.Equals(
                ProviderSwitchConfirmationInput.Text,
                pendingProviderSwitch.ConfirmationText,
                StringComparison.Ordinal);
    }

    private void ConfirmProviderSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (pendingProviderSwitch is null)
        {
            return;
        }
        try
        {
            var result = providerSourceSwitchService.Execute(
                pendingProviderSwitch,
                ProviderSwitchConfirmationInput.Text);
            var selectedProvider = distributionProviderCatalog.GetProvider(result.Selection.ProviderId);
            ProviderSwitchPreviewText.Text = result.ConfigurationBackupPath is null
                ? result.Message
                : $"{result.Message} Configuration backup: {result.ConfigurationBackupPath}";
            ReleaseSourceButton.Content = $"Next source: {selectedProvider.DisplayName}";
            providerSelectionPendingRestart = result.Selection;
            HomeSettingsTitleBarButton.IsEnabled = false;
            HomeSettingsTitleBarButton.ToolTip = result.Message;
            ProviderRecoveryMessage.Text = result.Message;
            ProviderRecoveryBanner.Visibility = Visibility.Visible;
            ProviderSourceSelector.IsEnabled = false;
            ReviewProviderSwitchButton.IsEnabled = false;
            ConfirmProviderSwitchButton.IsEnabled = false;
            ProviderSwitchConfirmationPanel.Visibility = Visibility.Collapsed;
            pendingProviderSwitch = null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException)
        {
            ProviderSwitchPreviewText.Text = $"The provider switch failed: {exception.Message}";
            ConfirmProviderSwitchButton.IsEnabled = false;
            ProviderSwitchConfirmationPanel.Visibility = Visibility.Collapsed;
            pendingProviderSwitch = null;
        }
    }

    private void CancelProviderSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        pendingProviderSwitch = null;
        ProviderSwitchDialog.IsOpen = false;
    }

    private void UpdateProviderCapabilityText()
    {
        if (ProviderSourceSelector.SelectedItem is not LauncherDistributionProvider provider)
        {
            ProviderCapabilityText.Text = providerSelectionResolution.IsResolved
                ? string.Empty
                : providerSelectionResolution.Message;
            return;
        }
        ProviderCapabilityText.Text =
            $"{provider.Description}{Environment.NewLine}{provider.CapabilitySummary}";
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

    private void SettingsSearchOpenButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                TitleBarSettingsSearchBox.Focus();
                TitleBarSettingsSearchBox.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void SettingsSearchCloseButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                SettingsSearchOpenButton.Focus();
                Keyboard.Focus(SettingsSearchOpenButton);
            },
            DispatcherPriority.Input);
    }

    private void SettingsSearchHost_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Escape
            || SettingsWorkspace.DataContext is not SettingsViewModel settings)
        {
            return;
        }

        e.Handled = true;
        settings.SearchCloseCommand.Execute(null);
        SettingsSearchCloseButton_Click(this, new RoutedEventArgs());
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
            $"Mod Control color mode, {selectedColorMode}");
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
            isMaximized ? "Restore Mod Control" : "Maximize Mod Control");
    }

    private void SetSettingsWorkspaceOpen(bool isOpen)
    {
        if (isOpen)
        {
            DiagnosticsWorkspace.Visibility = Visibility.Collapsed;
            DiagnosticsHomeTitleBarButton.Visibility = Visibility.Collapsed;
            SettingsDiagnosticsTitleBarButton.ClearValue(VisibilityProperty);
        }
        isSettingsWorkspaceOpen = isOpen;
        HomeWorkspace.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        SettingsWorkspace.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        HomeSettingsTitleBarButton.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        SettingsHomeTitleBarButton.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchHost.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        ColorModeSelector.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;

        MinWidth = isOpen ? SettingsMinWidth : 560;
        MinHeight = isOpen ? SettingsMinHeight : 620;
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

    private void SetDiagnosticsWorkspaceOpen(bool isOpen)
    {
        if (isOpen)
        {
            isSettingsWorkspaceOpen = false;
        }
        HomeWorkspace.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        SettingsWorkspace.Visibility = Visibility.Collapsed;
        DiagnosticsWorkspace.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        HomeSettingsTitleBarButton.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
        SettingsHomeTitleBarButton.Visibility = Visibility.Collapsed;
        DiagnosticsHomeTitleBarButton.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        if (isOpen)
        {
            SettingsDiagnosticsTitleBarButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SettingsDiagnosticsTitleBarButton.ClearValue(VisibilityProperty);
        }
        SettingsSearchHost.Visibility = Visibility.Collapsed;
        ColorModeSelector.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!isOpen)
        {
            diagnosticsFocusTransition.Exit();
        }

        MinWidth = isOpen ? SettingsMinWidth : 560;
        MinHeight = SettingsMinHeight;
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

    private void ScheduleFocus(IInputElement target)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (target is UIElement { IsVisible: true, IsEnabled: true } element)
                {
                    element.Focus();
                    Keyboard.Focus(target);
                }
            });
    }

    private bool EnsureSettingsWorkspaceInitialized()
    {
        if (isSettingsWorkspaceInitialized)
        {
            return true;
        }

        try
        {
            if (!providerSelectionResolution.IsResolved)
            {
                throw new LauncherConfigurationSchemaException(
                    $"Settings are disabled until the release source is repaired. "
                    + providerSelectionResolution.Message);
            }
            var catalog = BundledLauncherProviderCatalog.LoadConfigurationCatalog(distributionProvider);
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

internal enum MaintenanceAction
{
    None,
    Recover,
    Uninstall,
}
