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
        private DispatcherTimer? _steamCheckTimer;
        private DispatcherTimer? _dashboardResizeDebounceTimer;
        private bool _isSteamRunning = false;
        private UserProfile? _currentUserProfile = null;
        private bool _isProfileEditorOpen = false;
        private List<ManifestFile> _mostDownloadedManifests = new();
        private int _currentSlideIndex = 0;
        private DispatcherTimer? _slideAutoAdvanceTimer;
        private bool _isSliderHovered = false;
        private bool _isImageAActive = true;
        private List<ManifestFile> _newestManifests = new();
        private int _newestManifestColumns = 3;
        private readonly HttpClient _thumbnailHttpClient = new();
        private readonly string _thumbnailCacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamPluginManager", "ThumbnailCache");
        private static DashboardView? _currentInstance;
        private const int SaltSize = 16;

        private static string GetBackupPassword()
        {
            var password = Environment.GetEnvironmentVariable("SPM_BACKUP_PASSWORD");
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Environment variable 'SPM_BACKUP_PASSWORD' is not set.");
            }

            return password;
        }

        public DashboardView()
        {
            InitializeComponent();
            InitializeDashboardResizeDebounceTimer();
            Loaded += DashboardView_Loaded;
            Unloaded += DashboardView_Unloaded;
            SizeChanged += DashboardView_SizeChanged;
            Allow18PlusContentPreferences.Allow18PlusContentChanged += Allow18PlusContentPreferences_Changed;
        }

        private void DashboardView_Loaded(object sender, RoutedEventArgs e)
        {
            _currentInstance = this;

            if (!_isInitialized)
            {
                RegisterTextElements();
                Directory.CreateDirectory(_thumbnailCacheFolder);
                _isInitialized = true;
                _ = LoadUserProfileAsync();
            }

            // Always reload newest manifests and featured slider whenever user visits Dashboard
            _ = LoadFeaturedSliderAsync();
            _ = LoadNewestManifestCardsAsync();

            App.LanguageChanged += LanguageChanged_Handler;
            InitializeSteamCheckTimer();
            _ = CheckForUpdatesOnStartupAsync();
        }

        /// <summary>
        /// Refreshes the newest manifest cards and stats on the active DashboardView instance.
        /// Can be called from any other view (e.g. HZManifestView) when a new manifest is added.
        /// </summary>
        public static async Task RefreshNewestManifestsAsync()
        {
            if (_currentInstance != null)
            {
                await _currentInstance.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await _currentInstance.LoadFeaturedSliderAsync();
                        await _currentInstance.LoadNewestManifestCardsAsync();
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

                double width = NewestManifestCardsPanel.ActualWidth > 0 ? NewestManifestCardsPanel.ActualWidth : 900;
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

        private int CalculateNewestManifestColumns(double width)
        {
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

            if (_steamCheckTimer != null)
            {
                _steamCheckTimer.Stop();
                _steamCheckTimer = null;
            }
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
                string deviceId = GetDeviceId();
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
                UpdateSteamStatus(forceUpdate: true);
            }
            catch (Exception ex) { Logger.Log($"Error registering text elements: {ex.Message}"); }
        }

        public void RefreshLanguageUI()
        {
            try { Logger.Log("[DashboardView] Refreshing language UI"); LanguageHelper.UpdateAllRegisteredTextBlocks(); Logger.Log("[DashboardView] Language UI refresh completed"); UpdateSteamStatus(); }
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
                win?.GlobalSidebar?.SetProfile(string.IsNullOrWhiteSpace(name) ? "Set display name" : name, string.IsNullOrWhiteSpace(name) ? "Profile loaded, but display name is empty." : string.Empty);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    win?.SetHeaderProfile(name);
                }
            }
            catch { }
        }

        private async Task LoadFeaturedSliderAsync()
        {
            try
            {
                Logger.Log("[DashboardView] LoadFeaturedSliderAsync started");
                if (SliderLoadingOverlay != null && _mostDownloadedManifests.Count == 0)
                {
                    SliderLoadingOverlay.Visibility = Visibility.Visible;
                }

                var manifests = await SupabaseConfig.GetMostDownloadedManifestFilesAsync(10);
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

                _currentSlideIndex = 0;
                ShowSlide(_currentSlideIndex, animate: false);

                StartSlideAutoAdvanceTimer();

                // Prefetch artworks in background for all carousel games (same cache as GameLibraryDetailView)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string imagesDir = Path.Combine(CacheManager.CacheDirectory, "Images");
                        Directory.CreateDirectory(imagesDir);

                        foreach (var m in _mostDownloadedManifests)
                        {
                            if (!m.AppId.HasValue || m.AppId.Value <= 0) continue;
                            int aid = m.AppId.Value;
                            string hPath = Path.Combine(imagesDir, $"{aid}_hero.jpg");
                            string hdPath = Path.Combine(imagesDir, $"{aid}.jpg");

                            if (!File.Exists(hPath))
                            {
                                try
                                {
                                    using var resp = await _thumbnailHttpClient.GetAsync($"https://steamcdn-a.akamaihd.net/steam/apps/{aid}/library_hero.jpg");
                                    if (resp.IsSuccessStatusCode)
                                    {
                                        var b = await resp.Content.ReadAsByteArrayAsync();
                                        if (b != null && b.Length > 0)
                                        {
                                            await File.WriteAllBytesAsync(hPath, b);
                                            Dispatcher.Invoke(() =>
                                            {
                                                if (_mostDownloadedManifests != null && _currentSlideIndex < _mostDownloadedManifests.Count && _mostDownloadedManifests[_currentSlideIndex].AppId == aid)
                                                {
                                                    SetSlideArtwork(hPath, animate: true);
                                                }
                                            });
                                            continue;
                                        }
                                    }
                                }
                                catch { }

                                if (!File.Exists(hdPath))
                                {
                                    try
                                    {
                                        using var fb = await _thumbnailHttpClient.GetAsync($"https://cdn.cloudflare.steamstatic.com/steam/apps/{aid}/header.jpg");
                                        if (fb.IsSuccessStatusCode)
                                        {
                                            var b = await fb.Content.ReadAsByteArrayAsync();
                                            if (b != null && b.Length > 0)
                                            {
                                                await File.WriteAllBytesAsync(hdPath, b);
                                                Dispatcher.Invoke(() =>
                                                {
                                                    if (_mostDownloadedManifests != null && _currentSlideIndex < _mostDownloadedManifests.Count && _mostDownloadedManifests[_currentSlideIndex].AppId == aid)
                                                    {
                                                        SetSlideArtwork(hdPath, animate: true);
                                                    }
                                                });
                                            }
                                        }
                                    }
                                    catch { }
                                }
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
                bool displayedFromCache = false;
                if (File.Exists(heroLocalPath) && new FileInfo(heroLocalPath).Length > 0)
                {
                    SetSlideArtwork(heroLocalPath, animate);
                    displayedFromCache = true;
                }
                else if (File.Exists(headerLocalPath) && new FileInfo(headerLocalPath).Length > 0)
                {
                    SetSlideArtwork(headerLocalPath, animate);
                    displayedFromCache = true;
                }
                else
                {
                    ClearSlideArtwork(animate);
                }

                // If high-resolution hero art is already on disk, we are 100% done!
                if (File.Exists(heroLocalPath) && new FileInfo(heroLocalPath).Length > 0)
                {
                    Logger.Log($"[DashboardView] Using cached hero artwork for '{manifest.Name}' ({appId})");
                    return;
                }

                // 2. ASYNC BACKGROUND FETCH & LOCAL DISK PERSISTENCE (exact same URLs as GameLibraryDetailView)
                string heroCdnUrl = $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/library_hero.jpg";
                string fallbackUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // A. Try fetching library_hero.jpg
                        using var response = await _thumbnailHttpClient.GetAsync(heroCdnUrl);
                        if (response.IsSuccessStatusCode)
                        {
                            var bytes = await response.Content.ReadAsByteArrayAsync();
                            if (bytes != null && bytes.Length > 0)
                            {
                                await File.WriteAllBytesAsync(heroLocalPath, bytes);
                                Dispatcher.Invoke(() =>
                                {
                                    if (_currentSlideIndex == slideIndex)
                                    {
                                        SetSlideArtwork(heroLocalPath, animate: true);
                                    }
                                });
                                Logger.Log($"[DashboardView] Downloaded & cached hero artwork for '{manifest.Name}' ({appId})");
                                return;
                            }
                        }

                        // B. Fallback to header.jpg if no local image is displayed yet or hero was missing
                        if (!displayedFromCache && !File.Exists(headerLocalPath))
                        {
                            using var fbResp = await _thumbnailHttpClient.GetAsync(fallbackUrl);
                            if (fbResp.IsSuccessStatusCode)
                            {
                                var bytes = await fbResp.Content.ReadAsByteArrayAsync();
                                if (bytes != null && bytes.Length > 0)
                                {
                                    await File.WriteAllBytesAsync(headerLocalPath, bytes);
                                    Dispatcher.Invoke(() =>
                                    {
                                        if (_currentSlideIndex == slideIndex)
                                        {
                                            SetSlideArtwork(headerLocalPath, animate: true);
                                        }
                                    });
                                    Logger.Log($"[DashboardView] Downloaded & cached fallback header for '{manifest.Name}' ({appId})");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[DashboardView] Background hero fetch error for {appId}: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[DashboardView] LoadSlideArtwork error: {ex.Message}");
            }
        }

        private void SetSlideArtwork(string filePath, bool animate)
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

                if (!animate)
                {
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

        private async Task LoadUserProfileAsync()
        {
            try
            {
                SetProfileStatus("Loading profile...");
                string deviceId = GetDeviceId();
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

        private async Task LoadNewestManifestCardsAsync()
        {
            try
            {
                Logger.Log("[DashboardView] LoadNewestManifestCardsAsync started");
                var manifests = await FetchNewestManifestsAsync(18);
                var filteredManifests = FilterNewestManifests(manifests);
                _newestManifests = filteredManifests.Take(12).ToList();
                Logger.Log($"[DashboardView] LoadNewestManifestCardsAsync got {_newestManifests.Count} manifests after filtering");
                _newestManifestColumns = CalculateNewestManifestColumns(ActualWidth);
                CreateNewestManifestCards();
                UpdateNewestManifestPlaceholder();
                Logger.Log("[DashboardView] LoadNewestManifestCardsAsync completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DashboardView] LoadNewestManifestCardsAsync failed: {ex.Message}\n{ex.StackTrace}");
                UpdateNewestManifestPlaceholder();
            }
        }

        private void UpdateNewestManifestPlaceholder()
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
                _ = LoadFeaturedSliderAsync();
                _ = LoadNewestManifestCardsAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"[DashboardView] Failed to refresh newest manifests after adult content preference changed: {ex.Message}");
            }
        }

        private void CreateNewestManifestCards()
        {
            if (NewestManifestCardsPanel == null)
                return;

            NewestManifestCardsPanel.Children.Clear();
            if (_newestManifestColumns <= 1)
            {
                foreach (var manifest in _newestManifests)
                {
                    var card = CreateNewestManifestCard(manifest);
                    card.Width = double.NaN;
                    card.HorizontalAlignment = HorizontalAlignment.Stretch;
                    NewestManifestCardsPanel.Children.Add(card);
                }
                return;
            }

            int shownCount = Math.Min(_newestManifests.Count, _newestManifestColumns * 2);

            for (int i = 0; i < shownCount; i++)
            {
                var card = CreateNewestManifestCard(_newestManifests[i]);
                card.Width = double.NaN;
                card.MinWidth = 240;
                card.HorizontalAlignment = HorizontalAlignment.Stretch;
                NewestManifestCardsPanel.Children.Add(card);
            }

            UpdateUniformGridColumns();
        }

        private void UpdateUniformGridColumns()
        {
            if (NewestManifestCardsPanel is UniformGrid uniformGrid)
            {
                uniformGrid.Columns = _newestManifestColumns;
                uniformGrid.Rows = 2;
            }
        }

        private Border CreateNewestManifestCard(ManifestFile manifest)
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
                if (s is Border border)
                {
                    border.Clip = new RectangleGeometry(new Rect(0, 0, border.ActualWidth, border.ActualHeight), 20, 20);
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

        private Task LoadManifestThumbnailAsync(ManifestFile manifest, Image image)
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

                    image.Dispatcher.Invoke(() => image.Source = bitmap);
                }
                catch
                {
                    // Ignore missing or invalid thumbnails
                }
            });
        }

        private async Task<bool> FetchAndCacheManifestThumbnailAsync(ManifestFile manifest, Image image)
        {
            try
            {
                if (!manifest.AppId.HasValue || manifest.AppId.Value <= 0)
                    return false;

                string cacheFileName = $"{manifest.AppId}_header.jpg";
                string cachedPath = Path.Combine(_thumbnailCacheFolder, cacheFileName);

                if (File.Exists(cachedPath))
                {
                    manifest.ThumbnailPath = new Uri(cachedPath, UriKind.Absolute).ToString();
                    await LoadManifestThumbnailAsync(manifest, image);
                    return true;
                }

                string[] imageFormats = new[]
                {
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{manifest.AppId}/header_292x136.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{manifest.AppId}/header_616x353.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{manifest.AppId}/header_1840x620.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{manifest.AppId}/capsule_231x87.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{manifest.AppId}/capsule_467x181.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{manifest.AppId}/header.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{manifest.AppId}/library_600x900.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{manifest.AppId}/capsule.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{manifest.AppId}/logo.png"
                };

                byte[]? downloadedData = null;
                foreach (var imageUrl in imageFormats)
                {
                    try
                    {
                        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var response = await _thumbnailHttpClient.GetAsync(imageUrl, cts.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            downloadedData = await response.Content.ReadAsByteArrayAsync();
                            if (downloadedData != null && downloadedData.Length > 0)
                                break;
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                if (downloadedData == null || downloadedData.Length == 0)
                {
                    try
                    {
                        string storeUrl = $"https://store.steampowered.com/app/{manifest.AppId}";
                        var storeResponse = await _thumbnailHttpClient.GetAsync(storeUrl);
                        if (storeResponse.IsSuccessStatusCode)
                        {
                            var html = await storeResponse.Content.ReadAsStringAsync();
                            var match = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]*property=[\"']og:image[\"'][^>]*content=[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (!match.Success)
                            {
                                match = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]*content=[\"']([^\"']+)[\"'][^>]*property=[\"']og:image[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            }

                            if (match.Success && Uri.IsWellFormedUriString(match.Groups[1].Value, UriKind.Absolute))
                            {
                                var assetUrl = match.Groups[1].Value;
                                using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
                                var assetResp = await _thumbnailHttpClient.GetAsync(assetUrl, cts2.Token);
                                if (assetResp.IsSuccessStatusCode)
                                {
                                    downloadedData = await assetResp.Content.ReadAsByteArrayAsync();
                                }
                            }
                        }
                    }
                    catch
                    {
                        // ignore failures from the store page fallback
                    }
                }

                if (downloadedData != null && downloadedData.Length > 0)
                {
                    await File.WriteAllBytesAsync(cachedPath, downloadedData);
                    manifest.ThumbnailPath = new Uri(cachedPath, UriKind.Absolute).ToString();
                    await LoadManifestThumbnailAsync(manifest, image);
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

        private void SeeMoreManifests_Click(object sender, RoutedEventArgs e)
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
                using (var memoryStream = DecryptStream(backupFilePath))
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

        private MemoryStream DecryptStream(string inputFile)
        {
            var memoryStream = new MemoryStream();
            using (var inStream = new FileStream(inputFile, FileMode.Open))
            {
                byte[] salt = new byte[SaltSize]; inStream.Read(salt, 0, salt.Length);
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256; byte[] iv = new byte[aes.IV.Length]; inStream.Read(iv, 0, iv.Length); aes.IV = iv;
                    var key = new Rfc2898DeriveBytes(GetBackupPassword(), salt, 10000, HashAlgorithmName.SHA256).GetBytes(aes.KeySize / 8); aes.Key = key;
                    using (var cryptoStream = new CryptoStream(inStream, aes.CreateDecryptor(), CryptoStreamMode.Read)) { cryptoStream.CopyTo(memoryStream); }
                }
            }
            memoryStream.Position = 0; return memoryStream;
        }

        private void InitializeSteamCheckTimer()
        {
            // Steam status is actively and efficiently monitored by MainShell
        }

        private void UpdateSteamStatus(bool forceUpdate = false)
        {
        }

        public static async Task<bool> IsDeviceBlockedAsync(string? deviceId = null)
        {
            try
            {
                string id = deviceId ?? GetDeviceId();
                if (string.IsNullOrWhiteSpace(id) || id == "UNKNOWN") return false;

                var http = SharedHttpClient.Instance;
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/blocked_devices?device_id=eq.{Uri.EscapeDataString(id)}&select=id";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Accept", "application/json");

                var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return false;

                var content = await response.Content.ReadAsStringAsync();
                return !string.IsNullOrWhiteSpace(content) && content != "[]";
            }
            catch
            {
                return false;
            }
        }

        public static async Task<bool> CheckDeviceTokenAsync()
        {
            try
            {
                string deviceId = GetDeviceId();
                if (!DeviceIdentifier.IsValidDeviceId(deviceId))
                    return false;

                if (await IsDeviceBlockedAsync(deviceId))
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
            catch
            {
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
                string deviceId = GetDeviceId();
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

        public static string GetDeviceId()
        {
            return DeviceIdentifier.GetDeviceId();
        }
    }
}
