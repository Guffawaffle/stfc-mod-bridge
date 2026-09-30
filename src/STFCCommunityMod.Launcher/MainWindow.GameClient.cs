using System.ComponentModel;
using System.Windows;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher;

public partial class MainWindow
{
    private async void OpenGameClientButton_Click(object sender, RoutedEventArgs e)
    {
        GameClientDialog.IsOpen = true;
        if (DataContext is MainWindowViewModel viewModel)
            await viewModel.GameClient.RefreshStatusAsync(lifetimeCancellation.Token);
    }

    private async void CheckGameClientButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            await viewModel.GameClient.CheckAsync(lifetimeCancellation.Token);
    }

    private async void UpdateGameClientButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && ReadyForGameClientMutation())
            await viewModel.GameClient.UpdateAsync(lifetimeCancellation.Token);
    }

    private async void RecoverGameClientButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && ReadyForGameClientMutation())
            await viewModel.GameClient.RecoverAsync(lifetimeCancellation.Token);
    }

    private bool ReadyForGameClientMutation()
    {
        if (!SharedSettings.HasPendingChanges) return true;
        SettingsUnavailableMessage.Text = "Save or discard Settings and Data Sync drafts before updating or recovering the game installation.";
        SettingsUnavailableDialog.IsOpen = true;
        return false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.GameClient.IsMutationInProgress)
        {
            e.Cancel = true;
            GameClientDialog.IsOpen = true;
        }
        base.OnClosing(e);
    }
}
