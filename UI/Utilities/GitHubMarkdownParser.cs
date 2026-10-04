using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SteamPluginManager
{
    /// <summary>
    /// Lightweight, theme-aware Markdown parser that renders GitHub-style changelog formatting in WPF.
    /// Supports headers, bullet lists with bold feature prefixes, inline code pills, bold, italic,
    /// blockquotes, dividers, and URLs.
    /// </summary>
    public static class GitHubMarkdownParser
    {
        private static readonly Regex BoldRegex = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled);
        private static readonly Regex ItalicRegex = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)|(?<!_)_(?!_)(.+?)(?<!_)_(?!_)", RegexOptions.Compiled);
        private static readonly Regex CodeRegex = new(@"`([^`]+)`", RegexOptions.Compiled);
        private static readonly Regex LinkRegex = new(@"\[([^\]]+)\]\((https?://[^\)]+)\)", RegexOptions.Compiled);
        private static readonly Regex UrlRegex = new(@"(https?://[^\s\)]+)", RegexOptions.Compiled);

        public static UIElement ParseToUIElement(string markdown)
        {
            var container = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0)
            };

            if (string.IsNullOrWhiteSpace(markdown))
            {
                container.Children.Add(new TextBlock
                {
                    Text = "No release notes available.",
                    Foreground = GetResourceBrush("MutedForegroundBrush", "#94A3B8"),
                    FontStyle = FontStyles.Italic,
                    FontSize = 12
                });
                return container;
            }

            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            bool inCodeBlock = false;
            var codeBlockLines = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];
                var trimmedLine = rawLine.Trim();

                // Fenced Code Block
                if (trimmedLine.StartsWith("```"))
                {
                    if (inCodeBlock)
                    {
                        container.Children.Add(CreateCodeBlock(string.Join("\n", codeBlockLines)));
                        codeBlockLines.Clear();
                        inCodeBlock = false;
                    }
                    else
                    {
                        inCodeBlock = true;
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    codeBlockLines.Add(rawLine);
                    continue;
                }

                // Horizontal Rule
                if (trimmedLine == "---" || trimmedLine == "***" || trimmedLine == "___")
                {
                    container.Children.Add(CreateDivider());
                    continue;
                }

                // Empty line
                if (string.IsNullOrWhiteSpace(trimmedLine))
                {
                    container.Children.Add(new FrameworkElement { Height = 6 });
                    continue;
                }

                // Headers (#, ##, ###, ####)
                if (trimmedLine.StartsWith("#"))
                {
                    container.Children.Add(CreateHeader(trimmedLine));
                    continue;
                }

                // Blockquote (> text)
                if (trimmedLine.StartsWith(">"))
                {
                    var quoteText = trimmedLine.Substring(1).Trim();
                    container.Children.Add(CreateBlockquote(quoteText));
                    continue;
                }

                // Bullet List (*, -, +)
                if (Regex.IsMatch(trimmedLine, @"^(\*|-|\+|\d+\.)\s+"))
                {
                    var match = Regex.Match(trimmedLine, @"^(\*|-|\+|\d+\.)\s+(.*)$");
                    if (match.Success)
                    {
                        var bulletMarker = match.Groups[1].Value;
                        var content = match.Groups[2].Value;
                        container.Children.Add(CreateBulletItem(bulletMarker, content));
                        continue;
                    }
                }

                // Normal Paragraph
                container.Children.Add(CreateParagraph(trimmedLine));
            }

            if (inCodeBlock && codeBlockLines.Count > 0)
            {
                container.Children.Add(CreateCodeBlock(string.Join("\n", codeBlockLines)));
            }

            return container;
        }

        private static UIElement CreateHeader(string line)
        {
            int level = 0;
            while (level < line.Length && line[level] == '#')
                level++;

            var text = line.Substring(level).Trim();

            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, 4)
            };

            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Badge / Icon detection for common GitHub changelog sections
            string? badgeText = null;
            SolidColorBrush? badgeBg = null;
            SolidColorBrush? badgeFg = null;
            string? iconText = null;

            string lower = text.ToLowerInvariant();
            if (lower.Contains("bug") || lower.Contains("fix"))
            {
                iconText = "\uEBE8"; // Bug icon in MDL2
                badgeText = "FIX";
                badgeBg = new SolidColorBrush(Color.FromArgb(35, 239, 68, 68)); // #EF4444 red
                badgeFg = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            }
            else if (lower.Contains("feature") || lower.Contains("new") || lower.Contains("added"))
            {
                iconText = "\uE735"; // Sparkle/star in MDL2
                badgeText = "NEW";
                badgeBg = new SolidColorBrush(Color.FromArgb(35, 16, 185, 129)); // #10B981 green
                badgeFg = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            }
            else if (lower.Contains("improve") || lower.Contains("update") || lower.Contains("changed"))
            {
                iconText = "\uE777"; // Arrow/sync
                badgeText = "IMPROVED";
                badgeBg = new SolidColorBrush(Color.FromArgb(35, 59, 130, 246)); // #3B82F6 blue
                badgeFg = new SolidColorBrush(Color.FromRgb(96, 165, 250));
            }
            else if (lower.Contains("security"))
            {
                iconText = "\uE72E"; // Lock/shield
                badgeText = "SECURITY";
                badgeBg = new SolidColorBrush(Color.FromArgb(35, 168, 85, 247)); // #A855F7 purple
                badgeFg = new SolidColorBrush(Color.FromRgb(192, 132, 252));
            }
            else if (lower.Contains("changelog") || lower.Contains("what's changed") || lower.Contains("release notes"))
            {
                iconText = "\uE8A5"; // Document/list
            }

            if (!string.IsNullOrEmpty(iconText))
            {
                titleRow.Children.Add(new TextBlock
                {
                    Text = iconText,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = level == 1 ? 16 : (level == 2 ? 14 : 12.5),
                    Foreground = GetResourceBrush("AccentBrush", "#3B82F6"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 7, 0)
                });
            }

            var textBlock = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                FontSize = level == 1 ? 16 : (level == 2 ? 14.5 : (level == 3 ? 13 : 12)),
                Foreground = GetResourceBrush("ForegroundBrush", "#F8FAFC"),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleRow.Children.Add(textBlock);

            if (!string.IsNullOrEmpty(badgeText) && badgeBg != null && badgeFg != null)
            {
                var badge = new Border
                {
                    Background = badgeBg,
                    BorderBrush = badgeFg,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = badgeText,
                        FontSize = 9.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = badgeFg
                    }
                };
                titleRow.Children.Add(badge);
            }

            headerPanel.Children.Add(titleRow);

            // Subtle underline divider for H1 and H2
            if (level <= 2)
            {
                headerPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = GetResourceBrush("BorderBrush", "#334155"),
                    Margin = new Thickness(0, 5, 0, 4)
                });
            }

            return headerPanel;
        }

        private static UIElement CreateBulletItem(string bulletMarker, string text)
        {
            var grid = new Grid
            {
                Margin = new Thickness(2, 3, 0, 4)
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Bullet icon
            UIElement bulletElement;
            if (char.IsDigit(bulletMarker[0]))
            {
                bulletElement = new TextBlock
                {
                    Text = bulletMarker,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = GetResourceBrush("AccentBrush", "#3B82F6"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 1, 0, 0)
                };
            }
            else
            {
                // Sleek circle bullet point
                bulletElement = new Ellipse
                {
                    Width = 5.5,
                    Height = 5.5,
                    Fill = GetResourceBrush("AccentBrush", "#3B82F6"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 7, 0, 0)
                };
            }
            Grid.SetColumn(bulletElement, 0);
            grid.Children.Add(bulletElement);

            // Item content
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
                FontSize = 12.5,
                Foreground = GetResourceBrush("ForegroundBrush", "#E2E8F0"),
                VerticalAlignment = VerticalAlignment.Center
            };

            // Detect if item starts with a bold title like "**Discord Webhook Fix** — description"
            ParseInlines(text, textBlock.Inlines);

            Grid.SetColumn(textBlock, 1);
            grid.Children.Add(textBlock);

            return grid;
        }

        private static UIElement CreateParagraph(string text)
        {
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
                FontSize = 12.5,
                Foreground = GetResourceBrush("ForegroundBrush", "#E2E8F0"),
                Margin = new Thickness(0, 2, 0, 4)
            };

            ParseInlines(text, textBlock.Inlines);
            return textBlock;
        }

        private static UIElement CreateBlockquote(string text)
        {
            var border = new Border
            {
                BorderBrush = GetResourceBrush("AccentBrush", "#3B82F6"),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(15, 59, 130, 246)),
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(0, 6, 6, 0),
                Margin = new Thickness(0, 4, 0, 6)
            };

            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                FontSize = 12,
                LineHeight = 18,
                Foreground = GetResourceBrush("MutedForegroundBrush", "#94A3B8")
            };

            ParseInlines(text, textBlock.Inlines);
            border.Child = textBlock;
            return border;
        }

        private static UIElement CreateCodeBlock(string code)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)), // #0F172A
                BorderBrush = GetResourceBrush("BorderBrush", "#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 6)
            };

            var textBlock = new TextBlock
            {
                Text = code,
                FontFamily = new FontFamily("Consolas, Cascadia Code, Courier New"),
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                TextWrapping = TextWrapping.Wrap
            };

            border.Child = textBlock;
            return border;
        }

        private static UIElement CreateDivider()
        {
            return new Border
            {
                Height = 1,
                Background = GetResourceBrush("BorderBrush", "#334155"),
                Margin = new Thickness(0, 8, 0, 8)
            };
        }

        /// <summary>
        /// Parses inline Markdown constructs: **bold**, *italic*, `code`, and links [text](url).
        /// </summary>
        private static void ParseInlines(string text, InlineCollection inlines)
        {
            if (string.IsNullOrEmpty(text))
                return;

            // Pattern that captures bold, code, links, and italics
            // Order is important: Bold before italic, code before links
            string pattern = @"(?<bold>\*\*(?:[^*]+)\*\*|__(?:[^_]+)__)|" +
                             @"(?<code>`[^`]+`)|" +
                             @"(?<link>\[(?:[^\]]+)\]\((?:https?://[^\)]+)\))|" +
                             @"(?<italic>(?<!\*)\*(?!\*)(?:[^*]+)(?<!\*)\*(?!\*)|(?<!_)_(?!_)(?:[^_]+)(?<!_)_(?!_))";

            int lastIndex = 0;
            var matches = Regex.Matches(text, pattern);

            foreach (Match match in matches)
            {
                // Text before match
                if (match.Index > lastIndex)
                {
                    string plain = text.Substring(lastIndex, match.Index - lastIndex);
                    inlines.Add(new Run(plain));
                }

                if (match.Groups["bold"].Success)
                {
                    string val = match.Groups["bold"].Value;
                    string content = val.StartsWith("**") ? val.Substring(2, val.Length - 4) : val.Substring(2, val.Length - 4);
                    inlines.Add(new Bold(new Run(content))
                    {
                        Foreground = GetResourceBrush("ForegroundBrush", "#F8FAFC")
                    });
                }
                else if (match.Groups["code"].Success)
                {
                    string val = match.Groups["code"].Value;
                    string code = val.Substring(1, val.Length - 2);

                    var codeBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                        BorderBrush = GetResourceBrush("BorderBrush", "#334155"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(4, 0, 4, 1),
                        Margin = new Thickness(2, 0, 2, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = code,
                            FontFamily = new FontFamily("Consolas, Cascadia Code, Courier New"),
                            FontSize = 11,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = GetResourceBrush("AccentBrush", "#38BDF8"),
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };

                    inlines.Add(new InlineUIContainer(codeBorder)
                    {
                        BaselineAlignment = BaselineAlignment.Center
                    });
                }
                else if (match.Groups["link"].Success)
                {
                    string val = match.Groups["link"].Value;
                    var linkMatch = Regex.Match(val, @"\[([^\]]+)\]\((https?://[^\)]+)\)");
                    if (linkMatch.Success)
                    {
                        string linkText = linkMatch.Groups[1].Value;
                        string linkUrl = linkMatch.Groups[2].Value;

                        var hyperlink = new Hyperlink(new Run(linkText))
                        {
                            NavigateUri = new Uri(linkUrl),
                            Foreground = GetResourceBrush("AccentBrush", "#3B82F6"),
                            TextDecorations = TextDecorations.Underline,
                            Cursor = System.Windows.Input.Cursors.Hand
                        };
                        hyperlink.RequestNavigate += (s, e) =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                            }
                            catch { }
                            e.Handled = true;
                        };
                        inlines.Add(hyperlink);
                    }
                    else
                    {
                        inlines.Add(new Run(val));
                    }
                }
                else if (match.Groups["italic"].Success)
                {
                    string val = match.Groups["italic"].Value;
                    string content = val.Substring(1, val.Length - 2);
                    inlines.Add(new Italic(new Run(content)));
                }

                lastIndex = match.Index + match.Length;
            }

            // Remaining text
            if (lastIndex < text.Length)
            {
                inlines.Add(new Run(text.Substring(lastIndex)));
            }
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
    }
}
