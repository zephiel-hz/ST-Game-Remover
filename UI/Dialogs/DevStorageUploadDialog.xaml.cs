using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SteamPluginManager.Services.API;
using SteamPluginManager;
using SteamPluginManager.Views;

namespace SteamPluginManager.UI.Dialogs
{
    public partial class DevStorageUploadDialog : Window
    {
        private static readonly HashSet<int> KnownRockstarAppIds = new()
        {
            271590, 1174180, 1404210, 12170, 12180, 12190, 1546970, 1546990, 1547000, 1546980, 12200, 204100, 12140, 12150, 110800, 266850
        };

        private static readonly HashSet<int> KnownEaAppIds = new()
        {
            1222680, 1693980, 1845910, 3654560, 1238810, 1238840, 1238860, 1517290, 1794680, 2195250, 1172380, 1774580, 1849250, 1426210, 1225570, 1262560, 1262540, 1222670, 1237950, 1237970, 1172470, 1328670, 24720, 17460, 24980, 997010, 1846380, 2488620
        };

        private static readonly HashSet<int> KnownUbisoftAppIds = new()
        {
            3159330, 3193030, 208480, 2063840, 3751950, 582160, 359550, 524220, 477160, 2208920, 460930, 242050, 302510, 242700, 44350, 48220
        };

        private readonly List<DevManifestGame> _knownGames;
        private string _selectedFilePath = string.Empty;
        private long _selectedFileSize = 0;
        private bool _isUploading = false;

        public bool UploadSucceeded { get; private set; }
        public string UploadedKey { get; private set; } = string.Empty;
        public string UploadedStorageType { get; private set; } = "gamebypass";

        public DevStorageUploadDialog(IEnumerable<DevManifestGame>? knownGames = null, string initialStorage = "gamebypass", string? initialCategory = null)
        {
            InitializeComponent();
            _knownGames = knownGames?.ToList() ?? new List<DevManifestGame>();

            if (!string.IsNullOrWhiteSpace(initialCategory) && CategoryComboBox != null)
            {
                foreach (ComboBoxItem item in CategoryComboBox.Items)
                {
                    if (item.Tag?.ToString()?.Equals(initialCategory, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        CategoryComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            if (initialStorage.Equals("onlinefix", StringComparison.OrdinalIgnoreCase))
            {
                TargetOnlineFixRadio.IsChecked = true;
                if (CategorySelectionPanel != null) CategorySelectionPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                TargetGameBypassRadio.IsChecked = true;
                if (CategorySelectionPanel != null) CategorySelectionPanel.Visibility = Visibility.Visible;
            }
        }

        private string GetSelectedCategory()
        {
            if (CategoryComboBox?.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
            {
                return tag.Trim().ToUpperInvariant();
            }
            return "DENUVO";
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUploading)
            {
                if (!ModernMessageBox.ShowConfirm("Upload is currently in progress. Are you sure you want to cancel?", "Cancel Upload", isDestructive: true, owner: this))
                {
                    return;
                }
            }
            DialogResult = false;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseButton_Click(sender, e);
        }

        private void TargetStorage_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            bool isGameBypass = TargetGameBypassRadio.IsChecked == true;
            if (CategorySelectionPanel != null)
            {
                CategorySelectionPanel.Visibility = isGameBypass ? Visibility.Visible : Visibility.Collapsed;
            }

            string currentKey = RemoteKeyTextBox.Text.Trim();
            string filename = "";
            if (!string.IsNullOrEmpty(currentKey))
            {
                filename = Path.GetFileName(currentKey);
            }
            else if (!string.IsNullOrEmpty(_selectedFilePath))
            {
                filename = Path.GetFileName(_selectedFilePath);
            }

            if (isGameBypass)
            {
                string cat = GetSelectedCategory();
                RemoteKeyTextBox.Text = string.IsNullOrEmpty(filename) ? $"gamebypass/{cat}/" : $"gamebypass/{cat}/{filename}";
            }
            else
            {
                RemoteKeyTextBox.Text = string.IsNullOrEmpty(filename) ? "onlinefix/" : $"onlinefix/{filename}";
            }

            ValidateForm();
        }

        private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || TargetGameBypassRadio.IsChecked != true) return;

            string currentKey = RemoteKeyTextBox.Text.Trim();
            string filename = "";
            if (!string.IsNullOrEmpty(currentKey))
            {
                filename = Path.GetFileName(currentKey);
            }
            else if (!string.IsNullOrEmpty(_selectedFilePath))
            {
                filename = Path.GetFileName(_selectedFilePath);
            }

            string cat = GetSelectedCategory();
            RemoteKeyTextBox.Text = string.IsNullOrEmpty(filename) ? $"gamebypass/{cat}/" : $"gamebypass/{cat}/{filename}";
            ValidateForm();
        }

        private void BrowseLocalFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Select Archive File to Upload",
                    Filter = "Archive Files (*.zip;*.7z;*.rar;*.tar.gz)|*.zip;*.7z;*.rar;*.tar.gz|All Files (*.*)|*.*",
                    Multiselect = false
                };

                if (dialog.ShowDialog(this) == true)
                {
                    _selectedFilePath = dialog.FileName;
                    var fileInfo = new FileInfo(_selectedFilePath);
                    _selectedFileSize = fileInfo.Length;

                    LocalFilePathTextBox.Text = _selectedFilePath;
                    LocalFileNameText.Text = fileInfo.Name;
                    LocalFileSizeBadge.Text = $"Size: {FormatFileSize(_selectedFileSize)}";
                    LocalFileInfoPanel.Visibility = Visibility.Visible;

                    if (TargetGameBypassRadio.IsChecked == true)
                    {
                        AutoDetectCategoryFromFileName(fileInfo.Name);
                        string cat = GetSelectedCategory();
                        RemoteKeyTextBox.Text = $"gamebypass/{cat}/{fileInfo.Name}";
                    }
                    else
                    {
                        RemoteKeyTextBox.Text = $"onlinefix/{fileInfo.Name}";
                    }

                    ValidateForm();
                }
            }
            catch (Exception ex)
            {
                ErrorNoticeText.Text = $"Error selecting file: {ex.Message}";
            }
        }

        private void AutoDetectCategoryFromFileName(string fileName)
        {
            var match = Regex.Match(fileName, @"\b(\d{4,8})\b");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int appId)) return;

            string detected = "";
            if (KnownRockstarAppIds.Contains(appId))
                detected = "ROCKSTAR";
            else if (KnownEaAppIds.Contains(appId))
                detected = "EA";
            else if (KnownUbisoftAppIds.Contains(appId))
                detected = "UBISOFT";
            else
            {
                var matched = _knownGames.FirstOrDefault(g => g.AppId == appId);
                if (matched != null)
                {
                    string nameAndGenre = $"{matched.Name} {matched.Genre}".ToLowerInvariant();
                    if (nameAndGenre.Contains("ubisoft") || nameAndGenre.Contains("assassin")) detected = "UBISOFT";
                    else if (nameAndGenre.Contains("electronic arts") || nameAndGenre.Contains("battlefield") || nameAndGenre.Contains("fifa") || nameAndGenre.Contains("need for speed")) detected = "EA";
                    else if (nameAndGenre.Contains("rockstar") || nameAndGenre.Contains("grand theft auto") || nameAndGenre.Contains("red dead")) detected = "ROCKSTAR";
                    else if (nameAndGenre.Contains("denuvo")) detected = "DENUVO";
                }
            }

            if (!string.IsNullOrEmpty(detected) && CategoryComboBox != null)
            {
                foreach (ComboBoxItem item in CategoryComboBox.Items)
                {
                    if (item.Tag?.ToString()?.Equals(detected, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        CategoryComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private void RemoteKeyTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            DetectMatchingGame();
            ValidateForm();
        }

        private void DetectMatchingGame()
        {
            string key = RemoteKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                MatchedGameCard.Visibility = Visibility.Collapsed;
                return;
            }

            string filename = Path.GetFileNameWithoutExtension(key);
            var match = Regex.Match(filename, @"\b(\d{4,8})\b");

            if (match.Success && int.TryParse(match.Groups[1].Value, out int appId))
            {
                var matched = _knownGames.FirstOrDefault(g => g.AppId == appId);
                if (matched != null)
                {
                    MatchedGameTitleText.Text = $"Detected Game: {matched.Name}";
                    MatchedGameAppIdText.Text = $"AppID: {matched.AppId} • Genre: {matched.Genre}";
                    MatchedGameCard.Visibility = Visibility.Visible;
                    return;
                }
                else
                {
                    MatchedGameTitleText.Text = $"Unknown Game Title";
                    MatchedGameAppIdText.Text = $"Detected AppID: {appId} (Not in local catalog)";
                    MatchedGameCard.Visibility = Visibility.Visible;
                    return;
                }
            }

            MatchedGameCard.Visibility = Visibility.Collapsed;
        }

        private void ValidateForm()
        {
            ErrorNoticeText.Text = string.Empty;

            if (string.IsNullOrEmpty(_selectedFilePath) || !File.Exists(_selectedFilePath))
            {
                UploadNowButton.IsEnabled = false;
                return;
            }

            string key = RemoteKeyTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                UploadNowButton.IsEnabled = false;
                return;
            }

            bool isGameBypass = TargetGameBypassRadio.IsChecked == true;
            string requiredPrefix = isGameBypass ? "gamebypass/" : "onlinefix/";
            if (!key.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase) || key.Length <= requiredPrefix.Length)
            {
                ErrorNoticeText.Text = $"Remote key must start with '{requiredPrefix}' followed by filename.";
                UploadNowButton.IsEnabled = false;
                return;
            }

            if (isGameBypass)
            {
                var parts = key.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || string.IsNullOrWhiteSpace(Path.GetFileName(key)))
                {
                    ErrorNoticeText.Text = "GameBypass remote key must follow 'gamebypass/{CATEGORY}/{filename.zip}'.";
                    UploadNowButton.IsEnabled = false;
                    return;
                }
            }

            UploadNowButton.IsEnabled = !_isUploading;
        }

        private async void UploadNowButton_Click(object sender, RoutedEventArgs e)
        {
            string key = RemoteKeyTextBox.Text.Trim();
            if (string.IsNullOrEmpty(_selectedFilePath) || !File.Exists(_selectedFilePath) || string.IsNullOrEmpty(key))
            {
                return;
            }

            bool isGameBypass = TargetGameBypassRadio.IsChecked == true;
            string targetProvider = isGameBypass ? "Cloudflare R2 (gamebypass/)" : "Backblaze B2 (onlinefix/)";

            _isUploading = true;
            UploadNowButton.IsEnabled = false;
            LocalFilePathTextBox.IsEnabled = false;
            RemoteKeyTextBox.IsEnabled = false;
            TargetGameBypassRadio.IsEnabled = false;
            TargetOnlineFixRadio.IsEnabled = false;
            if (CategoryComboBox != null) CategoryComboBox.IsEnabled = false;

            UploadProgressPanel.Visibility = Visibility.Visible;
            UploadProgressBar.Value = 0;
            UploadPercentText.Text = "0%";
            UploadStatusLabel.Text = $"Preparing upload to {targetProvider}...";
            ErrorNoticeText.Text = string.Empty;

            var progress = new Progress<int>(percent =>
            {
                Dispatcher.Invoke(() =>
                {
                    UploadProgressBar.Value = percent;
                    UploadPercentText.Text = $"{percent}%";
                    UploadStatusLabel.Text = $"Uploading to {targetProvider} ({percent}%)...";
                });
            });

            try
            {
                string uploadedUrl = string.Empty;
                if (isGameBypass)
                {
                    uploadedUrl = await R2Config.UploadFileAsync(_selectedFilePath, key, "application/zip", progress);
                }
                else
                {
                    uploadedUrl = await B2Config.UploadFileAsync(_selectedFilePath, key, "application/zip", progress);
                }

                if (!string.IsNullOrEmpty(uploadedUrl))
                {
                    UploadSucceeded = true;
                    UploadedKey = key;
                    UploadedStorageType = isGameBypass ? "gamebypass" : "onlinefix";

                    UploadStatusLabel.Text = "Upload completed successfully!";
                    UploadPercentText.Text = "100%";
                    UploadProgressBar.Value = 100;

                    ModernMessageBox.ShowInformation(
                        $"Successfully uploaded '{key}' ({FormatFileSize(_selectedFileSize)}) to {targetProvider}.",
                        "Upload Success",
                        owner: this
                    );

                    DialogResult = true;
                    Close();
                }
                else
                {
                    throw new InvalidOperationException($"Upload operation to {targetProvider} returned failure.");
                }
            }
            catch (Exception ex)
            {
                ErrorNoticeText.Text = $"Upload failed: {ex.Message}";
                ModernMessageBox.ShowError(
                    $"Failed to upload archive: {ex.Message}",
                    "Upload Failed",
                    owner: this
                );

                _isUploading = false;
                UploadProgressPanel.Visibility = Visibility.Collapsed;
                LocalFilePathTextBox.IsEnabled = true;
                RemoteKeyTextBox.IsEnabled = true;
                TargetGameBypassRadio.IsEnabled = true;
                TargetOnlineFixRadio.IsEnabled = true;
                if (CategoryComboBox != null) CategoryComboBox.IsEnabled = true;
                ValidateForm();
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024)):F2} MB";
            return $"{(bytes / (1024.0 * 1024 * 1024)):F2} GB";
        }
    }
}
