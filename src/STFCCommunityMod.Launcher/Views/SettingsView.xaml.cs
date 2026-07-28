using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void SearchToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { IsSearchVisible: true })
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () =>
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                });
        }
    }
}
