using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

public partial class MainWindow
{
    private LauncherProfilesSnapshot profiles = LauncherProfilesSnapshot.Empty;
    private string profilesRevision = "missing";
    private bool isAdoptingProfile;
    private bool isRemovingProfile;

    private JsonLauncherProfilesStore ProfilesStore => new(stateDirectory);

    private void OpenProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        var loaded = ProfilesStore.Load();
        if (loaded.State == LauncherProfilesLoadState.Invalid || loaded.Snapshot is null)
        {
            SettingsUnavailableMessage.Text = loaded.Error ?? "The launch profile registry is unavailable.";
            SettingsUnavailableDialog.IsOpen = true;
            return;
        }

        profiles = loaded.Snapshot;
        profilesRevision = loaded.Revision ?? "missing";
        RefreshProfilesList(profiles.SelectedProfileId);
        if (profiles.SelectedProfile is null)
        {
            NewProfileButton_Click(sender, e);
        }
        UpdateProfileLaunchSelection();
        ProfilesDialog.IsOpen = true;
    }

    private void UpdateProfileLaunchSelection()
    {
        ProfileLaunchSelection.Text = profiles.SelectedProfile is { } selected
            ? $"Selected for launch: {selected.Name} ({selected.Id})"
            : "Selected for launch: Default";
    }

    private void RefreshProfilesList(string? selectedId)
    {
        ProfilesList.ItemsSource = profiles.Profiles;
        ProfilesList.SelectedItem = profiles.Profiles.FirstOrDefault(profile => profile.Id == selectedId);
        RemoveProfileButton.IsEnabled = ProfilesList.SelectedItem is not null;
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        isRemovingProfile = false;
        RemoveProfileButton.Content = "_Remove";
        ProfileError.Text = string.Empty;
        if (ProfilesList.SelectedItem is not LauncherProfile profile)
        {
            RemoveProfileButton.IsEnabled = false;
            return;
        }

        isAdoptingProfile = false;
        ProfileFormTitle.Text = "Edit profile";
        ProfileIdentity.Text = $"Profile key: {profile.Id}";
        ProfileIdentity.Visibility = Visibility.Visible;
        ProfileNameBox.Text = profile.Name;
        ProfileFolderBox.Text = profile.GameDirectory;
        ProfileKeyPanel.Visibility = Visibility.Collapsed;
        RemoveProfileButton.IsEnabled = true;
    }

    private void NewProfileButton_Click(object sender, RoutedEventArgs e)
    {
        ProfilesList.SelectedItem = null;
        isAdoptingProfile = false;
        isRemovingProfile = false;
        ProfileFormTitle.Text = "New profile";
        ProfileIdentity.Visibility = Visibility.Collapsed;
        ProfileNameBox.Text = string.Empty;
        ProfileFolderBox.Text = string.Empty;
        ProfileKeyBox.Text = string.Empty;
        ProfileKeyPanel.Visibility = Visibility.Collapsed;
        ProfileError.Text = string.Empty;
        RemoveProfileButton.Content = "_Remove";
        RemoveProfileButton.IsEnabled = false;
    }

    private void AdoptProfileButton_Click(object sender, RoutedEventArgs e)
    {
        NewProfileButton_Click(sender, e);
        isAdoptingProfile = true;
        ProfileFormTitle.Text = "Adopt existing profile";
        ProfileKeyPanel.Visibility = Visibility.Visible;
    }

    private void BrowseProfileFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the profile game folder containing prime.exe",
            Multiselect = false,
        };
        if (Directory.Exists(ProfileFolderBox.Text))
        {
            dialog.InitialDirectory = ProfileFolderBox.Text;
        }
        if (dialog.ShowDialog(this) == true)
        {
            ProfileFolderBox.Text = dialog.FolderName;
            if (isAdoptingProfile)
            {
                var contract = LauncherProfileLaunchContract.Inspect(dialog.FolderName);
                ProfileKeyBox.Text = contract.ProfileId ?? string.Empty;
                ProfileError.Text = contract.IsValid ? string.Empty : contract.Message;
            }
        }
    }

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var defaultDirectory = (DataContext as ViewModels.MainWindowViewModel)?.SelectedGameDirectory;
            var selected = ProfilesList.SelectedItem as LauncherProfile;
            LauncherProfilesSnapshot updated;
            string revision;
            if (selected is null && !isAdoptingProfile)
            {
                var created = await ProfilesStore.CreateNewAsync(ProfileNameBox.Text, ProfileFolderBox.Text,
                    defaultDirectory, profilesRevision);
                updated = created.Snapshot;
                revision = created.Revision;
            }
            else if (selected is null)
            {
                var adopted = await ProfilesStore.AdoptExistingAsync(ProfileNameBox.Text, ProfileFolderBox.Text,
                    defaultDirectory, profilesRevision);
                updated = adopted.Snapshot;
                revision = adopted.Revision;
            }
            else
            {
                updated = LauncherProfiles.Edit(profiles, selected.Id, ProfileNameBox.Text, ProfileFolderBox.Text,
                    defaultDirectory);
                revision = await ProfilesStore.SaveAsync(updated, profilesRevision);
            }
            var id = selected?.Id ?? updated.Profiles[^1].Id;
            profiles = updated;
            profilesRevision = revision;
            RefreshProfilesList(id);
            UpdateProfileLaunchSelection();
            (DataContext as ViewModels.MainWindowViewModel)?.ReloadLaunchProfile();
            ProfileError.Text = string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            ProfileError.Text = exception.Message;
        }
    }

    private async void RemoveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not LauncherProfile profile)
        {
            return;
        }
        if (!isRemovingProfile)
        {
            isRemovingProfile = true;
            RemoveProfileButton.Content = "_Confirm remove";
            ProfileError.Text = "Only the Bridge profile entry will be removed. Game files and login data stay in place.";
            return;
        }

        try
        {
            var updated = LauncherProfiles.Remove(profiles, profile.Id);
            var revision = await ProfilesStore.SaveAsync(updated, profilesRevision);
            profiles = updated;
            profilesRevision = revision;
            RefreshProfilesList(null);
            NewProfileButton_Click(sender, e);
            UpdateProfileLaunchSelection();
            (DataContext as ViewModels.MainWindowViewModel)?.ReloadLaunchProfile();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ProfileError.Text = exception.Message;
        }
    }

    private async void UseSelectedProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not LauncherProfile profile)
        {
            ProfileError.Text = "Choose a saved profile first.";
            return;
        }
        var contract = LauncherProfileLaunchContract.Inspect(profile.GameDirectory, profile.Id);
        if (!contract.IsValid)
        {
            ProfileError.Text = contract.Message;
            return;
        }
        await SaveLaunchSelectionAsync(profile.Id);
    }

    private async void UseDefaultProfileButton_Click(object sender, RoutedEventArgs e) =>
        await SaveLaunchSelectionAsync(null);

    private async Task SaveLaunchSelectionAsync(string? profileId)
    {
        try
        {
            var defaultDirectory = (DataContext as ViewModels.MainWindowViewModel)?.SelectedGameDirectory;
            var saved = await ProfilesStore.SelectAsync(profileId, defaultDirectory, profilesRevision);
            profiles = saved.Snapshot;
            profilesRevision = saved.Revision;
            UpdateProfileLaunchSelection();
            (DataContext as ViewModels.MainWindowViewModel)?.ReloadLaunchProfile();
            ProfileError.Text = string.Empty;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ProfileError.Text = exception.Message;
        }
    }
}
