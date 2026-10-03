using System.IO;
using System.Windows;
using System.Windows.Controls;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher;

internal enum ProfileDraftDecision { Stay, Save, Discard }

public partial class MainWindow
{
    private bool isEngineering;
    private bool isRestoringProfileSelection;
    private string engineeringSection = "Profiles";
    private TaskCompletionSource<ProfileDraftDecision>? profileDraftDecision;
    private TaskCompletionSource<ProfileSession?>? profileSessionChoice;
    private TaskCompletionSource<RegisteredGameInstallation?>? installationChoice;
    private RegisteredGameInstallation? profileFormInstallation;

    private void ShuttleBayButton_Click(object sender, RoutedEventArgs e) => SetPrimaryWorkspace(false);
    private void EngineeringButton_Click(object sender, RoutedEventArgs e) => SetPrimaryWorkspace(true);

    private void SetPrimaryWorkspace(bool engineering)
    {
        isEngineering = engineering;
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.SetWorkspaceMode(engineering ? "Engineering" : "ShuttleBay");
        UpdatePrimaryWorkspaceVisibility();
    }

    private void UpdatePrimaryWorkspaceVisibility()
    {
        ShuttleBayWorkspace.Visibility = isEngineering ? Visibility.Collapsed : Visibility.Visible;
        EngineeringNavigation.Visibility = isEngineering ? Visibility.Visible : Visibility.Collapsed;
        ProfilesWorkspace.Visibility = isEngineering && engineeringSection == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
        HomeWorkspace.Visibility = isEngineering && engineeringSection == "Installations" ? Visibility.Visible : Visibility.Collapsed;
        SettingsWorkspace.Visibility = isEngineering && engineeringSection == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsWorkspace.Visibility = isEngineering && engineeringSection == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchHost.Visibility = SettingsWorkspace.Visibility;
        HomeSettingsTitleBarButton.Visibility = Visibility.Collapsed;
        SettingsHomeTitleBarButton.Visibility = Visibility.Collapsed;
        DiagnosticsHomeTitleBarButton.Visibility = Visibility.Collapsed;
        ColorModeSelector.Visibility = Visibility.Visible;
        SettingsDiagnosticsTitleBarButton.Visibility = Visibility.Collapsed;
        ShuttleBayButton.IsEnabled = true;
        EngineeringButton.IsEnabled = true;
        ApplyWorkspaceSizing(isEngineering ? LauncherWorkspace.Settings : LauncherWorkspace.Home);
    }

    private void EngineeringProfilesButton_Click(object sender, RoutedEventArgs e) => OpenProfilesWorkspace();
    private void EngineeringInstallationsButton_Click(object sender, RoutedEventArgs e)
    {
        engineeringSection = "Installations";
        SetPrimaryWorkspace(true);
    }

    private void OpenProfilesWorkspace()
    {
        engineeringSection = "Profiles";
        SetPrimaryWorkspace(true);
    }

    private async void ConfigureProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string profileId } && await SelectVisibleProfileAsync(profileId))
        {
            RefreshProfilesList(profileId);
            OpenProfilesWorkspace();
        }
        else ShuttleBaySelectionError.Text = ProfileError.Text;
    }

    private async void LaunchProfileCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string profileId }) return;
        if (!await SelectVisibleProfileAsync(profileId))
        {
            ShuttleBaySelectionError.Text = ProfileError.Text;
            return;
        }
        ShuttleBaySelectionError.Text = string.Empty;
        if (DataContext is not MainWindowViewModel viewModel || viewModel.SelectedProfile?.Id != profileId) return;
        if (viewModel.ProfileCards.FirstOrDefault(card => card.Id == profileId) is { NeedsSetup: true } card)
        {
            engineeringSection = "Installations";
            SetPrimaryWorkspace(true);
            if (card.NextAction == LauncherLaunchRecoveryAction.SelectGameFolder)
                await SelectInstallationForSelectedProfileAsync();
            return;
        }
        if (viewModel.LaunchPrimaryCommand.CanExecute(null))
            viewModel.LaunchPrimaryCommand.Execute(null);
    }

    private async Task<bool> SelectVisibleProfileAsync(string profileId)
    {
        if (isProfileOperationPending) return false;
        if (profiles.SelectedProfileId == profileId) return true;
        SetProfileOperationPending(true);
        try
        {
            if (!await ResolveProfileDraftsAsync()) return false;
            await ProfilesStore.SelectAsync(profileId);
            if (!ReloadProfiles()) return false;
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.ReloadLaunchProfile();
                RecomposeSelectedProfileRuntime();
                viewModel = (MainWindowViewModel)DataContext;
                await viewModel.RefreshProfileSessionsAsync();
            }
            UpdateProfileLaunchSelection();
            await ReconcileSettingsTargetAsync();
            ProfileError.Text = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ProfileError.Text = exception.Message;
            return false;
        }
        finally { SetProfileOperationPending(false); }
    }

    private async Task<bool> ResolveProfileDraftsAsync()
    {
        var settings = SharedSettings.Current;
        if (settings is null) return true;
        return await ResolveDraftTransitionAsync(
            () => settings.HasPendingChanges || settings.SyncWorkspace.HasPendingChanges,
            () => settings.IsSaveInProgress || settings.SyncWorkspace.IsSaveInProgress,
            async () =>
            {
                profileDraftDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);
                ProfileDraftSaveButton.IsEnabled = (!settings.HasPendingChanges || settings.CanSave)
                    && (!settings.SyncWorkspace.HasPendingChanges || settings.SyncWorkspace.CanSave);
                ProfileDraftMessage.Text = $"{settings.ConfigurationTargetLabel} has unsaved Settings or Data Sync changes. Save or discard them before selecting another profile.";
                ProfileDraftDialog.IsOpen = true;
                try { return await profileDraftDecision.Task; }
                finally { ProfileDraftDialog.IsOpen = false; profileDraftDecision = null; }
            },
            async () =>
            {
                if (settings.HasPendingChanges) await settings.SaveAsync();
                if (!settings.HasPendingChanges && settings.SyncWorkspace.HasPendingChanges) await settings.SyncWorkspace.SaveAsync();
            },
            settings.DiscardAllDraftsForContextChange);
    }

    internal static async Task<bool> ResolveDraftTransitionAsync(Func<bool> hasDrafts, Func<bool> isSaving,
        Func<Task<ProfileDraftDecision>> choose, Func<Task> save, Action discard)
    {
        if (isSaving()) return false;
        if (!hasDrafts()) return true;
        var decision = await choose();
        if (isSaving()) return false;
        if (decision == ProfileDraftDecision.Stay) return false;
        if (decision == ProfileDraftDecision.Save) await save();
        else discard();
        return !hasDrafts() && !isSaving();
    }

    private void ProfileDraftStayButton_Click(object sender, RoutedEventArgs e) => profileDraftDecision?.TrySetResult(ProfileDraftDecision.Stay);
    private void ProfileDraftSaveButton_Click(object sender, RoutedEventArgs e) => profileDraftDecision?.TrySetResult(ProfileDraftDecision.Save);
    private void ProfileDraftDiscardButton_Click(object sender, RoutedEventArgs e) => profileDraftDecision?.TrySetResult(ProfileDraftDecision.Discard);
    private void ProfileDraftDialog_Closed(object? sender, EventArgs e) => profileDraftDecision?.TrySetResult(ProfileDraftDecision.Stay);

    private void RecomposeSelectedProfileRuntime()
    {
        if (DataContext is MainWindowViewModel { ConfigurationRuntimeSelection: { } selection }
            && (selection.ProviderId != ProviderSession.Provider.Id || selection.ReleaseChannelId != ProviderSession.ReleaseChannel.Id))
        {
            var session = providerSessions.Recompose(selection);
            ApplyProviderSession(session);
            UpdatePrimaryWorkspaceVisibility();
        }
    }

    private async Task<ProfileSession?> ChooseProfileSessionAsync(LauncherProfile profile, IReadOnlyList<ProfileSession> sessions)
    {
        if (profileSessionChoice is not null) return null;
        profileSessionChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProfileSessionTitle.Text = $"Choose the running {profile.Name} session to focus.";
        ProfileSessionsList.ItemsSource = sessions;
        ProfileSessionsList.SelectedItem = null;
        FocusProfileSessionButton.IsEnabled = false;
        ProfileSessionsDialog.IsOpen = true;
        try { return await profileSessionChoice.Task; }
        finally { ProfileSessionsDialog.IsOpen = false; profileSessionChoice = null; }
    }

    private void ProfileSessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        FocusProfileSessionButton.IsEnabled = ProfileSessionsList.SelectedItem is ProfileSession;
    private void FocusProfileSessionButton_Click(object sender, RoutedEventArgs e) =>
        profileSessionChoice?.TrySetResult(ProfileSessionsList.SelectedItem as ProfileSession);
    private void CancelProfileSessionButton_Click(object sender, RoutedEventArgs e) => profileSessionChoice?.TrySetResult(null);
    private void ProfileSessionsDialog_Closed(object? sender, EventArgs e) => profileSessionChoice?.TrySetResult(null);

    private async Task<RegisteredGameInstallation?> ChooseInstallationAsync(string preferredId, string path)
    {
        if (installationChoice is not null) return null;
        installationChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InstallationSelectionError.Text = string.Empty;
        InstallationFolderBox.Text = path;
        InstallationNameBox.Text = string.Empty;
        RegisteredInstallationsList.ItemsSource = null;
        InstallationSelectionDialog.IsOpen = true;
        try
        {
            try
            {
                var registrations = await ProfilesStore.InstallationsAsync(lifetimeCancellation.Token);
                RegisteredInstallationsList.ItemsSource = registrations;
                RegisteredInstallationsList.SelectedItem = registrations.FirstOrDefault(item => item.Id == preferredId);
            }
            catch (Exception exception) when (IsProfileImportException(exception))
            { InstallationSelectionError.Text = $"Saved installations could not be read: {exception.Message}"; }
            return await installationChoice.Task;
        }
        finally { InstallationSelectionDialog.IsOpen = false; installationChoice = null; }
    }

    private void RegisteredInstallationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RegisteredInstallationsList.SelectedItem is not RegisteredGameInstallation installation) return;
        InstallationFolderBox.Text = installation.GameDirectory;
        InstallationNameBox.Text = installation.Name;
    }

    private void BrowseInstallationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select installation — choose the folder containing prime.exe", Multiselect = false };
        if (Directory.Exists(InstallationFolderBox.Text)) dialog.InitialDirectory = InstallationFolderBox.Text;
        if (dialog.ShowDialog(this) != true) return;
        RegisteredInstallationsList.SelectedItem = null;
        InstallationFolderBox.Text = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(InstallationNameBox.Text)) InstallationNameBox.Text = "STFC game";
    }

    private async void ConfirmInstallationButton_Click(object sender, RoutedEventArgs e)
    {
        if (installationChoice is null || !ConfirmInstallationButton.IsEnabled) return;
        var completion = installationChoice;
        ConfirmInstallationButton.IsEnabled = false;
        try
        {
            var registration = RegisteredInstallationsList.SelectedItem as RegisteredGameInstallation;
            if (registration is null || !GameDirectoryIdentity.SameLocation(registration.GameDirectory, InstallationFolderBox.Text))
                registration = await ProfilesStore.RegisterInstallationAsync(InstallationNameBox.Text, InstallationFolderBox.Text, lifetimeCancellation.Token);
            completion.TrySetResult(registration);
        }
        catch (Exception exception) when (IsProfileImportException(exception))
        { InstallationSelectionError.Text = exception.Message; }
        finally { ConfirmInstallationButton.IsEnabled = true; }
    }

    private void CancelInstallationButton_Click(object sender, RoutedEventArgs e) => installationChoice?.TrySetResult(null);
    private void InstallationSelectionDialog_Closed(object? sender, EventArgs e) => installationChoice?.TrySetResult(null);
}
