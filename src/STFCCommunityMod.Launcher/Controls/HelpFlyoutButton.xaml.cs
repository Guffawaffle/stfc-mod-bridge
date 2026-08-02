using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace STFCCommunityMod.Launcher.Controls;

public partial class HelpFlyoutButton : UserControl
{
    private static WeakReference<HelpFlyoutButton>? activeFlyout;

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

    private readonly DispatcherTimer closeTimer;
    private Window? ownerWindow;

    public HelpFlyoutButton()
    {
        InitializeComponent();
        closeTimer = new(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        closeTimer.Tick += CloseTimer_Tick;
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
        OpenFlyout();
    }

    private void FlyoutButton_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        OpenFlyout();
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
        ClearActiveFlyout();
    }

    private void FlyoutSurface_MouseEnter(object sender, MouseEventArgs e)
    {
        _ = sender;
        _ = e;
        OpenFlyout();
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
        Flyout.IsOpen = false;
        FlyoutButton.Focus();
        Keyboard.Focus(FlyoutButton);
    }

    private void CloseTimer_Tick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        closeTimer.Stop();
        if (!IsLoaded)
        {
            return;
        }

        if (FlyoutButton.IsMouseOver
            || FlyoutContent.IsMouseOver
            || FlyoutButton.IsKeyboardFocusWithin
            || FlyoutContent.IsKeyboardFocusWithin)
        {
            return;
        }

        Flyout.IsOpen = false;
    }

    private void OpenFlyout()
    {
        closeTimer.Stop();
        if (activeFlyout?.TryGetTarget(out var previous) == true
            && !ReferenceEquals(previous, this))
        {
            previous.CloseFromPeer();
        }

        activeFlyout = new(this);
        Flyout.IsOpen = true;
    }

    private void CloseFromPeer()
    {
        closeTimer.Stop();
        Flyout.IsOpen = false;
    }

    private void ClearActiveFlyout()
    {
        if (activeFlyout?.TryGetTarget(out var active) == true
            && ReferenceEquals(active, this))
        {
            activeFlyout = null;
        }
    }

    private void ScheduleCloseIfUnowned()
    {
        if (!IsLoaded)
        {
            return;
        }

        closeTimer.Stop();
        closeTimer.Start();
    }

    private void HelpFlyoutButton_Loaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ownerWindow is not null)
        {
            ownerWindow.Deactivated -= OwnerWindow_Deactivated;
        }

        ownerWindow = Window.GetWindow(this);
        if (ownerWindow is not null)
        {
            ownerWindow.Deactivated += OwnerWindow_Deactivated;
        }
    }

    private void OwnerWindow_Deactivated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        CloseFromPeer();
    }

    private void HelpFlyoutButton_Unloaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        closeTimer.Stop();
        if (ownerWindow is not null)
        {
            ownerWindow.Deactivated -= OwnerWindow_Deactivated;
            ownerWindow = null;
        }

        Flyout.IsOpen = false;
        ClearActiveFlyout();
    }
}
