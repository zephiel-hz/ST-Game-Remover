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
        private string _activeTempDir = string.Empty;
        private bool _isExecuting = false;
        private bool _isInitialized = false;

        public bool ReplaceSucceeded { get; private set; }
        public int TargetAppId { get; private set; }
        public int ReplacedFilesCount { get; private set; }
        public long ReplacedTotalBytes { get; private set; }

        public DevReplaceLuaManifestDialog(IEnumerable<DevManifestGame>? knownGames = null, int? initialAppId = null)
        {
            InitializeComponent();
            _knownGames = knownGames?.ToList() ?? new List<DevManifestGame>();
            _isInitialized = true;

            if (initialAppId.HasValue && initialAppId.Value > 0)
            {
                AppIdTextBox.Text = initialAppId.Value.ToString();
            }
            else
            {
                ValidateForm();
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
                if (!ModernMessageBox.ShowConfirm("Replace operation is currently in progress. Are you sure you want to cancel?", "Cancel Replace", isDestructive: true, owner: this))
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
                WarningDetailText.Text = $"Executing this will wipe all existing files in Supabase Storage folder '{appId}/' before uploading the staged replacement files.";

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
                WarningDetailText.Text = "Executing this will wipe all existing files in Supabase Storage folder '{appId}/' before uploading the staged replacement files.";
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
                TotalStagedSizeText.Text = $"Total replacement payload: {SupabaseStorageService.FormatSize(totalBytes)}";
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

            bool confirmed = ModernMessageBox.ShowConfirm(
                $"Are you sure you want to replace all files for AppID {TargetAppId}?\n\n" +
                $"1. All existing files in cloud folder '{TargetAppId}/' will be PERMANENTLY DELETED.\n" +
                $"2. {_stagedFiles.Count} new file(s) ({formattedTotal}) will be uploaded.\n" +
                $"3. The catalog record in 'hzmanifest_files' will be updated.\n\n" +
                $"Do you want to proceed?",
                "Confirm Cloud Replacement",
                isDestructive: true,
                owner: this);

            if (!confirmed) return;

            _isExecuting = true;
            ExecuteReplaceButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            AppIdTextBox.IsEnabled = false;
            CheckCloudBtn.IsEnabled = false;
            SourceZipRadio.IsEnabled = false;
            SourceFilesRadio.IsEnabled = false;
            BrowseZipBtn.IsEnabled = false;
            BrowseFilesBtn.IsEnabled = false;
            BrowseFolderBtn.IsEnabled = false;

            ProgressPanel.Visibility = Visibility.Visible;
            ExecutionProgressBar.Value = 0;
            ProgressPercentText.Text = "0%";
            ProgressStatusLabel.Text = $"Purging old files from cloud folder '{TargetAppId}/'...";

            try
            {
                // Step 1: Clean wipe old files in {appId}/
                var (delOk, delCount, delMsg) = await SupabaseStorageService.DeleteAppIdFolderFilesAsync(TargetAppId);
                if (!delOk)
                {
                    throw new InvalidOperationException($"Cloud folder purge failed: {delMsg}");
                }

                ExecutionProgressBar.Value = 25;
                ProgressPercentText.Text = "25%";
                ProgressStatusLabel.Text = $"Purged {delCount} old file(s). Uploading {_stagedFiles.Count} replacement file(s)...";

                // Step 2: Upload new replacement files
                var progress = new Progress<(int current, int total, string fileName)>(info =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        double uploadPct = 25.0 + ((info.current / (double)info.total) * 65.0);
                        ExecutionProgressBar.Value = uploadPct;
                        ProgressPercentText.Text = $"{(int)uploadPct}%";
                        ProgressStatusLabel.Text = $"Uploading ({info.current}/{info.total}): {info.fileName}...";
                    });
                });

                var (upOk, upCount, uploadedBytes, upMsg) = await SupabaseStorageService.UploadReplacementFilesAsync(TargetAppId, _stagedFiles, progress);
                if (!upOk)
                {
                    throw new InvalidOperationException($"Upload failed: {upMsg}");
                }

                // Step 3: Update catalog record in hzmanifest_files
                ExecutionProgressBar.Value = 92;
                ProgressPercentText.Text = "92%";
                ProgressStatusLabel.Text = "Updating catalog database...";

                await SupabaseStorageService.UpdateCatalogMetadataAsync(TargetAppId, uploadedBytes);

                // Step 4: Complete
                ExecutionProgressBar.Value = 100;
                ProgressPercentText.Text = "100%";
                ProgressStatusLabel.Text = "Replacement completed successfully!";

                ReplaceSucceeded = true;
                ReplacedFilesCount = upCount;
                ReplacedTotalBytes = uploadedBytes;

                ModernMessageBox.ShowInformation(
                    $"Successfully replaced Lua & Manifest files for AppID {TargetAppId}!\n\n" +
                    $"• Purged old files: {delCount}\n" +
                    $"• Uploaded new files: {upCount} ({SupabaseStorageService.FormatSize(uploadedBytes)})\n" +
                    $"• Catalog metadata: Updated",
                    "Replacement Complete",
                    owner: this);

                Cleanup();
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                _isExecuting = false;
                NoticeText.Text = $"Error: {ex.Message}";
                ModernMessageBox.ShowError($"Replacement failed: {ex.Message}", "Replacement Failed", owner: this);

                ProgressPanel.Visibility = Visibility.Collapsed;
                ExecuteReplaceButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
                AppIdTextBox.IsEnabled = true;
                CheckCloudBtn.IsEnabled = true;
                SourceZipRadio.IsEnabled = true;
                SourceFilesRadio.IsEnabled = true;
                BrowseZipBtn.IsEnabled = true;
                BrowseFilesBtn.IsEnabled = true;
                BrowseFolderBtn.IsEnabled = true;
                ValidateForm();
            }
        }
    }
}
