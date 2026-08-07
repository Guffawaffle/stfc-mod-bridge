using System.Windows;

namespace STFCCommunityMod.Launcher;

public partial class ReleaseSecurityGuidanceWindow : Window
{
    public ReleaseSecurityGuidanceWindow()
    {
        InitializeComponent();
        var guidance = BundledReleaseSecurityGuidance.Load();
        IndependentVerificationText.Text = guidance.IndependentVerification;
        CompromiseResponseText.Text = guidance.CompromiseResponse;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }
}
