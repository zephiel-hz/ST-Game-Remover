using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SteamPluginManager.Views
{
    public partial class SplashWindow : Window
    {
        private readonly DispatcherTimer _progressTimer;
        private readonly Stopwatch _progressStopwatch = new();
        private const double ProgressRampDurationMs = 1600;

        public SplashWindow()
        {
            InitializeComponent();
            _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, ProgressTimer_Tick, Dispatcher);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (Resources["IntroStoryboard"] is Storyboard storyboard)
            {
                storyboard.Begin(this, true);
            }

            StartProgressAnimation();
        }

        private void StartProgressAnimation()
        {
            SplashProgress.Value = 0;
            _progressStopwatch.Restart();
            _progressTimer.Start();
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            double elapsedMs = _progressStopwatch.Elapsed.TotalMilliseconds;
            double targetValue = Math.Min(99, 99 * elapsedMs / ProgressRampDurationMs);
            SplashProgress.Value = targetValue;

            if (targetValue >= 99)
            {
                _progressTimer.Stop();
                _progressStopwatch.Stop();
            }
        }

        public async Task FadeOutAsync()
        {
            _progressTimer.Stop();
            _progressStopwatch.Stop();
            SplashProgress.Value = 100;

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
            fadeOut.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            await Dispatcher.InvokeAsync(() =>
            {
                BeginAnimation(OpacityProperty, fadeOut);
            });

            await Task.Delay(120);
            Close();
        }
    }
}
