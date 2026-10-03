using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

public partial class MainWindow
{
    private LauncherProfilesSnapshot profiles = LauncherProfilesSnapshot.Empty;
    private IReadOnlyList<LauncherProfile> archivedProfiles = [];
    private bool isArchivingProfile;
    private bool isImportingProfile;
    private bool isProfileOperationPending;
    private TaskCompletionSource<bool>? importReviewCompletion;
    private ProfileImportSources? importSources;
    private NativeLauncherProfilesStore ProfilesStore => new(stateDirectory);

    private void OpenProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (isProfileOperationPending) return;
        ReloadProfiles();
        if (ProfilesList.ItemsSource is null) RefreshProfilesList(profiles.SelectedProfileId);
        UpdateProfileLaunchSelection();
        OpenProfilesWorkspace();
    }

    private bool ReloadProfiles()
    {
        var active = ProfilesStore.Load(allowSelectionRepair: true);
        var archived = ProfilesStore.Load(archived: true, allowSelectionRepair: true);
        profiles = active.Snapshot ?? LauncherProfilesSnapshot.Empty;
        archivedProfiles = archived.Snapshot?.Profiles ?? [];
        var issues = (profiles.Issues ?? []).Concat(archived.Snapshot?.Issues ?? []).ToArray();
        ProfileCatalogIssues.Text = string.Join(Environment.NewLine,
            issues.Select(issue => $"{issue.Id ?? issue.Path}: {issue.Message}")
                .Prepend(active.Error ?? string.Empty).Prepend(archived.Error ?? string.Empty)
                .Where(message => message.Length > 0));
        return active.State != LauncherProfilesLoadState.Invalid && active.Snapshot is not null;
    }

    private void UpdateProfileLaunchSelection() => ProfileLaunchSelection.Text = profiles.SelectedProfile is { } selected
        ? selected.Name
        : profiles.SelectedProfileId is { } missing
            ? $"Selected profile is unavailable: {missing}. Restore it or choose Default."
            : "Selected for launch: Default";

    private void RefreshProfilesList(string? selectedId)
    {
        var entries = ShowArchivedProfilesBox.IsChecked == true ? archivedProfiles : profiles.Profiles;
        isRestoringProfileSelection = true;
        try
        {
            ProfilesList.ItemsSource = entries;
            ProfilesList.SelectedItem = entries.FirstOrDefault(profile => profile.Id == selectedId);
        }
        finally { isRestoringProfileSelection = false; }
        if (ProfilesList.SelectedItem is LauncherProfile profile) FillProfileForm(profile);
        ArchiveProfileButton.IsEnabled = ProfilesList.SelectedItem is LauncherProfile { IsDefault: false };
    }

    private void ShowArchivedProfilesBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ProfilesList is null) return;
        RefreshProfilesList(null);
    }

    private async void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRestoringProfileSelection) return;
        if (ProfilesList.SelectedItem is not LauncherProfile profile) return;
        if (profile.State == "archived") { FillProfileForm(profile); return; }
        if (!await SelectVisibleProfileAsync(profile.Id))
        {
            RefreshProfilesList(profiles.SelectedProfileId);
            return;
        }
        FillProfileForm(profile);
    }

    private void FillProfileForm(LauncherProfile profile)
    {
        profileFormInstallation = null;
        if (!string.IsNullOrEmpty(profile.PreferredInstallationId))
        {
            try { profileFormInstallation = ProfilesStore.InstallationPaths(profile.PreferredInstallationId); }
            catch (Exception exception) when (IsProfileImportException(exception))
            { ProfileCatalogIssues.Text = exception.Message; }
        }
        isArchivingProfile = false;
        ProfileImportFeedback.Text = string.Empty;
        ResetImportMode();
        ProfileError.Text = string.Empty;
        var archived = profile.State == "archived";
        ProfileLaunchSelection.Text = profile.Name;
        ProfileFormTitle.Text = archived ? "Archived profile" : "Edit profile";
        ProfileIdentity.Text = $"Profile ID: {profile.Id}";
        ProfileIdentity.Visibility = Visibility.Visible;
        ProfileNameBox.Text = profile.Name;
        ProfileWorkspaceKindLabel.Text = profile.IsDefault ? "Windows setup" : "Isolated profile";
        ProfileFolderBox.Text = profile.GameDirectory;
        ProfileNameBox.IsReadOnly = archived || profile.IsDefault;
        ProfileFolderBox.IsReadOnly = archived;
        BrowseProfileFolderButton.IsEnabled = !archived;
        SaveProfileButton.IsEnabled = !archived;
        ProfileSettingsButton.IsEnabled = !archived;
        ArchiveProfileButton.Content = archived ? "_Restore" : "_Archive";
        ArchiveProfileButton.IsEnabled = !profile.IsDefault;
    }

    private void ResetImportMode()
    {
        isImportingProfile = false;
        ProfileImportSourcePanel.Visibility = Visibility.Collapsed;
        SaveProfileButton.Content = "_Save profile";
        AutomationProperties.SetName(SaveProfileButton, "Save launch profile");
    }

    private void NewProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (isProfileOperationPending) return;
        ResetImportMode();
        profileFormInstallation = null;
        ProfileImportFeedback.Text = string.Empty;
        ShowArchivedProfilesBox.IsChecked = false;
        isRestoringProfileSelection = true;
        try { ProfilesList.SelectedItem = null; }
        finally { isRestoringProfileSelection = false; }
        isArchivingProfile = false;
        ProfileFormTitle.Text = "Create an empty profile";
        ProfileLaunchSelection.Text = "New profile";
        ProfileWorkspaceKindLabel.Text = "Isolated profile";
        SaveProfileButton.Content = "_Create profile";
        ProfileIdentity.Visibility = Visibility.Collapsed;
        ProfileNameBox.Text = string.Empty;
        ProfileNameBox.IsReadOnly = false;
        ProfileFolderBox.Text = (DataContext as ViewModels.MainWindowViewModel)?.SelectedGameDirectory ?? string.Empty;
        ProfileFolderBox.IsReadOnly = false;
        BrowseProfileFolderButton.IsEnabled = true;
        SaveProfileButton.IsEnabled = true;
        ProfileSettingsButton.IsEnabled = false;
        ProfileError.Text = string.Empty;
        ArchiveProfileButton.Content = "_Archive";
        ArchiveProfileButton.IsEnabled = false;
    }

    private void SetProfileOperationPending(bool pending)
    {
        isProfileOperationPending = pending;
        ProfilesEditor.IsEnabled = !pending;
        ProfilesList.IsEnabled = !pending;
    }

    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (isProfileOperationPending) return;
        NewProfileButton_Click(sender, e);
        isImportingProfile = true;
        ProfileImportSourcePanel.Visibility = Visibility.Visible;
        ProfileFormTitle.Text = "Import a Windows user’s STFC setup";
        ProfileLaunchSelection.Text = "Copy Windows setup";
        SaveProfileButton.Content = "_Review import…";
        AutomationProperties.SetName(SaveProfileButton, "Review Windows user import");
        importSources = null;
        ProfileImportSourceBox.ItemsSource = null;
        FindImportUsersButton.Visibility = Visibility.Collapsed;
        ProfileImportFeedback.Text = "Checking for saved STFC setups…";
        SetProfileOperationPending(true);
        try
        {
            var discovered = await ProfilesStore.ImportSourcesAsync();
            if (isImportingProfile) ApplyImportSources(discovered);
        }
        catch (Exception exception) when (IsProfileImportException(exception))
        {
            ProfileImportFeedback.Text = string.Empty;
            ProfileError.Text = exception.Message;
        }
        finally { SetProfileOperationPending(false); }
    }

    private void ApplyImportSources(ProfileImportSources sources)
    {
        importSources = sources;
        var previousSid = (ProfileImportSourceBox.SelectedItem as ProfileImportUser)?.Sid;
        ProfileImportSourceBox.ItemsSource = sources.Users;
        ProfileImportSourceBox.SelectedItem = sources.Users.FirstOrDefault(user => user.Sid == previousSid)
            ?? sources.Users.FirstOrDefault(user => user.CurrentUser) ?? (sources.Users.Count > 0 ? sources.Users[0] : null);
        FindImportUsersButton.Visibility = sources.RequiresElevation ? Visibility.Visible : Visibility.Collapsed;
        ProfileError.Text = string.Empty;
        ProfileImportFeedback.Text = sources.RequiresElevation
            ? "Windows permission is needed to check other user setups. You can use a listed setup or find other Windows users."
            : sources.UnavailableUsers > 0 ? "Some user setups could not be checked right now. You can use a listed setup or retry later."
            : sources.Users.Count == 0 ? "No saved STFC setups were found for the Windows users checked." : string.Empty;
    }

    private async Task<bool> ReviewProfileImportAsync(ViewModels.ProfileImportPresentation presentation)
    {
        if (!isImportingProfile) return false;
        ProfileImportReviewDialog.DialogTitle = presentation.Title;
        AutomationProperties.SetName(ProfileImportReviewDialog, presentation.Title);
        ProfileImportCopyExplanation.Text = presentation.CopyExplanation;
        ProfileImportPermissionExplanation.Text = presentation.PermissionExplanation;
        ProfileImportDetailExplanation.Text = presentation.Details;
        ProfileImportDetails.IsExpanded = false;
        importReviewCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProfileImportReviewDialog.IsOpen = true;
        return await importReviewCompletion.Task;
    }

    private async void FindImportUsersButton_Click(object sender, RoutedEventArgs e)
    {
        if (isProfileOperationPending || importSources is not { RequiresElevation: true } sources) return;
        ProfileError.Text = string.Empty;
        SetProfileOperationPending(true);
        try
        {
            if (!await ReviewProfileImportAsync(ViewModels.ProfileImportPresentation.ForDiscovery())) return;
            ProfileImportFeedback.Text = "Checking other Windows users for saved STFC setups…";
            ApplyImportSources(await ProfilesStore.ImportSourcesAsync(true, sources.DestinationUser.Sid));
        }
        catch (Exception exception) when (IsProfileImportException(exception))
        {
            ProfileImportFeedback.Text = string.Empty;
            ProfileError.Text = exception.Message;
        }
        finally
        {
            importReviewCompletion = null;
            ProfileImportReviewDialog.IsOpen = false;
            SetProfileOperationPending(false);
        }
    }

    private async Task ImportProfileAsync()
    {
        if (ProfileImportSourceBox.SelectedItem is not ProfileImportUser source)
        {
            ProfileError.Text = "Choose the Windows user whose STFC setup you want to copy.";
            return;
        }
        ProfileError.Text = string.Empty;
        ProfileImportFeedback.Text = string.Empty;
        SetProfileOperationPending(true);
        try
        {
            var plan = await ProfilesStore.PrepareUserImportAsync(source.Sid, ProfileNameBox.Text, ProfileFolderBox.Text,
                preferredInstallationId: SelectedFormInstallationId());
            if (!await ReviewProfileImportAsync(ViewModels.ProfileImportPresentation.From(plan))) return;
            if (!await ResolveProfileDraftsAsync()) return;
            ProfileImportFeedback.Text = "Importing the selected STFC setup…";
            var imported = await ProfilesStore.ImportUserAsync(plan);
            await ProfilesStore.SelectAsync(imported.Id);
            if (!ReloadProfiles()) return;
            ShowArchivedProfilesBox.IsChecked = false;
            RefreshProfilesList(imported.Id);
            UpdateProfileLaunchSelection();
            (DataContext as ViewModels.MainWindowViewModel)?.ReloadLaunchProfile();
            RecomposeSelectedProfileRuntime();
            await ReconcileSettingsTargetAsync();
            ProfileImportFeedback.Text = $"Created {imported.Name} from {source.Name}. It is selected; launch when you’re ready.";
        }
        catch (Exception exception) when (IsProfileImportException(exception))
        {
            ProfileImportFeedback.Text = string.Empty;
            ProfileError.Text = exception.Message;
        }
        finally
        {
            importReviewCompletion = null;
            ProfileImportReviewDialog.IsOpen = false;
            SetProfileOperationPending(false);
        }
    }

    private static bool IsProfileImportException(Exception exception) => exception is ArgumentException
        or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException
        or System.Text.Json.JsonException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private void ContinueProfileImportButton_Click(object sender, RoutedEventArgs e)
    {
        importReviewCompletion?.TrySetResult(true);
        ProfileImportReviewDialog.IsOpen = false;
    }

    private void CancelProfileImportButton_Click(object sender, RoutedEventArgs e) => ProfileImportReviewDialog.IsOpen = false;

    private void ProfileImportReviewDialog_Closed(object? sender, EventArgs e) => importReviewCompletion?.TrySetResult(false);

    private async void BrowseProfileFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (isProfileOperationPending) return;
        var selected = await ChooseInstallationAsync(profileFormInstallation?.Id ?? "", ProfileFolderBox.Text);
        if (selected is null) return;
        profileFormInstallation = selected;
        ProfileFolderBox.Text = selected.GameDirectory;
    }

    private string SelectedFormInstallationId() => profileFormInstallation is { } installation
        && GameDirectoryIdentity.SameLocation(installation.GameDirectory, ProfileFolderBox.Text) ? installation.Id : "";

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (isProfileOperationPending) return;
        if (isImportingProfile)
        {
            await ImportProfileAsync();
            return;
        }
        SetProfileOperationPending(true);
        try
        {
            if (!await ResolveProfileDraftsAsync()) return;
            var selected = ProfilesList.SelectedItem as LauncherProfile;
            var updated = selected is null
                ? await ProfilesStore.CreateNewAsync(ProfileNameBox.Text, ProfileFolderBox.Text,
                    preferredInstallationId: SelectedFormInstallationId())
                : await ProfilesStore.EditAsync(selected, ProfileNameBox.Text, ProfileFolderBox.Text,
                    preferredInstallationId: SelectedFormInstallationId());
            await ProfilesStore.SelectAsync(updated.Id);
            if (!ReloadProfiles()) return;
            RefreshProfilesList(updated.Id);
            UpdateProfileLaunchSelection();
            (DataContext as ViewModels.MainWindowViewModel)?.ReloadLaunchProfile();
            RecomposeSelectedProfileRuntime();
            await ReconcileSettingsTargetAsync();
            ProfileError.Text = string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        { ProfileError.Text = exception.Message; }
        finally { SetProfileOperationPending(false); }
    }

    private async void ArchiveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (isProfileOperationPending || ProfilesList.SelectedItem is not LauncherProfile { IsDefault: false } profile) return;
        if (profile.Id == profiles.SelectedProfileId && SharedSettings.HasPendingChanges)
        {
            ProfileError.Text = "Save or discard Settings and Data Sync drafts before archiving their profile.";
            return;
        }
        if (profile.State == "active" && !isArchivingProfile)
        {
            isArchivingProfile = true;
            ArchiveProfileButton.Content = "_Confirm archive";
            ProfileError.Text = "Archiving retains this profile's account data, configuration and logs. Its session must be stopped.";
            return;
        }
        SetProfileOperationPending(true);
        try
        {
            var updated = profile.State == "archived"
                ? await ProfilesStore.RestoreAsync(profile)
                : await ProfilesStore.ArchiveAsync(profile);
            if (!ReloadProfiles()) return;
            ShowArchivedProfilesBox.IsChecked = updated.State == "archived";
            RefreshProfilesList(updated.Id);
            UpdateProfileLaunchSelection();
            (DataContext as ViewModels.MainWindowViewModel)?.ReloadLaunchProfile();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        { ProfileError.Text = exception.Message; }
        finally { SetProfileOperationPending(false); }
    }

}
