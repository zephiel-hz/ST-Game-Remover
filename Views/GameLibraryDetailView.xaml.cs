using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamPluginManager.Models;
using SteamPluginManager.Services.API;
using SteamPluginManager.Services.Steam;
using SteamPluginManager.UI.Dialogs;
using SteamPluginManager;
using System.Windows.Input;
using System.Windows.Threading;
using System.Diagnostics;

namespace SteamPluginManager.Views
{
    public partial class GameLibraryDetailView : UserControl
    {
        private readonly GameEntry _game;
        private static readonly HttpClient _httpClient = new HttpClient();
        private DispatcherTimer? _downloadTrackerTimer;
        private long _liveBytesDownloaded = 0;
        private long _totalBytes = 0;
        private double _currentSpeedMBps = 0.0;
        private SteamAppState _currentAppState = SteamAppState.NotInstalled;
        private int _syncCounter = 0;

        private Process? _runningProcess;
        private bool _isGameRunning = false;
        private DispatcherTimer? _gameRunningMonitorTimer;
        private HashSet<string>? _cachedGameExeNames;

        public GameLibraryDetailView(GameEntry game)
        {
            InitializeComponent();
            _game = game ?? throw new ArgumentNullException(nameof(game));
            Loaded += GameLibraryDetailView_Loaded;
            Unloaded += GameLibraryDetailView_Unloaded;
        }

        private void GameLibraryDetailView_Unloaded(object sender, RoutedEventArgs e)
        {
            StopDownloadTracker();
            StopGameRunningMonitor();
        }

        private async void GameLibraryDetailView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                RefreshInstallationState();
                PopulateBasicInfo();
                LoadBackgroundArtwork();
                var detailsTask = LoadDetailsAsync();
                var dlcTask = LoadDlcListAsync();
                await Task.WhenAll(detailsTask, dlcTask);
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryDetailView] Error in loaded event: {ex.Message}");
            }
        }

        private void RefreshInstallationState()
        {
            var progress = SteamHelper.GetGameDownloadProgress(_game.AppId);

            if (progress.ParentAppId.HasValue && !_game.ParentAppId.HasValue)
            {
                _game.ParentAppId = progress.ParentAppId;
                PopulateBasicInfo();
            }

            if (progress.State == SteamAppState.Downloading || progress.State == SteamAppState.Paused || progress.State == SteamAppState.Validating)
            {
                _game.IsInstalled = false;
                _game.InstallationPath = progress.InstallPath;
                ApplyDownloadingState(progress);
                StartDownloadTracker();
            }
            else if (progress.State == SteamAppState.Installed)
            {
                _game.IsInstalled = true;
                _game.InstallationPath = progress.InstallPath;
                StopDownloadTracker();
                ApplyInstalledState(progress.InstallPath);
            }
            else
            {
                _game.IsInstalled = false;
                _game.InstallationPath = null;
                StopDownloadTracker();
                ApplyNotInstalledState();
            }
        }

        private void ApplyInstalledState(string? foundPath)
        {
            PlayButton.Visibility = Visibility.Visible;
            InstallButton.Visibility = Visibility.Collapsed;
            DownloadProgressPanel.Visibility = Visibility.Collapsed;

            // Show relevant action buttons when game is installed
            BrowseFilesBtn.Visibility = Visibility.Visible;
            BrowseFilesBtn.IsEnabled = true;
            VerifyFilesBtn.Visibility = Visibility.Visible;
            VerifyFilesBtn.IsEnabled = true;
            CreateShortcutBtn.Visibility = Visibility.Visible;
            CreateShortcutBtn.IsEnabled = true;
            CustomLaunchBtn.Visibility = Visibility.Visible;
            CustomLaunchBtn.IsEnabled = true;
            UninstallBtn.Visibility = Visibility.Visible;

            InstalledStatusBadge.Background = new SolidColorBrush(Color.FromArgb(45, 16, 185, 129));
            InstalledStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            InstalledStatusText.Text = "INSTALLED";
            InstalledStatusText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            InstalledStatusIcon.Text = "▶";
            InstalledStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));

            InstallLocationPanel.Visibility = Visibility.Visible;
            InstallPathText.Text = foundPath ?? "-";
            if (_game.ParentAppId.HasValue)
            {
                InstallPathText.ToolTip = $"Installed under Base Game (AppID: {_game.ParentAppId.Value})\nPath: {foundPath}";
            }
            SpecInstallPath.Text = foundPath ?? "-";

            UpdateCustomLaunchVisualState();

            // Cache game executable names in background for fast process matching
            if (_cachedGameExeNames == null && !string.IsNullOrWhiteSpace(foundPath))
            {
                Task.Run(() =>
                {
                    var names = CustomLaunchService.GetGameExecutableNames(foundPath);
                    Dispatcher.Invoke(() => _cachedGameExeNames = names);
                });
            }

            StartGameRunningMonitor();
            CheckAndUpdateRunningState();
        }

        private void ApplyNotInstalledState()
        {
            StopGameRunningMonitor();
            _isGameRunning = false;

            PlayButton.Visibility = Visibility.Collapsed;
            InstallButton.Visibility = Visibility.Visible;
            DownloadProgressPanel.Visibility = Visibility.Collapsed;

            // Completely hide unneeded buttons when not installed to eliminate clutter
            BrowseFilesBtn.Visibility = Visibility.Collapsed;
            VerifyFilesBtn.Visibility = Visibility.Collapsed;
            CreateShortcutBtn.Visibility = Visibility.Collapsed;
            CustomLaunchBtn.Visibility = Visibility.Collapsed;
            CustomLaunchIndicatorBadge.Visibility = Visibility.Collapsed;
            UninstallBtn.Visibility = Visibility.Collapsed;

            InstalledStatusBadge.Background = new SolidColorBrush(Color.FromArgb(45, 156, 163, 175));
            InstalledStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            InstalledStatusText.Text = "NOT INSTALLED";
            InstalledStatusText.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            InstalledStatusIcon.Text = "📥";
            InstalledStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));

            InstallLocationPanel.Visibility = Visibility.Collapsed;
            SpecInstallPath.Text = "Not Installed";
        }

        private void ApplyDownloadingState(SteamDownloadProgress progress)
        {
            StopGameRunningMonitor();
            _isGameRunning = false;

            PlayButton.Visibility = Visibility.Collapsed;
            InstallButton.Visibility = Visibility.Collapsed;
            DownloadProgressPanel.Visibility = Visibility.Visible;

            // Completely hide unneeded buttons during active download
            BrowseFilesBtn.Visibility = Visibility.Collapsed;
            VerifyFilesBtn.Visibility = Visibility.Collapsed;
            CreateShortcutBtn.Visibility = Visibility.Collapsed;
            CustomLaunchBtn.Visibility = Visibility.Collapsed;
            CustomLaunchIndicatorBadge.Visibility = Visibility.Collapsed;
            UninstallBtn.Visibility = Visibility.Collapsed;

            InstallLocationPanel.Visibility = Visibility.Collapsed;
            SpecInstallPath.Text = progress.State == SteamAppState.Paused ? "Download Paused" : "Downloading...";

            _totalBytes = progress.BytesTotal;
            _currentAppState = progress.State;

            if (progress.BytesDownloaded > _liveBytesDownloaded || _liveBytesDownloaded == 0)
            {
                _liveBytesDownloaded = progress.BytesDownloaded;
            }

            if (progress.State == SteamAppState.Downloading && progress.DownloadRateMBps > 0.05)
            {
                _currentSpeedMBps = progress.DownloadRateMBps;
            }

            UpdateDownloadPanelStatus(progress.State);
            UpdateDownloadProgressUI();
        }

        private void UpdateDownloadPanelStatus(SteamAppState state)
        {
            _currentAppState = state;
            if (state == SteamAppState.Paused)
            {
                InstalledStatusBadge.Background = new SolidColorBrush(Color.FromArgb(45, 234, 179, 8));
                InstalledStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(234, 179, 8));
                InstalledStatusText.Text = "DOWNLOAD PAUSED";
                InstalledStatusText.Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21));
                InstalledStatusIcon.Text = "⏸";
                InstalledStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21));

                DownloadStatusIcon.Text = "⏸";
                DownloadStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21));
                DownloadStatusLabel.Text = "DOWNLOAD PAUSED ON STEAM";
                DownloadStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21));
                DownloadProgressPanel.BorderBrush = new SolidColorBrush(Color.FromRgb(234, 179, 8));
            }
            else if (state == SteamAppState.Validating)
            {
                InstalledStatusBadge.Background = new SolidColorBrush(Color.FromArgb(45, 168, 85, 247));
                InstalledStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(168, 85, 247));
                InstalledStatusText.Text = "VALIDATING FILES";
                InstalledStatusText.Foreground = new SolidColorBrush(Color.FromRgb(192, 132, 252));
                InstalledStatusIcon.Text = "🔄";
                InstalledStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(192, 132, 252));

                DownloadStatusIcon.Text = "🔄";
                DownloadStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(192, 132, 252));
                DownloadStatusLabel.Text = "VALIDATING FILES ON STEAM...";
                DownloadStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(192, 132, 252));
                DownloadProgressPanel.BorderBrush = new SolidColorBrush(Color.FromRgb(168, 85, 247));
            }
            else
            {
                InstalledStatusBadge.Background = new SolidColorBrush(Color.FromArgb(45, 2, 132, 199));
                InstalledStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(2, 132, 199));
                InstalledStatusText.Text = "DOWNLOADING";
                InstalledStatusText.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                InstalledStatusIcon.Text = "📥";
                InstalledStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));

                DownloadStatusIcon.Text = "📥";
                DownloadStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                DownloadStatusLabel.Text = "DOWNLOADING VIA STEAM...";
                DownloadStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                DownloadProgressPanel.BorderBrush = new SolidColorBrush(Color.FromRgb(2, 132, 199));
            }
        }

        private void UpdateDownloadProgressUI()
        {
            double pct = _totalBytes > 0
                ? Math.Min(100.0, Math.Round((double)_liveBytesDownloaded / _totalBytes * 100.0, 1))
                : 0.0;

            DownloadProgressBar.Value = pct;
            DownloadPercentText.Text = $"{pct:F1}%";
            DownloadBytesText.Text = $"{SteamDownloadProgress.FormatBytes(_liveBytesDownloaded)} / {SteamDownloadProgress.FormatBytes(_totalBytes)}";

            if (_currentAppState == SteamAppState.Paused)
            {
                DownloadSpeedText.Text = "• PAUSED";
                DownloadSpeedText.Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21));
                DownloadEtaText.Text = "";
            }
            else if (_currentAppState == SteamAppState.Validating)
            {
                DownloadSpeedText.Text = "• VALIDATING";
                DownloadSpeedText.Foreground = new SolidColorBrush(Color.FromRgb(192, 132, 252));
                DownloadEtaText.Text = "";
            }
            else if (_currentSpeedMBps > 0.05)
            {
                DownloadSpeedText.Text = $"• ↓ {_currentSpeedMBps:F1} MB/s";
                DownloadSpeedText.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));

                long remaining = Math.Max(0, _totalBytes - _liveBytesDownloaded);
                double bytesPerSec = _currentSpeedMBps * 1024.0 * 1024.0;
                if (bytesPerSec > 0 && remaining > 0)
                {
                    var eta = TimeSpan.FromSeconds(remaining / bytesPerSec);
                    if (eta.TotalHours >= 1)
                    {
                        DownloadEtaText.Text = $"• ETA: {(int)eta.TotalHours}h {eta.Minutes}m";
                    }
                    else
                    {
                        DownloadEtaText.Text = $"• ETA: {eta.Minutes}m {eta.Seconds:D2}s";
                    }
                }
                else
                {
                    DownloadEtaText.Text = "";
                }
            }
            else
            {
                DownloadSpeedText.Text = "• STARTING...";
                DownloadSpeedText.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                DownloadEtaText.Text = "";
            }
        }

        private void StartDownloadTracker()
        {
            if (_downloadTrackerTimer == null)
            {
                _downloadTrackerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _downloadTrackerTimer.Tick += (s, e) =>
                {
                    try
                    {
                        _syncCounter++;

                        // 1. Every 2 seconds, check Steam manifest & logs for state changes
                        if (_syncCounter >= 2)
                        {
                            _syncCounter = 0;
                            var p = SteamHelper.GetGameDownloadProgress(_game.AppId);
                            if (p.State == SteamAppState.Installed)
                            {
                                RefreshInstallationState();
                                return;
                            }

                            if (p.State == SteamAppState.NotInstalled && _liveBytesDownloaded == 0)
                            {
                                RefreshInstallationState();
                                return;
                            }

                            if (p.BytesDownloaded > _liveBytesDownloaded)
                            {
                                _liveBytesDownloaded = p.BytesDownloaded;
                            }
                            _totalBytes = p.BytesTotal;
                            _currentAppState = p.State;
                            UpdateDownloadPanelStatus(p.State);
                        }

                        // 2. Real-time sub-second traffic tracking
                        if (_currentAppState == SteamAppState.Downloading)
                        {
                            (double speedMBps, long bytesDelta) = SteamProcessMonitor.SampleDownloadTraffic();
                            if (speedMBps > 0.05)
                            {
                                _currentSpeedMBps = speedMBps;
                                _liveBytesDownloaded += bytesDelta;
                                _liveBytesDownloaded = Math.Min(_totalBytes, _liveBytesDownloaded);
                            }
                            else
                            {
                                // Fallback: check log download rate
                                double logMbps = SteamHelper.GetLiveDownloadRateMbps();
                                if (logMbps > 0.05)
                                {
                                    double logMBps = (logMbps * 1_000_000.0) / (8.0 * 1024.0 * 1024.0);
                                    _currentSpeedMBps = logMBps;
                                    long logBytesSec = (long)((logMbps * 1_000_000.0) / 8.0);
                                    _liveBytesDownloaded += logBytesSec;
                                    _liveBytesDownloaded = Math.Min(_totalBytes, _liveBytesDownloaded);
                                }
                                else
                                {
                                    _currentSpeedMBps = 0.0;
                                }
                            }
                        }
                        else
                        {
                            _currentSpeedMBps = 0.0;
                        }

                        UpdateDownloadProgressUI();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[GameLibraryDetailView] Download tracker tick error: {ex.Message}");
                    }
                };
            }

            if (!_downloadTrackerTimer.IsEnabled)
            {
                _syncCounter = 0;
                _downloadTrackerTimer.Start();
            }
        }

        private void StopDownloadTracker()
        {
            _downloadTrackerTimer?.Stop();
        }

        private void PopulateBasicInfo()
        {
            GameTitleText.Text = _game.Name;
            HeroAppIdText.Text = $"AppID: {_game.AppId}";

            if (!string.IsNullOrWhiteSpace(_game.Genre) && _game.Genre != "Loading...")
            {
                HeroGenreText.Text = _game.Genre;
                HeroGenreBorder.Visibility = Visibility.Visible;
            }
            else
            {
                HeroGenreBorder.Visibility = Visibility.Collapsed;
            }

            if (_game.ParentAppId.HasValue)
            {
                string? parentName = CacheManager.GetGameName(_game.ParentAppId.Value, "en");
                HeroBaseGameText.Text = !string.IsNullOrWhiteSpace(parentName)
                    ? $"Base Game: {parentName}"
                    : $"Base Game: {_game.ParentAppId.Value}";
                HeroBaseGameBorder.Visibility = Visibility.Visible;
            }
            else
            {
                HeroBaseGameBorder.Visibility = Visibility.Collapsed;
            }

            SpecLuaPath.Text = string.IsNullOrWhiteSpace(_game.FilePath) ? "Not available" : _game.FilePath;
        }

        private void LoadBackgroundArtwork()
        {
            try
            {
                string imagesDir = Path.Combine(CacheManager.CacheDirectory, "Images");
                Directory.CreateDirectory(imagesDir);

                string heroLocalPath = Path.Combine(imagesDir, $"{_game.AppId}_hero.jpg");
                string headerLocalPath = Path.Combine(imagesDir, $"{_game.AppId}.jpg");

                string? parentHeroLocalPath = _game.ParentAppId.HasValue
                    ? Path.Combine(imagesDir, $"{_game.ParentAppId.Value}_hero.jpg")
                    : null;
                string? parentHeaderLocalPath = _game.ParentAppId.HasValue
                    ? Path.Combine(imagesDir, $"{_game.ParentAppId.Value}.jpg")
                    : null;

                // 1. INSTANT LOCAL CACHE DISPLAY (0ms, no network delay, prevents black screen)
                bool displayedFromCache = false;
                if (File.Exists(heroLocalPath) && new FileInfo(heroLocalPath).Length > 0)
                {
                    SetHeroImageFromFile(heroLocalPath);
                    displayedFromCache = true;
                }
                else if (!string.IsNullOrWhiteSpace(_game.ScreenshotUrl) && File.Exists(_game.ScreenshotUrl) && new FileInfo(_game.ScreenshotUrl).Length > 0)
                {
                    SetHeroImageFromFile(_game.ScreenshotUrl);
                    displayedFromCache = true;
                }
                else if (File.Exists(headerLocalPath) && new FileInfo(headerLocalPath).Length > 0)
                {
                    SetHeroImageFromFile(headerLocalPath);
                    displayedFromCache = true;
                }
                else if (parentHeroLocalPath != null && File.Exists(parentHeroLocalPath) && new FileInfo(parentHeroLocalPath).Length > 0)
                {
                    SetHeroImageFromFile(parentHeroLocalPath);
                    displayedFromCache = true;
                }
                else if (parentHeaderLocalPath != null && File.Exists(parentHeaderLocalPath) && new FileInfo(parentHeaderLocalPath).Length > 0)
                {
                    SetHeroImageFromFile(parentHeaderLocalPath);
                    displayedFromCache = true;
                }

                // If high-resolution hero art is already on disk, we are 100% done!
                if (File.Exists(heroLocalPath) && new FileInfo(heroLocalPath).Length > 0)
                {
                    Logger.Log($"[GameLibraryDetailView] Using cached hero artwork for '{_game.Name}' ({_game.AppId})");
                    return;
                }

                // 2. ASYNC BACKGROUND FETCH & LOCAL DISK PERSISTENCE
                string heroCdnUrl = $"https://steamcdn-a.akamaihd.net/steam/apps/{_game.AppId}/library_hero.jpg";
                string fallbackUrl = !string.IsNullOrWhiteSpace(_game.ScreenshotUrl) && _game.ScreenshotUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? _game.ScreenshotUrl
                    : $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_game.AppId}/header.jpg";

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // A. Try fetching library_hero.jpg
                        using var response = await _httpClient.GetAsync(heroCdnUrl);
                        if (response.IsSuccessStatusCode)
                        {
                            var bytes = await response.Content.ReadAsByteArrayAsync();
                            if (bytes.Length > 0)
                            {
                                await File.WriteAllBytesAsync(heroLocalPath, bytes);
                                Dispatcher.Invoke(() => SetHeroImageFromFile(heroLocalPath));
                                Logger.Log($"[GameLibraryDetailView] Downloaded & cached hero artwork for '{_game.Name}' ({_game.AppId})");
                                return;
                            }
                        }

                        // B. If hero not found (e.g. 404), check if parent has hero
                        if (_game.ParentAppId.HasValue)
                        {
                            string parentHeroUrl = $"https://steamcdn-a.akamaihd.net/steam/apps/{_game.ParentAppId.Value}/library_hero.jpg";
                            using var parentResp = await _httpClient.GetAsync(parentHeroUrl);
                            if (parentResp.IsSuccessStatusCode)
                            {
                                var bytes = await parentResp.Content.ReadAsByteArrayAsync();
                                if (bytes.Length > 0 && parentHeroLocalPath != null)
                                {
                                    await File.WriteAllBytesAsync(parentHeroLocalPath, bytes);
                                    Dispatcher.Invoke(() => SetHeroImageFromFile(parentHeroLocalPath));
                                    return;
                                }
                            }
                        }

                        // C. Fallback to header.jpg if no local image is displayed yet
                        if (!displayedFromCache && !File.Exists(headerLocalPath))
                        {
                            using var fbResp = await _httpClient.GetAsync(fallbackUrl);
                            if (fbResp.IsSuccessStatusCode)
                            {
                                var bytes = await fbResp.Content.ReadAsByteArrayAsync();
                                if (bytes.Length > 0)
                                {
                                    await File.WriteAllBytesAsync(headerLocalPath, bytes);
                                    Dispatcher.Invoke(() => SetHeroImageFromFile(headerLocalPath));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[GameLibraryDetailView] Background hero fetch error: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryDetailView] Background image error: {ex.Message}");
            }
        }

        private void SetHeroImageFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(filePath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                BackgroundHeroImage.Source = bmp;
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryDetailView] Error setting hero image from {filePath}: {ex.Message}");
            }
        }

        private async Task LoadDetailsAsync()
        {
            try
            {
                // 1. Try from local cache first
                string? cachedDesc = CacheManager.GetGameDescription(_game.AppId, "en");
                string? cachedRelease = CacheManager.GetGameReleaseDate(_game.AppId);
                string? cachedPub = CacheManager.GetGamePublisher(_game.AppId);
                string? cachedDrm = CacheManager.GetGameDRM(_game.AppId);
                string? cachedAntiCheat = CacheManager.GetGameAntiCheat(_game.AppId);

                // If sub-DLC has no description in cache, check parent game cache as fallback
                if (string.IsNullOrWhiteSpace(cachedDesc) && _game.ParentAppId.HasValue)
                {
                    cachedDesc = CacheManager.GetGameDescription(_game.ParentAppId.Value, "en");
                    cachedRelease ??= CacheManager.GetGameReleaseDate(_game.ParentAppId.Value);
                    cachedPub ??= CacheManager.GetGamePublisher(_game.ParentAppId.Value);
                    cachedDrm ??= CacheManager.GetGameDRM(_game.ParentAppId.Value);
                    cachedAntiCheat ??= CacheManager.GetGameAntiCheat(_game.ParentAppId.Value);
                }

                if (!string.IsNullOrWhiteSpace(cachedDesc))
                {
                    ApplyDetailsUI(cachedDesc, cachedRelease, cachedPub, cachedDrm, cachedAntiCheat);
                    return; // Loaded instantly from cache!
                }

                // 2. Fetch from Steam Store API (Only when not in cache)
                int fetchAppId = _game.AppId;
                string url = $"https://store.steampowered.com/api/appdetails?appids={fetchAppId}&l=english";
                string json;
                try
                {
                    json = await _httpClient.GetStringAsync(url);
                }
                catch
                {
                    if (_game.ParentAppId.HasValue)
                    {
                        fetchAppId = _game.ParentAppId.Value;
                        url = $"https://store.steampowered.com/api/appdetails?appids={fetchAppId}&l=english";
                        json = await _httpClient.GetStringAsync(url);
                    }
                    else
                    {
                        throw;
                    }
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(fetchAppId.ToString(), out var appProp) &&
                    appProp.TryGetProperty("success", out var successProp) && successProp.GetBoolean() &&
                    appProp.TryGetProperty("data", out var data))
                {
                    string desc = "";
                    if (data.TryGetProperty("short_description", out var shortDesc))
                    {
                        desc = shortDesc.GetString() ?? "";
                    }
                    else if (data.TryGetProperty("detailed_description", out var detailedDesc))
                    {
                        desc = System.Text.RegularExpressions.Regex.Replace(detailedDesc.GetString() ?? "", "<.*?>", string.Empty);
                    }

                    string releaseDate = "";
                    if (data.TryGetProperty("release_date", out var releaseObj) && releaseObj.TryGetProperty("date", out var dateProp))
                    {
                        releaseDate = dateProp.GetString() ?? "";
                    }

                    string publisher = "";
                    if (data.TryGetProperty("publishers", out var pubArr) && pubArr.ValueKind == JsonValueKind.Array && pubArr.GetArrayLength() > 0)
                    {
                        var firstPub = pubArr[0];
                        publisher = firstPub.ValueKind == JsonValueKind.String ? firstPub.GetString() ?? "" : firstPub.ToString();
                    }

                    string drm = "";
                    if (data.TryGetProperty("drm_notice", out var drmNotice))
                    {
                        drm = drmNotice.GetString() ?? "";
                    }

                    string antiCheat = "";
                    if (data.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                    {
                        var acList = new List<string>();
                        foreach (var cat in cats.EnumerateArray())
                        {
                            if (cat.TryGetProperty("description", out var catDesc))
                            {
                                string? text = catDesc.GetString();
                                if (text != null && (text.Contains("Anti-Cheat") || text.Contains("VAC")))
                                {
                                    acList.Add(text);
                                }
                            }
                        }
                        if (acList.Count > 0) antiCheat = string.Join(", ", acList);
                    }

                    CacheManager.SaveGameDetails(_game.AppId, desc, _game.ScreenshotUrl ?? "", releaseDate, publisher, drm, antiCheat, "en");
                    ApplyDetailsUI(desc, releaseDate, publisher, drm, antiCheat);
                }
                else
                {
                    // Fallback to parent if primary app has no data
                    if (_game.ParentAppId.HasValue && fetchAppId != _game.ParentAppId.Value)
                    {
                        string pUrl = $"https://store.steampowered.com/api/appdetails?appids={_game.ParentAppId.Value}&l=english";
                        var pJson = await _httpClient.GetStringAsync(pUrl);
                        using var pDoc = JsonDocument.Parse(pJson);
                        if (pDoc.RootElement.TryGetProperty(_game.ParentAppId.Value.ToString(), out var pAppProp) &&
                            pAppProp.TryGetProperty("success", out var pSuccess) && pSuccess.GetBoolean() &&
                            pAppProp.TryGetProperty("data", out var pData))
                        {
                            string pDesc = pData.TryGetProperty("short_description", out var sd) ? sd.GetString() ?? "" : "";
                            CacheManager.SaveGameDetails(_game.AppId, pDesc, _game.ScreenshotUrl ?? "", "", "", "", "", "en");
                            ApplyDetailsUI(pDesc, "", "", "", "");
                            return;
                        }
                    }
                    GameDescriptionText.Text = "No description available for this title.";
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryDetailView] Failed to load details: {ex.Message}");
                if (string.IsNullOrWhiteSpace(GameDescriptionText.Text) || GameDescriptionText.Text == "Loading game description...")
                {
                    GameDescriptionText.Text = "No description available for this title.";
                }
            }
        }

        private void ApplyDetailsUI(string? desc, string? releaseDate, string? publisher, string? drm, string? antiCheat)
        {
            Dispatcher.Invoke(() =>
            {
                GameDescriptionText.Text = string.IsNullOrWhiteSpace(desc) ? "No description available for this title." : desc;
                SpecReleaseDate.Text = string.IsNullOrWhiteSpace(releaseDate) ? "-" : releaseDate;
                SpecPublisher.Text = string.IsNullOrWhiteSpace(publisher) ? "-" : publisher;
                SpecDrm.Text = string.IsNullOrWhiteSpace(drm) ? "Standard Steam DRM" : drm;
                SpecAntiCheat.Text = string.IsNullOrWhiteSpace(antiCheat) ? "None reported" : antiCheat;
            });
        }

        private async Task LoadDlcListAsync()
        {
            try
            {
                // 1. INSTANT LOCAL CACHE DISPLAY (0ms)
                var cachedDlc = CacheManager.GetCachedDlcItems(_game.AppId);
                if ((cachedDlc == null || cachedDlc.Count == 0) && _game.ParentAppId.HasValue)
                {
                    cachedDlc = CacheManager.GetCachedDlcItems(_game.ParentAppId.Value);
                }

                if (cachedDlc != null && cachedDlc.Count > 0)
                {
                    ApplyDlcListUI(cachedDlc);
                }

                // 2. BACKGROUND FRESH FETCH FROM SUPABASE
                var dlcItems = await SupabaseDlcService.GetDlcItemsAsync(_game.AppId);
                if ((dlcItems == null || dlcItems.Count == 0) && _game.ParentAppId.HasValue)
                {
                    dlcItems = await SupabaseDlcService.GetDlcItemsAsync(_game.ParentAppId.Value);
                }

                if (dlcItems != null && dlcItems.Count > 0)
                {
                    CacheManager.SaveCachedDlcItems(_game.AppId, dlcItems);
                    ApplyDlcListUI(dlcItems);
                }
                else if (cachedDlc == null || cachedDlc.Count == 0)
                {
                    ApplyDlcListUI(new List<DLCItem>());
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryDetailView] Error loading DLCs: {ex.Message}");
            }
        }

        private void ApplyDlcListUI(List<DLCItem> dlcItems)
        {
            Dispatcher.Invoke(() =>
            {
                if (dlcItems != null && dlcItems.Count > 0)
                {
                    DlcItemsControl.ItemsSource = dlcItems;
                    DlcCountBadge.Text = $"{dlcItems.Count} DLCs";
                    NoDlcPlaceholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    DlcItemsControl.ItemsSource = null;
                    DlcCountBadge.Text = "0 DLCs";
                    NoDlcPlaceholder.Visibility = Visibility.Visible;
                }
            });
        }

        #region Game Running State & Process Monitoring

        private void StartGameRunningMonitor()
        {
            if (_gameRunningMonitorTimer == null)
            {
                _gameRunningMonitorTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1.5)
                };
                _gameRunningMonitorTimer.Tick += GameRunningMonitorTimer_Tick;
            }

            if (!_gameRunningMonitorTimer.IsEnabled)
            {
                _gameRunningMonitorTimer.Start();
            }
        }

        private void StopGameRunningMonitor()
        {
            _gameRunningMonitorTimer?.Stop();
        }

        private void GameRunningMonitorTimer_Tick(object? sender, EventArgs e)
        {
            CheckAndUpdateRunningState();
        }

        private void CheckAndUpdateRunningState()
        {
            if (!_game.IsInstalled)
            {
                if (_isGameRunning) SetRunningVisualState(false);
                return;
            }

            bool isRunning = CustomLaunchService.IsGameRunning(
                _game.AppId,
                _game.ParentAppId,
                _game.InstallationPath,
                _runningProcess,
                _cachedGameExeNames
            );

            if (isRunning != _isGameRunning)
            {
                SetRunningVisualState(isRunning);
            }
        }

        private void SetRunningVisualState(bool isRunning)
        {
            _isGameRunning = isRunning;
            if (isRunning)
            {
                // STOP State
                if (TryFindResource("LauncherPrimaryStopButton") is Style stopStyle)
                {
                    PlayButton.Style = stopStyle;
                }
                PlayButtonIcon.Text = "⏹";
                PlayButtonText.Text = "STOP";
                PlayButton.ToolTip = "Stop running game";
                PlayButton.IsEnabled = true;

                // Hide actions that cannot/should not be performed while game is running
                VerifyFilesBtn.Visibility = Visibility.Collapsed;
                CustomLaunchBtn.Visibility = Visibility.Collapsed;
                UninstallBtn.Visibility = Visibility.Collapsed;

                // Status badge
                InstalledStatusBadge.Background = new SolidColorBrush(Color.FromArgb(45, 59, 130, 246));
                InstalledStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                InstalledStatusText.Text = "RUNNING";
                InstalledStatusText.Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250));
                InstalledStatusIcon.Text = "⚡";
                InstalledStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250));
            }
            else
            {
                // PLAY State
                if (TryFindResource("LauncherPrimaryPlayButton") is Style playStyle)
                {
                    PlayButton.Style = playStyle;
                }
                PlayButtonIcon.Text = "▶";
                PlayButtonText.Text = "PLAY";
                PlayButton.IsEnabled = true;

                // Restore actions when game stops
                VerifyFilesBtn.Visibility = Visibility.Visible;
                CustomLaunchBtn.Visibility = Visibility.Visible;
                UninstallBtn.Visibility = Visibility.Visible;

                // Status badge
                InstalledStatusBadge.Background = new SolidColorBrush(Color.FromArgb(45, 16, 185, 129));
                InstalledStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                InstalledStatusText.Text = "INSTALLED";
                InstalledStatusText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                InstalledStatusIcon.Text = "▶";
                InstalledStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));

                UpdateCustomLaunchVisualState();
            }
        }

        #endregion

        #region User Actions

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToGameLibrary();
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isGameRunning)
            {
                // Stop the game
                PlayButton.IsEnabled = false;
                PlayButtonText.Text = "STOPPING...";

                await Task.Run(() =>
                {
                    CustomLaunchService.StopGame(
                        _game.AppId,
                        _game.ParentAppId,
                        _game.InstallationPath,
                        _runningProcess,
                        _cachedGameExeNames
                    );
                    _runningProcess = null;
                });

                await Task.Delay(300);
                CheckAndUpdateRunningState();
            }
            else
            {
                // Launch the game
                PlayButton.IsEnabled = false;
                PlayButtonText.Text = "LAUNCHING...";

                var (success, msg, proc) = await Task.Run(() =>
                    CustomLaunchService.LaunchGameWithProcess(_game.AppId, _game.InstallationPath));

                if (!success)
                {
                    SetRunningVisualState(false);
                    ModernMessageBox.Show($"Failed to launch game: {msg}", "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _runningProcess = proc;
                if (_runningProcess != null)
                {
                    try
                    {
                        _runningProcess.EnableRaisingEvents = true;
                        _runningProcess.Exited += (s, args) =>
                        {
                            Dispatcher.Invoke(() => CheckAndUpdateRunningState());
                        };
                    }
                    catch { }
                }

                await Task.Delay(600);
                CheckAndUpdateRunningState();
            }
        }

        private void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            string msg = $"[GameLibraryDetailView] Clicked Install button for '{_game.Name}' (AppID: {_game.AppId})";
            System.Diagnostics.Debug.WriteLine(msg);
            Logger.Log(msg);

            SteamHelper.InstallGame(_game.AppId);
            StartDownloadTracker();
        }

        private void OpenSteamDownloads_Click(object sender, RoutedEventArgs e)
        {
            SteamHelper.OpenSteamDownloads();
        }

        private void BrowseFiles_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_game.InstallationPath))
            {
                SteamHelper.OpenGameFolder(_game.InstallationPath);
            }
        }

        private void VerifyFiles_Click(object sender, RoutedEventArgs e)
        {
            SteamHelper.VerifyGame(_game.AppId);
        }

        private void StorePage_Click(object sender, RoutedEventArgs e)
        {
            SteamHelper.OpenSteamStore(_game.AppId);
        }

        private void SteamDb_Click(object sender, RoutedEventArgs e)
        {
            SteamHelper.OpenSteamDb(_game.AppId);
        }

        private void CreateShortcut_Click(object sender, RoutedEventArgs e)
        {
            var (success, msg) = CustomLaunchService.CreateDesktopShortcut(_game.AppId, _game.Name, _game.InstallationPath);
            if (success)
            {
                ModernMessageBox.Show(msg, "Shortcut Created", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                ModernMessageBox.Show(msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CustomLaunch_Click(object sender, RoutedEventArgs e)
        {
            OpenCustomLaunchDialog();
        }

        private void CustomLaunchIndicatorBadge_Click(object sender, MouseButtonEventArgs e)
        {
            OpenCustomLaunchDialog();
        }

        private void OpenCustomLaunchDialog()
        {
            try
            {
                if (!_game.IsInstalled || string.IsNullOrWhiteSpace(_game.InstallationPath) || !Directory.Exists(_game.InstallationPath))
                {
                    ModernMessageBox.Show(
                        $"Game '{_game.Name}' is not installed.\n\nPlease install the game first before configuring Custom Launch.",
                        "Game Not Installed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var window = Window.GetWindow(this);
                var dialog = new CustomLaunchDialog(_game)
                {
                    Owner = window
                };

                dialog.ShowDialog();

                if (dialog.ConfigChanged)
                {
                    UpdateCustomLaunchVisualState();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryDetailView] Error opening custom launch dialog: {ex.Message}");
            }
        }

        private void UpdateCustomLaunchVisualState()
        {
            try
            {
                var config = CustomLaunchService.GetConfig(_game.AppId, _game.InstallationPath);
                bool isCustomActive = config != null && config.IsEnabled && !string.IsNullOrWhiteSpace(config.ExecutablePath);

                if (isCustomActive && _game.IsInstalled)
                {
                    string fileName = Path.GetFileName(config!.ExecutablePath);
                    CustomLaunchIndicatorBadge.Visibility = Visibility.Visible;
                    CustomLaunchBadgeText.Text = $"CUSTOM: {fileName}";
                    CustomLaunchActiveDot.Visibility = Visibility.Visible;
                    CustomLaunchBtnText.Text = "Custom Launch";

                    if (!_isGameRunning)
                    {
                        PlayButton.ToolTip = $"Launch {_game.Name} via custom target ({fileName})";
                    }
                }
                else
                {
                    CustomLaunchIndicatorBadge.Visibility = Visibility.Collapsed;
                    CustomLaunchActiveDot.Visibility = Visibility.Collapsed;
                    CustomLaunchBtnText.Text = "Custom Launch";

                    if (!_isGameRunning)
                    {
                        PlayButton.ToolTip = "Launch game immediately via Steam";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryDetailView] Error updating custom launch UI: {ex.Message}");
            }
        }

        private void UninstallGame_Click(object sender, RoutedEventArgs e)
        {
            var result = ModernMessageBox.Show($"Are you sure you want to uninstall {_game.Name} via Steam?\n\nThis will also cleanly remove leftover files and the game installation folder.", "Uninstall Game", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                string? gameFolder = SteamHelper.GetGameInstallationPath(_game.AppId, _game.Name);
                int appId = _game.AppId;

                SteamHelper.UninstallGame(appId);

                _ = Task.Run(async () =>
                {
                    Logger.Log($"[GameLibraryDetailView] Waiting for Steam uninstall of {appId} ({_game.Name})...");
                    var sw = Stopwatch.StartNew();
                    var timeout = TimeSpan.FromMinutes(10);
                    bool uninstalled = false;

                    // Initial wait for Steam protocol to trigger
                    await Task.Delay(2500);

                    while (sw.Elapsed < timeout)
                    {
                        if (!SteamHelper.IsSteamAppInstalled(appId))
                        {
                            uninstalled = true;
                            break;
                        }
                        await Task.Delay(1500);
                    }

                    if (uninstalled)
                    {
                        Logger.Log($"[GameLibraryDetailView] Steam reported uninstall complete for AppID {appId}. Cleaning leftover folder: {gameFolder}");
                        if (!string.IsNullOrEmpty(gameFolder) && Directory.Exists(gameFolder))
                        {
                            SteamHelper.DeleteGameFolderCleanly(gameFolder);
                        }

                        // Clean tracking records and custom launch settings
                        OnlineFixTrackerService.DeleteInstallRecord(appId);
                        CustomLaunchService.RemoveConfig(appId);

                        Logger.Log($"[GameLibraryDetailView] Game {appId} uninstalled and folder cleaned.");
                    }
                    else
                    {
                        Logger.Log($"[GameLibraryDetailView] Timed out waiting for Steam uninstall of {appId}.");
                    }

                    Dispatcher.Invoke(() =>
                    {
                        RefreshInstallationState();
                        WindowNavigator.RefreshGameList();
                    });
                });
            }
        }

        private void RemoveFromLibrary_Click(object sender, RoutedEventArgs e)
        {
            var result = ModernMessageBox.Show($"Are you sure you want to remove the unlocker Lua script for '{_game.Name}' from your library?\n\nFile: {_game.FilePath}", "Remove from Library", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    if (File.Exists(_game.FilePath))
                    {
                        File.Delete(_game.FilePath);
                    }
                    ModernMessageBox.Show($"Removed {_game.Name} unlocker script.", "Removed", MessageBoxButton.OK, MessageBoxImage.Information);
                    WindowNavigator.RefreshGameList();
                    WindowNavigator.NavigateToGameLibrary();
                }
                catch (Exception ex)
                {
                    ModernMessageBox.Show($"Failed to delete file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        #endregion
    }
}
