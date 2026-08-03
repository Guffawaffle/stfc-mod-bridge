using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace STFCCommunityMod.Launcher.Behaviors;

public static class KeepVisibleAboveFooterBehavior
{
    private const double SafeMargin = 12;
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(BehaviorState),
        typeof(KeepVisibleAboveFooterBehavior));

    public static readonly DependencyProperty FooterProperty = DependencyProperty.RegisterAttached(
        "Footer",
        typeof(FrameworkElement),
        typeof(KeepVisibleAboveFooterBehavior),
        new PropertyMetadata(null, FooterChanged));

    public static FrameworkElement? GetFooter(DependencyObject element) =>
        (FrameworkElement?)element.GetValue(FooterProperty);

    public static void SetFooter(DependencyObject element, FrameworkElement? value) =>
        element.SetValue(FooterProperty, value);

    internal static double CalculateTargetOffset(
        double currentOffset,
        double scrollableHeight,
        double viewportHeight,
        double anchorTop,
        double anchorHeight,
        double safeMargin = SafeMargin)
    {
        if (!double.IsFinite(currentOffset)
            || !double.IsFinite(scrollableHeight)
            || !double.IsFinite(viewportHeight)
            || !double.IsFinite(anchorTop)
            || !double.IsFinite(anchorHeight)
            || viewportHeight <= 0
            || anchorHeight <= 0)
        {
            return currentOffset;
        }

        var boundedMargin = Math.Clamp(safeMargin, 0, viewportHeight / 2);
        var visibleTop = boundedMargin;
        var visibleBottom = viewportHeight - boundedMargin;
        var anchorBottom = anchorTop + anchorHeight;
        var delta = anchorTop < visibleTop
            ? anchorTop - visibleTop
            : anchorBottom > visibleBottom
                ? anchorBottom - visibleBottom
                : 0;
        return Math.Clamp(currentOffset + delta, 0, Math.Max(0, scrollableHeight));
    }

    internal static bool ShouldAdjustForFooterTransition(bool wasVisible, bool isVisible) =>
        !wasVisible && isVisible;

    private static void FooterChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not FrameworkElement scrollOwner)
        {
            throw new ArgumentException("The footer visibility behavior requires a FrameworkElement owner.");
        }

        if (scrollOwner.GetValue(StateProperty) is BehaviorState previous)
        {
            previous.Dispose();
            scrollOwner.ClearValue(StateProperty);
        }

        if (args.NewValue is FrameworkElement footer)
        {
            scrollOwner.SetValue(StateProperty, new BehaviorState(scrollOwner, footer));
        }
    }

    private sealed class BehaviorState : IDisposable
    {
        private static readonly DependencyPropertyDescriptor VisibilityDescriptor =
            DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(FrameworkElement));
        private readonly FrameworkElement scrollOwner;
        private readonly FrameworkElement footer;
        private WeakReference<FrameworkElement>? lastInteractionAnchor;
        private bool footerVisible;
        private bool isListening;
        private int transitionVersion;

        public BehaviorState(FrameworkElement scrollOwner, FrameworkElement footer)
        {
            this.scrollOwner = scrollOwner;
            this.footer = footer;
            scrollOwner.Loaded += ScrollOwner_Loaded;
            scrollOwner.Unloaded += ScrollOwner_Unloaded;
            if (scrollOwner.IsLoaded)
            {
                StartListening();
            }
        }

        public void Dispose()
        {
            scrollOwner.Loaded -= ScrollOwner_Loaded;
            scrollOwner.Unloaded -= ScrollOwner_Unloaded;
            StopListening();
        }

        private void ScrollOwner_Loaded(object sender, RoutedEventArgs e) => StartListening();

        private void ScrollOwner_Unloaded(object sender, RoutedEventArgs e) => StopListening();

        private void StartListening()
        {
            if (isListening)
            {
                return;
            }

            isListening = true;
            footerVisible = footer.Visibility == Visibility.Visible;
            scrollOwner.AddHandler(
                UIElement.PreviewMouseDownEvent,
                new MouseButtonEventHandler(ScrollOwner_PreviewMouseDown),
                true);
            scrollOwner.AddHandler(
                Keyboard.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(ScrollOwner_GotKeyboardFocus),
                true);
            VisibilityDescriptor.AddValueChanged(footer, Footer_VisibilityChanged);
        }

        private void StopListening()
        {
            if (!isListening)
            {
                return;
            }

            isListening = false;
            transitionVersion++;
            lastInteractionAnchor = null;
            scrollOwner.RemoveHandler(
                UIElement.PreviewMouseDownEvent,
                new MouseButtonEventHandler(ScrollOwner_PreviewMouseDown));
            scrollOwner.RemoveHandler(
                Keyboard.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(ScrollOwner_GotKeyboardFocus));
            VisibilityDescriptor.RemoveValueChanged(footer, Footer_VisibilityChanged);
        }

        private void ScrollOwner_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
            RememberInteractionAnchor(e.OriginalSource as DependencyObject);

        private void ScrollOwner_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
            RememberInteractionAnchor(e.NewFocus as DependencyObject);

        private void RememberInteractionAnchor(DependencyObject? source)
        {
            if (FindControlAnchor(source) is { } anchor && scrollOwner.IsAncestorOf(anchor))
            {
                lastInteractionAnchor = new(anchor);
            }
        }

        private void Footer_VisibilityChanged(object? sender, EventArgs e)
        {
            var isVisible = footer.Visibility == Visibility.Visible;
            var shouldAdjust = ShouldAdjustForFooterTransition(footerVisible, isVisible);
            if (isVisible == footerVisible)
            {
                return;
            }

            footerVisible = isVisible;
            var version = ++transitionVersion;
            if (!shouldAdjust || ResolveAnchor() is not { } anchor)
            {
                return;
            }

            var anchorReference = new WeakReference<FrameworkElement>(anchor);
            _ = scrollOwner.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                () => KeepAnchorVisible(anchorReference, version));
        }

        private FrameworkElement? ResolveAnchor()
        {
            if (Keyboard.FocusedElement is DependencyObject focused
                && FindControlAnchor(focused) is { } focusedAnchor
                && scrollOwner.IsAncestorOf(focusedAnchor))
            {
                return focusedAnchor;
            }

            return lastInteractionAnchor is not null
                && lastInteractionAnchor.TryGetTarget(out var remembered)
                && scrollOwner.IsAncestorOf(remembered)
                    ? remembered
                    : null;
        }

        private void KeepAnchorVisible(WeakReference<FrameworkElement> anchorReference, int version)
        {
            if (version != transitionVersion
                || !footerVisible
                || footer.Visibility != Visibility.Visible
                || !anchorReference.TryGetTarget(out var anchor)
                || !scrollOwner.IsAncestorOf(anchor)
                || FindDescendant<ScrollViewer>(scrollOwner) is not { } scrollViewer)
            {
                return;
            }

            try
            {
                var bounds = anchor.TransformToAncestor(scrollViewer)
                    .TransformBounds(new Rect(new Point(), anchor.RenderSize));
                var targetOffset = CalculateTargetOffset(
                    scrollViewer.VerticalOffset,
                    scrollViewer.ScrollableHeight,
                    scrollViewer.ActualHeight,
                    bounds.Top,
                    bounds.Height);
                if (Math.Abs(targetOffset - scrollViewer.VerticalOffset) >= 0.5)
                {
                    scrollViewer.ScrollToVerticalOffset(targetOffset);
                }
            }
            catch (InvalidOperationException)
            {
                // The anchor was recycled between layout and correction; do not move the viewport.
            }
        }

        private FrameworkElement? FindControlAnchor(DependencyObject? source)
        {
            FrameworkElement? anchor = null;
            for (var current = source; current is not null && !ReferenceEquals(current, scrollOwner); current = ParentOf(current))
            {
                if (current is Control control && current is not ListBoxItem)
                {
                    anchor = control;
                }
            }

            return anchor;
        }

        private static DependencyObject? ParentOf(DependencyObject child) =>
            child is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);

        private static T? FindDescendant<T>(DependencyObject parent)
            where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match)
                {
                    return match;
                }

                if (FindDescendant<T>(child) is { } descendant)
                {
                    return descendant;
                }
            }

            return null;
        }
    }
}
