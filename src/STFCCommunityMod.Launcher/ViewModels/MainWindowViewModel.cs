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
    private readonly IWindowsReleaseDiscoveryClient releaseDiscoveryClient;
    private LauncherEnvironmentSnapshot snapshot;
    private LauncherHomePresentation presentation;
    private ModManagementPresentation modPresentation;
    private GameLaunchPresentation launchPresentation;
    private string selectionFeedback = string.Empty;
    private readonly LauncherActionFeedbackChannels actionFeedback = new();
    private bool isLaunchInProgress;
    private string launchFeedback = string.Empty;

    private MainWindowViewModel(
        LauncherEnvironmentProbe environmentProbe,
        ModManagementCoordinator modManagementCoordinator,
        GameLaunchHandoffCoordinator gameLaunchCoordinator,
        LauncherDiagnosticService diagnosticService,
        LauncherSelfUpdateService launcherSelfUpdateService,
        IWindowsReleaseDiscoveryClient releaseDiscoveryClient)
    {
        this.environmentProbe = environmentProbe;
        this.modManagementCoordinator = modManagementCoordinator;
        this.gameLaunchCoordinator = gameLaunchCoordinator;
        this.diagnosticService = diagnosticService;
        this.launcherSelfUpdateService = launcherSelfUpdateService;
        this.releaseDiscoveryClient = releaseDiscoveryClient;
        snapshot = environmentProbe.Capture();
        presentation = LauncherHomePresentation.FromSnapshot(snapshot);
        modPresentation = modManagementCoordinator.CapturePresentation(
            snapshot.SelectedGameDirectory,
            snapshot.IsGameRunning);
        launchPresentation = gameLaunchCoordinator.CapturePresentation(snapshot.SelectedGameDirectory);
        actionFeedback.Refresh.PropertyChanged += RefreshActionState_PropertyChanged;
        actionFeedback.Mod.PropertyChanged += ModActionState_PropertyChanged;
        actionFeedback.LauncherUpdate.PropertyChanged += LauncherUpdateActionState_PropertyChanged;
        RefreshCommand = new ObservableActionCommand(
            actionFeedback.Refresh,
            "Refresh accepted. Checking launcher status…",
            RefreshStatusAsync,
            exception => $"Launcher status refresh failed: {exception.Message}");
        UpdateModActionAvailability();
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

    public bool CanManageMod => actionFeedback.Mod.IsCommandAvailable && !isLaunchInProgress;

    public ModManagementActionKind ModActionKind => modPresentation.ActionKind;

    public bool CanRecoverMod =>
        modPresentation.ActionKind == ModManagementActionKind.Recover
        && actionFeedback.CanStartModMaintenance(modPresentation.CanExecute, isLaunchInProgress);

    public bool CanUninstallMod =>
        modPresentation.ActionKind == ModManagementActionKind.CheckForUpdate
        && actionFeedback.CanStartModMaintenance(modPresentation.CanExecute, isLaunchInProgress);

    public string LaunchActionLabel => isLaunchInProgress ? "Official launcher open…" : launchPresentation.ActionLabel;

    public string LaunchActionAutomationName => isLaunchInProgress
        ? "Official Star Trek Fleet Command launcher handoff in progress"
        : launchPresentation.AutomationName;

    public bool CanLaunchGame => launchPresentation.CanExecute && !isLaunchInProgress && !actionFeedback.Mod.IsWorking;

    public string ModOperationFeedback => actionFeedback.Mod.StatusText;

    public bool HasModOperationFeedback => actionFeedback.Mod.HasStatus;

    public bool IsModOperationInProgress => actionFeedback.Mod.IsWorking;

    public bool IsLaunchInProgress => isLaunchInProgress;

    public string HomeOperationFeedback => !string.IsNullOrWhiteSpace(launchFeedback)
        ? launchFeedback
        : actionFeedback.Mod.StatusText;

    public bool HasHomeOperationFeedback => !string.IsNullOrWhiteSpace(HomeOperationFeedback);

    public string? SelectedGameDirectory => snapshot.SelectedGameDirectory;

    public string SelectionFeedback => selectionFeedback;

    public bool HasSelectionFeedback => !string.IsNullOrWhiteSpace(selectionFeedback);

    public ICommand RefreshCommand { get; }

    public string RefreshActionLabel => actionFeedback.Refresh.IsWorking ? "_Refreshing…" : "_Refresh status";

    public string RefreshActionAutomationName => actionFeedback.Refresh.IsWorking
        ? actionFeedback.Refresh.AutomationAnnouncement
        : "Refresh launcher status";

    public string RefreshActionStatus => actionFeedback.Refresh.StatusText;

    public bool CanRefresh => actionFeedback.Refresh.IsCommandAvailable;

    public string LauncherUpdateActionLabel => actionFeedback.LauncherUpdate.IsWorking
        ? "Checking for launcher update…"
        : "Check launcher _update";

    public string LauncherUpdateActionAutomationName => actionFeedback.LauncherUpdate.IsWorking
        ? actionFeedback.LauncherUpdate.AutomationAnnouncement
        : "Check for a launcher self-update";

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
        string? providerResolutionFailure = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(distributionProvider);
        ArgumentNullException.ThrowIfNull(releaseChannel);
        var installLayout = PerUserInstallLayout.FromCurrentUser();
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
        var launcherReleaseClient = new GitHubWindowsReleaseClient(
            httpClient,
            LauncherSelfUpdateAuthority.ReleaseRepository,
            LauncherSelfUpdateAuthority.ReleaseManifestAssetName);
        var officialLauncherService = WindowsOfficialLauncherService.FromCurrentUser();
        var launchCoordinator = new GameLaunchHandoffCoordinator(
            installLayout.StateDirectory,
            deploymentService,
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
                new Version(0, 1, 0),
                providerBinding.ReleaseChannelId,
                providerUnavailableReason: providerBinding.UnavailableReason),
            launchCoordinator,
            new LauncherDiagnosticService(
                deploymentService,
                officialLauncherService,
                processInspector,
                "0.1.0"),
            new LauncherSelfUpdateService(
                installLayout.StateDirectory,
                installLayout.ProgramDirectory,
                new HttpLauncherArchiveDownloader(httpClient),
                new WindowsAuthenticodeVerifier(LauncherSelfUpdateAuthority.WindowsArtifactPublisher),
                new WindowsLauncherArtifactIdentityReader()),
            launcherReleaseClient);
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
            ? ObservableActionResult.Changed("Launcher status refreshed. The displayed status changed.")
            : ObservableActionResult.Unchanged("Launcher status is up to date. No changes were found.");
    }

    private void RefreshCore()
    {
        snapshot = environmentProbe.Capture();
        presentation = LauncherHomePresentation.FromSnapshot(snapshot);
        modPresentation = modManagementCoordinator.CapturePresentation(
            snapshot.SelectedGameDirectory,
            snapshot.IsGameRunning);
        launchPresentation = gameLaunchCoordinator.CapturePresentation(snapshot.SelectedGameDirectory);
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

    public async Task<GameLaunchHandoffResult?> LaunchGameAsync(CancellationToken cancellationToken = default)
    {
        if (!CanLaunchGame || snapshot.SelectedGameDirectory is null)
        {
            return null;
        }

        SetLaunchState(true, "Opening the official Star Trek Fleet Command launcher…");
        try
        {
            var result = await gameLaunchCoordinator.LaunchAsync(
                snapshot.SelectedGameDirectory,
                GameLaunchMode.Modded,
                cancellationToken);
            launchFeedback = result.Message;
            return result;
        }
        catch (OperationCanceledException)
        {
            launchFeedback = "The official-launcher handoff was canceled.";
            return null;
        }
        finally
        {
            SetLaunchState(false, launchFeedback);
            Refresh();
        }
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
        if (!actionFeedback.LauncherUpdate.TryBegin("Launcher update check accepted. Checking for an update…"))
        {
            return null;
        }
        try
        {
            var discovery = await releaseDiscoveryClient.DiscoverLatestAsync(
                "stable",
                new Version(0, 1, 0),
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
            actionFeedback.LauncherUpdate.Cancel("The launcher update check was canceled or timed out.");
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            actionFeedback.LauncherUpdate.Fail($"The launcher update could not be prepared: {exception.Message}");
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
            "Removing the launcher-managed community mod…",
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
                OnPropertyChanged(nameof(HomeOperationFeedback));
                OnPropertyChanged(nameof(HasHomeOperationFeedback));
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

    private void SetLaunchState(bool isInProgress, string feedback)
    {
        isLaunchInProgress = isInProgress;
        launchFeedback = feedback;
        OnPropertyChanged(nameof(IsLaunchInProgress));
        OnPropertyChanged(nameof(LaunchActionLabel));
        OnPropertyChanged(nameof(LaunchActionAutomationName));
        OnPropertyChanged(nameof(CanLaunchGame));
        OnPropertyChanged(nameof(CanManageMod));
        OnPropertyChanged(nameof(CanRecoverMod));
        OnPropertyChanged(nameof(CanUninstallMod));
        OnPropertyChanged(nameof(HomeOperationFeedback));
        OnPropertyChanged(nameof(HasHomeOperationFeedback));
    }

    private void NotifyLaunchPresentationChanged()
    {
        OnPropertyChanged(nameof(LaunchActionLabel));
        OnPropertyChanged(nameof(LaunchActionAutomationName));
        OnPropertyChanged(nameof(CanLaunchGame));
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
