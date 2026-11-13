using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using SteamPluginManager;
using SteamPluginManager.Models;
using SteamPluginManager.Services.API;

namespace SteamPluginManager.Views
{
    public partial class ToolsView : UserControl
    {
        private List<DiscordForumPost> _posts = new();
        private bool _hasLoaded;
        private bool _isLoading;

        public ToolsView()
        {
            InitializeComponent();
        }

        private async void ToolsView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_hasLoaded)
                return;

            _hasLoaded = true;
            await LoadPostsAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadPostsAsync();
        }

        private async Task LoadPostsAsync()
        {
            if (_isLoading)
                return;

            _isLoading = true;
            RefreshButton.IsEnabled = false;
            StatusText.Text = "Loading posts...";
            EmptyStateText.Visibility = Visibility.Collapsed;
            LoadingStatusText.Text = "Loading posts...";
            LoadingOverlay.Visibility = Visibility.Visible;
            StartContinuousLoadingAnimation();

            try
            {
                _posts = await DiscordForumService.GetPostsAsync();
                ApplySearchFilter();
                StatusText.Text = $"Loaded {_posts.Count} posts.";
            }
            catch (Exception ex)
            {
                Logger.Log($"[ToolsView] Failed to load Discord forum posts: {ex.Message}");
                _posts = new List<DiscordForumPost>();
                PostsItemsControl.ItemsSource = null;
                EmptyStateText.Text = ex.Message;
                EmptyStateText.Visibility = Visibility.Visible;
                StatusText.Text = "Could not load forum posts.";
            }
            finally
            {
                StopContinuousLoadingAnimation();
                LoadingOverlay.Visibility = Visibility.Collapsed;
                RefreshButton.IsEnabled = true;
                _isLoading = false;
            }
        }

        private void StartContinuousLoadingAnimation()
        {
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(1),
                RepeatBehavior = RepeatBehavior.Forever
            };
            ContinuousRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
        }

        private void StopContinuousLoadingAnimation()
        {
            ContinuousRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplySearchFilter();
        }

        private void ApplySearchFilter()
        {
            if (PostsItemsControl == null || SearchTextBox == null)
                return;

            string query = SearchTextBox.Text.Trim();
            var filteredPosts = _posts
                .Where(post => post.MatchesSearch(query))
                .ToList();
            PostsItemsControl.ItemsSource = filteredPosts;

            if (_posts.Count == 0)
            {
                EmptyStateText.Text = "No forum posts found.";
                EmptyStateText.Visibility = Visibility.Visible;
            }
            else if (filteredPosts.Count == 0)
            {
                EmptyStateText.Text = "No posts match your search.";
                EmptyStateText.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyStateText.Visibility = Visibility.Collapsed;
            }
        }

        private void PostCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border card || card.DataContext is not DiscordForumPost post)
                return;

            e.Handled = true;
            WindowNavigator.GetMainShell()?.OpenInspector(
                new ToolsPostDetailView(post),
                post.Author,
                post.Title);
        }

        private async void DownloadAttachment_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not DiscordForumAttachment attachment)
                return;

            string safeFileName = Path.GetFileName(attachment.FileName);
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
                safeFileName = safeFileName.Replace(invalidCharacter, '_');

            if (string.IsNullOrWhiteSpace(safeFileName))
                safeFileName = "discord-attachment";

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
                Title = "Save Discord attachment",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog() != true)
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
                Logger.Log($"[ToolsView] Attachment download failed: {ex.Message}");
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
