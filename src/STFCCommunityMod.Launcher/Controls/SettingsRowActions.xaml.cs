using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace STFCCommunityMod.Launcher.Controls;

public partial class SettingsRowActions : UserControl
{
    public SettingsRowActions()
    {
        InitializeComponent();
    }

    private void MoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        CapturePopup.IsOpen = false;
        ActionPopup.IsOpen = !ActionPopup.IsOpen;
        if (ActionPopup.IsOpen)
        {
            Dispatcher.BeginInvoke(
                () =>
                {
                    AddBindingAction.Focus();
                    Keyboard.Focus(AddBindingAction);
                },
                DispatcherPriority.Input);
        }
    }

    private void AddBinding_Click(object sender, RoutedEventArgs e)
    {
        ActionPopup.IsOpen = false;
        CapturePopup.IsOpen = true;
        Dispatcher.BeginInvoke(
            CaptureButton.StartCapture,
            DispatcherPriority.Input);
    }

    private void CaptureButton_CaptureFinished(object? sender, EventArgs e)
    {
        CapturePopup.IsOpen = false;
    }

    private void CapturePopup_Closed(object? sender, EventArgs e)
    {
        CaptureButton.StopCapture();
    }

    private void ActionPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        ActionPopup.IsOpen = false;
        MoreActionsButton.Focus();
        Keyboard.Focus(MoreActionsButton);
    }
}
