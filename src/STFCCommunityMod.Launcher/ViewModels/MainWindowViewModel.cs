using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

internal sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly LauncherEnvironmentProbe environmentProbe;
    private readonly ModManagementCoordinator modManagementCoordinator;
    private readonly GameLaunchHandoffCoordinator gameLaunchCoordinator;
    private LauncherEnvironmentSnapshot snapshot;
    private LauncherHomePresentation presentation;
    private ModManagementPresentation modPresentation;
    private GameLaunchPresentation launchPresentation;
    private string selectionFeedback = string.Empty;
    private string modOperationFeedback = string.Empty;
    private bool isModOperationInProgress;
    private bool isLaunchInProgress;
    private string launchFeedback = string.Empty;

    private MainWindowViewModel(
        LauncherEnvironmentProbe environmentProbe,
        ModManagementCoordinator modManagementCoordinator,
        GameLaunchHandoffCoordinator gameLaunchCoordinator)
    {
        this.environmentProbe = environmentProbe;
        this.modManagementCoordinator = modManagementCoordinator;
        this.gameLaunchCoordinator = gameLaunchCoordinator;
        snapshot = environmentProbe.Capture();
        presentation = LauncherHomePresentation.FromSnapshot(snapshot);
        modPresentation = modManagementCoordinator.CapturePresentation(
            snapshot.SelectedGameDirectory,
            snapshot.IsGameRunning);
        launchPresentation = gameLaunchCoordinator.CapturePresentation(snapshot.SelectedGameDirectory);
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

    public string ModActionLabel => isModOperationInProgress ? "Working…" : modPresentation.ActionLabel;

    public string ModActionAutomationName => isModOperationInProgress
        ? "Community mod operation in progress"
        : modPresentation.AutomationName;

    public bool CanManageMod => modPresentation.CanExecute && !isModOperationInProgress && !isLaunchInProgress;

    public string LaunchActionLabel => isLaunchInProgress ? "Official launcher open…" : launchPresentation.ActionLabel;

    public string LaunchActionAutomationName => isLaunchInProgress
        ? "Official Star Trek Fleet Command launcher handoff in progress"
        : launchPresentation.AutomationName;

    public bool CanLaunchGame => launchPresentation.CanExecute && !isLaunchInProgress && !isModOperationInProgress;

    public string ModOperationFeedback => modOperationFeedback;

    public bool HasModOperationFeedback => !string.IsNullOrWhiteSpace(modOperationFeedback);

    public bool IsModOperationInProgress => isModOperationInProgress;

    public bool IsLaunchInProgress => isLaunchInProgress;

    public string HomeOperationFeedback => !string.IsNullOrWhiteSpace(launchFeedback)
        ? launchFeedback
        : modOperationFeedback;

    public bool HasHomeOperationFeedback => !string.IsNullOrWhiteSpace(HomeOperationFeedback);

    public string? SelectedGameDirectory => snapshot.SelectedGameDirectory;

    public string SelectionFeedback => selectionFeedback;

    public bool HasSelectionFeedback => !string.IsNullOrWhiteSpace(selectionFeedback);

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

    public static MainWindowViewModel CreateDefault(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
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
            new WindowsAuthenticodeVerifier("Joseph Gustavson"),
            processInspector.IsGameRunning);
        var launchCoordinator = new GameLaunchHandoffCoordinator(
            installLayout.StateDirectory,
            deploymentService,
            WindowsOfficialLauncherService.FromCurrentUser(),
            processInspector);
        return new(
            new LauncherEnvironmentProbe(
                processInspector,
                installLayout,
                installDiscovery),
            new ModManagementCoordinator(
                deploymentService,
                new GitHubWindowsReleaseClient(httpClient),
                new Version(0, 1, 0)),
            launchCoordinator);
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
    }

    public async Task<ModOperationPreparation?> PrepareModOperationAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanManageMod || snapshot.SelectedGameDirectory is null)
        {
            return null;
        }

        SetModOperationState(true, "Checking the selected release…");
        try
        {
            var preparation = await modManagementCoordinator.PrepareLatestAsync(
                snapshot.SelectedGameDirectory,
                snapshot.IsGameRunning,
                cancellationToken);
            if (preparation.State == ModOperationPreparationState.UpToDate)
            {
                modOperationFeedback = preparation.Message;
                OnPropertyChanged(nameof(ModOperationFeedback));
                OnPropertyChanged(nameof(HasModOperationFeedback));
            }
            else
            {
                modOperationFeedback = $"Community mod {preparation.ReleaseVersion} is ready for confirmation.";
            }
            return preparation;
        }
        catch (OperationCanceledException)
        {
            modOperationFeedback = "The release check was canceled or timed out.";
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            modOperationFeedback = $"Could not prepare the mod operation: {exception.Message}";
            OnPropertyChanged(nameof(ModOperationFeedback));
            OnPropertyChanged(nameof(HasModOperationFeedback));
            return null;
        }
        finally
        {
            SetModOperationState(false, modOperationFeedback);
        }
    }

    public async Task<ModDeploymentResult?> ExecuteModOperationAsync(
        ModOperationPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (isModOperationInProgress)
        {
            return null;
        }

        SetModOperationState(true, "Installing the verified community mod…");
        try
        {
            var result = await modManagementCoordinator.ExecuteAsync(preparation, cancellationToken);
            modOperationFeedback = result.Message;
            return result;
        }
        catch (OperationCanceledException)
        {
            modOperationFeedback = "The mod operation was canceled.";
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or HttpRequestException)
        {
            modOperationFeedback = $"The mod operation failed: {exception.Message}";
            return null;
        }
        finally
        {
            SetModOperationState(false, modOperationFeedback);
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

    private void SetModOperationState(bool isInProgress, string feedback)
    {
        isModOperationInProgress = isInProgress;
        modOperationFeedback = feedback;
        OnPropertyChanged(nameof(IsModOperationInProgress));
        OnPropertyChanged(nameof(ModActionLabel));
        OnPropertyChanged(nameof(ModActionAutomationName));
        OnPropertyChanged(nameof(CanManageMod));
        OnPropertyChanged(nameof(ModOperationFeedback));
        OnPropertyChanged(nameof(HasModOperationFeedback));
        OnPropertyChanged(nameof(HomeOperationFeedback));
        OnPropertyChanged(nameof(HasHomeOperationFeedback));
        OnPropertyChanged(nameof(CanLaunchGame));
    }

    private void NotifyModPresentationChanged()
    {
        OnPropertyChanged(nameof(ModStatus));
        OnPropertyChanged(nameof(ModTone));
        OnPropertyChanged(nameof(ModActionLabel));
        OnPropertyChanged(nameof(ModActionAutomationName));
        OnPropertyChanged(nameof(CanManageMod));
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
}
