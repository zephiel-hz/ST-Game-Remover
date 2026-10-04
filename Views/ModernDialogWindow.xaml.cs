using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SteamPluginManager.Views
{
    public partial class ModernDialogWindow : Window
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;
        private MessageBoxButton _buttons = MessageBoxButton.OK;

        public ModernDialogWindow()
        {
            InitializeComponent();
        }

        public void Configure(
            string message,
            string caption,
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            _buttons = buttons;
            Title = caption ?? "Notification";
            TitleTextBlock.Text = caption ?? "Notification";
            MessageTextBlock.Text = message ?? string.Empty;

            ConfigureIcon(icon, caption, message);
            ConfigureButtons(buttons, caption, message);
        }

        private void ConfigureIcon(MessageBoxImage icon, string? caption, string? message)
        {
            bool isDestructive = IsDestructiveAction(caption, message);

            switch (icon)
            {
                case MessageBoxImage.Error: // or Hand, Stop
                    SetIconVisual("\uE711", "#EF4444", "#20EF4444", "#40EF4444");
                    break;

                case MessageBoxImage.Warning: // or Exclamation
                    SetIconVisual("\uE7BA", "#F59E0B", "#20F59E0B", "#40F59E0B");
                    break;

                case MessageBoxImage.Question:
                    if (isDestructive)
                    {
                        SetIconVisual("\uE74D", "#EF4444", "#20EF4444", "#40EF4444"); // Trash bin
                    }
                    else
                    {
                        SetIconVisual("\uE897", "#818CF8", "#206366F1", "#406366F1"); // Help question
                    }
                    break;

                case MessageBoxImage.Information: // or Asterisk
                    if (caption != null && (caption.Contains("Success", StringComparison.OrdinalIgnoreCase) ||
                                           caption.Contains("Complete", StringComparison.OrdinalIgnoreCase)))
                    {
                        SetIconVisual("\uE73E", "#10B981", "#2010B981", "#4010B981"); // Success checkmark
                    }
                    else
                    {
                        SetIconVisual("\uE946", "#38BDF8", "#200284C7", "#400284C7"); // Information
                    }
                    break;

                default:
                    if (isDestructive)
                    {
                        SetIconVisual("\uE74D", "#EF4444", "#20EF4444", "#40EF4444");
                    }
                    else
                    {
                        SetIconVisual("\uE946", "#38BDF8", "#200284C7", "#400284C7");
                    }
                    break;
            }
        }

        private void SetIconVisual(string glyph, string foregroundHex, string bgHex, string borderHex)
        {
            try
            {
                IconGlyphText.Text = glyph;
                IconGlyphText.Foreground = (Brush)new BrushConverter().ConvertFromString(foregroundHex)!;
                IconBadgeBorder.Background = (Brush)new BrushConverter().ConvertFromString(bgHex)!;
                IconBadgeBorder.BorderBrush = (Brush)new BrushConverter().ConvertFromString(borderHex)!;
            }
            catch { }
        }

        private void ConfigureButtons(MessageBoxButton buttons, string? caption, string? message)
        {
            bool isDestructive = IsDestructiveAction(caption, message);

            CancelButton.Visibility = Visibility.Collapsed;
            NoButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Visibility = Visibility.Visible;

            switch (buttons)
            {
                case MessageBoxButton.OK:
                    PrimaryButton.Content = "OK";
                    ApplyButtonStyle(PrimaryButton, isDestructive ? "DangerDialogButton" : "PrimaryDialogButton");
                    break;

                case MessageBoxButton.OKCancel:
                    PrimaryButton.Content = "OK";
                    ApplyButtonStyle(PrimaryButton, isDestructive ? "DangerDialogButton" : "PrimaryDialogButton");
                    CancelButton.Content = "Cancel";
                    CancelButton.Visibility = Visibility.Visible;
                    break;

                case MessageBoxButton.YesNo:
                    PrimaryButton.Content = "Yes";
                    ApplyButtonStyle(PrimaryButton, isDestructive ? "DangerDialogButton" : "PrimaryDialogButton");
                    NoButton.Content = "No";
                    NoButton.Visibility = Visibility.Visible;
                    break;

                case MessageBoxButton.YesNoCancel:
                    PrimaryButton.Content = "Yes";
                    ApplyButtonStyle(PrimaryButton, isDestructive ? "DangerDialogButton" : "PrimaryDialogButton");
                    NoButton.Content = "No";
                    NoButton.Visibility = Visibility.Visible;
                    CancelButton.Content = "Cancel";
                    CancelButton.Visibility = Visibility.Visible;
                    break;
            }

            PrimaryButton.Focus();
        }

        private void ApplyButtonStyle(Button button, string styleKey)
        {
            if (TryFindResource(styleKey) is Style style)
            {
                button.Style = style;
            }
        }

        private static bool IsDestructiveAction(string? caption, string? message)
        {
            string combined = $"{(caption ?? "")} {(message ?? "")}";
            return combined.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
                   combined.Contains("remove", StringComparison.OrdinalIgnoreCase) ||
                   combined.Contains("clear", StringComparison.OrdinalIgnoreCase) ||
                   combined.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
                   combined.Contains("reset", StringComparison.OrdinalIgnoreCase);
        }

        private void PrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            Result = (_buttons == MessageBoxButton.OK || _buttons == MessageBoxButton.OKCancel)
                ? MessageBoxResult.OK
                : MessageBoxResult.Yes;
            Close();
        }

        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.No;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.Cancel;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_buttons == MessageBoxButton.YesNo)
                Result = MessageBoxResult.No;
            else if (_buttons == MessageBoxButton.OK)
                Result = MessageBoxResult.OK;
            else
                Result = MessageBoxResult.Cancel;

            Close();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                PrimaryButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }
}
