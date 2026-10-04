using System;
using System.Windows;
using System.Windows.Input;

namespace SteamPluginManager
{
    public partial class UpdateAvailableDialog : Window
    {
        public bool IsConfirmed { get; private set; } = false;

        public UpdateAvailableDialog(string currentVersion, string latestVersion, string releaseNotes)
        {
            InitializeComponent();

            string cleanCurrent = string.IsNullOrWhiteSpace(currentVersion) ? "1.0.0" : currentVersion.TrimStart('v', 'V');
            string cleanLatest = string.IsNullOrWhiteSpace(latestVersion) ? "Latest" : latestVersion.TrimStart('v', 'V');

            HeaderVersionText.Text = $"v{cleanLatest}";
            CurrentVersionText.Text = $"v{cleanCurrent}";
            LatestVersionText.Text = $"v{cleanLatest}";

            // Render GitHub Markdown Changelog
            ChangelogContentHost.Content = GitHubMarkdownParser.ParseToUIElement(releaseNotes);
        }

        public UpdateAvailableDialog(UpdateInfo updateInfo)
            : this(updateInfo.CurrentVersion ?? "1.0.0", updateInfo.LatestVersion ?? "Latest", updateInfo.ReleaseNotes ?? "")
        {
        }

        /// <summary>
        /// Shows the Update Available dialog centered on the specified owner window.
        /// Returns true if the user clicked "Download & Install", false otherwise.
        /// </summary>
        public static bool ShowUpdate(Window? owner, UpdateInfo updateInfo)
        {
            var dialog = new UpdateAvailableDialog(updateInfo);
            if (owner != null && owner.IsLoaded)
            {
                dialog.Owner = owner;
            }
            var result = dialog.ShowDialog();
            return result == true;
        }

        /// <summary>
        /// Shows the Update Available dialog with version strings and release notes.
        /// </summary>
        public static bool ShowUpdate(Window? owner, string currentVersion, string latestVersion, string releaseNotes)
        {
            var dialog = new UpdateAvailableDialog(currentVersion, latestVersion, releaseNotes);
            if (owner != null && owner.IsLoaded)
            {
                dialog.Owner = owner;
            }
            var result = dialog.ShowDialog();
            return result == true;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            IsConfirmed = false;
            DialogResult = false;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            IsConfirmed = false;
            DialogResult = false;
            Close();
        }

        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            IsConfirmed = true;
            DialogResult = true;
            Close();
        }
    }
}
