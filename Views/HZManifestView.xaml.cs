#pragma warning disable CS0103 // The name does not exist
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Globalization;
using System.Management;

namespace SteamPluginManager.Views
{
    public class ManifestFile : System.ComponentModel.INotifyPropertyChanged
    {
        public int? Id { get; set; }
        [JsonPropertyName("app_id")]
        public int? AppId { get; set; }
        [JsonPropertyName("file_name")]
        public string FileName { get; set; } = "";
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string FileSize { get; set; } = "";
        public string UpdatedDate { get; set; } = "";
        [JsonPropertyName("url")]
        public string FileUrl { get; set; } = "";
        [JsonPropertyName("storage_type")]
        public string StorageType { get; set; } = "zip";
        [JsonPropertyName("folder_path")]
        public string FolderPath { get; set; } = "";
        public bool IsFolderStorage =>
            StorageType?.Equals("folder", StringComparison.OrdinalIgnoreCase) == true ||
            IsValidFolderPath(FolderPath);

        private static bool IsValidFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var trimmed = path.Trim();
            if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }
        public string Description { get; set; } = "";
        public string Genre { get; set; } = "";
        public string GenreBadgeDisplay => Genre;
        private int? _downloadCount;
        [JsonPropertyName("download_count")]
        public int? DownloadCount
        {
            get => _downloadCount;
            set
            {
                if (_downloadCount != value)
                {
                    _downloadCount = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadCount)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadCountDisplay)));
                }
            }
        }
        public string DownloadCountDisplay => DownloadCount.HasValue && DownloadCount.Value >= 0 ? $"{DownloadCount.Value}" : "0";

        [JsonPropertyName("minimum_requirements")]
        public string MinimumRequirements { get; set; } = "";
        [JsonPropertyName("recommended_requirements")]
        public string RecommendedRequirements { get; set; } = "";
        [JsonIgnore]
        public bool IsAdult { get; set; } = false;

        [JsonPropertyName("is_adult")]
        public object? IsAdultValue
        {
            set
            {
                if (value is bool boolValue)
                {
                    IsAdult = boolValue;
                    return;
                }

                if (value is string stringValue && bool.TryParse(stringValue, out var parsedStringValue))
                {
                    IsAdult = parsedStringValue;
                    return;
                }

                if (value is JsonElement element)
                {
                    switch (element.ValueKind)
                    {
                        case JsonValueKind.True:
                            IsAdult = true;
                            break;
                        case JsonValueKind.False:
                            IsAdult = false;
                            break;
                        case JsonValueKind.String:
                            if (bool.TryParse(element.GetString(), out var parsedElementValue))
                                IsAdult = parsedElementValue;
                            break;
                        case JsonValueKind.Number:
                            if (element.TryGetInt32(out var intValue))
                                IsAdult = intValue != 0;
                            break;
                    }
                }
            }
        }

        [JsonPropertyName("created_at")]
        public DateTime? CreatedAt { get; set; }
        
        private string _thumbnailPath = "";
        [JsonPropertyName("thumbnail_path")]
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
        
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    }

    public class CategoryFilterItem
    {
        public string Category { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int Count { get; set; }
    }

    public partial class HZManifestView : UserControl
    {
        // Static cache to prevent refetching data when navigating back
        private static List<ManifestFile>? _cachedFiles = null;
        private static int _cachedPage = 0;
        private static DateTime _lastFetchTime = DateTime.MinValue;
        private static readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5); // Cache for 5 minutes

        // Property yang di-expose untuk binding - ini yang ItemsControl bind ke
        public ObservableCollection<ManifestFile> Files { get; set; }
        
        // CollectionViewSource untuk search filtering
        private System.ComponentModel.ICollectionView _filesView;
        
        private readonly HttpClient _httpClient = new();
        private readonly HashSet<string> _thumbnailLoadRequestedKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _thumbnailLoadCompletedKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _thumbnailLoadFailedKeys = new(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Threading.DispatcherTimer? _thumbnailLoadDebounceTimer;
        private const int MIN_VISIBLE_THUMBNAILS_PER_PASS = 24;
        
        // Cache folder untuk Steam thumbnails
        private readonly string _thumbnailCacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamPluginManager", "ThumbnailCache");
        
        // Pagination fields
        private int _currentPage = 0;
        private int _pageSize = 50;
        private int _totalFiles = 0;
        private List<ManifestFile> _allFiles = new(); // Store all files from Supabase

        // Category filtering
        private static string _currentCategory = "All";
        private static string _currentFilterCategory = "All"; // For dropdown filter
        private static string _currentSortOrder = "NameAsc"; // Default sort
        private static string _currentSearchText = string.Empty;
        private List<string> _availableCategories = new();
        private Dictionary<string, Button> _categoryButtons = new();
        private ObservableCollection<CategoryFilterItem> _categoryFilterItems = new();

        // Auto-refresh timer
        private System.Windows.Threading.DispatcherTimer? _autoRefreshTimer;
        private bool _forceRefreshOnLoad = true; // Default to true for backward compatibility
        private bool _requiresDeviceVerificationOnLoad = false;
        
        // Debounce timer for SizeChanged to prevent multiple rapid refreshes during window state transitions
        private System.Windows.Threading.DispatcherTimer? _sizeChangeDebounceTimer;
        private const double MIN_CARD_WIDTH = 250;
        private const int ROWS_PER_PAGE = 10;
        // Runtime reference to the generated WrapPanel from the ItemsControl template
        private WrapPanel? CardPanel;
        // Auto-paging state
        private bool _isLoadingPage = false;
        private const double AUTO_PAGING_THRESHOLD = 200; // px from bottom to trigger load

        private static HZManifestView? _activeManifestView;

        public HZManifestView()
        {
            try
            {
                InitializeComponent(); // Initialize XAML UI
                LogDebug("InitializeComponent succeeded");
            }
            catch (Exception ex)
            {
                LogDebug($"InitializeComponent error: {ex.GetType().Name} - {ex.Message}");
                // Continue anyway - UI might be partially loaded
            }
            
            // Initialize the collection FIRST
            Files = new ObservableCollection<ManifestFile>();
            LogDebug("Files collection initialized");
            
            // Set DataContext FIRST before anything else so bindings establish
            DataContext = this;
            LogDebug("DataContext set");

            // Restore the last known page when returning from detail
            _currentPage = Math.Max(0, _cachedPage);
            
            // Setup ICollectionView untuk search filtering
            _filesView = CollectionViewSource.GetDefaultView(Files);
            _filesView.Filter = FileFilterPredicate;
            LogDebug("ICollectionView setup completed for search filtering");
            
            // Initialize thumbnail cache folder
            Directory.CreateDirectory(_thumbnailCacheFolder);
            LogDebug($"Thumbnail cache folder initialized: {_thumbnailCacheFolder}");
            
            // Note: Auto-refresh timer disabled - refresh happens when view is loaded instead
            // SetupAutoRefreshTimer();
            
            // Check requests timer disabled - webhook cleanup is handled by Discord bot now
            LogDebug("HZManifestView constructor completed");
            
            _activeManifestView = this;

            Loaded += HZManifestView_Loaded;
            Unloaded += HZManifestView_Unloaded;
            SizeChanged += UserControl_SizeChanged;
            InitializeThumbnailLoadDebounceTimer();

            // Capture generated ItemsPanel (WrapPanel) at runtime
            FileCards.Loaded += (s, e) =>
            {
                try
                {
                    CardPanel = FindVisualChild<WrapPanel>(FileCards);
                    UpdateCardPanelLayout();
                    // Do NOT wire scroll changed: disable auto-paging on scroll
                }
                catch { }
            };
            
            // Subscribe to 18+ content setting changes to refresh display
            Allow18PlusContentPreferences.Allow18PlusContentChanged += Allow18PlusContentPreferences_Changed;
            
            LogDebug("HZManifestView constructor completed");
        }

        private void InitializeThumbnailLoadDebounceTimer()
        {
            _thumbnailLoadDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _thumbnailLoadDebounceTimer.Tick += ThumbnailLoadDebounceTimer_Tick;
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

        public static async Task RefreshActiveManifestViewAsync(bool forceRefresh = true)
        {
            if (_activeManifestView != null)
            {
                await _activeManifestView.RefreshManifestDataAsync(forceRefresh);
            }
        }

        public async Task RefreshManifestDataAsync(bool forceRefresh = true)
        {
            try
            {
                CacheManager.ClearHzmCache();
                LogDebug("HZM cache cleared for hot reload.");
                await LoadFilesFromSupabase(forceRefresh: true);
            }
            catch (Exception ex)
            {
                LogDebug($"[HZManifestView] Hot reload failed: {ex.Message}");
            }
        }

        private void Allow18PlusContentPreferences_Changed(object? sender, bool isEnabled)
        {
            try
            {
                LogDebug($"[Allow18PlusContentPreferences_Changed] Setting changed to: {isEnabled}");
                // Refresh display with new filter
                DisplayCurrentPage();
                LogDebug("[Allow18PlusContentPreferences_Changed] Display refreshed");
            }
            catch (Exception ex)
            {
                LogDebug($"[Allow18PlusContentPreferences_Changed] Error: {ex.Message}");
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

        private async void HZManifestView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Wait for any view transition animation to complete (fade animation duration)
                // This prevents layout changes during the transition, which causes glitches
                await Task.Delay(200);
                
                // Show loading overlay
                var loadingOverlay = FindName("LoadingOverlay") as Grid;
                if (loadingOverlay != null)
                {
                    loadingOverlay.Visibility = Visibility.Visible;
                    StartContinuousLoadingAnimation();
                    UpdateLoadingStatus("Loading...");
                }
                
                if (_requiresDeviceVerificationOnLoad)
                {
                    LogDebug("[HZManifestView_Loaded] Checking device token verification...");

                    // Check if device token is verified
                    bool isTokenVerified = await CheckDeviceTokenAsync();

                    if (!isTokenVerified)
                    {
                        LogDebug("[HZManifestView_Loaded] Device token not verified, redirecting to VerifyTokenView");
                        WindowNavigator.NextViewAfterVerify = "HZManifest";
                        WindowNavigator.NavigateToVerifyToken();
                        return; // Don't load manifest, user needs to verify token first
                    }

                    LogDebug("[HZManifestView_Loaded] Device token verified, proceeding with manifest load");
                }
                else
                {
                    LogDebug("[HZManifestView_Loaded] Skipping device token verification because HZManifest was not explicitly requested for gated access.");
                }
                
                var debugInfo = FindName("DebugInfo") as TextBlock;
                if (debugInfo != null)
                    debugInfo.Text = $"DEBUG: Files count before load = {Files.Count}";
                
                // Register text elements for localization
                RegisterTextElements();
                HideManifestNotificationBanner();

                // Setup search box event handler
                if (FindName("SearchBox") is TextBox searchBox)
                {
                    searchBox.TextChanged += SearchBox_TextChanged;
                    searchBox.Foreground = Application.Current.FindResource("ForegroundBrush") as System.Windows.Media.Brush;
                    if (!string.IsNullOrWhiteSpace(_currentSearchText))
                    {
                        searchBox.Text = _currentSearchText;
                    }
                }

                // Load files from Supabase - force refresh based on navigation source
                bool shouldForceRefresh = _forceRefreshOnLoad || _cachedFiles == null || (DateTime.Now - _lastFetchTime) >= _cacheDuration;
                LogDebug($"[HZManifestView_Loaded] Force refresh from navigation: {_forceRefreshOnLoad}, Cache status: {_cachedFiles?.Count ?? 0} items, age: {(DateTime.Now - _lastFetchTime).TotalMinutes:F1} min, final force refresh: {shouldForceRefresh}");
                await LoadFilesFromSupabase(forceRefresh: shouldForceRefresh);
                
                if (debugInfo != null)
                    debugInfo.Text = $"DEBUG: Files count after load = {Files.Count}";
                
                // Setup lazy loading untuk thumbnails (tidak load semuanya)
                // Ini akan load thumbnail hanya saat user scroll/view
                SetupLazyThumbnailLoading();

                // Setup category tabs after data is loaded
                SetupCategoryTabs();
                
                LogDebug($"HZManifestView_Loaded completed. Files in collection: {Files.Count}");
                
                // Hide loading overlay
                if (loadingOverlay != null)
                {
                    StopContinuousLoadingAnimation();
                    loadingOverlay.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error loading manifest view: {ex.Message}");
                Logger.Log($"[HZManifestView] Error: {ex.Message}");
                
                var debugInfo = FindName("DebugInfo") as TextBlock;
                if (debugInfo != null)
                    debugInfo.Text = $"ERROR: {ex.Message}";
                
                // Hide loading overlay on error
                var loadingOverlay = FindName("LoadingOverlay") as Grid;
                if (loadingOverlay != null)
                {
                    StopContinuousLoadingAnimation();
                    loadingOverlay.Visibility = Visibility.Collapsed;
                }
            }
        }

        //private void SetupAutoRefreshTimer()
        //{
        //    try
        //    {
        //        _autoRefreshTimer = new System.Windows.Threading.DispatcherTimer();
        //        _autoRefreshTimer.Interval = TimeSpan.FromSeconds(30); // Auto-refresh setiap 30 detik
        //        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        //        _autoRefreshTimer.Start();
        //        LogDebug("[AUTO-REFRESH] Timer started - will check for new files every 30 seconds");
        //    }
        //    catch (Exception ex)
        //    {
        //        LogDebug($"[AUTO-REFRESH] Error setting up timer: {ex.Message}");
        //    }
        //}

        //private void StopAutoRefreshTimer()
        //{
        //    try
        //    {
        //        if (_autoRefreshTimer != null)
        //        {
        //            _autoRefreshTimer.Stop();
        //            LogDebug("[AUTO-REFRESH] Timer stopped");
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        LogDebug($"[AUTO-REFRESH] Error stopping timer: {ex.Message}");
        //    }
        //}

        //private async void AutoRefreshTimer_Tick(object? sender, EventArgs e)
        //{
        //    try
        //    {
        //        // Skip if already refreshing
        //        if (_isRefreshing)
        //            return;

        //        _isRefreshing = true;
        //        LogDebug("[AUTO-REFRESH] Checking for new files...");
        //        
        //        // Refresh file list with FORCE REFRESH (bypass cache) to detect new files
        //        await LoadFilesFromSupabase(forceRefresh: true);
        //        
        //        LogDebug($"[AUTO-REFRESH] Check completed - Files in collection: {Files.Count}");
        //    }
        //    catch (Exception ex)
        //    {
        //        LogDebug($"[AUTO-REFRESH] Error during auto-refresh: {ex.Message}");
        //    }
        //    finally
        //    {
        //        _isRefreshing = false;
        //    }
        //}

        private void HZManifestView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_activeManifestView == this)
                _activeManifestView = null;
            try
            {
                LogDebug("[HZManifestView_Unloaded] View unloaded");
                
                // Clean up debounce timer
                if (_sizeChangeDebounceTimer != null)
                {
                    _sizeChangeDebounceTimer.Stop();
                    _sizeChangeDebounceTimer = null;
                }
                
                // Unsubscribe from 18+ content setting changes to prevent memory leak
                Allow18PlusContentPreferences.Allow18PlusContentChanged -= Allow18PlusContentPreferences_Changed;
                
                // Timer is disabled, no need to stop it
            }
            catch (Exception ex)
            {
                LogDebug($"[HZManifestView_Unloaded] Error: {ex.Message}");
            }
        }


        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                _currentSearchText = (sender as TextBox)?.Text?.Trim().ToLower() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(_currentSearchText))
                {
                    LogDebug("Search cleared, returning to paginated view");
                    _currentPage = 0;
                }
                else
                {
                    LogDebug($"Searching for: {_currentSearchText}");
                }

                DisplayCurrentPage();
            }
            catch (Exception ex)
            {
                LogDebug($"Error in SearchBox_TextChanged: {ex.Message}");
            }
        }

        private void SetupCategoryTabs()
        {
            try
            {
                if (FindName("CategoryTabs") is not StackPanel categoryTabs)
                    return;

                // Clear existing category buttons (except All)
                categoryTabs.Children.Clear();
                _categoryButtons.Clear();
                _categoryFilterItems.Clear();

                // Predefined categories based on user specification
                var predefinedCategories = new List<string>
                {
                    // Genre Utama (Top-Level Genres)
                    "Action",
                    "Indie", 
                    "Adventure",
                    "RPG",
                    "Simulation",
                    "Strategy",
                    "Casual",
                    "Racing",
                    "Sport",
                    // Sub-genre & Tag Populer
                    "Survival Craft",
                    "Roguelike/Roguelite",
                    "Roguelike",
                    "Souls-like",
                    "Horror",
                    "Bullet Heavens",
                    "4X Strategy",
                    "Puzzle & Platformer",
                    "Co-op/Multiplayer",
                    "Co-op",
                    "Local Co-op",
                    // Genre Berdasarkan Model Bisnis
                    "Free to Play (F2P)",
                    "Early Access",
                    "Split Screen"
                };

                // Add "All" to filter dropdown
                _categoryFilterItems.Add(new CategoryFilterItem
                {
                    Category = "All",
                    DisplayName = $"All Categories ({_allFiles.Count})",
                    Count = _allFiles.Count
                });

                // Sort categories alphabetically
                var sortedCategories = predefinedCategories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

                // Add predefined categories to filter dropdown
                foreach (var category in sortedCategories)
                {
                    var count = _allFiles.Count(f => GenreMatchesCategory(f.Genre, category));
                    
                    if (count > 0) // Only add categories that have files
                    {
                        _categoryFilterItems.Add(new CategoryFilterItem
                        {
                            Category = category,
                            DisplayName = $"{category} ({count})",
                            Count = count
                        });
                    }
                }

                // Setup dropdown (don't change current selection)
                if (FindName("CategoryFilterComboBox") is ComboBox comboBox)
                {
                    comboBox.ItemsSource = _categoryFilterItems;
                    var selectedItem = _categoryFilterItems.FirstOrDefault(item => item.Category == _currentFilterCategory);
                    if (selectedItem != null)
                    {
                        comboBox.SelectedItem = selectedItem;
                    }
                    else if (comboBox.SelectedIndex < 0)
                    {
                        comboBox.SelectedIndex = 0; // Select "All" by default
                    }
                }

                // Setup sort dropdown
                if (FindName("SortComboBox") is ComboBox sortComboBox)
                {
                    // Set default sort to "Name A-Z"
                    foreach (ComboBoxItem item in sortComboBox.Items)
                    {
                        if (item.Tag as string == _currentSortOrder)
                        {
                            sortComboBox.SelectedItem = item;
                            break;
                        }
                    }
                }

                // Add All button
                var allButton = new Button
                {
                    Content = $"All ({_allFiles.Count})",
                    Width = 100,
                    Height = 28,
                    Background = Application.Current.FindResource("PrimaryButtonBrush") as System.Windows.Media.Brush,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Style = Application.Current.FindResource("RoundedButtonStyle") as Style,
                    ToolTip = $"All categories ({_allFiles.Count} items)"
                };
                allButton.Click += CategoryButton_Click;
                categoryTabs.Children.Add(allButton);
                _categoryButtons["All"] = allButton;

                // Create category buttons for each predefined category
                foreach (var category in predefinedCategories)
                {
                    var count = _allFiles.Count(f => GenreMatchesCategory(f.Genre, category));
                    
                    if (count > 0) // Only create buttons for categories that have files
                    {
                        var displayText = category.Length > 12 ? category.Substring(0, 10) + "..." : category;
                        var buttonText = $"{displayText} ({count})";

                        var categoryButton = new Button
                        {
                            Content = buttonText,
                            Width = 120,
                            Height = 28,
                            Background = Application.Current.FindResource("SecondaryButtonBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray,
                            Foreground = System.Windows.Media.Brushes.White,
                            FontWeight = FontWeights.Normal,
                            FontSize = 10,
                            Margin = new Thickness(0, 0, 4, 0),
                            Cursor = System.Windows.Input.Cursors.Hand,
                            Style = Application.Current.FindResource("RoundedButtonStyle") as Style,
                            ToolTip = $"{category} ({count} items)"
                        };
                        categoryButton.Click += CategoryButton_Click;
                        categoryTabs.Children.Add(categoryButton);
                        _categoryButtons[category] = categoryButton;
                    }
                }

                // Set initial selection based on previous state if available
                var initialSelection = _categoryButtons.ContainsKey(_currentCategory)
                    ? _currentCategory
                    : _currentFilterCategory;

                if (string.IsNullOrWhiteSpace(initialSelection) || !_categoryButtons.ContainsKey(initialSelection))
                    initialSelection = "All";

                UpdateCategorySelection(initialSelection);
            }
            catch (Exception ex)
            {
                LogDebug($"Error setting up category tabs: {ex.Message}");
            }
        }

        private void UpdateCategorySelection(string selectedCategory)
        {
            _currentCategory = selectedCategory;
            _currentFilterCategory = selectedCategory; // Sync with filter category

            foreach (var kvp in _categoryButtons)
            {
                var button = kvp.Value;
                if (kvp.Key == selectedCategory)
                {
                    button.Background = Application.Current.FindResource("PrimaryButtonBrush") as System.Windows.Media.Brush;
                    button.FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    button.Background = Application.Current.FindResource("SecondaryButtonBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
                    button.FontWeight = FontWeights.Normal;
                }
            }

            // Refresh display with new category filter
            DisplayCurrentPage();
        }

        private void UpdateFilterCategory(string selectedCategory)
        {
            _currentFilterCategory = selectedCategory;

            // Update dropdown selection without triggering event
            if (FindName("CategoryFilterComboBox") is ComboBox comboBox)
            {
                var selectedItem = _categoryFilterItems.FirstOrDefault(item => item.Category == selectedCategory);
                if (selectedItem != null)
                {
                    comboBox.SelectedItem = selectedItem;
                }
            }

            // Refresh display with new filter
            DisplayCurrentPage();
        }

        private List<ManifestFile> ApplySorting(List<ManifestFile> files)
        {
            return _currentSortOrder switch
            {
                "NameAsc" => files
                    .OrderBy(f => f.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.CreatedAt ?? DateTime.MinValue)
                    .ToList(),
                "NameDesc" => files
                    .OrderByDescending(f => f.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(f => f.CreatedAt ?? DateTime.MinValue)
                    .ToList(),
                "DateDesc" => files
                    .OrderByDescending(f => f.CreatedAt ?? DateTime.MinValue)
                    .ThenBy(f => f.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                "DateAsc" => files
                    .OrderBy(f => f.CreatedAt ?? DateTime.MinValue)
                    .ThenBy(f => f.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                "CategoryAsc" => files
                    .OrderBy(f => f.Genre ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                "CategoryDesc" => files
                    .OrderByDescending(f => f.Genre ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                _ => files.OrderBy(f => f.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                if (selectedItem.Tag is string sortOrder)
                {
                    _currentSortOrder = sortOrder;
                    DisplayCurrentPage();
                }
            }
        }

        private void CategoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is CategoryFilterItem selectedItem)
            {
                UpdateCategorySelection(selectedItem.Category);
            }
        }

        private void CategoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Content is string buttonText)
            {
                // Extract category name from button text (remove count)
                var category = buttonText;
                if (category.Contains(" ("))
                {
                    category = category.Substring(0, category.LastIndexOf(" ("));
                }

                // Handle truncated category names
                if (category.EndsWith("..."))
                {
                    var fullCategory = _categoryButtons.FirstOrDefault(kvp => 
                        kvp.Value == button && kvp.Key.Length > 12 && kvp.Key.StartsWith(category.Substring(0, category.Length - 3))).Key;
                    if (!string.IsNullOrEmpty(fullCategory))
                        category = fullCategory;
                }

                UpdateCategorySelection(category);
            }
        }

        private static bool GenreMatchesCategory(string? genre, string category)
        {
            if (string.IsNullOrWhiteSpace(genre) || string.IsNullOrWhiteSpace(category))
                return false;

            var categoryOptions = category.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var genreItems = genre.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return genreItems.Any(genreItem => categoryOptions.Any(categoryOption =>
                string.Equals(genreItem, categoryOption, StringComparison.OrdinalIgnoreCase)));
        }

        private bool FileFilterPredicate(object obj)
        {
            if (obj is not ManifestFile file)
                return false;

            if (_currentFilterCategory != "All" && !GenreMatchesCategory(file.Genre, _currentFilterCategory))
                return false;

            if (FindName("SearchBox") is not TextBox searchBox)
                return true;

            string searchText = searchBox.Text?.ToLower() ?? "";
            if (string.IsNullOrWhiteSpace(searchText))
                return true;

            // Search di Name dan FileName
            return (file.Name?.ToLower().Contains(searchText) ?? false) ||
                   (file.FileName?.ToLower().Contains(searchText) ?? false);
        }

        private void RegisterTextElements()
        {
            try
            {
                if (FindName("ManifestTitle") is TextBlock manifestTitle)
                    manifestTitle.Text = Application.Current.FindResource("Dashboard.HZManifest")?.ToString() ?? "HZ Manifest";
                if (FindName("ManifestSubtitle") is TextBlock manifestSubtitle)
                    manifestSubtitle.Text = "Download lua scripts and manifest files";
                if (FindName("RefreshText") is TextBlock refreshText)
                    refreshText.Text = "Refresh";
                if (FindName("BackButtonText") is TextBlock backButtonText)
                    backButtonText.Text = "Back";
            }
            catch (Exception ex)
            {
                LogDebug($"[HZManifestView] RegisterTextElements error: {ex.Message}");
            }
        }

        private async Task LoadFilesFromSupabase(bool forceRefresh = false)
        {
            try
            {
                LogDebug("=== HZManifestView.LoadFilesFromSupabase STARTED ===");
                if (forceRefresh)
                    LogDebug("**FORCE REFRESH MODE - Cache will be bypassed**");
                LogDebug($"Files before load: {Files.Count} items");
                
                var debugInfo = FindName("DebugInfo") as TextBlock;
                var loadingStatusText = FindName("LoadingStatusText") as TextBlock;
                
                if (loadingStatusText != null)
                    loadingStatusText.Text = "Fetching data from database...";
                if (debugInfo != null)
                    debugInfo.Text = "DEBUG: Fetching from Supabase...";

                // Check if we have valid cached data and not forcing refresh
                bool hasValidCache = _cachedFiles != null && 
                                   (DateTime.Now - _lastFetchTime) < _cacheDuration &&
                                   !forceRefresh;

                if (hasValidCache)
                {
                    LogDebug("✓ Using cached HZManifest data to avoid refetching");
                    // Load from cache and reapply current sort/filter before display
                    _allFiles.Clear();
                    _allFiles.AddRange(_cachedFiles);
                    _currentPage = Math.Max(0, Math.Min(_cachedPage, (int)Math.Ceiling((double)_cachedFiles.Count / _pageSize) - 1));
                    DisplayCurrentPage();
                    
                    if (debugInfo != null)
                        debugInfo.Text = $"DEBUG: Loaded {Files.Count} files from cache ✓";
                    
                    // Update status
                    if (loadingStatusText != null)
                        loadingStatusText.Text = "Loading thumbnails...";
                    
                    // Setup thumbnail loading
                    SetupLazyThumbnailLoading();
                    
                    LogDebug($"Final result: {Files.Count} files loaded from cache");
                    
                    LogDebug("=== HZManifestView.LoadFilesFromSupabase COMPLETED (FROM CACHE) ===");
                    return;
                }

                LogDebug("Attempting to fetch from Supabase...");
                bool success = await FetchStorageFiles(forceRefresh);
                
                if (success && Files.Count > 0)
                {
                    LogDebug($"✓ Successfully fetched {Files.Count} files from Supabase!");
                    
                    // Check if there are new manifests since last seen count
                    await NotifyIfNewManifestDataAsync(Files.Count);
                    
                    // Cache the fetched data
                    _cachedFiles = new List<ManifestFile>(_allFiles);
                    _cachedPage = _currentPage;
                    _lastFetchTime = DateTime.Now;
                    LogDebug("✓ Data cached for future use");
                    
                    if (debugInfo != null)
                        debugInfo.Text = $"DEBUG: Loaded {Files.Count} files from Supabase ✓";
                    
                    // Update status
                    if (loadingStatusText != null)
                        loadingStatusText.Text = "Loading thumbnails...";
                    
                    // Setup thumbnail loading untuk files that were just loaded
                    LogDebug("Setting up thumbnail loading for newly loaded files...");
                    SetupLazyThumbnailLoading();
                }
                else
                {
                    LogDebug("⚠️  Supabase fetch failed or returned 0 items");
                    LogDebug($"Files currently in collection: {Files.Count}");
                    
                    if (debugInfo != null)
                        debugInfo.Text = $"DEBUG: No data loaded (waiting for Supabase)";
                }
                
                if (loadingStatusText != null)
                    loadingStatusText.Text = "Preparing display...";
                
                LogDebug($"Final result: {Files.Count} files ready to display");
                
                LogDebug("=== HZManifestView.LoadFilesFromSupabase COMPLETED ===");
            }
            catch (Exception ex)
            {
                LogDebug("=== EXCEPTION in LoadFilesFromSupabase ===");
                LogDebug($"Type: {ex.GetType().Name}");
                LogDebug($"Message: {ex.Message}");
                LogDebug($"Stack:\n{ex.StackTrace}");
                
                var loadingStatusText = FindName("LoadingStatusText") as TextBlock;
                if (loadingStatusText != null)
                    loadingStatusText.Text = "Error loading data";
                
                var debugInfo = FindName("DebugInfo") as TextBlock;
                if (debugInfo != null)
                    debugInfo.Text = $"DEBUG: ERROR - {ex.Message}";
            }
        }

        private Task NotifyIfNewManifestDataAsync(int currentCount)
        {
            try
            {
                var previousState = CacheManager.GetLastSeenManifestState();
                if (previousState.LastSeenManifestCount < 0)
                {
                    LogDebug("First manifest load detected, updating last seen state without notification");
                    previousState.LastSeenManifestCount = currentCount;
                    previousState.LastSeenManifestIds = _allFiles.Where(file => file.Id.HasValue).Select(file => file.Id!.Value).Distinct().ToList();
                    CacheManager.SetLastSeenManifestState(previousState);
                    HideManifestNotificationBanner();
                    return Task.CompletedTask;
                }

                var currentIds = _allFiles.Where(file => file.Id.HasValue).Select(file => file.Id!.Value).Distinct().ToHashSet();
                var newIds = previousState.LastSeenManifestIds?.Any() == true
                    ? currentIds.Except(previousState.LastSeenManifestIds).ToList()
                    : currentIds.ToList();

                var newFiles = _allFiles.Where(file => file.Id.HasValue && newIds.Contains(file.Id!.Value)).ToList();
                if (!newFiles.Any() && currentCount > previousState.LastSeenManifestCount)
                {
                    int missingCount = currentCount - previousState.LastSeenManifestCount;
                    newFiles = _allFiles
                        .OrderByDescending(file => file.CreatedAt ?? DateTime.MinValue)
                        .Take(missingCount)
                        .ToList();
                }

                previousState.LastSeenManifestCount = currentCount;
                previousState.LastSeenManifestIds = currentIds.ToList();
                CacheManager.SetLastSeenManifestState(previousState);

                if (newFiles.Any())
                {
                    LogDebug($"Detected {newFiles.Count} new manifest(s) since last visit");
                    Dispatcher.Invoke(() => UpdateManifestNotificationBanner(newFiles.Select(file => file.Name))); 
                }
                else
                {
                    LogDebug("No new manifest items detected");
                    Dispatcher.Invoke(HideManifestNotificationBanner);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error checking new manifest notifications: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private void UpdateManifestNotificationBanner(IEnumerable<string> newManifestNames)
        {
            try
            {
                var banner = FindName("NotificationBanner") as Border;
                var title = FindName("NotificationBannerTitle") as TextBlock;
                var details = FindName("NotificationBannerDetails") as TextBlock;

                if (banner == null || title == null || details == null)
                    return;

                var names = newManifestNames.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
                if (!names.Any())
                {
                    banner.Visibility = Visibility.Collapsed;
                    return;
                }

                title.Text = names.Count == 1
                    ? "A new game manifest has been added"
                    : $"{names.Count} new game manifests have been added";

                var displayNames = names.Take(5).ToList();
                var detailsText = string.Join(", ", displayNames);
                if (names.Count > displayNames.Count)
                {
                    detailsText += $" and {names.Count - displayNames.Count} other games";
                }

                details.Text = detailsText;
                banner.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                LogDebug($"Error updating notification banner: {ex.Message}");
            }
        }

        private void HideManifestNotificationBanner()
        {
            try
            {
                var banner = FindName("NotificationBanner") as Border;
                if (banner != null)
                    banner.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                LogDebug($"Error hiding notification banner: {ex.Message}");
            }
        }

        public void SetForceRefresh(bool forceRefresh)
        {
            _forceRefreshOnLoad = forceRefresh;
        }

        public void SetRequiresDeviceVerificationOnLoad(bool requiresVerification)
        {
            _requiresDeviceVerificationOnLoad = requiresVerification;
        }

        private async Task FetchAllThumbnails()
        {
            try
            {
                LogDebug("=== STARTING THUMBNAIL FETCH FOR ALL FILES ===");
                int successCount = 0;

                foreach (var file in Files)
                {
                    if (file.AppId.HasValue && file.AppId.Value > 0)
                    {
                        bool success = await FetchAndCacheSteamThumbnail(file);
                        if (success) successCount++;
                    }
                }

                LogDebug($"✓ Completed thumbnail fetch: {successCount}/{Files.Count} successful");
            }
            catch (Exception ex)
            {
                LogDebug($"Error fetching thumbnails: {ex.Message}");
            }
        }

        private void SetupLazyThumbnailLoading()
        {
            try
            {
                if (_thumbnailLoadDebounceTimer == null)
                {
                    InitializeThumbnailLoadDebounceTimer();
                }

                _thumbnailLoadDebounceTimer?.Stop();
                _thumbnailLoadDebounceTimer?.Start();
            }
            catch (Exception ex)
            {
                LogDebug($"Error setting up thumbnail loading: {ex.Message}");
            }
        }

        private async void ThumbnailLoadDebounceTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                _thumbnailLoadDebounceTimer?.Stop();
                await LoadVisiblePageThumbnailsAsync();
            }
            catch (Exception ex)
            {
                LogDebug($"Error loading visible thumbnails: {ex.Message}");
            }
        }

        private async Task LoadVisiblePageThumbnailsAsync()
        {
            try
            {
                var visibleFiles = new List<ManifestFile>();
                if (_lastFilteredFiles.Count > 0)
                {
                    int startIndex = Math.Max(0, _currentPage * _pageSize);
                    visibleFiles = _lastFilteredFiles.Skip(startIndex).Take(Math.Max(1, _pageSize)).ToList();
                }
                else
                {
                    visibleFiles = Files.ToList();
                }

                int started = 0;
                int thumbnailLimit = Math.Max(MIN_VISIBLE_THUMBNAILS_PER_PASS, _pageSize);
                foreach (var file in visibleFiles)
                {
                    if (started >= thumbnailLimit)
                    {
                        break;
                    }

                    if (!file.AppId.HasValue || file.AppId.Value <= 0)
                    {
                        continue;
                    }

                    string thumbnailKey = GetThumbnailKey(file);
                    if (string.IsNullOrEmpty(thumbnailKey) ||
                        _thumbnailLoadRequestedKeys.Contains(thumbnailKey) ||
                        _thumbnailLoadCompletedKeys.Contains(thumbnailKey) ||
                        _thumbnailLoadFailedKeys.Contains(thumbnailKey))
                    {
                        continue;
                    }

                    _thumbnailLoadRequestedKeys.Add(thumbnailKey);
                    started++;
                    _ = FetchAndCacheSteamThumbnail(file, thumbnailKey);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error queueing visible thumbnails: {ex.Message}");
            }
        }

        private static string GetThumbnailKey(ManifestFile file)
        {
            if (file.AppId.HasValue && file.AppId.Value > 0)
            {
                return $"app:{file.AppId.Value}";
            }

            if (file.Id.HasValue && file.Id.Value > 0)
            {
                return $"id:{file.Id.Value}";
            }

            return $"name:{file.FileName}";
        }
        
        private async Task<bool> FetchAndCacheSteamThumbnail(ManifestFile file, string? thumbnailKey = null)
        {
            bool success = false;
            try
            {
                if (!file.AppId.HasValue || file.AppId.Value <= 0)
                    return false;

                if (!string.IsNullOrEmpty(file.ThumbnailPath))
                {
                    if (!string.IsNullOrEmpty(thumbnailKey))
                    {
                        _thumbnailLoadCompletedKeys.Add(thumbnailKey);
                    }
                    return true;
                }

                string cacheFileName = $"{file.AppId}_header.jpg";
                string cachedPath = Path.Combine(_thumbnailCacheFolder, cacheFileName);

                // Check if already cached
                if (File.Exists(cachedPath))
                {
                    // Convert to file:// URI for Image binding
                    file.ThumbnailPath = new Uri(cachedPath, UriKind.Absolute).ToString();
                    success = true;
                    return true;
                }

                // Try multiple thumbnail formats in order of preference
                // Steam CDN URL - use smaller resolution (hemat 94% storage)
                // Full header.jpg: ~500KB per image
                // header_292x136.jpg: ~30KB per image (preferred)
                string[] imageFormats = new[]
                    {
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/header_292x136.jpg",  // Small header (preferred)
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/header_616x353.jpg",  // Medium header
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/header_1840x620.jpg", // Large header
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/capsule_231x87.jpg",   // Small capsule
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/capsule_467x181.jpg",  // Medium capsule
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/header.jpg",           // Full header
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/library_600x900.jpg",  // Library portrait
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/capsule.jpg",          // Full capsule (last resort)
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{file.AppId}/logo.png"              // Logo if nothing else
                    };

                byte[]? downloadedData = null;
                
                // Try each format
                foreach (var steamImageUrl in imageFormats)
                {
                    try
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, steamImageUrl);
                        
                        // Add timeout untuk thumbnail fetch (5 detik)
                        using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        {
                            var response = await _httpClient.SendAsync(request, cts.Token);

                            if (response.IsSuccessStatusCode)
                            {
                                downloadedData = await response.Content.ReadAsByteArrayAsync();
                                if (downloadedData != null && downloadedData.Length > 0)
                                {
                                    LogDebug($"Downloaded thumbnail for {file.Name} (AppID: {file.AppId}) from {steamImageUrl.Split('/').Last()}");
                                    break; // Success, exit loop
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Try next format
                        continue;
                    }
                }

                // If we got image data, save and use it
                if (downloadedData != null && downloadedData.Length > 0)
                {
                    await File.WriteAllBytesAsync(cachedPath, downloadedData);
                    // Convert to file:// URI for Image binding
                    file.ThumbnailPath = new Uri(cachedPath, UriKind.Absolute).ToString();
                    success = true;
                    return true;
                }
                else
                {
                    LogDebug($"Failed to download thumbnail for AppID {file.AppId}: All formats returned 404. Trying store page scrape...");
                    // Try to scrape the Steam store page for the asset URL (og:image)
                    try
                    {
                        string storeUrl = $"https://store.steampowered.com/app/{file.AppId}";
                        var resp = await _httpClient.GetAsync(storeUrl);
                        if (resp.IsSuccessStatusCode)
                        {
                            var html = await resp.Content.ReadAsStringAsync();
                            // look for og:image meta
                            var m = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]*property=[\"']og:image[\"'][^>]*content=[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (!m.Success)
                            {
                                // alternate attribute order
                                m = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]*content=[\"']([^\"']+)[\"'][^>]*property=[\"']og:image[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            }

                            if (m.Success && Uri.IsWellFormedUriString(m.Groups[1].Value, UriKind.Absolute))
                            {
                                var assetUrl = m.Groups[1].Value;
                                LogDebug($"Found store asset URL: {assetUrl}");
                                try
                                {
                                    using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
                                    var assetResp = await _httpClient.GetAsync(assetUrl, cts2.Token);
                                    if (assetResp.IsSuccessStatusCode)
                                    {
                                        var bytes = await assetResp.Content.ReadAsByteArrayAsync();
                                        if (bytes != null && bytes.Length > 0)
                                        {
                                            await File.WriteAllBytesAsync(cachedPath, bytes);
                                            file.ThumbnailPath = new Uri(cachedPath, UriKind.Absolute).ToString();
                                            success = true;
                                            LogDebug($"Downloaded thumbnail from store asset for {file.Name} (AppID: {file.AppId})");
                                            return true;
                                        }
                                    }
                                    else
                                    {
                                        LogDebug($"Failed to download store asset URL: HTTP {(int)assetResp.StatusCode}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogDebug($"Error downloading store asset URL for AppID {file.AppId}: {ex.Message}");
                                }
                            }
                            else
                            {
                                LogDebug($"No og:image found on store page for AppID {file.AppId}");
                            }
                        }
                        else
                        {
                            LogDebug($"Failed to fetch store page for AppID {file.AppId}: HTTP {(int)resp.StatusCode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"Error scraping store page for AppID {file.AppId}: {ex.Message}");
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error fetching thumbnail for AppID {file.AppId}: {ex.Message}");
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(thumbnailKey))
                {
                    _thumbnailLoadRequestedKeys.Remove(thumbnailKey);
                    if (success)
                    {
                        _thumbnailLoadCompletedKeys.Add(thumbnailKey);
                    }
                    else
                    {
                        _thumbnailLoadFailedKeys.Add(thumbnailKey);
                    }
                }
            }
        }

        private async Task<bool> FetchStorageFiles(bool forceRefresh = false)
        {
            try
            {
                LogDebug("=== STARTING FETCH FROM SUPABASE TABLE ===");

                // Log cache status
                string cacheStatus = CacheManager.GetHzmCacheStatus("hzm_manifest_data");
                LogDebug($"Cache Status: {cacheStatus}");

                // Bypass cache jika forceRefresh = true
                if (!forceRefresh)
                {
                    // Coba ambil dari cache dulu
                    var cachedData = CacheManager.GetHzmCache("hzm_manifest_data");
                    if (cachedData != null)
                    {
                        LogDebug("✓ Found HZM data in cache. Parsing cached data...");
                        bool parsed = ParseSupabaseTableResponse(cachedData);
                        if (parsed)
                        {
                            LogDebug("✓ Successfully parsed cached HZM data.");
                            return true;
                        }
                        LogDebug("⚠️ Cached HZM data parsing failed. Fetching from source...");
                    }
                }
                else
                {
                    LogDebug("[FORCE REFRESH] Bypassing cache, fetching from network...");
                }

                LogDebug($"Endpoint: {SupabaseConfig.SupabaseUrl}/rest/v1/hzmanifest_files");

                // Fetch ALL data dengan offset pagination
                List<string> allJsonArrays = new();
                int offset = 0;
                int limit = 1000;
                int batchCount = 0;
                bool hasMoreData = true;

                while (hasMoreData)
                {
                    batchCount++;
                    LogDebug($"Fetching batch {batchCount}: offset={offset}, limit={limit}");
                    
                    string url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/hzmanifest_files?select=*&order=id.asc&limit={limit}&offset={offset}";

                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                    request.Headers.Add("Prefer", "count=exact");

                    LogDebug($"URL: {url}");

                    // Create a cancellation token with timeout
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    {
                        var response = await _httpClient.SendAsync(request, cts.Token);
                        string content = await response.Content.ReadAsStringAsync();

                        LogDebug($"Batch {batchCount} - Response Status: {(int)response.StatusCode}");
                        LogDebug($"Batch {batchCount} - Response Length: {content.Length} bytes");
                        
                        if (response.Content.Headers.TryGetValues("Content-Range", out var rangeValues))
                        {
                            LogDebug($"Batch {batchCount} - Content-Range: {string.Join(", ", rangeValues)}");
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            LogDebug($"✗ Batch {batchCount} failed - Response: {content.Substring(0, Math.Min(500, content.Length))}");
                            
                            if (batchCount == 1)
                            {
                                LogDebug("Attempting to fall back to cache...");
                                var fallbackCache = CacheManager.GetHzmCache("hzm_manifest_data");
                                if (fallbackCache != null)
                                {
                                    LogDebug("✓ Using fallback cache due to API failure");
                                    bool fallbackParsed = ParseSupabaseTableResponse(fallbackCache);
                                    return fallbackParsed;
                                }
                            }
                            
                            if (allJsonArrays.Count > 0)
                            {
                                LogDebug($"⚠️ Batch {batchCount} failed but we have {allJsonArrays.Count} batch(es) already");
                                hasMoreData = false;
                            }
                            else
                            {
                                return false;
                            }
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(content) || content == "[]")
                            {
                                LogDebug($"Batch {batchCount} - No data returned, stopping pagination");
                                hasMoreData = false;
                            }
                            else
                            {
                                try
                                {
                                    var jsonDoc = JsonDocument.Parse(content);
                                    int itemCount = jsonDoc.RootElement.GetArrayLength();
                                    LogDebug($"✓ Batch {batchCount} - Got {itemCount} items");
                                    
                                    allJsonArrays.Add(content);
                                    offset += limit;
                                    
                                    if (itemCount < limit)
                                    {
                                        LogDebug($"Batch {batchCount} - Got less than {limit} items, this is the last batch");
                                        hasMoreData = false;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogDebug($"Batch {batchCount} - Error parsing JSON: {ex.Message}");
                                    hasMoreData = false;
                                }
                            }
                        }
                    }
                }

                LogDebug($"✓ Pagination complete - fetched {batchCount} batches");

                if (allJsonArrays.Count == 0)
                {
                    LogDebug("✗ No data fetched");
                    return false;
                }

                string mergedContent = MergeJsonArrays(allJsonArrays);
                LogDebug($"✓ Merged {allJsonArrays.Count} batches into single JSON response");
                LogDebug($"✓ Merged content length: {mergedContent.Length} bytes");

                LogDebug("✓ SUCCESS! Parsing merged response...");
                bool parsedResponse = ParseSupabaseTableResponse(mergedContent);
                LogDebug($"Parse result: {parsedResponse}, items loaded: {_totalFiles}");

                if (parsedResponse)
                {
                    CacheManager.SaveHzmCache("hzm_manifest_data", mergedContent);
                    LogDebug("✓ Saved merged HZM data to cache.");
                }

                return parsedResponse;
            }
            catch (System.Threading.Tasks.TaskCanceledException ex)
            {
                LogDebug("=== TIMEOUT IN FetchStorageFiles ===");
                LogDebug($"Request timed out after 15 seconds: {ex.Message}");
                LogDebug("Attempting to fall back to cache...");
                
                // If timeout, try to use cache
                var cachedData = CacheManager.GetHzmCache("hzm_manifest_data");
                if (cachedData != null)
                {
                    LogDebug("✓ Using cache due to timeout");
                    bool parsed = ParseSupabaseTableResponse(cachedData);
                    return parsed;
                }
                
                LogDebug("✗ No cache available to use as fallback");
                return false;
            }
            catch (Exception ex)
            {
                LogDebug("=== EXCEPTION IN FetchStorageFiles ===");
                LogDebug($"Type: {ex.GetType().Name}");
                LogDebug($"Message: {ex.Message}");
                
                // Try cache as fallback
                LogDebug("Attempting to use cache as fallback...");
                var fallbackData = CacheManager.GetHzmCache("hzm_manifest_data");
                if (fallbackData != null)
                {
                    LogDebug("✓ Using cache due to exception");
                    bool parsed = ParseSupabaseTableResponse(fallbackData);
                    return parsed;
                }
                
                return false;
            }
        }

        private bool ParseSupabaseTableResponse(string content)
        {
            try
            {
                LogDebug("=== PARSING SUPABASE TABLE RESPONSE ===");
                LogDebug($"Response content length: {content.Length} bytes");
                
                if (string.IsNullOrWhiteSpace(content))
                {
                    LogDebug("✗ Empty response content");
                    return false;
                }

                var jsonDoc = JsonDocument.Parse(content);
                var root = jsonDoc.RootElement;

                // Expect array of objects
                if (root.ValueKind != JsonValueKind.Array)
                {
                    LogDebug($"✗ Expected array, got {root.ValueKind}");
                    return false;
                }

                // Count total items in JSON before processing
                int totalItemsInJson = root.GetArrayLength();
                LogDebug($"✓ Total items in JSON response: {totalItemsInJson}");

                int fileCount = 0;
                List<ManifestFile> allParsedFiles = new();
                
                foreach (var item in root.EnumerateArray())
                {
                    try
                    {
                        var dbName = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                        var appId = item.TryGetProperty("app_id", out var appIdElement) && appIdElement.TryGetInt32(out int aid) ? aid : 0;
                        var description = item.TryGetProperty("description", out var descElement) ? descElement.GetString() ?? "" : "";
                        var genre = item.TryGetProperty("genre", out var genreElement) ? genreElement.GetString() ?? "" : "";
                        
                        // Parse is_adult field - can be boolean OR string "true"/"false"
                        bool isAdult = false;
                        if (item.TryGetProperty("is_adult", out var isAdultElement))
                        {
                            if (isAdultElement.ValueKind == System.Text.Json.JsonValueKind.True)
                            {
                                // Handle JSON boolean true
                                isAdult = true;
                            }
                            else if (isAdultElement.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                // Handle string "true" or "false" from Supabase
                                string isAdultStr = isAdultElement.GetString()?.ToLower() ?? "false";
                                isAdult = isAdultStr == "true";
                            }
                            else if (isAdultElement.ValueKind == System.Text.Json.JsonValueKind.False)
                            {
                                // Handle JSON boolean false
                                isAdult = false;
                            }
                        }
                        
                        // Debug logging for is_adult field
                        if (fileCount < 5 || isAdult) // Log first 5 files and all adult files
                        {
                            LogDebug($"[Parse] File: {dbName} (AppID: {appId}) - is_adult field exists: {item.TryGetProperty("is_adult", out _)}, is_adult value: {isAdult}");
                        }
                        
                        string storageType = "zip";
                        if (item.TryGetProperty("storage_type", out var storageTypeElement))
                        {
                            if (storageTypeElement.ValueKind == JsonValueKind.String)
                                storageType = storageTypeElement.GetString() ?? "zip";
                            else if (storageTypeElement.ValueKind == JsonValueKind.True || storageTypeElement.ValueKind == JsonValueKind.False)
                                storageType = storageTypeElement.GetBoolean() ? "true" : "false";
                        }

                        string folderPath = string.Empty;
                        if (item.TryGetProperty("folder_path", out var folderPathElement))
                        {
                            if (folderPathElement.ValueKind == JsonValueKind.String)
                                folderPath = folderPathElement.GetString() ?? string.Empty;
                            // Ignore non-string folder_path values that may come from schema mismatch
                        }

                        var file = new ManifestFile
                        {
                            Id = item.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out int id) ? id : null,
                            AppId = appId,
                            FileName = item.TryGetProperty("file_name", out var fnElement) ? fnElement.GetString() ?? "" : "",
                            Name = dbName,  // Use real game name from database (now updated with actual Steam names)
                            Description = description,  // Steam game description
                            Genre = genre,  // Steam genres
                            MinimumRequirements = item.TryGetProperty("minimum_requirements", out var minReqElement) ? minReqElement.GetString() ?? "" : "",
                            RecommendedRequirements = item.TryGetProperty("recommended_requirements", out var recReqElement) ? recReqElement.GetString() ?? "" : "",
                            IsAdult = isAdult,  // Adult content flag from database
                            FileSize = item.TryGetProperty("file_size", out var sizeElement) ? sizeElement.GetString() ?? "Unknown" : "Unknown",
                            FileUrl = item.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? "" : "",
                            StorageType = storageType,
                            FolderPath = folderPath,
                            ThumbnailPath = "", // Reset thumbnail path - akan di-fetch ulang
                            DownloadCount = item.TryGetProperty("download_count", out var downloadCountElement) && downloadCountElement.TryGetInt32(out int downloadCount) ? downloadCount : 0
                        };

                        // Parse updated_at if exists
                        if (item.TryGetProperty("created_at", out var createdAtElement) && 
                            DateTime.TryParse(createdAtElement.GetString(), out var createdAt))
                        {
                            file.CreatedAt = createdAt;
                            file.UpdatedDate = createdAt.ToString("yyyy-MM-dd");
                        }
                        else if (item.TryGetProperty("updated_at", out var updatedAtElement) && 
                                 DateTime.TryParse(updatedAtElement.GetString(), out var updatedAt))
                        {
                            file.CreatedAt = updatedAt;
                            file.UpdatedDate = updatedAt.ToString("yyyy-MM-dd");
                        }

                        // Keep entries visible even if the database record has no download URL.
                        // This is important for manifest items that are stored as folders or otherwise
                        // rely on metadata-only records.
                        allParsedFiles.Add(file);
                        fileCount++;
                        LogDebug($"[{fileCount}] Parsed file: {file.Name ?? file.FileName} (AppID: {file.AppId}, hasUrl: {!string.IsNullOrWhiteSpace(file.FileUrl)}, isFolderStorage: {file.IsFolderStorage})");
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"Error parsing item: {ex.Message}");
                    }
                }

                LogDebug($"✓ Successfully parsed {fileCount} files");
                
                // Sort files by Name (A-Z)
                _allFiles = allParsedFiles.OrderBy(f => f.Name ?? "").ToList();
                LogDebug($"✓ Files sorted alphabetically (A-Z)");
                
                // Set total count dan reset ke page 0
                _totalFiles = _allFiles.Count;
                _currentPage = 0;
                LogDebug($"✓ Pagination initialized: {_totalFiles} total files, {_pageSize} per page");
                
                // Display first page
                DisplayCurrentPage();
                
                return fileCount > 0;
            }
            catch (Exception ex)
            {
                LogDebug($"✗ Exception parsing JSON: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        private void LogDebug(string message)
        {
            Logger.Log($"[HZManifestView] {message}");
        }

        private string FormatFileSize(long bytes)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;

            if (bytes >= GB)
                return Math.Round((double)bytes / GB, 2) + " GB";
            else if (bytes >= MB)
                return Math.Round((double)bytes / MB, 2) + " MB";
            else if (bytes >= KB)
                return Math.Round((double)bytes / KB, 2) + " KB";
            else
                return bytes + " B";
        }



        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            var loadingText = FindName("LoadingText") as TextBlock;
            if (loadingText != null)
                loadingText.Visibility = Visibility.Visible;
            try
            {
                // Hapus cache HZM sebelum refresh
                CacheManager.ClearHzmCache();
                LogDebug("HZM cache cleared for refresh.");

                await LoadFilesFromSupabase();
            }
            catch (Exception ex)
            {
                ShowError($"Refresh failed: {ex.Message}");
                if (loadingText != null)
                    loadingText.Visibility = Visibility.Collapsed;
            }
        }

        private void RequestButton_Click(object sender, RoutedEventArgs e)
        {
            ShowRequestPopup();
        }

        private void CancelRequestButton_Click(object sender, RoutedEventArgs e)
        {
            HideRequestPopup();
        }

        private async void SubmitRequestButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var steamUrl = RequestUrlTextBox?.Text?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(steamUrl))
                {
                    ShowRequestValidation("URL Steam wajib diisi.");
                    return;
                }

                if (!Uri.TryCreate(steamUrl, UriKind.Absolute, out var steamUri) ||
                    (steamUri.Scheme != Uri.UriSchemeHttp && steamUri.Scheme != Uri.UriSchemeHttps))
                {
                    ShowRequestValidation("URL Steam tidak valid. Gunakan format http atau https.");
                    return;
                }

                // Check if display name is set
                var displayName = await DashboardView.GetCurrentDisplayNameAsync();
                Logger.Log($"[HZManifestView] Display name check: '{displayName}'");
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    ShowRequestValidation("Display name harus diset sebelum request game. Silakan set display name di Dashboard.");
                    return;
                }

                // Extract AppID from Steam or SteamDB URL; SteamDB URLs do not require a game name in the path.
                var appIdMatch = Regex.Match(steamUrl, @"(?:store\.steampowered\.com/app|steamdb\.info/app)/(\d+)", RegexOptions.IgnoreCase);
                if (!appIdMatch.Success)
                {
                    ShowRequestValidation("URL tidak valid. Gunakan format Steam Store atau SteamDB: https://store.steampowered.com/app/{APPID}/{APP_NAME} atau https://steamdb.info/app/{APPID}/charts/");
                    return;
                }

                int appId = int.Parse(appIdMatch.Groups[1].Value);

                // Check if game already exists
                if (_allFiles.Any(f => f.AppId == appId))
                {
                    ShowRequestValidation("Game already available in-app.");
                    return;
                }

                var alreadyRequested = await GetPendingRequestsFromSupabaseAsync();
                var duplicateRequest = alreadyRequested.FirstOrDefault(r => r.AppId == appId);
                if (duplicateRequest != null)
                {
                    var requesterLabel = string.IsNullOrWhiteSpace(duplicateRequest.Requester) ? "another user" : duplicateRequest.Requester;
                    ShowRequestValidation($"This game has already been requested by {requesterLabel}.");
                    return;
                }

                var webhookUrl = DiscordWebhookUrl;
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    ShowRequestValidation("Discord webhook tidak tersedia di aplikasi.");
                    return;
                }

                SubmitRequestButton.IsEnabled = false;
                SubmitRequestButton.Content = "Sending...";
                RequestValidationText.Visibility = Visibility.Collapsed;

                var (success, messageId) = await SendDiscordRequestAsync(steamUrl, appId, webhookUrl);
                if (success)
                {
                    MessageBox.Show("Game request has been successfully sent.", "Request Submitted", MessageBoxButton.OK, MessageBoxImage.Information);
                    HideRequestPopup();
                }
                else
                {
                    ShowRequestValidation("Gagal mengirim request. Coba lagi nanti atau periksa webhook URL.");
                }
            }
            catch (Exception ex)
            {
                ShowRequestValidation($"Request gagal: {ex.Message}");
            }
            finally
            {
                SubmitRequestButton.IsEnabled = true;
                SubmitRequestButton.Content = "Submit";
            }
        }

        private class ManifestRequestRecord
        {
            [JsonPropertyName("app_id")]
            public int AppId { get; set; }

            [JsonPropertyName("steam_url")]
            public string? SteamUrl { get; set; }

            [JsonPropertyName("requester")]
            public string? Requester { get; set; }

            [JsonPropertyName("discord_message_id")]
            public string? DiscordMessageId { get; set; }

            [JsonPropertyName("webhook_url")]
            public string? WebhookUrl { get; set; }

            [JsonPropertyName("requested_at")]
            public DateTime? RequestedAt { get; set; }
        }

        private async Task<bool> SavePendingRequestToSupabaseAsync(int appId, string appName, string steamUrl, string requester, string webhookUrl, string? messageId)
        {
            try
            {
                var payload = new
                {
                    app_id = appId,
                    app_name = appName,
                    steam_url = steamUrl,
                    requester = requester,
                    requester_id = GetRequesterUuid(GetDeviceId()).ToString(),
                    webhook_url = webhookUrl,
                    discord_message_id = messageId
                };

                string url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/hzmanifest_requests";
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Prefer", "return=representation");

                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    LogDebug($"Failed to save pending request to Supabase: {response.StatusCode} - {responseBody}");
                    return false;
                }

                LogDebug($"Saved pending request for AppID {appId} to Supabase");
                return true;
            }
            catch (Exception ex)
            {
                LogDebug($"Error saving pending request to Supabase: {ex.Message}");
                return false;
            }
        }

        private async Task<List<ManifestRequestRecord>> GetPendingRequestsFromSupabaseAsync()
        {
            try
            {
                string url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/hzmanifest_requests?select=app_id,steam_url,requester,webhook_url,discord_message_id,requested_at";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Accept", "application/json");

                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    LogDebug($"Failed to fetch pending requests from Supabase: {response.StatusCode}");
                    return new List<ManifestRequestRecord>();
                }

                var content = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<List<ManifestRequestRecord>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return result ?? new List<ManifestRequestRecord>();
            }
            catch (Exception ex)
            {
                LogDebug($"Error fetching pending requests from Supabase: {ex.Message}");
                return new List<ManifestRequestRecord>();
            }
        }

        private async Task<bool> RemovePendingRequestFromSupabaseAsync(int appId)
        {
            try
            {
                string url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/hzmanifest_requests?app_id=eq.{appId}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Prefer", "return=minimal");

                using var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogDebug($"Error removing pending request from Supabase: {ex.Message}");
                return false;
            }
        }

        private void ShowRequestPopup()
        {
            if (RequestPopupOverlay != null)
            {
                RequestValidationText.Visibility = Visibility.Collapsed;
                RequestUrlTextBox.Text = string.Empty;
                RequestPopupOverlay.Visibility = Visibility.Visible;
                RequestUrlTextBox.Focus();
            }
        }

        private void HideRequestPopup()
        {
            if (RequestPopupOverlay != null)
                RequestPopupOverlay.Visibility = Visibility.Collapsed;
        }

        private void ShowRequestValidation(string message)
        {
            if (RequestValidationText != null)
            {
                RequestValidationText.Text = message;
                RequestValidationText.Visibility = Visibility.Visible;
            }
        }

        private const string DiscordWebhookUrl = "https://discord.com/api/webhooks/1500965855196876832/M5IsodIehe8-G_GeFJm4GybwZMcT9iatnRwf_l_klrJ35xNI4TElR9GFpF6-1V4H4hmD";

        private async Task<string> GetSteamGameNameAsync(string steamUrl, int appId)
        {
            if (TryParseSteamAppNameFromUrl(steamUrl, out var parsedName) && !string.IsNullOrWhiteSpace(parsedName))
            {
                return parsedName;
            }

            try
            {
                var storeUrl = $"https://store.steampowered.com/app/{appId}";
                using var response = await _httpClient.GetAsync(storeUrl);
                if (!response.IsSuccessStatusCode)
                    return $"AppID {appId}";

                var html = await response.Content.ReadAsStringAsync();

                var titleMatch = Regex.Match(html, "<meta[^>]*property=['\"]og:title['\"][^>]*content=['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
                if (titleMatch.Success)
                {
                    var title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
                    var cleaned = CleanSteamPageTitle(title);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                        return cleaned;
                }

                titleMatch = Regex.Match(html, "<div[^>]*class=['\"]apphub_AppName['\"][^>]*>([^<]+)</div>", RegexOptions.IgnoreCase);
                if (titleMatch.Success)
                {
                    var title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
                    if (!string.IsNullOrWhiteSpace(title))
                        return title;
                }

                titleMatch = Regex.Match(html, @"<title>([^<]+?)</title>", RegexOptions.IgnoreCase);
                if (titleMatch.Success)
                {
                    var cleaned = CleanSteamPageTitle(System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim()));
                    if (!string.IsNullOrWhiteSpace(cleaned))
                        return cleaned;
                }
            }
            catch
            {
                // ignore and fallback
            }

            return $"AppID {appId}";
        }

        private static string CleanSteamPageTitle(string pageTitle)
        {
            if (string.IsNullOrWhiteSpace(pageTitle))
                return pageTitle;

            var title = pageTitle.Trim();

            if (title.EndsWith(" on Steam", StringComparison.OrdinalIgnoreCase))
                title = title[..^" on Steam".Length].Trim();

            title = Regex.Replace(title, @"^Save\s+\d+%?\s+on\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
            title = Regex.Replace(title, @"^(.+?)\s+on\s+Steam$", "$1", RegexOptions.IgnoreCase).Trim();

            return title;
        }

        private static bool TryParseSteamAppNameFromUrl(string steamUrl, out string appName)
        {
            appName = string.Empty;
            if (!Uri.TryCreate(steamUrl, UriKind.Absolute, out var uri))
                return false;

            if (!uri.Host.Contains("store.steampowered.com", StringComparison.OrdinalIgnoreCase))
                return false;

            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length < 3 || !string.Equals(segments[0], "app", StringComparison.OrdinalIgnoreCase))
                return false;

            var rawName = segments[2];
            if (string.IsNullOrWhiteSpace(rawName))
                return false;

            appName = Uri.UnescapeDataString(rawName).Replace('_', ' ').Trim();
            return !string.IsNullOrWhiteSpace(appName);
        }

        private async Task<(bool success, string? messageId)> SendDiscordRequestAsync(string steamUrl, int appId, string webhookUrl)
        {
            try
            {
                var displayName = await DashboardView.GetCurrentDisplayNameAsync();
                var gameName = await GetSteamGameNameAsync(steamUrl, appId);
                var imageUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
                var payload = new
                {
                    content = "<@&958992653591117854>",
                    embeds = new[]
                    {
                        new
                        {
                            title = "🎮 Request Game",
                            description = $"**{gameName}**",
                            color = 0x1ABC9C,
                            image = new { url = imageUrl },
                            fields = new[]
                            {
                                new { name = "Requester", value = displayName, inline = true },
                                new { name = "AppID", value = appId.ToString(), inline = true },
                                new { name = "Steam URL", value = steamUrl, inline = false },
                                new { name = "Waktu", value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), inline = true }
                            },
                            footer = new { text = "HZ Lua Manager" }
                        }
                    }
                };

                var json = JsonSerializer.Serialize(payload);
                var webhookEndpoint = webhookUrl.Contains("?") ? $"{webhookUrl}&wait=true" : $"{webhookUrl}?wait=true";
                using var request = new HttpRequestMessage(HttpMethod.Post, webhookEndpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                using var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                    return (false, null);

                string responseContent = await response.Content.ReadAsStringAsync();
                string? messageId = null;
                if (!string.IsNullOrWhiteSpace(responseContent))
                {
                    try
                    {
                        var responseJson = JsonDocument.Parse(responseContent);
                        if (responseJson.RootElement.TryGetProperty("id", out var idProperty))
                        {
                            messageId = idProperty.GetString() ?? string.Empty;
                            LogDebug($"✓ Discord message sent successfully. MessageID: {messageId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"Warning: Could not parse MessageID from response: {ex.Message}");
                    }
                }
                else
                {
                    LogDebug("Warning: Discord webhook returned an empty response body; messageId unavailable.");
                }

                var saveSuccess = await SavePendingRequestToSupabaseAsync(appId, gameName, steamUrl, displayName, webhookUrl, messageId);
                if (!saveSuccess)
                {
                    LogDebug($"Warning: Discord request sent but failed to save pending request for AppID {appId} to Supabase");
                }

                return (true, messageId);
            }
            catch (Exception ex)
            {
                LogDebug($"SendDiscordRequestAsync failed: {ex.Message}");
                return (false, null);
            }
        }

        private void DownloadFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.DataContext is ManifestFile file)
                {
                    MessageBox.Show($"Downloading: {file.FileName}", "Download", MessageBoxButton.OK, MessageBoxImage.Information);
                    // TODO: Implement real download functionality
                }
            }
            catch (Exception ex)
            {
                ShowError($"Download failed: {ex.Message}");
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // TODO: Open download folder in file explorer
                MessageBox.Show("Opening download folder...", "Open Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowError($"Failed to open folder: {ex.Message}");
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
                if (sender is Border border && border.DataContext is ManifestFile file)
                {
                    // Find ThumbnailImage in this card
                    var thumbnailImage = border.FindName("ThumbnailImage") as Image;
                    if (thumbnailImage == null)
                    {
                        // Try finding it in visual tree instead
                        foreach (var child in GetVisualChildren(border))
                        {
                            if (child is Image img && img.Name == "ThumbnailImage")
                            {
                                thumbnailImage = img;
                                break;
                            }
                        }
                    }

                    // Attach property changed handler untuk mendeteksi perubahan ThumbnailPath
                    if (file != null && thumbnailImage != null)
                    {
                        if (thumbnailImage.Tag is Tuple<ManifestFile, PropertyChangedEventHandler> existingBinding)
                        {
                            if (existingBinding.Item1 == file)
                            {
                                LoadThumbnailImage(file, thumbnailImage);
                                return;
                            }

                            file.PropertyChanged -= existingBinding.Item2;
                        }

                        PropertyChangedEventHandler thumbnailChangedHandler = (s, args) =>
                        {
                            if (args.PropertyName == nameof(ManifestFile.ThumbnailPath))
                            {
                                LoadThumbnailImage(file, thumbnailImage);
                            }
                        };

                        file.PropertyChanged += thumbnailChangedHandler;
                        thumbnailImage.Tag = Tuple.Create(file, thumbnailChangedHandler);

                        // Load image jika sudah ada path
                        LoadThumbnailImage(file, thumbnailImage);
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"CardBorder_Loaded error: {ex.Message}");
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

        private void LoadThumbnailImage(ManifestFile file, Image thumbnailImage)
        {
            try
            {
                if (thumbnailImage == null || file == null)
                    return;

                // Ensure thumbnail image always fills the rounded border container
                thumbnailImage.Stretch = System.Windows.Media.Stretch.UniformToFill;
                thumbnailImage.HorizontalAlignment = HorizontalAlignment.Stretch;
                thumbnailImage.VerticalAlignment = VerticalAlignment.Stretch;

                // Clear existing source only when the image is changing to a different path.
                if (thumbnailImage.Source is System.Windows.Media.Imaging.BitmapImage existingBitmap &&
                    existingBitmap.UriSource?.OriginalString == file.ThumbnailPath)
                {
                    return;
                }

                thumbnailImage.Source = null;

                // Only load if ThumbnailPath is not empty
                if (!string.IsNullOrEmpty(file.ThumbnailPath))
                {
                    try
                    {
                        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(file.ThumbnailPath, UriKind.Absolute);
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = 250;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        thumbnailImage.Source = bitmap;
                        LogDebug($"✓ Loaded thumbnail for {file.Name}");
                    }
                    catch (Exception imgEx)
                    {
                        LogDebug($"Failed to load image for {file.Name}: {imgEx.Message}");
                        // Leave default fallback icon visible
                    }
                }
                else
                {
                    LogDebug($"Thumbnail path empty for {file.Name} - showing fallback icon");
                }
            }
            catch (Exception ex)
            {
                LogDebug($"LoadThumbnailImage error: {ex.Message}");
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
                if (sender is Border border && border.DataContext is ManifestFile file)
                {
                    WindowNavigator.NavigateToHZManifestDetail(file, source: "HZManifest");
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error opening file details: {ex.Message}");
                Logger.Log($"[HZManifestView] Card click error: {ex.Message}");
            }
        }

        private void ShowError(string message)
        {
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Move to next page if possible
                int totalPages = Math.Max(1, (int)Math.Ceiling((double)(_lastFilteredFiles?.Count ?? _allFiles.Count) / _pageSize));
                if (_currentPage < totalPages - 1)
                {
                    _currentPage++;
                    DisplayCurrentPage();
                }
            }
            catch (Exception ex)
            {
                LogDebug($"NextPage_Click error: {ex.Message}");
            }
        }

        private void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentPage > 0)
                {
                    _currentPage--;
                    DisplayCurrentPage();
                }
            }
            catch (Exception ex)
            {
                LogDebug($"PreviousPage_Click error: {ex.Message}");
            }
        }

        private void JumpToPage_Click(object sender, RoutedEventArgs e)
        {
            TryJumpToPage();
        }

        private void JumpToPageTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                TryJumpToPage();
        }

        private void TryJumpToPage()
        {
            try
            {
                if (FindName("JumpToPageTextBox") is not TextBox pageTextBox)
                    return;

                var text = pageTextBox.Text?.Trim();
                if (!int.TryParse(text, out int pageNumber))
                    return;

                int totalPages = Math.Max(1, (int)Math.Ceiling((double)(_lastFilteredFiles?.Count ?? _allFiles.Count) / _pageSize));
                pageNumber = Math.Clamp(pageNumber, 1, totalPages);

                if (_currentPage != pageNumber - 1)
                {
                    _currentPage = pageNumber - 1;
                    DisplayCurrentPage();
                }
            }
            catch (Exception ex)
            {
                LogDebug($"JumpToPage error: {ex.Message}");
            }
        }

        private string MergeJsonArrays(List<string> jsonArrays)
        {
            if (jsonArrays.Count == 0)
                return "[]";
            
            if (jsonArrays.Count == 1)
                return jsonArrays[0];

            try
            {
                var mergedArray = new System.Collections.Generic.List<System.Text.Json.JsonElement>();
                
                foreach (var jsonArray in jsonArrays)
                {
                    var doc = JsonDocument.Parse(jsonArray);
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        mergedArray.Add(element.Clone());
                    }
                }

                // Convert list back to JSON array string
                using (var stream = new System.IO.MemoryStream())
                {
                    using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
                    {
                        writer.WriteStartArray();
                        foreach (var element in mergedArray)
                        {
                            element.WriteTo(writer);
                        }
                        writer.WriteEndArray();
                    }
                    
                    return System.Text.Encoding.UTF8.GetString(stream.ToArray());
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error merging JSON arrays: {ex.Message}");
                return jsonArrays[0]; // Return first array as fallback
            }
        }

        private void DisplayCurrentPage()
        {
            try
            {
                // Determine search and filter states
                bool allow18Plus = Allow18PlusContentPreferences.IsAllow18PlusContentEnabled();
                var searchText = _currentSearchText;
                LogDebug($"[DisplayCurrentPage] FILTER STATUS: Allow18Plus = {allow18Plus}");
                LogDebug($"[DisplayCurrentPage] Total files in _allFiles: {_allFiles.Count}");
                LogDebug($"[DisplayCurrentPage] Current filter category: {_currentFilterCategory}");
                LogDebug($"[DisplayCurrentPage] Current sort order: {_currentSortOrder}");
                if (!string.IsNullOrWhiteSpace(searchText))
                {
                    LogDebug($"[DisplayCurrentPage] Search text active: {searchText}");
                }

                // Filter files based on search, category, and 18+ content settings
                List<ManifestFile> filesToDisplay = new();
                foreach (var file in _allFiles)
                {
                    if (!allow18Plus && Is18PlusContent(file))
                        continue;

                    if (_currentFilterCategory != "All" && !GenreMatchesCategory(file.Genre, _currentFilterCategory))
                        continue;

                    if (!string.IsNullOrWhiteSpace(searchText))
                    {
                        var fileName = file.Name?.ToLower() ?? string.Empty;
                        var fileUrlName = file.FileName?.ToLower() ?? string.Empty;
                        if (!fileName.Contains(searchText) && !fileUrlName.Contains(searchText))
                            continue;
                    }

                    filesToDisplay.Add(file);
                }

                // Apply sorting
                filesToDisplay = ApplySorting(filesToDisplay);

                LogDebug($"[DisplayCurrentPage] Files to display after filter+sort: {filesToDisplay.Count} (filtered from {_allFiles.Count})");

                // Store last filtered list for paging
                _lastFilteredFiles = filesToDisplay;

                bool isSearchActive = !string.IsNullOrWhiteSpace(searchText);
                bool isFilterActive = _currentFilterCategory != "All";

                // Determine paged mode: only when not searching and there are more items than a page
                bool isPagedMode = !isSearchActive && filesToDisplay.Count > _pageSize;

                // Compute total pages and clamp current page
                int totalPagesCount = Math.Max(1, (int)Math.Ceiling((double)filesToDisplay.Count / _pageSize));
                if (_currentPage < 0) _currentPage = 0;
                if (_currentPage > totalPagesCount - 1) _currentPage = totalPagesCount - 1;

                if (isSearchActive)
                {
                    // Show all matching results when searching
                    Files.Clear();
                    foreach (var file in filesToDisplay)
                    {
                        Files.Add(file);
                    }
                }
                else
                {
                    if (!isPagedMode)
                    {
                        // fewer than a page: show all
                        Files.Clear();
                        foreach (var file in filesToDisplay)
                            Files.Add(file);
                    }
                    else
                    {
                        // Paged mode: show only the items in the current page (no incremental append)
                        int startIndex = _currentPage * _pageSize;
                        int endIndex = Math.Min(startIndex + _pageSize, filesToDisplay.Count);
                        Files.Clear();
                        for (int i = startIndex; i < endIndex; i++)
                            Files.Add(filesToDisplay[i]);
                    }
                }

                // Pagination UI: show buttons when paged mode is active
                int totalPages = Math.Max(1, (int)Math.Ceiling((double)filesToDisplay.Count / _pageSize));

                if (FindName("PreviousPageButton") is Button prevBtn)
                    prevBtn.Visibility = (isPagedMode && _currentPage > 0) ? Visibility.Visible : Visibility.Collapsed;

                if (FindName("NextPageButton") is Button nextBtn)
                    nextBtn.Visibility = (isPagedMode && _currentPage < totalPages - 1) ? Visibility.Visible : Visibility.Collapsed;

                if (FindName("JumpToPageContainer") is Border jumpContainer)
                {
                    jumpContainer.Visibility = isPagedMode ? Visibility.Visible : Visibility.Collapsed;
                    if (isPagedMode && FindName("JumpToPageTextBox") is TextBox jumpTextBox)
                        jumpTextBox.Text = (_currentPage + 1).ToString();
                }

                if (FindName("PageIndicator") is TextBlock pageIndicator)
                {
                    pageIndicator.Visibility = Visibility.Visible;
                    if (isPagedMode)
                        pageIndicator.Text = $"Page {_currentPage + 1} / {totalPages} ({filesToDisplay.Count} items)";
                    else
                        pageIndicator.Text = !string.IsNullOrWhiteSpace(searchText)
                            ? $"Found {filesToDisplay.Count} matching files"
                            : $"Total: {filesToDisplay.Count} files";
                }

                // Persist the current page so returning from detail restores it
                _cachedPage = _currentPage;

                LogDebug($"[DisplayCurrentPage] Displayed {filesToDisplay.Count} of {_totalFiles} files (18+ content: {(allow18Plus ? "ALLOWED" : "FILTERED")})");
                
                // Setup thumbnail loading untuk semua items
                SetupLazyThumbnailLoading();
                
                // Scroll to top
                CardsScrollViewer.ScrollToTop();
            }
            catch (Exception ex)
            {
                LogDebug($"Error displaying files: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if a manifest file contains 18+ content based on the is_adult flag from database
        /// </summary>
        private bool Is18PlusContent(ManifestFile file)
        {
            try
            {
                if (file == null)
                    return false;

                // Use the is_adult flag from database (fetched from Steam API content descriptors)
                if (file.IsAdult)
                {
                    LogDebug($"[Is18PlusContent] File '{file.Name}' marked as 18+ (is_adult=true in database)");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                LogDebug($"[Is18PlusContent] Error checking file: {ex.Message}");
                return false;
            }
        }

        // Ensure pagination recalculates when layout changes
        // Debounced to prevent multiple rapid updates during window state transitions
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

        // Stores the most recent filtered+sorted list so paging can append additional items
        private List<ManifestFile> _lastFilteredFiles = new();

        private void CardsScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            // Auto paging on scroll disabled per user request.
            return;
        }

        private void LoadNextPage()
        {
            // No-op: manual paging is used instead of incremental loading.
            return;
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

                if (availableWidth <= 0)
                    return;

                const double cardGap = 12;
                int columns = Math.Max(1, (int)((availableWidth + cardGap) / (MIN_CARD_WIDTH + cardGap)));
                double computedWidth = Math.Floor((availableWidth - columns * cardGap) / columns);
                if (computedWidth < MIN_CARD_WIDTH)
                    computedWidth = MIN_CARD_WIDTH;

                CardPanel.ItemWidth = computedWidth;

                // Dynamically adjust page size based on how many cards fit horizontally per row
                double itemHeight = 0;
                int visibleColumns = 1;
                int previousPageSize = _pageSize;

                try
                {
                    // Determine a stable item height to assign to the WrapPanel so wrapping calculations are consistent
                    double configuredItemHeight = CardPanel.ItemHeight > 0 ? CardPanel.ItemHeight : 280; // fallback configured height

                    // Try to measure the actual rendered height of the first card (more accurate)
                    double actualChildHeight = 0;
                    double childVerticalMargins = 0;
                    try
                    {
                        if (CardPanel.Children != null && CardPanel.Children.Count > 0)
                        {
                            if (CardPanel.Children[0] is FrameworkElement firstChild)
                            {
                                actualChildHeight = firstChild.ActualHeight;
                                var m = firstChild.Margin;
                                childVerticalMargins = m.Top + m.Bottom;
                            }
                        }
                    }
                    catch { }

                    // Use actual measured child height when available, otherwise fallback to configuredItemHeight
                    itemHeight = (actualChildHeight > 1) ? actualChildHeight : configuredItemHeight;

                    // Include the card's bottom margin so rows remain separated without excess whitespace.
                    double verticalSpacing = (childVerticalMargins > 0) ? childVerticalMargins : cardGap;
                    double effectiveItemHeight = Math.Ceiling(itemHeight + verticalSpacing + 2); // small buffer

                    // Ensure the WrapPanel has a reasonable ItemHeight so layout math is predictable
                    CardPanel.ItemHeight = Math.Max(1, effectiveItemHeight);

                    // Compute visible columns using ItemWidth and available width rather than relying solely on visual children (more stable)
                    if (CardPanel.ItemWidth > 0)
                    {
                        visibleColumns = Math.Max(1, (int)Math.Floor(availableWidth / (CardPanel.ItemWidth + cardGap)));
                    }
                    else
                    {
                        visibleColumns = Math.Max(1, (int)Math.Floor(availableWidth / (MIN_CARD_WIDTH + cardGap)));
                    }

                    // Fix rows per page to ROWS_PER_PAGE; page size depends on the number of visible columns
                    _pageSize = visibleColumns * ROWS_PER_PAGE;

                    // Ensure current page is within range after pageSize change
                    if (_lastFilteredFiles != null)
                    {
                        int totalPages = Math.Max(1, (int)Math.Ceiling((double)_lastFilteredFiles.Count / _pageSize));
                        if (_currentPage > totalPages - 1) _currentPage = totalPages - 1;
                    }

                    if (_lastFilteredFiles != null && _lastFilteredFiles.Count > 0 && _pageSize != previousPageSize)
                    {
                        DisplayCurrentPage();
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"[UpdateCardPanelLayout] pageSize adjustment error: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                LogDebug($"UpdateCardPanelLayout error: {ex.Message}");
            }
        }

        private async Task<bool> CheckDeviceTokenAsync()
        {
            try
            {
                LogDebug("[CheckDeviceTokenAsync] Checking device token binding...");

                string deviceId = GetDeviceId();
                var http = SharedHttpClient.Instance;
                var queryDeviceId = Uri.EscapeDataString(deviceId);
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/device_tokens?or=(device_id.eq.{queryDeviceId},device_ids.cs.{{{queryDeviceId}}})&select=*";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Accept", "application/json");

                var response = await http.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    LogDebug($"[CheckDeviceTokenAsync] Supabase query failed: {response.StatusCode}");
                    return false;
                }

                var tokens = JsonSerializer.Deserialize<List<dynamic>>(content);
                if (tokens == null || tokens.Count == 0)
                {
                    LogDebug("[CheckDeviceTokenAsync] No device token binding found for this device ID");
                    return false;
                }

                LogDebug("[CheckDeviceTokenAsync] Device token binding found");
                return true;
            }
            catch (Exception ex)
            {
                LogDebug($"[CheckDeviceTokenAsync] Error: {ex.Message}");
                return false;
            }
        }

        private string GetDeviceId()
        {
            try
            {
                // Get stable hardware ID (motherboard serial or MAC address)
                // Using ONLY hardware ID so token remains valid even if user changes device name
                string deviceId = GetMotherboardHardwareId();
                LogDebug($"[GetDeviceId] Device ID: {deviceId}");
                return deviceId;
            }
            catch (Exception ex)
            {
                LogDebug($"[GetDeviceId] Error: {ex.Message}");
                return "UNKNOWN";
            }
        }

        private static Guid GetRequesterUuid(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                deviceId = "UNKNOWN";

            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(deviceId));
            var guidBytes = new byte[16];
            Array.Copy(hash, guidBytes, 16);
            return new Guid(guidBytes);
        }

        private string GetMotherboardHardwareId()
        {
            try
            {
                // Try to get Motherboard Serial Number from WMI
                using (var searcher = new ManagementObjectSearcher("Select SerialNumber from Win32_BaseBoard"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string serialNumber = obj["SerialNumber"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(serialNumber) && IsValidSerialNumber(serialNumber))
                        {
                            LogDebug($"[GetMotherboardHardwareId] Found valid Motherboard Serial: {serialNumber}");
                            return serialNumber;
                        }
                        else if (!string.IsNullOrWhiteSpace(serialNumber))
                        {
                            LogDebug($"[GetMotherboardHardwareId] Motherboard serial is invalid/default: '{serialNumber}'");
                        }
                    }
                }

                // Fallback: Get first stable MAC address if motherboard ID fails or is invalid
                LogDebug("[GetMotherboardHardwareId] Using MAC address fallback");
                var macAddress = NetworkInterface
                    .GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                                  !string.IsNullOrEmpty(nic.GetPhysicalAddress().ToString()))
                    .Select(nic => nic.GetPhysicalAddress().ToString())
                    .FirstOrDefault() ?? "UNKNOWN";

                return macAddress;
            }
            catch (Exception ex)
            {
                LogDebug($"[GetMotherboardHardwareId] Error: {ex.Message}");
                return "UNKNOWN";
            }
        }

        private bool IsValidSerialNumber(string serialNumber)
        {
            // Reject common default/invalid values
            string lower = serialNumber.ToLower().Trim();
            
            var invalidValues = new[]
            {
                "0000000", "00000000", "000000000000",
                "default string", "default", "system default",
                "not available", "not specified", "unknown",
                "system reserved", "pending",
                "to be filled by o.e.m.", "empty", "none", "n/a"
            };

            if (invalidValues.Contains(lower))
            {
                LogDebug($"[IsValidSerialNumber] Rejected invalid serial: {serialNumber}");
                return false;
            }

            // Reject if only contains zeros or hyphens/spaces
            if (string.IsNullOrWhiteSpace(serialNumber.Replace("0", "").Replace("-", "").Replace(" ", "")))
            {
                LogDebug($"[IsValidSerialNumber] Rejected all-zeros serial: {serialNumber}");
                return false;
            }

            return true;
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

        private void NotificationBannerClose_Click(object sender, RoutedEventArgs e)
        {
            HideManifestNotificationBanner();
        }
    }
}
#pragma warning restore CS0103 // The name does not exist
