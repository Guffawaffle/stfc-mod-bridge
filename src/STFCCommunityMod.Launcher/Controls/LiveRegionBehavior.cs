using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;

namespace STFCCommunityMod.Launcher.Controls;

public static class LiveRegionBehavior
{
    public static readonly DependencyProperty AnnouncementProperty = DependencyProperty.RegisterAttached(
        "Announcement",
        typeof(string),
        typeof(LiveRegionBehavior),
        new PropertyMetadata(string.Empty, AnnouncementChanged));

    public static string GetAnnouncement(DependencyObject element) =>
        (string)element.GetValue(AnnouncementProperty);

    public static void SetAnnouncement(DependencyObject element, string value) =>
        element.SetValue(AnnouncementProperty, value);

    public static bool IsAnnouncementTransition(string? previous, string? announcement) =>
        !string.IsNullOrWhiteSpace(announcement)
        && !string.Equals(previous, announcement, StringComparison.Ordinal);

    private static void AnnouncementChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        var previous = args.OldValue as string ?? string.Empty;
        var announcement = args.NewValue as string ?? string.Empty;
        AutomationProperties.SetName(element, announcement);
        if (!IsAnnouncementTransition(previous, announcement))
        {
            return;
        }

        _ = element.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                if (!element.IsVisible)
                {
                    return;
                }
                var peer = UIElementAutomationPeer.FromElement(element)
                    ?? UIElementAutomationPeer.CreatePeerForElement(element);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            });
    }
}
