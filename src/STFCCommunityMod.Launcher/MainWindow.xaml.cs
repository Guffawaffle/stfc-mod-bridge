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
    private readonly LauncherDistributionProviderCatalog distributionProviderCatalog;
    private readonly ILauncherProviderSelectionStore providerSelectionStore;
    private readonly LauncherProviderSessionRecomposer<LauncherProviderSession> providerSessions;
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
    private int isProcessStateRefreshPending;
    private ModOperationPreparation? pendingModOperation;
    private LauncherDiagnosticPreview? diagnosticPreview;
    private ConfigurationEffectiveExportDocument? pendingEffectiveConfigurationExport;
    private ConfigurationDocumentSnapshot? pendingConfigurationMigrationSnapshot;
    private LauncherConfigurationDiagnosisEvidence? pendingConfigurationMigrationEvidence;
    private ConfigurationMigrationPlanResult? pendingConfigurationMigrationPlan;
    private ConfigurationMigrationApplyCoordinator? pendingConfigurationMigrationCoordinator;
    private MaintenanceAction pendingMaintenanceAction;
    private LauncherUpdatePreparation? pendingLauncherUpdate;
    private LauncherProviderAtomicSwitchPreview? pendingProviderSwitch;
    private bool isProviderSwitchOperationPending;

    private LauncherProviderSession ProviderSession => providerSessions.Current;

    private LauncherDistributionProvider distributionProvider => ProviderSession.Provider;

    private LauncherProviderSelectionResolution providerSelectionResolution => ProviderSession.Resolution;

    private LauncherProviderShellAccess providerShellAccess => ProviderSession.ShellAccess;

    private LauncherProviderReleaseChannel distributionReleaseChannel => ProviderSession.ReleaseChannel;

    private LauncherProviderAtomicSwitchCoordinator providerSourceSwitchCoordinator =>
        ProviderSession.SwitchCoordinator;

    private LauncherStartupComposition startupComposition => ProviderSession.StartupComposition;

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
        providerSelectionStore = providerContext.SelectionStore;
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
        providerSessions = new(
            distributionProviderCatalog,
            providerContext.Selection,
            CreateProviderSession);
        ApplyProviderSession(ProviderSession);
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

    private LauncherProviderSession CreateProviderSession(
        LauncherProviderSelectionResolution resolution)
    {
        var shellAccess = LauncherProviderShellAccess.From(resolution);
        var provider = resolution.Provider ?? distributionProviderCatalog.DefaultProvider;
        var releaseChannel = resolution.ReleaseChannel ?? provider.DefaultReleaseChannel;
        var composition = LauncherStartupComposition.Create(provider, releaseChannel);
        var viewModel = MainWindowViewModel.CreateDefault(
            httpClient,
            distributionProviderCatalog,
            provider,
            releaseChannel,
            shellAccess.CanUseProviderBoundModActions
                ? null
                : shellAccess.RestrictionReason,
            uiPreferencesStore,
            providerSelectionStore);
        return new(
            resolution,
            shellAccess,
            provider,
            releaseChannel,
            composition,
            viewModel);
    }

    private void ApplyProviderSession(LauncherProviderSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        DataContext = session.ViewModel;
        pendingProviderSwitch = null;
        pendingModOperation = null;
        diagnosticPreview = null;
        SettingsWorkspace.DataContext = null;
        settingsViewModel = null;
        openRawTomlCommand = null;
        isSettingsWorkspaceInitialized = false;
        isProviderSwitchOperationPending = false;
        ProviderSwitchActionButton.IsEnabled = false;
        ProviderSourceSelector.IsEnabled = true;
        ReleaseSourceButton.IsEnabled = true;
        RetryProviderRecompositionButton.Visibility = Visibility.Collapsed;
        ProviderRecoveryBanner.Visibility = Visibility.Collapsed;
        HomeSettingsTitleBarButton.IsEnabled = session.ShellAccess.CanEditProviderSettings;
        HomeSettingsTitleBarButton.ToolTip = session.ShellAccess.CanEditProviderSettings
            ? null
            : session.ShellAccess.RestrictionReason;
        ModActionButton.IsHitTestVisible = true;
        ModActionButton.Focusable = true;
    }

    private void ShowProviderRecompositionFailure(Exception exception)
    {
        HomeSettingsTitleBarButton.IsEnabled = false;
        ReleaseSourceButton.IsEnabled = false;
        ModActionButton.IsHitTestVisible = false;
        ModActionButton.Focusable = false;
        ProviderRecoveryMessage.Text =
            "The provider switch committed, but its workspace could not be refreshed. "
            + $"Retry inside Mod Bridge before further mod management. {exception.Message}";
        RetryProviderRecompositionButton.Visibility = Visibility.Visible;
        ProviderRecoveryBanner.Visibility = Visibility.Visible;
    }

    private void RetryProviderRecompositionButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        RetryProviderRecompositionButton.IsEnabled = false;
        try
        {
            var session = providerSessions.Retry();
            ApplyProviderSession(session);
            ProviderSwitchPreviewText.Text =
                $"{session.Provider.DisplayName} is active. You can review another source immediately.";
            ProviderSourceSelector.ItemsSource = distributionProviderCatalog.Providers.Values
                .OrderBy(provider => provider.DisplayName, StringComparer.Ordinal)
                .ToArray();
            ProviderSourceSelector.SelectedValue = session.Provider.Id;
            UpdateProviderCapabilityText();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException)
        {
            ShowProviderRecompositionFailure(exception);
        }
        finally
        {
            RetryProviderRecompositionButton.IsEnabled = true;
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
        // The view model already captured the initial state during composition. The monitor
        // supplies subsequent edge-triggered changes; starting it must not repeat the DLL scan.
        _ = processStateMonitor.TryStart(new WindowInteropHelper(this).Handle);
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
        pendingLauncherUpdate?.Dispose();
        pendingLauncherUpdate = null;
        providerSessions.Dispose();
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
            ModManagementActionKind.UpdateManualInstallation => "Update community mod?",
            ModManagementActionKind.Repair => "Repair community mod?",
            ModManagementActionKind.CheckForUpdate => "Update community mod?",
            _ => "Install community mod?",
        };
        ModOperationSummary.Text = pendingModOperation.Message;
        ModOperationTarget.Text = pendingModOperation.GameDirectory;
        ConfirmModOperationButton.Content = pendingModOperation.ActionKind switch
        {
            ModManagementActionKind.UpdateManualInstallation => "_Update",
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
            Title = "Export redacted Mod Bridge diagnostics",
            FileName = $"stfc-mod-bridge-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
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

    private void ReviewEffectiveConfigurationExportButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        pendingEffectiveConfigurationExport = null;
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (!TryGetConfigurationFilePath(out var path))
        {
            viewModel.ReportDiagnosticAction(
                false,
                "Select a valid game folder before preparing an effective configuration export.");
            return;
        }

        try
        {
            var capability = distributionProvider.GetCapabilityStatus(
                LauncherProviderCapabilityIds.ConfigurationCatalog);
            if (capability != LauncherProviderCapabilityStatus.Supported)
            {
                viewModel.ReportDiagnosticAction(
                    false,
                    "The selected provider has no verified configuration catalog, so effective values cannot be exported.");
                return;
            }

            var catalog = BundledLauncherProviderCatalog.LoadConfigurationCatalog(distributionProvider);
            var contents = File.Exists(path) ? File.ReadAllBytes(path) : [];
            var result = ConfigurationEffectiveExportService.Build(
                new ConfigurationDocumentSnapshot(path, contents),
                LauncherConfigurationDiagnosisEvidence.Supported(
                    distributionProvider.Id,
                    distributionReleaseChannel.Id,
                    catalog));
            if (!result.IsSuccess)
            {
                viewModel.ReportDiagnosticAction(
                    false,
                    result.Error ?? "The effective configuration could not be established safely.");
                return;
            }

            pendingEffectiveConfigurationExport = result.Document;
            EffectiveConfigurationExportSummary.Text =
                $"Provider {result.Document!.ProviderId} · {result.Document.ChannelId} · "
                + $"catalog {result.Document.CatalogVersion} · {result.Document.Entries.Count} effective entries.";
            EffectiveConfigurationExportRevision.Text = result.Document.SourceRevisionSha256;
            EffectiveConfigurationExportDialog.IsOpen = true;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => ConfirmEffectiveConfigurationExportButton.Focus());
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or LauncherConfigurationSchemaException)
        {
            viewModel.ReportDiagnosticAction(
                false,
                $"The effective configuration export could not be prepared: {exception.Message}");
        }
    }

    private async void ConfirmEffectiveConfigurationExportButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (pendingEffectiveConfigurationExport is null
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var document = pendingEffectiveConfigurationExport;
        pendingEffectiveConfigurationExport = null;
        EffectiveConfigurationExportDialog.IsOpen = false;
        var dialog = new SaveFileDialog
        {
            Title = "Save unredacted effective configuration",
            FileName = $"stfc-mod-bridge-effective-config-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            DefaultExt = ".json",
            Filter = "JSON effective configuration (*.json)|*.json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            viewModel.ReportDiagnosticAction(true, "Effective configuration export canceled; no file was written.");
            return;
        }

        try
        {
            await ConfigurationEffectiveExportService.ExportAsync(
                document,
                dialog.FileName,
                lifetimeCancellation.Token);
            viewModel.ReportDiagnosticAction(
                true,
                "Saved the explicitly unredacted effective configuration locally. Nothing was uploaded.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            viewModel.ReportDiagnosticAction(false, $"The effective configuration export failed: {exception.Message}");
        }
    }

    private void ReviewConfigurationCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ClearPendingConfigurationMigration();
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (!TryGetConfigurationFilePath(out var path))
        {
            viewModel.ReportDiagnosticAction(
                false,
                "Select a valid game folder before reviewing configuration cleanup.");
            return;
        }
        if (settingsViewModel is not null
            && (settingsViewModel.HasPendingChanges || settingsViewModel.SyncWorkspace.HasPendingChanges))
        {
            viewModel.ReportDiagnosticAction(
                false,
                "Save or discard the current Settings and Data Sync edits before reviewing cleanup.");
            return;
        }

        try
        {
            if (distributionProvider.GetCapabilityStatus(LauncherProviderCapabilityIds.ConfigurationCatalog)
                != LauncherProviderCapabilityStatus.Supported)
            {
                viewModel.ReportDiagnosticAction(
                    false,
                    "Cleanup is unavailable because the selected provider has no verified configuration catalog.");
                return;
            }

            var catalog = BundledLauncherProviderCatalog.LoadConfigurationCatalog(distributionProvider);
            var evidence = LauncherConfigurationDiagnosisEvidence.Supported(
                distributionProvider.Id,
                distributionReleaseChannel.Id,
                catalog);
            var backupStore = new ProviderScopedConfigurationBackupStore(
                PerUserInstallLayout.FromCurrentUser().StateDirectory);
            var mutationBackup = new ProviderScopedConfigurationMutationBackup(
                backupStore,
                distributionProvider.Id,
                $"{distributionProvider.Id}/{distributionReleaseChannel.Id}",
                "configuration-migration");
            var repository = new TomlConfigurationRepository(mutationBackup: mutationBackup);
            var read = repository.Read(path);
            if (!read.IsSuccess || read.Snapshot is null)
            {
                viewModel.ReportDiagnosticAction(
                    false,
                    read.ValidationError?.Message
                        ?? read.Error
                        ?? "No safely editable configuration is selected.");
                return;
            }

            var diagnosis = new ConfigurationHealthAnalyzer().Analyze(read.Snapshot, evidence);
            var selectedRemediations = diagnosis.Findings
                .Select(finding => finding.RemediationId)
                .Where(remediationId => !string.IsNullOrWhiteSpace(remediationId))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var plan = new ConfigurationMigrationPlanner().Plan(
                read.Snapshot,
                evidence,
                diagnosis,
                selectedRemediations);
            if (plan.State != ConfigurationMigrationPlanState.Ready)
            {
                viewModel.ReportDiagnosticAction(
                    plan.State == ConfigurationMigrationPlanState.NoChange,
                    plan.State == ConfigurationMigrationPlanState.NoChange
                        ? "No catalog-authorized configuration cleanup is currently needed."
                        : plan.Message ?? "Configuration cleanup is blocked by the current diagnosis.");
                return;
            }

            pendingConfigurationMigrationSnapshot = read.Snapshot;
            pendingConfigurationMigrationEvidence = evidence;
            pendingConfigurationMigrationPlan = plan;
            pendingConfigurationMigrationCoordinator = new(repository);
            ConfigurationCleanupSummary.Text =
                $"{plan.Operations.Count} catalog-authorized cleanup operation(s) are selected. "
                + "Only recognized aliases will move or be removed; unknown content and values remain untouched.";
            ConfigurationCleanupBinding.Text =
                $"{plan.Binding!.ProviderId} · {plan.Binding.ChannelId} · catalog {plan.Binding.CatalogVersion} · "
                + $"source {plan.Binding.Revision.Sha256}";
            ConfigurationCleanupOperations.ItemsSource = plan.Operations;
            ConfigurationCleanupPreview.ItemsSource = plan.PreviewLines;
            ConfigurationCleanupDialog.IsOpen = true;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => ConfirmConfigurationCleanupButton.Focus());
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or LauncherConfigurationSchemaException)
        {
            viewModel.ReportDiagnosticAction(
                false,
                $"Configuration cleanup could not be prepared: {exception.Message}");
        }
    }

    private async void ConfirmConfigurationCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not MainWindowViewModel viewModel
            || pendingConfigurationMigrationSnapshot is null
            || pendingConfigurationMigrationEvidence is null
            || pendingConfigurationMigrationPlan is null
            || pendingConfigurationMigrationCoordinator is null)
        {
            return;
        }

        var request = new ConfigurationMigrationApplyRequest(
            pendingConfigurationMigrationSnapshot,
            pendingConfigurationMigrationPlan,
            pendingConfigurationMigrationEvidence);
        var coordinator = pendingConfigurationMigrationCoordinator;
        ConfigurationCleanupDialog.IsOpen = false;
        viewModel.ReportDiagnosticAction(true, "Applying the reviewed configuration cleanup…");
        ClearPendingConfigurationMigration();
        try
        {
            var result = await coordinator.ApplyAsync(request, lifetimeCancellation.Token);
            if (result.IsSuccess)
            {
                var receipt = result.BackupReceipt is null
                    ? "The configuration did not require replacement."
                    : $"Protected backup {result.BackupReceipt.BackupId} verified as {result.BackupReceipt.ContentSha256}.";
                viewModel.Refresh();
                diagnosticPreview = viewModel.BuildDiagnosticPreview();
                viewModel.ReportDiagnosticAction(
                    true,
                    result.State == AtomicTomlWriteState.NoChange
                        ? "The configuration was already clean; no bytes changed."
                        : $"Configuration cleanup applied and rescanned successfully. {receipt}");
            }
            else
            {
                viewModel.ReportDiagnosticAction(
                    false,
                    result.State == AtomicTomlWriteState.Conflict
                        ? "The TOML changed after review. External edits were preserved; refresh and review again."
                        : result.ValidationError?.Message
                            ?? result.Error
                            ?? "The reviewed cleanup failed without changing the configuration.");
            }
        }
        catch (OperationCanceledException)
        {
            viewModel.ReportDiagnosticAction(false, "Configuration cleanup was canceled; no unreviewed change was applied.");
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => ReviewConfigurationCleanupButton.Focus());
        }
    }

    private void CancelConfigurationCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ConfigurationCleanupDialog.IsOpen = false;
        ClearPendingConfigurationMigration();
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ReportDiagnosticAction(true, "Configuration cleanup review canceled; no file was changed.");
        }
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => ReviewConfigurationCleanupButton.Focus());
    }

    private void ClearPendingConfigurationMigration()
    {
        pendingConfigurationMigrationSnapshot = null;
        pendingConfigurationMigrationEvidence = null;
        pendingConfigurationMigrationPlan = null;
        pendingConfigurationMigrationCoordinator = null;
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
        pendingLauncherUpdate?.Dispose();
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
                or InvalidDataException
                or InvalidOperationException
                or TimeoutException
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
            : "Remove Mod Bridge-managed mod?";
        MaintenanceSummary.Text = action == MaintenanceAction.Recover
            ? "Roll back the incomplete transaction using its persisted journal. A provider switch restores version.dll, provider selection, and exact TOML bytes together."
            : "Remove Mod Bridge-managed version.dll. If you explicitly adopted a previous manual DLL, its preserved bytes will be restored. Configuration and unrelated files remain untouched.";
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
        isProviderSwitchOperationPending = false;
        ProviderSourceSelector.IsEnabled = true;
        ProviderSourceSelector.ItemsSource = distributionProviderCatalog.Providers.Values
            .OrderBy(provider => provider.DisplayName, StringComparer.Ordinal)
            .ToArray();
        ProviderSourceSelector.SelectedValue =
            providerSelectionResolution.IsResolved ? distributionProvider.Id : null;
        SetProviderSwitchAction("Switch", targetProvider: null, enabled: false);
        ProviderSwitchPreviewText.Text = "Choose another provider to continue.";
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
        var hasDifferentTarget =
            ProviderSourceSelector.SelectedItem is LauncherDistributionProvider provider
            && (!providerSelectionResolution.IsResolved
                || !string.Equals(provider.Id, distributionProvider.Id, StringComparison.Ordinal));
        if (hasDifferentTarget
            && ProviderSourceSelector.SelectedItem is LauncherDistributionProvider targetProvider)
        {
            SetProviderSwitchAction(
                uiPreferencesStore.Load().ProviderSwitchReviewAcknowledged ? "Switch" : "Review",
                targetProvider,
                enabled: !isProviderSwitchOperationPending);
        }
        else
        {
            SetProviderSwitchAction("Switch", targetProvider: null, enabled: false);
        }
        ProviderSwitchPreviewText.Text = hasDifferentTarget
            ? "The selected source will be verified before any files or settings change."
            : "This provider is active for the current Mod Bridge process.";
        UpdateProviderCapabilityText();
    }

    private async void ProviderSwitchActionButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (isProviderSwitchOperationPending)
        {
            return;
        }
        if (ProviderSourceSelector.SelectedItem is not LauncherDistributionProvider targetProvider)
        {
            return;
        }
        if (settingsViewModel is not null
            && (settingsViewModel.HasPendingChanges
                || settingsViewModel.SyncWorkspace.HasPendingChanges))
        {
            ProviderSwitchPreviewText.Text =
                "Save or discard staged Settings and Data Sync changes before switching sources.";
            SetProviderSwitchAction(
                uiPreferencesStore.Load().ProviderSwitchReviewAcknowledged ? "Switch" : "Review",
                targetProvider,
                enabled: true);
            return;
        }
        if (DataContext is not MainWindowViewModel viewModel
            || viewModel.SelectedGameDirectory is null)
        {
            ProviderSwitchPreviewText.Text =
                "Select a valid game installation before switching release sources.";
            return;
        }
        isProviderSwitchOperationPending = true;
        ProviderSwitchActionButton.IsEnabled = false;
        ProviderSourceSelector.IsEnabled = false;
        var operationWasPrepared = pendingProviderSwitch is not null;
        try
        {
            if (pendingProviderSwitch is null)
            {
                ProviderSwitchPreviewText.Text = "Discovering and verifying the target release…";
                var configurationPath = GetConfigurationFilePath();
                if (configurationPath is not null && !File.Exists(configurationPath))
                {
                    configurationPath = null;
                }
                pendingProviderSwitch = await providerSourceSwitchCoordinator.PreviewAsync(
                    targetProvider.Id,
                    targetProvider.DefaultReleaseChannelId,
                    viewModel.SelectedGameDirectory,
                    viewModel.IsGameRunning,
                    configurationPath,
                    lifetimeCancellation.Token);
                var review = ProviderSwitchReviewPresentation.From(
                    pendingProviderSwitch,
                    targetProvider.DefaultReleaseChannel.DisplayName,
                    uiPreferencesStore.Load().ProviderSwitchReviewAcknowledged);
                ProviderSwitchPreviewText.Text = review.Summary;
                if (review.RequiresReview)
                {
                    ProviderSourceSelector.IsEnabled = true;
                    SetProviderSwitchAction("Switch", targetProvider, enabled: true);
                    ProviderSwitchActionButton.Focus();
                    return;
                }
            }
            operationWasPrepared = true;
            var result = await providerSourceSwitchCoordinator.ExecuteAsync(
                pendingProviderSwitch,
                pendingProviderSwitch.ConfirmationText,
                lifetimeCancellation.Token);
            var selectedProvider = distributionProviderCatalog.GetProvider(result.Selection.ProviderId);
            ProviderSwitchPreviewText.Text = result.ConfigurationBackup is null
                ? result.Message
                : $"{result.Message} The prior TOML is protected in the {result.ConfigurationBackup.ProviderId} history.";
            AcknowledgeProviderSwitchReview();
            var session = providerSessions.Recompose(result.Selection);
            ApplyProviderSession(session);
            ProviderSourceSelector.ItemsSource = distributionProviderCatalog.Providers.Values
                .OrderBy(provider => provider.DisplayName, StringComparer.Ordinal)
                .ToArray();
            ProviderSourceSelector.SelectedValue = selectedProvider.Id;
            ProviderSwitchPreviewText.Text = result.ConfigurationBackup is null
                ? $"{result.Message} The {selectedProvider.DisplayName} workspace is active."
                : $"{result.Message} The prior TOML is protected in the {result.ConfigurationBackup.ProviderId} history. The {selectedProvider.DisplayName} workspace is active.";
            ProviderSwitchActionButton.IsEnabled = false;
            UpdateProviderCapabilityText();
        }
        catch (OperationCanceledException)
        {
            ProviderSwitchPreviewText.Text = "The provider switch was canceled.";
            pendingProviderSwitch = null;
            ResetProviderSwitchReviewControls();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or KeyNotFoundException
                or HttpRequestException)
        {
            ProviderSwitchPreviewText.Text = providerSessions.HasPendingRecomposition
                ? "The provider switch committed, but its workspace refresh needs attention."
                : operationWasPrepared
                    ? $"The provider switch failed: {exception.Message}"
                    : $"The provider switch could not be prepared: {exception.Message}";
            if (providerSessions.HasPendingRecomposition)
            {
                ShowProviderRecompositionFailure(exception);
            }
            pendingProviderSwitch = null;
            ResetProviderSwitchReviewControls();
        }
        finally
        {
            isProviderSwitchOperationPending = false;
            if (pendingProviderSwitch is not null)
            {
                ProviderSwitchActionButton.IsEnabled = true;
            }
        }
    }

    private void ResetProviderSwitchReviewControls()
    {
        ProviderSourceSelector.IsEnabled = !providerSessions.HasPendingRecomposition;
        ProviderSwitchActionButton.IsEnabled =
            !providerSessions.HasPendingRecomposition
            && ProviderSourceSelector.SelectedItem is LauncherDistributionProvider provider
            && (!providerSelectionResolution.IsResolved
                || !string.Equals(provider.Id, distributionProvider.Id, StringComparison.Ordinal));
        if (ProviderSourceSelector.SelectedItem is LauncherDistributionProvider targetProvider
            && ProviderSwitchActionButton.IsEnabled)
        {
            SetProviderSwitchAction(
                uiPreferencesStore.Load().ProviderSwitchReviewAcknowledged ? "Switch" : "Review",
                targetProvider,
                enabled: true);
        }
        else
        {
            SetProviderSwitchAction("Switch", targetProvider: null, enabled: false);
        }
    }

    private void SetProviderSwitchAction(
        string verb,
        LauncherDistributionProvider? targetProvider,
        bool enabled)
    {
        ProviderSwitchActionButton.IsEnabled = enabled;
        if (targetProvider is null)
        {
            ProviderSwitchActionButton.Content = "_Switch source";
            AutomationProperties.SetName(ProviderSwitchActionButton, "Switch community mod source");
            return;
        }

        var sourceName = providerSelectionResolution.IsResolved
            ? distributionProvider.DisplayName
            : providerSelectionResolution.Selection.ProviderId;
        ProviderSwitchActionButton.Content = $"_{verb} {sourceName} → {targetProvider.DisplayName}";
        AutomationProperties.SetName(
            ProviderSwitchActionButton,
            $"{verb} community mod source from {sourceName} to {targetProvider.DisplayName}");
    }

    private void AcknowledgeProviderSwitchReview()
    {
        try
        {
            var preferences = uiPreferencesStore.Load();
            if (!preferences.ProviderSwitchReviewAcknowledged)
            {
                uiPreferencesStore.Save(preferences with { ProviderSwitchReviewAcknowledged = true });
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The switch is already committed. A preferences failure must not
            // misreport it as a failed or incomplete provider transaction.
        }
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
        ProviderCapabilityText.Text = LauncherProviderPresentation.Describe(provider);
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
            $"Mod Bridge color mode, {selectedColorMode}");
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
            isMaximized ? "Restore Mod Bridge" : "Maximize Mod Bridge");
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
        if (!isOpen && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Refresh();
        }

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
            var stateDirectory = PerUserInstallLayout.FromCurrentUser().StateDirectory;
            var backupStore = new ProviderScopedConfigurationBackupStore(stateDirectory);
            var mutationBackup = new ProviderScopedConfigurationMutationBackup(
                backupStore,
                distributionProvider.Id,
                $"{distributionProvider.Id}/{distributionReleaseChannel.Id}");
            openRawTomlCommand = new RelayCommand(OpenRawConfiguration, CanOpenRawConfiguration);
            settingsViewModel = new SettingsViewModel(
                catalog,
                new RelayCommand(() => SetSettingsWorkspaceOpen(false)),
                openRawTomlCommand,
                GetConfigurationFilePath,
                startupComposition.SettingsLayout,
                startupComposition.SettingsDiagnostics,
                repository: new TomlConfigurationRepository(mutationBackup: mutationBackup),
                uiPreferencesStore: uiPreferencesStore,
                openExternalUri: OpenExternalUri,
                openDataFolder: OpenApplicationDataFolder,
                manageApplication: OpenWindowsInstalledApps,
                openReleaseSecurityGuidance: OpenReleaseSecurityGuidance);
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

    private void OpenExternalUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Mod Bridge opens HTTPS links only.");
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            SettingsUnavailableMessage.Text =
                $"Windows could not open this link: {exception.Message}";
            SettingsUnavailableDialog.IsOpen = true;
        }
    }

    private void OpenApplicationDataFolder()
    {
        try
        {
            var stateDirectory = PerUserInstallLayout.FromCurrentUser().StateDirectory;
            Directory.CreateDirectory(stateDirectory);
            _ = Process.Start(new ProcessStartInfo(stateDirectory) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            SettingsUnavailableMessage.Text = $"Windows could not open the Mod Bridge data folder: {exception.Message}";
            SettingsUnavailableDialog.IsOpen = true;
        }
    }

    private void OpenWindowsInstalledApps()
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            SettingsUnavailableMessage.Text =
                $"Windows could not open Installed Apps: {exception.Message}";
            SettingsUnavailableDialog.IsOpen = true;
        }
    }

    private void OpenReleaseSecurityGuidance()
    {
        var guidance = new ReleaseSecurityGuidanceWindow
        {
            Owner = this,
        };
        _ = guidance.ShowDialog();
    }

    private void ReleaseSecurityGuidanceButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        OpenReleaseSecurityGuidance();
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

        if (Interlocked.Exchange(ref isProcessStateRefreshPending, 1) != 0)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                Interlocked.Exchange(ref isProcessStateRefreshPending, 0);
                if (!isDisposed)
                {
                    shellLifecycleController.HandleGameProcessChanged();
                }
            });
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
