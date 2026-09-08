using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Allens.Helpers
{
    public static class SmoothScrollBehavior
    {
        public static readonly DependencyProperty IsSmoothScrollEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsSmoothScrollEnabled",
                typeof(bool),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(false, OnIsSmoothScrollEnabledChanged));

        public static bool GetIsSmoothScrollEnabled(DependencyObject obj) => (bool)obj.GetValue(IsSmoothScrollEnabledProperty);
        public static void SetIsSmoothScrollEnabled(DependencyObject obj, bool value) => obj.SetValue(IsSmoothScrollEnabledProperty, value);

        private static readonly DependencyProperty InstanceProperty =
            DependencyProperty.RegisterAttached(
                "Instance",
                typeof(SmoothScrollInstance),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(null));

        private static void OnIsSmoothScrollEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer sv) return;

            var oldInstance = (SmoothScrollInstance?)sv.GetValue(InstanceProperty);
            oldInstance?.Detach();

            if ((bool)e.NewValue)
            {
                var instance = new SmoothScrollInstance(sv);
                sv.SetValue(InstanceProperty, instance);
            }
            else
            {
                sv.ClearValue(InstanceProperty);
            }
        }
    }

    internal sealed class SmoothScrollInstance
    {
        private readonly ScrollViewer _sv;
        private double _targetOffset;
        private bool _isRendering;
        private long _lastWheelTime;
        private int _lastDirection;
        private readonly Stopwatch _stopwatch = new();

        public SmoothScrollInstance(ScrollViewer sv)
        {
            _sv = sv;
            _sv.CanContentScroll = false;
            _targetOffset = _sv.VerticalOffset;

            _sv.PreviewMouseWheel += OnPreviewMouseWheel;
            _sv.PreviewMouseDown += OnPreviewMouseDown;
            _sv.PreviewKeyDown += OnPreviewKeyDown;
            _sv.ScrollChanged += OnScrollChanged;
        }

        public void Detach()
        {
            StopAnimation();
            _sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            _sv.PreviewMouseDown -= OnPreviewMouseDown;
            _sv.PreviewKeyDown -= OnPreviewKeyDown;
            _sv.ScrollChanged -= OnScrollChanged;
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            StopAnimation();
            _targetOffset = _sv.VerticalOffset;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space)
            {
                StopAnimation();
                _targetOffset = _sv.VerticalOffset;
            }
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!_isRendering)
            {
                _targetOffset = _sv.VerticalOffset;
            }
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_sv.ScrollableHeight <= 0) return;

            e.Handled = true;

            long now = Environment.TickCount64;
            long timeSinceLast = now - _lastWheelTime;
            _lastWheelTime = now;

            int currentDirection = Math.Sign(-e.Delta); // 1 = scroll down, -1 = scroll up

            // If user reversed direction, paused > 250ms, or was not animating, anchor to current physical offset
            if (currentDirection != _lastDirection || timeSinceLast > 250 || !_isRendering)
            {
                _targetOffset = _sv.VerticalOffset;
            }
            _lastDirection = currentDirection;

            // Adaptive velocity multiplier when spinning quickly
            double multiplier = 1.0;
            if (timeSinceLast < 45)
            {
                multiplier = 1.55;
            }
            else if (timeSinceLast < 90)
            {
                multiplier = 1.3;
            }
            else if (timeSinceLast < 150)
            {
                multiplier = 1.15;
            }

            // Natural wheel step: 125px per 120 units of wheel delta
            double step = (Math.Abs(e.Delta) / 120.0) * 125.0 * multiplier;
            _targetOffset += currentDirection * step;

            // Clamp target within valid scrollable range
            _targetOffset = Math.Clamp(_targetOffset, 0.0, _sv.ScrollableHeight);

            StartAnimation();
        }

        private void StartAnimation()
        {
            if (!_isRendering)
            {
                _isRendering = true;
                _stopwatch.Restart();
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private void StopAnimation()
        {
            if (_isRendering)
            {
                _isRendering = false;
                CompositionTarget.Rendering -= OnRendering;
                _stopwatch.Reset();
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_isRendering) return;

            double current = _sv.VerticalOffset;
            double distance = _targetOffset - current;

            // When distance is negligible (< 0.5px), finalize and stop
            if (Math.Abs(distance) < 0.5)
            {
                _sv.ScrollToVerticalOffset(_targetOffset);
                StopAnimation();
                return;
            }

            // Frame-rate independent exponential decay / dampening:
            double dt = _stopwatch.Elapsed.TotalSeconds;
            _stopwatch.Restart();

            if (dt > 0.05) dt = 0.016; // guard against large spikes

            // Decay factor: 19.0 gives a perfectly fluid, snappy and buttery smooth glide without sluggish lag
            double factor = 1.0 - Math.Exp(-19.0 * dt);
            double newOffset = current + (distance * factor);

            _sv.ScrollToVerticalOffset(newOffset);
        }
    }
}
