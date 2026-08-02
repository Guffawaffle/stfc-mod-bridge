using System.Windows;
using System.Windows.Controls;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Views;

public partial class SyncView : UserControl
{
    public SyncView()
    {
        InitializeComponent();
    }

    private void ReplacementToken_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: SyncTargetCardViewModel target } passwordBox)
        {
            target.SetReplacementToken(passwordBox.Password);
        }
    }
}
