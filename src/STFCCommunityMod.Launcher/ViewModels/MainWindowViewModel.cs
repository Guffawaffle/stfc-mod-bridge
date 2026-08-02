using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Windows.Input;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

internal sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly LauncherEnvironmentProbe environmentProbe;
    private readonly ModManagementCoordinator modManagementCoordinator;
    private readonly GameLaunchHandoffCoordinator gameLaunchCoordinator;
    private readonly LauncherDiagnosticService diagnosticService;
    private readonly LauncherSelfUpdateService launcherSelfUpdateService;
    private readonly ILauncherReleaseDiscoveryClient releaseDiscoveryClient;
    private readonly ILauncherUiPreferencesStore uiPreferencesStore;
    private LauncherEnvironmentSnapshot snapshot;
    private LauncherHomePresentation presentation;
    private ModManagementPresentation modPresentation;
    private GameLaunchPresentation launchPresentation;
    private string selectionFeedback = string.Empty;
    private readonly LauncherActionFeedbackChannels actionFeedback = new();
    private readonly HomeActionFeedbackArbiter homeFeedback;
    private LauncherLaunchTarget selectedLaunchTarget;

    private MainWindowViewModel(
        LauncherEnvironmentProbe environmentProbe,
        ModManagementCoordinator modManagementCoordinator,
        GameLaunchHandoffCoordinator gameLaunchCoordinator,
        LauncherDiagnosticService diagnosticService,
        LauncherSelfUpdateService launcherSelfUpdateService,
        ILauncherReleaseDiscoveryClient releaseDiscoveryClient,
        ILauncherUiPreferencesStore uiPreferencesStore)
    {
        this.environmentProbe = environmentProbe;
        this.modManagementCoordinator = modManagementCoordinator;
        this.gameLaunchCoordinator = gameLaunchCoordinator;
        this.diagnosticService = diagnosticService;
        this.launcherSelfUpdateService = launcherSelfUpdateService;
        this.releaseDiscoveryClient = releaseDiscoveryClient;
        this.uiPreferencesStore = uiPreferencesStore;
        selectedLaunchTarget = uiPreferencesStore.Load().LaunchTarget;
        homeFeedback = new(actionFeedback.Mod, actionFeedback.Launch);
        homeFeedback.PropertyChanged += HomeFeedback_PropertyChanged;
        snapshot = environmentProbe.Capture();
        presentation = LauncherHomePresentation.FromSnapshot(snapshot);
        modPresentation = modManagementCoordinator.CapturePresentation(
            snapshot.SelectedGameDirectory,
            snapshot.IsGameRunning);
        launchPresentation = gameLaunchCoordinator.CapturePresentation(
            snapshot.SelectedGameDirectory,
            selectedLaunchTarget);
        actionFeedback.Refresh.PropertyChanged += RefreshActionState_PropertyChanged;
        actionFeedback.Mod.PropertyChanged += ModActionState_PropertyChanged;
        actionFeedback.Launch.PropertyChanged += LaunchActionState_PropertyChanged;
        actionFeedback.LauncherUpdate.PropertyChanged += LauncherUpdateActionState_PropertyChanged;
        RefreshCommand = new ObservableActionCommand(
            actionFeedback.Refresh,
            "Refresh accepted. Checking Mod Control status…",
            RefreshStatusAsync,
            exception => $"Mod Control status refresh failed: {exception.Message}");
        LaunchPrimaryCommand = new ObservableActionCommand(
            actionFeedback.Launch,
            "Launch accepted. Starting the selected target…",
            LaunchSelectedTargetAsync,
            exception => $"The selected launch target failed: {exception.Message}");
        SelectPrimeExecutableCommand = new RelayCommand(
            () => SelectLaunchTarget(LauncherLaunchTarget.PrimeExecutable));
        SelectScopelyLauncherCommand = new RelayCommand(
            () => SelectLaunchTarget(LauncherLaunchTarget.ScopelyLauncher));
        UpdateModActionAvailability();
        UpdateLaunchActionAvailability();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string GameFolderStatus => presentation.GameFolderStatus;

    public string GameFolderIcon => presentation.GameFolderIcon;

    public LauncherHomeTone GameFolderTone => presentation.GameFolderTone;

    public string GameFolderStatusAutomationName => presentation.GameFolderStatusAutomationName;

    public string GameFolderActionLabel => presentation.GameFolderActionLabel;

    public string GameFolderActionAutomationName => presentation.GameFolderActionAutomationName;

    public string GameClientStatus => presentation.GameClientStatus;

    public string GameClientIcon => presentation.GameClientIcon;

    public LauncherHomeTone GameClientTone => presentation.GameClientTone;

    public string GameClientStatusAutomationName => presentation.GameClientStatusAutomationName;

    public bool IsGameRunning => presentation.IsGameRunning;

    public string ModStatus => modPresentation.Status;

    public LauncherHomeTone ModTone => modPresentation.Tone;

    public string ModActionLabel => actionFeedback.Mod.IsWorking ? "Working…" : modPresentation.ActionLabel;

    public string ModActionAutomationName => actionFeedback.Mod.IsWorking
        ? actionFeedback.Mod.AutomationAnnouncement
        : modPresentation.AutomationName;

    public bool CanManageMod => actionFeedback.Mod.IsCommandAvailable && !actionFeedback.Launch.IsWorking;

    public ModManagementActionKind ModActionKind => modPresentation.ActionKind;

    public bool CanRecoverMod =>
        modPresentation.ActionKind == ModManagementActionKind.Recover
        && actionFeedback.CanStartModMaintenance(modPresentation.CanExecute, actionFeedback.Launch.IsWorking);

    public bool CanUninstallMod =>
        modPresentation.ActionKind == ModManagementActionKind.CheckForUpdate
        && actionFeedback.CanStartModMaintenance(modPresentation.CanExecute, actionFeedback.Launch.IsWorking);

    public string LaunchActionLabel => actionFeedback.Launch.IsWorking ? "Opening…" : launchPresentation.ActionLabel;

    public string LaunchActionAutomationName => actionFeedback.Launch.IsWorking
        ? actionFeedback.Launch.AutomationAnnouncement
        : launchPresentation.AutomationName;

    public bool CanLaunchGame => actionFeedback.Launch.IsCommandAvailable && !actionFeedback.Mod.IsWorking;

    public LauncherLaunchTarget SelectedLaunchTarget => selectedLaunchTarget;

    public bool IsPrimeExecutableSelected => selectedLaunchTarget == LauncherLaunchTarget.PrimeExecutable;

    public bool IsScopelyLauncherSelected => selectedLaunchTarget == LauncherLaunchTarget.ScopelyLauncher;

    public string PrimeExecutableChoiceAutomationName => BuildChoiceAutomationName(
        LauncherLaunchTarget.PrimeExecutable,
        "Launch prime.exe");

    public string ScopelyLauncherChoiceAutomationName => BuildChoiceAutomationName(
        LauncherLaunchTarget.ScopelyLauncher,
        "Open Scopely launcher");

    public string PrimeExecutableChoiceStatus => BuildChoiceStatus(LauncherLaunchTarget.PrimeExecutable);

    public string ScopelyLauncherChoiceStatus => BuildChoiceStatus(LauncherLaunchTarget.ScopelyLauncher);

    public bool CanOpenLaunchTargetMenu => Enum.IsDefined(selectedLaunchTarget);

    public ICommand LaunchPrimaryCommand { get; }

    public ICommand SelectPrimeExecutableCommand { get; }

    public ICommand SelectScopelyLauncherCommand { get; }

    public string ModOperationFeedback => actionFeedback.Mod.StatusText;

    public bool HasModOperationFeedback => actionFeedback.Mod.HasStatus;

    public bool IsModOperationInProgress => actionFeedback.Mod.IsWorking;

    public bool IsLaunchInProgress => actionFeedback.Launch.IsWorking;

    public string HomeOperationFeedback => homeFeedback.Text;

    public bool HasHomeOperationFeedback => homeFeedback.HasFeedback;

    public string? SelectedGameDirectory => snapshot.SelectedGameDirectory;

    public string SelectionFeedback => selectionFeedback;

    public bool HasSelectionFeedback => !string.IsNullOrWhiteSpace(selectionFeedback);

    public ICommand RefreshCommand { get; }

    public string RefreshActionLabel => actionFeedback.Refresh.IsWorking ? "_Refreshing…" : "_Refresh status";

    public string RefreshActionAutomationName => actionFeedback.Refresh.IsWorking
        ? actionFeedback.Refresh.AutomationAnnouncement
        : "Refresh Mod Control status";

    public string RefreshActionStatus => actionFeedback.Refresh.StatusText;

    public bool CanRefresh => actionFeedback.Refresh.IsCommandAvailable;

    public string LauncherUpdateActionLabel => actionFeedback.LauncherUpdate.IsWorking
        ? "Checking for Mod Control update…"
        : "Check Mod Control _update";

    public string LauncherUpdateActionAutomationName => actionFeedback.LauncherUpdate.IsWorking
        ? actionFeedback.LauncherUpdate.AutomationAnnouncement
        : "Check for a Mod Control self-update";

    public string LauncherUpdateFeedback => actionFeedback.LauncherUpdate.StatusText;

    public bool CanCheckLauncherUpdate => actionFeedback.LauncherUpdate.IsCommandAvailable;

    public string? InitialBrowseDirectory
    {
        get
        {
            var validCandidates = snapshot.Discovery.ValidCandidates;
            return snapshot.SelectedGameDirectory
                ?? (validCandidates.Count > 0 ? validCandidates[0].GameDirectory : null);
        }
    }

    public string? ConfigurationFilePath =>
        snapshot.SelectedGameDirectory is null
            ? null
            : Path.Combine(snapshot.SelectedGameDirectory, "community_patch_settings.toml");

    public static MainWindowViewModel CreateDefault(
        HttpClient httpClient,
        LauncherDistributionProvider distributionProvider,
        LauncherProviderReleaseChannel releaseChannel,
        string? providerResolutionFailure = null,
        ILauncherUiPreferencesStore? uiPreferencesStore = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(distributionProvider);
        ArgumentNullException.ThrowIfNull(releaseChannel);
        var installLayout = PerUserInstallLayout.FromCurrentUser();
        uiPreferencesStore ??= new JsonLauncherUiPreferencesStore(installLayout.StateDirectory);
        var currentLauncherVersion = CurrentLauncherVersion();
        var processInspector = new SystemGameProcessInspector();
        var installDiscovery = new GameInstallDiscovery(
            new JsonGameInstallSelectionStore(installLayout.StateDirectory),
            [
                OfficialLauncherSettingsCandidateProvider.FromCurrentUser(),
                BoundedGameInstallCandidateProvider.FromCurrentMachine(),
            ]);

        var providerBinding = LauncherProviderModBinding.Resolve(
            distributionProvider,
            releaseChannel,
            providerResolutionFailure);
        IModArtifactAuthenticityVerifier modArtifactVerifier =
            providerBinding.IsAvailable
                ? new WindowsAuthenticodeVerifier(providerBinding.WindowsPublisher!)
                : new FailClosedModArtifactAuthenticityVerifier(providerBinding.UnavailableReason);
        IWindowsReleaseDiscoveryClient modReleaseClient =
            providerBinding.IsAvailable
                ? new GitHubWindowsReleaseClient(
                    httpClient,
                    providerBinding.Repository,
                    providerBinding.ManifestAssetName!)
                : new UnavailableWindowsReleaseDiscoveryClient(providerBinding.UnavailableReason);

        var deploymentService = new ModDeploymentService(
            installLayout.StateDirectory,
            new HttpModArtifactDownloader(httpClient),
            new WindowsModArtifactVersionReader(),
            modArtifactVerifier,
            processInspector.IsGameRunning);
        var launcherReleaseClient = new GitHubLauncherReleaseClient(
            httpClient,
            LauncherSelfUpdateAuthority.ReleaseRepository,
            LauncherSelfUpdateAuthority.ReleaseManifestAssetName);
        var officialLauncherService = WindowsOfficialLauncherService.FromCurrentUser();
        var launchCoordinator = new GameLaunchHandoffCoordinator(
            installLayout.StateDirectory,
            deploymentService,
            new WindowsGameExecutableLaunchService(),
            officialLauncherService,
            processInspector);
        return new(
            new LauncherEnvironmentProbe(
                processInspector,
                installLayout,
                installDiscovery),
            new ModManagementCoordinator(
                deploymentService,
                modReleaseClient,
                currentLauncherVersion,
                providerBinding.ReleaseChannelId,
                providerUnavailableReason: providerBinding.UnavailableReason),
            launchCoordinator,
            new LauncherDiagnosticService(
                deploymentService,
                officialLauncherService,
                processInspector,
                currentLauncherVersion.ToString(3)),
            new LauncherSelfUpdateService(
                installLayout.StateDirectory,
                installLayout.ProgramDirectory,
                new HttpLauncherArchiveDownloader(httpClient),
                new WindowsAuthenticodeVerifier(LauncherSelfUpdateAuthority.WindowsArtifactPublisher),
                new WindowsLauncherArtifactIdentityReader()),
            launcherReleaseClient,
            uiPreferencesStore);
    }

    public void ConfirmManualSelection(string gameDirectory)
    {
        var candidate = environmentProbe.ConfirmManualSelection(gameDirectory);
        selectionFeedback = candidate.Validation.IsValid
            ? "Game folder saved."
            : candidate.Validation.Message;
        OnPropertyChanged(nameof(SelectionFeedback));
        OnPropertyChanged(nameof(HasSelectionFeedback));
    }

    public void Refresh()
    {
        RefreshCore();
    }

    private async Task<ObservableActionResult> RefreshStatusAsync()
    {
        var before = CaptureHomeState();
        await Task.Yield();
        RefreshCore();
        var changed = before != CaptureHomeState();
        return changed
            ? ObservableActionResult.Changed("Mod Control status refreshed. The displayed status changed.")
            : ObservableActionResult.Unchanged("Mod Control status is up to date. No changes were found.");
    }

    private void RefreshCore()
    {
        snapshot = environmentProbe.Capture();
        presentation = LauncherHomePresentation.FromSnapshot(snapshot);
        modPresentation = modManagementCoordinator.CapturePresentation(
            snapshot.SelectedGameDirectory,
            snapshot.IsGameRunning);
        launchPresentation = gameLaunchCoordinator.CapturePresentation(
            snapshot.SelectedGameDirectory,
            selectedLaunchTarget);
        OnPropertyChanged(nameof(GameFolderStatus));
        OnPropertyChanged(nameof(GameFolderIcon));
        OnPropertyChanged(nameof(GameFolderTone));
        OnPropertyChanged(nameof(GameFolderStatusAutomationName));
        OnPropertyChanged(nameof(GameFolderActionLabel));
        OnPropertyChanged(nameof(GameFolderActionAutomationName));
        OnPropertyChanged(nameof(GameClientStatus));
        OnPropertyChanged(nameof(GameClientIcon));
        OnPropertyChanged(nameof(GameClientTone));
        OnPropertyChanged(nameof(GameClientStatusAutomationName));
        OnPropertyChanged(nameof(IsGameRunning));
        NotifyModPresentationChanged();
        NotifyLaunchPresentationChanged();
        OnPropertyChanged(nameof(InitialBrowseDirectory));
        OnPropertyChanged(nameof(ConfigurationFilePath));
        OnPropertyChanged(nameof(SelectedGameDirectory));
        UpdateModActionAvailability();
        UpdateLaunchActionAvailability();
    }

    public async Task<ModOperationPreparation?> PrepareModOperationAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanManageMod || snapshot.SelectedGameDirectory is null)
        {
            return null;
        }

        if (!actionFeedback.Mod.TryBegin("Release check accepted. Checking the selected release…"))
        {
            return null;
        }
        try
        {
            var preparation = await modManagementCoordinator.PrepareLatestAsync(
                snapshot.SelectedGameDirectory,
                snapshot.IsGameRunning,
                cancellationToken);
            if (preparation.State == ModOperationPreparationState.UpToDate)
            {
                actionFeedback.Mod.Complete(false, preparation.Message);
            }
            else
            {
                actionFeedback.Mod.Complete(
                    true,
                    $"Community mod {preparation.ReleaseVersion} is ready for confirmation.");
            }
            return preparation;
        }
        catch (OperationCanceledException)
        {
            actionFeedback.Mod.Cancel("The release check was canceled or timed out.");
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            actionFeedback.Mod.Fail($"Could not prepare the mod operation: {exception.Message}");
            return null;
        }
    }

    public async Task<ModDeploymentResult?> ExecuteModOperationAsync(
        ModOperationPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!actionFeedback.Mod.TryBegin("Installation accepted. Installing the verified community mod…"))
        {
            return null;
        }

        try
        {
            var result = await modManagementCoordinator.ExecuteAsync(preparation, cancellationToken);
            actionFeedback.CompleteModDeployment(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            actionFeedback.Mod.Cancel("The mod operation was canceled.");
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException)
        {
            actionFeedback.Mod.Fail($"The mod operation failed: {exception.Message}");
            return null;
        }
        finally
        {
            Refresh();
        }
    }

    private async Task<ObservableActionResult> LaunchSelectedTargetAsync()
    {
        var result = await gameLaunchCoordinator.LaunchAsync(
            snapshot.SelectedGameDirectory,
            selectedLaunchTarget);
        RefreshCore();
        return ProjectLaunchResult(result);
    }

    internal static ObservableActionResult ProjectLaunchResult(GameLaunchHandoffResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.State switch
        {
            GameLaunchHandoffState.Completed when result.Changed => ObservableActionResult.Changed(result.Message),
            GameLaunchHandoffState.Completed => ObservableActionResult.Unchanged(result.Message),
            GameLaunchHandoffState.Failed => ObservableActionResult.Failed(result.Message),
            _ => ObservableActionResult.Unchanged(result.Message),
        };
    }

    public LauncherDiagnosticPreview BuildDiagnosticPreview() =>
        diagnosticService.BuildPreview(snapshot.SelectedGameDirectory);

    public static Task ExportDiagnosticsAsync(
        LauncherDiagnosticPreview preview,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        LauncherDiagnosticService.ExportAsync(preview, outputPath, cancellationToken);

    public async Task<LauncherUpdatePreparation?> PrepareLauncherUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!actionFeedback.LauncherUpdate.TryBegin("Mod Control update check accepted. Checking for an update…"))
        {
            return null;
        }
        try
        {
            var discovery = await releaseDiscoveryClient.DiscoverLatestAsync(
                "stable",
                CurrentLauncherVersion(),
                cancellationToken);
            var preparation = await launcherSelfUpdateService.PrepareAsync(
                discovery,
                CurrentSourceCommit(),
                Environment.ProcessId,
                cancellationToken);
            actionFeedback.LauncherUpdate.Complete(
                preparation.State == LauncherUpdatePreparationState.Ready,
                preparation.Message);
            return preparation;
        }
        catch (OperationCanceledException)
        {
            actionFeedback.LauncherUpdate.Cancel("The Mod Control update check was canceled or timed out.");
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            actionFeedback.LauncherUpdate.Fail($"The Mod Control update could not be prepared: {exception.Message}");
            return null;
        }
    }

    public static void StartLauncherUpdate(LauncherUpdatePreparation preparation) =>
        LauncherSelfUpdateService.StartUpdater(preparation);

    private static string CurrentSourceCommit()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var separator = informational?.LastIndexOf('+') ?? -1;
        return separator >= 0 ? informational![(separator + 1)..] : string.Empty;
    }

    private static Version CurrentLauncherVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version
        ?? throw new InvalidOperationException("The Mod Control assembly version is unavailable.");

    public async Task<ModDeploymentResult?> RecoverModAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRecoverMod)
        {
            return null;
        }
        return await ExecuteMaintenanceAsync(
            "Recovering the incomplete mod transaction…",
            modManagementCoordinator.RecoverAsync,
            cancellationToken);
    }

    public async Task<ModDeploymentResult?> UninstallModAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUninstallMod)
        {
            return null;
        }
        return await ExecuteMaintenanceAsync(
            "Removing the Mod Control-managed community mod…",
            modManagementCoordinator.UninstallAsync,
            cancellationToken);
    }

    private async Task<ModDeploymentResult?> ExecuteMaintenanceAsync(
        string progress,
        Func<CancellationToken, Task<ModDeploymentResult>> operation,
        CancellationToken cancellationToken)
    {
        if (!actionFeedback.Mod.TryBegin(progress))
        {
            return null;
        }
        try
        {
            var result = await operation(cancellationToken);
            actionFeedback.CompleteModDeployment(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            actionFeedback.Mod.Cancel("The maintenance operation was canceled.");
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            actionFeedback.Mod.Fail($"The maintenance operation failed: {exception.Message}");
            return null;
        }
        finally
        {
            Refresh();
        }
    }

    private void NotifyModPresentationChanged()
    {
        OnPropertyChanged(nameof(ModStatus));
        OnPropertyChanged(nameof(ModTone));
        OnPropertyChanged(nameof(ModActionLabel));
        OnPropertyChanged(nameof(ModActionAutomationName));
        OnPropertyChanged(nameof(CanManageMod));
        OnPropertyChanged(nameof(ModActionKind));
        OnPropertyChanged(nameof(CanRecoverMod));
        OnPropertyChanged(nameof(CanUninstallMod));
        UpdateModActionAvailability();
    }

    private void UpdateModActionAvailability() =>
        actionFeedback.Mod.SetAvailability(modPresentation.CanExecute, modPresentation.AutomationName);

    private void UpdateLaunchActionAvailability() =>
        actionFeedback.Launch.SetAvailability(
            launchPresentation.CanExecute && !actionFeedback.Mod.IsWorking,
            launchPresentation.AutomationName);

    private void SelectLaunchTarget(LauncherLaunchTarget target)
    {
        if (selectedLaunchTarget == target)
        {
            return;
        }

        selectedLaunchTarget = target;
        try
        {
            uiPreferencesStore.Save(uiPreferencesStore.Load() with { LaunchTarget = target });
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            // Launcher UI preferences are best-effort; selection remains valid for this session.
        }
        launchPresentation = gameLaunchCoordinator.CapturePresentation(
            snapshot.SelectedGameDirectory,
            selectedLaunchTarget);
        OnPropertyChanged(nameof(SelectedLaunchTarget));
        OnPropertyChanged(nameof(IsPrimeExecutableSelected));
        OnPropertyChanged(nameof(IsScopelyLauncherSelected));
        OnPropertyChanged(nameof(PrimeExecutableChoiceAutomationName));
        OnPropertyChanged(nameof(ScopelyLauncherChoiceAutomationName));
        OnPropertyChanged(nameof(PrimeExecutableChoiceStatus));
        OnPropertyChanged(nameof(ScopelyLauncherChoiceStatus));
        NotifyLaunchPresentationChanged();
        UpdateLaunchActionAvailability();
    }

    private string BuildChoiceAutomationName(LauncherLaunchTarget target, string label)
    {
        var choice = gameLaunchCoordinator.CapturePresentation(snapshot.SelectedGameDirectory, target);
        var selected = selectedLaunchTarget == target ? ", selected" : string.Empty;
        var availability = choice.CanExecute
            ? $", available, {choice.Reason}"
            : $", unavailable, {choice.Reason}, {choice.NextActionLabel}";
        return $"{label}{selected}{availability}";
    }

    private string BuildChoiceStatus(LauncherLaunchTarget target)
    {
        var choice = gameLaunchCoordinator.CapturePresentation(snapshot.SelectedGameDirectory, target);
        return choice.CanExecute
            ? choice.Reason
            : $"Unavailable · {choice.Reason} · {choice.NextActionLabel}";
    }

    private HomeState CaptureHomeState() => new(
        snapshot.HealthCode,
        snapshot.IsGameRunning,
        snapshot.SelectedGameDirectory,
        presentation.GameFolderStatus,
        presentation.GameClientStatus,
        modPresentation.Status,
        modPresentation.ActionKind,
        modPresentation.CanExecute,
        launchPresentation.ActionLabel,
        launchPresentation.CanExecute);

    private void RefreshActionState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        switch (e.PropertyName)
        {
            case nameof(ObservableActionState.IsWorking):
                OnPropertyChanged(nameof(RefreshActionLabel));
                break;
            case nameof(ObservableActionState.AutomationAnnouncement):
                OnPropertyChanged(nameof(RefreshActionAutomationName));
                break;
            case nameof(ObservableActionState.StatusText):
                OnPropertyChanged(nameof(RefreshActionStatus));
                break;
            case nameof(ObservableActionState.IsCommandAvailable):
                OnPropertyChanged(nameof(CanRefresh));
                break;
        }
    }

    private void ModActionState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        switch (e.PropertyName)
        {
            case nameof(ObservableActionState.IsWorking):
                OnPropertyChanged(nameof(IsModOperationInProgress));
                OnPropertyChanged(nameof(ModActionLabel));
                OnPropertyChanged(nameof(CanRecoverMod));
                OnPropertyChanged(nameof(CanUninstallMod));
                OnPropertyChanged(nameof(CanLaunchGame));
                break;
            case nameof(ObservableActionState.AutomationAnnouncement):
                OnPropertyChanged(nameof(ModActionAutomationName));
                break;
            case nameof(ObservableActionState.StatusText):
                OnPropertyChanged(nameof(ModOperationFeedback));
                break;
            case nameof(ObservableActionState.HasStatus):
                OnPropertyChanged(nameof(HasModOperationFeedback));
                break;
            case nameof(ObservableActionState.IsCommandAvailable):
                OnPropertyChanged(nameof(CanManageMod));
                OnPropertyChanged(nameof(CanRecoverMod));
                OnPropertyChanged(nameof(CanUninstallMod));
                break;
        }
    }

    private void LauncherUpdateActionState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        switch (e.PropertyName)
        {
            case nameof(ObservableActionState.IsWorking):
                OnPropertyChanged(nameof(LauncherUpdateActionLabel));
                break;
            case nameof(ObservableActionState.AutomationAnnouncement):
                OnPropertyChanged(nameof(LauncherUpdateActionAutomationName));
                break;
            case nameof(ObservableActionState.StatusText):
                OnPropertyChanged(nameof(LauncherUpdateFeedback));
                break;
            case nameof(ObservableActionState.IsCommandAvailable):
                OnPropertyChanged(nameof(CanCheckLauncherUpdate));
                break;
        }
    }

    private void LaunchActionState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        switch (e.PropertyName)
        {
            case nameof(ObservableActionState.IsWorking):
                OnPropertyChanged(nameof(IsLaunchInProgress));
                OnPropertyChanged(nameof(LaunchActionLabel));
                OnPropertyChanged(nameof(CanManageMod));
                OnPropertyChanged(nameof(CanRecoverMod));
                OnPropertyChanged(nameof(CanUninstallMod));
                break;
            case nameof(ObservableActionState.AutomationAnnouncement):
                OnPropertyChanged(nameof(LaunchActionAutomationName));
                break;
            case nameof(ObservableActionState.IsCommandAvailable):
                OnPropertyChanged(nameof(CanLaunchGame));
                break;
        }
    }

    private void HomeFeedback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName == nameof(HomeActionFeedbackArbiter.Text))
        {
            OnPropertyChanged(nameof(HomeOperationFeedback));
        }
        else if (e.PropertyName == nameof(HomeActionFeedbackArbiter.HasFeedback))
        {
            OnPropertyChanged(nameof(HasHomeOperationFeedback));
        }
    }

    private void NotifyLaunchPresentationChanged()
    {
        OnPropertyChanged(nameof(LaunchActionLabel));
        OnPropertyChanged(nameof(LaunchActionAutomationName));
        OnPropertyChanged(nameof(CanLaunchGame));
        OnPropertyChanged(nameof(PrimeExecutableChoiceAutomationName));
        OnPropertyChanged(nameof(ScopelyLauncherChoiceAutomationName));
        OnPropertyChanged(nameof(PrimeExecutableChoiceStatus));
        OnPropertyChanged(nameof(ScopelyLauncherChoiceStatus));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed record HomeState(
        LauncherHealthCode HealthCode,
        bool IsGameRunning,
        string? SelectedGameDirectory,
        string GameFolderStatus,
        string GameClientStatus,
        string ModStatus,
        ModManagementActionKind ModActionKind,
        bool CanExecuteModAction,
        string LaunchActionLabel,
        bool CanExecuteLaunchAction);
}
