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
        public string Category { get; set; } = "OTHERS";
        
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
        private string _selectedCategory = "ALL";

        private static readonly HashSet<int> KnownRockstarAppIds = new()
        {
            271590,  // Grand Theft Auto V
            1174180, // Red Dead Redemption 2
            1404210, // Red Dead Redemption
            12170,   // Grand Theft Auto: San Andreas
            12180,   // Grand Theft Auto: Vice City
            12190,   // Grand Theft Auto III
            1546970, // Grand Theft Auto: The Trilogy
            1546990, // Grand Theft Auto: San Andreas - The Definitive Edition
            1547000, // Grand Theft Auto: Vice City - The Definitive Edition
            1546980, // Grand Theft Auto III - The Definitive Edition
            12200,   // Bully: Scholarship Edition
            204100,  // Max Payne 3
            12140,   // Max Payne 2
            12150,   // Max Payne
            110800,  // L.A. Noire
            266850   // Grand Theft Auto IV
        };

        private static readonly HashSet<int> KnownEaAppIds = new()
        {
            1222680, // Need for Speed Heat
            1693980, // Dead Space
            1845910, // Dragon Age: The Veilguard
            3654560, // Plants vs. Zombies: Replanted
            1238810, // Battlefield V
            1238840, // Battlefield 1
            1238860, // Battlefield 4
            1517290, // Battlefield 2042
            1794680, // EA SPORTS FC 24
            2195250, // EA SPORTS FC 25
            1172380, // STAR WARS Jedi: Fallen Order
            1774580, // STAR WARS Jedi: Survivor
            1849250, // WILD HEARTS
            1426210, // It Takes Two
            1225570, // Unravel Two
            1262560, // Need for Speed Unbound
            1262540, // Need for Speed Payback
            1222670, // The Sims 4
            1237950, // STAR WARS: Squadrons
            1237970, // Titanfall 2
            1172470, // Apex Legends
            1328670, // Mass Effect Legendary Edition
            24720,   // Command & Conquer 3
            17460,   // Mass Effect (2007)
            24980,   // Mass Effect 2
            997010,  // EA SPORTS WRC
            1846380, // F1 23
            2488620  // F1 24
        };

        private static readonly HashSet<int> KnownUbisoftAppIds = new()
        {
            3159330, // Assassin's Creed Shadows
            3193030, // Assassin's Creed Shadows (alt)
            208480,  // Assassin's Creed® III
            2063840, // Assassin's Creed® III (alt)
            3751950, // Assassin's Creed Black Flag Resynced
            2208920, // Assassin's Creed Valhalla
            289650,  // Assassin's Creed® Unity
            582160,  // Assassin's Creed Origins
            812140,  // Assassin's Creed Odyssey
            33230,   // Assassin's Creed 2
            3035570, // Assassin's Creed Mirage
            15100,   // Assassin's Creed Director's Cut
            2840770, // Avatar: Frontiers of Pandora
            2842040, // Star Wars Outlaws
            2751000, // Prince of Persia The Lost Crown
            646910,  // The Crew 2
            1934680, // The Crew 2 (alt)
            241560,  // The Crew
            2668510, // The Crew Motorfest
            552520,  // Far Cry 5
            962130,  // Far Cry New Dawn
            2225070, // Far Cry 6
            2420110, // Watch Dogs: Legion
            447040,  // Watch_Dogs 2
            243470,  // Watch_Dogs
            359550,  // Tom Clancy's Rainbow Six Siege
            2231380, // Tom Clancy's Ghost Recon Breakpoint
            460930,  // Tom Clancy's Ghost Recon Wildlands
            2229850, // Tom Clancy's The Division 2
            365590,  // Tom Clancy's The Division
            2225080, // Riders Republic
            544920,  // For Honor
            2420510, // Skull and Bones
            982700,  // The Settlers
            916440,  // Anno 1800
            219990   // Rayman Legends
        };

        private static readonly HashSet<int> KnownDenuvoAppIds = new()
        {
            1113000, // Persona 4 Golden
            1237320, // Sonic Frontiers
            1382330, // Persona 5 Strikers
            1659040, // HITMAN World of Assassination
            1687950, // Persona 5 Royal
            1941540, // Mafia: The Old Country
            1971870, // Mortal Kombat 1
            2161700, // Persona 3 Reload
            2215200, // LEGO Batman: Legacy of the Dark Knight
            2358720, // Black Myth: Wukong
            2373990, // Solo Leveling: ARISE OVERDRIVE
            2680010, // The First Berserker: Khazan
            287700,  // METAL GEAR SOLID V: THE PHANTOM PAIN
            3357650, // PRAGMATA
            3764200, // Resident Evil Requiem
            637650,  // FINAL FANTASY XV WINDOWS EDITION
            779340,  // Total War: THREE KINGDOMS
            990080,  // Hogwarts Legacy
            1875830, // Shin Megami Tensei V: Vengeance
            2620600, // Metaphor: ReFantazio
            2022370, // Sonic Superstars
            1142710, // Total War: WARHAMMER III
            594570,  // Total War: WARHAMMER II
            2072450, // Like a Dragon: Infinite Wealth
            2375550, // Like a Dragon Gaiden
            2058190, // Lost Judgment
            2058180, // Judgment
            1364780, // Street Fighter 6
            2050650, // Resident Evil 4 Remake
            1196590, // Resident Evil Village
            883710,  // Resident Evil 2 Remake
            2186320, // Dead Island 2
            668580,  // Atomic Heart
            1026300, // Sniper Elite 5
            1030830, // Mafia: Definitive Edition
            1490890, // Demon Slayer: Kimetsu no Yaiba
            1599340, // Lies of P
            2239550, // Monster Hunter Wilds
            2246340, // Company of Heroes 3
            1675200, // Monster Hunter Rise
            1824220, // Gotham Knights
            1962700, // Football Manager 2024
            1778820  // TEKKEN 8
        };
        
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
            ApplyFilters();
        }

        private void CategoryFilter_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is RadioButton rb && rb.Tag is string tag)
                {
                    _selectedCategory = tag.ToUpperInvariant();
                    LogDebug($"Category selected: {_selectedCategory}");
                    ApplyFilters();
                }
            }
            catch (Exception ex)
            {
                LogDebug($"CategoryFilter_Checked error: {ex.Message}");
            }
        }

        private bool IsEaGame(BypassFile file)
        {
            if (file.AppId.HasValue && KnownEaAppIds.Contains(file.AppId.Value))
                return true;

            string name = file.Name ?? "";
            string fileName = file.FileName ?? "";

            string[] eaKeywords = new[]
            {
                "need for speed", "dead space", "dragon age", "plants vs", "pvz",
                "battlefield", "ea sports", "fifa", "mass effect", "the sims",
                "sims 4", "star wars jedi", "jedi: fallen", "jedi: survivor",
                "titanfall", "apex legends", "wild hearts", "it takes two",
                "unravel", "electronic arts", "burnout", "crysis"
            };

            foreach (var kw in eaKeywords)
            {
                if (name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fileName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsUbisoftGame(BypassFile file)
        {
            if (file.AppId.HasValue && KnownUbisoftAppIds.Contains(file.AppId.Value))
                return true;

            string name = file.Name ?? "";
            string fileName = file.FileName ?? "";

            string[] ubiKeywords = new[]
            {
                "assassin", "creed", "avatar", "star wars outlaws", "outlaws",
                "prince of persia", "the crew", "far cry", "watch dogs", "watch_dogs",
                "tom clancy", "ghost recon", "rainbow six", "the division", "division 2",
                "splinter cell", "rayman", "immortals fenyx", "skull and bones",
                "riders republic", "for honor", "ubisoft", "anno 1800"
            };

            foreach (var kw in ubiKeywords)
            {
                if (name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fileName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsDenuvoGame(BypassFile file)
        {
            if (file.AppId.HasValue && KnownDenuvoAppIds.Contains(file.AppId.Value))
                return true;

            string name = file.Name ?? "";
            string desc = file.Description ?? "";
            string genre = file.Genre ?? "";

            if (desc.IndexOf("denuvo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                genre.IndexOf("denuvo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("denuvo", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            string[] denuvoKeywords = new[]
            {
                "black myth", "wukong", "persona", "shin megami tensei", "metaphor",
                "sonic frontiers", "sonic superstars", "mortal kombat", "hogwarts legacy",
                "three kingdoms", "warhammer iii", "infinite wealth", "like a dragon",
                "lost judgment", "judgment", "street fighter", "dead island", "atomic heart",
                "sniper elite", "final fantasy xv", "mafia", "hitman", "metal gear",
                "phantom pain", "khazan", "berserker", "requiem", "pragmata",
                "solo leveling", "batman"
            };

            foreach (var kw in denuvoKeywords)
            {
                if (name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private bool IsRockstarGame(BypassFile file)
        {
            if (file.AppId.HasValue && KnownRockstarAppIds.Contains(file.AppId.Value))
                return true;

            string name = file.Name ?? "";
            string fileName = file.FileName ?? "";

            string[] rockstarKeywords = new[]
            {
                "rockstar", "grand theft auto", "gta ", "gta:", "gta-", "gta v", "gta iv", "gta iii",
                "red dead", "max payne", "l.a. noire", "la noire", "bully"
            };

            foreach (var kw in rockstarKeywords)
            {
                if (name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fileName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        public string GetGameCategory(BypassFile file)
        {
            if (!string.IsNullOrWhiteSpace(file.Category))
            {
                var upper = file.Category.Trim().ToUpperInvariant();
                if (upper == "DENUVO" || upper == "UBISOFT" || upper == "EA" || upper == "ROCKSTAR" || upper == "OTHERS")
                {
                    return upper;
                }
            }

            // Fallback hierarchy if category not explicitly populated from R2 folder
            if (IsRockstarGame(file))
                return "ROCKSTAR";

            if (IsEaGame(file))
                return "EA";

            if (IsUbisoftGame(file))
                return "UBISOFT";

            if (IsDenuvoGame(file))
                return "DENUVO";

            return "OTHERS";
        }

        private void ApplyFilters()
        {
            try
            {
                string searchText = SearchBox?.Text?.Trim().ToLowerInvariant() ?? "";
                
                var query = _allFiles.AsEnumerable();

                // 1. Filter by Category
                if (!string.Equals(_selectedCategory, "ALL", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(f => string.Equals(GetGameCategory(f), _selectedCategory, StringComparison.OrdinalIgnoreCase));
                }

                // 2. Filter by Search Query
                if (!string.IsNullOrWhiteSpace(searchText))
                {
                    query = query.Where(f => 
                        (f.Name?.ToLowerInvariant().Contains(searchText) ?? false) ||
                        (f.FileName?.ToLowerInvariant().Contains(searchText) ?? false) ||
                        (f.AppId.HasValue && f.AppId.Value.ToString().Contains(searchText))
                    );
                }

                var filteredFiles = query.OrderBy(f => f.Name ?? "").ToList();

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
                    if (string.IsNullOrWhiteSpace(searchText) && string.Equals(_selectedCategory, "ALL", StringComparison.OrdinalIgnoreCase))
                    {
                        pageIndicator.Text = $"Total: {_allFiles.Count} files";
                    }
                    else if (string.IsNullOrWhiteSpace(searchText))
                    {
                        pageIndicator.Text = $"Total: {filteredFiles.Count} files ({_selectedCategory})";
                    }
                    else
                    {
                        pageIndicator.Text = $"Found {filteredFiles.Count} matching files ({_selectedCategory})";
                    }
                }

                SetupLazyThumbnailLoading();
                RestoreScrollPosition();
            }
            catch (Exception ex)
            {
                LogDebug($"Error in ApplyFilters: {ex.Message}");
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
                    if (string.IsNullOrWhiteSpace(r2file.Key) || r2file.Key.EndsWith("/") || !r2file.Key.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string r2FileName = System.IO.Path.GetFileName(r2file.Key);
                    int? appId = SteamPluginManager.SteamManifestHelper.ExtractAppIdFromFilename(r2FileName);

                    // Extract category directly from R2 subfolder structure (e.g., gamebypass/UBISOFT/3159330.zip -> "UBISOFT")
                    string category = "OTHERS";
                    var pathParts = r2file.Key.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                    if (pathParts.Length >= 3 && string.Equals(pathParts[0], "gamebypass", StringComparison.OrdinalIgnoreCase))
                    {
                        category = pathParts[1].Trim().ToUpperInvariant();
                    }
                    else if (pathParts.Length == 2 && !string.Equals(pathParts[0], "gamebypass", StringComparison.OrdinalIgnoreCase))
                    {
                        category = pathParts[0].Trim().ToUpperInvariant();
                    }

                    // Cari metadata: by app_id (jika ada) atau file_name
                    var meta = supabaseRecords.FirstOrDefault(m => 
                        (appId != null && m.AppId == appId) || 
                        string.Equals(m.FileName, r2FileName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(m.FileName, r2file.Key, StringComparison.OrdinalIgnoreCase));

                    string metaLog;
                    if (meta != null)
                    {
                        if (appId != null && meta.AppId == appId)
                            metaLog = $"[MATCH] Metadata ditemukan untuk '{r2file.Key}' berdasarkan app_id: {appId} (meta.Name: '{meta.Name}')";
                        else if (string.Equals(meta.FileName, r2FileName, StringComparison.OrdinalIgnoreCase) || string.Equals(meta.FileName, r2file.Key, StringComparison.OrdinalIgnoreCase))
                            metaLog = $"[MATCH] Metadata ditemukan untuk '{r2file.Key}' berdasarkan file_name: '{meta.FileName}' (meta.Name: '{meta.Name}')";
                        else
                            metaLog = $"[MATCH] Metadata ditemukan untuk '{r2file.Key}' (meta.Name: '{meta.Name}')";
                    }
                    else
                    {
                        metaLog = $"[NO MATCH] Metadata TIDAK ditemukan untuk '{r2file.Key}' (app_id: {appId})";
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
                        Genre = meta?.Genre ?? string.Empty,
                        Category = category
                    };

                    if (appId.HasValue && appId.Value > 0)
                    {
                        string cacheFileName = $"{appId.Value}_header.jpg";
                        string cachedPath = Path.Combine(_thumbnailCacheFolder, cacheFileName);
                        if (File.Exists(cachedPath))
                        {
                            bypassFile.ThumbnailPath = new Uri(cachedPath, UriKind.Absolute).ToString();
                        }
                    }

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
                                    LogDebug($"✓ Downloaded thumbnail from store asset for {file.Name} (AppID: {file.AppId})");
                                    return true;
                                }
                            }
                        }
                    }
                }
                catch (Exception scrapeEx)
                {
                    LogDebug($"Store asset scrape failed for AppID {file.AppId}: {scrapeEx.Message}");
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
                _cachedFiles = null;
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
                        if (thumbnailImage.Tag is Tuple<BypassFile, PropertyChangedEventHandler> existingBinding)
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
                            if (args.PropertyName == nameof(BypassFile.ThumbnailPath))
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
            ModernMessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            ApplyFilters();
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
                        Duration = new System.Windows.Duration(System.TimeSpan.FromSeconds(1)),
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
