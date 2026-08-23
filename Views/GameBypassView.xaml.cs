#pragma warning disable CS0103 // The name does not exist
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Globalization;

namespace SteamPluginManager.Views
{
    public class BypassFile : System.ComponentModel.INotifyPropertyChanged
    {
        public int? Id { get; set; }
        public int? AppId { get; set; }
        public string FileName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string FileSize { get; set; } = "";
        public string UpdatedDate { get; set; } = "";
        public string FileUrl { get; set; } = "";
        public string Description { get; set; } = "";
        public string Genre { get; set; } = "";
        public DateTime? CreatedAt { get; set; }
        
        private string _thumbnailPath = "";
        public string ThumbnailPath 
        { 
            get => _thumbnailPath;
            set
            {
                if (_thumbnailPath != value)
                {
                    _thumbnailPath = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ThumbnailPath)));
                }
            }
        }
        
        private bool _isSelected = false;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }
        
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public partial class GameBypassView : UserControl
    {
        // Static cache to prevent refetching data when navigating back
        private static List<BypassFile>? _cachedFiles = null;
        private static DateTime _lastFetchTime = DateTime.MinValue;
        private static readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5); // Cache for 5 minutes

        public ObservableCollection<BypassFile> Files { get; set; }
        
        private System.ComponentModel.ICollectionView _filesView;
        
        private readonly HttpClient _httpClient = new();
        
        private readonly string _thumbnailCacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamPluginManager", "ThumbnailCache");
        
        private List<BypassFile> _allFiles = new();
        
        // Debounce timer for SizeChanged to prevent multiple rapid refreshes during window state transitions
        private System.Windows.Threading.DispatcherTimer? _sizeChangeDebounceTimer;
        private System.Windows.Threading.DispatcherTimer? _thumbnailLoadDebounceTimer;
        private readonly HashSet<string> _thumbnailLoadRequestedKeys = new(StringComparer.OrdinalIgnoreCase);
        private const double MIN_CARD_WIDTH = 250;
        private const int MAX_THUMBNAILS_TO_LOAD = 48;
        // Runtime reference to the generated WrapPanel from the ItemsControl template
        private WrapPanel? CardPanel;
        private static double _cachedScrollOffset = 0;
        private bool _hasRestoredScrollPosition = false;

        public GameBypassView()
        {
            try
            {
                InitializeComponent();
                LogDebug("InitializeComponent succeeded");
            }
            catch (Exception ex)
            {
                LogDebug($"InitializeComponent error: {ex.GetType().Name} - {ex.Message}");
            }
            
            Files = new ObservableCollection<BypassFile>();
            LogDebug("Files collection initialized");
            
            DataContext = this;
            LogDebug("DataContext set");
            
            _filesView = CollectionViewSource.GetDefaultView(Files);
            _filesView.Filter = FileFilterPredicate;
            LogDebug("ICollectionView setup completed for search filtering");
            
            Directory.CreateDirectory(_thumbnailCacheFolder);
            LogDebug($"Thumbnail cache folder initialized: {_thumbnailCacheFolder}");
            
            Loaded += GameBypassView_Loaded;
            Unloaded += GameBypassView_Unloaded;
            SizeChanged += UserControl_SizeChanged;
            InitializeThumbnailLoadDebounceTimer();

            // ItemsControl creates its items panel at runtime; capture it once loaded
            FileCards.Loaded += (s, e) =>
            {
                try
                {
                    CardPanel = FindVisualChild<WrapPanel>(FileCards);
                    UpdateCardPanelLayout();
                }
                catch { }
            };
            LogDebug("GameBypassView constructor completed");
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void InitializeThumbnailLoadDebounceTimer()
        {
            _thumbnailLoadDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            _thumbnailLoadDebounceTimer.Tick += ThumbnailLoadDebounceTimer_Tick;
        }

        private void ThumbnailLoadDebounceTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                _thumbnailLoadDebounceTimer?.Stop();

                int queued = 0;
                foreach (var file in Files)
                {
                    if (queued >= MAX_THUMBNAILS_TO_LOAD)
                        break;

                    if (file.AppId.HasValue && file.AppId > 0 && string.IsNullOrEmpty(file.ThumbnailPath))
                    {
                        string requestKey = file.AppId?.ToString() ?? file.FileName;
                        if (_thumbnailLoadRequestedKeys.Add(requestKey))
                        {
                            _ = FetchAndCacheSteamThumbnailWithRetry(file);
                            queued++;
                        }
                    }
                }

                LogDebug($"Queued thumbnail loads for {queued} active bypass cards");
            }
            catch (Exception ex)
            {
                LogDebug($"Thumbnail debounce handler error: {ex.Message}");
            }
        }

        private void StartSpinnerAnimation()
        {
            try
            {
                var spinnerRotate = FindName("SpinnerRotate") as System.Windows.Media.RotateTransform;
                if (spinnerRotate != null)
                {
                    var animation = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 0,
                        To = 360,
                        Duration = new System.Windows.Duration(System.TimeSpan.FromSeconds(2)),
                        RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                    };
                    spinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, animation);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error starting spinner animation: {ex.Message}");
            }
        }

        private async void GameBypassView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Wait for any view transition animation to complete (fade animation duration)
                // This prevents layout changes during the transition, which causes glitches
                await Task.Delay(200);
                
                var loadingOverlay = FindName("LoadingOverlay") as Grid;
                if (loadingOverlay != null)
                {
                    loadingOverlay.Visibility = Visibility.Visible;
                    StartContinuousLoadingAnimation();
                    UpdateLoadingStatus("Loading files...");
                }
                
                LogDebug("[GameBypassView_Loaded] View loaded");
                
                var debugInfo = FindName("DebugInfo") as TextBlock;
                if (debugInfo != null)
                    debugInfo.Text = $"DEBUG: Files count before load = {Files.Count}";
                
                RegisterTextElements();
                
                if (FindName("SearchBox") is TextBox searchBox)
                {
                    searchBox.TextChanged += SearchBox_TextChanged;
                    searchBox.Foreground = Application.Current.FindResource("ForegroundBrush") as System.Windows.Media.Brush;
                }
                
                // Prepare scroll handlers for restore/caching
                if (FindName("CardsScrollViewer") is ScrollViewer sc)
                {
                    sc.ScrollChanged -= CardsScrollViewer_ScrollChanged;
                    sc.ScrollChanged += CardsScrollViewer_ScrollChanged;
                }
                _hasRestoredScrollPosition = false;

                await LoadFilesFromSource();
                
                if (debugInfo != null)
                    debugInfo.Text = $"DEBUG: Files count after load = {Files.Count}";
                
                SetupLazyThumbnailLoading();
                
                LogDebug($"GameBypassView_Loaded completed. Files in collection: {Files.Count}");
                
                // Wait until layout is stable before hiding the overlay to avoid
                // a brief width-jump when the WrapPanel measures for the first time.
                HideLoadingOverlayWhenLayoutReady();
            }
            catch (Exception ex)
            {
                ShowError($"Error loading view: {ex.Message}");
                Logger.Log($"[GameBypassView] Error: {ex.Message}");
                
                var debugInfo = FindName("DebugInfo") as TextBlock;
                if (debugInfo != null)
                    debugInfo.Text = $"ERROR: {ex.Message}";
                
                var loadingOverlay = FindName("LoadingOverlay") as Grid;
                if (loadingOverlay != null)
                {
                    StopContinuousLoadingAnimation();
                    loadingOverlay.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void HideLoadingOverlayWhenLayoutReady()
        {
            try
            {
                var overlay = FindName("LoadingOverlay") as Grid;
                var fileCards = FindName("FileCards") as ItemsControl;

                // Hide content initially to avoid flicker while layout stabilizes
                if (fileCards != null)
                    fileCards.Visibility = Visibility.Hidden;

                if (FindName("CardsScrollViewer") is not ScrollViewer viewer)
                {
                    // Fallback: nothing to wait for
                    if (overlay != null)
                    {
                        StopContinuousLoadingAnimation();
                        overlay.Visibility = Visibility.Collapsed;
                    }
                    if (fileCards != null)
                        fileCards.Visibility = Visibility.Visible;
                    return;
                }

                EventHandler? layoutHandler = null;
                layoutHandler = (s, e) =>
                {
                    try
                    {
                        // Ensure item widths are computed
                        UpdateCardPanelLayout();

                        if (CardPanel != null && CardPanel.ItemWidth > 0)
                        {
                            viewer.LayoutUpdated -= layoutHandler;
                            // Restore scroll after measurement
                            RestoreScrollPosition();

                            if (fileCards != null)
                                fileCards.Visibility = Visibility.Visible;

                            if (overlay != null)
                            {
                                StopContinuousLoadingAnimation();
                                overlay.Visibility = Visibility.Collapsed;
                            }
                        }
                    }
                    catch { }
                };

                viewer.LayoutUpdated += layoutHandler;

                // Kick one render pass to encourage layout
                viewer.Dispatcher.BeginInvoke(new Action(() => UpdateCardPanelLayout()), System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                LogDebug($"HideLoadingOverlayWhenLayoutReady error: {ex.Message}");
            }
        }

        private void GameBypassView_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Clean up debounce timer
                if (_sizeChangeDebounceTimer != null)
                {
                    _sizeChangeDebounceTimer.Stop();
                    _sizeChangeDebounceTimer = null;
                }

                if (_thumbnailLoadDebounceTimer != null)
                {
                    _thumbnailLoadDebounceTimer.Stop();
                    _thumbnailLoadDebounceTimer = null;
                }
                if (FindName("CardsScrollViewer") is ScrollViewer sc2)
                {
                    sc2.ScrollChanged -= CardsScrollViewer_ScrollChanged;
                    sc2.LayoutUpdated -= CardsScrollViewer_LayoutUpdated;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"[GameBypassView_Unloaded] Error: {ex.Message}");
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                string searchText = (sender as TextBox)?.Text?.ToLower() ?? "";
                
                if (string.IsNullOrWhiteSpace(searchText))
                {
                    LogDebug("Search cleared, returning to paginated view");
                    DisplayCurrentPage();
                }
                else
                {
                    LogDebug($"Searching for: {searchText}");
                    var filteredFiles = _allFiles.Where(f => 
                        (f.Name?.ToLower().Contains(searchText) ?? false) ||
                        (f.FileName?.ToLower().Contains(searchText) ?? false)
                    ).OrderBy(f => f.Name ?? "").ToList();
                    
                    LogDebug($"✓ Found {filteredFiles.Count} matching files from {_allFiles.Count} total");
                    
                    Files.Clear();
                    foreach (var file in filteredFiles)
                    {
                        Files.Add(file);
                    }
                    
                    if (FindName("PreviousPageButton") is Button prevBtn)
                        prevBtn.Visibility = Visibility.Collapsed;
                    if (FindName("NextPageButton") is Button nextBtn)
                        nextBtn.Visibility = Visibility.Collapsed;
                    if (FindName("PageIndicator") is TextBlock pageIndicator)
                    {
                        pageIndicator.Visibility = Visibility.Visible;
                        pageIndicator.Text = $"Found {filteredFiles.Count} matching files";
                    }
                    
                    SetupLazyThumbnailLoading();
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error in SearchBox_TextChanged: {ex.Message}");
            }
        }

        private bool FileFilterPredicate(object obj)
        {
            if (obj is not BypassFile file)
                return false;

            if (FindName("SearchBox") is not TextBox searchBox)
                return true;

            string searchText = searchBox.Text?.ToLower() ?? "";
            if (string.IsNullOrWhiteSpace(searchText))
                return true;

            return (file.Name?.ToLower().Contains(searchText) ?? false) ||
                   (file.FileName?.ToLower().Contains(searchText) ?? false);
        }

        private void RegisterTextElements()
        {
            try
            {
                if (FindName("BypassTitle") is TextBlock bypassTitle)
                    bypassTitle.Text = "Game Bypass";
                if (FindName("BypassSubtitle") is TextBlock bypassSubtitle)
                    bypassSubtitle.Text = "DRM Protection Bypass Tools";
                if (FindName("RefreshText") is TextBlock refreshText)
                    refreshText.Text = "Refresh";
                if (FindName("BackButtonText") is TextBlock backButtonText)
                    backButtonText.Text = "Back";
            }
            catch (Exception ex)
            {
                LogDebug($"[GameBypassView] RegisterTextElements error: {ex.Message}");
            }
        }

        private async Task LoadFilesFromSource()
        {
            try
            {
                LogDebug("=== GameBypassView.LoadFilesFromSource STARTED ===");
                LogDebug($"Files before load: {Files.Count} items");

                // Check if we have valid cached data
                bool hasValidCache = _cachedFiles != null && 
                                   (DateTime.Now - _lastFetchTime) < _cacheDuration;

                if (hasValidCache)
                {
                    LogDebug("✓ Using cached GameBypass data to avoid refetching");
                    // Load from cache
                    _allFiles.Clear();
                    _allFiles.AddRange(_cachedFiles);
                    Files.Clear();
                    foreach (var file in _cachedFiles)
                    {
                        Files.Add(file);
                    }
                    
                    var debugInfoCache = FindName("DebugInfo") as TextBlock;
                    if (debugInfoCache != null)
                        debugInfoCache.Text = $"DEBUG: Loaded {Files.Count} files from cache ✓";
                    
                    // Ensure UI page indicator and thumbnails are set after loading
                    DisplayCurrentPage();
                    
                    LogDebug($"Final result: {Files.Count} files loaded from cache");
                    LogDebug("=== GameBypassView.LoadFilesFromSource COMPLETED (FROM CACHE) ===");
                    return;
                }

                var debugInfo = FindName("DebugInfo") as TextBlock;
                var loadingStatusText = FindName("LoadingStatusText") as TextBlock;

                if (loadingStatusText != null)
                    loadingStatusText.Text = "Fetching data from database...";
                if (debugInfo != null)
                    debugInfo.Text = "DEBUG: Fetching data from R2...";

                // Ambil file list dari R2
                var r2Files = await SteamPluginManager.R2Config.ListFilesAsync();
                // Ambil metadata dari Supabase
                var supabaseRecords = await SteamPluginManager.SteamDataUpdater.GetAllRecordsAsync();
                LogDebug($"[SUPABASE] Total records: {supabaseRecords.Count}");
                foreach (var rec in supabaseRecords)
                {
                    LogDebug($"[SUPABASE] id={rec.Id}, app_id={rec.AppId}, file_name='{rec.FileName}', name='{rec.Name}'");
                }
                Files.Clear();
                _allFiles.Clear();

                foreach (var r2file in r2Files)
                {
                    string r2FileName = System.IO.Path.GetFileName(r2file.Key);
                    int? appId = SteamPluginManager.SteamManifestHelper.ExtractAppIdFromFilename(r2FileName);
                    // Cari metadata: by app_id (jika ada) atau file_name
                    var meta = supabaseRecords.FirstOrDefault(m => (appId != null && m.AppId == appId) || string.Equals(m.FileName, r2FileName, StringComparison.OrdinalIgnoreCase));
                    string metaLog;
                    if (meta != null)
                    {
                        if (appId != null && meta.AppId == appId)
                            metaLog = $"[MATCH] Metadata ditemukan untuk '{r2FileName}' berdasarkan app_id: {appId} (meta.Name: '{meta.Name}')";
                        else if (string.Equals(meta.FileName, r2FileName, StringComparison.OrdinalIgnoreCase))
                            metaLog = $"[MATCH] Metadata ditemukan untuk '{r2FileName}' berdasarkan file_name: '{meta.FileName}' (meta.Name: '{meta.Name}')";
                        else
                            metaLog = $"[MATCH] Metadata ditemukan untuk '{r2FileName}' (tidak diketahui by apa) (meta.Name: '{meta.Name}')";
                    }
                    else
                    {
                        metaLog = $"[NO MATCH] Metadata TIDAK ditemukan untuk '{r2FileName}' (app_id: {appId})";
                    }
                    LogDebug(metaLog);
                    if (meta != null && meta.AppId > 0)
                        appId = meta.AppId;
                    var bypassFile = new BypassFile
                    {
                        FileName = r2file.Key,
                        FileUrl = r2file.Url,
                        Name = meta?.Name ?? System.IO.Path.GetFileNameWithoutExtension(r2FileName),
                        AppId = appId,
                        Id = meta?.Id,
                        Description = meta?.Description ?? string.Empty,
                        Genre = meta?.Genre ?? string.Empty
                    };
                    _allFiles.Add(bypassFile);
                }

                foreach (var file in _allFiles)
                    Files.Add(file);

                // Cache the fetched data
                _cachedFiles = new List<BypassFile>(_allFiles);
                _lastFetchTime = DateTime.Now;
                LogDebug("✓ GameBypass data cached for future use");

                if (loadingStatusText != null)
                    loadingStatusText.Text = "Preparing display...";

                LogDebug($"Final result: {Files.Count} files ready to display");
                LogDebug("=== GameBypassView.LoadFilesFromSource COMPLETED ===");
                // Ensure UI page indicator and thumbnails are set after loading
                DisplayCurrentPage();
            }
            catch (Exception ex)
            {
                LogDebug("=== EXCEPTION in LoadFilesFromSource ===");
                LogDebug($"Type: {ex.GetType().Name}");
                LogDebug($"Message: {ex.Message}");

                var loadingStatusText = FindName("LoadingStatusText") as TextBlock;
                if (loadingStatusText != null)
                    loadingStatusText.Text = "Error loading data";

                var debugInfo = FindName("DebugInfo") as TextBlock;
                if (debugInfo != null)
                    debugInfo.Text = $"DEBUG: ERROR - {ex.Message}";
            }
        }

        private void SetupLazyThumbnailLoading()
        {
            try
            {
                if (_thumbnailLoadDebounceTimer == null)
                    InitializeThumbnailLoadDebounceTimer();

                _thumbnailLoadDebounceTimer?.Stop();
                _thumbnailLoadDebounceTimer?.Start();
                LogDebug($"Queued thumbnail loading for visible bypass cards (max {MAX_THUMBNAILS_TO_LOAD})");
            }
            catch (Exception ex)
            {
                LogDebug($"Error setting up thumbnail loading: {ex.Message}");
            }
        }
        
        private async Task<bool> FetchAndCacheSteamThumbnailWithRetry(BypassFile file, int maxRetries = 2)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var result = await FetchAndCacheSteamThumbnail(file);
                    if (result) return true;
                    
                    if (attempt < maxRetries)
                    {
                        await Task.Delay((int)Math.Pow(2, attempt) * 500);
                    }
                }
                catch (Exception _)
                {
                    if (attempt == maxRetries) return false;
                    await Task.Delay((int)Math.Pow(2, attempt) * 500);
                }
            }
            return false;
        }

        private async Task<bool> FetchAndCacheSteamThumbnail(BypassFile file)
        {
            try
            {
                if (!file.AppId.HasValue || file.AppId.Value <= 0)
                    return false;

                string cacheFileName = $"{file.AppId}_header.jpg";
                string cachedPath = Path.Combine(_thumbnailCacheFolder, cacheFileName);

                // Return cached file if exists
                if (File.Exists(cachedPath))
                {
                    file.ThumbnailPath = new Uri(cachedPath, UriKind.Absolute).ToString();
                    return true;
                }

                string[] imageFormats = new[]
                {
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/header_292x136.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/header_616x353.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/header_1840x620.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/capsule_231x87.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/capsule_467x181.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/header.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/library_600x900.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/capsule.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/logo.png"
                };

                byte[]? downloadedData = null;
                
                foreach (var steamImageUrl in imageFormats)
                {
                    try
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, steamImageUrl);
                        request.Headers.Add("User-Agent", "Mozilla/5.0");
                        
                        using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8)))
                        {
                            var response = await _httpClient.SendAsync(request, cts.Token);

                            if (response.IsSuccessStatusCode)
                            {
                                downloadedData = await response.Content.ReadAsByteArrayAsync();
                                if (downloadedData != null && downloadedData.Length > 0)
                                {
                                    LogDebug($"✓ Downloaded thumbnail for {file.Name} (AppID: {file.AppId})");
                                    break;
                                }
                            }
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                // Save downloaded data to cache
                if (downloadedData != null && downloadedData.Length > 0)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
                        await File.WriteAllBytesAsync(cachedPath, downloadedData);
                        file.ThumbnailPath = new Uri(cachedPath, UriKind.Absolute).ToString();
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                LogDebug($"Error fetching thumbnail for AppID {file.AppId}: {ex.Message}");
                return false;
            }
        }

        private void LogDebug(string message)
        {
            Logger.Log($"[GameBypassView] {message}");
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadFilesFromSource();
            }
            catch (Exception ex)
            {
                ShowError($"Refresh failed: {ex.Message}");
            }
        }

        private void BackToDashboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WindowNavigator.NavigateToDashboard();
            }
            catch (Exception ex)
            {
                ShowError($"Navigation failed: {ex.Message}");
            }
        }

        private void CardBorder_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Border border && border.DataContext is BypassFile file)
                {
                    var thumbnailImage = border.FindName("ThumbnailImage") as Image;
                    if (thumbnailImage == null)
                    {
                        foreach (var child in GetVisualChildren(border))
                        {
                            if (child is Image img && img.Name == "ThumbnailImage")
                            {
                                thumbnailImage = img;
                                break;
                            }
                        }
                    }

                    if (file != null && thumbnailImage != null)
                    {
                        file.PropertyChanged += (s, args) =>
                        {
                            if (args.PropertyName == nameof(BypassFile.ThumbnailPath))
                            {
                                LoadThumbnailImage(file, thumbnailImage);
                            }
                        };
                        
                        LoadThumbnailImage(file, thumbnailImage);
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"CardBorder_Loaded error: {ex.Message}");
            }
        }

        private void LoadThumbnailImage(BypassFile file, Image thumbnailImage)
        {
            try
            {
                if (thumbnailImage == null || file == null)
                    return;

                thumbnailImage.Stretch = Stretch.UniformToFill;
                thumbnailImage.HorizontalAlignment = HorizontalAlignment.Stretch;
                thumbnailImage.VerticalAlignment = VerticalAlignment.Stretch;

                thumbnailImage.Source = null;

                if (!string.IsNullOrEmpty(file.ThumbnailPath))
                {
                    try
                    {
                        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(file.ThumbnailPath, UriKind.Absolute);
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = 220;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        thumbnailImage.Source = bitmap;
                        LogDebug($"✓ Loaded thumbnail for {file.Name}");
                    }
                    catch (Exception imgEx)
                    {
                        LogDebug($"Failed to load image for {file.Name}: {imgEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"LoadThumbnailImage error: {ex.Message}");
            }
        }

        private void ThumbnailBorder_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border thumbnailBorder)
            {
                thumbnailBorder.Clip = new RectangleGeometry(new Rect(0, 0, thumbnailBorder.ActualWidth, thumbnailBorder.ActualHeight), 20, 20);
            }
        }

        private void ThumbnailBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Border thumbnailBorder)
            {
                thumbnailBorder.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 20, 20);
            }
        }

        private System.Collections.Generic.IEnumerable<System.Windows.DependencyObject> GetVisualChildren(System.Windows.DependencyObject parent)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var grandchild in GetVisualChildren(child))
                {
                    yield return grandchild;
                }
            }
        }

        private void FileCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (sender is Border border && border.DataContext is BypassFile file)
                {
                    WindowNavigator.NavigateToGameBypassDetail(file);
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error opening file details: {ex.Message}");
                Logger.Log($"[GameBypassView] Card click error: {ex.Message}");
            }
        }

        private void ShowError(string message)
        {
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }


        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            // Pagination disabled - all files shown on single page
        }

        private void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            // Pagination disabled - all files shown on single page
        }

        private void DisplayCurrentPage()
        {
            try
            {
                Files.Clear();
                // Sort files alphabetically by Name (A-Z)
                var sortedFiles = _allFiles.OrderBy(f => f.Name ?? "").ToList();
                foreach (var file in sortedFiles)
                {
                    Files.Add(file);
                }
                
                if (FindName("PreviousPageButton") is Button prevBtn)
                    prevBtn.Visibility = Visibility.Collapsed;
                if (FindName("NextPageButton") is Button nextBtn)
                    nextBtn.Visibility = Visibility.Collapsed;
                if (FindName("PageIndicator") is TextBlock pageIndicator)
                {
                    pageIndicator.Visibility = Visibility.Visible;
                    pageIndicator.Text = $"Total: {_allFiles.Count} files";
                }
                
                LogDebug($"Displayed all {_allFiles.Count} files in one page (sorted alphabetically)");
                
                SetupLazyThumbnailLoading();
                
                // Restore previous scroll position if available
                RestoreScrollPosition();
            }
            catch (Exception ex)
            {
                LogDebug($"Error displaying files: {ex.Message}");
            }
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                // Cancel previous debounce timer
                if (_sizeChangeDebounceTimer != null)
                {
                    _sizeChangeDebounceTimer.Stop();
                }
                
                // Create new debounce timer - only update after 250ms of no size changes
                _sizeChangeDebounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                
                _sizeChangeDebounceTimer.Tick += (s, args) =>
                {
                    try
                    {
                        _sizeChangeDebounceTimer.Stop();
                        // Only recalculate layout, don't reload data
                        RecalculateLayout();
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"SizeChanged debounce handler error: {ex.Message}");
                    }
                };
                
                _sizeChangeDebounceTimer.Start();
            }
            catch (Exception ex)
            {
                LogDebug($"SizeChanged handler error: {ex.Message}");
            }
        }

        private void CardsScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            try
            {
                if (sender is ScrollViewer scrollViewer)
                {
                    if (e.VerticalChange == 0)
                        return;

                    _cachedScrollOffset = scrollViewer.VerticalOffset;
                }
            }
            catch { }
        }

        private void RestoreScrollPosition()
        {
            if (FindName("CardsScrollViewer") is not ScrollViewer viewer)
                return;

            if (_hasRestoredScrollPosition)
                return;

            viewer.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (viewer == null)
                        return;

                    if (_cachedScrollOffset > 0 && viewer.ScrollableHeight > 0)
                    {
                        double targetOffset = Math.Min(_cachedScrollOffset, viewer.ScrollableHeight);
                        viewer.ScrollToVerticalOffset(targetOffset);
                        _hasRestoredScrollPosition = true;
                    }
                    else if (_cachedScrollOffset > 0)
                    {
                        viewer.LayoutUpdated += CardsScrollViewer_LayoutUpdated;
                    }
                    else
                    {
                        viewer.ScrollToTop();
                        _hasRestoredScrollPosition = true;
                    }
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void CardsScrollViewer_LayoutUpdated(object? sender, EventArgs e)
        {
            if (FindName("CardsScrollViewer") is not ScrollViewer viewer || _hasRestoredScrollPosition)
                return;

            if (viewer.ScrollableHeight <= 0)
                return;

            viewer.LayoutUpdated -= CardsScrollViewer_LayoutUpdated;
            double targetOffset = Math.Min(_cachedScrollOffset, viewer.ScrollableHeight);
            viewer.ScrollToVerticalOffset(targetOffset);
            _hasRestoredScrollPosition = true;
        }

        /// <summary>
        /// Recalculate layout on size changes without reloading data.
        /// This prevents excessive data refresh during window state transitions.
        /// </summary>
        private void RecalculateLayout()
        {
            try
            {
                UpdateCardPanelLayout();

                if (FindName("CardsScrollViewer") is ScrollViewer viewer && viewer.ScrollableHeight > 0)
                {
                    viewer.UpdateLayout();
                }
                
                LogDebug("Layout recalculated without data reload");
            }
            catch (Exception ex)
            {
                LogDebug($"RecalculateLayout error: {ex.Message}");
            }
        }

        private void UpdateCardPanelLayout()
        {
            try
            {
                if (CardsScrollViewer == null || CardPanel == null)
                    return;

                double availableWidth = CardsScrollViewer.ViewportWidth > 0
                    ? CardsScrollViewer.ViewportWidth
                    : CardsScrollViewer.ActualWidth;
                if (availableWidth <= 0)
                    return;

                const double cardGap = 12;
                int columns = Math.Max(1, (int)((availableWidth + cardGap) / (MIN_CARD_WIDTH + cardGap)));
                double computedWidth = Math.Floor((availableWidth - columns * cardGap) / columns);
                if (computedWidth < MIN_CARD_WIDTH)
                    computedWidth = MIN_CARD_WIDTH;

                CardPanel.ItemWidth = computedWidth;
            }
            catch (Exception ex)
            {
                LogDebug($"UpdateCardPanelLayout error: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts continuous loading animation
        /// </summary>
        private void StartContinuousLoadingAnimation()
        {
            try
            {
                var continuousRotate = FindName("ContinuousRotate") as System.Windows.Media.RotateTransform;
                if (continuousRotate != null)
                {
                    var animation = new DoubleAnimation
                    {
                        From = 0,
                        To = 360,
                        Duration = new System.Windows.Duration(System.TimeSpan.FromSeconds(2)),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    continuousRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, animation);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"StartContinuousLoadingAnimation error: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops continuous loading animation
        /// </summary>
        private void StopContinuousLoadingAnimation()
        {
            try
            {
                var continuousRotate = FindName("ContinuousRotate") as System.Windows.Media.RotateTransform;
                if (continuousRotate != null)
                {
                    continuousRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"StopContinuousLoadingAnimation error: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates loading status text
        /// </summary>
        private void UpdateLoadingStatus(string status)
        {
            try
            {
                var statusText = FindName("LoadingStatusText") as TextBlock;
                if (statusText != null)
                    statusText.Text = status;
            }
            catch (Exception ex)
            {
                LogDebug($"UpdateLoadingStatus error: {ex.Message}");
            }
        }
    }
}
#pragma warning restore CS0103 // The name does not exist
