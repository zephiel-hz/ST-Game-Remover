using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Win32;
using SteamPluginManager.Models;
using SteamPluginManager.Services.API;

namespace SteamPluginManager.Views
{
    public partial class ToolsPostDetailView : UserControl
    {
        private static readonly Regex UrlPattern = new(
            @"https?://[^\s<>]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MarkdownLinkPattern = new(
            @"\[([^\]\r\n]+)\]\((https?://[^\s)]+)\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public ToolsPostDetailView(DiscordForumPost post)
        {
            InitializeComponent();
            DataContext = post;
        }

        private void LinkedContentText_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBlock textBlock || textBlock.Tag is not string text)
                return;

            textBlock.Inlines.Clear();
            int currentIndex = 0;
            while (currentIndex < text.Length)
            {
                Match markdownMatch = MarkdownLinkPattern.Match(text, currentIndex);
                Match urlMatch = UrlPattern.Match(text, currentIndex);
                bool isMarkdownLink = markdownMatch.Success &&
                    (!urlMatch.Success || markdownMatch.Index <= urlMatch.Index);
                Match match = isMarkdownLink ? markdownMatch : urlMatch;
                if (!match.Success)
                    break;

                if (match.Index > currentIndex)
                    textBlock.Inlines.Add(new Run(text.Substring(currentIndex, match.Index - currentIndex)));

                if (isMarkdownLink &&
                    Uri.TryCreate(markdownMatch.Groups[2].Value, UriKind.Absolute, out var markdownUri) &&
                    IsHttpUri(markdownUri))
                {
                    var button = new Button
                    {
                        Content = markdownMatch.Groups[1].Value,
                        Tag = markdownUri,
                        Style = (Style)FindResource("MarkdownLinkButtonStyle")
                    };
                    button.Click += MarkdownLinkButton_Click;
                    textBlock.Inlines.Add(new InlineUIContainer(button)
                    {
                        BaselineAlignment = BaselineAlignment.Center
                    });
                    currentIndex = markdownMatch.Index + markdownMatch.Length;
                    continue;
                }

                if (isMarkdownLink)
                {
                    textBlock.Inlines.Add(new Run(markdownMatch.Value));
                    currentIndex = markdownMatch.Index + markdownMatch.Length;
                    continue;
                }

                string urlText = urlMatch.Value.TrimEnd('.', ',', '!', '?', ';', ':', ')', ']', '}');
                if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri) || !IsHttpUri(uri))
                {
                    textBlock.Inlines.Add(new Run(urlMatch.Value));
                    currentIndex = urlMatch.Index + urlMatch.Length;
                    continue;
                }

                var hyperlink = new Hyperlink(new Run(urlText))
                {
                    NavigateUri = uri,
                    Cursor = Cursors.Hand
                };
                hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                hyperlink.RequestNavigate += Hyperlink_RequestNavigate;
                textBlock.Inlines.Add(hyperlink);

                int trailingLength = urlMatch.Length - urlText.Length;
                if (trailingLength > 0)
                    textBlock.Inlines.Add(new Run(urlMatch.Value.Substring(urlText.Length)));

                currentIndex = urlMatch.Index + urlMatch.Length;
            }

            if (currentIndex < text.Length)
                textBlock.Inlines.Add(new Run(text.Substring(currentIndex)));
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            if (!IsHttpUri(e.Uri))
                return;

            e.Handled = true;
            OpenExternalLink(e.Uri);
        }

        private void MarkdownLinkButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Uri uri } && IsHttpUri(uri))
                OpenExternalLink(uri);
        }

        private static bool IsHttpUri(Uri uri) =>
            uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

        private static void OpenExternalLink(Uri uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[ToolsPostDetailView] Could not open link '{uri}': {ex.Message}");
                ModernMessageBox.ShowError(
                    $"Could not open the link.\n\n{ex.Message}",
                    "Open link failed");
            }
        }

        private async void ToolsPostDetailView_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is DiscordForumPost post)
                await LoadRepliesAsync(post, loadEarlier: false);
        }

        private async void LoadEarlierReplies_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is DiscordForumPost post)
                await LoadRepliesAsync(post, loadEarlier: true);
        }

        private async Task LoadRepliesAsync(DiscordForumPost post, bool loadEarlier)
        {
            if (post.IsLoadingReplies ||
                (!loadEarlier && post.RepliesLoaded) ||
                (loadEarlier && !post.HasMoreReplies))
            {
                return;
            }

            post.IsLoadingReplies = true;
            post.RepliesStatus = "Loading replies...";
            try
            {
                var page = await DiscordForumService.GetRepliesAsync(
                    post.Id,
                    loadEarlier ? post.BeforeReplyId : null);

                if (loadEarlier)
                {
                    for (int index = 0; index < page.Replies.Count; index++)
                        post.Replies.Insert(index, page.Replies[index]);
                }
                else
                {
                    foreach (var reply in page.Replies)
                        post.Replies.Add(reply);
                }

                post.BeforeReplyId = page.BeforeMessageId;
                post.HasMoreReplies = page.HasMore;
                post.RepliesLoaded = true;
                post.RepliesStatus = post.Replies.Count == 0 ? "No replies yet." : string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ToolsPostDetailView] Failed to load replies for post {post.Id}: {ex.Message}");
                post.RepliesStatus = "Could not load replies. Reopen this post to retry.";
            }
            finally
            {
                post.IsLoadingReplies = false;
            }
        }

        private async void DownloadAttachment_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not DiscordForumAttachment attachment)
                return;

            string safeFileName = Path.GetFileName(attachment.FileName);
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
                safeFileName = safeFileName.Replace(invalidCharacter, '_');

            if (string.IsNullOrWhiteSpace(safeFileName))
                safeFileName = "attachment";

            string extension = Path.GetExtension(safeFileName);
            string fileType = string.IsNullOrWhiteSpace(extension)
                ? "All files (*.*)|*.*"
                : $"{extension.TrimStart('.').ToUpperInvariant()} files (*{extension})|*{extension}|All files (*.*)|*.*";

            var dialog = new SaveFileDialog
            {
                FileName = safeFileName,
                Filter = fileType,
                DefaultExt = extension.TrimStart('.'),
                AddExtension = !string.IsNullOrWhiteSpace(extension),
                Title = "Save attachment",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;

            button.IsEnabled = false;
            try
            {
                await DiscordForumService.DownloadAttachmentAsync(attachment, dialog.FileName);
                ModernMessageBox.ShowInformation(
                    $"Attachment saved to:\n{dialog.FileName}",
                    "Download complete");
            }
            catch (Exception ex)
            {
                Logger.Log($"[ToolsPostDetailView] Attachment download failed: {ex.Message}");
                ModernMessageBox.ShowError(
                    $"Could not download the attachment.\n\n{ex.Message}",
                    "Download failed");
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }
}
