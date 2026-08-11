using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SteamPluginManager.Views
{
    public partial class OnlineFixDetailView : UserControl
    {
        private readonly string _thumbnailCacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamPluginManager", "ThumbnailCache");

        private OnlineFixFile? _currentFile;
        
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
        }
        private void OnlineFixDetailView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                RegisterTextElements();
                DisplayFileDetails();
            }
            catch
            {
                // Log error
            }
        }
        public void SetFile(OnlineFixFile file)
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
                    detailTitle.Text = "Online Fix Details";
                if (FindName("DetailSubtitle") is TextBlock detailSubtitle)
                    detailSubtitle.Text = "Multiplayer/Online Fix Information";
                if (FindName("AddToLibraryButton") is Button addToLibraryButton)
                    addToLibraryButton.Content = "Apply Online Fix";
                if (FindName("BackButtonText") is TextBlock backButtonText)
                    backButtonText.Text = "← Back";
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
                    MessageBox.Show("No file selected", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                _ = LoadBannerAsync();
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
                MessageBox.Show($"Error displaying details.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateAddToLibraryButton()
        {
            try
            {
                if (_currentFile == null)
                    return;
                if (FindName("AddToLibraryButton") is Button addBtn)
                {
                    addBtn.Content = "Apply Online Fix";
                    addBtn.IsEnabled = true;
                }
            }
            catch { }
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
                    MessageBox.Show("No file selected", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (FindName("AddToLibraryButton") is Button addBtn)
                    addBtn.IsEnabled = false;

                // Cek apakah game sudah terinstal
                string? gameFolder = null;
                bool isInstalled = false;
                if (_currentFile.AppId != null)
                {
                    var mainWindow = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w is SteamPluginManager.MainWindow) as SteamPluginManager.MainWindow;
                    if (mainWindow != null)
                    {
                        gameFolder = mainWindow.GetGameInstallationPath(_currentFile.AppId.Value);
                        isInstalled = !string.IsNullOrEmpty(gameFolder) && Directory.Exists(gameFolder);
                    }
                }

                if (!isInstalled)
                {
                    ShowLoadingState(true, "Game not installed!");
                    ShowResultNotification(false, "✗ Game not installed!");
                    await Task.Delay(1500);
                    ShowLoadingState(false);
                    if (FindName("AddToLibraryButton") is Button btnError)
                        btnError.IsEnabled = true;
                    return;
                }

                // Download file dari R2
                string r2Key = _currentFile.FileName;
                string tempZip = Path.Combine(Path.GetTempPath(), $"onlinefix_{Guid.NewGuid()}.zip");
                ShowLoadingState(true, "Downloading online fix from Cloudflare R2...", GetCurrentFileSizeDetail());
                UpdateProgressStep(1);
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
                                    ? $"Downloading online fix ({_currentFile.FileSize})... {p}%"
                                    : $"Downloading online fix... {p}%";
                        });
                    });

                    byte[] zipBytes = await SteamPluginManager.R2Config.DownloadFileAsync(r2Key, progress);
                    await File.WriteAllBytesAsync(tempZip, zipBytes);
                }
                catch
                {
                    ShowResultNotification(false, "✗ Download failed.");
                    await Task.Delay(1500);
                    ShowLoadingState(false);
                    if (FindName("AddToLibraryButton") is Button btnError)
                        btnError.IsEnabled = true;
                    return;
                }

                // Ekstrak file ZIP
                ShowLoadingState(true, "Extracting files...");
                UpdateProgressStep(2);
                string extractDir = Path.Combine(Path.GetTempPath(), $"onlinefix_extract_{Guid.NewGuid()}");
                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, extractDir);

                // Cari target folder yang tepat berdasarkan hasil ekstrak
                ShowLoadingState(true, "Checking target folder...");
                UpdateProgressStep(3);
                string? targetFolder = FindBestTargetFolder(gameFolder!, extractDir);
                if (string.IsNullOrEmpty(targetFolder))
                {
                    ShowResultNotification(false, "✗ Cannot determine target folder!");
                    await Task.Delay(1500);
                    ShowLoadingState(false);
                    if (FindName("AddToLibraryButton") is Button btnExeError)
                        btnExeError.IsEnabled = true;
                    return;
                }

                // Copy file hasil ekstrak ke target folder
                ShowLoadingState(true, "Moving files to game folder...");
                UpdateProgressStep(3);
                foreach (var file in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
                {
                    string relPath = Path.GetRelativePath(extractDir, file);
                    string targetPath = Path.Combine(targetFolder, relPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    
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
                UpdateProgressStep(4, true);
                Dispatcher.Invoke(() => ShowResultNotification(true, $"✓ Successfully applied online fix to '{_currentFile.Name}'!"));
                await Task.Delay(1500);
                ShowLoadingState(false);
                if (FindName("AddToLibraryButton") is Button btnSuccess)
                    btnSuccess.IsEnabled = true;
            }
            catch
            {
                ShowResultNotification(false, $"✗ Error applying online fix.");
                await Task.Delay(1500);
                ShowLoadingState(false);
                if (FindName("AddToLibraryButton") is Button btnError)
                    btnError.IsEnabled = true;
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
    }
}
