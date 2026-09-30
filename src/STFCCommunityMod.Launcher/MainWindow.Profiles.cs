using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

public partial class MainWindow
{
    private LauncherProfilesSnapshot profiles = LauncherProfilesSnapshot.Empty;
    private IReadOnlyList<LauncherProfile> archivedProfiles = [];
    private bool isArchivingProfile;
    private NativeLauncherProfilesStore ProfilesStore => new(stateDirectory);

    private void OpenProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ReloadProfiles()) return;
        ShowArchivedProfilesBox.IsChecked = false;
        RefreshProfilesList(profiles.SelectedProfileId);
        if (ProfilesList.SelectedItem is null) NewProfileButton_Click(sender, e);
        UpdateProfileLaunchSelection();
        ProfilesDialog.IsOpen = true;
    }

    private bool ReloadProfiles()
    {
        var active = ProfilesStore.Load(allowSelectionRepair: true);
        var archived = ProfilesStore.Load(archived: true, allowSelectionRepair: true);
        if (active.State == LauncherProfilesLoadState.Invalid || active.Snapshot is null
            || archived.State == LauncherProfilesLoadState.Invalid || archived.Snapshot is null)
        {
            SettingsUnavailableMessage.Text = active.Error ?? archived.Error ?? "The shared profile catalog is unavailable.";
            SettingsUnavailableDialog.IsOpen = true;
            return false;
        }
        profiles = active.Snapshot;
        archivedProfiles = archived.Snapshot.Profiles;
        var issues = (profiles.Issues ?? []).Concat(archived.Snapshot.Issues ?? []).ToArray();
        ProfileCatalogIssues.Text = string.Join(Environment.NewLine,
            issues.Select(issue => $"{issue.Id ?? issue.Path}: {issue.Message}")
                .Prepend(active.Error ?? string.Empty).Where(message => message.Length > 0));
        return true;
    }

    private void UpdateProfileLaunchSelection() => ProfileLaunchSelection.Text = profiles.SelectedProfile is { } selected
        ? $"Selected for launch: {selected.Name} ({selected.Id})"
        : profiles.SelectedProfileId is { } missing
            ? $"Selected profile is unavailable: {missing}. Restore it or choose Default."
            : "Selected for launch: Default";

    private void RefreshProfilesList(string? selectedId)
    {
        var entries = ShowArchivedProfilesBox.IsChecked == true ? archivedProfiles : profiles.Profiles;
        ProfilesList.ItemsSource = entries;
        ProfilesList.SelectedItem = entries.FirstOrDefault(profile => profile.Id == selectedId);
        ArchiveProfileButton.IsEnabled = ProfilesList.SelectedItem is not null;
    }

    private void ShowArchivedProfilesBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ProfilesList is null) return;
        RefreshProfilesList(null);
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        isArchivingProfile = false;
        ProfileError.Text = string.Empty;
        if (ProfilesList.SelectedItem is not LauncherProfile profile)
        {
            ArchiveProfileButton.IsEnabled = false;
            return;
        }
        var archived = profile.State == "archived";
        ProfileFormTitle.Text = archived ? "Archived profile" : "Edit profile";
        ProfileIdentity.Text = $"Profile ID: {profile.Id}";
        ProfileIdentity.Visibility = Visibility.Visible;
        ProfileNameBox.Text = profile.Name;
        ProfileFolderBox.Text = profile.GameDirectory;
        ProfileNameBox.IsReadOnly = archived;
        ProfileFolderBox.IsReadOnly = archived;
        BrowseProfileFolderButton.IsEnabled = !archived;
        SaveProfileButton.IsEnabled = !archived;
        ArchiveProfileButton.Content = archived ? "_Restore" : "_Archive";
        ArchiveProfileButton.IsEnabled = true;
    }

    private void NewProfileButton_Click(object sender, RoutedEventArgs e)
    {
        ShowArchivedProfilesBox.IsChecked = false;
        ProfilesList.SelectedItem = null;
        isArchivingProfile = false;
        ProfileFormTitle.Text = "New profile";
        ProfileIdentity.Visibility = Visibility.Collapsed;
        ProfileNameBox.Text = string.Empty;
        ProfileNameBox.IsReadOnly = false;
        ProfileFolderBox.Text = (DataContext as ViewModels.MainWindowViewModel)?.SelectedGameDirectory ?? string.Empty;
        ProfileFolderBox.IsReadOnly = false;
        BrowseProfileFolderButton.IsEnabled = true;
        SaveProfileButton.IsEnabled = true;
        ProfileError.Text = string.Empty;
        ArchiveProfileButton.Content = "_Archive";
        ArchiveProfileButton.IsEnabled = false;
    }

    private void BrowseProfileFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the preferred game folder containing prime.exe", Multiselect = false };
        if (Directory.Exists(ProfileFolderBox.Text)) dialog.InitialDirectory = ProfileFolderBox.Text;
        if (dialog.ShowDialog(this) == true) ProfileFolderBox.Text = dialog.FolderName;
    }

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = ProfilesList.SelectedItem as LauncherProfile;
            var updated = selected is null
                ? await ProfilesStore.CreateNewAsync(ProfileNameBox.Text, ProfileFolderBox.Text)
                : await ProfilesStore.EditAsync(selected, ProfileNameBox.Text, ProfileFolderBox.Text);
            if (!ReloadProfiles()) return;
            RefreshProfilesList(updated.Id);
            UpdateProfileLaunchSelection();
            (DataContext as ViewModels.MainWindowViewModel)?.ReloadLaunchProfile();
            ProfileError.Text = string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        { ProfileError.Text = exception.Message; }
    }

    private async void ArchiveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not LauncherProfile profile) return;
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
    }

    private async void UseSelectedProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not LauncherProfile { State: "active" } profile)
        {
            ProfileError.Text = "Choose an active profile first. Restore archived profiles before launching.";
            return;
        }
        await SaveLaunchSelectionAsync(profile.Id);
    }

    private async void UseDefaultProfileButton_Click(object sender, RoutedEventArgs e) => await SaveLaunchSelectionAsync(null);

    private async Task SaveLaunchSelectionAsync(string? profileId)
    {
        if (SharedSettings.HasPendingChanges)
        {
            ProfileError.Text = "Save or discard Settings and Data Sync drafts before changing profiles.";
            return;
        }
        try
        {
            await ProfilesStore.SelectAsync(profileId);
            SettingsWorkspace.DataContext = null;
            isSettingsWorkspaceInitialized = false;
            await SharedSettings.InvalidateAsync(LauncherSettingsInvalidationReason.ConfigurationTargetChanged);
            if (!ReloadProfiles()) return;
            UpdateProfileLaunchSelection();
            (DataContext as ViewModels.MainWindowViewModel)?.ReloadLaunchProfile();
            ProfileError.Text = string.Empty;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        { ProfileError.Text = exception.Message; }
    }
}
