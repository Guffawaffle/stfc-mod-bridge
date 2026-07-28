using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace STFCCommunityMod.Launcher.Behaviors;

public static class DragScrollBehavior
{
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
            listBox.LostMouseCapture += LostMouseCapture;
            return;
        }

        listBox.PreviewMouseLeftButtonDown -= PreviewMouseLeftButtonDown;
        listBox.PreviewMouseMove -= PreviewMouseMove;
        listBox.PreviewMouseLeftButtonUp -= PreviewMouseLeftButtonUp;
        listBox.LostMouseCapture -= LostMouseCapture;
        if (States.TryGetValue(listBox, out var state))
        {
            Reset(listBox, state);
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
        if (IsInteractiveElement(args.OriginalSource as DependencyObject, listBox)
            || FindDescendant<ScrollViewer>(listBox) is not { ScrollableHeight: > 0 } scrollViewer)
        {
            Reset(listBox, state);
            return;
        }

        state.IsCandidate = true;
        state.StartPoint = args.GetPosition(listBox);
        state.StartOffset = scrollViewer.VerticalOffset;
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
            Reset(listBox, state);
            return;
        }

        var delta = args.GetPosition(listBox) - state.StartPoint;
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
                Reset(listBox, state);
                return;
            }

            listBox.Cursor = Cursors.ScrollNS;
        }

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

        Reset(listBox, state);
    }

    private static void LostMouseCapture(object sender, MouseEventArgs args)
    {
        _ = args;
        if (sender is ListBox listBox && States.TryGetValue(listBox, out var state))
        {
            Reset(listBox, state, releaseCapture: false);
        }
    }

    private static void Reset(ListBox listBox, DragState state, bool releaseCapture = true)
    {
        state.IsCandidate = false;
        state.IsDragging = false;
        listBox.Cursor = null;
        if (releaseCapture && listBox.IsMouseCaptured)
        {
            listBox.ReleaseMouseCapture();
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

        public bool IsCandidate { get; set; }

        public bool IsDragging { get; set; }
    }
}
