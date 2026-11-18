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
using SteamPluginManager.Views;

namespace SteamPluginManager.UI.Dialogs
{
    public partial class DevReplaceLuaManifestDialog : Window
    {
        private readonly List<DevManifestGame> _knownGames;
        private readonly List<ReplacementFileInfo> _stagedFiles = new();
        private readonly List<ReplacementFileInfo> _allExtractedZipFiles = new();
        private readonly List<ReplacementFileInfo> _directFiles = new();
        private bool _replaceExistingFiles;
        private string _activeTempDir = string.Empty;
        private bool _isExecuting = false;
        private bool _isInitialized = false;

        public bool OperationSucceeded { get; private set; }
        public bool ReplaceExistingFiles => _replaceExistingFiles;
        public int TargetAppId { get; private set; }
        public int UploadedFilesCount { get; private set; }
        public long UploadedTotalBytes { get; private set; }

        public DevReplaceLuaManifestDialog(
            IEnumerable<DevManifestGame>? knownGames = null,
            int? initialAppId = null)
        {
            InitializeComponent();
            _knownGames = knownGames?.ToList() ?? new List<DevManifestGame>();
            _isInitialized = true;
            _replaceExistingFiles = OperationReplaceRadio.IsChecked == true;
            ConfigureOperationMode();

            if (initialAppId.HasValue && initialAppId.Value > 0)
            {
                AppIdTextBox.Text = initialAppId.Value.ToString();
            }
            else
            {
                ValidateForm();
            }
        }

        private void ConfigureOperationMode()
        {
            if (_replaceExistingFiles)
            {
                Title = "Replace Lua & Manifest Files";
                HeaderTitleText.Text = "Replace Lua & Manifest Files";
                HeaderSubtitleText.Text = "Delete all files in the AppID folder, then upload the selected files";
                HeaderIcon.Text = "🔄";
                SourceSectionTitleText.Text = "REPLACEMENT SOURCE:";
                OperationNoticePanel.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(38, 13, 18));
                OperationNoticePanel.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(220, 38, 38));
                OperationNoticeIcon.Text = "⚠️";
                OperationNoticeIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(248, 113, 113));
                OperationNoticeTitleText.Text = "WARNING: PERMANENT CLOUD REPLACEMENT";
                OperationNoticeTitleText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(248, 113, 113));
                WarningDetailText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(252, 165, 165));
                ExecuteReplaceButton.Content = "EXECUTE REPLACE (DELETE & UPLOAD)";
                ExecuteReplaceButton.Style = (Style)FindResource("DialogDangerExecuteBtn");
            }
            else
            {
                Title = "Upload Lua & Manifest Files";
                HeaderTitleText.Text = "Upload Lua & Manifest Files";
                HeaderSubtitleText.Text = "Upload files to the AppID folder without deleting existing cloud files";
                HeaderIcon.Text = "↑";
                SourceSectionTitleText.Text = "UPLOAD SOURCE:";
                OperationNoticePanel.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(12, 26, 46));
                OperationNoticePanel.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentCyanBrush");
                OperationNoticeIcon.Text = "i";
                OperationNoticeIcon.Foreground = (System.Windows.Media.Brush)FindResource("AccentCyanBrush");
                OperationNoticeTitleText.Text = "UPLOAD WITHOUT DELETING EXISTING FILES";
                OperationNoticeTitleText.Foreground = (System.Windows.Media.Brush)FindResource("AccentCyanBrush");
                WarningDetailText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(186, 230, 253));
                ExecuteReplaceButton.Content = "UPLOAD FILES";
                ExecuteReplaceButton.Style = (Style)FindResource("DialogPrimaryBtn");
            }

            UpdateOperationNotice();
        }

        private void OperationModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;

            _replaceExistingFiles = OperationReplaceRadio.IsChecked == true;
            ConfigureOperationMode();
        }

        private void UpdateOperationNotice()
        {
            if (_replaceExistingFiles)
            {
                WarningDetailText.Text = TargetAppId > 0
                    ? $"Executing this will wipe all existing files in Supabase Storage folder '{TargetAppId}/' before uploading the staged replacement files."
                    : "Executing this will wipe all existing files in the AppID folder before uploading the staged replacement files.";
            }
            else
            {
                WarningDetailText.Text = TargetAppId > 0
                    ? $"Selected files will be uploaded to Supabase Storage folder '{TargetAppId}/' and the game catalog will be updated. Existing cloud files will not be deleted."
                    : "Selected files will be uploaded to the AppID folder and the game catalog will be updated. Existing cloud files will not be deleted.";
            }
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
            if (_isExecuting)
            {
                string operation = _replaceExistingFiles ? "Replace" : "Upload";
                if (!ModernMessageBox.ShowConfirm($"{operation} operation is currently in progress. Are you sure you want to cancel?", $"Cancel {operation}", isDestructive: _replaceExistingFiles, owner: this))
                {
                    return;
                }
            }
            Cleanup();
            DialogResult = false;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseButton_Click(sender, e);
        }

        private void Cleanup()
        {
            if (!string.IsNullOrEmpty(_activeTempDir))
            {
                SupabaseStorageService.CleanupTempDir(_activeTempDir);
                _activeTempDir = string.Empty;
            }
            _allExtractedZipFiles.Clear();
        }

        private void AppIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isInitialized || AppIdTextBox == null || WarningDetailText == null) return;

            string text = AppIdTextBox.Text.Trim();
            if (int.TryParse(text, out int appId) && appId > 0)
            {
                TargetAppId = appId;
                UpdateOperationNotice();

                var matched = _knownGames.FirstOrDefault(g => g.AppId == appId);
                if (matched != null)
                {
                    if (MatchedGameTitleText != null) MatchedGameTitleText.Text = matched.Name ?? $"AppID {matched.AppId}";
                    if (MatchedGameSubtitleText != null) MatchedGameSubtitleText.Text = $"AppID: {matched.AppId} • {matched.Genre}";
                    if (CloudStatusText != null) CloudStatusText.Text = "Click 'Check Cloud' to query";
                    if (GameMatchBanner != null) GameMatchBanner.Visibility = Visibility.Visible;
                }
                else
                {
                    if (MatchedGameTitleText != null) MatchedGameTitleText.Text = $"AppID {appId}";
                    if (MatchedGameSubtitleText != null) MatchedGameSubtitleText.Text = $"AppID: {appId} (Not in local catalog)";
                    if (CloudStatusText != null) CloudStatusText.Text = "Click 'Check Cloud' to query";
                    if (GameMatchBanner != null) GameMatchBanner.Visibility = Visibility.Visible;
                }

                _ = CheckCloudForAppIdAsync(appId);
            }
            else
            {
                TargetAppId = 0;
                if (GameMatchBanner != null) GameMatchBanner.Visibility = Visibility.Collapsed;
                UpdateOperationNotice();
            }

            ValidateForm();
        }

        private async void CheckCloudBtn_Click(object sender, RoutedEventArgs e)
        {
            if (TargetAppId > 0)
            {
                await CheckCloudForAppIdAsync(TargetAppId);
            }
            else
            {
                ModernMessageBox.ShowWarning("Please enter a valid numeric AppID first.", "Invalid AppID", owner: this);
            }
        }

        private async Task CheckCloudForAppIdAsync(int appId)
        {
            if (!_isInitialized || CloudStatusText == null) return;

            try
            {
                CloudStatusText.Text = "Querying cloud storage...";
                var files = await SupabaseStorageService.ListAppIdFilesAsync(appId);

                if (CloudStatusText == null) return;

                if (files.Count > 0)
                {
                    CloudStatusText.Text = $"☁️ Cloud: {files.Count} existing files in {appId}/";
                }
                else
                {
                    CloudStatusText.Text = $"☁️ Cloud: Folder {appId}/ is empty";
                }
            }
            catch (Exception ex)
            {
                if (CloudStatusText != null)
                {
                    CloudStatusText.Text = $"☁️ Cloud query error: {ex.Message}";
                }
            }
        }

        private void SourceRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || ZipSourcePanel == null || DirectFilesSourcePanel == null || SourceZipRadio == null) return;

            bool isZip = SourceZipRadio.IsChecked == true;
            ZipSourcePanel.Visibility = isZip ? Visibility.Visible : Visibility.Collapsed;
            DirectFilesSourcePanel.Visibility = isZip ? Visibility.Collapsed : Visibility.Visible;

            if (isZip)
            {
                ApplyZipFilter();
            }
            else
            {
                _stagedFiles.Clear();
                _stagedFiles.AddRange(_directFiles);
                RefreshStagedDataGrid();
                ValidateForm();
            }
        }

        private void ZipFilterRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            ApplyZipFilter();
        }

        private void ApplyZipFilter()
        {
            if (!_isInitialized || SourceZipRadio == null || SourceZipRadio.IsChecked != true) return;

            bool includeManifest = ZipFilterAllRadio?.IsChecked == true;
            _stagedFiles.Clear();

            if (includeManifest)
            {
                _stagedFiles.AddRange(_allExtractedZipFiles);
            }
            else
            {
                _stagedFiles.AddRange(_allExtractedZipFiles.Where(f => f.FileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)));
            }

            RefreshStagedDataGrid();
            ValidateForm();
        }

        private void UpdateZipFilterRadioLabels()
        {
            int luaCount = _allExtractedZipFiles.Count(f => f.FileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            int manifestCount = _allExtractedZipFiles.Count(f => f.FileName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase));

            if (ZipFilterAllRadio != null)
            {
                ZipFilterAllRadio.Content = $"Include .manifest ({luaCount} lua, {manifestCount} manifest)";
            }
            if (ZipFilterLuaOnlyRadio != null)
            {
                ZipFilterLuaOnlyRadio.Content = $"Lua only ({luaCount} lua)";
            }
        }

        private void BrowseZipBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Select Lua & Manifest Zip Archive",
                    Filter = "Zip Archives (*.zip)|*.zip|All Files (*.*)|*.*",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog(this) == true)
                {
                    string zipPath = dialog.FileName;
                    ZipPathTextBox.Text = zipPath;

                    // Clean previous temp extraction
                    Cleanup();

                    // Auto detect AppID from filename if not set
                    if (TargetAppId == 0)
                    {
                        string fname = Path.GetFileNameWithoutExtension(zipPath);
                        var match = Regex.Match(fname, @"\b(\d{4,8})\b");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int detectedId))
                        {
                            AppIdTextBox.Text = detectedId.ToString();
                        }
                    }

                    // Extract and scan zip
                    var (tempDir, files) = SupabaseStorageService.ExtractAndScanZip(zipPath);
                    _activeTempDir = tempDir;

                    _allExtractedZipFiles.Clear();
                    _allExtractedZipFiles.AddRange(files);

                    UpdateZipFilterRadioLabels();
                    ApplyZipFilter();

                    // Secondary check: if AppId still 0, look for {appId}.lua
                    if (TargetAppId == 0)
                    {
                        var luaFile = files.FirstOrDefault(f => f.FileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
                        if (luaFile != null)
                        {
                            string luaBase = Path.GetFileNameWithoutExtension(luaFile.FileName);
                            if (int.TryParse(luaBase, out int detectedId))
                            {
                                AppIdTextBox.Text = detectedId.ToString();
                            }
                        }
                    }

                    if (_allExtractedZipFiles.Count == 0)
                    {
                        ModernMessageBox.ShowWarning(
                            "No .lua or .manifest files were found in the selected zip archive.\nPlease ensure the zip contains valid Lua scripts or depot manifests.",
                            "No Valid Files Found",
                            owner: this);
                    }
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.ShowError($"Failed to process zip archive: {ex.Message}", "Error Reading Archive", owner: this);
            }
        }

        private void BrowseFilesBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Select Lua & Manifest Files",
                    Filter = "Lua & Manifest Files (*.lua;*.manifest)|*.lua;*.manifest|All Files (*.*)|*.*",
                    Multiselect = true,
                    CheckFileExists = true
                };

                if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
                {
                    var files = SupabaseStorageService.ScanDirectFiles(dialog.FileNames);

                    // If AppId is not yet set, try detecting from first {appId}.lua
                    if (TargetAppId == 0)
                    {
                        var luaFile = files.FirstOrDefault(f => f.FileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
                        if (luaFile != null)
                        {
                            string luaBase = Path.GetFileNameWithoutExtension(luaFile.FileName);
                            if (int.TryParse(luaBase, out int detectedId))
                            {
                                AppIdTextBox.Text = detectedId.ToString();
                            }
                        }
                    }

                    // Merge into _directFiles without duplicates
                    foreach (var f in files)
                    {
                        if (!_directFiles.Any(s => s.FileName.Equals(f.FileName, StringComparison.OrdinalIgnoreCase)))
                        {
                            _directFiles.Add(f);
                        }
                    }

                    _stagedFiles.Clear();
                    _stagedFiles.AddRange(_directFiles);

                    DirectFilesCountLabel.Text = $"{_stagedFiles.Count} direct file(s) staged.";
                    RefreshStagedDataGrid();
                    ValidateForm();
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.ShowError($"Failed to select files: {ex.Message}", "Error Selecting Files", owner: this);
            }
        }

        private void BrowseFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "Select Folder Containing Lua & Manifest Files",
                    Multiselect = false
                };

                if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
                {
                    string folder = dialog.FolderName;
                    var files = SupabaseStorageService.ScanFolderFiles(folder);

                    // If AppId not set, check folder name or lua file
                    if (TargetAppId == 0)
                    {
                        string folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (int.TryParse(folderName, out int detectedId))
                        {
                            AppIdTextBox.Text = detectedId.ToString();
                        }
                        else
                        {
                            var luaFile = files.FirstOrDefault(f => f.FileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
                            if (luaFile != null && int.TryParse(Path.GetFileNameWithoutExtension(luaFile.FileName), out int luaId))
                            {
                                AppIdTextBox.Text = luaId.ToString();
                            }
                        }
                    }

                    // Merge into _directFiles without duplicates
                    foreach (var f in files)
                    {
                        if (!_directFiles.Any(s => s.FileName.Equals(f.FileName, StringComparison.OrdinalIgnoreCase)))
                        {
                            _directFiles.Add(f);
                        }
                    }

                    _stagedFiles.Clear();
                    _stagedFiles.AddRange(_directFiles);

                    DirectFilesCountLabel.Text = $"{_stagedFiles.Count} direct file(s) staged.";
                    RefreshStagedDataGrid();
                    ValidateForm();

                    if (files.Count == 0)
                    {
                        ModernMessageBox.ShowWarning("No .lua or .manifest files found in the selected folder.", "No Files Found", owner: this);
                    }
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.ShowError($"Failed to scan folder: {ex.Message}", "Error", owner: this);
            }
        }

        private void RefreshStagedDataGrid()
        {
            if (!_isInitialized || StagedFilesDataGrid == null) return;

            StagedFilesDataGrid.ItemsSource = null;
            StagedFilesDataGrid.ItemsSource = _stagedFiles;

            if (StagedFilesCounterText != null)
            {
                StagedFilesCounterText.Text = $"({_stagedFiles.Count} files)";
            }
            long totalBytes = _stagedFiles.Sum(f => f.FileSizeBytes);
            if (TotalStagedSizeText != null)
            {
                TotalStagedSizeText.Text = $"Total upload payload: {SupabaseStorageService.FormatSize(totalBytes)}";
            }
        }

        private void ValidateForm()
        {
            if (!_isInitialized || ExecuteReplaceButton == null) return;

            if (NoticeText != null)
            {
                NoticeText.Text = string.Empty;
            }

            if (TargetAppId <= 0)
            {
                ExecuteReplaceButton.IsEnabled = false;
                return;
            }

            if (SourceZipRadio?.IsChecked == true && _allExtractedZipFiles.Count > 0 && _stagedFiles.Count == 0)
            {
                if (NoticeText != null)
                {
                    NoticeText.Text = "No .lua files found in zip matching the 'Lua only' filter.";
                }
                ExecuteReplaceButton.IsEnabled = false;
                return;
            }

            if (_stagedFiles.Count == 0)
            {
                ExecuteReplaceButton.IsEnabled = false;
                return;
            }

            ExecuteReplaceButton.IsEnabled = !_isExecuting;
        }

        private async void ExecuteReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (TargetAppId <= 0 || _stagedFiles.Count == 0)
            {
                return;
            }

            long totalBytes = _stagedFiles.Sum(f => f.FileSizeBytes);
            string formattedTotal = SupabaseStorageService.FormatSize(totalBytes);
            string confirmationMessage = _replaceExistingFiles
                ? $"Are you sure you want to replace all files for AppID {TargetAppId}?\n\n" +
                  $"1. All existing files in cloud folder '{TargetAppId}/' will be PERMANENTLY DELETED.\n" +
                  $"2. {_stagedFiles.Count} new file(s) ({formattedTotal}) will be uploaded.\n" +
                  $"3. The catalog record in 'hzmanifest_files' will be updated.\n\n" +
                  "Do you want to proceed?"
                : $"Upload {_stagedFiles.Count} file(s) ({formattedTotal}) for AppID {TargetAppId}?\n\n" +
                  $"Files will be added to or overwrite matching files in cloud folder '{TargetAppId}/'.\n" +
                  "Other files in the folder will not be deleted. The game catalog will be updated.";

            bool confirmed = ModernMessageBox.ShowConfirm(
                confirmationMessage,
                _replaceExistingFiles ? "Confirm Cloud Replacement" : "Confirm Lua/Manifest Upload",
                isDestructive: _replaceExistingFiles,
                owner: this);

            if (!confirmed) return;

            _isExecuting = true;
            ExecuteReplaceButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            AppIdTextBox.IsEnabled = false;
            CheckCloudBtn.IsEnabled = false;
            SourceZipRadio.IsEnabled = false;
            SourceFilesRadio.IsEnabled = false;
            OperationUploadRadio.IsEnabled = false;
            OperationReplaceRadio.IsEnabled = false;
            BrowseZipBtn.IsEnabled = false;
            BrowseFilesBtn.IsEnabled = false;
            BrowseFolderBtn.IsEnabled = false;

            ProgressPanel.Visibility = Visibility.Visible;
            ExecutionProgressBar.Value = 0;
            ProgressPercentText.Text = "0%";
            ProgressStatusLabel.Text = _replaceExistingFiles
                ? $"Purging old files from cloud folder '{TargetAppId}/'..."
                : $"Preparing {_stagedFiles.Count} file(s) for upload...";

            try
            {
                int deletedCount = 0;
                int uploadedCount;
                long uploadedBytes;

                if (_replaceExistingFiles)
                {
                    var (delOk, delCount, delMsg) = await SupabaseStorageService.DeleteAppIdFolderFilesAsync(TargetAppId);
                    if (!delOk)
                    {
                        throw new InvalidOperationException($"Cloud folder purge failed: {delMsg}");
                    }

                    deletedCount = delCount;
                    ExecutionProgressBar.Value = 25;
                    ProgressPercentText.Text = "25%";
                    ProgressStatusLabel.Text = $"Purged {delCount} old file(s). Uploading {_stagedFiles.Count} replacement file(s)...";

                    var replacementProgress = new Progress<(int current, int total, string fileName)>(info =>
                    {
                        double uploadPct = 25.0 + ((info.current / (double)info.total) * 65.0);
                        ExecutionProgressBar.Value = uploadPct;
                        ProgressPercentText.Text = $"{(int)uploadPct}%";
                        ProgressStatusLabel.Text = $"Uploading ({info.current}/{info.total}): {info.fileName}...";
                    });

                    var (upOk, upCount, bytes, upMsg) = await SupabaseStorageService.UploadReplacementFilesAsync(TargetAppId, _stagedFiles, replacementProgress);
                    if (!upOk)
                    {
                        throw new InvalidOperationException($"Upload failed: {upMsg}");
                    }

                    ExecutionProgressBar.Value = 92;
                    ProgressPercentText.Text = "92%";
                    ProgressStatusLabel.Text = "Updating catalog metadata...";

                    if (!await SupabaseStorageService.UpdateCatalogMetadataAsync(TargetAppId, bytes))
                    {
                        throw new InvalidOperationException("Files were uploaded, but catalog metadata could not be updated.");
                    }

                    uploadedCount = upCount;
                    uploadedBytes = bytes;
                }
                else
                {
                    var uploadProgress = new Progress<(int current, int total, string fileName)>(info =>
                    {
                        double uploadPct = info.current / (double)info.total * 80.0;
                        ExecutionProgressBar.Value = uploadPct;
                        ProgressPercentText.Text = $"{(int)uploadPct}%";
                        ProgressStatusLabel.Text = $"Uploading ({info.current}/{info.total}): {info.fileName}...";
                    });

                    var (uploadOk, uploadCount, bytes, uploadMessage) =
                        await SupabaseStorageService.UploadReplacementFilesAsync(TargetAppId, _stagedFiles, uploadProgress);
                    if (!uploadOk)
                    {
                        throw new InvalidOperationException(uploadMessage);
                    }

                    string? fallbackGameName = _knownGames.FirstOrDefault(game => game.AppId == TargetAppId)?.Name;
                    var (catalogUpdated, catalogMessage, gameName) = await ManifestHubService.RegisterUploadedFolderAsync(
                        TargetAppId,
                        bytes,
                        status => Dispatcher.Invoke(() =>
                        {
                            ExecutionProgressBar.Value = status.StartsWith("Fetching", StringComparison.Ordinal) ? 88 : 95;
                            ProgressPercentText.Text = $"{(int)ExecutionProgressBar.Value}%";
                            ProgressStatusLabel.Text = status;
                        }),
                        fallbackGameName);
                    if (!catalogUpdated)
                    {
                        throw new InvalidOperationException($"Files were uploaded, but catalog registration failed: {catalogMessage}");
                    }

                    ProgressStatusLabel.Text = $"Catalog updated for {gameName}.";
                    uploadedCount = uploadCount;
                    uploadedBytes = bytes;
                }

                ExecutionProgressBar.Value = 100;
                ProgressPercentText.Text = "100%";
                ProgressStatusLabel.Text = _replaceExistingFiles
                    ? "Replacement completed successfully!"
                    : "Upload completed successfully!";

                OperationSucceeded = true;
                UploadedFilesCount = uploadedCount;
                UploadedTotalBytes = uploadedBytes;

                string completionMessage = _replaceExistingFiles
                    ? $"Successfully replaced Lua & Manifest files for AppID {TargetAppId}!\n\n" +
                      $"• Purged old files: {deletedCount}\n" +
                      $"• Uploaded new files: {uploadedCount} ({SupabaseStorageService.FormatSize(uploadedBytes)})\n" +
                      "• Catalog metadata: Updated"
                    : $"Successfully uploaded {uploadedCount} Lua/manifest file(s) for AppID {TargetAppId}.\n\n" +
                      $"Total size: {SupabaseStorageService.FormatSize(uploadedBytes)}\n" +
                      "Other files in the cloud folder were not deleted. The game catalog was updated.";

                ModernMessageBox.ShowInformation(
                    completionMessage,
                    _replaceExistingFiles ? "Replacement Complete" : "Upload Complete",
                    owner: this);

                Cleanup();
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                _isExecuting = false;
                NoticeText.Text = $"Error: {ex.Message}";
                string operation = _replaceExistingFiles ? "Replacement" : "Upload";
                ModernMessageBox.ShowError($"{operation} failed: {ex.Message}", $"{operation} Failed", owner: this);

                ProgressPanel.Visibility = Visibility.Collapsed;
                ExecuteReplaceButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
                AppIdTextBox.IsEnabled = true;
                CheckCloudBtn.IsEnabled = true;
                SourceZipRadio.IsEnabled = true;
                SourceFilesRadio.IsEnabled = true;
                OperationUploadRadio.IsEnabled = true;
                OperationReplaceRadio.IsEnabled = true;
                BrowseZipBtn.IsEnabled = true;
                BrowseFilesBtn.IsEnabled = true;
                BrowseFolderBtn.IsEnabled = true;
                ValidateForm();
            }
        }
    }
}
