using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

public partial class MainWindow
{
    private LauncherProfilesSnapshot profiles = LauncherProfilesSnapshot.Empty;
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
        RefreshProfilesList(null);
        NewProfileButton_Click(sender, e);
        ProfilesDialog.IsOpen = true;
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
        }
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var defaultDirectory = (DataContext as ViewModels.MainWindowViewModel)?.SelectedGameDirectory;
            var selected = ProfilesList.SelectedItem as LauncherProfile;
            var updated = selected is not null
                ? LauncherProfiles.Edit(profiles, selected.Id, ProfileNameBox.Text, ProfileFolderBox.Text, defaultDirectory)
                : LauncherProfiles.Add(profiles, ProfileNameBox.Text, ProfileFolderBox.Text, defaultDirectory,
                    isAdoptingProfile ? ProfileKeyBox.Text.Trim() : null);
            var id = selected?.Id ?? updated.Profiles[^1].Id;
            ProfilesStore.Save(updated);
            profiles = updated;
            RefreshProfilesList(id);
            ProfileError.Text = string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            ProfileError.Text = exception.Message;
        }
    }

    private void RemoveProfileButton_Click(object sender, RoutedEventArgs e)
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
            ProfilesStore.Save(updated);
            profiles = updated;
            RefreshProfilesList(null);
            NewProfileButton_Click(sender, e);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ProfileError.Text = exception.Message;
        }
    }
}
