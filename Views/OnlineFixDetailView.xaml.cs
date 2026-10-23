using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using SteamPluginManager.Models;
using SteamPluginManager.Services.Steam;
using SteamPluginManager.UI.Dialogs;

namespace SteamPluginManager.Views
{
    public partial class OnlineFixDetailView : UserControl
    {
        private readonly string _thumbnailCacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamPluginManager", "ThumbnailCache");

        private OnlineFixFile? _currentFile;
        private CustomLaunchTutorial? _currentTutorial;
        private System.Windows.Threading.DispatcherTimer? _validationMonitorTimer;
        private bool _isActionInProgress = false;
        
        public OnlineFixDetailView()
        {
            try
            {
                Directory.CreateDirectory(_thumbnailCacheFolder);
                InitializeComponent();
            }
            catch
            {
                // Log error
            }
            Loaded += OnlineFixDetailView_Loaded;
            Unloaded += OnlineFixDetailView_Unloaded;
            SizeChanged += OnlineFixDetailView_SizeChanged;
        }

        private void OnlineFixDetailView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                RegisterTextElements();
                DisplayFileDetails();
                UpdateLayoutMode(ActualWidth < 680);
                StartValidationMonitor();
                CheckSteamValidationState();
            }
            catch
            {
                // Log error
            }
        }

        private void OnlineFixDetailView_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                StopValidationMonitor();
            }
            catch { }
        }

        private void StartValidationMonitor()
        {
            if (_validationMonitorTimer == null)
            {
                _validationMonitorTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1500)
                };
                _validationMonitorTimer.Tick += (s, e) =>
                {
                    CheckSteamValidationState();
                };
            }
            _validationMonitorTimer.Start();
        }

        private void StopValidationMonitor()
        {
            _validationMonitorTimer?.Stop();
        }

        private void OnlineFixDetailView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool isCompact = e.NewSize.Width < 680;
            UpdateLayoutMode(isCompact);
        }

        private void UpdateLayoutMode(bool isCompact)
        {
            try
            {
                if (FindName("BackButton") is Button backBtn)
                {
                    backBtn.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
                }
                if (FindName("DetailContentPanel") is StackPanel contentPanel)
                {
                    contentPanel.Margin = isCompact ? new Thickness(14, 12, 14, 16) : new Thickness(24, 20, 24, 24);
                }
                if (FindName("ThumbnailBorder") is Border thumbBorder)
                {
                    thumbBorder.MaxHeight = isCompact ? 170 : 320;
                }
            }
            catch
            {
                // Ignore layout errors
            }
        }
        public void SetFile(OnlineFixFile file)
        {
            _currentFile = file;
            if (IsLoaded)
            {
                DisplayFileDetails();
                CheckSteamValidationState();
            }
        }

        private void RegisterTextElements()
        {
            try
            {
                if (FindName("AddToLibraryButtonText") is TextBlock addText)
                    addText.Text = "Apply Online Fix";
                else if (FindName("AddToLibraryButton") is Button addToLibraryButton)
                    addToLibraryButton.Content = "Apply Online Fix";
            }
            catch
            {
                // Log error
            }
        }

        private void DisplayFileDetails()
        {
            try
            {
                if (_currentFile == null)
                {
                    ModernMessageBox.Show("No file selected", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                _ = LoadBannerAsync();
                _ = LoadTutorialAsync();
                if (FindName("GameNameDetail") is TextBlock gameNameDetail)
                    gameNameDetail.Text = _currentFile.Name ?? "Unknown Fix";
                if (FindName("GenreDetail") is TextBlock genreDetail)
                    genreDetail.Text = _currentFile.Genre ?? "N/A";
                if (FindName("DescriptionDetail") is TextBlock descriptionDetail)
                    descriptionDetail.Text = _currentFile.Description ?? "No description available";
                UpdateAddToLibraryButton();
            }
            catch
            {
                ModernMessageBox.Show($"Error displaying details.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateAddToLibraryButton()
        {
            try
            {
                if (_currentFile == null)
                    return;

                if (FindName("AddToLibraryButtonText") is TextBlock addText)
                {
                    addText.Text = "Apply Online Fix";
                }
                else if (FindName("AddToLibraryButton") is Button addBtn)
                {
                    addBtn.Content = "Apply Online Fix";
                }

                if (FindName("AddToLibraryButton") is Button btn)
                {
                    btn.IsEnabled = true;
                    btn.ToolTip = null;
                }

                if (FindName("RestoreGameButtonText") is TextBlock restoreText)
                {
                    restoreText.Text = "Restore Original Game";
                }
                else if (FindName("RestoreGameButton") is Button restoreBtn)
                {
                    restoreBtn.Content = "Restore Original Game";
                }

                if (FindName("RestoreGameButton") is Button rBtn)
                {
                    rBtn.IsEnabled = true;
                    rBtn.ToolTip = null;
                }

                CheckSteamValidationState();
            }
            catch { }
        }

        private void CheckSteamValidationState()
        {
            try
            {
                if (_isActionInProgress || _currentFile == null || !_currentFile.AppId.HasValue || _currentFile.AppId.Value <= 0)
                    return;

                int appId = _currentFile.AppId.Value;
                bool isCurrentlyValidating = SteamHelper.IsGameValidating(appId);

                if (isCurrentlyValidating)
                {
                    // Hide both buttons while validating so only the validation status badge is displayed
                    if (FindName("AddToLibraryButton") is Button addBtn)
                    {
                        addBtn.Visibility = Visibility.Collapsed;
                        addBtn.IsEnabled = false;
                        addBtn.ToolTip = "Steam is currently validating / verifying files for this game. Please wait until verification completes.";
                    }

                    if (FindName("RestoreGameButton") is Button restoreBtn)
                    {
                        restoreBtn.Visibility = Visibility.Collapsed;
                        restoreBtn.IsEnabled = false;
                        restoreBtn.ToolTip = "Steam file verification is currently in progress.";
                    }

                    if (FindName("ValidationStatusBadge") is Border badge)
                    {
                        badge.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    // Hide validation status badge
                    if (FindName("ValidationStatusBadge") is Border badge)
                    {
                        badge.Visibility = Visibility.Collapsed;
                    }

                    // Show both buttons and ensure they are active
                    if (FindName("AddToLibraryButton") is Button addBtn)
                    {
                        addBtn.Visibility = Visibility.Visible;
                        addBtn.IsEnabled = true;
                        addBtn.ToolTip = null;
                    }

                    if (FindName("AddToLibraryButtonText") is TextBlock addText)
                    {
                        addText.Text = "Apply Online Fix";
                    }
                    else if (FindName("AddToLibraryButton") is Button addBtnFallback && addBtnFallback.Content is string)
                    {
                        addBtnFallback.Content = "Apply Online Fix";
                    }

                    if (FindName("RestoreGameButton") is Button restoreBtn)
                    {
                        restoreBtn.Visibility = Visibility.Visible;
                        restoreBtn.IsEnabled = true;
                        restoreBtn.ToolTip = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[OnlineFixDetailView] Error checking validation state: {ex.Message}");
            }
        }

        private string GetCurrentFileSizeDetail()
        {
            if (_currentFile == null || string.IsNullOrWhiteSpace(_currentFile.FileSize))
                return string.Empty;

            return $"File size: {_currentFile.FileSize}";
        }

        private void ShowLoadingState(bool show, string status = "Processing...", string detail = "")
        {
            try
            {
                var modal = WindowNavigator.GetProgressModal();
                if (modal != null)
                {
                    if (show)
                    {
                        modal.Configure(
                            "Applying Online Fix",
                            "Verify File",
                            "Download Fix",
                            "Extract Files",
                            "Apply to Game",
                            "\uE774",
                            gameTitle: _currentFile?.Name,
                            appId: _currentFile?.AppId?.ToString());
                        modal.ShowModal(status, detail);
                    }
                    else
                    {
                        modal.HideModal();
                    }
                    return;
                }

                if (FindName("ProgressOverlay") is Grid overlay)
                    overlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

                if (FindName("ProgressTitle") is TextBlock progressTitle)
                    progressTitle.Text = "Applying Online Fix...";

                if (show)
                {
                    if (FindName("ProgressStatus") is TextBlock statusText)
                        statusText.Text = status;
                    if (FindName("ProgressDetail") is TextBlock detailText)
                    {
                        detailText.Text = detail;
                        detailText.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
                    }
                    if (FindName("ProgressBar") is ProgressBar progressBar)
                    {
                        progressBar.Visibility = Visibility.Visible;
                        progressBar.Minimum = 0;
                        progressBar.Maximum = 100;
                        progressBar.Value = 0;
                        progressBar.IsIndeterminate = true;
                    }
                    
                    // Reset result notification and steps panel
                    if (FindName("ResultNotification") is Border resultBorder)
                        resultBorder.Visibility = Visibility.Collapsed;
                    
                    if (FindName("StepsPanel") is StackPanel stepsPanel)
                        stepsPanel.Visibility = Visibility.Visible;
                    
                    // Reset all step icons to initial state
                    UpdateProgressStep(1);
                }
                else
                {
                    if (FindName("ProgressDetail") is TextBlock detailText)
                        detailText.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void ShowResultNotification(bool isSuccess, string message)
        {
            try
            {
                var modal = WindowNavigator.GetProgressModal();
                if (modal != null)
                {
                    modal.ShowResultNotification(isSuccess, message);
                    return;
                }

                if (FindName("ResultNotification") is Border resultBorder)
                {
                    resultBorder.Visibility = Visibility.Visible;

                    if (isSuccess)
                    {
                        resultBorder.BorderBrush = FindResource("SuccessButtonBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Green;
                        if (FindName("ResultIcon") is TextBlock resultIcon)
                            resultIcon.Text = "\uE73E";
                    }
                    else
                    {
                        resultBorder.BorderBrush = FindResource("DangerButtonBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Red;
                        if (FindName("ResultIcon") is TextBlock resultIcon)
                            resultIcon.Text = "\uE711";
                    }

                    if (FindName("ResultMessage") is TextBlock resultMsg)
                        resultMsg.Text = message;

                    if (FindName("ProgressBar") is ProgressBar progressBar)
                        progressBar.Visibility = Visibility.Collapsed;
                    if (FindName("StepsPanel") is StackPanel stepsPanel)
                        stepsPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void UpdateProgressStep(int stepNumber, bool completed = false)
        {
            try
            {
                var modal = WindowNavigator.GetProgressModal();
                if (modal != null)
                {
                    modal.UpdateProgressStep(stepNumber, completed);
                    return;
                }

                string[] stepIcons = new[] { "Step1Icon", "Step2Icon", "Step3Icon", "Step4Icon" };

                for (int i = 1; i <= 4; i++)
                {
                    if (FindName(stepIcons[i - 1]) is TextBlock stepIcon)
                    {
                        if (i < stepNumber)
                        {
                            stepIcon.Text = "\uE73E";
                            stepIcon.Foreground = FindResource("SuccessButtonBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Green;
                        }
                        else if (i == stepNumber && stepNumber <= 4)
                        {
                            stepIcon.Text = "\uE7BA";
                            stepIcon.Foreground = FindResource("PrimaryButtonBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
                        }
                        else
                        {
                            stepIcon.Text = "\uE7BA";
                            stepIcon.Foreground = FindResource("MutedForegroundBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
                        }
                    }
                }
            }
            catch { }
        }

        private async void AddToLibrary_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentFile == null)
                {
                    ModernMessageBox.Show("No file selected", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (_currentFile.AppId.HasValue && SteamHelper.IsGameValidating(_currentFile.AppId.Value))
                {
                    ModernMessageBox.Show(
                        $"Steam is currently verifying and validating files for '{_currentFile.Name}'.\n\n" +
                        "Please wait until Steam finishes validating game files before applying the Online Fix.",
                        "Steam Validation In Progress",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    CheckSteamValidationState();
                    return;
                }

                _isActionInProgress = true;
                if (FindName("AddToLibraryButton") is Button addBtn)
                    addBtn.IsEnabled = false;
                if (FindName("RestoreGameButton") is Button restoreBtn)
                    restoreBtn.IsEnabled = false;

                // Cek apakah game sudah terinstal via SteamHelper (direct ACF + common + manifest check)
                string? gameFolder = null;
                if (_currentFile.AppId.HasValue && _currentFile.AppId.Value > 0)
                {
                    gameFolder = SteamHelper.GetGameInstallationPath(_currentFile.AppId.Value, _currentFile.Name);
                }

                // Fallback: If not found automatically, prompt user to select folder manually
                if (string.IsNullOrEmpty(gameFolder) || !Directory.Exists(gameFolder))
                {
                    var browsePrompt = ModernMessageBox.Show(
                        $"The game installation folder for '{_currentFile.Name}' was not detected automatically in your Steam libraries.\n\n" +
                        "Would you like to manually locate and select your game directory?",
                        "Game Folder Not Found",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (browsePrompt == MessageBoxResult.Yes)
                    {
                        var folderDialog = new Microsoft.Win32.OpenFolderDialog
                        {
                            Title = $"Select Game Installation Directory for '{_currentFile.Name}'"
                        };
                        if (folderDialog.ShowDialog() == true && Directory.Exists(folderDialog.FolderName))
                        {
                            gameFolder = folderDialog.FolderName;
                        }
                    }
                }

                bool isInstalled = !string.IsNullOrEmpty(gameFolder) && Directory.Exists(gameFolder);

                if (!isInstalled)
                {
                    ShowLoadingState(true, "Game not installed!");
                    ShowResultNotification(false, "✗ Game not installed!");
                    await Task.Delay(1500);
                    ShowLoadingState(false);
                    if (FindName("AddToLibraryButton") is Button btnError)
                        btnError.IsEnabled = true;
                    if (FindName("RestoreGameButton") is Button btnRestore)
                        btnRestore.IsEnabled = true;
                    return;
                }

                // Download file dari Backblaze B2
                string r2Key = _currentFile.FileName;
                string tempZip = Path.Combine(Path.GetTempPath(), $"onlinefix_{Guid.NewGuid()}.zip");
                ShowLoadingState(true, "Downloading Online-Fix archive...", GetCurrentFileSizeDetail());
                UpdateProgressStep(2);
                try
                {
                    var progress = new Progress<int>(p =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            string sizeInfo = !string.IsNullOrWhiteSpace(_currentFile?.FileSize)
                                ? $"{_currentFile.FileSize} • {p}% complete"
                                : $"{p}% complete";

                            var modal = WindowNavigator.GetProgressModal();
                            if (modal != null)
                            {
                                modal.SetProgress(p, 100, false);
                                modal.UpdateStatus("Downloading Online-Fix archive...", sizeInfo);
                            }

                            if (FindName("ProgressBar") is ProgressBar pb)
                            {
                                pb.IsIndeterminate = false;
                                pb.Value = p;
                            }
                            if (FindName("ProgressStatus") is TextBlock statusText)
                                statusText.Text = $"Downloading ({p}%)...";
                        });
                    });

                    byte[] zipBytes = await SteamPluginManager.B2Config.DownloadFileAsync(r2Key, progress);
                    await File.WriteAllBytesAsync(tempZip, zipBytes);
                }
                catch
                {
                    ShowResultNotification(false, "✗ Download failed. Please check internet connection.");
                    await Task.Delay(1500);
                    ShowLoadingState(false);
                    if (FindName("AddToLibraryButton") is Button btnError)
                        btnError.IsEnabled = true;
                    if (FindName("RestoreGameButton") is Button btnRestore)
                        btnRestore.IsEnabled = true;
                    return;
                }

                // Ekstrak file ZIP
                ShowLoadingState(true, "Extracting files...", "Unpacking archive to temporary folder...");
                UpdateProgressStep(3);
                string extractDir = Path.Combine(Path.GetTempPath(), $"onlinefix_extract_{Guid.NewGuid()}");
                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, extractDir);

                // Resolve effective content directory (in case archive has a single root wrapper directory)
                string effectiveExtractDir = SteamPluginManager.Services.ResourcePipeline.OnlineFixResourceService.ResolveEffectiveContentDirectory(extractDir, _currentFile?.FileName, _currentFile?.Name);

                // Cari target folder yang tepat berdasarkan hasil ekstrak
                ShowLoadingState(true, "Detecting target directory...", "Matching extracted files with game folder structure...");
                string? targetFolder = FindBestTargetFolder(gameFolder!, effectiveExtractDir);
                if (string.IsNullOrEmpty(targetFolder))
                {
                    ShowResultNotification(false, "✗ Cannot determine target game directory.");
                    await Task.Delay(1500);
                    ShowLoadingState(false);
                    if (FindName("AddToLibraryButton") is Button btnExeError)
                        btnExeError.IsEnabled = true;
                    if (FindName("RestoreGameButton") is Button btnRestore)
                        btnRestore.IsEnabled = true;
                    return;
                }

                // Copy file hasil ekstrak ke target folder
                UpdateProgressStep(4);
                var allExtractedFiles = Directory.GetFiles(effectiveExtractDir, "*", SearchOption.AllDirectories);
                var installedRelPaths = new List<string>();
                int fileIndex = 0;
                foreach (var file in allExtractedFiles)
                {
                    fileIndex++;
                    string relPath = Path.GetRelativePath(effectiveExtractDir, file);
                    installedRelPaths.Add(relPath);
                    string targetPath = Path.Combine(targetFolder, relPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                    double copyPct = (double)fileIndex / allExtractedFiles.Length * 100;
                    var modal = WindowNavigator.GetProgressModal();
                    if (modal != null)
                    {
                        modal.SetProgress(copyPct, 100, false);
                        modal.UpdateStatus($"Applying files to game directory ({fileIndex}/{allExtractedFiles.Length})...", relPath);
                    }

                    bool fileExists = File.Exists(targetPath);
                    File.Copy(file, targetPath, true);

                    if (fileExists)
                    {
                        Logger.Log($"[OnlineFixDetailView] Replaced existing file: {relPath}");
                    }
                    else
                    {
                        Logger.Log($"[OnlineFixDetailView] Added new file: {relPath}");
                    }
                }

                // Save installation tracking manifest for 100% accurate restore
                if (_currentFile != null && _currentFile.AppId.HasValue && !string.IsNullOrEmpty(targetFolder))
                {
                    OnlineFixTrackerService.SaveInstallRecord(_currentFile.AppId.Value, _currentFile.Name ?? "Steam Game", targetFolder, installedRelPaths);
                }

                try { File.Delete(tempZip); } catch { }
                try { Directory.Delete(extractDir, true); } catch { }

                UpdateProgressStep(4, true);
                Dispatcher.Invoke(() => ShowResultNotification(true, $"✓ Successfully applied Online-Fix to '{_currentFile?.Name ?? "Steam Game"}'!"));
                await Task.Delay(1600);
                ShowLoadingState(false);
            }
            catch
            {
                ShowResultNotification(false, $"✗ Error applying online fix.");
                await Task.Delay(1500);
                ShowLoadingState(false);
            }
            finally
            {
                _isActionInProgress = false;
                if (FindName("AddToLibraryButton") is Button addBtn)
                {
                    addBtn.IsEnabled = true;
                    addBtn.ToolTip = null;
                }
                if (FindName("RestoreGameButton") is Button restoreBtn)
                {
                    restoreBtn.IsEnabled = true;
                    restoreBtn.ToolTip = null;
                }
                CheckSteamValidationState();
            }
        }

        private async void RestoreGame_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentFile == null || !_currentFile.AppId.HasValue || _currentFile.AppId.Value <= 0)
                {
                    ModernMessageBox.Show("No valid game selected for restoration.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                int appId = _currentFile.AppId.Value;
                string gameTitle = _currentFile.Name ?? "Steam Game";

                if (SteamHelper.IsGameValidating(appId))
                {
                    ModernMessageBox.Show(
                        $"Steam is already verifying and validating files for '{gameTitle}'.\n\n" +
                        "Please wait for the current verification process to finish.",
                        "Steam Validation In Progress",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    CheckSteamValidationState();
                    return;
                }

                _isActionInProgress = true;

                // Check if game is installed via SteamHelper
                string? gameFolder = SteamHelper.GetGameInstallationPath(appId, gameTitle);
                if (string.IsNullOrEmpty(gameFolder) || !Directory.Exists(gameFolder))
                {
                    var cachedRecord = OnlineFixTrackerService.GetInstallRecord(appId);
                    if (cachedRecord != null && !string.IsNullOrEmpty(cachedRecord.TargetFolder) && Directory.Exists(cachedRecord.TargetFolder))
                    {
                        gameFolder = cachedRecord.TargetFolder;
                    }
                }

                if (string.IsNullOrEmpty(gameFolder) || !Directory.Exists(gameFolder))
                {
                    var browsePrompt = ModernMessageBox.Show(
                        $"The game installation folder for '{gameTitle}' was not detected automatically in your Steam libraries.\n\n" +
                        "Would you like to manually locate and select your game directory to restore?",
                        "Game Folder Not Found",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (browsePrompt == MessageBoxResult.Yes)
                    {
                        var folderDialog = new Microsoft.Win32.OpenFolderDialog
                        {
                            Title = $"Select Game Installation Directory for '{gameTitle}'"
                        };
                        if (folderDialog.ShowDialog() == true && Directory.Exists(folderDialog.FolderName))
                        {
                            gameFolder = folderDialog.FolderName;
                        }
                    }
                }

                bool isInstalled = !string.IsNullOrEmpty(gameFolder) && Directory.Exists(gameFolder);

                if (!isInstalled)
                {
                    ModernMessageBox.Show(
                        "This game is not currently installed or located.\n\nCannot restore files for a game that is not installed.",
                        "Game Not Installed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Check if game is running
                if (CustomLaunchService.IsGameRunning(appId, null, gameFolder))
                {
                    ModernMessageBox.Show(
                        "The game is currently running.\n\nPlease close the game first before restoring original game files.",
                        "Game Is Running",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Confirmation dialog
                var confirmResult = ModernMessageBox.Show(
                    $"Restore original game files for '{gameTitle}'?\n\n" +
                    "This operation will:\n" +
                    "• Remove all Online-Fix files and modifications from your game folder\n" +
                    "• Reset any Custom Launch configurations\n" +
                    "• Open Steam to verify file integrity and re-acquire official clean files\n\n" +
                    "Do you want to proceed?",
                    "Restore Original Game",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirmResult != MessageBoxResult.Yes)
                {
                    return;
                }

                if (FindName("AddToLibraryButton") is Button addBtn) addBtn.IsEnabled = false;
                if (FindName("RestoreGameButton") is Button restoreBtn) restoreBtn.IsEnabled = false;

                // Configure Progress Modal
                var modal = WindowNavigator.GetProgressModal();
                if (modal != null)
                {
                    modal.Configure(
                        "Restoring Original Game",
                        "Locate Files",
                        "Scan Fix Files",
                        "Remove Fix Files",
                        "Verify Integrity",
                        "\uE72C",
                        gameTitle: gameTitle,
                        appId: appId.ToString());
                    modal.ShowModal("Initializing restore process...", "Locating game installation directory...");
                }

                // Step 1: Locate Game Files
                UpdateProgressStep(1);
                await Task.Delay(300);
                UpdateProgressStep(1, true);

                // Step 2: Scan Fix Files
                UpdateProgressStep(2);
                if (modal != null)
                {
                    modal.UpdateStatus("Identifying Online-Fix files...", "Checking installation manifest...");
                }

                var filesToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var dirsToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string? targetFolder = null;

                var installRecord = OnlineFixTrackerService.GetInstallRecord(appId, gameFolder);
                if (installRecord != null && installRecord.InstalledRelativePaths.Count > 0)
                {
                    targetFolder = (!string.IsNullOrEmpty(installRecord.TargetFolder) && Directory.Exists(installRecord.TargetFolder))
                        ? installRecord.TargetFolder
                        : gameFolder!;
                    foreach (var relPath in installRecord.InstalledRelativePaths)
                    {
                        string fullPath = Path.Combine(targetFolder!, relPath);
                        if (File.Exists(fullPath))
                        {
                            filesToDelete.Add(fullPath);
                        }
                    }
                    Logger.Log($"[OnlineFixDetailView] Found {filesToDelete.Count} files to delete from manifest.");
                }
                else
                {
                    // Fallback: If no manifest exists, try to inspect the archive from B2
                    if (!string.IsNullOrWhiteSpace(_currentFile.FileName))
                    {
                        try
                        {
                            if (modal != null)
                            {
                                modal.UpdateStatus("Downloading fix archive...", "Inspecting archive contents to identify installed files...");
                            }

                            string tempZip = Path.Combine(Path.GetTempPath(), $"onlinefix_inspect_{Guid.NewGuid()}.zip");
                            var progress = new Progress<int>(p =>
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (modal != null)
                                    {
                                        modal.SetProgress(p, 100, false);
                                        modal.UpdateStatus("Downloading fix archive to inspect files...", $"{p}% complete");
                                    }
                                });
                            });

                            byte[] zipBytes = await SteamPluginManager.B2Config.DownloadFileAsync(_currentFile.FileName, progress);
                            await File.WriteAllBytesAsync(tempZip, zipBytes);

                            string inspectExtractDir = Path.Combine(Path.GetTempPath(), $"onlinefix_inspect_ext_{Guid.NewGuid()}");
                            Directory.CreateDirectory(inspectExtractDir);
                            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, inspectExtractDir);

                            string effectiveInspectDir = SteamPluginManager.Services.ResourcePipeline.OnlineFixResourceService.ResolveEffectiveContentDirectory(inspectExtractDir, _currentFile?.FileName, _currentFile?.Name);
                            targetFolder = FindBestTargetFolder(gameFolder!, effectiveInspectDir) ?? gameFolder!;

                            var extractedFiles = Directory.GetFiles(effectiveInspectDir, "*", SearchOption.AllDirectories);
                            foreach (var f in extractedFiles)
                            {
                                string relPath = Path.GetRelativePath(effectiveInspectDir, f);
                                string fullPath = Path.Combine(targetFolder!, relPath);
                                if (File.Exists(fullPath))
                                {
                                    filesToDelete.Add(fullPath);
                                }
                            }

                            try { File.Delete(tempZip); } catch { }
                            try { Directory.Delete(inspectExtractDir, true); } catch { }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[OnlineFixDetailView] Failed to download archive for inspection: {ex.Message}");
                        }
                    }
                }

                // Also run signature scan to catch any known Online-Fix files (OnlineFix.ini, OnlineFix64.dll, steam_settings, etc.)
                var (sigFiles, sigDirs) = OnlineFixTrackerService.ScanKnownOnlineFixSignatures(gameFolder!);
                foreach (var sf in sigFiles)
                {
                    filesToDelete.Add(sf);
                }
                foreach (var sd in sigDirs)
                {
                    dirsToDelete.Add(sd);
                }

                UpdateProgressStep(2, true);

                // Step 3: Remove Fix Files
                UpdateProgressStep(3);
                if (modal != null)
                {
                    modal.UpdateStatus("Removing Online-Fix files...", $"Deleting {filesToDelete.Count} files and modifications...");
                }

                int deletedCount = 0;
                int totalFiles = filesToDelete.Count;
                foreach (var filePath in filesToDelete)
                {
                    try
                    {
                        if (File.Exists(filePath))
                        {
                            var attr = File.GetAttributes(filePath);
                            if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                            {
                                File.SetAttributes(filePath, attr & ~FileAttributes.ReadOnly);
                            }
                            File.Delete(filePath);
                            deletedCount++;
                            Logger.Log($"[OnlineFixDetailView] Restored: Deleted {filePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[OnlineFixDetailView] Failed to delete file {filePath}: {ex.Message}");
                    }

                    if (modal != null && totalFiles > 0)
                    {
                        double pct = (double)deletedCount / totalFiles * 100.0;
                        modal.SetProgress(pct, 100, false);
                    }
                }

                // Delete known Online-Fix directories
                foreach (var dir in dirsToDelete)
                {
                    try
                    {
                        if (Directory.Exists(dir))
                        {
                            Directory.Delete(dir, true);
                            Logger.Log($"[OnlineFixDetailView] Restored: Deleted directory {dir}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[OnlineFixDetailView] Failed to delete directory {dir}: {ex.Message}");
                    }
                }

                // Clean empty directories in gameFolder
                OnlineFixTrackerService.CleanEmptySubdirectories(gameFolder!);

                // Reset Custom Launch if configured
                if (CustomLaunchService.IsCustomLaunchConfigured(appId, gameFolder!))
                {
                    CustomLaunchService.RemoveConfig(appId);
                    Logger.Log($"[OnlineFixDetailView] Reset Custom Launch configuration for AppID {appId}");
                }

                // Clean tracking record
                OnlineFixTrackerService.DeleteInstallRecord(appId, gameFolder);

                UpdateProgressStep(3, true);

                // Step 4: Trigger Steam Verify Integrity
                UpdateProgressStep(4);
                if (modal != null)
                {
                    modal.UpdateStatus("Triggering Steam file integrity verification...", "Opening Steam validate service...");
                }

                await Task.Delay(500);
                SteamHelper.VerifyGame(appId);
                Logger.Log($"[OnlineFixDetailView] Triggered Steam integrity validation for AppID {appId}");

                UpdateProgressStep(4, true);

                ShowResultNotification(true, $"✓ Online-Fix files removed! Steam is now verifying game file integrity.");
                await Task.Delay(2500);
                ShowLoadingState(false);
            }
            catch (Exception ex)
            {
                Logger.Log($"[OnlineFixDetailView] Error in RestoreGame_Click: {ex.Message}");
                ShowResultNotification(false, $"✗ Failed to restore game files: {ex.Message}");
                await Task.Delay(2000);
                ShowLoadingState(false);
            }
            finally
            {
                _isActionInProgress = false;
                CheckSteamValidationState();
            }
        }

        private void BackToDashboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WindowNavigator.NavigateToOnlineFix();
            }
            catch
            {
                // Log error or show message if needed
            }
        }

        private async Task LoadBannerAsync()
        {
            try
            {
                if (_currentFile == null)
                {
                    Logger.Log("[OnlineFixDetailView] CurrentFile is null");
                    if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                        thumbnailBorder.Visibility = Visibility.Collapsed;
                    return;
                }

                if (_currentFile.AppId == null || _currentFile.AppId == 0)
                {
                    Logger.Log($"[OnlineFixDetailView] Invalid AppId: {_currentFile.AppId}");
                    if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                        thumbnailBorder.Visibility = Visibility.Collapsed;
                    return;
                }

                Logger.Log($"[OnlineFixDetailView] Starting banner load for AppID: {_currentFile.AppId}");

                string cacheFileName = $"{_currentFile.AppId}_detail.jpg";
                string cachedPath = Path.Combine(_thumbnailCacheFolder, cacheFileName);

                if (File.Exists(cachedPath))
                {
                    try
                    {
                        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.UriSource = new Uri(cachedPath, UriKind.Absolute);
                        bitmap.EndInit();

                        Dispatcher.Invoke(() =>
                        {
                            if (FindName("GameThumbnail") is Image thumbnailImg)
                            {
                                thumbnailImg.Source = bitmap;
                                if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                {
                                    thumbnailBorder.Visibility = Visibility.Visible;
                                    Logger.Log($"[OnlineFixDetailView] ✓ Loaded detail image from cache: {cachedPath}");
                                }
                            }
                        });

                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[OnlineFixDetailView] Failed to load cached detail image: {ex.Message}");
                    }
                }

                Logger.Log($"[OnlineFixDetailView] Detail cache miss, fetching high-resolution image for AppID: {_currentFile.AppId}");

                string[] bannerFormats = new[]
                {
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_currentFile.AppId}/header_1840x620.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_currentFile.AppId}/header_920x430.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_currentFile.AppId}/header_616x353.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_currentFile.AppId}/header.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_currentFile.AppId}/capsule_467x181.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_currentFile.AppId}/capsule.jpg"
                };

                var httpClient = SharedHttpClient.Instance;
                if (!httpClient.DefaultRequestHeaders.Contains("User-Agent"))
                {
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                }

                byte[]? imageData = null;

                foreach (var bannerUrl in bannerFormats)
                {
                    try
                    {
                        Logger.Log($"[OnlineFixDetailView] Trying banner URL: {bannerUrl}");
                        var response = await httpClient.GetAsync(bannerUrl);

                        Logger.Log($"[OnlineFixDetailView] HTTP Response: {(int)response.StatusCode}");

                        if (response.IsSuccessStatusCode)
                        {
                            imageData = await response.Content.ReadAsByteArrayAsync();
                            Logger.Log($"[OnlineFixDetailView] ✓ Successfully found banner at: {bannerUrl} ({imageData.Length} bytes)");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[OnlineFixDetailView] Skipping {bannerUrl}: {ex.Message}");
                    }
                }

                if (imageData != null && imageData.Length > 0)
                {
                    try
                    {
                        try
                        {
                            await File.WriteAllBytesAsync(cachedPath, imageData);
                            Logger.Log($"[OnlineFixDetailView] Saved banner to cache: {cachedPath}");
                        }
                        catch (Exception cacheEx)
                        {
                            Logger.Log($"[OnlineFixDetailView] Failed to save banner cache: {cacheEx.Message}");
                        }

                        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = new MemoryStream(imageData);
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.EndInit();

                        Dispatcher.Invoke(() =>
                        {
                            if (FindName("GameThumbnail") is Image thumbnailImg)
                            {
                                thumbnailImg.Source = bitmap;
                                if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                {
                                    thumbnailBorder.Visibility = Visibility.Visible;
                                    Logger.Log("[OnlineFixDetailView] ✓ Banner border set to visible");
                                }
                            }
                        });

                        Logger.Log($"[OnlineFixDetailView] ✓ Successfully loaded banner for {_currentFile.Name} (AppID: {_currentFile.AppId})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[OnlineFixDetailView] ✗ Error creating/displaying BitmapImage: {ex.Message}");
                        if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                            thumbnailBorder.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    Logger.Log($"[OnlineFixDetailView] ✗ No banner found for AppID {_currentFile.AppId} (tried all formats). Trying store page scrape...");
                    try
                    {
                        string storeUrl = $"https://store.steampowered.com/app/{_currentFile.AppId}";
                        var resp = await httpClient.GetAsync(storeUrl);
                        if (resp.IsSuccessStatusCode)
                        {
                            var html = await resp.Content.ReadAsStringAsync();
                            var m = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]*property=[\"']og:image[\"'][^>]*content=[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (!m.Success)
                            {
                                m = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]*content=[\"']([^\"']+)[\"'][^>]*property=[\"']og:image[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            }

                            if (m.Success && Uri.IsWellFormedUriString(m.Groups[1].Value, UriKind.Absolute))
                            {
                                var assetUrl = m.Groups[1].Value;
                                Logger.Log($"[OnlineFixDetailView] Found store asset URL: {assetUrl}");
                                try
                                {
                                    using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
                                    var assetResp = await httpClient.GetAsync(assetUrl, cts2.Token);
                                    if (assetResp.IsSuccessStatusCode)
                                    {
                                        var bytes = await assetResp.Content.ReadAsByteArrayAsync();
                                        if (bytes != null && bytes.Length > 0)
                                        {
                                            try
                                            {
                                                await File.WriteAllBytesAsync(cachedPath, bytes);
                                                Logger.Log($"[OnlineFixDetailView] Saved store asset banner to cache: {cachedPath}");
                                            }
                                            catch (Exception cacheEx)
                                            {
                                                Logger.Log($"[OnlineFixDetailView] Failed to save store asset banner cache: {cacheEx.Message}");
                                            }

                                            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                                            bitmap.BeginInit();
                                            bitmap.StreamSource = new MemoryStream(bytes);
                                            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                            bitmap.EndInit();

                                            Dispatcher.Invoke(() =>
                                            {
                                                if (FindName("GameThumbnail") is Image thumbnailImg)
                                                {
                                                    thumbnailImg.Source = bitmap;
                                                    if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                                        thumbnailBorder.Visibility = Visibility.Visible;
                                                }
                                            });

                                            Logger.Log($"[OnlineFixDetailView] Downloaded banner from store asset for {_currentFile.Name} (AppID: {_currentFile.AppId})");
                                        }
                                    }
                                    else
                                    {
                                        Logger.Log($"[OnlineFixDetailView] Failed to download store asset URL: HTTP {(int)assetResp.StatusCode}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Log($"[OnlineFixDetailView] Error downloading store asset URL for AppID {_currentFile.AppId}: {ex.Message}");
                                }
                            }
                            else
                            {
                                Logger.Log($"[OnlineFixDetailView] No og:image found on store page for AppID {_currentFile.AppId}");
                                if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                    thumbnailBorder.Visibility = Visibility.Collapsed;
                            }
                        }
                        else
                        {
                            Logger.Log($"[OnlineFixDetailView] Failed to fetch store page for AppID {_currentFile.AppId}: HTTP {(int)resp.StatusCode}");
                            if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                thumbnailBorder.Visibility = Visibility.Collapsed;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[OnlineFixDetailView] Error scraping store page for AppID {_currentFile.AppId}: {ex.Message}");
                        if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                            thumbnailBorder.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[OnlineFixDetailView] ✗ Error in LoadBannerAsync: {ex.Message}");
                Logger.Log($"[OnlineFixDetailView] Stack trace: {ex.StackTrace}");
                if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                    thumbnailBorder.Visibility = Visibility.Collapsed;
            }
        }

        private void LoadBitmapFromFile(string filePath)
        {
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();

                Dispatcher.Invoke(() =>
                {
                    if (FindName("GameThumbnail") is Image thumbnailImg)
                    {
                        thumbnailImg.Source = bitmap;
                        if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                            thumbnailBorder.Visibility = Visibility.Visible;
                    }
                });
            }
            catch
            {
                HideThumbnail();
            }
        }

        private void LoadBitmapFromMemory(byte[] imageData)
        {
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = new MemoryStream(imageData);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                Dispatcher.Invoke(() =>
                {
                    if (FindName("GameThumbnail") is Image thumbnailImg)
                    {
                        thumbnailImg.Source = bitmap;
                        if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                            thumbnailBorder.Visibility = Visibility.Visible;
                    }
                });
            }
            catch
            {
                HideThumbnail();
            }
        }

        private void HideThumbnail()
        {
            Dispatcher.Invoke(() =>
            {
                if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                    thumbnailBorder.Visibility = Visibility.Collapsed;
                if (FindName("GameThumbnail") is Image thumbnailImg)
                    thumbnailImg.Source = null;
            });
        }

        /// <summary>
        /// Cari folder yang berisi game executable (.exe)
        /// Prioritas: 1. exe di root, 2. exe non-dlc, 3. root folder (fallback)
        /// </summary>
        private string? FindGameExecutableFolder(string gameFolderPath)
        {
            try
            {
                if (string.IsNullOrEmpty(gameFolderPath) || !Directory.Exists(gameFolderPath))
                    return null;

                // Step 1: Cari .exe files di root folder terlebih dahulu (bukan di subfolder)
                var rootExeFiles = Directory.GetFiles(gameFolderPath, "*.exe", SearchOption.TopDirectoryOnly);
                
                if (rootExeFiles.Length > 0)
                {
                    // Prioritas exe terbesar (main executable biasanya lebih besar)
                    string largestExe = rootExeFiles
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .First();
                    
                    string exeFolder = Path.GetDirectoryName(largestExe);
                    Logger.Log($"[OnlineFixDetailView] Found main game executable at root: {largestExe}");
                    return exeFolder;
                }

                // Step 2: Fallback - cari semua exe tapi hindari yang di folder dlc
                var allExeFiles = Directory.GetFiles(gameFolderPath, "*.exe", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.Combine(gameFolderPath, "dlc"), StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (allExeFiles.Length > 0)
                {
                    // Prioritas exe terbesar
                    string largestExe = allExeFiles
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .First();
                    
                    string exeFolder = Path.GetDirectoryName(largestExe);
                    Logger.Log($"[OnlineFixDetailView] Found game executable (non-dlc) at: {largestExe}");
                    return exeFolder;
                }

                // Step 3: Jika tidak ada exe, gunakan root game folder sebagai fallback
                Logger.Log($"[OnlineFixDetailView] No .exe found, using root game folder as fallback: {gameFolderPath}");
                return gameFolderPath;
            }
            catch (Exception ex)
            {
                Logger.Log($"[OnlineFixDetailView] Error finding game executable: {ex.Message}");
                // Fallback ke root folder jika ada error
                return gameFolderPath;
            }
        }

        private string? FindBestTargetFolder(string gameFolderPath, string extractDir)
        {
            if (TryUseRootReplaceFolder(gameFolderPath, extractDir))
            {
                Logger.Log($"[OnlineFixDetailView] Applying extracted files to root game folder: {gameFolderPath}");
                return gameFolderPath;
            }

            return FindGameExecutableFolder(gameFolderPath);
        }

        private bool TryUseRootReplaceFolder(string gameFolderPath, string extractDir)
        {
            try
            {
                if (string.IsNullOrEmpty(gameFolderPath) || !Directory.Exists(gameFolderPath) ||
                    string.IsNullOrEmpty(extractDir) || !Directory.Exists(extractDir))
                {
                    return false;
                }

                foreach (var extractedFile in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
                {
                    string relPath = Path.GetRelativePath(extractDir, extractedFile);
                    string rootMatchPath = Path.Combine(gameFolderPath, relPath);

                    if (File.Exists(rootMatchPath) || Directory.Exists(rootMatchPath))
                        return true;

                    var parts = relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0]))
                    {
                        string topLevelPath = Path.Combine(gameFolderPath, parts[0]);
                        if (File.Exists(topLevelPath) || Directory.Exists(topLevelPath))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[OnlineFixDetailView] Error checking root match target folder: {ex.Message}");
            }

            return false;
        }

        #region Custom Launch Tutorial Integration

        private async Task LoadTutorialAsync()
        {
            try
            {
                if (_currentFile == null || !_currentFile.AppId.HasValue || _currentFile.AppId.Value <= 0)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (FindName("TutorialCard") is Border card) card.Visibility = Visibility.Collapsed;
                    });
                    return;
                }

                int appId = _currentFile.AppId.Value;
                var tutorial = await CustomLaunchTutorialService.GetTutorialForAppAsync(appId, "ONLINEFIX");
                _currentTutorial = tutorial;

                Dispatcher.Invoke(() =>
                {
                    if (FindName("TutorialCard") is not Border card) return;

                    if (tutorial != null)
                    {
                        if (FindName("TutorialSummaryText") is TextBlock summaryText)
                            summaryText.Text = string.IsNullOrWhiteSpace(tutorial.Summary)
                                ? "This game has recommended custom launch configurations to ensure optimal gameplay."
                                : tutorial.Summary;

                        if (FindName("TutorialExeText") is TextBlock exeText)
                            exeText.Text = string.IsNullOrWhiteSpace(tutorial.RecommendedExecutable)
                                ? "(Default Executable)"
                                : tutorial.RecommendedExecutable;

                        if (FindName("TutorialArgsText") is TextBlock argsText)
                            argsText.Text = string.IsNullOrWhiteSpace(tutorial.RecommendedArguments)
                                ? "(None)"
                                : tutorial.RecommendedArguments;

                        if (FindName("TutorialStepsList") is ItemsControl stepsList)
                            stepsList.ItemsSource = tutorial.Steps;

                        if (FindName("TutorialNotesBorder") is Border notesBorder && FindName("TutorialNotesText") is TextBlock notesText)
                        {
                            if (!string.IsNullOrWhiteSpace(tutorial.Notes))
                            {
                                notesText.Text = tutorial.Notes;
                                notesBorder.Visibility = Visibility.Visible;
                            }
                            else
                            {
                                notesBorder.Visibility = Visibility.Collapsed;
                            }
                        }

                        card.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        card.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[OnlineFixDetailView] Error loading tutorial: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    if (FindName("TutorialCard") is Border card) card.Visibility = Visibility.Collapsed;
                });
            }
        }

        private void CopyExe_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentTutorial != null && !string.IsNullOrWhiteSpace(_currentTutorial.RecommendedExecutable))
                {
                    Clipboard.SetText(_currentTutorial.RecommendedExecutable);
                    ModernMessageBox.Show($"Executable path copied to clipboard:\n{_currentTutorial.RecommendedExecutable}", "Copied to Clipboard", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch { }
        }

        private void CopyArgs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentTutorial != null && !string.IsNullOrWhiteSpace(_currentTutorial.RecommendedArguments))
                {
                    Clipboard.SetText(_currentTutorial.RecommendedArguments);
                    ModernMessageBox.Show($"Launch arguments copied to clipboard:\n{_currentTutorial.RecommendedArguments}", "Copied to Clipboard", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch { }
        }

        private void ApplyTutorialButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentFile == null || !_currentFile.AppId.HasValue || _currentTutorial == null)
                {
                    ModernMessageBox.Show("Tutorial configuration data is not available.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int appId = _currentFile.AppId.Value;
                string? gamePath = SteamHelper.GetGameInstallationPath(appId, _currentFile.Name);

                if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
                {
                    ModernMessageBox.Show("This game is not currently installed in your local Steam library.\n\nPlease install the game first before applying automatic Custom Launch settings.", "Game Not Installed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                bool success = CustomLaunchTutorialService.ApplyTutorialToLocalGame(appId, _currentTutorial, gamePath);
                if (success)
                {
                    ModernMessageBox.Show($"Custom Launch configuration for '{_currentFile.Name}' has been applied successfully!\n\n• Target: {_currentTutorial.RecommendedExecutable}\n• Arguments: {_currentTutorial.RecommendedArguments}\n\nYou can now launch the game directly via PLAY in your Game Library.", "Applied Successfully", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    ModernMessageBox.Show("Failed to apply Custom Launch configuration. Please verify that the game directory exists.", "Application Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"An unexpected error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenGameInLibrary_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentFile == null || !_currentFile.AppId.HasValue)
                {
                    WindowNavigator.NavigateToGameLibrary();
                    return;
                }

                int appId = _currentFile.AppId.Value;
                string? gamePath = SteamHelper.GetGameInstallationPath(appId, _currentFile.Name);

                var gameEntry = new GameEntry
                {
                    AppId = appId,
                    Name = _currentFile.Name ?? "Game",
                    InstallationPath = gamePath ?? string.Empty,
                    IsInstalled = !string.IsNullOrEmpty(gamePath) && Directory.Exists(gamePath)
                };

                WindowNavigator.NavigateToGameLibraryDetail(gameEntry);
            }
            catch (Exception ex)
            {
                Logger.Log($"[OnlineFixDetailView] Error navigating to game library: {ex.Message}");
                WindowNavigator.NavigateToGameLibrary();
            }
        }

        #endregion
    }
}
