using System;
using System.IO;
using System.Windows;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    public partial class SettingsWindow : Window
    {
        private bool _isCheckingUpdate = false;
        private System.Windows.Controls.Button? _checkUpdateButton = null;

        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            try
            {
                // Load version
                try
                {
                    var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                    VersionText.Text = $"Version {version?.Major}.{version?.Minor}.{version?.Build}";
                }
                catch { }

                // Load cache size
                UpdateCacheSize();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateCacheSize()
        {
            try
            {
                // Gunakan path yang sama dengan CacheManager (AppData/Local)
                string cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SteamPluginManager",
                    "Cache"
                );

                long totalBytes = 0;
                if (Directory.Exists(cacheDir))
                {
                    var dir = new DirectoryInfo(cacheDir);
                    foreach (var file in dir.GetFiles("*", System.IO.SearchOption.AllDirectories))
                    {
                        try
                        {
                            totalBytes += file.Length;
                        }
                        catch { }
                    }
                }

                // Format cache size
                string sizeText = FormatBytes(totalBytes);
                CacheSizeText.Text = $"{sizeText} of Cache";
            }
            catch (Exception ex)
            {
                CacheSizeText.Text = "Unable to determine cache size";
            }
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show(
                    "Are you sure you want to clear the cache?\nThis will remove cached game data and screenshots.",
                    "Clear Cache",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result == MessageBoxResult.Yes)
                {
                    // Gunakan path yang sama dengan CacheManager (AppData/Local)
                    string cacheDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SteamPluginManager",
                        "Cache"
                    );

                    if (Directory.Exists(cacheDir))
                    {
                        try
                        {
                            // Force release of file handles by triggering garbage collection
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                            
                            // Add small delay to allow handles to be released
                            System.Threading.Thread.Sleep(100);
                            
                            // Try to delete with retry logic
                            bool deleted = false;
                            int retryCount = 3;
                            
                            while (retryCount > 0 && !deleted)
                            {
                                try
                                {
                                    Directory.Delete(cacheDir, true);
                                    deleted = true;
                                }
                                catch (IOException)
                                {
                                    retryCount--;
                                    if (retryCount > 0)
                                    {
                                        // Wait before retry
                                        System.Threading.Thread.Sleep(200);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Failed to delete cache folder: {ex.Message}\n\nPlease close all windows and try again.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }

                    // Recreate empty cache directory
                    try
                    {
                        Directory.CreateDirectory(cacheDir);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to recreate cache folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    MessageBox.Show("Cache cleared successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    UpdateCacheSize();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clear cache: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingUpdate)
            {
                MessageBox.Show("Already checking for updates. Please wait...", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Store button reference and disable during check
            _checkUpdateButton = sender as System.Windows.Controls.Button;
            if (_checkUpdateButton != null)
            {
                _checkUpdateButton.IsEnabled = false;
            }

            _ = CheckForUpdatesAsync();
        }

        private async Task CheckForUpdatesAsync()
        {
            _isCheckingUpdate = true;

            try
            {
                var updateInfo = await UpdateChecker.CheckForUpdatesAsync();

                if (updateInfo.ErrorMessage != null)
                {
                    MessageBox.Show(
                        $"Current Version: {updateInfo.CurrentVersion}\n\n" +
                        $"Error: {updateInfo.ErrorMessage}",
                        "Check for Update",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return;
                }

                if (updateInfo.HasUpdate)
                {
                    var result = MessageBox.Show(
                        $"New Version Available!\n\n" +
                        $"Current: {updateInfo.CurrentVersion}\n" +
                        $"Latest: {updateInfo.LatestVersion}\n\n" +
                        $"{updateInfo.ReleaseNotes}\n\n" +
                        $"Download and install the latest version?",
                        "Update Available",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information
                    );

                    if (result == MessageBoxResult.Yes && !string.IsNullOrEmpty(updateInfo.DownloadUrl))
                    {
                        // Download dan install secara langsung
                        await DownloadAndInstallUpdateAsync(updateInfo.DownloadUrl);
                    }
                }
                else
                {
                    MessageBox.Show(
                        $"Current Version: {updateInfo.CurrentVersion}\n" +
                        $"Latest: {updateInfo.LatestVersion}\n\n" +
                        "You are already on the latest version!",
                        "Check for Update",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to check for updates: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isCheckingUpdate = false;
                
                // Re-enable button
                Dispatcher.Invoke(() =>
                {
                    if (_checkUpdateButton != null)
                    {
                        _checkUpdateButton.IsEnabled = true;
                    }
                });
            }
        }

        private async Task DownloadAndInstallUpdateAsync(string downloadUrl)
        {
            try
            {
                MessageBox.Show(
                    "Starting download. This may take a few moments...",
                    "Downloading Update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );

                // Lakukan download dengan callback progress
                var result = await UpdateChecker.DownloadAndInstallUpdateAsync(
                    downloadUrl,
                    (current, total) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (total > 0)
                            {
                                int percentage = (int)((current * 100) / total);
                                // Update dapat ditambahkan di sini jika perlu
                            }
                        });
                    }
                );

                if (result.success)
                {
                    MessageBox.Show(
                        "Update has been downloaded and installer has been started.\n\n" +
                        "Please follow the installation wizard to complete the update.",
                        "Update Starting",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
                else
                {
                    MessageBox.Show(
                        $"Failed to download and install update:\n{result.message}",
                        "Update Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during update: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
