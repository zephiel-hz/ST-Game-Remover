using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SteamPluginManager.Helpers
{
    /// <summary>
    /// Attached behavior providing Chromium/Discord-style impulse smooth scrolling for WPF ScrollViewers.
    /// Uses cubic ease-out polynomial trajectory with boundary duration scaling (Chromium ScrollOffsetAnimationCurve).
    /// Prevents subpixel boundary crawling, overshooting, and thrashing near edges.
    /// Subscribes directly to routed ScrollBar.ScrollEvent for 1:1 synchronization with manual thumb dragging,
    /// track clicks, and keyboard navigation.
    /// </summary>
    public static class SmoothScrollViewerHelper
    {
        #region Attached Properties

        public static readonly DependencyProperty EnableSmoothScrollingProperty =
            DependencyProperty.RegisterAttached(
                "EnableSmoothScrolling",
                typeof(bool),
                typeof(SmoothScrollViewerHelper),
                new PropertyMetadata(false, OnEnableSmoothScrollingChanged));

        public static bool GetEnableSmoothScrolling(DependencyObject obj) =>
            (bool)obj.GetValue(EnableSmoothScrollingProperty);

        public static void SetEnableSmoothScrolling(DependencyObject obj, bool value) =>
            obj.SetValue(EnableSmoothScrollingProperty, value);

        public static readonly DependencyProperty DurationMillisecondsProperty =
            DependencyProperty.RegisterAttached(
                "DurationMilliseconds",
                typeof(int),
                typeof(SmoothScrollViewerHelper),
                new PropertyMetadata(200));

        public static int GetDurationMilliseconds(DependencyObject obj) =>
            (int)obj.GetValue(DurationMillisecondsProperty);

        public static void SetDurationMilliseconds(DependencyObject obj, int value) =>
            obj.SetValue(DurationMillisecondsProperty, value);

        public static readonly DependencyProperty ScrollDistanceMultiplierProperty =
            DependencyProperty.RegisterAttached(
                "ScrollDistanceMultiplier",
                typeof(double),
                typeof(SmoothScrollViewerHelper),
                new PropertyMetadata(1.0));

        public static double GetScrollDistanceMultiplier(DependencyObject obj) =>
            (double)obj.GetValue(ScrollDistanceMultiplierProperty);

        public static void SetScrollDistanceMultiplier(DependencyObject obj, double value) =>
            obj.SetValue(ScrollDistanceMultiplierProperty, value);

        private static readonly DependencyProperty SmoothScrollContextProperty =
            DependencyProperty.RegisterAttached(
                "SmoothScrollContext",
                typeof(SmoothScrollContext),
                typeof(SmoothScrollViewerHelper),
                new PropertyMetadata(null));

        #endregion

        private static void OnEnableSmoothScrollingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            bool enable = (bool)e.NewValue;

            if (d is ScrollViewer scrollViewer)
            {
                ApplySmoothScrolling(scrollViewer, enable);
            }
            else if (d is FrameworkElement fe)
            {
                if (fe.IsLoaded)
                {
                    if (FindVisualChild<ScrollViewer>(fe) is ScrollViewer sv)
                    {
                        ApplySmoothScrolling(sv, enable);
                    }
                }
                else
                {
                    RoutedEventHandler? loadedHandler = null;
                    loadedHandler = (s, args) =>
                    {
                        fe.Loaded -= loadedHandler;
                        if (FindVisualChild<ScrollViewer>(fe) is ScrollViewer sv)
                        {
                            ApplySmoothScrolling(sv, (bool)fe.GetValue(EnableSmoothScrollingProperty));
                        }
                    };
                    fe.Loaded += loadedHandler;
                }
            }
        }

        private static void ApplySmoothScrolling(ScrollViewer scrollViewer, bool enable)
        {
            if (enable)
            {
                if (scrollViewer.GetValue(SmoothScrollContextProperty) is SmoothScrollContext oldContext)
                {
                    oldContext.Detach();
                }

                var context = new SmoothScrollContext(scrollViewer);
                scrollViewer.SetValue(SmoothScrollContextProperty, context);
                context.Attach();
            }
            else
            {
                if (scrollViewer.GetValue(SmoothScrollContextProperty) is SmoothScrollContext context)
                {
                    context.Detach();
                    scrollViewer.ClearValue(SmoothScrollContextProperty);
                }
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }
    }

    internal sealed class SmoothScrollContext
    {
        private readonly WeakReference<ScrollViewer> _scrollViewerRef;
        private double _startOffset;
        private double _targetOffset;
        private double _currentOffset;
        private double _durationSec;
        private long _startTimeTicks;
        private bool _isAnimating;
        private long _lastWheelTicks;
        private int _consecutiveWheelCount;
        private int _lastWheelDirection; // 1 for down, -1 for up
        private ScrollEventHandler? _scrollBarScrollHandler;

        public SmoothScrollContext(ScrollViewer scrollViewer)
        {
            _scrollViewerRef = new WeakReference<ScrollViewer>(scrollViewer);
        }

        public void Attach()
        {
            if (!_scrollViewerRef.TryGetTarget(out var sv))
                return;

            _startOffset = sv.VerticalOffset;
            _targetOffset = sv.VerticalOffset;
            _currentOffset = sv.VerticalOffset;

            // Hook routed ScrollBar.ScrollEvent to guarantee immediate 1:1 sync with scrollbar thumb drag/click
            _scrollBarScrollHandler = new ScrollEventHandler(OnScrollBarScroll);
            sv.AddHandler(ScrollBar.ScrollEvent, _scrollBarScrollHandler);

            sv.PreviewMouseWheel += OnPreviewMouseWheel;
            sv.PreviewMouseDown += OnPreviewMouseDown;
            sv.PreviewKeyDown += OnPreviewKeyDown;
            sv.ScrollChanged += OnScrollChanged;
            sv.Loaded += OnLoaded;
            sv.Unloaded += OnUnloaded;
        }

        public void Detach()
        {
            StopAnimation();
            if (_scrollViewerRef.TryGetTarget(out var sv))
            {
                if (_scrollBarScrollHandler != null)
                {
                    sv.RemoveHandler(ScrollBar.ScrollEvent, _scrollBarScrollHandler);
                    _scrollBarScrollHandler = null;
                }

                sv.PreviewMouseWheel -= OnPreviewMouseWheel;
                sv.PreviewMouseDown -= OnPreviewMouseDown;
                sv.PreviewKeyDown -= OnPreviewKeyDown;
                sv.ScrollChanged -= OnScrollChanged;
                sv.Loaded -= OnLoaded;
                sv.Unloaded -= OnUnloaded;
            }
        }

        private void OnScrollBarScroll(object sender, ScrollEventArgs e)
        {
            // Immediate cancel of wheel animation whenever user directly interacts with the scrollbar
            StopAnimation();
            if (_scrollViewerRef.TryGetTarget(out var sv))
            {
                _startOffset = sv.VerticalOffset;
                _currentOffset = sv.VerticalOffset;
                _targetOffset = sv.VerticalOffset;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_scrollViewerRef.TryGetTarget(out var sv))
            {
                _startOffset = sv.VerticalOffset;
                _targetOffset = sv.VerticalOffset;
                _currentOffset = sv.VerticalOffset;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopAnimation();
        }

        private void StopAnimation()
        {
            if (_isAnimating)
            {
                _isAnimating = false;
                CompositionTarget.Rendering -= OnRendering;
            }
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isAnimating)
            {
                StopAnimation();
                if (_scrollViewerRef.TryGetTarget(out var sv))
                {
                    _startOffset = sv.VerticalOffset;
                    _targetOffset = sv.VerticalOffset;
                    _currentOffset = sv.VerticalOffset;
                }
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Up or Key.Down)
            {
                StopAnimation();
                if (_scrollViewerRef.TryGetTarget(out var sv))
                {
                    _startOffset = sv.VerticalOffset;
                    _targetOffset = sv.VerticalOffset;
                    _currentOffset = sv.VerticalOffset;
                }
            }
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!_scrollViewerRef.TryGetTarget(out var sv))
                return;

            // Content or viewport resized: clamp targets to new bounds
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
            {
                double max = sv.ScrollableHeight;
                _targetOffset = Math.Clamp(_targetOffset, 0, max);
                _currentOffset = Math.Clamp(_currentOffset, 0, max);
                _startOffset = Math.Clamp(_startOffset, 0, max);
            }

            // Sync tracked offsets when native scroll changes while not animating (e.g. keyboard navigation, code-behind)
            if (!_isAnimating && e.VerticalChange != 0)
            {
                _startOffset = sv.VerticalOffset;
                _currentOffset = sv.VerticalOffset;
                _targetOffset = sv.VerticalOffset;
            }
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0)
                return;

            if (!_scrollViewerRef.TryGetTarget(out var sv))
                return;

            // Allow eligible nested ScrollViewers to handle their own wheel events
            if (e.OriginalSource is DependencyObject source && IsNestedScrollViewerEligible(source, sv, e.Delta))
            {
                return;
            }

            double maxOffset = sv.ScrollableHeight;
            if (maxOffset <= 0)
                return;

            bool isScrollingDown = e.Delta < 0;
            int direction = isScrollingDown ? 1 : -1;

            double current = _isAnimating ? _currentOffset : sv.VerticalOffset;

            // Boundary guard: cleanly suppress if already resting at boundary and rolling into wall
            if (isScrollingDown && current >= maxOffset - 0.5 && _targetOffset >= maxOffset - 0.5)
            {
                e.Handled = true;
                return;
            }
            if (!isScrollingDown && current <= 0.5 && _targetOffset <= 0.5)
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;

            // Calculate step size based on system lines and multiplier (Chromium default standard: ~96-100px)
            double lines = SystemParameters.WheelScrollLines;
            if (lines <= 0) lines = 3.0;

            double multiplier = SmoothScrollViewerHelper.GetScrollDistanceMultiplier(sv);
            if (multiplier <= 0.05) multiplier = 1.0;

            double stepPerNotch = Math.Max(54.0, lines * 28.0) * multiplier;
            double notches = (double)e.Delta / 120.0;

            // Continuous momentum tracking
            long now = Stopwatch.GetTimestamp();
            double timeSinceLastWheel = _lastWheelTicks > 0
                ? (double)(now - _lastWheelTicks) / Stopwatch.Frequency
                : 1.0;
            _lastWheelTicks = now;

            // Direction change: reset momentum immediately for instantaneous turnaround
            if (direction != _lastWheelDirection)
            {
                _consecutiveWheelCount = 0;
                _lastWheelDirection = direction;
                _targetOffset = current;
            }
            else if (timeSinceLastWheel < 0.22) // Rapid consecutive wheel in the same direction
            {
                _consecutiveWheelCount = Math.Min(6, _consecutiveWheelCount + 1);
            }
            else
            {
                _consecutiveWheelCount = 0;
            }

            double momentum = 1.0 + (_consecutiveWheelCount * 0.16);
            double deltaOffset = -notches * stepPerNotch * momentum;

            _startOffset = current;
            _targetOffset = Math.Clamp((_isAnimating ? _targetOffset : current) + deltaOffset, 0, maxOffset);

            double totalDistance = Math.Abs(_targetOffset - _startOffset);
            if (totalDistance < 0.5)
            {
                _currentOffset = _targetOffset;
                sv.ScrollToVerticalOffset(_targetOffset);
                StopAnimation();
                return;
            }

            // Chromium / Discord duration scaling:
            // Base duration is snappy (~180-200ms).
            // When remaining distance to boundary is small, scale duration down proportionally
            // so it arrives at the wall briskly and never crawls at subpixel speeds!
            int baseMs = SmoothScrollViewerHelper.GetDurationMilliseconds(sv);
            double baseSec = Math.Max(0.12, baseMs / 1000.0);
            double typicalStep = Math.Max(48.0, stepPerNotch);

            _durationSec = totalDistance < typicalStep
                ? Math.Max(0.05, baseSec * (totalDistance / typicalStep))
                : baseSec;

            _startTimeTicks = now;
            StartAnimation();
        }

        private void StartAnimation()
        {
            if (!_isAnimating)
            {
                _isAnimating = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_isAnimating)
                return;

            if (!_scrollViewerRef.TryGetTarget(out var sv) || !sv.IsLoaded || !sv.IsVisible)
            {
                StopAnimation();
                return;
            }

            long now = Stopwatch.GetTimestamp();
            double elapsed = (double)(now - _startTimeTicks) / Stopwatch.Frequency;

            double maxOffset = sv.ScrollableHeight;
            if (maxOffset <= 0)
            {
                StopAnimation();
                return;
            }

            _targetOffset = Math.Clamp(_targetOffset, 0, maxOffset);

            // Completion check: exact time reached -> snap directly to target and stop cleanly
            if (elapsed >= _durationSec)
            {
                _currentOffset = _targetOffset;
                sv.ScrollToVerticalOffset(_targetOffset);
                StopAnimation();
                return;
            }

            // Chromium Cubic Ease-Out Polynomial:
            // p(t) = 1 - (1 - t)^3
            // Starts briskly, decelerates smoothly, guaranteed monotonic, zero overshoot.
            double t = Math.Clamp(elapsed / _durationSec, 0.0, 1.0);
            double progress = 1.0 - Math.Pow(1.0 - t, 3.0);

            double nextOffset = _startOffset + ((_targetOffset - _startOffset) * progress);

            // Boundary cutoff snap: if within 1px of boundary, complete immediately
            if (Math.Abs(nextOffset - _targetOffset) < 1.0)
            {
                _currentOffset = _targetOffset;
                sv.ScrollToVerticalOffset(_targetOffset);
                StopAnimation();
                return;
            }

            _currentOffset = nextOffset;
            sv.ScrollToVerticalOffset(nextOffset);
        }

        private static bool IsNestedScrollViewerEligible(DependencyObject source, ScrollViewer rootViewer, int delta)
        {
            DependencyObject? current = source;
            while (current != null && current != rootViewer)
            {
                if (current is ScrollViewer nested && nested != rootViewer && nested.IsVisible)
                {
                    if (nested.ScrollableHeight > 0)
                    {
                        if (delta < 0 && nested.VerticalOffset < nested.ScrollableHeight - 0.5)
                            return true;
                        if (delta > 0 && nested.VerticalOffset > 0.5)
                            return true;
                    }
                }

                if (current is Visual or System.Windows.Media.Media3D.Visual3D)
                {
                    current = VisualTreeHelper.GetParent(current);
                }
                else if (current is FrameworkContentElement fce)
                {
                    current = fce.Parent;
                }
                else
                {
                    break;
                }
            }
            return false;
        }
    }
}
