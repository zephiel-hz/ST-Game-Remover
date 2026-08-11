using System;
using System.IO;
using System.Windows;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    public partial class SettingsWindow : Window
    {
        

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

                // Load hardware acceleration setting
                LoadHardwareAccelerationSetting();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadHardwareAccelerationSetting()
        {
            try
            {
                var savedPreference = HardwareAccelerationPreferences.GetSavedHardwareAccelerationPreference();
                // Default to true (enabled) if no preference is saved
                HardwareAccelerationCheckBox.IsChecked = savedPreference ?? true;
                
                // Add event handler after loading to prevent firing on initial load
                HardwareAccelerationCheckBox.Checked += HardwareAccelerationCheckBox_Changed;
                HardwareAccelerationCheckBox.Unchecked += HardwareAccelerationCheckBox_Changed;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load hardware acceleration setting: {ex.Message}");
            }
        }

        private void HardwareAccelerationCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                bool isEnabled = HardwareAccelerationCheckBox.IsChecked ?? true;
                HardwareAccelerationPreferences.SaveHardwareAccelerationPreference(isEnabled);
                
                MessageBox.Show(
                    "Hardware acceleration setting saved.\nPlease restart the application for changes to take effect.",
                    "Settings Saved",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save hardware acceleration setting: {ex.Message}");
                MessageBox.Show($"Failed to save setting: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            catch (Exception)
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
                    "Clear unused/expired cache?\nThis will remove cached game data that is expired (365+ days old) or incomplete.\nValid cached items will be preserved.",
                    "Clear Cache",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result == MessageBoxResult.Yes)
                {
                    // Use CacheManager.ClearCache() to handle cache clearing properly
                    CacheManager.ClearCache();
                    
                    // Show result
                    int cacheSize = CacheManager.GetCacheSize();
                    MessageBox.Show(
                        $"Cache cleared successfully.\nRemaining cache items: {cacheSize}",
                        "Cache Cleared",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error clearing cache: {ex.Message}");
                MessageBox.Show(
                    $"Error clearing cache: {ex.Message}",
                    "Cache Clear Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Close Settings and return to Dashboard
            this.Hide();
            WindowNavigator.OpenDashboard();
        }

        // Window Controls
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                this.DragMove();
        }

        private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
