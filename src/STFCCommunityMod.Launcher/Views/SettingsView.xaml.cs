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

    public void FocusSearchBoxWhenVisible()
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (DataContext is SettingsViewModel { IsSearchVisible: true })
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                }
            });
    }
}
