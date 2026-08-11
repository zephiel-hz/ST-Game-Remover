using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Animation;

namespace SteamPluginManager.Views
{
    public partial class SteamtoolsOverlayWindow : Window
    {
        private DispatcherTimer? _autoHideTimer;

        public SteamtoolsOverlayWindow()
        {
            InitializeComponent();
        }

        public void SetTitle(string text)
        {
            SteamtoolsOverlayText.Text = text;
        }

        public void SetStatus(string text)
        {
            SteamtoolsOverlayStatus.Text = text;
        }

        public void SetStepComplete(int step)
        {
            // Stop animation first
            StopStepAnimation(step);

            // change icon to check glyph and color
            string glyph = "\uE73E"; // check
            switch (step)
            {
                case 1:
                    SteamtoolsStep1Icon.Text = glyph;
                    SteamtoolsStep1Icon.Foreground = (Brush)FindResource("SuccessButtonBrush");
                    break;
                case 2:
                    SteamtoolsStep2Icon.Text = glyph;
                    SteamtoolsStep2Icon.Foreground = (Brush)FindResource("SuccessButtonBrush");
                    break;
                case 3:
                    SteamtoolsStep3Icon.Text = glyph;
                    SteamtoolsStep3Icon.Foreground = (Brush)FindResource("SuccessButtonBrush");
                    break;
                case 4:
                    SteamtoolsStep4Icon.Text = glyph;
                    SteamtoolsStep4Icon.Foreground = (Brush)FindResource("SuccessButtonBrush");
                    break;
            }
        }

        public void SetStepText(int step, string text)
        {
            switch (step)
            {
                case 1:
                    SteamtoolsStep1Text.Text = text;
                    break;
                case 2:
                    SteamtoolsStep2Text.Text = text;
                    break;
                case 3:
                    SteamtoolsStep3Text.Text = text;
                    break;
                case 4:
                    SteamtoolsStep4Text.Text = text;
                    break;
            }
        }

        public void ResetSteps()
        {
            string defaultGlyph = "\uE7BA"; // warning glyph
            SteamtoolsStep1Icon.Text = defaultGlyph;
            SteamtoolsStep2Icon.Text = defaultGlyph;
            SteamtoolsStep3Icon.Text = defaultGlyph;
            SteamtoolsStep4Icon.Text = defaultGlyph;

            SteamtoolsStep1Icon.Foreground = (Brush)FindResource("MutedForegroundBrush");
            SteamtoolsStep2Icon.Foreground = (Brush)FindResource("MutedForegroundBrush");
            SteamtoolsStep3Icon.Foreground = (Brush)FindResource("MutedForegroundBrush");
            SteamtoolsStep4Icon.Foreground = (Brush)FindResource("MutedForegroundBrush");
        }

        public void ShowResult(string message, bool success = true)
        {
            SteamtoolsResultMessage.Text = message;
            SteamtoolsResultNotification.Visibility = Visibility.Visible;
            SteamtoolsResultIcon.Foreground = success ? (Brush)FindResource("SuccessButtonBrush") : (Brush)FindResource("ErrorButtonBrush");

            // Start auto-hide timer: close window after 3 seconds
            StartAutoHideTimer();
        }

        private void StartAutoHideTimer()
        {
            if (_autoHideTimer != null)
                _autoHideTimer.Stop();

            _autoHideTimer = new DispatcherTimer
            {
                Interval = System.TimeSpan.FromSeconds(3)
            };
            _autoHideTimer.Tick += (s, e) =>
            {
                _autoHideTimer.Stop();
                this.Close();
            };
            _autoHideTimer.Start();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                this.DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        public void SetStepInProgress(int step)
        {
            string loadingGlyph = "\uEA3F"; // redo/sync icon for loading
            TextBlock stepIcon = GetStepIcon(step);
            if (stepIcon == null) return;

            // Set to loading glyph and muted color
            stepIcon.Text = loadingGlyph;
            stepIcon.Foreground = (Brush)FindResource("MutedForegroundBrush");

            // Get and ensure we have the RotateTransform
            var rotateTransform = stepIcon.RenderTransform as RotateTransform;
            if (rotateTransform == null)
            {
                rotateTransform = new RotateTransform();
                stepIcon.RenderTransform = rotateTransform;
            }

            // Stop any existing animation first
            rotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);

            // Start new rotation animation
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = new System.TimeSpan(0, 0, 0, 1, 200),
                RepeatBehavior = RepeatBehavior.Forever
            };
            rotateTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
        }

        private void StopStepAnimation(int step)
        {
            TextBlock stepIcon = GetStepIcon(step);
            if (stepIcon == null) return;

            var rotateTransform = stepIcon.RenderTransform as RotateTransform;
            if (rotateTransform != null)
            {
                rotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
                rotateTransform.Angle = 0;
            }
        }

        private TextBlock GetStepIcon(int step)
        {
            return step switch
            {
                1 => SteamtoolsStep1Icon,
                2 => SteamtoolsStep2Icon,
                3 => SteamtoolsStep3Icon,
                4 => SteamtoolsStep4Icon,
                _ => null
            };
        }
    }
}
