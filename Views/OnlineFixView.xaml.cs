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
    public class OnlineFixFile : System.ComponentModel.INotifyPropertyChanged
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
        private string? _thumbnailPath = null;
        public string? ThumbnailPath 
        { 
            get => string.IsNullOrWhiteSpace(_thumbnailPath) ? null : _thumbnailPath;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
                if (_thumbnailPath != normalized)
                {
                    _thumbnailPath = normalized;
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

    public partial class OnlineFixView : UserControl
    {
        // Static cache to prevent refetching data when navigating back
        private static List<OnlineFixFile>? _cachedFiles = null;
        private static DateTime _lastFetchTime = DateTime.MinValue;
        private static readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5); // Cache for 5 minutes

        public static OnlineFixView? ActiveInstance { get; private set; }

        public static void InvalidateCache()
        {
            _cachedFiles = null;
            _lastFetchTime = DateTime.MinValue;
        }

        public async Task ReloadFilesAsync(bool forceRefresh = false)
        {
            if (forceRefresh)
            {
                InvalidateCache();
            }
            await LoadFilesFromSource();
        }

        public ObservableCollection<OnlineFixFile> Files { get; set; }
        private System.ComponentModel.ICollectionView _filesView;
        private readonly HttpClient _httpClient = new();
        private readonly string _thumbnailCacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamPluginManager", "ThumbnailCache");
        private List<OnlineFixFile> _allFiles = new();
        
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

            // --- Thumbnail Loading Logic ---
            private void SetupLazyThumbnailLoading()
            {
                try
                {
                    if (_thumbnailLoadDebounceTimer == null)
                        InitializeThumbnailLoadDebounceTimer();

                    _thumbnailLoadDebounceTimer?.Stop();
                    _thumbnailLoadDebounceTimer?.Start();
                }
                catch (Exception _)
                {
                    // Log error
                }
            }

            private async Task<bool> FetchAndCacheSteamThumbnailWithRetry(OnlineFixFile file, int maxRetries = 2)
            {
                string requestKey = file.AppId?.ToString() ?? file.FileName;
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
                        if (attempt == maxRetries) break;
                        await Task.Delay((int)Math.Pow(2, attempt) * 500);
                    }
                }

                _thumbnailLoadRequestedKeys.Remove(requestKey);
                return false;
            }

            private async Task<bool> FetchAndCacheSteamThumbnail(OnlineFixFile file)
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

                    // Fallback: Try store page scrape for og:image
                    try
                    {
                        string storeUrl = $"https://store.steampowered.com/app/{file.AppId}";
                        var resp = await _httpClient.GetAsync(storeUrl);
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
                                using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
                                var assetResp = await _httpClient.GetAsync(assetUrl, cts2.Token);
                                if (assetResp.IsSuccessStatusCode)
                                {
                                    var bytes = await assetResp.Content.ReadAsByteArrayAsync();
                                    if (bytes != null && bytes.Length > 0)
                                    {
                                        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
                                        await File.WriteAllBytesAsync(cachedPath, bytes);
                                        file.ThumbnailPath = new Uri(cachedPath, UriKind.Absolute).ToString();
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore scrape errors
                    }

                    return false;
                }
                catch
                {
                    return false;
                }
            }

            private void CardBorder_Loaded(object sender, RoutedEventArgs e)
            {
                try
                {
                    if (sender is Border border && border.DataContext is OnlineFixFile file)
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
                            if (thumbnailImage.Tag is Tuple<OnlineFixFile, PropertyChangedEventHandler> existingBinding)
                            {
                                if (ReferenceEquals(existingBinding.Item1, file))
                                {
                                    LoadThumbnailImage(file, thumbnailImage);
                                    return;
                                }

                                existingBinding.Item1.PropertyChanged -= existingBinding.Item2;
                            }

                            PropertyChangedEventHandler thumbnailChangedHandler = (s, args) =>
                            {
                                if (args.PropertyName == nameof(OnlineFixFile.ThumbnailPath))
                                {
                                    LoadThumbnailImage(file, thumbnailImage);
                                }
                            };

                            file.PropertyChanged += thumbnailChangedHandler;
                            thumbnailImage.Tag = Tuple.Create(file, thumbnailChangedHandler);

                            LoadThumbnailImage(file, thumbnailImage);

                            if (string.IsNullOrEmpty(file.ThumbnailPath) && file.AppId.HasValue && file.AppId.Value > 0)
                            {
                                string requestKey = file.AppId?.ToString() ?? file.FileName;
                                if (_thumbnailLoadRequestedKeys.Add(requestKey))
                                {
                                    _ = FetchAndCacheSteamThumbnailWithRetry(file);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Log error
                }
            }

            private void ThumbnailBorder_Loaded(object sender, RoutedEventArgs e)
            {
                if (sender is Border thumbnailBorder && thumbnailBorder.ActualWidth > 0 && thumbnailBorder.ActualHeight > 0)
                {
                    thumbnailBorder.Clip = new RectangleGeometry(new Rect(0, 0, thumbnailBorder.ActualWidth, thumbnailBorder.ActualHeight), 13, 13);
                }
            }

            private void ThumbnailBorder_SizeChanged(object sender, SizeChangedEventArgs e)
            {
                if (sender is Border thumbnailBorder && e.NewSize.Width > 0 && e.NewSize.Height > 0)
                {
                    thumbnailBorder.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 13, 13);
                }
            }

            private void LoadThumbnailImage(OnlineFixFile file, Image thumbnailImage)
            {
                try
                {
                    if (thumbnailImage == null || file == null)
                        return;

                    if (!thumbnailImage.Dispatcher.CheckAccess())
                    {
                        thumbnailImage.Dispatcher.BeginInvoke(new Action(() => LoadThumbnailImage(file, thumbnailImage)));
                        return;
                    }

                    thumbnailImage.Stretch = Stretch.UniformToFill;
                    thumbnailImage.HorizontalAlignment = HorizontalAlignment.Stretch;
                    thumbnailImage.VerticalAlignment = VerticalAlignment.Stretch;

                    // Clear existing source only when the image is changing to a different path
                    if (thumbnailImage.Source is System.Windows.Media.Imaging.BitmapImage existingBitmap &&
                        existingBitmap.UriSource?.OriginalString == file.ThumbnailPath)
                    {
                        return;
                    }

                    thumbnailImage.Source = null;

                    if (!string.IsNullOrEmpty(file.ThumbnailPath))
                    {
                        try
                        {
                            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                            bitmap.BeginInit();
                            bitmap.UriSource = new Uri(file.ThumbnailPath, UriKind.Absolute);
                            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bitmap.DecodePixelWidth = 280;
                            bitmap.EndInit();
                            bitmap.Freeze();
                            thumbnailImage.Source = bitmap;
                        }
                        catch (Exception)
                        {
                            thumbnailImage.Source = null;
                        }
                    }
                }
                catch
                {
                    // Log error
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

        public OnlineFixView()
        {
            try
            {
                InitializeComponent();
            }
            catch
            {
                // Log error
            }
            Files = new ObservableCollection<OnlineFixFile>();
            DataContext = this;
            ActiveInstance = this;
            _filesView = CollectionViewSource.GetDefaultView(Files);
            _filesView.Filter = FileFilterPredicate;
            Directory.CreateDirectory(_thumbnailCacheFolder);
            Loaded += OnlineFixView_Loaded;
            Unloaded += OnlineFixView_Unloaded;
            SizeChanged += UserControl_SizeChanged;
            InitializeThumbnailLoadDebounceTimer();

            // Capture the generated WrapPanel once the ItemsControl creates it
            FileCards.Loaded += (s, e) =>
            {
                try
                {
                    CardPanel = FindVisualChild<WrapPanel>(FileCards);
                    UpdateCardPanelLayout();
                }
                catch { }
            };
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
            }
            catch (Exception)
            {
                // Ignore thumbnail debounce failures
            }
        }

        private void CardsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            try
            {
                if (sender is ScrollViewer scrollViewer)
                {
                    if (e.VerticalChange != 0)
                    {
                        _cachedScrollOffset = scrollViewer.VerticalOffset;
                        SetupLazyThumbnailLoading();
                    }

                    if (e.ViewportWidthChange != 0)
                    {
                        UpdateCardPanelLayout();
                    }
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

        private async Task LoadFilesFromSource()
        {
            try
            {
                // Check if we have valid cached data
                bool hasValidCache = _cachedFiles != null && 
                                   (DateTime.Now - _lastFetchTime) < _cacheDuration;

                if (hasValidCache)
                {
                    // Load from cache
                    _allFiles.Clear();
                    _allFiles.AddRange(_cachedFiles);
                    Files.Clear();
                    foreach (var file in _cachedFiles)
                    {
                        Files.Add(file);
                    }
                    
                    SetupLazyThumbnailLoading();
                    
                    // Update displayed page and page indicator after loading completes
                    DisplayCurrentPage();
                    // Hold overlay until layout stabilizes to avoid a brief width-jump
                    HideLoadingOverlayWhenLayoutReady();
                    return;
                }

                var loadingOverlay = FindName("LoadingOverlay") as Grid;
                var loadingStatusText = FindName("LoadingStatusText") as TextBlock;
                var debugInfo = FindName("DebugInfo") as TextBlock;
                if (loadingOverlay != null) loadingOverlay.Visibility = Visibility.Visible;
                if (loadingStatusText != null) loadingStatusText.Text = "Fetching data from database...";
                if (debugInfo != null) debugInfo.Text = "DEBUG: Fetching data from B2...";


                // Ambil file list dari Backblaze B2 (onlinefix/ folder)
                var onlineFixFiles = await SteamPluginManager.B2Config.ListFilesAsync("onlinefix/");

                // Ambil metadata dari Supabase
                var supabaseRecords = await SteamPluginManager.SteamDataUpdater.GetAllRecordsAsync();

                Files.Clear();
                _allFiles.Clear();

                foreach (var b2file in onlineFixFiles)
                {
                    string b2FileName = System.IO.Path.GetFileName(b2file.Key);
                    int? appId = SteamPluginManager.SteamManifestHelper.ExtractAppIdFromFilename(b2FileName);
                    var meta = supabaseRecords.FirstOrDefault(m => (appId != null && m.AppId == appId) || string.Equals(m.FileName, b2FileName, StringComparison.OrdinalIgnoreCase));
                    if (meta != null && meta.AppId > 0)
                        appId = meta.AppId;
                    var fixFile = new OnlineFixFile
                    {
                        FileName = b2file.Key,
                        FileUrl = b2file.Url,
                        Name = meta?.Name ?? System.IO.Path.GetFileNameWithoutExtension(b2FileName),
                        AppId = appId,
                        Id = meta?.Id,
                        Description = meta?.Description ?? string.Empty,
                        Genre = meta?.Genre ?? string.Empty,
                        FileSize = b2file.Size >= 0 ? FormatFileSize(b2file.Size) : string.Empty
                    };

                    if (appId.HasValue && appId.Value > 0)
                    {
                        string cacheFileName = $"{appId.Value}_header.jpg";
                        string cachedPath = Path.Combine(_thumbnailCacheFolder, cacheFileName);
                        if (File.Exists(cachedPath))
                        {
                            fixFile.ThumbnailPath = new Uri(cachedPath, UriKind.Absolute).ToString();
                        }
                    }

                    _allFiles.Add(fixFile);
                }

                foreach (var file in _allFiles)
                    Files.Add(file);

                // Cache the fetched data
                _cachedFiles = new List<OnlineFixFile>(_allFiles);
                _lastFetchTime = DateTime.Now;

                SetupLazyThumbnailLoading();

                if (loadingStatusText != null) loadingStatusText.Text = "Preparing display...";
                
                // Update displayed page and page indicator after loading completes
                DisplayCurrentPage();
                // Hold overlay until layout stabilizes to avoid a brief width-jump
                HideLoadingOverlayWhenLayoutReady();
            }
            catch (Exception ex)
            {
                var loadingStatusText = FindName("LoadingStatusText") as TextBlock;
                if (loadingStatusText != null) loadingStatusText.Text = "Error loading data";
                var debugInfo = FindName("DebugInfo") as TextBlock;
                if (debugInfo != null) debugInfo.Text = $"DEBUG: ERROR - {ex.Message}";
                if (FindName("LoadingOverlay") is Grid loadingOverlay)
                {
                    StopContinuousLoadingAnimation();
                    loadingOverlay.Visibility = Visibility.Collapsed;
                }
            }
        }

        private bool FileFilterPredicate(object obj)
        {
            if (obj is not OnlineFixFile file)
                return false;
            if (FindName("SearchBox") is not TextBox searchBox)
                return true;
            string searchText = searchBox.Text?.ToLower() ?? "";
            if (string.IsNullOrWhiteSpace(searchText))
                return true;
            return (file.Name?.ToLower().Contains(searchText) ?? false) ||
                   (file.FileName?.ToLower().Contains(searchText) ?? false);
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void OnlineFixView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var loadingOverlay = FindName("LoadingOverlay") as Grid;
                if (loadingOverlay != null)
                {
                    loadingOverlay.Visibility = Visibility.Visible;
                    StartContinuousLoadingAnimation();
                    UpdateLoadingStatus("Loading files...");
                }
                RegisterTextElements();
                if (FindName("SearchBox") is TextBox searchBox)
                {
                    searchBox.TextChanged += SearchBox_TextChanged;
                    searchBox.Foreground = Application.Current.FindResource("ForegroundBrush") as System.Windows.Media.Brush;
                }
                if (FindName("CardsScrollViewer") is ScrollViewer sv)
                {
                    sv.ScrollChanged -= CardsScrollViewer_ScrollChanged;
                    sv.ScrollChanged += CardsScrollViewer_ScrollChanged;
                }
                _hasRestoredScrollPosition = false;

                _ = LoadFilesFromSource();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error loading OnlineFixView: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                var loadingOverlay = FindName("LoadingOverlay") as Grid;
                if (loadingOverlay != null)
                {
                    StopContinuousLoadingAnimation();
                    loadingOverlay.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void OnlineFixView_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ReferenceEquals(ActiveInstance, this))
                    ActiveInstance = null;

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
                if (FindName("CardsScrollViewer") is ScrollViewer sv2)
                {
                    sv2.ScrollChanged -= CardsScrollViewer_ScrollChanged;
                    sv2.LayoutUpdated -= CardsScrollViewer_LayoutUpdated;
                }
            }
            catch (Exception ex)
            {
                // Log error
            }
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                if (e.WidthChanged)
                {
                    UpdateCardPanelLayout();
                }

                // Cancel previous debounce timer
                if (_sizeChangeDebounceTimer != null)
                {
                    _sizeChangeDebounceTimer.Stop();
                }
                
                // Debounce timer for smooth resize
                _sizeChangeDebounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                
                _sizeChangeDebounceTimer.Tick += (s, args) =>
                {
                    try
                    {
                        _sizeChangeDebounceTimer.Stop();
                        RecalculateLayout();
                    }
                    catch (Exception ex)
                    {
                        // Log error
                    }
                };
                
                _sizeChangeDebounceTimer.Start();
            }
            catch (Exception _)
            {
                // Log error
            }
        }

        /// <summary>
        /// Public method to force layout recalculation when window state changes (e.g. Maximize/Restore)
        /// </summary>
        public void RecalculateActiveLayout()
        {
            RecalculateLayout();
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
            }
            catch (Exception _)
            {
                // Log error
            }
        }

        private void UpdateCardPanelLayout()
        {
            try
            {
                if (CardPanel == null && FileCards != null)
                {
                    CardPanel = FindVisualChild<WrapPanel>(FileCards);
                }

                if (CardsScrollViewer == null || CardPanel == null)
                    return;

                double availableWidth = CardsScrollViewer.ViewportWidth > 0
                    ? CardsScrollViewer.ViewportWidth
                    : CardsScrollViewer.ActualWidth;
                if (availableWidth <= 0)
                    return;

                const double cardMarginRight = 14;
                const double targetCardWidth = 230;
                const double rightClearance = 36; // Accounts for scrollbar, padding, and drop shadow projection

                double usableWidth = Math.Max(targetCardWidth + cardMarginRight, availableWidth - rightClearance);
                int columns = Math.Max(1, (int)(usableWidth / (targetCardWidth + cardMarginRight)));
                double computedWidth = Math.Floor(usableWidth / columns);

                CardPanel.ItemWidth = computedWidth;
                CardPanel.ItemHeight = 272; // 260 card height + 12 bottom margin
            }
            catch (Exception _)
            {
                // Log error
            }
        }

        private void HideLoadingOverlayWhenLayoutReady()
        {
            try
            {
                var overlay = FindName("LoadingOverlay") as Grid;
                var fileCards = FindName("FileCards") as ItemsControl;

                if (fileCards != null)
                    fileCards.Visibility = Visibility.Hidden;

                if (FindName("CardsScrollViewer") is not ScrollViewer viewer)
                {
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
                        UpdateCardPanelLayout();

                        if (CardPanel != null && CardPanel.ItemWidth > 0)
                        {
                            viewer.LayoutUpdated -= layoutHandler;
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
                viewer.Dispatcher.BeginInvoke(new Action(() => UpdateCardPanelLayout()), System.Windows.Threading.DispatcherPriority.Render);
            }
            catch { }
        }


        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadFilesFromSource();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Refresh failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BackToDashboard_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToDashboard();
        }

        private void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            // Pagination disabled - all files shown on single page
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            // Pagination disabled - all files shown on single page
        }


        private void FileCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (sender is Border border && border.DataContext is OnlineFixFile file)
                {
                    WindowNavigator.NavigateToOnlineFixDetail(file);
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Error opening file details: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RegisterTextElements()
        {
            if (FindName("BypassTitle") is TextBlock bypassTitle)
                bypassTitle.Text = "Online Fix";
            if (FindName("BypassSubtitle") is TextBlock bypassSubtitle)
                bypassSubtitle.Text = "Multiplayer/Online Fix Tools";
            if (FindName("RefreshText") is TextBlock refreshText)
                refreshText.Text = "Refresh";
            if (FindName("BackButtonText") is TextBlock backButtonText)
                backButtonText.Text = "Back";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string searchText = (sender as TextBox)?.Text?.ToLower() ?? "";
            if (string.IsNullOrWhiteSpace(searchText))
            {
                DisplayCurrentPage();
            }
            else
            {
                var filteredFiles = _allFiles.Where(f =>
                    (f.Name?.ToLower().Contains(searchText) ?? false) ||
                    (f.FileName?.ToLower().Contains(searchText) ?? false)
                ).OrderBy(f => f.Name ?? "").ToList();
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
            }
        }

        private void DisplayCurrentPage()
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
            SetupLazyThumbnailLoading();
            // Restore scrolling state once items are displayed
            RestoreScrollPosition();
        }

        private void StartSpinnerAnimation()
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
                        Duration = new System.Windows.Duration(System.TimeSpan.FromSeconds(1)),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    continuousRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, animation);
                }
            }
            catch (Exception ex)
            {
                // Silent fail
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
                // Silent fail
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
                // Silent fail for status updates
            }
        }
    }
}
