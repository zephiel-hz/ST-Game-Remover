using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SteamPluginManager
{
    public partial class StyledMessageDialog : Window
    {
        public bool IsConfirmed { get; private set; } = false;

        public StyledMessageDialog(string title, string message, bool showCancel = false)
        {
            InitializeComponent();
            DialogTitle.Text = title;
            
            if (showCancel)
            {
                SecondaryButton.Visibility = Visibility.Visible;
            }

            // Check if this is an "Update Available" message with version comparison
            if (title == "Update Available" && (message.Contains("Current:") || message.Contains("Latest:")))
            {
                if (DialogIconBadge != null)
                {
                    DialogIconBadge.Visibility = Visibility.Visible;
                    DialogIconText.Text = "\uE896"; // Download/update icon
                }
                RenderUpdateLayout(message);
            }
            else if (IsMarkdownContent(message))
            {
                MessageText.Visibility = Visibility.Collapsed;
                RichContentHost.Visibility = Visibility.Visible;
                RichContentHost.Content = GitHubMarkdownParser.ParseToUIElement(message);
            }
            else
            {
                MessageText.Text = message;
            }
        }

        private static bool IsMarkdownContent(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            // Simple heuristic for Markdown content
            return text.Contains("### ") || text.Contains("## ") || text.Contains("# ") ||
                   text.Contains("* **") || text.Contains("- **") ||
                   text.Contains("```") || (text.Contains("**") && text.Contains("\n"));
        }

        private void RenderUpdateLayout(string rawMessage)
        {
            MessageText.Visibility = Visibility.Collapsed;
            RichContentHost.Visibility = Visibility.Visible;

            // Parse version numbers
            string currentVer = "1.0.0";
            string latestVer = "Latest";

            var curMatch = Regex.Match(rawMessage, @"Current:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            if (curMatch.Success) currentVer = curMatch.Groups[1].Value.Trim().TrimStart('v', 'V');

            var latMatch = Regex.Match(rawMessage, @"Latest:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            if (latMatch.Success) latestVer = latMatch.Groups[1].Value.Trim().TrimStart('v', 'V');

            // Extract changelog
            string changelog = rawMessage;

            // Strip "New Version Available!", "Current: ...", "Latest: ..."
            changelog = Regex.Replace(changelog, @"^.*?Latest:\s*[^\r\n]+", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();

            // Strip trailing "Download and install the latest version?" or similar questions
            changelog = Regex.Replace(changelog, @"\n+ *(Download and install|Would you like to download).*?$", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();

            // Build Rich Update Layout
            var rootPanel = new StackPanel { Margin = new Thickness(0) };

            // 1. Version Comparison Hero Card
            var heroBorder = new Border
            {
                Background = GetResourceBrush("BackgroundBrush", "#0F172A"),
                BorderBrush = GetResourceBrush("BorderBrush", "#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 14)
            };

            var heroGrid = new Grid();
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left: Current Version
            var curPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            curPanel.Children.Add(new TextBlock
            {
                Text = "CURRENT VERSION",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = GetResourceBrush("MutedForegroundBrush", "#94A3B8"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            var curPill = new Border
            {
                Background = GetResourceBrush("ButtonBackgroundBrush", "#1E293B"),
                BorderBrush = GetResourceBrush("BorderBrush", "#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 4, 0, 0)
            };
            curPill.Child = new TextBlock
            {
                Text = $"v{currentVer}",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetResourceBrush("MutedForegroundBrush", "#94A3B8")
            };
            curPanel.Children.Add(curPill);
            Grid.SetColumn(curPanel, 0);
            heroGrid.Children.Add(curPanel);

            // Center: Glowing arrow
            var arrowBlock = new TextBlock
            {
                Text = "➜",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = GetResourceBrush("AccentBrush", "#3B82F6"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(arrowBlock, 1);
            heroGrid.Children.Add(arrowBlock);

            // Right: Latest Version
            var latPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            latPanel.Children.Add(new TextBlock
            {
                Text = "LATEST RELEASE",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = GetResourceBrush("AccentBrush", "#3B82F6"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            var latPill = new Border
            {
                Background = GetResourceBrush("ButtonBackgroundBrush", "#1E293B"),
                BorderBrush = GetResourceBrush("AccentBrush", "#3B82F6"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 4, 0, 0)
            };
            var latInnerPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            latInnerPanel.Children.Add(new TextBlock
            {
                Text = $"v{latestVer}",
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = GetResourceBrush("ForegroundBrush", "#F8FAFC")
            });
            var newBadge = new Border
            {
                Background = GetResourceBrush("AccentBrush", "#3B82F6"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(6, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = "NEW",
                    FontSize = 8.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White
                }
            };
            latInnerPanel.Children.Add(newBadge);
            latPill.Child = latInnerPanel;
            latPanel.Children.Add(latPill);
            Grid.SetColumn(latPanel, 2);
            heroGrid.Children.Add(latPanel);

            heroBorder.Child = heroGrid;
            rootPanel.Children.Add(heroBorder);

            // 2. Changelog Header Bar
            var changelogHeader = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            var headerLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            headerLeft.Children.Add(new TextBlock
            {
                Text = "\uE8A5", // Document icon
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = GetResourceBrush("AccentBrush", "#3B82F6"),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            headerLeft.Children.Add(new TextBlock
            {
                Text = "Release Notes & Changelog",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetResourceBrush("ForegroundBrush", "#F8FAFC"),
                VerticalAlignment = VerticalAlignment.Center
            });
            changelogHeader.Children.Add(headerLeft);

            var headerRight = new TextBlock
            {
                Text = "GitHub Release",
                FontSize = 10.5,
                Foreground = GetResourceBrush("MutedForegroundBrush", "#94A3B8"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            changelogHeader.Children.Add(headerRight);
            rootPanel.Children.Add(changelogHeader);

            // 3. Changelog Markdown Box
            var changelogBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(11, 15, 25)), // #0B0F19 deep contrast background
                BorderBrush = GetResourceBrush("BorderBrush", "#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 260,
                Padding = new Thickness(0, 0, 6, 0),
                Content = GitHubMarkdownParser.ParseToUIElement(changelog)
            };
            changelogBox.Child = scroller;
            rootPanel.Children.Add(changelogBox);

            // 4. Download Prompt Callout
            var promptRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 0) };
            promptRow.Children.Add(new TextBlock
            {
                Text = "\uE946", // Info icon
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12.5,
                Foreground = GetResourceBrush("AccentBrush", "#3B82F6"),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            promptRow.Children.Add(new TextBlock
            {
                Text = "Download and install update now? The application will update and restart automatically.",
                FontSize = 11.5,
                Foreground = GetResourceBrush("MutedForegroundBrush", "#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center
            });
            rootPanel.Children.Add(promptRow);

            RichContentHost.Content = rootPanel;
        }

        private static Brush GetResourceBrush(string resourceKey, string fallbackHex)
        {
            try
            {
                if (Application.Current?.Resources[resourceKey] is Brush brush)
                    return brush;
            }
            catch { }

            try
            {
                return (SolidColorBrush)new BrushConverter().ConvertFrom(fallbackHex)!;
            }
            catch
            {
                return Brushes.White;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                this.DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.IsConfirmed = false;
            this.DialogResult = false;
            this.Close();
        }

        private void PrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            this.IsConfirmed = true;
            this.DialogResult = true;
            this.Close();
        }

        private void SecondaryButton_Click(object sender, RoutedEventArgs e)
        {
            this.IsConfirmed = false;
            this.DialogResult = false;
            this.Close();
        }
    }
}
