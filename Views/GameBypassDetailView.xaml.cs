#pragma warning disable CS0103 // The name does not exist
using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SteamPluginManager.Views
{
        public partial class GameBypassDetailView : UserControl
        {
            private readonly string _thumbnailCacheFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SteamPluginManager", "ThumbnailCache");

            private void BackToDashboard_Click(object sender, RoutedEventArgs e)
            {
                try
                {
                    WindowNavigator.NavigateToGameBypass();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[GameBypassDetailView] Navigation error: {ex.Message}");
                    MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        private BypassFile? _currentFile;
        private CancellationTokenSource? _downloadCancellationTokenSource;
        private bool _isPaused;

        public GameBypassDetailView()
        {
            try
            {
                Directory.CreateDirectory(_thumbnailCacheFolder);
                InitializeComponent();
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] InitializeComponent error: {ex.Message}");
            }
            Loaded += GameBypassDetailView_Loaded;
        }

        private void GameBypassDetailView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                RegisterTextElements();
                DisplayFileDetails();
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] Error: {ex.Message}");
            }
        }

        public void SetFile(BypassFile file)
        {
            _currentFile = file;
            if (IsLoaded)
            {
                DisplayFileDetails();
            }
        }

        private void RegisterTextElements()
        {
            try
            {
                if (FindName("DetailTitle") is TextBlock detailTitle)
                    detailTitle.Text = "Game Details";
                if (FindName("DetailSubtitle") is TextBlock detailSubtitle)
                    detailSubtitle.Text = "DRM Protection Bypass Information";
                if (FindName("AddToLibraryButton") is Button addToLibraryButton)
                    addToLibraryButton.Content = "Apply Bypass";
                if (FindName("BackButtonText") is TextBlock backButtonText)
                    backButtonText.Text = "← Back";
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] RegisterTextElements error: {ex.Message}");
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
                if (FindName("ProgressOverlay") is Grid overlay)
                {
                    overlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    Logger.Log($"[GameBypassDetailView] Overlay visibility set to: {overlay.Visibility}");
                }

                if (FindName("ProgressTitle") is TextBlock progressTitle)
                {
                    progressTitle.Text = "Applying Bypass...";
                }

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
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] Error in ShowLoadingState: {ex.Message}");
            }
        }

        private void ShowResultNotification(bool isSuccess, string message)
        {
            try
            {
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
                    {
                        resultMsg.Text = message;
                    }
                    
                    if (FindName("ProgressBar") is ProgressBar progressBar)
                        progressBar.Visibility = Visibility.Collapsed;
                    if (FindName("StepsPanel") is StackPanel stepsPanel)
                        stepsPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] Error showing result notification: {ex.Message}");
            }
        }

        private void UpdateProgressStep(int stepNumber, bool completed = false)
        {
            try
            {
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
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] Error updating progress step: {ex.Message}");
            }
        }

        private void DisplayFileDetails()
        {
            try
            {
                if (_currentFile == null)
                {
                    MessageBox.Show("No file selected", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _ = LoadBannerAsync();

                if (FindName("GameNameDetail") is TextBlock gameNameDetail)
                    gameNameDetail.Text = _currentFile.Name ?? "Unknown Game";
                
                if (FindName("GenreDetail") is TextBlock genreDetail)
                    genreDetail.Text = _currentFile.Genre ?? "N/A";
                
                if (FindName("DescriptionDetail") is TextBlock descriptionDetail)
                    descriptionDetail.Text = _currentFile.Description ?? "No description available";
                
                UpdateAddToLibraryButton();
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] Error displaying details: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadBannerAsync()
        {
            try
            {
                if (_currentFile == null)
                {
                    Logger.Log("[GameBypassDetailView] CurrentFile is null");
                    if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                        thumbnailBorder.Visibility = Visibility.Collapsed;
                    return;
                }

                if (_currentFile.AppId == null || _currentFile.AppId == 0)
                {
                    Logger.Log($"[GameBypassDetailView] Invalid AppId: {_currentFile.AppId}");
                    if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                        thumbnailBorder.Visibility = Visibility.Collapsed;
                    return;
                }

                Logger.Log($"[GameBypassDetailView] Starting banner load for AppID: {_currentFile.AppId}");

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
                                    Logger.Log($"[GameBypassDetailView] ✓ Loaded detail image from cache: {cachedPath}");
                                }
                            }
                        });

                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[GameBypassDetailView] Failed to load cached detail image: {ex.Message}");
                    }
                }

                Logger.Log($"[GameBypassDetailView] Detail cache miss, fetching high-resolution image for AppID: {_currentFile.AppId}");

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
                        Logger.Log($"[GameBypassDetailView] Trying banner URL: {bannerUrl}");
                        var response = await httpClient.GetAsync(bannerUrl);

                        Logger.Log($"[GameBypassDetailView] HTTP Response: {(int)response.StatusCode}");

                        if (response.IsSuccessStatusCode)
                        {
                            imageData = await response.Content.ReadAsByteArrayAsync();
                            Logger.Log($"[GameBypassDetailView] ✓ Successfully found banner at: {bannerUrl} ({imageData.Length} bytes)");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[GameBypassDetailView] Skipping {bannerUrl}: {ex.Message}");
                    }
                }

                if (imageData != null && imageData.Length > 0)
                {
                    try
                    {
                        try
                        {
                            await File.WriteAllBytesAsync(cachedPath, imageData);
                            Logger.Log($"[GameBypassDetailView] Saved banner to cache: {cachedPath}");
                        }
                        catch (Exception cacheEx)
                        {
                            Logger.Log($"[GameBypassDetailView] Failed to save banner cache: {cacheEx.Message}");
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
                                    Logger.Log("[GameBypassDetailView] ✓ Banner border set to visible");
                                }
                            }
                        });

                        Logger.Log($"[GameBypassDetailView] ✓ Successfully loaded banner for {_currentFile.Name} (AppID: {_currentFile.AppId})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[GameBypassDetailView] ✗ Error creating/displaying BitmapImage: {ex.Message}");
                        if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                            thumbnailBorder.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    Logger.Log($"[GameBypassDetailView] ✗ No banner found for AppID {_currentFile.AppId} (tried all formats). Trying store page scrape...");
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
                                Logger.Log($"[GameBypassDetailView] Found store asset URL: {assetUrl}");
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
                                                Logger.Log($"[GameBypassDetailView] Saved store asset banner to cache: {cachedPath}");
                                            }
                                            catch (Exception cacheEx)
                                            {
                                                Logger.Log($"[GameBypassDetailView] Failed to save store asset banner cache: {cacheEx.Message}");
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

                                            Logger.Log($"[GameBypassDetailView] Downloaded banner from store asset for {_currentFile.Name} (AppID: {_currentFile.AppId})");
                                        }
                                    }
                                    else
                                    {
                                        Logger.Log($"[GameBypassDetailView] Failed to download store asset URL: HTTP {(int)assetResp.StatusCode}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Log($"[GameBypassDetailView] Error downloading store asset URL for AppID {_currentFile.AppId}: {ex.Message}");
                                }
                            }
                            else
                            {
                                Logger.Log($"[GameBypassDetailView] No og:image found on store page for AppID {_currentFile.AppId}");
                                if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                    thumbnailBorder.Visibility = Visibility.Collapsed;
                            }
                        }
                        else
                        {
                            Logger.Log($"[GameBypassDetailView] Failed to fetch store page for AppID {_currentFile.AppId}: HTTP {(int)resp.StatusCode}");
                            if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                thumbnailBorder.Visibility = Visibility.Collapsed;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[GameBypassDetailView] Error scraping store page for AppID {_currentFile.AppId}: {ex.Message}");
                        if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                            thumbnailBorder.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] ✗ Error in LoadBannerAsync: {ex.Message}");
                if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                    thumbnailBorder.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateAddToLibraryButton()
        {
            try
            {
                if (_currentFile == null || _currentFile.AppId == null || _currentFile.AppId == 0)
                    return;

                if (FindName("AddToLibraryButton") is Button addBtn)
                {
                    addBtn.Content = "Apply Bypass";
                    addBtn.Tag = FindResource("Icon.Plus");
                    addBtn.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] Error updating button state: {ex.Message}");
            }
        }

        private void ShowControlButtons(bool show)
        {
            try
            {
                if (FindName("ControlButtonsPanel") is StackPanel controlPanel)
                {
                    controlPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                }

                if (show)
                {
                    if (FindName("PauseResumeButton") is Button pauseBtn)
                    {
                        pauseBtn.Content = "Pause";
                        pauseBtn.Tag = FindResource("Icon.Pause");
                        pauseBtn.IsEnabled = true;
                    }
                    
                    if (FindName("CancelDownloadButton") is Button cancelBtn)
                    {
                        cancelBtn.IsEnabled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] Error in ShowControlButtons: {ex.Message}");
            }
        }

        private async Task PauseCheckAsync()
        {
            while (_isPaused)
            {
                await Task.Delay(100);
            }
        }

        private void PauseResume_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isPaused = !_isPaused;

                if (_isPaused)
                {
                    Logger.Log("[GameBypassDetailView] Download paused");
                    
                    // Update UI to show Resume button
                    if (FindName("PauseResumeButton") is Button pauseBtn)
                    {
                        pauseBtn.Content = "Resume";
                        pauseBtn.Tag = FindResource("Icon.Play");
                    }
                    
                    if (FindName("ProgressStatus") is TextBlock statusText)
                        statusText.Text = "Download paused...";
                }
                else
                {
                    Logger.Log("[GameBypassDetailView] Download resumed");
                    
                    // Update UI to show Pause button
                    if (FindName("PauseResumeButton") is Button resumeBtn)
                    {
                        resumeBtn.Content = "Pause";
                        resumeBtn.Tag = FindResource("Icon.Pause");
                    }
                    
                    if (FindName("ProgressStatus") is TextBlock statusText)
                        statusText.Text = "Downloading bypass file...";
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] Error in PauseResume_Click: {ex.Message}");
            }
        }

        private void CancelDownload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_downloadCancellationTokenSource == null)
                    return;

                Logger.Log("[GameBypassDetailView] Cancelling download...");
                
                // Resume if paused so cancellation can take effect immediately
                _isPaused = false;

                _downloadCancellationTokenSource.Cancel();

                if (FindName("ProgressStatus") is TextBlock statusText)
                    statusText.Text = "Cancelling download...";

                if (FindName("CancelDownloadButton") is Button cancelBtn)
                    cancelBtn.IsEnabled = false;

                if (FindName("PauseResumeButton") is Button pauseBtn)
                    pauseBtn.IsEnabled = false;
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] Error in CancelDownload_Click: {ex.Message}");
            }
        }

        private async void AddToLibrary_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentFile == null)
                {
                    MessageBox.Show("No file selected", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (FindName("AddToLibraryButton") is Button addBtn)
                    addBtn.IsEnabled = false;

                // Initialize cancellation token source for this download
                _downloadCancellationTokenSource = new CancellationTokenSource();
                _isPaused = false;

                // Cek apakah game sudah terinstal
                var mainWindow = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w is SteamPluginManager.MainWindow) as SteamPluginManager.MainWindow;
                bool isInstalled = false;
                string? gameFolder = null;
                if (mainWindow != null && _currentFile.AppId != null)
                {
                    gameFolder = mainWindow.GetGameInstallationPath(_currentFile.AppId.Value);
                    isInstalled = !string.IsNullOrEmpty(gameFolder) && Directory.Exists(gameFolder);
                }
                if (!isInstalled)
                {
                    ShowLoadingState(true, "Game not installed!");
                    ShowResultNotification(false, "✗ Game not installed!");
                    ShowControlButtons(false);
                    await Task.Delay(1500);
                    ShowLoadingState(false);
                    if (FindName("AddToLibraryButton") is Button btnError)
                        btnError.IsEnabled = true;
                    return;
                }

                ShowLoadingState(true, "Downloading bypass file from Cloudflare R2...", GetCurrentFileSizeDetail());
                ShowControlButtons(true);
                UpdateProgressStep(1);



                // 1. Download file dari R2 (debug log detail)
                string r2Key = _currentFile.FileName;
                Logger.Log($"[GameBypassDetailView] Attempting to download from R2. FileName: '{_currentFile.FileName}', r2Key: '{r2Key}'");
                string tempZip = Path.Combine(Path.GetTempPath(), $"bypass_{Guid.NewGuid()}.zip");
                try
                {
                    var progress = new Progress<int>(p =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (FindName("ProgressBar") is ProgressBar pb)
                            {
                                pb.IsIndeterminate = false;
                                pb.Value = p;
                            }
                            if (FindName("ProgressStatus") is TextBlock statusText)
                                statusText.Text = !string.IsNullOrWhiteSpace(_currentFile?.FileSize)
                                    ? $"Downloading bypass file ({_currentFile.FileSize})... {p}%"
                                    : $"Downloading bypass file... {p}%";
                        });
                    });

                    byte[] zipBytes = await SteamPluginManager.R2Config.DownloadFileAsync(r2Key, progress, _downloadCancellationTokenSource.Token, PauseCheckAsync);
                    Logger.Log($"[GameBypassDetailView] Download success. Size: {zipBytes?.Length ?? 0} bytes");
                    await File.WriteAllBytesAsync(tempZip, zipBytes);
                }
                catch (OperationCanceledException ex)
                {
                    Logger.Log($"[GameBypassDetailView] Download cancelled by user. Exception: {ex.Message}");
                    ShowResultNotification(false, "✗ Download cancelled!");
                    ShowLoadingState(false);
                    ShowControlButtons(false);
                    if (FindName("AddToLibraryButton") is Button btnCancel)
                        btnCancel.IsEnabled = true;
                    return;
                }
                catch (Exception exDownload)
                {
                    Logger.Log($"[GameBypassDetailView] DownloadFileAsync failed for key '{r2Key}'. Exception: {exDownload.Message}\nStackTrace: {exDownload.StackTrace}");
                    throw;
                }

                ShowLoadingState(true, "Extracting bypass file...");
                UpdateProgressStep(2);

                // 2. Ekstrak file ZIP
                string extractDir = Path.Combine(Path.GetTempPath(), $"bypass_extract_{Guid.NewGuid()}");
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(tempZip, extractDir);

                ShowLoadingState(true, "Checking target folder...");
                UpdateProgressStep(3);

                // 3. Cari folder target yang paling sesuai berdasarkan isi ekstrak
                if (string.IsNullOrEmpty(gameFolder) || !Directory.Exists(gameFolder))
                {
                    ShowResultNotification(false, "✗ Game folder not found!");
                    ShowLoadingState(false);
                    ShowControlButtons(false);
                    if (FindName("AddToLibraryButton") is Button btnError)
                        btnError.IsEnabled = true;
                    return;
                }

                string? targetFolder = FindBestTargetFolder(gameFolder, extractDir);
                if (string.IsNullOrEmpty(targetFolder))
                {
                    ShowResultNotification(false, "✗ Cannot determine target folder!");
                    ShowLoadingState(false);
                    ShowControlButtons(false);
                    if (FindName("AddToLibraryButton") is Button btnExeError)
                        btnExeError.IsEnabled = true;
                    return;
                }

                ShowLoadingState(true, "Copying bypass files to game folder...");
                UpdateProgressStep(4);

                // 4. Copy file hasil ekstrak ke target folder
                foreach (var file in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
                {
                    string relPath = Path.GetRelativePath(extractDir, file);
                    string targetPath = Path.Combine(targetFolder, relPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    
                    bool fileExists = File.Exists(targetPath);
                    File.Copy(file, targetPath, true);
                    
                    if (fileExists)
                    {
                        Logger.Log($"[GameBypassDetailView] Replaced existing file: {relPath}");
                    }
                    else
                    {
                        Logger.Log($"[GameBypassDetailView] Added new file: {relPath}");
                    }
                }

                await Task.Delay(1000);

                Dispatcher.Invoke(() =>
                {
                    ShowResultNotification(true, $"✓ Successfully applied bypass to '{_currentFile.Name}'!");
                    Logger.Log($"[GameBypassDetailView] ✓ Showing success notification");
                });

                await Task.Delay(1500);

                if (FindName("AddToLibraryButton") is Button btnSuccess)
                    btnSuccess.IsEnabled = true;

                ShowLoadingState(false);
                ShowControlButtons(false);
                Logger.Log($"[GameBypassDetailView] ✓ Successfully applied bypass: {_currentFile.Name}");

                UpdateAddToLibraryButton();
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] Apply bypass error: {ex.Message}");

                ShowResultNotification(false, $"✗ Error: {ex.Message}");
                await Task.Delay(1500);

                ShowLoadingState(false);
                ShowControlButtons(false);
                if (FindName("AddToLibraryButton") is Button btnError)
                    btnError.IsEnabled = true;
            }
            finally
            {
                _downloadCancellationTokenSource?.Dispose();
                _downloadCancellationTokenSource = null;
                _isPaused = false;
            }
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
                    Logger.Log($"[GameBypassDetailView] Found main game executable at root: {largestExe}");
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
                    Logger.Log($"[GameBypassDetailView] Found game executable (non-dlc) at: {largestExe}");
                    return exeFolder;
                }

                // Step 3: Jika tidak ada exe, gunakan root game folder sebagai fallback
                Logger.Log($"[GameBypassDetailView] No .exe found, using root game folder as fallback: {gameFolderPath}");
                return gameFolderPath;
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] Error finding game executable: {ex.Message}");
                // Fallback ke root folder jika ada error
                return gameFolderPath;
            }
        }

        private string? FindBestTargetFolder(string gameFolderPath, string extractDir)
        {
            if (TryUseRootReplaceFolder(gameFolderPath, extractDir))
            {
                Logger.Log($"[GameBypassDetailView] Applying extracted files to root game folder: {gameFolderPath}");
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

                    var parts = relPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        string topLevelPath = Path.Combine(gameFolderPath, parts[0]);
                        if (File.Exists(topLevelPath) || Directory.Exists(topLevelPath))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameBypassDetailView] Error checking root match target folder: {ex.Message}");
            }

            return false;
        }
    }
}
#pragma warning restore CS0103 // The name does not exist
