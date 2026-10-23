using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SteamPluginManager.Models;
using SteamPluginManager.Services.Steam;

namespace SteamPluginManager.UI.Dialogs
{
    public partial class CustomLaunchDialog : Window
    {
        private readonly GameEntry _game;
        private string? _resolvedGameIcon;

        public bool ConfigChanged { get; private set; }

        public CustomLaunchDialog(GameEntry game)
        {
            InitializeComponent();
            _game = game ?? throw new ArgumentNullException(nameof(game));

            GameNameBadge.Text = $"{_game.Name} ({_game.AppId})";
            ShortcutNamePreview.Text = $"{string.Join("_", _game.Name.Split(Path.GetInvalidFileNameChars()))}.lnk";

            Loaded += CustomLaunchDialog_Loaded;
        }

        private void CustomLaunchDialog_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_game.IsInstalled || string.IsNullOrWhiteSpace(_game.InstallationPath) || !Directory.Exists(_game.InstallationPath))
            {
                ModernMessageBox.Show(
                    $"Game '{_game.Name}' is not installed.\n\nPlease install the game first before configuring Custom Launch.",
                    "Game Not Installed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Close();
                return;
            }

            LoadConfigAndPopulateFiles();
        }

        private void LoadConfigAndPopulateFiles()
        {
            try
            {
                // Scan game folder for executables and batch scripts
                var launchables = CustomLaunchService.FindGameLaunchables(_game.InstallationPath);
                DetectedFilesComboBox.ItemsSource = launchables;

                // Load existing configuration
                var existingConfig = CustomLaunchService.GetConfig(_game.AppId, _game.InstallationPath);

                if (existingConfig != null && existingConfig.IsEnabled && !string.IsNullOrWhiteSpace(existingConfig.ExecutablePath))
                {
                    CustomLaunchRadio.IsChecked = true;
                    ExecutablePathTextBox.Text = existingConfig.ExecutablePath;
                    ArgumentsTextBox.Text = existingConfig.Arguments;
                    WorkingDirTextBox.Text = existingConfig.WorkingDirectory;

                    // Match selection in ComboBox
                    var matched = launchables.FirstOrDefault(l =>
                        string.Equals(l.FullPath, existingConfig.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(existingConfig.RelativePath) && string.Equals(l.RelativePath, existingConfig.RelativePath, StringComparison.OrdinalIgnoreCase)));

                    if (matched != null)
                    {
                        DetectedFilesComboBox.SelectedItem = matched;
                    }
                }
                else
                {
                    DefaultLaunchRadio.IsChecked = true;

                    // Pre-select primary file if user later switches to custom
                    if (launchables.Count > 0)
                    {
                        DetectedFilesComboBox.SelectedIndex = 0;
                    }
                }

                // Resolve game icon for shortcut preview
                _resolvedGameIcon = CustomLaunchService.ResolveGameOriginalIcon(_game.AppId, _game.Name, _game.InstallationPath, existingConfig?.OriginalIconPath);
                UpdateShortcutIconPreview();
                UpdateUiState();
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomLaunchDialog] Error initializing: {ex.Message}");
            }
        }

        private void UpdateShortcutIconPreview()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_resolvedGameIcon) && File.Exists(_resolvedGameIcon))
                {
                    string ext = Path.GetExtension(_resolvedGameIcon).ToLowerInvariant();
                    if (ext is ".ico" or ".png" or ".jpg" or ".jpeg")
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(_resolvedGameIcon, UriKind.Absolute);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        ShortcutIconPreview.Source = bmp;
                        return;
                    }
                    else if (ext is ".exe")
                    {
                        IntPtr hIcon = ExtractIcon(IntPtr.Zero, _resolvedGameIcon, 0);
                        if (hIcon != IntPtr.Zero)
                        {
                            try
                            {
                                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                                    hIcon,
                                    Int32Rect.Empty,
                                    BitmapSizeOptions.FromEmptyOptions());
                                source.Freeze();
                                ShortcutIconPreview.Source = source;
                                return;
                            }
                            finally
                            {
                                DestroyIcon(hIcon);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomLaunchDialog] Error loading icon preview: {ex.Message}");
            }

            // Fallback placeholder
            ShortcutIconPreview.Source = null;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private void UpdateUiState()
        {
            bool isCustom = CustomLaunchRadio.IsChecked == true;
            CustomConfigPanel.IsEnabled = isCustom;
            CustomConfigPanel.Opacity = isCustom ? 1.0 : 0.45;

            // Highlight active card border
            if (isCustom)
            {
                CustomModeCard.BorderBrush = (Brush)FindResource("AccentBrush");
                CustomModeCard.Background = new SolidColorBrush(Color.FromArgb(20, 249, 115, 22));

                DefaultModeCard.BorderBrush = (Brush)FindResource("BorderBrush");
                DefaultModeCard.Background = (Brush)FindResource("BackgroundBrush");
            }
            else
            {
                DefaultModeCard.BorderBrush = (Brush)FindResource("AccentBrush");
                DefaultModeCard.Background = new SolidColorBrush(Color.FromArgb(20, 249, 115, 22));

                CustomModeCard.BorderBrush = (Brush)FindResource("BorderBrush");
                CustomModeCard.Background = (Brush)FindResource("BackgroundBrush");
            }
        }

        private void LaunchModeRadio_Changed(object sender, RoutedEventArgs e)
        {
            UpdateUiState();
        }

        private void DefaultModeCard_Click(object sender, MouseButtonEventArgs e)
        {
            DefaultLaunchRadio.IsChecked = true;
        }

        private void CustomModeCard_Click(object sender, MouseButtonEventArgs e)
        {
            CustomLaunchRadio.IsChecked = true;
        }

        private void DetectedFilesComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (DetectedFilesComboBox.SelectedItem is LaunchableFileInfo selected)
            {
                ExecutablePathTextBox.Text = selected.FullPath;
                if (string.IsNullOrWhiteSpace(WorkingDirTextBox.Text))
                {
                    WorkingDirTextBox.Text = Path.GetDirectoryName(selected.FullPath) ?? "";
                }
            }
        }

        private static string? GetSafeDirectoryPath(params string?[] candidatePaths)
        {
            foreach (var raw in candidatePaths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    string clean = raw.Trim().Replace('/', '\\');
                    if (Directory.Exists(clean))
                    {
                        return Path.GetFullPath(clean);
                    }
                }
                catch { }
            }
            return null;
        }

        private void BrowseFileBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? initialDir = GetSafeDirectoryPath(
                    !string.IsNullOrWhiteSpace(ExecutablePathTextBox.Text) ? Path.GetDirectoryName(ExecutablePathTextBox.Text.Trim()) : null,
                    _game.InstallationPath);

                var dialog = new OpenFileDialog
                {
                    Title = $"Select Game Executable or Script for {_game.Name}",
                    Filter = "Launchable Files (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|Executable Files (*.exe)|*.exe|Batch Files (*.bat;*.cmd)|*.bat;*.cmd|All Files (*.*)|*.*",
                };

                if (!string.IsNullOrWhiteSpace(initialDir))
                {
                    dialog.InitialDirectory = initialDir;
                }

                bool? result;
                try
                {
                    result = dialog.ShowDialog(this);
                }
                catch (ArgumentException)
                {
                    // Fallback if Windows Shell rejects the initial directory (e.g. COM shell path error)
                    dialog.InitialDirectory = null;
                    result = dialog.ShowDialog(this);
                }

                if (result == true)
                {
                    ExecutablePathTextBox.Text = dialog.FileName;
                    if (string.IsNullOrWhiteSpace(WorkingDirTextBox.Text))
                    {
                        WorkingDirTextBox.Text = Path.GetDirectoryName(dialog.FileName) ?? "";
                    }

                    if (DetectedFilesComboBox.ItemsSource is System.Collections.Generic.List<LaunchableFileInfo> list)
                    {
                        var matched = list.FirstOrDefault(x => string.Equals(x.FullPath, dialog.FileName, StringComparison.OrdinalIgnoreCase));
                        if (matched != null)
                        {
                            DetectedFilesComboBox.SelectedItem = matched;
                        }
                        else
                        {
                            var newItem = new LaunchableFileInfo
                            {
                                DisplayName = Path.GetFileName(dialog.FileName),
                                FullPath = dialog.FileName,
                                RelativePath = !string.IsNullOrWhiteSpace(_game.InstallationPath) && dialog.FileName.StartsWith(_game.InstallationPath, StringComparison.OrdinalIgnoreCase)
                                    ? Path.GetRelativePath(_game.InstallationPath, dialog.FileName)
                                    : Path.GetFileName(dialog.FileName),
                                IsBatch = dialog.FileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || dialog.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase),
                                IsPrimary = false
                            };
                            var updatedList = new System.Collections.Generic.List<LaunchableFileInfo>(list) { newItem };
                            DetectedFilesComboBox.ItemsSource = updatedList;
                            DetectedFilesComboBox.SelectedItem = newItem;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to open file picker: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BrowseWorkingDirBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? initialDir = GetSafeDirectoryPath(
                    WorkingDirTextBox.Text,
                    !string.IsNullOrWhiteSpace(ExecutablePathTextBox.Text) ? Path.GetDirectoryName(ExecutablePathTextBox.Text.Trim()) : null,
                    _game.InstallationPath);

                var dialog = new OpenFolderDialog
                {
                    Title = "Select Working Directory"
                };

                if (!string.IsNullOrWhiteSpace(initialDir))
                {
                    dialog.InitialDirectory = initialDir;
                }

                bool? result;
                try
                {
                    result = dialog.ShowDialog(this);
                }
                catch (ArgumentException)
                {
                    // Fallback if Windows Shell rejects the initial directory
                    dialog.InitialDirectory = null;
                    result = dialog.ShowDialog(this);
                }

                if (result == true)
                {
                    WorkingDirTextBox.Text = dialog.FolderName;
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to open folder picker: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TestLaunchBtn_Click(object sender, RoutedEventArgs e)
        {
            if (CustomLaunchRadio.IsChecked == true)
            {
                string path = ExecutablePathTextBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    ModernMessageBox.Show("Please select a valid executable or batch file first.", "Invalid File", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Temporarily build test config
                var testConfig = new GameCustomLaunchConfig
                {
                    AppId = _game.AppId,
                    IsEnabled = true,
                    ExecutablePath = path,
                    Arguments = ArgumentsTextBox.Text.Trim(),
                    WorkingDirectory = WorkingDirTextBox.Text.Trim()
                };

                CustomLaunchService.SaveConfig(testConfig, _game.InstallationPath);
                var (success, msg) = CustomLaunchService.LaunchGame(_game.AppId, _game.InstallationPath);

                if (!success)
                {
                    ModernMessageBox.Show($"Failed to test launch: {msg}", "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                SteamHelper.PlayGame(_game.AppId);
            }
        }

        private void ResetDefaultBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = ModernMessageBox.Show(
                $"Reset launch configuration for '{_game.Name}' to Steam default?",
                "Reset Launch Configuration",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                CustomLaunchService.RemoveConfig(_game.AppId);
                ConfigChanged = true;
                DefaultLaunchRadio.IsChecked = true;
                ExecutablePathTextBox.Text = string.Empty;
                ArgumentsTextBox.Text = string.Empty;
                WorkingDirTextBox.Text = string.Empty;
                UpdateUiState();
                ModernMessageBox.Show("Custom launch has been reset to Steam default.", "Reset Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (CustomLaunchRadio.IsChecked == true)
            {
                if (!_game.IsInstalled || string.IsNullOrWhiteSpace(_game.InstallationPath) || !Directory.Exists(_game.InstallationPath))
                {
                    ModernMessageBox.Show(
                        $"Cannot configure custom launch because '{_game.Name}' is not installed.",
                        "Game Not Installed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                string targetPath = ExecutablePathTextBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
                {
                    ModernMessageBox.Show("Please select a valid .exe, .bat, or .cmd file.", "File Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var config = new GameCustomLaunchConfig
                {
                    AppId = _game.AppId,
                    IsEnabled = true,
                    ExecutablePath = targetPath,
                    Arguments = ArgumentsTextBox.Text.Trim(),
                    WorkingDirectory = WorkingDirTextBox.Text.Trim(),
                    OriginalIconPath = _resolvedGameIcon
                };

                CustomLaunchService.SaveConfig(config, _game.InstallationPath);
                ConfigChanged = true;

                ModernMessageBox.Show(
                    $"Custom launch configured for '{_game.Name}'!\n\nTarget: {Path.GetFileName(targetPath)}\nPLAY button and Shortcut will now follow this configuration.",
                    "Configuration Saved",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                CustomLaunchService.RemoveConfig(_game.AppId);
                ConfigChanged = true;

                ModernMessageBox.Show(
                    $"Launch configured for '{_game.Name}' using Steam default.",
                    "Configuration Saved",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }
}
