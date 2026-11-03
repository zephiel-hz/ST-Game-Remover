using System;

using System.Net.Http;

using System.Reflection;

using System.Text;

using System.Text.Json;

using System.Text.Json.Serialization;

using System.Text.RegularExpressions;

using System.Diagnostics;

using System.Windows;

using System.Windows.Controls;

using System.Windows.Controls.Primitives;

using System.Windows.Data;

using System.Windows.Input;

using System.Windows.Media;

using System.Windows.Media.Effects;

using System.Windows.Media.Imaging;

using System.Windows.Threading;

using Microsoft.Win32;

using System.IO;

using System.IO.Compression;

using System.Security.Cryptography;

using System.Threading.Tasks;

using System.Management;

using System.Linq;

using System.Net.NetworkInformation;

using SteamPluginManager.Models;

using SteamPluginManager.Services.Security;




namespace SteamPluginManager.Views

{

    public partial class DashboardView : UserControl

    {



        private async void OnlineFix_Click(object sender, RoutedEventArgs e)

        {

            try

            {

                bool verified = await CheckDeviceTokenAsync();

                if (!verified)

                {

                    WindowNavigator.NextViewAfterVerify = "OnlineFix";

                    WindowNavigator.NavigateToVerifyToken();

                    return;

                }

                WindowNavigator.NavigateToOnlineFix();

            }

            catch { WindowNavigator.NavigateToOnlineFix(); }

        }

        private bool _isInitialized = false;

        private DispatcherTimer? _dashboardResizeDebounceTimer;

        private UserProfile? _currentUserProfile = null;

        private List<ManifestFile> _mostDownloadedManifests = new();

        private int _currentSlideIndex = 0;

        private DispatcherTimer? _slideAutoAdvanceTimer;

        private bool _isSliderHovered = false;

        private bool _isImageAActive = true;

        private List<ManifestFile> _newestManifests = new();

        private readonly List<Border> _cachedNewestManifestCards = new();

        private List<BypassFile> _newestBypasses = new();

        private List<OnlineFixFile> _newestOnlineFixes = new();

        private readonly List<Border> _cachedBypassCards = new();

        private readonly List<Border> _cachedOnlineFixCards = new();

        private Task? _bypassLoadingTask;

        private Task? _onlineFixLoadingTask;

        private Task<List<SteamPluginManager.SteamDataUpdater.Record>>? _supabaseRecordsTask;



        private Task<List<SteamPluginManager.SteamDataUpdater.Record>> GetSupabaseRecordsAsync()

        {

            return _supabaseRecordsTask ??= SteamPluginManager.SteamDataUpdater.GetAllRecordsAsync();

        }

        private string _activeRecentTab = "Manifest";

        private Panel? NewestManifestCardsPanel => RecentCardsPanel;

        private TextBlock? NewestManifestPlaceholder => RecentPlaceholder;

        private int _newestManifestColumns = 3;

        private readonly string _thumbnailCacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamPluginManager", "ThumbnailCache");

        private static DashboardView? _currentInstance;

        private static DashboardView? _cachedInstance;

        private static bool _manifestCacheDirty = false;

        public static void InvalidateCache()
        {
            _manifestCacheDirty = true;
            if (_cachedInstance != null)
            {
                _cachedInstance._lastManifestFetchTimeUtc = null;
                _cachedInstance._newestManifests.Clear();
                _cachedInstance._cachedNewestManifestCards.Clear();
                _cachedInstance._newestBypasses.Clear();
                _cachedInstance._newestOnlineFixes.Clear();
                _cachedInstance._cachedBypassCards.Clear();
                _cachedInstance._cachedOnlineFixCards.Clear();
            }
        }

        // Negative thumbnail cache for manifest cards (PERF-6.3C)

        private static readonly object _negativeThumbnailCacheLock = new();

        private static readonly Dictionary<int, DateTime> _negativeThumbnailCache = new();

        private const int MaxNegativeCacheEntries = 1000;

        private static readonly TimeSpan NegativeThumbnailCacheDuration = TimeSpan.FromHours(12);



        // Manifest data freshness & lifecycle safety (PERF-6.4B)

        private static readonly TimeSpan ManifestDataFreshnessDuration = TimeSpan.FromMinutes(5);

        private DateTime? _lastManifestFetchTimeUtc = null;

        private bool _lastLoadedAllow18PlusContent = false;

        private long _navigationGeneration = 0;



        public DashboardView()

        {

            _cachedInstance = this;

            InitializeComponent();

            InitializeDashboardResizeDebounceTimer();

            Loaded += DashboardView_Loaded;

            Unloaded += DashboardView_Unloaded;

            SizeChanged += DashboardView_SizeChanged;

        }



        private void DashboardView_Loaded(object sender, RoutedEventArgs e)

        {

            _currentInstance = this;

            _cachedInstance = this;

            long generation = unchecked(++_navigationGeneration);



            if (!_isInitialized)

            {

                RegisterTextElements();

                Directory.CreateDirectory(_thumbnailCacheFolder);

                _isInitialized = true;

                _lastLoadedAllow18PlusContent = Allow18PlusContentPreferences.IsAllow18PlusContentEnabled();

                _ = LoadUserProfileAsync();

                _ = CheckForUpdatesOnStartupAsync();

            }



            // If manifest cache was invalidated or is stale, reload fresh data from Supabase

            if (_manifestCacheDirty || !IsManifestDataFresh())

            {

                _manifestCacheDirty = false;

                _lastManifestFetchTimeUtc = null;

                _ = RefreshDashboardDataAsync(generation, force: true);

            }

            else

            {

                var sw = System.Diagnostics.Stopwatch.StartNew();

                ReactivateExistingData();

                sw.Stop();

                Logger.Log($"[DashboardView] Dashboard reactivated from memory in {sw.ElapsedMilliseconds} ms (0 Supabase queries, {_cachedNewestManifestCards.Count} cards preserved)");



                if (_newestBypasses.Count == 0 && (_bypassLoadingTask == null || _bypassLoadingTask.IsCompleted))

                {

                    _bypassLoadingTask = LoadRecentBypassesAsync(generation);

                }

                if (_newestOnlineFixes.Count == 0 && (_onlineFixLoadingTask == null || _onlineFixLoadingTask.IsCompleted))

                {

                    _onlineFixLoadingTask = LoadRecentOnlineFixesAsync(generation);

                }

            }



            App.LanguageChanged -= LanguageChanged_Handler;

            App.LanguageChanged += LanguageChanged_Handler;



            Allow18PlusContentPreferences.Allow18PlusContentChanged -= Allow18PlusContentPreferences_Changed;

            Allow18PlusContentPreferences.Allow18PlusContentChanged += Allow18PlusContentPreferences_Changed;

        }



        private bool IsManifestDataFresh()

        {

            if (!_lastManifestFetchTimeUtc.HasValue)

                return false;



            if (_mostDownloadedManifests.Count == 0 || _newestManifests.Count == 0)

                return false;



            bool current18Plus = Allow18PlusContentPreferences.IsAllow18PlusContentEnabled();

            if (_lastLoadedAllow18PlusContent != current18Plus)

                return false;



            return (DateTime.UtcNow - _lastManifestFetchTimeUtc.Value) < ManifestDataFreshnessDuration;

        }



        private void ReactivateExistingData()

        {

            try

            {

                // 1. Featured Slider reactivation

                if (_mostDownloadedManifests.Count > 0)

                {

                    if (SliderLoadingOverlay != null)

                        SliderLoadingOverlay.Visibility = Visibility.Collapsed;



                    FeaturedSliderCard.Visibility = Visibility.Visible;

                    PopulateSliderDots();



                    if (_currentSlideIndex < 0 || _currentSlideIndex >= _mostDownloadedManifests.Count)

                        _currentSlideIndex = 0;



                    ShowSlide(_currentSlideIndex, animate: false);

                    StartSlideAutoAdvanceTimer();

                }



                // 2. Newest Manifest Cards reactivation

                double availableWidth = GetAvailableManifestPanelWidth();

                if (availableWidth > 0)

                {

                    int cols = CalculateNewestManifestColumns(availableWidth);

                    if (cols != _newestManifestColumns || _cachedNewestManifestCards.Count == 0)

                    {

                        _newestManifestColumns = cols;

                        CreateNewestManifestCards();

                    }

                }

                UpdateNewestManifestPlaceholder();

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] ReactivateExistingData error: {ex.Message}");

            }

        }



        private async Task RefreshDashboardDataAsync(long generation, bool force)

        {

            try

            {

                if (force)

                {

                    _lastManifestFetchTimeUtc = null;

                    _newestManifests.Clear();

                    _cachedNewestManifestCards.Clear();

                    _supabaseRecordsTask = null;

                    _newestBypasses.Clear();

                    _newestOnlineFixes.Clear();

                    _cachedBypassCards.Clear();

                    _cachedOnlineFixCards.Clear();

                }



                if (!force && IsManifestDataFresh())

                {

                    ReactivateExistingData();



                    if (_newestBypasses.Count == 0 && (_bypassLoadingTask == null || _bypassLoadingTask.IsCompleted))

                    {

                        _bypassLoadingTask = LoadRecentBypassesAsync(generation);

                    }

                    if (_newestOnlineFixes.Count == 0 && (_onlineFixLoadingTask == null || _onlineFixLoadingTask.IsCompleted))

                    {

                        _onlineFixLoadingTask = LoadRecentOnlineFixesAsync(generation);

                    }

                    return;

                }



                var sw = System.Diagnostics.Stopwatch.StartNew();

                var sliderTask = LoadFeaturedSliderAsync(generation);

                var cardsTask = LoadNewestManifestCardsAsync(generation);

                _bypassLoadingTask = LoadRecentBypassesAsync(generation);

                _onlineFixLoadingTask = LoadRecentOnlineFixesAsync(generation);



                var activeRecentTask = _activeRecentTab switch
                {
                    "Bypass" => _bypassLoadingTask,
                    "OnlineFix" => _onlineFixLoadingTask,
                    _ => cardsTask
                };

                await Task.WhenAll(sliderTask, activeRecentTask);

                sw.Stop();



                if (generation == _navigationGeneration && _mostDownloadedManifests.Count > 0 && _newestManifests.Count > 0)

                {

                    _lastManifestFetchTimeUtc = DateTime.UtcNow;

                    _lastLoadedAllow18PlusContent = Allow18PlusContentPreferences.IsAllow18PlusContentEnabled();

                    Logger.Log($"[DashboardView] Dashboard data refresh completed in {sw.ElapsedMilliseconds} ms (slider, manifest, bypass, onlinefix; gen {generation})");

                }

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] RefreshDashboardDataAsync failed: {ex.Message}");

            }

        }



        /// <summary>

        /// Refreshes the newest manifest cards and stats on the active DashboardView instance.

        /// Can be called from any other view (e.g. HZManifestView) when a new manifest is added.

        /// </summary>

        public static async Task RefreshNewestManifestsAsync()

        {

            InvalidateCache();

            if (_currentInstance != null && _currentInstance.IsLoaded)

            {

                await _currentInstance.Dispatcher.InvokeAsync(async () =>

                {

                    try

                    {

                        _manifestCacheDirty = false;

                        long generation = unchecked(++_currentInstance._navigationGeneration);

                        await _currentInstance.RefreshDashboardDataAsync(generation, force: true);

                    }

                    catch (Exception ex)

                    {

                        Logger.Log($"[DashboardView] RefreshNewestManifestsAsync error: {ex.Message}");

                    }

                });

            }

        }



        private void InitializeDashboardResizeDebounceTimer()

        {

            _dashboardResizeDebounceTimer = new DispatcherTimer

            {

                Interval = TimeSpan.FromMilliseconds(180)

            };

            _dashboardResizeDebounceTimer.Tick += DashboardResizeDebounceTimer_Tick;

        }



        private void DashboardView_SizeChanged(object sender, SizeChangedEventArgs e)

        {

            if (NewestManifestCardsPanel == null)

                return;



            if (_dashboardResizeDebounceTimer == null)

            {

                InitializeDashboardResizeDebounceTimer();

            }



            _dashboardResizeDebounceTimer?.Stop();

            _dashboardResizeDebounceTimer?.Start();

        }



        private void DashboardResizeDebounceTimer_Tick(object? sender, EventArgs e)

        {

            try

            {

                _dashboardResizeDebounceTimer?.Stop();

                if (NewestManifestCardsPanel == null)

                    return;



                double width = GetAvailableManifestPanelWidth();

                int columns = CalculateNewestManifestColumns(width);

                if (columns != _newestManifestColumns)

                {

                    _newestManifestColumns = columns;

                    CreateNewestManifestCards();

                }

            }

            catch

            {

                // Ignore resize timing issues.

            }

        }



        private double GetAvailableManifestPanelWidth()

        {

            if (NewestManifestCardsPanel != null && NewestManifestCardsPanel.ActualWidth > 0)

                return NewestManifestCardsPanel.ActualWidth;



            if (ActualWidth > 0)

                return Math.Min(1240, Math.Max(0, ActualWidth - 92));



            var window = Window.GetWindow(this);

            if (window != null && window.ActualWidth > 0)

            {

                double effective = window.ActualWidth - 280; // approximate sidebar and outer margins

                if (effective > 0)

                    return Math.Min(1240, effective);

            }



            return 0;

        }



        private int CalculateNewestManifestColumns(double width)

        {

            if (width <= 0)

                return _newestManifestColumns > 0 ? _newestManifestColumns : 3;



            const double minCardWidth = 240;

            const double cardMargin = 20; // right + bottom spacing accounted separately

            const double panelMargin = 48; // some padding inside panel



            double availableWidth = Math.Max(0, width - panelMargin);

            int maxColumns = Math.Max(1, (int)Math.Floor(availableWidth / (minCardWidth + cardMargin)));

            return Math.Min(maxColumns, 6);

        }



        private async Task CheckForUpdatesOnStartupAsync()

        {

            try

            {

                string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamPluginManager");

                string autoUpdateFile = Path.Combine(appDataPath, "autoupdate.txt");



                bool autoUpdateEnabled = true;

                if (File.Exists(autoUpdateFile))

                {

                    string content = File.ReadAllText(autoUpdateFile).Trim().ToLower();

                    autoUpdateEnabled = (content == "true" || content == "1");

                }



                if (!autoUpdateEnabled) return;



                await Task.Delay(500);

                await DoCheckForUpdatesAndNotifyAsync(isAutoUpdate: true);

            }

            catch { }

        }



        private async Task DoCheckForUpdatesAndNotifyAsync(bool isAutoUpdate = false)

        {

            try

            {

                var updateInfo = await UpdateChecker.CheckForUpdatesAsync();

                if (updateInfo.ErrorMessage != null)

                {

                    Logger.Log($"[DashboardView] Update check error: {updateInfo.ErrorMessage}");

                    // Hanya tampilkan error popup untuk manual check, tidak untuk auto update

                    if (!isAutoUpdate)

                    {

                        ModernMessageBox.Show($"Failed to check for updates: {updateInfo.ErrorMessage}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);

                    }

                    return;

                }



                if (updateInfo.HasUpdate)

                {

                    bool shouldUpdate = UpdateAvailableDialog.ShowUpdate(Window.GetWindow(this), updateInfo);

                    if (shouldUpdate && !string.IsNullOrEmpty(updateInfo.DownloadUrl))

                        await DownloadAndInstallUpdateAsync(updateInfo.DownloadUrl);

                }

                else if (!isAutoUpdate)

                {

                    // Hanya tampilkan "Up to Date" untuk manual check, tidak untuk auto update

                    var dialog = new StyledMessageDialog("Up to Date", "You are already running the latest version.");

                    dialog.Owner = Window.GetWindow(this);

                    dialog.ShowDialog();

                }

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] Exception during update check: {ex.Message}");

                // Hanya tampilkan error popup untuk manual check, tidak untuk auto update

                if (!isAutoUpdate)

                {

                    ModernMessageBox.Show($"Failed to check for updates: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);

                }

            }

        }



        private void DashboardView_Unloaded(object sender, RoutedEventArgs e)

        {

            App.LanguageChanged -= LanguageChanged_Handler;

            Allow18PlusContentPreferences.Allow18PlusContentChanged -= Allow18PlusContentPreferences_Changed;



            if (ReferenceEquals(_currentInstance, this))

            {

                _currentInstance = null;

            }



            if (_dashboardResizeDebounceTimer != null)

            {

                _dashboardResizeDebounceTimer.Stop();

                _dashboardResizeDebounceTimer = null;

            }



            if (_slideAutoAdvanceTimer != null)

            {

                _slideAutoAdvanceTimer.Stop();

                _slideAutoAdvanceTimer = null;

            }



            // BUG-1.1: Detach active opacity animation clocks from carousel images on unload

            SlideImageA?.BeginAnimation(UIElement.OpacityProperty, null);

            SlideImageB?.BeginAnimation(UIElement.OpacityProperty, null);



            // PERF-6.4B: Preserve _cachedNewestManifestCards and NewestManifestCardsPanel.Children

            // so returning to the Dashboard tab reactivates immediately without card recreation.

        }



        private void LanguageChanged_Handler(object? sender, EventArgs e) => RefreshLanguageUI();



        public static async Task OpenProfileEditorAsync(bool isMandatorySetup = false)

        {

            try

            {

                var profile = await LoadUserProfileForEditingAsync();

                if (profile == null)

                {

                    return;

                }



                bool isMandatory = isMandatorySetup || string.IsNullOrWhiteSpace(profile.DisplayName);

                var mainShell = WindowNavigator.GetMainShell();

                if (mainShell != null)

                {

                    mainShell.GlobalProfileModal.ShowModal(profile, onSaved: () =>

                    {

                        if (_currentInstance != null && profile != null)

                        {

                            _currentInstance._currentUserProfile = profile;

                            _currentInstance.UpdateProfileUI();

                        }

                    }, isMandatorySetup: isMandatory);

                }

                else

                {

                    var ownerWindow = GetProfileEditorOwnerWindow();

                    await ShowProfileEditorDialogInternalAsync(profile, ownerWindow);

                }

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] OpenProfileEditorAsync failed: {ex.Message}");

            }

        }



        private static Window? GetProfileEditorOwnerWindow()

        {

            try

            {

                var activeWindow = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

                if (activeWindow != null)

                {

                    return activeWindow;

                }



                return WindowNavigator.GetMainShell();

            }

            catch

            {

                return WindowNavigator.GetMainShell();

            }

        }



        public static async Task<UserProfile?> LoadUserProfileForEditingAsync()

        {

            try

            {

                string deviceId = DeviceIdentifier.GetDeviceId();

                var profile = await QueryUserProfileByDeviceIdAsync(deviceId);

                return profile ?? new UserProfile

                {

                    DeviceId = deviceId,

                    DisplayName = string.Empty,

                    Bio = string.Empty

                };

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] LoadUserProfileForEditingAsync failed: {ex.Message}");

                return null;

            }

        }



        private static void UpdateSidebarProfile(string displayName, string status)

        {

            try

            {

                var shell = WindowNavigator.GetMainShell();

                shell?.GlobalSidebar?.SetProfile(displayName, status ?? string.Empty);

            }

            catch { }

        }



        private void RegisterTextElements()

        {

            try

            {

                Logger.Log("[DashboardView] Starting RegisterTextElements");

                // This dashboard uses the new stats cards instead of the legacy welcome message.

                RefreshLanguageUI();

            }

            catch (Exception ex) { Logger.Log($"Error registering text elements: {ex.Message}"); }

        }



        public void RefreshLanguageUI()

        {

            try { Logger.Log("[DashboardView] Refreshing language UI"); LanguageHelper.UpdateAllRegisteredTextBlocks(); Logger.Log("[DashboardView] Language UI refresh completed"); }

            catch (Exception ex) { Logger.Log($"Error refreshing language UI: {ex.Message}"); }

        }





        private void SetProfileStatus(string status)

        {

            // Forward profile status to global sidebar if available

            try

            {

                var win = Window.GetWindow(this) as MainShell;

                win?.GlobalSidebar?.SetProfile(_currentUserProfile?.DisplayName ?? "", status ?? string.Empty);

            }

            catch { }

        }



        private void UpdateProfileUI()

        {

            // Forward profile display name/status to global sidebar and top header

            try

            {

                var win = Window.GetWindow(this) as MainShell ?? WindowNavigator.GetMainShell();

                string name = _currentUserProfile?.DisplayName ?? string.Empty;



                System.Windows.Media.ImageSource? avatar = _currentUserProfile?.AvatarImage;

                if (avatar == null && _currentUserProfile?.Id.HasValue == true)

                {

                    avatar = Services.Profile.ProfilePictureCacheService.GetCachedAvatarInMemory(_currentUserProfile.Id.Value);

                }



                win?.GlobalSidebar?.SetProfile(

                    string.IsNullOrWhiteSpace(name) ? "Set display name" : name,

                    string.IsNullOrWhiteSpace(name) ? "Profile loaded, but display name is empty." : string.Empty,

                    avatar);



                if (!string.IsNullOrWhiteSpace(name))

                {

                    win?.SetHeaderProfile(name, avatar);

                }

            }

            catch { }

        }



        private async Task LoadFeaturedSliderAsync(long? generation = null)

        {

            long gen = generation ?? _navigationGeneration;

            try

            {

                Logger.Log($"[DashboardView] LoadFeaturedSliderAsync started (gen {gen})");

                if (SliderLoadingOverlay != null && _mostDownloadedManifests.Count == 0)

                {

                    SliderLoadingOverlay.Visibility = Visibility.Visible;

                }



                var manifests = await SupabaseConfig.GetMostDownloadedManifestFilesAsync(10);



                // Lifecycle & generation guard: discard if view unloaded or superseded

                if (!IsLoaded || gen != _navigationGeneration)

                {

                    Logger.Log($"[DashboardView] Discarding LoadFeaturedSliderAsync for obsolete gen {gen} (current {_navigationGeneration}, IsLoaded={IsLoaded})");

                    return;

                }



                var filtered = FilterNewestManifests(manifests);

                _mostDownloadedManifests = filtered.Take(8).ToList();

                Logger.Log($"[DashboardView] LoadFeaturedSliderAsync loaded {_mostDownloadedManifests.Count} manifests");



                if (SliderLoadingOverlay != null)

                {

                    SliderLoadingOverlay.Visibility = Visibility.Collapsed;

                }



                if (_mostDownloadedManifests.Count == 0)

                {

                    FeaturedSliderCard.Visibility = Visibility.Collapsed;

                    return;

                }



                FeaturedSliderCard.Visibility = Visibility.Visible;

                PopulateSliderDots();



                // Preserve active slide index if valid

                if (_currentSlideIndex < 0 || _currentSlideIndex >= _mostDownloadedManifests.Count)

                {

                    _currentSlideIndex = 0;

                }

                ShowSlide(_currentSlideIndex, animate: false);



                StartSlideAutoAdvanceTimer();



                // Prefetch artworks in background for all carousel games (same cache as GameLibraryDetailView)

                _ = Task.Run(async () =>

                {

                    try

                    {

                        foreach (var m in _mostDownloadedManifests)

                        {

                            if (!IsLoaded || gen != _navigationGeneration)

                                break;



                            if (!m.AppId.HasValue || m.AppId.Value <= 0) continue;

                            int aid = m.AppId.Value;



                            try

                            {

                                string? downloadedPath = await FetchAndCacheHeroArtworkAsync(aid, m.Name);

                                if (downloadedPath != null)

                                {

                                    if (!IsLoaded || gen != _navigationGeneration)

                                        break;



                                    if (_mostDownloadedManifests != null && _currentSlideIndex < _mostDownloadedManifests.Count && _mostDownloadedManifests[_currentSlideIndex].AppId == aid)

                                    {

                                        var bmp = await Task.Run(() => DecodeSlideArtwork(downloadedPath));

                                        if (bmp != null)

                                        {

                                            await Dispatcher.InvokeAsync(() =>

                                            {

                                                if (IsLoaded && gen == _navigationGeneration &&

                                                    _mostDownloadedManifests != null && _currentSlideIndex < _mostDownloadedManifests.Count && _mostDownloadedManifests[_currentSlideIndex].AppId == aid)

                                                {

                                                    SetSlideArtwork(bmp, animate: true);

                                                }

                                            });

                                        }

                                    }

                                }

                            }

                            catch (Exception ex)

                            {

                                Logger.Log($"[DashboardView] Prefetch error for {aid}: {ex.Message}");

                            }

                        }

                    }

                    catch { }

                });

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] LoadFeaturedSliderAsync failed: {ex.Message}");

                if (SliderLoadingOverlay != null)

                    SliderLoadingOverlay.Visibility = Visibility.Collapsed;

            }

        }



        private void PopulateSliderDots()

        {

            if (SliderDotsPanel == null) return;

            SliderDotsPanel.Children.Clear();



            for (int i = 0; i < _mostDownloadedManifests.Count; i++)

            {

                int index = i;

                var dotBorder = new Border

                {

                    Height = 8,

                    Width = (i == _currentSlideIndex) ? 26 : 8,

                    CornerRadius = new CornerRadius(4),

                    Margin = new Thickness(4, 0, 4, 0),

                    Cursor = Cursors.Hand,

                    Background = (i == _currentSlideIndex)

                        ? (Application.Current.Resources["AccentBrush"] as Brush ?? new SolidColorBrush(Color.FromRgb(59, 130, 246)))

                        : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),

                    SnapsToDevicePixels = true

                };



                dotBorder.MouseLeftButtonUp += (s, e) =>

                {

                    e.Handled = true;

                    if (_currentSlideIndex != index)

                    {

                        _currentSlideIndex = index;

                        ShowSlide(_currentSlideIndex, animate: true);

                        RestartSlideAutoAdvanceTimer();

                    }

                };



                SliderDotsPanel.Children.Add(dotBorder);

            }

        }



        private void UpdateSliderDots(int activeIndex)

        {

            if (SliderDotsPanel == null) return;

            for (int i = 0; i < SliderDotsPanel.Children.Count; i++)

            {

                if (SliderDotsPanel.Children[i] is Border dot)

                {

                    bool isActive = (i == activeIndex);

                    dot.Width = isActive ? 26 : 8;

                    dot.Background = isActive

                        ? (Application.Current.Resources["AccentBrush"] as Brush ?? new SolidColorBrush(Color.FromRgb(59, 130, 246)))

                        : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));

                }

            }

        }



        private void ShowSlide(int index, bool animate)

        {

            if (_mostDownloadedManifests == null || _mostDownloadedManifests.Count == 0)

                return;



            if (index < 0 || index >= _mostDownloadedManifests.Count)

                return;



            var manifest = _mostDownloadedManifests[index];



            // 1. Update text metadata

            SlideGameTitle.Text = manifest.Name ?? "Unknown Game";

            SlideGameDescription.Text = !string.IsNullOrWhiteSpace(manifest.Description)

                ? manifest.Description

                : "Explore and download the latest manifest files.";



            SlideDownloadsText.Text = $"{manifest.DownloadCountDisplay} Downloads";

            SlideAppIdText.Text = manifest.AppId.HasValue ? $"AppID: {manifest.AppId.Value}" : "";

            SlideAppIdText.Visibility = manifest.AppId.HasValue ? Visibility.Visible : Visibility.Collapsed;



            UpdateSliderDots(index);



            // 2. Load artwork using the EXACT same logic as GameLibraryDetailView

            LoadSlideArtwork(manifest, index, animate);

        }



        private void LoadSlideArtwork(ManifestFile manifest, int slideIndex, bool animate)

        {

            _ = LoadSlideArtworkAsync(manifest, slideIndex, animate);

        }



        private async Task LoadSlideArtworkAsync(ManifestFile manifest, int slideIndex, bool animate)

        {

            try

            {

                if (!manifest.AppId.HasValue || manifest.AppId.Value <= 0)

                {

                    ClearSlideArtwork(animate);

                    return;

                }



                int appId = manifest.AppId.Value;

                string imagesDir = Path.Combine(CacheManager.CacheDirectory, "Images");

                Directory.CreateDirectory(imagesDir);



                string heroLocalPath = Path.Combine(imagesDir, $"{appId}_hero.jpg");

                string headerLocalPath = Path.Combine(imagesDir, $"{appId}.jpg");



                // 1. INSTANT LOCAL CACHE DISPLAY (0ms, no network delay, prevents black screen)

                string? displayedLocalFile = null;

                if (File.Exists(heroLocalPath) && new FileInfo(heroLocalPath).Length > 0)

                {

                    displayedLocalFile = heroLocalPath;

                }

                else if (File.Exists(headerLocalPath) && new FileInfo(headerLocalPath).Length > 0)

                {

                    displayedLocalFile = headerLocalPath;

                }



                if (displayedLocalFile != null)

                {

                    var bmp = await Task.Run(() => DecodeSlideArtwork(displayedLocalFile));

                    if (bmp != null)

                    {

                        await Dispatcher.InvokeAsync(() =>

                        {

                            if (IsLoaded && _currentSlideIndex == slideIndex)

                            {

                                SetSlideArtwork(bmp, animate);

                            }

                        });

                    }



                    // If high-resolution hero art is already on disk, we are 100% done!

                    if (displayedLocalFile == heroLocalPath)

                    {

                        Logger.Log($"[DashboardView] Using cached hero artwork for '{manifest.Name}' ({appId})");

                        return;

                    }

                }

                else

                {

                    ClearSlideArtwork(animate);

                }



                // 2. ASYNC BACKGROUND FETCH & LOCAL DISK PERSISTENCE

                string? downloadedPath = await FetchAndCacheHeroArtworkAsync(appId, manifest.Name);

                if (downloadedPath != null && downloadedPath != displayedLocalFile)

                {

                    var bmp = await Task.Run(() => DecodeSlideArtwork(downloadedPath));

                    if (bmp != null)

                    {

                        await Dispatcher.InvokeAsync(() =>

                        {

                            if (IsLoaded && _currentSlideIndex == slideIndex)

                            {

                                SetSlideArtwork(bmp, animate: true);

                            }

                        });

                    }

                }

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] LoadSlideArtwork error: {ex.Message}");

            }

        }



        private async Task<string?> FetchAndCacheHeroArtworkAsync(int appId, string? title = null)

        {

            try

            {

                string imagesDir = Path.Combine(CacheManager.CacheDirectory, "Images");

                Directory.CreateDirectory(imagesDir);



                string heroLocalPath = Path.Combine(imagesDir, $"{appId}_hero.jpg");

                string headerLocalPath = Path.Combine(imagesDir, $"{appId}.jpg");



                if (File.Exists(heroLocalPath) && new FileInfo(heroLocalPath).Length > 0)

                    return heroLocalPath;

                if (File.Exists(headerLocalPath) && new FileInfo(headerLocalPath).Length > 0)

                    return headerLocalPath;



                bool success = await SharedHttpClient.RunThrottledThumbnailFetchAsync($"hero_{appId}", async () =>

                {

                    if (File.Exists(heroLocalPath) && new FileInfo(heroLocalPath).Length > 0)

                        return true;



                    // A. Try fetching library_hero.jpg

                    string heroCdnUrl = $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/library_hero.jpg";

                    try

                    {

                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                        using var response = await SharedHttpClient.Instance.GetAsync(heroCdnUrl, cts.Token);

                        if (response.IsSuccessStatusCode)

                        {

                            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);

                            if (bytes != null && bytes.Length > 0)

                            {

                                await File.WriteAllBytesAsync(heroLocalPath, bytes);

                                Logger.Log($"[DashboardView] Downloaded & cached hero artwork for '{title ?? appId.ToString()}' ({appId})");

                                return true;

                            }

                        }

                    }

                    catch

                    {

                        // Fallback to header.jpg

                    }



                    // B. Fallback to header.jpg if hero missing

                    if (!File.Exists(headerLocalPath))

                    {

                        string fallbackUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";

                        try

                        {

                            using var fbCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                            using var fbResp = await SharedHttpClient.Instance.GetAsync(fallbackUrl, fbCts.Token);

                            if (fbResp.IsSuccessStatusCode)

                            {

                                var bytes = await fbResp.Content.ReadAsByteArrayAsync(fbCts.Token);

                                if (bytes != null && bytes.Length > 0)

                                {

                                    await File.WriteAllBytesAsync(headerLocalPath, bytes);

                                    Logger.Log($"[DashboardView] Downloaded & cached fallback header for '{title ?? appId.ToString()}' ({appId})");

                                    return true;

                                }

                            }

                        }

                        catch

                        {

                            // Ignore fallback failure

                        }

                    }



                    return File.Exists(headerLocalPath) && new FileInfo(headerLocalPath).Length > 0;

                });



                if (File.Exists(heroLocalPath) && new FileInfo(heroLocalPath).Length > 0)

                    return heroLocalPath;

                if (File.Exists(headerLocalPath) && new FileInfo(headerLocalPath).Length > 0)

                    return headerLocalPath;



                return null;

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] FetchAndCacheHeroArtworkAsync error for {appId}: {ex.Message}");

                return null;

            }

        }



        private static BitmapSource? DecodeSlideArtwork(string filePath)

        {

            try

            {

                if (!File.Exists(filePath)) return null;



                var bmp = new BitmapImage();

                bmp.BeginInit();

                bmp.CacheOption = BitmapCacheOption.OnLoad;

                bmp.UriSource = new Uri(filePath, UriKind.Absolute);

                bmp.DecodePixelWidth = 1280;

                bmp.EndInit();

                bmp.Freeze();

                return bmp;

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] DecodeSlideArtwork failed for '{filePath}': {ex.Message}");

                return null;

            }

        }



        private void SetSlideArtwork(BitmapSource bmp, bool animate)

        {

            try

            {

                if (bmp == null) return;



                if (!animate)

                {

                    // BUG-1.1: Release any active/held animation clocks so local Opacity assignments take effect

                    SlideImageA.BeginAnimation(UIElement.OpacityProperty, null);

                    SlideImageB.BeginAnimation(UIElement.OpacityProperty, null);

                    SlideImageA.Source = bmp;

                    SlideImageA.Opacity = 1.0;

                    SlideImageB.Source = null;

                    SlideImageB.Opacity = 0.0;

                    _isImageAActive = true;

                    return;

                }



                // Cross-fade animation

                var targetImage = _isImageAActive ? SlideImageB : SlideImageA;

                var currentImage = _isImageAActive ? SlideImageA : SlideImageB;



                targetImage.Source = bmp;



                var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(300));

                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(300));



                targetImage.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                currentImage.BeginAnimation(UIElement.OpacityProperty, fadeOut);



                _isImageAActive = !_isImageAActive;

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] SetSlideArtwork error: {ex.Message}");

            }

        }



        private void ClearSlideArtwork(bool animate)

        {

            try

            {

                if (!animate)

                {

                    // BUG-1.1: Release animation clocks before clearing artwork and setting opacity to 0

                    SlideImageA.BeginAnimation(UIElement.OpacityProperty, null);

                    SlideImageB.BeginAnimation(UIElement.OpacityProperty, null);

                    SlideImageA.Source = null;

                    SlideImageB.Source = null;

                    SlideImageA.Opacity = 0.0;

                    SlideImageB.Opacity = 0.0;

                    return;

                }



                var activeImage = _isImageAActive ? SlideImageA : SlideImageB;

                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(activeImage.Opacity, 0.0, TimeSpan.FromMilliseconds(200));

                activeImage.BeginAnimation(UIElement.OpacityProperty, fadeOut);

            }

            catch { }

        }



        private void SliderPrevButton_Click(object sender, RoutedEventArgs e)

        {

            e.Handled = true;

            if (_mostDownloadedManifests.Count <= 1) return;

            _currentSlideIndex = (_currentSlideIndex - 1 + _mostDownloadedManifests.Count) % _mostDownloadedManifests.Count;

            ShowSlide(_currentSlideIndex, animate: true);

            RestartSlideAutoAdvanceTimer();

        }



        private void SliderNextButton_Click(object sender, RoutedEventArgs e)

        {

            e.Handled = true;

            if (_mostDownloadedManifests.Count <= 1) return;

            _currentSlideIndex = (_currentSlideIndex + 1) % _mostDownloadedManifests.Count;

            ShowSlide(_currentSlideIndex, animate: true);

            RestartSlideAutoAdvanceTimer();

        }



        private void FeaturedSliderCard_Click(object sender, MouseButtonEventArgs e)

        {

            NavigateToCurrentSlideManifest();

        }



        private void SlideActionButton_Click(object sender, RoutedEventArgs e)

        {

            e.Handled = true;

            NavigateToCurrentSlideManifest();

        }



        private void NavigateToCurrentSlideManifest()

        {

            if (_mostDownloadedManifests != null && _currentSlideIndex >= 0 && _currentSlideIndex < _mostDownloadedManifests.Count)

            {

                var manifest = _mostDownloadedManifests[_currentSlideIndex];

                WindowNavigator.NavigateToHZManifestDetail(manifest, source: "Dashboard");

            }

        }



        private void FeaturedSlider_MouseEnter(object sender, MouseEventArgs e)

        {

            _isSliderHovered = true;

            _slideAutoAdvanceTimer?.Stop();

            SetSliderNavButtonsVisible(true);

        }



        private void FeaturedSlider_MouseLeave(object sender, MouseEventArgs e)

        {

            _isSliderHovered = false;

            RestartSlideAutoAdvanceTimer();

            SetSliderNavButtonsVisible(false);

        }



        private void SetSliderNavButtonsVisible(bool visible)

        {

            if (SliderPrevButton == null || SliderNextButton == null) return;



            bool shouldShow = visible && _mostDownloadedManifests != null && _mostDownloadedManifests.Count > 1;



            SliderPrevButton.IsHitTestVisible = shouldShow;

            SliderNextButton.IsHitTestVisible = shouldShow;



            double targetOpacity = shouldShow ? 1.0 : 0.0;

            var anim = new System.Windows.Media.Animation.DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(200));



            SliderPrevButton.BeginAnimation(UIElement.OpacityProperty, anim);

            SliderNextButton.BeginAnimation(UIElement.OpacityProperty, anim);

        }



        private void StartSlideAutoAdvanceTimer()

        {

            if (_slideAutoAdvanceTimer == null)

            {

                _slideAutoAdvanceTimer = new DispatcherTimer

                {

                    Interval = TimeSpan.FromSeconds(5)

                };

                _slideAutoAdvanceTimer.Tick += (s, e) =>

                {

                    if (_isSliderHovered || _mostDownloadedManifests.Count <= 1)

                        return;



                    _currentSlideIndex = (_currentSlideIndex + 1) % _mostDownloadedManifests.Count;

                    ShowSlide(_currentSlideIndex, animate: true);

                };

            }

            _slideAutoAdvanceTimer.Stop();

            _slideAutoAdvanceTimer.Start();

        }



        private void RestartSlideAutoAdvanceTimer()

        {

            if (!_isSliderHovered && _slideAutoAdvanceTimer != null)

            {

                _slideAutoAdvanceTimer.Stop();

                _slideAutoAdvanceTimer.Start();

            }

        }



        private void DashboardScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)

        {

            if (e.VerticalChange != 0)

            {

                // If hero slider is scrolled completely out of view (offset > 350), pause the timer

                if (DashboardScrollViewer.VerticalOffset > 350)

                {

                    _slideAutoAdvanceTimer?.Stop();

                }

                else

                {

                    // Delay auto-advance while user is actively scrolling

                    RestartSlideAutoAdvanceTimer();

                }

            }

        }



        private async Task LoadUserProfileAsync()

        {

            try

            {

                SetProfileStatus("Loading profile...");

                string deviceId = DeviceIdentifier.GetDeviceId();

                _currentUserProfile = await QueryUserProfileByDeviceIdAsync(deviceId);



                if (_currentUserProfile == null)

                {

                    _currentUserProfile = new UserProfile

                    {

                        DeviceId = deviceId,

                        DisplayName = string.Empty,

                        Bio = string.Empty

                    };

                    SetProfileStatus("No profile found. Create one now.");

                }

                else if (string.IsNullOrWhiteSpace(_currentUserProfile.DisplayName))

                {

                    SetProfileStatus("Profile loaded, but display name is empty.");

                }

                else

                {

                    SetProfileStatus(string.Empty);

                }



                UpdateProfileUI();



                // Force user to set display name on startup if it has never been set

                if (string.IsNullOrWhiteSpace(_currentUserProfile.DisplayName))

                {

                    var mainShell = WindowNavigator.GetMainShell();

                    if (mainShell != null)

                    {

                        _ = Dispatcher.InvokeAsync(async () =>

                        {

                            await Task.Delay(400);

                            mainShell.GlobalProfileModal.ShowModal(_currentUserProfile, onSaved: () =>

                            {

                                if (_currentInstance != null && _currentUserProfile != null)

                                {

                                    _currentInstance.UpdateProfileUI();

                                }

                            }, isMandatorySetup: true);

                        });

                    }

                }

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] LoadUserProfileAsync failed: {ex.Message}");

                SetProfileStatus("Unable to load profile.");

            }

        }



        private async Task<List<ManifestFile>> FetchNewestManifestsAsync(int count)

        {

            try

            {

                Logger.Log($"[DashboardView] Starting FetchNewestManifestsAsync with count={count}");

                var manifests = await SupabaseConfig.GetNewestManifestFilesAsync(count);

                Logger.Log($"[DashboardView] FetchNewestManifestsAsync returned {manifests?.Count ?? 0} manifests");

                return manifests ?? new List<ManifestFile>();

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] FetchNewestManifestsAsync failed: {ex.Message}\n{ex.StackTrace}");

                return new List<ManifestFile>();

            }

        }



        private static bool IsNamePlaceholder(string? name, int? appId)

        {

            if (string.IsNullOrWhiteSpace(name))

                return true;



            string trimmed = name.Trim();

            if (long.TryParse(trimmed, out _))

                return true;



            if (trimmed.StartsWith("App ", StringComparison.OrdinalIgnoreCase) ||

                trimmed.StartsWith("Game ", StringComparison.OrdinalIgnoreCase))

            {

                int spaceIdx = trimmed.IndexOf(' ');

                if (spaceIdx >= 0)

                {

                    string rest = trimmed.Substring(spaceIdx + 1).Trim();

                    if (long.TryParse(rest, out _))

                        return true;

                }

            }



            if (appId.HasValue && string.Equals(trimmed, appId.Value.ToString(), StringComparison.OrdinalIgnoreCase))

                return true;



            if (string.Equals(trimmed, "Unknown bypass", StringComparison.OrdinalIgnoreCase) ||

                string.Equals(trimmed, "Unknown fix", StringComparison.OrdinalIgnoreCase) ||

                string.Equals(trimmed, "Unknown game", StringComparison.OrdinalIgnoreCase) ||

                string.Equals(trimmed, "Unknown Game Title", StringComparison.OrdinalIgnoreCase))

            {

                return true;

            }



            return false;

        }



        private static int? ExtractAppId(string fileName)

        {

            int? id = SteamPluginManager.SteamManifestHelper.ExtractAppIdFromFilename(fileName);

            if (id.HasValue && id.Value > 0)

                return id;



            string baseName = Path.GetFileNameWithoutExtension(fileName);

            var match = System.Text.RegularExpressions.Regex.Match(baseName, @"\b(\d{4,8})\b");

            if (match.Success && int.TryParse(match.Groups[1].Value, out int regexId) && regexId > 0)

            {

                return regexId;

            }

            return null;

        }



        private async Task<List<BypassFile>> FetchNewestBypassFilesAsync(int count)

        {

            try

            {

                var r2Files = await SteamPluginManager.R2Config.ListFilesAsync("gamebypass/");

                if (r2Files == null || r2Files.Count == 0)

                    return new List<BypassFile>();



                var newestR2 = r2Files

                    .Where(f => !string.IsNullOrWhiteSpace(f.Key) && !f.Key.EndsWith("/") && f.Key.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))

                    .OrderByDescending(f => f.LastModified ?? DateTime.MinValue)

                    .Take(count)

                    .ToList();



                var supabaseRecords = await GetSupabaseRecordsAsync();

                string thumbnailFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamPluginManager", "ThumbnailCache");



                var list = new List<BypassFile>();

                foreach (var r2file in newestR2)

                {

                    string r2FileName = System.IO.Path.GetFileName(r2file.Key);

                    int? appId = ExtractAppId(r2FileName);



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



                    var meta = supabaseRecords.FirstOrDefault(m =>

                        (appId != null && m.AppId == appId) ||

                        string.Equals(m.FileName, r2FileName, StringComparison.OrdinalIgnoreCase) ||

                        string.Equals(m.FileName, r2file.Key, StringComparison.OrdinalIgnoreCase));



                    if (meta != null && meta.AppId > 0)

                        appId = meta.AppId;



                    string displayName = meta?.Name ?? string.Empty;

                    string genre = meta?.Genre ?? string.Empty;

                    string description = meta?.Description ?? string.Empty;



                    if (IsNamePlaceholder(displayName, appId) && appId.HasValue && appId.Value > 0)

                    {

                        string? cached = CacheManager.GetGameName(appId.Value);

                        if (!IsNamePlaceholder(cached, appId))

                        {

                            displayName = cached!;

                        }

                        else

                        {

                            try

                            {

                                var steamInfo = await SteamPluginManager.SteamManifestHelper.GetGameInfoFromSteamAsync(appId.Value);

                                if (steamInfo != null && !IsNamePlaceholder(steamInfo.Name, appId))

                                {

                                    displayName = steamInfo.Name!.Trim();

                                    if (string.IsNullOrWhiteSpace(genre) && !string.IsNullOrWhiteSpace(steamInfo.Genre))

                                    {

                                        genre = steamInfo.Genre;

                                    }

                                    if (string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(steamInfo.Description))

                                    {

                                        description = steamInfo.Description;

                                    }

                                    CacheManager.SaveGameData(appId.Value, displayName, genre);

                                }

                            }

                            catch { }

                        }

                    }



                    if (string.IsNullOrWhiteSpace(displayName))

                    {

                        displayName = System.IO.Path.GetFileNameWithoutExtension(r2FileName);

                    }



                    var bypassFile = new BypassFile

                    {

                        FileName = r2file.Key,

                        FileUrl = r2file.Url,

                        Name = displayName,

                        AppId = appId,

                        Id = meta?.Id,

                        Description = description,

                        Genre = genre,

                        Category = category,

                        CreatedAt = r2file.LastModified

                    };



                    if (appId.HasValue && appId.Value > 0)

                    {

                        string cacheFileName = $"{appId.Value}_header.jpg";

                        string cachedPath = Path.Combine(thumbnailFolder, cacheFileName);

                        if (File.Exists(cachedPath))

                        {

                            bypassFile.ThumbnailPath = new Uri(cachedPath, UriKind.Absolute).ToString();

                        }

                    }



                    list.Add(bypassFile);

                }

                return list;

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] FetchNewestBypassFilesAsync failed: {ex.Message}");

                return new List<BypassFile>();

            }

        }



        private async Task<List<OnlineFixFile>> FetchNewestOnlineFixFilesAsync(int count)

        {

            try

            {

                var b2Files = await SteamPluginManager.B2Config.ListFilesAsync("onlinefix/");

                if (b2Files == null || b2Files.Count == 0)

                    return new List<OnlineFixFile>();



                var newestB2 = b2Files

                    .Where(f => !string.IsNullOrWhiteSpace(f.Key) && !f.Key.EndsWith("/"))

                    .OrderByDescending(f => f.LastModified ?? DateTime.MinValue)

                    .Take(count)

                    .ToList();



                var supabaseRecords = await GetSupabaseRecordsAsync();

                string thumbnailFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamPluginManager", "ThumbnailCache");



                var list = new List<OnlineFixFile>();

                foreach (var b2file in newestB2)

                {

                    string b2FileName = System.IO.Path.GetFileName(b2file.Key);

                    int? appId = ExtractAppId(b2FileName);



                    var meta = supabaseRecords.FirstOrDefault(m =>

                        (appId != null && m.AppId == appId) ||

                        string.Equals(m.FileName, b2FileName, StringComparison.OrdinalIgnoreCase));



                    if (meta != null && meta.AppId > 0)

                        appId = meta.AppId;



                    string displayName = meta?.Name ?? string.Empty;

                    string genre = meta?.Genre ?? string.Empty;

                    string description = meta?.Description ?? string.Empty;



                    if (IsNamePlaceholder(displayName, appId) && appId.HasValue && appId.Value > 0)

                    {

                        string? cached = CacheManager.GetGameName(appId.Value);

                        if (!IsNamePlaceholder(cached, appId))

                        {

                            displayName = cached!;

                        }

                        else

                        {

                            try

                            {

                                var steamInfo = await SteamPluginManager.SteamManifestHelper.GetGameInfoFromSteamAsync(appId.Value);

                                if (steamInfo != null && !IsNamePlaceholder(steamInfo.Name, appId))

                                {

                                    displayName = steamInfo.Name!.Trim();

                                    if (string.IsNullOrWhiteSpace(genre) && !string.IsNullOrWhiteSpace(steamInfo.Genre))

                                    {

                                        genre = steamInfo.Genre;

                                    }

                                    if (string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(steamInfo.Description))

                                    {

                                        description = steamInfo.Description;

                                    }

                                    CacheManager.SaveGameData(appId.Value, displayName, genre);

                                }

                            }

                            catch { }

                        }

                    }



                    if (string.IsNullOrWhiteSpace(displayName))

                    {

                        displayName = System.IO.Path.GetFileNameWithoutExtension(b2FileName);

                    }



                    var fixFile = new OnlineFixFile

                    {

                        FileName = b2file.Key,

                        FileUrl = b2file.Url,

                        Name = displayName,

                        AppId = appId,

                        Id = meta?.Id,

                        Description = description,

                        Genre = genre,

                        FileSize = b2file.Size >= 0 ? FormatFileSize(b2file.Size) : string.Empty,

                        CreatedAt = b2file.LastModified

                    };



                    if (appId.HasValue && appId.Value > 0)

                    {

                        string cacheFileName = $"{appId.Value}_header.jpg";

                        string cachedPath = Path.Combine(thumbnailFolder, cacheFileName);

                        if (File.Exists(cachedPath))

                        {

                            fixFile.ThumbnailPath = new Uri(cachedPath, UriKind.Absolute).ToString();

                        }

                    }



                    list.Add(fixFile);

                }

                return list;

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] FetchNewestOnlineFixFilesAsync failed: {ex.Message}");

                return new List<OnlineFixFile>();

            }

        }



        private async Task LoadNewestManifestCardsAsync(long? generation = null)

        {

            long gen = generation ?? _navigationGeneration;

            try

            {

                Logger.Log($"[DashboardView] LoadNewestManifestCardsAsync started (gen {gen})");

                var manifests = await FetchNewestManifestsAsync(18);



                // Lifecycle & generation guard: discard if view unloaded or superseded

                if (!IsLoaded || gen != _navigationGeneration)

                {

                    Logger.Log($"[DashboardView] Discarding LoadNewestManifestCardsAsync for obsolete gen {gen} (current {_navigationGeneration}, IsLoaded={IsLoaded})");

                    return;

                }



                var filteredManifests = FilterNewestManifests(manifests);

                _newestManifests = filteredManifests.Take(12).ToList();

                Logger.Log($"[DashboardView] LoadNewestManifestCardsAsync got {_newestManifests.Count} manifests after filtering");

                _cachedNewestManifestCards.Clear();

                NewestManifestCardsPanel?.Children.Clear();

                double availableWidth = GetAvailableManifestPanelWidth();

                _newestManifestColumns = CalculateNewestManifestColumns(availableWidth);

                CreateNewestManifestCards();

                UpdateNewestManifestPlaceholder();

                Logger.Log($"[DashboardView] LoadNewestManifestCardsAsync completed (gen {gen})");

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] LoadNewestManifestCardsAsync failed: {ex.Message}\n{ex.StackTrace}");

                if (IsLoaded && gen == _navigationGeneration)

                {

                    UpdateNewestManifestPlaceholder();

                }

            }

        }



        private async Task LoadRecentBypassesAsync(long? generation = null)

        {

            long gen = generation ?? _navigationGeneration;

            try

            {

                Logger.Log($"[DashboardView] LoadRecentBypassesAsync started (gen {gen})");

                var bypasses = await FetchNewestBypassFilesAsync(18);



                if (!IsLoaded || gen != _navigationGeneration)

                {

                    Logger.Log($"[DashboardView] Discarding LoadRecentBypassesAsync for obsolete gen {gen} (current {_navigationGeneration}, IsLoaded={IsLoaded})");

                    return;

                }



                _newestBypasses = bypasses;

                _cachedBypassCards.Clear();

                Logger.Log($"[DashboardView] LoadRecentBypassesAsync loaded {_newestBypasses.Count} bypasses (gen {gen})");



                if (_activeRecentTab == "Bypass")

                {

                    RenderActiveRecentCards();

                }

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] LoadRecentBypassesAsync failed: {ex.Message}");

                if (IsLoaded && gen == _navigationGeneration && _activeRecentTab == "Bypass")

                {

                    UpdateRecentPlaceholder();

                }

            }

        }



        private async Task LoadRecentOnlineFixesAsync(long? generation = null)

        {

            long gen = generation ?? _navigationGeneration;

            try

            {

                Logger.Log($"[DashboardView] LoadRecentOnlineFixesAsync started (gen {gen})");

                var fixes = await FetchNewestOnlineFixFilesAsync(18);



                if (!IsLoaded || gen != _navigationGeneration)

                {

                    Logger.Log($"[DashboardView] Discarding LoadRecentOnlineFixesAsync for obsolete gen {gen} (current {_navigationGeneration}, IsLoaded={IsLoaded})");

                    return;

                }



                _newestOnlineFixes = fixes;

                _cachedOnlineFixCards.Clear();

                Logger.Log($"[DashboardView] LoadRecentOnlineFixesAsync loaded {_newestOnlineFixes.Count} online fixes (gen {gen})");



                if (_activeRecentTab == "OnlineFix")

                {

                    RenderActiveRecentCards();

                }

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] LoadRecentOnlineFixesAsync failed: {ex.Message}");

                if (IsLoaded && gen == _navigationGeneration && _activeRecentTab == "OnlineFix")

                {

                    UpdateRecentPlaceholder();

                }

            }

        }



        private void UpdateNewestManifestPlaceholder() => UpdateRecentPlaceholder();



        private void UpdateRecentPlaceholder()

        {

            if (RecentPlaceholder == null || RecentCardsPanel == null)

                return;



            int count = _activeRecentTab switch

            {

                "Bypass" => _newestBypasses.Count,

                "OnlineFix" => _newestOnlineFixes.Count,

                _ => _newestManifests.Count

            };



            if (count == 0)

            {

                RecentPlaceholder.Text = _activeRecentTab switch

                {

                    "Bypass" => "No recent bypass entries available.",

                    "OnlineFix" => "No recent online fix entries available.",

                    _ => "No newest manifests available."

                };

                RecentPlaceholder.Visibility = Visibility.Visible;

            }

            else

            {

                RecentPlaceholder.Visibility = Visibility.Collapsed;

            }

        }



        private void UnusedOriginalPlaceholder()

        {

            if (NewestManifestPlaceholder == null || NewestManifestCardsPanel == null)

                return;



            if (_newestManifests.Count == 0)

            {

                NewestManifestPlaceholder.Text = "No newest manifests available.";

                NewestManifestPlaceholder.Visibility = Visibility.Visible;

            }

            else

            {

                NewestManifestPlaceholder.Visibility = Visibility.Collapsed;

            }

        }



        private List<ManifestFile> FilterNewestManifests(IEnumerable<ManifestFile> manifests)

        {

            if (manifests == null)

                return new List<ManifestFile>();



            if (Allow18PlusContentPreferences.IsAllow18PlusContentEnabled())

                return manifests.ToList();



            return manifests.Where(manifest => !manifest.IsAdult).ToList();

        }



        private void Allow18PlusContentPreferences_Changed(object? sender, bool isEnabled)

        {

            try

            {

                Logger.Log($"[DashboardView] Adult content preference changed: {isEnabled}");

                long generation = unchecked(++_navigationGeneration);

                _ = RefreshDashboardDataAsync(generation, force: true);

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] Failed to refresh newest manifests after adult content preference changed: {ex.Message}");

            }

        }



        private void CreateNewestManifestCards()

        {

            RenderActiveRecentCards();

        }



        private async void RecentTab_Checked(object sender, RoutedEventArgs e)

        {

            if (sender is not RadioButton radio || !radio.IsLoaded)

                return;



            string selectedTab = "Manifest";

            if (radio == TabBypassRadio)

                selectedTab = "Bypass";

            else if (radio == TabOnlineFixRadio)

                selectedTab = "OnlineFix";



            await SwitchRecentTabAsync(selectedTab);

        }



        private async Task SwitchRecentTabAsync(string tab)

        {

            _activeRecentTab = tab;



            if (tab == "Bypass" && _newestBypasses.Count == 0)

            {

                if (RecentPlaceholder != null)

                {

                    RecentPlaceholder.Text = "Loading recent bypasses...";

                    RecentPlaceholder.Visibility = Visibility.Visible;

                }

                RecentCardsPanel?.Children.Clear();



                if (_bypassLoadingTask != null && !_bypassLoadingTask.IsCompleted)

                {

                    await _bypassLoadingTask;

                }

                else

                {

                    _bypassLoadingTask = LoadRecentBypassesAsync(_navigationGeneration);

                    await _bypassLoadingTask;

                }

            }

            else if (tab == "OnlineFix" && _newestOnlineFixes.Count == 0)

            {

                if (RecentPlaceholder != null)

                {

                    RecentPlaceholder.Text = "Loading recent online fixes...";

                    RecentPlaceholder.Visibility = Visibility.Visible;

                }

                RecentCardsPanel?.Children.Clear();



                if (_onlineFixLoadingTask != null && !_onlineFixLoadingTask.IsCompleted)

                {

                    await _onlineFixLoadingTask;

                }

                else

                {

                    _onlineFixLoadingTask = LoadRecentOnlineFixesAsync(_navigationGeneration);

                    await _onlineFixLoadingTask;

                }

            }



            RenderActiveRecentCards();

        }



        private void RenderActiveRecentCards()

        {

            if (RecentCardsPanel == null)

                return;



            List<Border> targetCachedCards;

            int totalItems;



            if (_activeRecentTab == "Bypass")

            {

                targetCachedCards = _cachedBypassCards;

                totalItems = _newestBypasses.Count;

            }

            else if (_activeRecentTab == "OnlineFix")

            {

                targetCachedCards = _cachedOnlineFixCards;

                totalItems = _newestOnlineFixes.Count;

            }

            else

            {

                targetCachedCards = _cachedNewestManifestCards;

                totalItems = _newestManifests.Count;

            }



            if (totalItems == 0)

            {

                RecentCardsPanel.Children.Clear();

                UpdateRecentPlaceholder();

                return;

            }



            int shownCount = _newestManifestColumns <= 1

                ? totalItems

                : Math.Min(totalItems, _newestManifestColumns * 2);



            if (_activeRecentTab == "Bypass")

            {

                while (_cachedBypassCards.Count < shownCount && _cachedBypassCards.Count < _newestBypasses.Count)

                {

                    int index = _cachedBypassCards.Count;

                    _cachedBypassCards.Add(CreateBypassCard(_newestBypasses[index]));

                }

            }

            else if (_activeRecentTab == "OnlineFix")

            {

                while (_cachedOnlineFixCards.Count < shownCount && _cachedOnlineFixCards.Count < _newestOnlineFixes.Count)

                {

                    int index = _cachedOnlineFixCards.Count;

                    _cachedOnlineFixCards.Add(CreateOnlineFixCard(_newestOnlineFixes[index]));

                }

            }

            else

            {

                while (_cachedNewestManifestCards.Count < shownCount && _cachedNewestManifestCards.Count < _newestManifests.Count)

                {

                    int index = _cachedNewestManifestCards.Count;

                    _cachedNewestManifestCards.Add(CreateNewestManifestCard(_newestManifests[index]));

                }

            }



            UpdateUniformGridColumns();



            RecentCardsPanel.Children.Clear();

            for (int i = 0; i < shownCount && i < targetCachedCards.Count; i++)

            {

                var card = targetCachedCards[i];

                if (card.Parent is Panel p)

                {

                    p.Children.Remove(card);

                }

                RecentCardsPanel.Children.Add(card);

            }



            UpdateRecentPlaceholder();

        }



        private void OriginalCreateNewestManifestCards_Replaced()

        {

            if (NewestManifestCardsPanel == null)

                return;



            if (_newestManifests == null || _newestManifests.Count == 0)

            {

                NewestManifestCardsPanel.Children.Clear();

                _cachedNewestManifestCards.Clear();

                return;

            }



            int shownCount = _newestManifestColumns <= 1

                ? _newestManifests.Count

                : Math.Min(_newestManifests.Count, _newestManifestColumns * 2);



            // Ensure cached cards exist up to shownCount

            while (_cachedNewestManifestCards.Count < shownCount && _cachedNewestManifestCards.Count < _newestManifests.Count)

            {

                int index = _cachedNewestManifestCards.Count;

                var card = CreateNewestManifestCard(_newestManifests[index]);

                _cachedNewestManifestCards.Add(card);

            }



            // Update UniformGrid rows and columns

            UpdateUniformGridColumns();



            // Verify visual children match the cached card prefix

            bool childrenMatch = true;

            for (int i = 0; i < NewestManifestCardsPanel.Children.Count; i++)

            {

                if (i >= _cachedNewestManifestCards.Count || !ReferenceEquals(NewestManifestCardsPanel.Children[i], _cachedNewestManifestCards[i]))

                {

                    childrenMatch = false;

                    break;

                }

            }



            if (!childrenMatch)

            {

                NewestManifestCardsPanel.Children.Clear();

            }



            // Remove trailing cards if fewer cards needed

            while (NewestManifestCardsPanel.Children.Count > shownCount)

            {

                NewestManifestCardsPanel.Children.RemoveAt(NewestManifestCardsPanel.Children.Count - 1);

            }



            // Append cards from cache if more cards needed

            while (NewestManifestCardsPanel.Children.Count < shownCount)

            {

                int nextIndex = NewestManifestCardsPanel.Children.Count;

                var card = _cachedNewestManifestCards[nextIndex];

                if (card.Parent is Panel parent)

                {

                    parent.Children.Remove(card);

                }

                NewestManifestCardsPanel.Children.Add(card);

            }

        }



        private void UpdateUniformGridColumns()

        {

            if (NewestManifestCardsPanel is UniformGrid uniformGrid)

            {

                uniformGrid.Columns = _newestManifestColumns;

                uniformGrid.Rows = _newestManifestColumns <= 1 ? 0 : 2;

            }

        }



        private Border CreateNewestManifestCard(ManifestFile manifest)

        {

            return CreateDashboardCard(

                title: manifest.Name ?? "Unknown manifest",

                tag1: !string.IsNullOrEmpty(manifest.Genre) ? manifest.Genre : "Uncategorized",

                tag2: manifest.AppId.HasValue ? $"App ID: {manifest.AppId}" : string.Empty,

                date: manifest.CreatedAt.HasValue ? manifest.CreatedAt.Value.ToString("MMM dd, yyyy") : "Unknown date",

                thumbnailPath: manifest.ThumbnailPath,

                appId: manifest.AppId,

                onClick: () => NewestManifestCard_Click(manifest),

                onThumbnailCached: path => manifest.ThumbnailPath = path

            );

        }



        private Border CreateBypassCard(BypassFile bypass)

        {

            return CreateDashboardCard(

                title: bypass.Name ?? "Unknown bypass",

                tag1: !string.IsNullOrEmpty(bypass.Category) && bypass.Category != "OTHERS" ? bypass.Category : (!string.IsNullOrEmpty(bypass.Genre) ? bypass.Genre : "Game Bypass"),

                tag2: bypass.AppId.HasValue ? $"App ID: {bypass.AppId}" : string.Empty,

                date: bypass.CreatedAt.HasValue ? bypass.CreatedAt.Value.ToString("MMM dd, yyyy") : "Recently added",

                thumbnailPath: bypass.ThumbnailPath,

                appId: bypass.AppId,

                onClick: () => WindowNavigator.NavigateToGameBypassDetail(bypass),

                onThumbnailCached: path => bypass.ThumbnailPath = path

            );

        }



        private Border CreateOnlineFixCard(OnlineFixFile onlineFix)

        {

            return CreateDashboardCard(

                title: onlineFix.Name ?? "Unknown fix",

                tag1: !string.IsNullOrEmpty(onlineFix.Genre) ? onlineFix.Genre : "Online Fix",

                tag2: onlineFix.AppId.HasValue ? $"App ID: {onlineFix.AppId}" + (!string.IsNullOrEmpty(onlineFix.FileSize) ? $" • {onlineFix.FileSize}" : "") : (!string.IsNullOrEmpty(onlineFix.FileSize) ? onlineFix.FileSize : string.Empty),

                date: onlineFix.CreatedAt.HasValue ? onlineFix.CreatedAt.Value.ToString("MMM dd, yyyy") : "Recently added",

                thumbnailPath: onlineFix.ThumbnailPath,

                appId: onlineFix.AppId,

                onClick: () => WindowNavigator.NavigateToOnlineFixDetail(onlineFix),

                onThumbnailCached: path => onlineFix.ThumbnailPath = path

            );

        }



        private Border CreateDashboardCard(string title, string tag1, string tag2, string date, string? thumbnailPath, int? appId, Action onClick, Action<string>? onThumbnailCached = null)

        {

            var card = new Border

            {

                MinWidth = 240,

                Height = 290,

                Margin = new Thickness(0, 0, 20, 20),

                Padding = new Thickness(0),

                HorizontalAlignment = HorizontalAlignment.Stretch,

                CornerRadius = new CornerRadius(20),

                Background = Application.Current.Resources["CardBackgroundBrush"] as System.Windows.Media.Brush,

                BorderBrush = Application.Current.Resources["BorderBrush"] as System.Windows.Media.Brush,

                BorderThickness = new Thickness(1),

                Cursor = System.Windows.Input.Cursors.Hand,

                SnapsToDevicePixels = true,

                UseLayoutRounding = true

            };



            card.MouseEnter += (_, _) =>

            {

                card.BorderBrush = Application.Current.Resources["AccentBrush"] as System.Windows.Media.Brush;

            };

            card.MouseLeave += (_, _) =>

            {

                card.BorderBrush = Application.Current.Resources["BorderBrush"] as System.Windows.Media.Brush;

            };



            var cardGrid = new Grid();

            cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });

            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });



            var thumbnailPanel = new Border

            {

                Background = Application.Current.Resources["BackgroundBrush"] as System.Windows.Media.Brush,

                CornerRadius = new CornerRadius(20, 20, 0, 0),

                HorizontalAlignment = HorizontalAlignment.Stretch,

                VerticalAlignment = VerticalAlignment.Stretch

            };

            thumbnailPanel.SizeChanged += (s, e) =>

            {

                if (s is Border border && border.ActualWidth > 0 && border.ActualHeight > 0)

                {

                    if (border.Clip is RectangleGeometry existing &&

                        Math.Abs(existing.Rect.Width - border.ActualWidth) < 0.1 &&

                        Math.Abs(existing.Rect.Height - border.ActualHeight) < 0.1)

                    {

                        return;

                    }



                    var geom = new RectangleGeometry(new Rect(0, 0, border.ActualWidth, border.ActualHeight), 20, 20);

                    geom.Freeze();

                    border.Clip = geom;

                }

            };



            var thumbnailGrid = new Grid();

            var placeholderIcon = new TextBlock

            {

                Text = "🎮",

                FontSize = 54,

                Opacity = 0.26,

                HorizontalAlignment = HorizontalAlignment.Center,

                VerticalAlignment = VerticalAlignment.Center

            };

            var imageElement = new Image

            {

                Stretch = System.Windows.Media.Stretch.UniformToFill,

                VerticalAlignment = VerticalAlignment.Stretch,

                HorizontalAlignment = HorizontalAlignment.Stretch

            };

            thumbnailGrid.Children.Add(placeholderIcon);

            thumbnailGrid.Children.Add(imageElement);

            thumbnailPanel.Child = thumbnailGrid;

            Grid.SetRow(thumbnailPanel, 0);

            cardGrid.Children.Add(thumbnailPanel);



            var contentPanel = new StackPanel

            {

                Margin = new Thickness(14, 12, 14, 14),

                VerticalAlignment = VerticalAlignment.Top

            };



            var titleText = new TextBlock

            {

                Text = title,

                FontSize = 14,

                FontWeight = FontWeights.SemiBold,

                Foreground = Application.Current.Resources["ForegroundBrush"] as System.Windows.Media.Brush,

                TextWrapping = TextWrapping.Wrap,

                TextTrimming = TextTrimming.CharacterEllipsis,

                MaxHeight = 42

            };



            var genreText = new TextBlock

            {

                Text = tag1,

                FontSize = 11,

                Foreground = Application.Current.Resources["MutedForegroundBrush"] as System.Windows.Media.Brush,

                Margin = new Thickness(0, 8, 0, 0)

            };



            var appIdText = new TextBlock

            {

                Text = tag2,

                FontSize = 11,

                Foreground = Application.Current.Resources["MutedForegroundBrush"] as System.Windows.Media.Brush,

                Margin = new Thickness(0, 6, 0, 0),

                Visibility = !string.IsNullOrEmpty(tag2) ? Visibility.Visible : Visibility.Collapsed

            };



            var dateText = new TextBlock

            {

                Text = date,

                FontSize = 11,

                Foreground = Application.Current.Resources["MutedForegroundBrush"] as System.Windows.Media.Brush,

                Margin = new Thickness(0, 6, 0, 0)

            };



            contentPanel.Children.Add(titleText);

            contentPanel.Children.Add(genreText);

            contentPanel.Children.Add(appIdText);

            contentPanel.Children.Add(dateText);



            Grid.SetRow(contentPanel, 1);

            cardGrid.Children.Add(contentPanel);



            card.Child = cardGrid;

            card.MouseLeftButtonUp += (_, _) => onClick();



            if (IsNamePlaceholder(title, appId) && appId.HasValue && appId.Value > 0)

            {

                _ = ResolveAndApplyCardTitleAsync(appId.Value, titleText);

            }



            if (!string.IsNullOrWhiteSpace(thumbnailPath))

            {

                _ = LoadCardThumbnailPathAsync(thumbnailPath, imageElement);

            }

            else if (appId.HasValue && appId.Value > 0)

            {

                _ = FetchAndCacheAppIdThumbnailAsync(appId.Value, imageElement, onThumbnailCached);

            }



            return card;

        }



        private static async Task ResolveAndApplyCardTitleAsync(int appId, TextBlock titleBlock)

        {

            try

            {

                string? cached = CacheManager.GetGameName(appId);

                if (!IsNamePlaceholder(cached, appId))

                {

                    await titleBlock.Dispatcher.InvokeAsync(() => titleBlock.Text = cached);

                    return;

                }



                var info = await SteamPluginManager.SteamManifestHelper.GetGameInfoFromSteamAsync(appId);

                if (info != null && !IsNamePlaceholder(info.Name, appId))

                {

                    string clean = info.Name!.Trim();

                    CacheManager.SaveGameData(appId, clean, info.Genre ?? string.Empty);

                    await titleBlock.Dispatcher.InvokeAsync(() => titleBlock.Text = clean);

                }

            }

            catch { }

        }



        private Border LegacyOriginalCreateNewestManifestCard_Removed(ManifestFile manifest)

        {

            var card = new Border

            {

                MinWidth = 240,

                Height = 290,

                Margin = new Thickness(0, 0, 20, 20),

                Padding = new Thickness(0),

                HorizontalAlignment = HorizontalAlignment.Stretch,

                CornerRadius = new CornerRadius(20),

                Background = Application.Current.Resources["CardBackgroundBrush"] as System.Windows.Media.Brush,

                BorderBrush = Application.Current.Resources["BorderBrush"] as System.Windows.Media.Brush,

                BorderThickness = new Thickness(1),

                Cursor = System.Windows.Input.Cursors.Hand,

                SnapsToDevicePixels = true,

                UseLayoutRounding = true

            };



            card.MouseEnter += (_, _) =>

            {

                card.BorderBrush = Application.Current.Resources["AccentBrush"] as System.Windows.Media.Brush;

            };

            card.MouseLeave += (_, _) =>

            {

                card.BorderBrush = Application.Current.Resources["BorderBrush"] as System.Windows.Media.Brush;

            };



            var cardGrid = new Grid();

            cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });

            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });



            var thumbnailPanel = new Border

            {

                Background = Application.Current.Resources["BackgroundBrush"] as System.Windows.Media.Brush,

                CornerRadius = new CornerRadius(20, 20, 0, 0),

                HorizontalAlignment = HorizontalAlignment.Stretch,

                VerticalAlignment = VerticalAlignment.Stretch

            };

            thumbnailPanel.SizeChanged += (s, e) =>

            {

                if (s is Border border && border.ActualWidth > 0 && border.ActualHeight > 0)

                {

                    if (border.Clip is RectangleGeometry existing &&

                        Math.Abs(existing.Rect.Width - border.ActualWidth) < 0.1 &&

                        Math.Abs(existing.Rect.Height - border.ActualHeight) < 0.1)

                    {

                        return;

                    }



                    var geom = new RectangleGeometry(new Rect(0, 0, border.ActualWidth, border.ActualHeight), 20, 20);

                    geom.Freeze();

                    border.Clip = geom;

                }

            };



            var thumbnailGrid = new Grid();

            var placeholderIcon = new TextBlock

            {

                Text = "🎮",

                FontSize = 54,

                Opacity = 0.26,

                HorizontalAlignment = HorizontalAlignment.Center,

                VerticalAlignment = VerticalAlignment.Center

            };

            var imageElement = new Image

            {

                Stretch = System.Windows.Media.Stretch.UniformToFill,

                VerticalAlignment = VerticalAlignment.Stretch,

                HorizontalAlignment = HorizontalAlignment.Stretch

            };

            thumbnailGrid.Children.Add(placeholderIcon);

            thumbnailGrid.Children.Add(imageElement);

            thumbnailPanel.Child = thumbnailGrid;

            Grid.SetRow(thumbnailPanel, 0);

            cardGrid.Children.Add(thumbnailPanel);



            var contentPanel = new StackPanel

            {

                Margin = new Thickness(14, 12, 14, 14),

                VerticalAlignment = VerticalAlignment.Top

            };



            var titleText = new TextBlock

            {

                Text = manifest.Name ?? "Unknown manifest",

                FontSize = 14,

                FontWeight = FontWeights.SemiBold,

                Foreground = Application.Current.Resources["ForegroundBrush"] as System.Windows.Media.Brush,

                TextWrapping = TextWrapping.Wrap,

                TextTrimming = TextTrimming.CharacterEllipsis,

                MaxHeight = 42

            };



            var genreText = new TextBlock

            {

                Text = !string.IsNullOrEmpty(manifest.Genre) ? manifest.Genre : "Uncategorized",

                FontSize = 11,

                Foreground = Application.Current.Resources["MutedForegroundBrush"] as System.Windows.Media.Brush,

                Margin = new Thickness(0, 8, 0, 0)

            };



            var appIdText = new TextBlock

            {

                Text = manifest.AppId.HasValue ? $"App ID: {manifest.AppId}" : string.Empty,

                FontSize = 11,

                Foreground = Application.Current.Resources["MutedForegroundBrush"] as System.Windows.Media.Brush,

                Margin = new Thickness(0, 6, 0, 0),

                Visibility = manifest.AppId.HasValue ? Visibility.Visible : Visibility.Collapsed

            };



            var dateText = new TextBlock

            {

                Text = manifest.CreatedAt.HasValue ? manifest.CreatedAt.Value.ToString("MMM dd, yyyy") : "Unknown date",

                FontSize = 11,

                Foreground = Application.Current.Resources["MutedForegroundBrush"] as System.Windows.Media.Brush,

                Margin = new Thickness(0, 6, 0, 0)

            };



            contentPanel.Children.Add(titleText);

            contentPanel.Children.Add(genreText);

            contentPanel.Children.Add(appIdText);

            contentPanel.Children.Add(dateText);



            Grid.SetRow(contentPanel, 1);

            cardGrid.Children.Add(contentPanel);



            card.Child = cardGrid;

            card.MouseLeftButtonUp += (_, _) => NewestManifestCard_Click(manifest);



            if (!string.IsNullOrWhiteSpace(manifest.ThumbnailPath))

            {

                _ = LoadManifestThumbnailAsync(manifest, imageElement);

            }

            else if (manifest.AppId.HasValue && manifest.AppId.Value > 0)

            {

                _ = FetchAndCacheManifestThumbnailAsync(manifest, imageElement);

            }



            return card;

        }



        private Task LoadCardThumbnailPathAsync(string? path, Image image)

        {

            return Task.Run(() =>

            {

                try

                {

                    if (string.IsNullOrWhiteSpace(path))

                        return;



                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();

                    bitmap.BeginInit();

                    bitmap.UriSource = new Uri(path, UriKind.RelativeOrAbsolute);

                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;

                    bitmap.DecodePixelWidth = 240;

                    bitmap.EndInit();

                    bitmap.Freeze();



                    image.Dispatcher.InvokeAsync(() => image.Source = bitmap);

                }

                catch

                {

                    // Ignore missing or invalid thumbnails

                }

            });

        }



        private Task LoadManifestThumbnailAsync(ManifestFile manifest, Image image) =>

            LoadCardThumbnailPathAsync(manifest.ThumbnailPath, image);



        private Task UnusedOriginalLoadManifestThumbnailAsync(ManifestFile manifest, Image image)

        {

            return Task.Run(() =>

            {

                try

                {

                    if (string.IsNullOrWhiteSpace(manifest.ThumbnailPath))

                        return;



                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();

                    bitmap.BeginInit();

                    bitmap.UriSource = new Uri(manifest.ThumbnailPath, UriKind.RelativeOrAbsolute);

                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;

                    bitmap.DecodePixelWidth = 240;

                    bitmap.EndInit();

                    bitmap.Freeze();



                    image.Dispatcher.InvokeAsync(() => image.Source = bitmap);

                }

                catch

                {

                    // Ignore missing or invalid thumbnails

                }

            });

        }



        private static bool IsThumbnailNegativelyCached(int appId)

        {

            lock (_negativeThumbnailCacheLock)

            {

                if (_negativeThumbnailCache.TryGetValue(appId, out var expiresAt))

                {

                    if (DateTime.UtcNow < expiresAt)

                    {

                        return true;

                    }

                    _negativeThumbnailCache.Remove(appId);

                }

                return false;

            }

        }



        private static void RecordNegativeThumbnailCache(int appId)

        {

            lock (_negativeThumbnailCacheLock)

            {

                var now = DateTime.UtcNow;



                if (_negativeThumbnailCache.Count >= MaxNegativeCacheEntries)

                {

                    var expiredKeys = new List<int>();

                    foreach (var kvp in _negativeThumbnailCache)

                    {

                        if (kvp.Value <= now)

                        {

                            expiredKeys.Add(kvp.Key);

                        }

                    }



                    foreach (var key in expiredKeys)

                    {

                        _negativeThumbnailCache.Remove(key);

                    }



                    if (_negativeThumbnailCache.Count >= MaxNegativeCacheEntries)

                    {

                        int removeCount = MaxNegativeCacheEntries / 5;

                        var oldestKeys = _negativeThumbnailCache

                            .OrderBy(kvp => kvp.Value)

                            .Take(removeCount)

                            .Select(kvp => kvp.Key)

                            .ToList();



                        foreach (var key in oldestKeys)

                        {

                            _negativeThumbnailCache.Remove(key);

                        }

                    }

                }



                _negativeThumbnailCache[appId] = now.Add(NegativeThumbnailCacheDuration);

            }

        }



        private static void RemoveNegativeThumbnailCache(int appId)

        {

            lock (_negativeThumbnailCacheLock)

            {

                _negativeThumbnailCache.Remove(appId);

            }

        }



        private Task<bool> FetchAndCacheManifestThumbnailAsync(ManifestFile manifest, Image image) =>

            manifest.AppId.HasValue ? FetchAndCacheAppIdThumbnailAsync(manifest.AppId.Value, image, path => manifest.ThumbnailPath = path) : Task.FromResult(false);



        private async Task<bool> FetchAndCacheAppIdThumbnailAsync(int appId, Image image, Action<string>? onCached = null)

        {

            try

            {

                if (appId <= 0)

                    return false;

                string cacheFileName = $"{appId}_header.jpg";

                string cachedPath = Path.Combine(_thumbnailCacheFolder, cacheFileName);



                if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 0)

                {

                    RemoveNegativeThumbnailCache(appId);

                    string uriString = new Uri(cachedPath, UriKind.Absolute).ToString();

                    onCached?.Invoke(uriString);

                    await LoadCardThumbnailPathAsync(uriString, image);

                    return true;

                }



                // Skip repeated network lookups if negatively cached within the last 12 hours

                if (IsThumbnailNegativelyCached(appId))

                {

                    return false;

                }



                bool success = await SharedHttpClient.RunThrottledThumbnailFetchAsync(cacheFileName, async () =>

                {

                    if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 0)

                    {

                        RemoveNegativeThumbnailCache(appId);

                        return true;

                    }



                    if (IsThumbnailNegativelyCached(appId))

                    {

                        return false;

                    }



                    string[] imageFormats = new[]

                    {

                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header_292x136.jpg",

                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header_616x353.jpg",

                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header_1840x620.jpg",

                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_231x87.jpg",

                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_467x181.jpg",

                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",

                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",

                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule.jpg",

                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/logo.png"

                    };



                    int cdnNotFoundCount = 0;

                    bool hadTransientOrInconclusiveFailure = false;

                    byte[]? downloadedData = null;



                    foreach (var imageUrl in imageFormats)

                    {

                        try

                        {

                            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));

                            using var response = await SharedHttpClient.Instance.GetAsync(imageUrl, cts.Token);

                            if (response.IsSuccessStatusCode)

                            {

                                downloadedData = await response.Content.ReadAsByteArrayAsync(cts.Token);

                                if (downloadedData != null && downloadedData.Length > 0)

                                    break;

                            }

                            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)

                            {

                                cdnNotFoundCount++;

                            }

                            else

                            {

                                // 429, 5xx, 403, or any non-404 status is inconclusive / transient

                                hadTransientOrInconclusiveFailure = true;

                            }

                        }

                        catch

                        {

                            // Timeout, network disconnect, or cancellation

                            hadTransientOrInconclusiveFailure = true;

                        }

                    }



                    bool storeConfirmedNoArtwork = false;



                    if (downloadedData == null || downloadedData.Length == 0)

                    {

                        try

                        {

                            using var storeCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));

                            string storeUrl = $"https://store.steampowered.com/app/{appId}";

                            using var storeResponse = await SharedHttpClient.Instance.GetAsync(storeUrl, storeCts.Token);

                            if (storeResponse.IsSuccessStatusCode)

                            {

                                var html = await storeResponse.Content.ReadAsStringAsync(storeCts.Token);



                                bool isAgeOrAccessGate = html.Contains("agecheck", StringComparison.OrdinalIgnoreCase) ||

                                                         html.Contains("app_agegate", StringComparison.OrdinalIgnoreCase) ||

                                                         html.Contains("checkage", StringComparison.OrdinalIgnoreCase) ||

                                                         html.Contains("access_denied", StringComparison.OrdinalIgnoreCase);



                                var match = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]*property=[\"']og:image[\"'][^>]*content=[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                                if (!match.Success)

                                {

                                    match = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]*content=[\"']([^\"']+)[\"'][^>]*property=[\"']og:image[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                                }



                                if (match.Success && Uri.IsWellFormedUriString(match.Groups[1].Value, UriKind.Absolute))

                                {

                                    var assetUrl = match.Groups[1].Value;

                                    try

                                    {

                                        using var assetCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));

                                        using var assetResp = await SharedHttpClient.Instance.GetAsync(assetUrl, assetCts.Token);

                                        if (assetResp.IsSuccessStatusCode)

                                        {

                                            downloadedData = await assetResp.Content.ReadAsByteArrayAsync(assetCts.Token);

                                        }

                                        else

                                        {

                                            // Discovered og:image but download returned non-success (e.g. 5xx, 429, 404)

                                            hadTransientOrInconclusiveFailure = true;

                                        }

                                    }

                                    catch

                                    {

                                        // Discovered og:image but download threw timeout / network error

                                        hadTransientOrInconclusiveFailure = true;

                                    }

                                }

                                else if (!isAgeOrAccessGate)

                                {

                                    // Clean Store page returned 200 without og:image and is not an age/access gate

                                    storeConfirmedNoArtwork = true;

                                }

                                else

                                {

                                    // Age or access gate page: absence of og:image is inconclusive

                                    hadTransientOrInconclusiveFailure = true;

                                }

                            }

                            else if (storeResponse.StatusCode == System.Net.HttpStatusCode.NotFound)

                            {

                                // Store confirmed app does not exist (404)

                                storeConfirmedNoArtwork = true;

                            }

                            else

                            {

                                // Store returned 429, 5xx, 403, etc.

                                hadTransientOrInconclusiveFailure = true;

                            }

                        }

                        catch

                        {

                            // Store request timed out or experienced network error

                            hadTransientOrInconclusiveFailure = true;

                        }

                    }



                    if (downloadedData != null && downloadedData.Length > 0)

                    {

                        try

                        {

                            await File.WriteAllBytesAsync(cachedPath, downloadedData);

                            RemoveNegativeThumbnailCache(appId);

                            return true;

                        }

                        catch (Exception ex)

                        {

                            Logger.Log($"[DashboardView] Failed to write thumbnail to '{cachedPath}': {ex.Message}");

                            return false;

                        }

                    }



                    // Check if the file appeared concurrently on disk

                    if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 0)

                    {

                        RemoveNegativeThumbnailCache(appId);

                        return true;

                    }



                    bool isShuttingDown = false;

                    try

                    {

                        isShuttingDown = Application.Current?.Dispatcher?.HasShutdownStarted == true;

                    }

                    catch { }



                    // Only record a negative cache entry if:

                    // 1. All CDN candidates returned HTTP 404 (NotFound).

                    // 2. Store fallback affirmatively confirmed no artwork (404 or clean 200 with no og:image and no age/access gate).

                    // 3. Zero transient/inconclusive failures (no 429, 5xx, 403, timeouts, or network exceptions).

                    // 4. Application is not shutting down.

                    // 5. No valid file has appeared on disk.

                    if (!hadTransientOrInconclusiveFailure &&

                        cdnNotFoundCount == imageFormats.Length &&

                        storeConfirmedNoArtwork &&

                        !isShuttingDown &&

                        !File.Exists(cachedPath))

                    {

                        RecordNegativeThumbnailCache(appId);

                    }



                    return false;

                });



                if (success && File.Exists(cachedPath))

                {

                    string uriString = new Uri(cachedPath, UriKind.Absolute).ToString();

                    onCached?.Invoke(uriString);

                    await LoadCardThumbnailPathAsync(uriString, image);

                    return true;

                }



                return false;

            }

            catch

            {

                return false;

            }

        }



        private void NewestManifestCard_Click(ManifestFile manifest)

        {

            WindowNavigator.NavigateToHZManifestDetail(manifest, source: "Dashboard");

        }



        private void SeeMoreRecent_Click(object sender, RoutedEventArgs e)

        {

            if (_activeRecentTab == "Bypass")

            {

                WindowNavigator.NavigateToGameBypassWithSort("DateDesc");

            }

            else if (_activeRecentTab == "OnlineFix")

            {

                WindowNavigator.NavigateToOnlineFixWithSort("DateDesc");

            }

            else

            {

                WindowNavigator.NavigateToHZManifestWithSort("DateDesc");

            }

        }



        private static string FormatFileSize(long bytes)

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



        private void SeeMoreManifests_Click(object sender, RoutedEventArgs e) => SeeMoreRecent_Click(sender, e);

        private void LegacyOriginalSeeMoreManifests_Removed(object sender, RoutedEventArgs e)

        {

            WindowNavigator.NavigateToHZManifestWithSort("DateDesc");

        }



        private void ViewAllManifests_Click(object sender, RoutedEventArgs e)

        {

            WindowNavigator.NavigateToHZManifestWithSort("DateDesc");

        }



        private static async Task<UserProfile?> QueryUserProfileByDeviceIdAsync(string deviceId)

        {

            if (string.IsNullOrWhiteSpace(deviceId))

                return null;



            var http = SharedHttpClient.Instance;

            var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?device_id=eq.{Uri.EscapeDataString(deviceId)}&select=*";

            var request = new HttpRequestMessage(HttpMethod.Get, url);

            request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);

            request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

            request.Headers.Add("Accept", "application/json");



            var response = await http.SendAsync(request);

            if (!response.IsSuccessStatusCode)

                return null;



            var content = await response.Content.ReadAsStringAsync();

            var profiles = JsonSerializer.Deserialize<List<UserProfile>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return profiles != null && profiles.Count > 0 ? profiles[0] : null;

        }



        private async Task SaveUserProfileAsync()

        {

            if (_currentUserProfile == null)

            {

                SetProfileStatus("No profile data to save.");

                return;

            }



            await SaveUserProfileAsync(_currentUserProfile);

        }



        public static async Task<(bool Success, string? ErrorMessage)> SaveUserProfileAsync(UserProfile profile)

        {

            try

            {

                var http = SharedHttpClient.Instance;

                var payload = new Dictionary<string, object?>

                {

                    ["device_id"] = profile.DeviceId,

                    ["display_name"] = profile.DisplayName,

                    ["bio"] = profile.Bio,

                    ["avatar_url"] = profile.AvatarUrl,

                    ["updated_at"] = DateTime.UtcNow.ToString("o")

                };



                var requestUrl = profile.Id.HasValue

                    ? $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?id=eq.{profile.Id.Value}"

                    : $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles";

                var requestMethod = profile.Id.HasValue ? new HttpMethod("PATCH") : HttpMethod.Post;

                var request = new HttpRequestMessage(requestMethod, requestUrl)

                {

                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")

                };

                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);

                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                request.Headers.Add("Prefer", "return=representation");



                var response = await http.SendAsync(request);

                var responseContent = await response.Content.ReadAsStringAsync();



                if (!response.IsSuccessStatusCode)

                {

                    string errorMessage = $"HTTP {(int)response.StatusCode}: {responseContent}";

                    Logger.Log($"[DashboardView] SaveUserProfileAsync failed - {errorMessage}");

                    Logger.Log($"[DashboardView] Request payload: {JsonSerializer.Serialize(payload)}");



                    string userFriendlyError;

                    if (response.StatusCode == System.Net.HttpStatusCode.Conflict)

                    {

                        userFriendlyError = "Device ID already exists. Contact support if this persists.";

                    }

                    else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || response.StatusCode == System.Net.HttpStatusCode.Unauthorized)

                    {

                        userFriendlyError = "Permission denied. API configuration may need updating.";

                    }

                    else

                    {

                        userFriendlyError = "Failed to save profile.";

                    }

                    UpdateSidebarProfile(profile.DisplayName, userFriendlyError);

                    return (false, userFriendlyError);

                }



                if (!profile.Id.HasValue)

                {

                    var createdProfiles = JsonSerializer.Deserialize<List<UserProfile>>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (createdProfiles != null && createdProfiles.Count > 0)

                    {

                        profile.Id = createdProfiles[0].Id;

                        Logger.Log($"[DashboardView] New profile created with ID: {profile.Id}");

                    }

                }



                UpdateSidebarProfile(profile.DisplayName, string.IsNullOrWhiteSpace(profile.DisplayName) ? "Profile saved, but display name is empty." : string.Empty);

                return (true, null);

            }

            catch (Exception ex)

            {

                Logger.Log($"[DashboardView] SaveUserProfileAsync failed: {ex.Message}");

                Logger.Log($"[DashboardView] Stack trace: {ex.StackTrace}");

                UpdateSidebarProfile(profile.DisplayName, "Failed to save profile.");

                return (false, ex.Message);

            }

        }



        private async void ProfileNameText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)

        {

            await OpenProfileEditorAsync();

        }



        public async Task ShowProfileEditorDialogAsync()

        {

            if (_currentUserProfile == null)

                return;



            await ShowProfileEditorDialogInternalAsync(_currentUserProfile, Window.GetWindow(this));

        }



        private static Task ShowProfileEditorDialogInternalAsync(UserProfile profile, Window? ownerWindow)

        {

            if (profile == null)

                return Task.CompletedTask;



            var window = new Window

            {

                Title = "Customize Profile",

                Owner = ownerWindow,

                WindowStartupLocation = WindowStartupLocation.CenterOwner,

                SizeToContent = SizeToContent.WidthAndHeight,

                ResizeMode = ResizeMode.NoResize,

                WindowStyle = WindowStyle.None,

                Background = System.Windows.Media.Brushes.Transparent,

                AllowsTransparency = true

            };



            var modal = new CustomizeProfileModalView();

            modal.ShowModal(profile, onSaved: () =>

            {

                if (_currentInstance != null)

                {

                    _currentInstance._currentUserProfile = profile;

                    _currentInstance.UpdateProfileUI();

                }

                window.Close();

            });



            window.Content = modal;

            window.ShowDialog();

            return Task.CompletedTask;

        }



        private void Library_Click(object sender, RoutedEventArgs e) => WindowNavigator.NavigateToGameLibrary();



        private async void GameBypass_Click(object sender, RoutedEventArgs e)

        {

            try

            {

                bool verified = await CheckDeviceTokenAsync();

                if (!verified)

                {

                    WindowNavigator.NextViewAfterVerify = "GameBypass";

                    WindowNavigator.NavigateToVerifyToken();

                    return;

                }

                WindowNavigator.NavigateToGameBypass();

            }

            catch (Exception ex)

            {

                ModernMessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);

                Logger.Log($"[DashboardView] GameBypass navigation error: {ex.Message}");

            }

        }



        private void Restore_Click(object sender, RoutedEventArgs e)

        {

            var openFileDialog = new OpenFileDialog { Filter = "HZ Backup (*.hzbak)|*.hzbak|SPM Backup (*.spmb)|*.spmb|All Files (*.*)|*.*", Title = "Restore Game Backup", Multiselect = false };

            if (openFileDialog.ShowDialog() != true) return;

            try

            {

                string backupFilePath = openFileDialog.FileName;

                if (!File.Exists(backupFilePath)) { ModernMessageBox.Show("Selected backup file does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error); return; }

                var result = ModernMessageBox.Show($"This will restore the backup from:\n{Path.GetFileName(backupFilePath)}\n\nExisting plugin and manifest files will be overwritten.\nContinue?", "Confirm Restore", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes) return;

                _ = RestoreBackupAsync(backupFilePath);

            }

            catch (Exception ex) { ModernMessageBox.Show($"Error opening backup file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }

        }



        private void RestartSteam_Click(object sender, RoutedEventArgs e) { if (SteamHelper.IsSteamRunning()) SteamHelper.RestartSteam(); else SteamHelper.LaunchSteam(); }



        private void HZManifest_Click(object sender, RoutedEventArgs e) => WindowNavigator.NavigateToHZManifest();



        private void Settings_Click(object sender, RoutedEventArgs e) => WindowNavigator.NavigateToSettings();



        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)

        {

            try

            {

                await DoCheckForUpdatesAndNotifyAsync();

            }

            catch (Exception ex)

            {

                ModernMessageBox.Show($"Failed to check for updates: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);

            }

        }



        private async void About_Click(object sender, RoutedEventArgs e)

        {

            var version = UpdateChecker.GetCurrentVersion();

            string licenseStatus = await GetLicenseStatusAsync();

            var dialog = new StyledMessageDialog("About HZ Lua Manager", $"HZ Lua Manager\nVersion {version}\n\n{licenseStatus}\n\nYour comprehensive lua management solution.");

            dialog.Owner = Window.GetWindow(this);

            dialog.ShowDialog();

        }



        private async Task<string> GetLicenseStatusAsync()

        {

            try

            {

                bool isValid = await CheckDeviceTokenAsync();

                return isValid ? "License: Activated" : "License: Not Activated";

            }

            catch

            {

                return "License: Not Activated";

            }

        }



        private async Task DownloadAndInstallUpdateAsync(string downloadUrl)

        {

            try

            {

                var progressWindow = new Window { Title = "Downloading Update", WindowStartupLocation = WindowStartupLocation.CenterScreen, Width = 400, Height = 150, WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false };

                var textBlock = new TextBlock { Text = "Downloading update...", VerticalAlignment = VerticalAlignment.Center, TextAlignment = System.Windows.TextAlignment.Center, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(20, 0, 20, 20) };

                var progressBar = new ProgressBar { Height = 20, IsIndeterminate = true, Margin = new Thickness(20, 50, 20, 20) };

                var panel = new StackPanel { Background = System.Windows.Media.Brushes.DarkGray, Margin = new Thickness(20, 0, 20, 20) };

                panel.Children.Add(textBlock); panel.Children.Add(progressBar); progressWindow.Content = panel; progressWindow.Show();

                void OnProgressChanged(long downloaded, long total) { Application.Current.Dispatcher.Invoke(() => { if (total > 0) { progressBar.IsIndeterminate = false; progressBar.Maximum = total; progressBar.Value = downloaded; textBlock.Text = $"Downloading update... ({((double)downloaded / total):P0})"; } }); }

                var (success, message) = await UpdateChecker.DownloadAndInstallUpdateAsync(downloadUrl, OnProgressChanged);

                progressWindow.Close();

                if (success)

                {

                    // Silent update; current process terminates after installer starts.

                    return;

                }

                else

                {

                    ModernMessageBox.Show($"Failed to download and install update:\n{message}", "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);

                }

            }

            catch (Exception ex)

            {

                ModernMessageBox.Show($"Error during update: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);

            }

        }



        private async Task RestoreBackupAsync(string backupFilePath)

        {

            try

            {

                var progressWindow = new Window { Title = "Restoring Backup", Width = 400, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), Background = Application.Current.Resources["BackgroundBrush"] as System.Windows.Media.Brush, WindowStyle = WindowStyle.None, AllowsTransparency = false, ResizeMode = ResizeMode.NoResize };

                var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

                var textBlock = new TextBlock { Text = "Restoring backup files...", FontSize = 14, Foreground = Application.Current.Resources["ForegroundBrush"] as System.Windows.Media.Brush, Margin = new Thickness(20) };

                panel.Children.Add(textBlock); progressWindow.Content = panel; progressWindow.Show();

                string steamPath = SteamHelper.GetSteamPath(); if (string.IsNullOrEmpty(steamPath)) { progressWindow.Close(); ModernMessageBox.Show("Could not find Steam installation directory.", "Error", MessageBoxButton.OK, MessageBoxImage.Error); return; }

                using (var memoryStream = BackupCryptoService.DecryptStream(backupFilePath))

                {

                    string stplugInPath = Path.Combine(steamPath, "config", "stplug-in"); string depotCachePath = Path.Combine(steamPath, "depotcache"); string depotCachePathOld = Path.Combine(steamPath, "config", "depotcache"); Directory.CreateDirectory(stplugInPath); Directory.CreateDirectory(depotCachePath); Directory.CreateDirectory(depotCachePathOld);

                    int luaFiles = 0; int manifestFiles = 0;

                    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read))

                    {

                        foreach (var entry in archive.Entries)

                        {

                            if (entry.FullName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) { string targetPath = Path.Combine(stplugInPath, entry.Name); entry.ExtractToFile(targetPath, overwrite: true); luaFiles++; Logger.Log($"[Restore] Extracted lua file: {targetPath}"); }

                            else if (entry.FullName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) { string targetPath = Path.Combine(depotCachePath, entry.Name); string targetPathOld = Path.Combine(depotCachePathOld, entry.Name); entry.ExtractToFile(targetPath, overwrite: true); entry.ExtractToFile(targetPathOld, overwrite: true); manifestFiles++; Logger.Log($"[Restore] Extracted manifest file: {targetPath} and {targetPathOld}"); }

                        }

                    }

                    Logger.Log($"[Restore] Extraction complete: {luaFiles} lua files, {manifestFiles} manifest files"); progressWindow.Close();

                    try { Logger.Log("[Restore] Navigating to GameLibrary..."); WindowNavigator.NavigateToGameLibrary(); await Task.Delay(500); if (Window.GetWindow(this) is MainShell mainShell) { Logger.Log("[Restore] Calling RefreshGameLibrary..."); mainShell.RefreshGameLibrary(); Logger.Log("[Restore] RefreshGameLibrary completed"); } }

                    catch (Exception ex) { Logger.Log($"[Restore] Error during navigation: {ex.Message}"); }

                    ModernMessageBox.Show($"Backup restored successfully!\n\nLua Files: {luaFiles}\nManifest Files: {manifestFiles}\n\nYour game plugins and manifests have been restored.", "Restore Complete", MessageBoxButton.OK, MessageBoxImage.Information);

                }

            }

            catch (Exception ex) { ModernMessageBox.Show($"Failed to restore backup:\n{ex.Message}", "Restore Error", MessageBoxButton.OK, MessageBoxImage.Error); }

        }



        // Device token verification & blocked check single-flight & caching infrastructure

        private static readonly object _tokenVerificationLock = new();

        private static Task<bool>? _inFlightTokenVerificationTask;

        private static DateTime _tokenCacheExpiryUtc = DateTime.MinValue;

        private static bool _cachedTokenVerificationResult;

        private static long _tokenVerificationGeneration;

        private static readonly TimeSpan TokenVerificationCacheDuration = TimeSpan.FromMinutes(3);



        private sealed class BlockedCheckEntry

        {

            public Task<bool>? InFlightTask { get; set; }

            public bool CachedResult { get; set; }

            public DateTime CacheExpiryUtc { get; set; } = DateTime.MinValue;

            public long Generation { get; set; }

        }



        private static readonly object _blockedDeviceLock = new();

        private static readonly Dictionary<string, BlockedCheckEntry> _blockedChecks = new(StringComparer.OrdinalIgnoreCase);

        private static long _blockedCheckGeneration;

        private static readonly TimeSpan BlockedCheckCacheDuration = TimeSpan.FromSeconds(60);



        /// <summary>

        /// Explicitly invalidates the in-memory cache for device-token verification and blocked-device status.

        /// Call this after successful token activation, manual token revocation, or user logout.

        /// </summary>

        public static void InvalidateDeviceTokenCache()

        {

            lock (_tokenVerificationLock)

            {

                _tokenVerificationGeneration++;

                _cachedTokenVerificationResult = false;

                _tokenCacheExpiryUtc = DateTime.MinValue;

                _inFlightTokenVerificationTask = null;

            }

            lock (_blockedDeviceLock)

            {

                _blockedCheckGeneration++;

                _blockedChecks.Clear();

            }

        }



        public static Task<bool> IsDeviceBlockedAsync(string? deviceId = null, bool forceRefresh = false)

        {

            string id = deviceId ?? DeviceIdentifier.GetDeviceId();

            if (string.IsNullOrWhiteSpace(id) || id == "UNKNOWN")

                return Task.FromResult(false);



            lock (_blockedDeviceLock)

            {

                if (!_blockedChecks.TryGetValue(id, out var entry))

                {

                    entry = new BlockedCheckEntry();

                    _blockedChecks[id] = entry;

                }



                if (forceRefresh)

                {

                    _blockedCheckGeneration++;

                    entry.Generation = _blockedCheckGeneration;

                    entry.CachedResult = false;

                    entry.CacheExpiryUtc = DateTime.MinValue;

                    entry.InFlightTask = null;

                }

                else

                {

                    if (DateTime.UtcNow < entry.CacheExpiryUtc)

                    {

                        return Task.FromResult(entry.CachedResult);

                    }



                    if (entry.InFlightTask != null)

                    {

                        return entry.InFlightTask;

                    }

                }



                long generation = _blockedCheckGeneration;

                entry.Generation = generation;



                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                var task = tcs.Task;

                entry.InFlightTask = task;



                _ = RunBlockedCheckAsync(id, tcs, task, generation);

                return task;

            }

        }



        private static async Task RunBlockedCheckAsync(string id, TaskCompletionSource<bool> tcs, Task<bool> currentTask, long generation)

        {

            try

            {

                var (isBlocked, isAuthoritative) = await QueryDeviceBlockedFromSupabaseAsync(id);

                Task<bool>? nextTask = null;



                lock (_blockedDeviceLock)

                {

                    if (_blockedChecks.TryGetValue(id, out var entry) && generation == entry.Generation && generation == _blockedCheckGeneration)

                    {

                        if (isAuthoritative)

                        {

                            entry.CachedResult = isBlocked;

                            entry.CacheExpiryUtc = DateTime.UtcNow.Add(BlockedCheckCacheDuration);

                        }

                        else

                        {

                            entry.CacheExpiryUtc = DateTime.MinValue;

                        }



                        tcs.TrySetResult(isBlocked);

                        return;

                    }



                    // Superseded by invalidation or newer forced refresh

                    if (_blockedChecks.TryGetValue(id, out var currentEntry) && DateTime.UtcNow < currentEntry.CacheExpiryUtc)

                    {

                        tcs.TrySetResult(currentEntry.CachedResult);

                        return;

                    }



                    if (_blockedChecks.TryGetValue(id, out var inFlightEntry) && inFlightEntry.InFlightTask != null && !ReferenceEquals(inFlightEntry.InFlightTask, currentTask))

                    {

                        nextTask = inFlightEntry.InFlightTask;

                    }

                    else

                    {

                        tcs.TrySetResult(false);

                        return;

                    }

                }



                if (nextTask != null)

                {

                    try

                    {

                        bool nextResult = await nextTask;

                        tcs.TrySetResult(nextResult);

                        return;

                    }

                    catch (Exception ex)

                    {

                        tcs.TrySetException(ex);

                        return;

                    }

                }



                tcs.TrySetResult(false);

            }

            catch (Exception ex)

            {

                try { Logger.Log($"[DashboardView] RunBlockedCheckAsync error: {ex.Message}"); } catch { }

                tcs.TrySetResult(false);

            }

            finally

            {

                lock (_blockedDeviceLock)

                {

                    if (_blockedChecks.TryGetValue(id, out var entry) && ReferenceEquals(entry.InFlightTask, currentTask))

                    {

                        entry.InFlightTask = null;

                    }

                }

            }

        }



        private static async Task<(bool isBlocked, bool isAuthoritative)> QueryDeviceBlockedFromSupabaseAsync(string id)

        {

            try

            {

                var http = SharedHttpClient.Instance;

                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/blocked_devices?device_id=eq.{Uri.EscapeDataString(id)}&select=id";

                var request = new HttpRequestMessage(HttpMethod.Get, url);

                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);

                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                request.Headers.Add("Accept", "application/json");



                var response = await http.SendAsync(request);

                if (!response.IsSuccessStatusCode) return (false, false);



                var content = await response.Content.ReadAsStringAsync();

                bool isBlocked = !string.IsNullOrWhiteSpace(content) && content != "[]";

                return (isBlocked, true);

            }

            catch (Exception ex)

            {

                try { Logger.Log($"[DashboardView] QueryDeviceBlocked error: {ex.Message}"); } catch { }

                return (false, false);

            }

        }



        public static Task<bool> CheckDeviceTokenAsync(bool forceRefresh = false)

        {

            lock (_tokenVerificationLock)

            {

                if (forceRefresh)

                {

                    _tokenVerificationGeneration++;

                    _cachedTokenVerificationResult = false;

                    _tokenCacheExpiryUtc = DateTime.MinValue;

                    _inFlightTokenVerificationTask = null;

                }

                else

                {

                    if (_cachedTokenVerificationResult && DateTime.UtcNow < _tokenCacheExpiryUtc)

                    {

                        return Task.FromResult(true);

                    }



                    if (_inFlightTokenVerificationTask != null)

                    {

                        return _inFlightTokenVerificationTask;

                    }

                }



                long generation = _tokenVerificationGeneration;

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                var task = tcs.Task;

                _inFlightTokenVerificationTask = task;



                _ = RunVerificationAsync(tcs, task, generation, forceRefresh);

                return task;

            }

        }



        private static async Task RunVerificationAsync(TaskCompletionSource<bool> tcs, Task<bool> currentTask, long generation, bool forceRefresh)

        {

            try

            {

                bool isVerified = await ExecuteDeviceTokenVerificationCoreAsync(forceRefresh);

                Task<bool>? nextTask = null;



                lock (_tokenVerificationLock)

                {

                    if (generation == _tokenVerificationGeneration)

                    {

                        if (isVerified)

                        {

                            _cachedTokenVerificationResult = true;

                            _tokenCacheExpiryUtc = DateTime.UtcNow.Add(TokenVerificationCacheDuration);

                        }

                        else

                        {

                            _cachedTokenVerificationResult = false;

                            _tokenCacheExpiryUtc = DateTime.MinValue;

                        }



                        tcs.TrySetResult(isVerified);

                        return;

                    }



                    // Superseded by invalidation or newer forced refresh

                    if (_cachedTokenVerificationResult && DateTime.UtcNow < _tokenCacheExpiryUtc)

                    {

                        tcs.TrySetResult(_cachedTokenVerificationResult);

                        return;

                    }



                    if (_inFlightTokenVerificationTask != null && !ReferenceEquals(_inFlightTokenVerificationTask, currentTask))

                    {

                        nextTask = _inFlightTokenVerificationTask;

                    }

                    else

                    {

                        tcs.TrySetResult(false);

                        return;

                    }

                }



                if (nextTask != null)

                {

                    try

                    {

                        bool nextResult = await nextTask;

                        tcs.TrySetResult(nextResult);

                        return;

                    }

                    catch (Exception ex)

                    {

                        tcs.TrySetException(ex);

                        return;

                    }

                }



                tcs.TrySetResult(false);

            }

            catch (Exception ex)

            {

                try { Logger.Log($"[DashboardView] RunVerificationAsync error: {ex.Message}"); } catch { }

                tcs.TrySetResult(false);

            }

            finally

            {

                lock (_tokenVerificationLock)

                {

                    if (ReferenceEquals(_inFlightTokenVerificationTask, currentTask))

                    {

                        _inFlightTokenVerificationTask = null;

                    }

                }

            }

        }



        private static async Task<bool> ExecuteDeviceTokenVerificationCoreAsync(bool forceRefresh = false)

        {

            try

            {

                string deviceId = DeviceIdentifier.GetDeviceId();

                if (!DeviceIdentifier.IsValidDeviceId(deviceId))

                    return false;



                if (await IsDeviceBlockedAsync(deviceId, forceRefresh))

                {

                    Logger.Log($"[DashboardView] Access denied: Hardware ID '{deviceId}' is permanently blocked.");

                    return false;

                }



                var http = SharedHttpClient.Instance;

                var queryDeviceId = Uri.EscapeDataString(deviceId);

                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/device_tokens?or=(device_id.eq.{queryDeviceId},device_ids.cs.{{{queryDeviceId}}})&select=device_id,device_ids,is_active";



                // Toleransi network startup (coba hingga 3x jika koneksi internet masih inisialisasi)

                HttpResponseMessage? response = null;

                for (int attempt = 0; attempt < 3; attempt++)

                {

                    try

                    {

                        var request = new HttpRequestMessage(HttpMethod.Get, url);

                        request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);

                        request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                        request.Headers.Add("Accept", "application/json");



                        response = await http.SendAsync(request);

                        if (response.IsSuccessStatusCode)

                            break;

                    }

                    catch

                    {

                        if (attempt == 2) throw;

                        await Task.Delay(400);

                    }

                }



                if (response == null || !response.IsSuccessStatusCode)

                    return false;



                var content = await response.Content.ReadAsStringAsync();

                var tokens = JsonSerializer.Deserialize<List<DeviceTokenStatus>>(content, new JsonSerializerOptions

                {

                    PropertyNameCaseInsensitive = true

                });



                return tokens != null && tokens.Count > 0 && tokens.Any(token => token.IsActive == true);

            }

            catch (Exception ex)

            {

                try { Logger.Log($"[DashboardView] CheckDeviceToken error: {ex.Message}"); } catch { }

                return false;

            }

        }



        private sealed class DeviceTokenStatus

        {

            [JsonPropertyName("device_id")]

            public string? DeviceId { get; set; }



            [JsonPropertyName("is_active")]

            public bool? IsActive { get; set; }

        }



        public static async Task<string> GetCurrentDisplayNameAsync()

        {

            try

            {

                string deviceId = DeviceIdentifier.GetDeviceId();

                var http = SharedHttpClient.Instance;

                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?device_id=eq.{Uri.EscapeDataString(deviceId)}&select=display_name";

                var request = new HttpRequestMessage(HttpMethod.Get, url);

                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);

                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                request.Headers.Add("Accept", "application/json");



                var response = await http.SendAsync(request);

                if (!response.IsSuccessStatusCode) return string.Empty;



                var content = await response.Content.ReadAsStringAsync();

                var profiles = JsonSerializer.Deserialize<List<UserProfile>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (profiles == null || profiles.Count == 0) return string.Empty;

                return string.IsNullOrWhiteSpace(profiles[0].DisplayName) ? string.Empty : profiles[0].DisplayName;

            }

            catch { return string.Empty; }

        }



    }

}

