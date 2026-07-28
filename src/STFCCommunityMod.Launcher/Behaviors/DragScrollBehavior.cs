using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace STFCCommunityMod.Launcher.Behaviors;

public static class DragScrollBehavior
{
    private const double DecelerationPerSecond = 5.8;
    private const double MaximumVelocity = 4600;
    private const double MinimumInertiaVelocity = 140;
    private const double StopVelocity = 30;
    private static readonly TimeSpan MaximumReleaseSampleAge = TimeSpan.FromMilliseconds(120);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(DragScrollBehavior),
        new PropertyMetadata(false, IsEnabledChanged));

    private static readonly ConditionalWeakTable<ListBox, DragState> States = new();

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void IsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ListBox listBox)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            _ = States.GetOrCreateValue(listBox);
            listBox.PreviewMouseLeftButtonDown += PreviewMouseLeftButtonDown;
            listBox.PreviewMouseMove += PreviewMouseMove;
            listBox.PreviewMouseLeftButtonUp += PreviewMouseLeftButtonUp;
            listBox.PreviewMouseWheel += PreviewMouseWheel;
            listBox.PreviewKeyDown += PreviewKeyDown;
            listBox.LostMouseCapture += LostMouseCapture;
            listBox.Unloaded += ListBoxUnloaded;
            return;
        }

        listBox.PreviewMouseLeftButtonDown -= PreviewMouseLeftButtonDown;
        listBox.PreviewMouseMove -= PreviewMouseMove;
        listBox.PreviewMouseLeftButtonUp -= PreviewMouseLeftButtonUp;
        listBox.PreviewMouseWheel -= PreviewMouseWheel;
        listBox.PreviewKeyDown -= PreviewKeyDown;
        listBox.LostMouseCapture -= LostMouseCapture;
        listBox.Unloaded -= ListBoxUnloaded;
        if (States.TryGetValue(listBox, out var state))
        {
            StopInertia(state);
            CancelDrag(listBox, state);
            States.Remove(listBox);
        }
    }

    private static void PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        var state = States.GetOrCreateValue(listBox);
        StopInertia(state);
        if (IsInteractiveElement(args.OriginalSource as DependencyObject, listBox)
            || FindDescendant<ScrollViewer>(listBox) is not { ScrollableHeight: > 0 } scrollViewer)
        {
            CancelDrag(listBox, state);
            return;
        }

        state.IsCandidate = true;
        state.StartPoint = args.GetPosition(listBox);
        state.StartOffset = scrollViewer.VerticalOffset;
        state.LastPoint = state.StartPoint;
        state.LastSampleTimestamp = Stopwatch.GetTimestamp();
        state.Velocity = 0;
    }

    private static void PreviewMouseMove(object sender, MouseEventArgs args)
    {
        if (sender is not ListBox listBox
            || !States.TryGetValue(listBox, out var state)
            || !state.IsCandidate)
        {
            return;
        }

        if (args.LeftButton != MouseButtonState.Pressed)
        {
            CancelDrag(listBox, state);
            return;
        }

        var currentPoint = args.GetPosition(listBox);
        var delta = currentPoint - state.StartPoint;
        if (!state.IsDragging
            && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (!state.IsDragging)
        {
            state.IsDragging = listBox.CaptureMouse();
            if (!state.IsDragging)
            {
                CancelDrag(listBox, state);
                return;
            }

            listBox.Cursor = Cursors.ScrollNS;
        }

        SampleVelocity(state, currentPoint);
        if (FindDescendant<ScrollViewer>(listBox) is { } scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset(state.StartOffset - delta.Y);
            args.Handled = true;
        }
    }

    private static void PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (sender is not ListBox listBox || !States.TryGetValue(listBox, out var state))
        {
            return;
        }

        if (state.IsDragging)
        {
            args.Handled = true;
        }

        var shouldStartInertia = state.IsDragging;
        AdjustVelocityForReleaseDelay(state);
        FinishDrag(listBox, state);
        if (shouldStartInertia)
        {
            StartInertia(listBox, state);
        }
    }

    private static void LostMouseCapture(object sender, MouseEventArgs args)
    {
        _ = args;
        if (sender is ListBox listBox && States.TryGetValue(listBox, out var state))
        {
            if (state.IsReleasingCapture)
            {
                return;
            }

            StopInertia(state);
            CancelDrag(listBox, state, releaseCapture: false);
        }
    }

    private static void PreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        _ = args;
        if (sender is ListBox listBox && States.TryGetValue(listBox, out var state))
        {
            StopInertia(state);
        }
    }

    private static void PreviewKeyDown(object sender, KeyEventArgs args)
    {
        _ = args;
        if (sender is ListBox listBox && States.TryGetValue(listBox, out var state))
        {
            StopInertia(state);
        }
    }

    private static void ListBoxUnloaded(object sender, RoutedEventArgs args)
    {
        _ = args;
        if (sender is ListBox listBox && States.TryGetValue(listBox, out var state))
        {
            StopInertia(state);
            CancelDrag(listBox, state);
        }
    }

    private static void SampleVelocity(DragState state, Point currentPoint)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(state.LastSampleTimestamp, now).TotalSeconds;
        if (elapsed is > 0 and <= 0.2)
        {
            var instantaneousVelocity = Math.Clamp(
                -(currentPoint.Y - state.LastPoint.Y) / elapsed,
                -MaximumVelocity,
                MaximumVelocity);
            state.Velocity = Math.Sign(instantaneousVelocity) != Math.Sign(state.Velocity)
                ? instantaneousVelocity
                : (state.Velocity * 0.55) + (instantaneousVelocity * 0.45);
        }
        else
        {
            state.Velocity = 0;
        }

        state.LastPoint = currentPoint;
        state.LastSampleTimestamp = now;
    }

    private static void AdjustVelocityForReleaseDelay(DragState state)
    {
        var elapsed = Stopwatch.GetElapsedTime(state.LastSampleTimestamp);
        if (elapsed >= MaximumReleaseSampleAge)
        {
            state.Velocity = 0;
            return;
        }

        state.Velocity *= Math.Exp(-DecelerationPerSecond * elapsed.TotalSeconds);
    }

    private static void StartInertia(ListBox listBox, DragState state)
    {
        if (!SystemParameters.ClientAreaAnimation
            || Math.Abs(state.Velocity) < MinimumInertiaVelocity
            || FindDescendant<ScrollViewer>(listBox) is not { ScrollableHeight: > 0 } scrollViewer
            || IsAtBoundary(scrollViewer, state.Velocity))
        {
            state.Velocity = 0;
            return;
        }

        state.Velocity = Math.Clamp(state.Velocity, -MaximumVelocity, MaximumVelocity);
        state.LastInertiaTimestamp = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(DispatcherPriority.Render, listBox.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        EventHandler? tickHandler = null;
        tickHandler = (_, _) => AdvanceInertia(listBox, state, timer);
        state.InertiaTimer = timer;
        state.InertiaTickHandler = tickHandler;
        timer.Tick += tickHandler;
        timer.Start();
    }

    private static void AdvanceInertia(
        ListBox listBox,
        DragState state,
        DispatcherTimer timer)
    {
        if (!ReferenceEquals(timer, state.InertiaTimer)
            || FindDescendant<ScrollViewer>(listBox) is not { } scrollViewer)
        {
            StopInertia(state);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Clamp(
            Stopwatch.GetElapsedTime(state.LastInertiaTimestamp, now).TotalSeconds,
            0,
            0.05);
        state.LastInertiaTimestamp = now;

        var requestedOffset = scrollViewer.VerticalOffset + (state.Velocity * elapsed);
        var offset = Math.Clamp(requestedOffset, 0, scrollViewer.ScrollableHeight);
        scrollViewer.ScrollToVerticalOffset(offset);
        state.Velocity *= Math.Exp(-DecelerationPerSecond * elapsed);

        if (Math.Abs(state.Velocity) < StopVelocity
            || !double.Equals(requestedOffset, offset)
            || IsAtBoundary(scrollViewer, state.Velocity))
        {
            StopInertia(state);
        }
    }

    private static bool IsAtBoundary(ScrollViewer scrollViewer, double velocity) =>
        (velocity < 0 && scrollViewer.VerticalOffset <= 0)
        || (velocity > 0 && scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight);

    private static void StopInertia(DragState state)
    {
        if (state.InertiaTimer is { } timer)
        {
            timer.Stop();
            if (state.InertiaTickHandler is { } tickHandler)
            {
                timer.Tick -= tickHandler;
            }
        }

        state.InertiaTimer = null;
        state.InertiaTickHandler = null;
        state.Velocity = 0;
    }

    private static void CancelDrag(ListBox listBox, DragState state, bool releaseCapture = true)
    {
        state.Velocity = 0;
        FinishDrag(listBox, state, releaseCapture);
    }

    private static void FinishDrag(ListBox listBox, DragState state, bool releaseCapture = true)
    {
        state.IsCandidate = false;
        state.IsDragging = false;
        listBox.Cursor = null;
        if (releaseCapture && listBox.IsMouseCaptured)
        {
            try
            {
                state.IsReleasingCapture = true;
                listBox.ReleaseMouseCapture();
            }
            finally
            {
                state.IsReleasingCapture = false;
            }
        }
    }

    private static bool IsInteractiveElement(DependencyObject? source, DependencyObject boundary)
    {
        for (var current = source;
             current is not null && !ReferenceEquals(current, boundary);
             current = GetParent(current))
        {
            if (current is ButtonBase
                or TextBoxBase
                or Selector
                or Slider
                or ScrollBar
                or Thumb)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element) =>
        element switch
        {
            Visual or Visual3D => VisualTreeHelper.GetParent(element),
            FrameworkContentElement contentElement => contentElement.Parent,
            _ => LogicalTreeHelper.GetParent(element),
        };

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
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

    private sealed class DragState
    {
        public Point StartPoint { get; set; }

        public double StartOffset { get; set; }

        public Point LastPoint { get; set; }

        public long LastSampleTimestamp { get; set; }

        public long LastInertiaTimestamp { get; set; }

        public double Velocity { get; set; }

        public bool IsCandidate { get; set; }

        public bool IsDragging { get; set; }

        public bool IsReleasingCapture { get; set; }

        public DispatcherTimer? InertiaTimer { get; set; }

        public EventHandler? InertiaTickHandler { get; set; }
    }
}
