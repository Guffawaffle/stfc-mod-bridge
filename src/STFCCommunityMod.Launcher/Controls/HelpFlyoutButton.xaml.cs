using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace STFCCommunityMod.Launcher.Controls;

public partial class HelpFlyoutButton : UserControl
{
    public static readonly DependencyProperty HelpTextProperty =
        DependencyProperty.Register(
            nameof(HelpText),
            typeof(string),
            typeof(HelpFlyoutButton),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty AutomationNameProperty =
        DependencyProperty.Register(
            nameof(AutomationName),
            typeof(string),
            typeof(HelpFlyoutButton),
            new PropertyMetadata("More information"));

    private bool isPinned;
    private DispatcherOperation? pendingCloseOperation;

    public HelpFlyoutButton()
    {
        InitializeComponent();
    }

    public string HelpText
    {
        get => (string)GetValue(HelpTextProperty);
        set => SetValue(HelpTextProperty, value);
    }

    public string AutomationName
    {
        get => (string)GetValue(AutomationNameProperty);
        set => SetValue(AutomationNameProperty, value);
    }

    public bool IsFlyoutOpen => Flyout.IsOpen;

    private void FlyoutButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        isPinned = !isPinned;
        Flyout.IsOpen = isPinned;
    }

    private void FlyoutButton_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        Flyout.IsOpen = true;
    }

    private void FlyoutButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        ScheduleCloseIfUnowned();
    }

    private void Flyout_Closed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        isPinned = false;
    }

    private void FlyoutSurface_MouseEnter(object sender, MouseEventArgs e)
    {
        _ = sender;
        _ = e;
        Flyout.IsOpen = true;
    }

    private void FlyoutSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        _ = sender;
        _ = e;
        ScheduleCloseIfUnowned();
    }

    private void FlyoutSurface_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        isPinned = false;
        Flyout.IsOpen = false;
        FlyoutButton.Focus();
        Keyboard.Focus(FlyoutButton);
    }

    private void CloseIfUnowned()
    {
        pendingCloseOperation = null;
        if (!IsLoaded)
        {
            return;
        }

        if (isPinned
            || FlyoutButton.IsMouseOver
            || FlyoutContent.IsMouseOver
            || FlyoutButton.IsKeyboardFocusWithin
            || FlyoutContent.IsKeyboardFocusWithin)
        {
            return;
        }

        Flyout.IsOpen = false;
    }

    private void ScheduleCloseIfUnowned()
    {
        if (!IsLoaded)
        {
            return;
        }

        pendingCloseOperation?.Abort();
        pendingCloseOperation = Dispatcher.BeginInvoke(
            CloseIfUnowned,
            DispatcherPriority.Background);
    }

    private void HelpFlyoutButton_Unloaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        pendingCloseOperation?.Abort();
        pendingCloseOperation = null;
        isPinned = false;
        Flyout.IsOpen = false;
    }
}
