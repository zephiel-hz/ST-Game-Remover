using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SteamPluginManager.Views
{
    public partial class OperationProgressModalView : UserControl
    {
        private Action? _onPauseResume;
        private Action? _onCancel;
        private Action? _onClose;
        private bool _isPaused;

        public bool IsOpen { get; private set; }

        public OperationProgressModalView()
        {
            InitializeComponent();
        }

        public void Configure(
            string title,
            string step1,
            string step2,
            string step3,
            string step4,
            string headerGlyph = "\uE896",
            string? gameTitle = null,
            string? appId = null)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressTitle.Text = title;
                HeaderIcon.Text = headerGlyph;
                Step1Text.Text = step1;
                Step2Text.Text = step2;
                Step3Text.Text = step3;
                Step4Text.Text = step4;

                // Game Context Pill
                if (!string.IsNullOrWhiteSpace(gameTitle) || !string.IsNullOrWhiteSpace(appId))
                {
                    GameInfoPill.Visibility = Visibility.Visible;
                    GameTitleText.Text = string.IsNullOrWhiteSpace(gameTitle) ? "Steam Game" : gameTitle;
                    GameAppIdText.Text = string.IsNullOrWhiteSpace(appId) ? string.Empty : $"AppID: {appId}";
                    GameAppIdText.Visibility = string.IsNullOrWhiteSpace(appId) ? Visibility.Collapsed : Visibility.Visible;
                }
                else
                {
                    GameInfoPill.Visibility = Visibility.Collapsed;
                }

                // Reset state
                ResetModalState();
            });
        }

        public void SetGameContext(string? gameTitle, string? appId)
        {
            Dispatcher.Invoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(gameTitle) || !string.IsNullOrWhiteSpace(appId))
                {
                    GameInfoPill.Visibility = Visibility.Visible;
                    GameTitleText.Text = string.IsNullOrWhiteSpace(gameTitle) ? "Steam Game" : gameTitle;
                    GameAppIdText.Text = string.IsNullOrWhiteSpace(appId) ? string.Empty : $"AppID: {appId}";
                    GameAppIdText.Visibility = string.IsNullOrWhiteSpace(appId) ? Visibility.Collapsed : Visibility.Visible;
                }
                else
                {
                    GameInfoPill.Visibility = Visibility.Collapsed;
                }
            });
        }

        public void ResetModalState()
        {
            Dispatcher.Invoke(() =>
            {
                ResultNotification.Visibility = Visibility.Collapsed;
                ProgressDetail.Visibility = Visibility.Collapsed;
                ProgressDetail.Text = string.Empty;
                ProgressCard.Visibility = Visibility.Visible;
                ProgressBar.Visibility = Visibility.Visible;
                ProgressBar.IsIndeterminate = true;
                ProgressBar.Value = 0;
                ProgressPercentageText.Text = "0%";
                StepsBorder.Visibility = Visibility.Visible;
                StepsPanel.Visibility = Visibility.Visible;
                DismissButton.Visibility = Visibility.Collapsed;
                ControlButtonsPanel.Visibility = Visibility.Collapsed;
                CloseModalButton.Visibility = Visibility.Collapsed;

                ResetStepNodes();
            });
        }

        private void ResetStepNodes()
        {
            var buttonBg = FindResource("ButtonBackgroundBrush") as Brush ?? Brushes.DarkSlateGray;
            var borderBrush = FindResource("BorderBrush") as Brush ?? Brushes.Gray;
            var mutedBrush = FindResource("MutedForegroundBrush") as Brush ?? Brushes.Gray;

            Border[] circles = { Step1Circle, Step2Circle, Step3Circle, Step4Circle };
            TextBlock[] icons = { Step1Icon, Step2Icon, Step3Icon, Step4Icon };
            TextBlock[] labels = { Step1Text, Step2Text, Step3Text, Step4Text };
            Border[] conns = { StepConn1, StepConn2, StepConn3 };

            for (int i = 0; i < 4; i++)
            {
                if (circles[i] != null)
                {
                    circles[i].Background = buttonBg;
                    circles[i].BorderBrush = borderBrush;
                }
                if (icons[i] != null)
                {
                    icons[i].Text = (i + 1).ToString();
                    icons[i].Foreground = mutedBrush;
                }
                if (labels[i] != null)
                {
                    labels[i].Foreground = mutedBrush;
                    labels[i].FontWeight = FontWeights.SemiBold;
                }
            }

            for (int i = 0; i < 3; i++)
            {
                if (conns[i] != null)
                {
                    conns[i].Background = borderBrush;
                }
            }
        }

        public void ShowModal(string status = "Processing...", string detail = "")
        {
            Dispatcher.Invoke(() =>
            {
                IsOpen = true;
                ProgressStatus.Text = status;

                if (!string.IsNullOrWhiteSpace(detail))
                {
                    ProgressDetail.Text = detail;
                    ProgressDetail.Visibility = Visibility.Visible;
                }
                else
                {
                    ProgressDetail.Visibility = Visibility.Collapsed;
                }

                var mainShell = WindowNavigator.GetMainShell();
                if (mainShell != null)
                {
                    mainShell.ShowGlobalModal(this);
                }
                else
                {
                    this.Visibility = Visibility.Visible;
                }
            });
        }

        public void UpdateStatus(string status, string detail = "")
        {
            Dispatcher.Invoke(() =>
            {
                ProgressStatus.Text = status;
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    ProgressDetail.Text = detail;
                    ProgressDetail.Visibility = Visibility.Visible;
                }
                else
                {
                    ProgressDetail.Visibility = Visibility.Collapsed;
                }
            });
        }

        public void UpdateProgressStep(int stepNumber, bool completed = false)
        {
            Dispatcher.Invoke(() =>
            {
                var successBrush = FindResource("SuccessButtonBrush") as Brush ?? Brushes.MediumSeaGreen;
                var accentBrush = FindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
                var borderBrush = FindResource("BorderBrush") as Brush ?? Brushes.Gray;
                var buttonBg = FindResource("ButtonBackgroundBrush") as Brush ?? Brushes.DarkSlateGray;
                var mutedBrush = FindResource("MutedForegroundBrush") as Brush ?? Brushes.Gray;
                var foregroundBrush = FindResource("ForegroundBrush") as Brush ?? Brushes.White;

                Border[] circles = { Step1Circle, Step2Circle, Step3Circle, Step4Circle };
                TextBlock[] icons = { Step1Icon, Step2Icon, Step3Icon, Step4Icon };
                TextBlock[] labels = { Step1Text, Step2Text, Step3Text, Step4Text };
                Border[] conns = { StepConn1, StepConn2, StepConn3 };

                for (int i = 0; i < 4; i++)
                {
                    int step = i + 1;
                    if (step < stepNumber || (step == stepNumber && completed))
                    {
                        // Completed state
                        if (circles[i] != null)
                        {
                            circles[i].Background = successBrush;
                            circles[i].BorderBrush = successBrush;
                        }
                        if (icons[i] != null)
                        {
                            icons[i].Text = "\uE73E"; // Checkmark
                            icons[i].Foreground = Brushes.White;
                        }
                        if (labels[i] != null)
                        {
                            labels[i].Foreground = successBrush;
                            labels[i].FontWeight = FontWeights.Bold;
                        }
                    }
                    else if (step == stepNumber)
                    {
                        // Active running state
                        if (circles[i] != null)
                        {
                            circles[i].Background = accentBrush;
                            circles[i].BorderBrush = accentBrush;
                        }
                        if (icons[i] != null)
                        {
                            icons[i].Text = step.ToString();
                            icons[i].Foreground = Brushes.White;
                        }
                        if (labels[i] != null)
                        {
                            labels[i].Foreground = foregroundBrush;
                            labels[i].FontWeight = FontWeights.Bold;
                        }
                    }
                    else
                    {
                        // Pending future state
                        if (circles[i] != null)
                        {
                            circles[i].Background = buttonBg;
                            circles[i].BorderBrush = borderBrush;
                        }
                        if (icons[i] != null)
                        {
                            icons[i].Text = step.ToString();
                            icons[i].Foreground = mutedBrush;
                        }
                        if (labels[i] != null)
                        {
                            labels[i].Foreground = mutedBrush;
                            labels[i].FontWeight = FontWeights.SemiBold;
                        }
                    }
                }

                // Update connectors
                for (int i = 0; i < 3; i++)
                {
                    if (conns[i] != null)
                    {
                        if (i + 1 < stepNumber)
                        {
                            conns[i].Background = successBrush;
                        }
                        else if (i + 1 == stepNumber && completed)
                        {
                            conns[i].Background = successBrush;
                        }
                        else
                        {
                            conns[i].Background = borderBrush;
                        }
                    }
                }
            });
        }

        public void SetProgress(double value, double maximum = 100, bool isIndeterminate = false)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressBar.IsIndeterminate = isIndeterminate;
                ProgressBar.Maximum = maximum;
                ProgressBar.Value = Math.Max(0, Math.Min(value, maximum));
                ProgressBar.Visibility = Visibility.Visible;

                if (isIndeterminate)
                {
                    ProgressPercentageText.Text = "...";
                }
                else
                {
                    double pct = maximum > 0 ? (value / maximum) * 100 : 0;
                    ProgressPercentageText.Text = $"{Math.Round(pct)}%";
                }
            });
        }

        public void ShowResultNotification(bool isSuccess, string message, int autoCloseMs = 0, Action? onClose = null)
        {
            Dispatcher.Invoke(async () =>
            {
                _onClose = onClose;

                ProgressCard.Visibility = Visibility.Collapsed;
                StepsBorder.Visibility = Visibility.Collapsed;
                DismissButton.Visibility = Visibility.Visible;

                ResultNotification.Visibility = Visibility.Visible;
                var successBrush = FindResource("SuccessButtonBrush") as Brush ?? Brushes.MediumSeaGreen;
                var dangerBrush = FindResource("DangerButtonBrush") as Brush ?? Brushes.Crimson;

                ResultNotification.BorderBrush = isSuccess ? successBrush : dangerBrush;
                ResultIcon.Text = isSuccess ? "\uE73E" : "\uE711";
                ResultIcon.Foreground = isSuccess ? successBrush : dangerBrush;

                ResultTitle.Text = isSuccess ? "Operation Completed Successfully" : "Operation Failed";
                ResultMessage.Text = message;

                // Adjust buttons
                PauseResumeButton.Visibility = Visibility.Collapsed;
                CancelDownloadButton.Visibility = Visibility.Collapsed;
                CloseModalButton.Visibility = Visibility.Visible;
                ControlButtonsPanel.Visibility = Visibility.Visible;

                if (autoCloseMs > 0)
                {
                    await Task.Delay(autoCloseMs);
                    if (IsOpen)
                    {
                        HideModal();
                        _onClose?.Invoke();
                    }
                }
            });
        }

        public void SetControlButtons(bool show, Action? onPauseResume = null, Action? onCancel = null, bool isPaused = false)
        {
            Dispatcher.Invoke(() =>
            {
                _onPauseResume = onPauseResume;
                _onCancel = onCancel;
                _isPaused = isPaused;

                ControlButtonsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                PauseResumeButton.Visibility = onPauseResume != null ? Visibility.Visible : Visibility.Collapsed;
                CancelDownloadButton.Visibility = onCancel != null ? Visibility.Visible : Visibility.Collapsed;
                CloseModalButton.Visibility = Visibility.Collapsed;

                UpdatePauseResumeButtonState();
            });
        }

        public void UpdatePauseState(bool isPaused)
        {
            Dispatcher.Invoke(() =>
            {
                _isPaused = isPaused;
                UpdatePauseResumeButtonState();
            });
        }

        private void UpdatePauseResumeButtonState()
        {
            if (_isPaused)
            {
                PauseResumeIcon.Text = "\uE768"; // Play / Resume glyph
                PauseResumeText.Text = "Resume";
            }
            else
            {
                PauseResumeIcon.Text = "\uE769"; // Pause glyph
                PauseResumeText.Text = "Pause";
            }
        }

        public void HideModal()
        {
            Dispatcher.Invoke(() =>
            {
                IsOpen = false;
                var mainShell = WindowNavigator.GetMainShell();
                if (mainShell != null)
                {
                    mainShell.HideGlobalModal();
                }
                else
                {
                    this.Visibility = Visibility.Collapsed;
                }
            });
        }

        private void PauseResume_Click(object sender, RoutedEventArgs e)
        {
            _onPauseResume?.Invoke();
        }

        private void CancelDownload_Click(object sender, RoutedEventArgs e)
        {
            _onCancel?.Invoke();
        }

        private void CloseModal_Click(object sender, RoutedEventArgs e)
        {
            HideModal();
            _onClose?.Invoke();
        }

        private void DismissButton_Click(object sender, RoutedEventArgs e)
        {
            HideModal();
            _onClose?.Invoke();
        }
    }
}
