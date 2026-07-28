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
    private static readonly ConditionalWeakTable<ListBox, DragController> Controllers = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(DragScrollBehavior),
        new PropertyMetadata(false, IsEnabledChanged));

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
            if (!Controllers.TryGetValue(listBox, out var controller))
            {
                controller = new DragController(listBox);
                Controllers.Add(listBox, controller);
            }

            controller.Attach();
            return;
        }

        if (Controllers.TryGetValue(listBox, out var existingController))
        {
            existingController.Dispose();
            Controllers.Remove(listBox);
        }
    }

    private sealed class DragController : IDisposable
    {
        private const double DecelerationPerSecond = 5.8;
        private const double MaximumVelocity = 4600;
        private const double MinimumInertiaVelocity = 140;
        private const double StopVelocity = 30;
        private static readonly TimeSpan MaximumReleaseSampleAge = TimeSpan.FromMilliseconds(120);

        private readonly ListBox _listBox;
        private readonly DispatcherTimer _inertiaTimer;
        private ScrollViewer? _scrollViewer;
        private Window? _captureHost;
        private Cursor? _captureHostCursor;
        private Point _startPoint;
        private Point _lastPoint;
        private double _startOffset;
        private double _velocity;
        private long _lastSampleTimestamp;
        private long _lastInertiaTimestamp;
        private bool _isAttached;
        private bool _isCandidate;
        private bool _isDragging;
        private bool _isReleasingCapture;

        public DragController(ListBox listBox)
        {
            _listBox = listBox;
            _inertiaTimer = new DispatcherTimer(DispatcherPriority.Render, listBox.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            _inertiaTimer.Tick += OnInertiaTick;
        }

        public void Attach()
        {
            if (_isAttached)
            {
                return;
            }

            _listBox.PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
            _listBox.PreviewMouseWheel += OnMouseWheel;
            _listBox.PreviewKeyDown += OnKeyDown;
            _listBox.Unloaded += OnUnloaded;
            _isAttached = true;
        }

        public void Dispose()
        {
            if (_isAttached)
            {
                _listBox.PreviewMouseLeftButtonDown -= OnMouseLeftButtonDown;
                _listBox.PreviewMouseWheel -= OnMouseWheel;
                _listBox.PreviewKeyDown -= OnKeyDown;
                _listBox.Unloaded -= OnUnloaded;
                _isAttached = false;
            }

            StopInertia();
            CancelDrag();
            _scrollViewer = null;
            _inertiaTimer.Tick -= OnInertiaTick;
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
        {
            _ = sender;
            StopInertia();
            CancelDrag();

            if (IsInteractiveElement(args.OriginalSource as DependencyObject, _listBox)
                || FindDescendant<ScrollViewer>(_listBox) is not { ScrollableHeight: > 0 } scrollViewer)
            {
                return;
            }

            _scrollViewer = scrollViewer;
            _isCandidate = true;
            _startPoint = args.GetPosition(_listBox);
            _startOffset = scrollViewer.VerticalOffset;
            _lastPoint = _startPoint;
            _lastSampleTimestamp = Stopwatch.GetTimestamp();
            _velocity = 0;
            if (!BeginPointerCapture())
            {
                CancelDrag();
                return;
            }

            // These rows are not selectable. Handling the press prevents ListBox from
            // taking capture and starting its built-in selection auto-scroll timer.
            args.Handled = true;
        }

        private void OnCapturedMouseMove(object sender, MouseEventArgs args)
        {
            _ = sender;
            if (!_isCandidate)
            {
                return;
            }

            if (args.LeftButton != MouseButtonState.Pressed)
            {
                CancelDrag();
                return;
            }

            var currentPoint = args.GetPosition(_listBox);
            var delta = currentPoint - _startPoint;
            if (!_isDragging
                && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            if (!_isDragging)
            {
                if (_captureHost?.IsMouseCaptured != true)
                {
                    CancelDrag();
                    return;
                }

                _isDragging = true;
                _captureHost.Cursor = Cursors.ScrollNS;
            }

            SampleVelocity(currentPoint);
            ScrollTo(_startOffset - delta.Y);
            args.Handled = true;
        }

        private void OnCapturedMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
        {
            _ = sender;
            if (!_isCandidate)
            {
                return;
            }

            if (_isDragging)
            {
                args.Handled = true;
            }

            var shouldStartInertia = _isDragging;
            AdjustVelocityForReleaseDelay();
            FinishDrag();
            if (shouldStartInertia)
            {
                StartInertia();
            }
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs args)
        {
            _ = sender;
            _ = args;
            if (_isReleasingCapture)
            {
                return;
            }

            StopInertia();
            CancelDrag(releaseCapture: false);
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs args)
        {
            _ = sender;
            _ = args;
            StopInertia();
            if (_isCandidate)
            {
                CancelDrag();
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs args)
        {
            _ = sender;
            _ = args;
            StopInertia();
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            _ = sender;
            _ = args;
            StopInertia();
            CancelDrag();
            _scrollViewer = null;
        }

        private bool BeginPointerCapture()
        {
            if (Window.GetWindow(_listBox) is not { } captureHost)
            {
                return false;
            }

            _captureHost = captureHost;
            _captureHostCursor = captureHost.Cursor;
            captureHost.PreviewMouseMove += OnCapturedMouseMove;
            captureHost.PreviewMouseLeftButtonUp += OnCapturedMouseLeftButtonUp;
            captureHost.PreviewMouseWheel += OnMouseWheel;
            captureHost.LostMouseCapture += OnLostMouseCapture;

            if (Mouse.Capture(captureHost, CaptureMode.Element))
            {
                return true;
            }

            DetachCaptureHost();
            return false;
        }

        private void SampleVelocity(Point currentPoint)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastSampleTimestamp, now).TotalSeconds;
            if (elapsed is > 0 and <= 0.2)
            {
                var instantaneousVelocity = Math.Clamp(
                    -(currentPoint.Y - _lastPoint.Y) / elapsed,
                    -MaximumVelocity,
                    MaximumVelocity);
                _velocity = Math.Sign(instantaneousVelocity) != Math.Sign(_velocity)
                    ? instantaneousVelocity
                    : (_velocity * 0.55) + (instantaneousVelocity * 0.45);
            }
            else
            {
                _velocity = 0;
            }

            _lastPoint = currentPoint;
            _lastSampleTimestamp = now;
        }

        private void ScrollTo(double requestedOffset)
        {
            if (_scrollViewer is not { } scrollViewer)
            {
                return;
            }

            var offset = Math.Clamp(requestedOffset, 0, scrollViewer.ScrollableHeight);
            if (Math.Abs(scrollViewer.VerticalOffset - offset) >= 0.1)
            {
                scrollViewer.ScrollToVerticalOffset(offset);
            }
        }

        private void AdjustVelocityForReleaseDelay()
        {
            var elapsed = Stopwatch.GetElapsedTime(_lastSampleTimestamp);
            if (elapsed >= MaximumReleaseSampleAge)
            {
                _velocity = 0;
                return;
            }

            _velocity *= Math.Exp(-DecelerationPerSecond * elapsed.TotalSeconds);
        }

        private void StartInertia()
        {
            if (!SystemParameters.ClientAreaAnimation
                || Math.Abs(_velocity) < MinimumInertiaVelocity
                || _scrollViewer is not { ScrollableHeight: > 0 } scrollViewer
                || IsAtBoundary(scrollViewer, _velocity))
            {
                _velocity = 0;
                return;
            }

            _velocity = Math.Clamp(_velocity, -MaximumVelocity, MaximumVelocity);
            _lastInertiaTimestamp = Stopwatch.GetTimestamp();
            _inertiaTimer.Start();
        }

        private void OnInertiaTick(object? sender, EventArgs args)
        {
            _ = sender;
            _ = args;
            if (_scrollViewer is not { } scrollViewer)
            {
                StopInertia();
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var elapsed = Math.Clamp(
                Stopwatch.GetElapsedTime(_lastInertiaTimestamp, now).TotalSeconds,
                0,
                0.05);
            _lastInertiaTimestamp = now;

            var requestedOffset = scrollViewer.VerticalOffset + (_velocity * elapsed);
            var offset = Math.Clamp(requestedOffset, 0, scrollViewer.ScrollableHeight);
            scrollViewer.ScrollToVerticalOffset(offset);
            _velocity *= Math.Exp(-DecelerationPerSecond * elapsed);

            if (Math.Abs(_velocity) < StopVelocity
                || !double.Equals(requestedOffset, offset)
                || IsAtBoundary(scrollViewer, _velocity))
            {
                StopInertia();
            }
        }

        private static bool IsAtBoundary(ScrollViewer scrollViewer, double velocity) =>
            (velocity < 0 && scrollViewer.VerticalOffset <= 0)
            || (velocity > 0 && scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight);

        private void StopInertia()
        {
            _inertiaTimer.Stop();
            _velocity = 0;
        }

        private void CancelDrag(bool releaseCapture = true)
        {
            _velocity = 0;
            FinishDrag(releaseCapture);
        }

        private void FinishDrag(bool releaseCapture = true)
        {
            _isCandidate = false;
            _isDragging = false;
            if (_captureHost is { } captureHost)
            {
                captureHost.Cursor = _captureHostCursor;
                if (releaseCapture && captureHost.IsMouseCaptured)
                {
                    try
                    {
                        _isReleasingCapture = true;
                        captureHost.ReleaseMouseCapture();
                    }
                    finally
                    {
                        _isReleasingCapture = false;
                    }
                }
            }

            DetachCaptureHost();
        }

        private void DetachCaptureHost()
        {
            if (_captureHost is { } captureHost)
            {
                captureHost.PreviewMouseMove -= OnCapturedMouseMove;
                captureHost.PreviewMouseLeftButtonUp -= OnCapturedMouseLeftButtonUp;
                captureHost.PreviewMouseWheel -= OnMouseWheel;
                captureHost.LostMouseCapture -= OnLostMouseCapture;
            }

            _captureHost = null;
            _captureHostCursor = null;
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
}
