using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Linq;
using SteamPluginManager.Views;

namespace SteamPluginManager
{
    public partial class Dashboard : Window
    {
        private List<ManifestFile> _newestManifests = new();
        private void OnlineFix_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToOnlineFix();
        }
        private const int SaltSize = 16;

        private static string GetBackupPassword()
        {
            var password = Environment.GetEnvironmentVariable("SPM_BACKUP_PASSWORD");
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Environment variable 'SPM_BACKUP_PASSWORD' is not set.");
            }

            return password;
        }
        public Dashboard()
        {
            InitializeComponent();
            WindowNavigator.RegisterDashboard(this);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CenterWindow();
            _ = LoadNewestManifestsAsync();
            _ = CheckForUpdatesOnStartupAsync();
        }

        private async Task LoadNewestManifestsAsync()
        {
            try
            {
                _newestManifests = await FetchNewestManifestsAsync(6);
                CreateManifestCards();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading manifests: {ex.Message}");
            }
        }

        private async Task<List<ManifestFile>> FetchNewestManifestsAsync(int count)
        {
            try
            {
                // Query database for newest manifests ordered by CreatedAt descending
                var manifests = await SupabaseConfig.GetNewestManifestFilesAsync(count);
                return manifests ?? new List<ManifestFile>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Database query error: {ex.Message}");
                return new List<ManifestFile>();
            }
        }

        private void CreateManifestCards()
        {
            if (FindName("ManifestCardsGrid") is not Grid grid)
                return;

            grid.Children.Clear();
            int columnIndex = 0;

            foreach (var manifest in _newestManifests)
            {
                if (columnIndex >= 5) break; // Max 3 columns (0, 2, 4 skipping spacing columns)

                // Skip spacing columns (1, 3)
                if (columnIndex % 2 == 1)
                    columnIndex++;

                var card = CreateManifestCard(manifest);
                Grid.SetColumn(card, columnIndex);
                grid.Children.Add(card);

                columnIndex += 2; // Move to next column pair
            }
        }

        private Border CreateManifestCard(ManifestFile manifest)
        {
            var card = new Border
            {
                Cursor = Cursors.Hand,
                Tag = manifest,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220)),
                BorderThickness = new Thickness(1),
                MinHeight = 200
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Thumbnail
            var imageElement = new Image
            {
                Stretch = System.Windows.Media.Stretch.UniformToFill,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var imageBorder = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)),
                CornerRadius = new CornerRadius(8, 8, 0, 0),
                ClipToBounds = true,
                Child = imageElement,
                Margin = new Thickness(0, 0, 0, 8)
            };

            Grid.SetRow(imageBorder, 0);
            grid.Children.Add(imageBorder);

            // Info panel
            var infoPanel = new StackPanel
            {
                Margin = new Thickness(12, 8, 12, 12),
                VerticalAlignment = VerticalAlignment.Top
            };

            var nameBlock = new TextBlock
            {
                Text = manifest.Name ?? "Unknown",
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black),
                Margin = new Thickness(0, 0, 0, 4),
                MaxHeight = 40
            };

            var genreBorder = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 122, 204)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = !string.IsNullOrEmpty(manifest.Genre) ? Visibility.Visible : Visibility.Collapsed
            };

            var genreBlock = new TextBlock
            {
                Text = manifest.Genre ?? "",
                FontSize = 9,
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.SemiBold
            };
            genreBorder.Child = genreBlock;

            var appIdBlock = new TextBlock
            {
                Text = $"ID: {manifest.AppId}",
                FontSize = 10,
                Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black)
            };

            infoPanel.Children.Add(nameBlock);
            infoPanel.Children.Add(genreBorder);
            infoPanel.Children.Add(appIdBlock);

            Grid.SetRow(infoPanel, 1);
            grid.Children.Add(infoPanel);

            card.Child = grid;
            card.MouseLeftButtonUp += (s, e) => ManifestCard_Click(manifest);

            // Load thumbnail async
            _ = LoadThumbnailForCardAsync(manifest, imageElement);

            return card;
        }

        private Task LoadThumbnailForCardAsync(ManifestFile manifest, Image imageElement)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(manifest.ThumbnailPath))
                        return;

                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(manifest.ThumbnailPath, UriKind.RelativeOrAbsolute);
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.EndInit();

                    imageElement.Dispatcher.Invoke(() => imageElement.Source = bitmap);
                }
                catch { /* Silently fail on image load */ }
            });
        }

        private void ManifestCard_Click(ManifestFile manifest)
        {
            WindowNavigator.NavigateToHZManifestDetail(manifest);
        }

        private void ViewAllManifests_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToHZManifest();
        }

        private async Task CheckForUpdatesOnStartupAsync()
        {
            try
            {
                // Read auto-update preference (default true)
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SteamPluginManager"
                );
                string autoUpdateFile = Path.Combine(appDataPath, "autoupdate.txt");

                bool autoUpdateEnabled = true;
                if (File.Exists(autoUpdateFile))
                {
                    string content = File.ReadAllText(autoUpdateFile).Trim().ToLower();
                    autoUpdateEnabled = (content == "true" || content == "1");
                }

                if (!autoUpdateEnabled) return;

                // Small delay to let UI finish loading
                await Task.Delay(500);
                await CheckForUpdatesAsync();
            }
            catch { /* don't block startup on errors */ }
        }

        private void CenterWindow()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double windowWidth = this.Width;
            double windowHeight = this.Height;

            this.Left = (screenWidth / 2) - (windowWidth / 2);
            this.Top = (screenHeight / 2) - (windowHeight / 2);
        }

        // Window Controls
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximizeRestore();
            }
            else
            {
                try { DragMove(); }
                catch { }
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); }
            catch { }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximizeRestore();
        }

        private void ToggleMaximizeRestore()
        {
            if (this.WindowState == WindowState.Maximized)
                this.WindowState = WindowState.Normal;
            else
                this.WindowState = WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Close Dashboard and shutdown the entire application
            Application.Current.Shutdown();
        }

        // Feature Button Handlers
        private void Library_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.OpenMainWindow();
        }

        private void CacheManager_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Cache Manager feature coming soon!", "Feature in Development", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ActivityLog_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Activity Log feature coming soon!", "Feature in Development", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RestartSteam_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SteamHelper.RestartSteam();
                MessageBox.Show("Steam is restarting...", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error restarting Steam: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            SettingsWindow settingsWindow = new SettingsWindow();
            settingsWindow.ShowDialog();
        }

        private void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            // Check updates without navigation, just show the results on the dashboard
            _ = CheckForUpdatesAsync();
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                var updateInfo = await UpdateChecker.CheckForUpdatesAsync();

                if (updateInfo.HasUpdate)
                {
                    var dialog = new StyledMessageDialog(
                        "Update Available",
                        $"New Version Available!\n\nCurrent: {updateInfo.CurrentVersion}\nLatest: {updateInfo.LatestVersion}\n\n{updateInfo.ReleaseNotes}\n\nWould you like to download and install the update?",
                        showCancel: true
                    );
                    dialog.Owner = this;
                    dialog.PrimaryButton.Content = "Download";
                    dialog.SecondaryButton.Content = "Cancel";
                    var result = dialog.ShowDialog();

                    if (result == true && !string.IsNullOrEmpty(updateInfo.DownloadUrl))
                    {
                        await DownloadAndInstallUpdateAsync(updateInfo.DownloadUrl);
                    }
                }
                else
                {
                    var dialog = new StyledMessageDialog("Up to Date", "You are already running the latest version.");
                    dialog.Owner = this;
                    dialog.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to check for updates: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DownloadAndInstallUpdateAsync(string downloadUrl)
        {
            var progressWindow = new Window
            {
                Title = "Downloading Update",
                Width = 400,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = Application.Current.Resources["BackgroundBrush"] as System.Windows.Media.Brush,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var textBlock = new TextBlock
            {
                Text = "Downloading update...",
                FontSize = 14,
                Foreground = Application.Current.Resources["ForegroundBrush"] as System.Windows.Media.Brush,
                Margin = new Thickness(20)
            };
            var progressBar = new ProgressBar
            {
                Width = 300,
                Height = 20,
                IsIndeterminate = true,
                Margin = new Thickness(20, 0, 20, 20)
            };

            panel.Children.Add(textBlock);
            panel.Children.Add(progressBar);
            progressWindow.Content = panel;
            progressWindow.Show();

            void OnProgressChanged(long downloaded, long total)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (total > 0)
                    {
                        progressBar.IsIndeterminate = false;
                        progressBar.Maximum = total;
                        progressBar.Value = downloaded;
                        textBlock.Text = $"Downloading update... ({((double)downloaded / total):P0})";
                    }
                });
            }

            var (success, message) = await UpdateChecker.DownloadAndInstallUpdateAsync(downloadUrl, OnProgressChanged);

            progressWindow.Close();

            if (success)
            {
                // Close the application immediately after installer starts
                await Task.Delay(500); // Small delay to ensure installer process starts
                Environment.Exit(0);
            }
            else
            {
                MessageBox.Show($"Failed to download or install update: {message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void HZManifest_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToHZManifest();
        }

        private void GameBypass_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToGameBypass();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
            MessageBox.Show(
                "SGR - Steam Game Remover\n\n" +
                $"Version: {version}\n" +
                "A comprehensive Steam game management tool\n\n" +
                "Created by: Vamola\n" +
                "© 2024-2025",
                "About SGR",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "SPM Backup (*.spmb)|*.spmb|All Files (*.*)|*.*",
                Title = "Restore Game Backup",
                Multiselect = false
            };

            if (openFileDialog.ShowDialog() != true)
                return;

            try
            {
                string backupFilePath = openFileDialog.FileName;
                
                // Validate file exists
                if (!File.Exists(backupFilePath))
                {
                    MessageBox.Show(
                        "Selected backup file does not exist.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                // Show confirmation dialog
                var result = MessageBox.Show(
                    $"This will restore the backup from:\n{Path.GetFileName(backupFilePath)}\n\n" +
                    "Existing plugin and manifest files will be overwritten.\n" +
                    "Continue?",
                    "Confirm Restore",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;

                _ = RestoreBackupAsync(backupFilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error opening backup file: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async Task RestoreBackupAsync(string backupFilePath)
        {
            try
            {
                // Show progress message
                var progressWindow = new Window
                {
                    Title = "Restoring Backup",
                    Width = 400,
                    Height = 150,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    Background = Application.Current.Resources["BackgroundBrush"] as System.Windows.Media.Brush,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = false,
                    ResizeMode = ResizeMode.NoResize
                };

                var panel = new System.Windows.Controls.StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var textBlock = new System.Windows.Controls.TextBlock
                {
                    Text = "Restoring backup files...",
                    FontSize = 14,
                    Foreground = Application.Current.Resources["ForegroundBrush"] as System.Windows.Media.Brush,
                    Margin = new Thickness(20)
                };

                panel.Children.Add(textBlock);
                progressWindow.Content = panel;
                progressWindow.Show();

                // Get Steam path from MainWindow
                string steamPath = SteamHelper.GetSteamPath();
                if (string.IsNullOrEmpty(steamPath))
                {
                    progressWindow.Close();
                    MessageBox.Show(
                        "Could not find Steam installation directory.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                // Decrypt and extract backup
                using (var memoryStream = DecryptStream(backupFilePath))
                {
                    string stplugInPath = Path.Combine(steamPath, "config", "stplug-in");
                    string depotCachePath = Path.Combine(steamPath, "depotcache");
                    string depotCachePathOld = Path.Combine(steamPath, "config", "depotcache");
                    
                    // Ensure directories exist
                    Directory.CreateDirectory(stplugInPath);
                    Directory.CreateDirectory(depotCachePath);
                    Directory.CreateDirectory(depotCachePathOld);

                    int luaFiles = 0;
                    int manifestFiles = 0;

                    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (entry.FullName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                            {
                                string targetPath = Path.Combine(stplugInPath, entry.Name);
                                entry.ExtractToFile(targetPath, overwrite: true);
                                luaFiles++;
                            }
                            else if (entry.FullName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                            {
                                // Write to both new and old paths
                                string targetPath = Path.Combine(depotCachePath, entry.Name);
                                string targetPathOld = Path.Combine(depotCachePathOld, entry.Name);
                                entry.ExtractToFile(targetPath, overwrite: true);
                                entry.ExtractToFile(targetPathOld, overwrite: true);
                                manifestFiles++;
                            }
                        }
                    }

                    // Close progress window
                    progressWindow.Close();

                    // Refresh game library after restore
                    try
                    {
                        WindowNavigator.OpenMainWindow();
                        MainWindow mainWindow = Application.Current.MainWindow as MainWindow;
                        if (mainWindow != null)
                        {
                            mainWindow.RefreshGameList();
                        }
                    }
                    catch { }

                    // Show success message
                    MessageBox.Show(
                        $"Backup restored successfully!\n\n" +
                        $"Lua Files: {luaFiles}\n" +
                        $"Manifest Files: {manifestFiles}\n\n" +
                        $"Your game plugins and manifests have been restored.",
                        "Restore Complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to restore backup:\n{ex.Message}",
                    "Restore Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private MemoryStream DecryptStream(string inputFile)
        {
            var memoryStream = new MemoryStream();
            using (var inStream = new FileStream(inputFile, FileMode.Open))
            {
                byte[] salt = new byte[SaltSize];
                inStream.Read(salt, 0, salt.Length);

                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    byte[] iv = new byte[aes.IV.Length];
                    inStream.Read(iv, 0, iv.Length);
                    aes.IV = iv;

                    var key = new Rfc2898DeriveBytes(GetBackupPassword(), salt, 10000, HashAlgorithmName.SHA256).GetBytes(aes.KeySize / 8);
                    aes.Key = key;

                    using (var cryptoStream = new CryptoStream(inStream, aes.CreateDecryptor(), CryptoStreamMode.Read))
                    {
                        cryptoStream.CopyTo(memoryStream);
                    }
                }
            }
            memoryStream.Position = 0;
            return memoryStream;
        }
    }
}
