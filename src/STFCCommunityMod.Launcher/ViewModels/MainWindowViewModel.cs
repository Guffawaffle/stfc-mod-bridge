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
    private readonly ObservableActionState refreshActionState = new();
    private readonly ObservableActionState modActionState = new();
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
        refreshActionState.PropertyChanged += RefreshActionState_PropertyChanged;
        modActionState.PropertyChanged += ModActionState_PropertyChanged;
        RefreshCommand = new ObservableActionCommand(
            refreshActionState,
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

    public string ModActionLabel => modActionState.IsWorking ? "Working…" : modPresentation.ActionLabel;

    public string ModActionAutomationName => modActionState.IsWorking
        ? modActionState.AutomationAnnouncement
        : modPresentation.AutomationName;

    public bool CanManageMod => modActionState.IsCommandAvailable && !isLaunchInProgress;

    public ModManagementActionKind ModActionKind => modPresentation.ActionKind;

    public bool CanRecoverMod =>
        modPresentation.ActionKind == ModManagementActionKind.Recover && CanManageMod;

    public bool CanUninstallMod =>
        modPresentation.ActionKind == ModManagementActionKind.CheckForUpdate
        && modPresentation.CanExecute
        && !isLaunchInProgress;

    public string LaunchActionLabel => isLaunchInProgress ? "Official launcher open…" : launchPresentation.ActionLabel;

    public string LaunchActionAutomationName => isLaunchInProgress
        ? "Official Star Trek Fleet Command launcher handoff in progress"
        : launchPresentation.AutomationName;

    public bool CanLaunchGame => launchPresentation.CanExecute && !isLaunchInProgress && !modActionState.IsWorking;

    public string ModOperationFeedback => modActionState.StatusText;

    public bool HasModOperationFeedback => modActionState.HasStatus;

    public bool IsModOperationInProgress => modActionState.IsWorking;

    public bool IsLaunchInProgress => isLaunchInProgress;

    public string HomeOperationFeedback => !string.IsNullOrWhiteSpace(launchFeedback)
        ? launchFeedback
        : modActionState.StatusText;

    public bool HasHomeOperationFeedback => !string.IsNullOrWhiteSpace(HomeOperationFeedback);

    public string? SelectedGameDirectory => snapshot.SelectedGameDirectory;

    public string SelectionFeedback => selectionFeedback;

    public bool HasSelectionFeedback => !string.IsNullOrWhiteSpace(selectionFeedback);

    public ICommand RefreshCommand { get; }

    public string RefreshActionLabel => refreshActionState.IsWorking ? "_Refreshing…" : "_Refresh status";

    public string RefreshActionAutomationName => refreshActionState.IsWorking
        ? refreshActionState.AutomationAnnouncement
        : "Refresh launcher status";

    public string RefreshActionStatus => refreshActionState.StatusText;

    public bool HasRefreshActionStatus => refreshActionState.HasStatus;

    public bool CanRefresh => refreshActionState.IsCommandAvailable;

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
        LauncherDistributionProvider distributionProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(distributionProvider);
        var installLayout = PerUserInstallLayout.FromCurrentUser();
        var processInspector = new SystemGameProcessInspector();
        var installDiscovery = new GameInstallDiscovery(
            new JsonGameInstallSelectionStore(installLayout.StateDirectory),
            [
                OfficialLauncherSettingsCandidateProvider.FromCurrentUser(),
                BoundedGameInstallCandidateProvider.FromCurrentMachine(),
            ]);

        var deploymentService = new ModDeploymentService(
            installLayout.StateDirectory,
            new HttpModArtifactDownloader(httpClient),
            new WindowsModArtifactVersionReader(),
            new WindowsAuthenticodeVerifier(distributionProvider.WindowsArtifactPublisher),
            processInspector.IsGameRunning);
        var releaseClient = new GitHubWindowsReleaseClient(
            httpClient,
            distributionProvider.ModReleaseRepository,
            distributionProvider.ModReleaseManifestAssetName);
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
                releaseClient,
                new Version(0, 1, 0)),
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
                new WindowsAuthenticodeVerifier(distributionProvider.WindowsArtifactPublisher),
                new WindowsLauncherArtifactIdentityReader()),
            releaseClient);
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

        if (!modActionState.TryBegin("Release check accepted. Checking the selected release…"))
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
                modActionState.Complete(false, preparation.Message);
            }
            else
            {
                modActionState.Complete(
                    true,
                    $"Community mod {preparation.ReleaseVersion} is ready for confirmation.");
            }
            return preparation;
        }
        catch (OperationCanceledException)
        {
            modActionState.Cancel("The release check was canceled or timed out.");
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            modActionState.Fail($"Could not prepare the mod operation: {exception.Message}");
            return null;
        }
    }

    public async Task<ModDeploymentResult?> ExecuteModOperationAsync(
        ModOperationPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!modActionState.TryBegin("Installation accepted. Installing the verified community mod…"))
        {
            return null;
        }

        try
        {
            var result = await modManagementCoordinator.ExecuteAsync(preparation, cancellationToken);
            if (result.IsSuccess)
            {
                modActionState.Complete(true, result.Message);
            }
            else
            {
                modActionState.Fail(result.Message);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            modActionState.Cancel("The mod operation was canceled.");
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException)
        {
            modActionState.Fail($"The mod operation failed: {exception.Message}");
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
        if (!modActionState.TryBegin("Launcher update check accepted. Checking for an update…"))
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
            modActionState.Complete(
                preparation.State == LauncherUpdatePreparationState.Ready,
                preparation.Message);
            return preparation;
        }
        catch (OperationCanceledException)
        {
            modActionState.Cancel("The launcher update check was canceled or timed out.");
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            modActionState.Fail($"The launcher update could not be prepared: {exception.Message}");
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
        if (!modActionState.TryBegin(progress))
        {
            return null;
        }
        try
        {
            var result = await operation(cancellationToken);
            if (result.IsSuccess)
            {
                modActionState.Complete(true, result.Message);
            }
            else
            {
                modActionState.Fail(result.Message);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            modActionState.Cancel("The maintenance operation was canceled.");
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            modActionState.Fail($"The maintenance operation failed: {exception.Message}");
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
        modActionState.SetAvailability(modPresentation.CanExecute, modPresentation.AutomationName);

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
        _ = e;
        OnPropertyChanged(nameof(RefreshActionLabel));
        OnPropertyChanged(nameof(RefreshActionAutomationName));
        OnPropertyChanged(nameof(RefreshActionStatus));
        OnPropertyChanged(nameof(HasRefreshActionStatus));
        OnPropertyChanged(nameof(CanRefresh));
    }

    private void ModActionState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        OnPropertyChanged(nameof(IsModOperationInProgress));
        OnPropertyChanged(nameof(ModActionLabel));
        OnPropertyChanged(nameof(ModActionAutomationName));
        OnPropertyChanged(nameof(CanManageMod));
        OnPropertyChanged(nameof(CanRecoverMod));
        OnPropertyChanged(nameof(CanUninstallMod));
        OnPropertyChanged(nameof(ModOperationFeedback));
        OnPropertyChanged(nameof(HasModOperationFeedback));
        OnPropertyChanged(nameof(HomeOperationFeedback));
        OnPropertyChanged(nameof(HasHomeOperationFeedback));
        OnPropertyChanged(nameof(CanLaunchGame));
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
