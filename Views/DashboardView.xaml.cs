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
using System.Windows.Threading;
using Microsoft.Win32;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Management;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows.Input;

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
                _ = LoadDashboardStatsAsync();
                _ = LoadNewestManifestCardsAsync();
            }
            App.LanguageChanged += LanguageChanged_Handler;
            InitializeSteamCheckTimer();
            _ = CheckForUpdatesOnStartupAsync();
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
                        MessageBox.Show($"Failed to check for updates: {updateInfo.ErrorMessage}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    return;
                }

                if (updateInfo.HasUpdate)
                {
                    var dialog = new StyledMessageDialog(
                        "Update Available",
                        $"New Version Available!\n\nCurrent: {updateInfo.CurrentVersion}\nLatest: {updateInfo.LatestVersion}\n\n{updateInfo.ReleaseNotes}\n\nDownload and install the latest version?",
                        showCancel: true
                    );
                    dialog.Owner = Window.GetWindow(this);
                    dialog.PrimaryButton.Content = "Download";
                    dialog.SecondaryButton.Content = "Cancel";
                    var result = dialog.ShowDialog();
                    if (result == true && !string.IsNullOrEmpty(updateInfo.DownloadUrl)) await DownloadAndInstallUpdateAsync(updateInfo.DownloadUrl);
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
                    MessageBox.Show($"Failed to check for updates: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

            if (_steamCheckTimer != null)
            {
                _steamCheckTimer.Stop();
                _steamCheckTimer = null;
            }
        }

        private void LanguageChanged_Handler(object? sender, EventArgs e) => RefreshLanguageUI();

        public static async Task OpenProfileEditorAsync()
        {
            try
            {
                var profile = await LoadUserProfileForEditingAsync();
                if (profile == null)
                {
                    return;
                }

                var ownerWindow = GetProfileEditorOwnerWindow();
                await ShowProfileEditorDialogInternalAsync(profile, ownerWindow);
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

        private static async Task<UserProfile?> LoadUserProfileForEditingAsync()
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
            // Forward profile display name/status to global sidebar
            try
            {
                var win = Window.GetWindow(this) as MainShell;
                win?.GlobalSidebar?.SetProfile(_currentUserProfile?.DisplayName ?? "Set display name", string.IsNullOrWhiteSpace(_currentUserProfile?.DisplayName) ? "Profile loaded, but display name is empty." : string.Empty);
            }
            catch { }
        }

        private async Task LoadDashboardStatsAsync()
        {
            try
            {
                var hzManifestCountTask = FetchHZManifestFileCountAsync();
                var gameBypassCountTask = FetchR2FileCountAsync("gamebypass/");
                var onlineFixCountTask = FetchR2FileCountAsync("onlinefix/");

                await Task.WhenAll(hzManifestCountTask, gameBypassCountTask, onlineFixCountTask);

                UpdateDashboardStats(hzManifestCountTask.Result, gameBypassCountTask.Result, onlineFixCountTask.Result);
            }
            catch (Exception ex)
            {
                Logger.Log($"[DashboardView] LoadDashboardStatsAsync failed: {ex.Message}");
            }
        }

        private async Task<int> FetchHZManifestFileCountAsync()
        {
            try
            {
                return await SupabaseConfig.GetHzManifestFilesCountAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"[DashboardView] FetchHZManifestFileCountAsync failed: {ex.Message}");
                return 0;
            }
        }

        private async Task<int> FetchR2FileCountAsync(string folderPrefix)
        {
            try
            {
                var files = await SteamPluginManager.R2Config.ListFilesAsync(folderPrefix);
                return files?.Count ?? 0;
            }
            catch (Exception ex)
            {
                Logger.Log($"[DashboardView] FetchR2FileCountAsync failed for {folderPrefix}: {ex.Message}");
                return 0;
            }
        }

        private void UpdateDashboardStats(int hzManifestCount, int gameBypassCount, int onlineFixCount)
        {
            if (HZManifestFileCountText != null)
                HZManifestFileCountText.Text = hzManifestCount.ToString("N0");

            if (GameBypassFileCountText != null)
                GameBypassFileCountText.Text = gameBypassCount.ToString("N0");

            if (OnlineFixFileCountText != null)
                OnlineFixFileCountText.Text = onlineFixCount.ToString("N0");
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

        private void ViewAllManifests_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToHZManifest();
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

        private static async Task SaveUserProfileAsync(UserProfile profile)
        {
            try
            {
                var http = SharedHttpClient.Instance;
                var payload = new Dictionary<string, object?>
                {
                    ["device_id"] = profile.DeviceId,
                    ["display_name"] = profile.DisplayName,
                    ["bio"] = profile.Bio,
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

                    if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                    {
                        UpdateSidebarProfile(profile.DisplayName, "Device ID already exists. Contact support if this persists.");
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        UpdateSidebarProfile(profile.DisplayName, "Permission denied. API configuration may need updating.");
                    }
                    else
                    {
                        UpdateSidebarProfile(profile.DisplayName, "Failed to save profile.");
                    }
                    return;
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
            }
            catch (Exception ex)
            {
                Logger.Log($"[DashboardView] SaveUserProfileAsync failed: {ex.Message}");
                Logger.Log($"[DashboardView] Stack trace: {ex.StackTrace}");
                UpdateSidebarProfile(profile.DisplayName, "Failed to save profile.");
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

        private static async Task ShowProfileEditorDialogInternalAsync(UserProfile profile, Window? ownerWindow)
        {
            if (profile == null)
                return;

            var window = new Window
            {
                Title = "Customize Profile",
                Owner = ownerWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                Background = System.Windows.Media.Brushes.Transparent,
                AllowsTransparency = true
            };

            var accentBrush = Application.Current.TryFindResource("AccentBrush") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 138, 0));
            var foregroundBrush = Application.Current.TryFindResource("ForegroundBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
            var mutedBrush = Application.Current.TryFindResource("MutedForegroundBrush") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));
            var cardBrush = Application.Current.TryFindResource("CardBackgroundBrush") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 26, 34));
            var inputBackgroundBrush = Application.Current.TryFindResource("BackgroundBrush") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 20, 30));
            var borderBrush = Application.Current.TryFindResource("BorderBrush") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(44, 50, 64));
            var secondaryBrush = Application.Current.TryFindResource("SecondaryButtonBrush") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 42, 54));
            var hoverBrush = Application.Current.TryFindResource("HoverBrush") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 39, 54));

            var inputTextBoxStyle = new Style(typeof(TextBox));
            inputTextBoxStyle.Setters.Add(new Setter(Control.BackgroundProperty, inputBackgroundBrush));
            inputTextBoxStyle.Setters.Add(new Setter(Control.ForegroundProperty, foregroundBrush));
            inputTextBoxStyle.Setters.Add(new Setter(Control.BorderBrushProperty, borderBrush));
            inputTextBoxStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            inputTextBoxStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 10, 12, 10)));
            inputTextBoxStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            inputTextBoxStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Normal));
            inputTextBoxStyle.Setters.Add(new Setter(TextBox.CaretBrushProperty, accentBrush));
            inputTextBoxStyle.Setters.Add(new Setter(Control.SnapsToDevicePixelsProperty, true));
            inputTextBoxStyle.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));

            var focusTrigger = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focusTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, accentBrush));
            focusTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 24, 35))));
            focusTrigger.Setters.Add(new Setter(Control.EffectProperty, new DropShadowEffect { Color = System.Windows.Media.Color.FromArgb(96, 255, 138, 0), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.16 }));
            inputTextBoxStyle.Triggers.Add(focusTrigger);

            var nameTextBox = new TextBox
            {
                Text = profile.DisplayName ?? string.Empty,
                Height = 40,
                Margin = new Thickness(0, 6, 0, 18),
                Style = inputTextBoxStyle,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var bioTextBox = new TextBox
            {
                Text = profile.Bio ?? string.Empty,
                Height = 120,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Style = inputTextBoxStyle,
                Margin = new Thickness(0, 6, 0, 12)
            };

            var validationText = new TextBlock
            {
                Text = string.Empty,
                Foreground = System.Windows.Media.Brushes.Tomato,
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var buttonTemplate = new ControlTemplate(typeof(Button));
            var buttonRoot = new FrameworkElementFactory(typeof(Border), "buttonRoot");
            buttonRoot.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            buttonRoot.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            buttonRoot.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            buttonRoot.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            buttonRoot.SetValue(Border.SnapsToDevicePixelsProperty, true);
            buttonRoot.SetValue(Border.PaddingProperty, new Thickness(0));
            buttonRoot.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            buttonRoot.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Stretch);

            var buttonPanelTemplate = new FrameworkElementFactory(typeof(StackPanel));
            buttonPanelTemplate.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            buttonPanelTemplate.SetValue(StackPanel.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            buttonPanelTemplate.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);
            buttonPanelTemplate.SetValue(StackPanel.MarginProperty, new Thickness(14, 0, 14, 0));

            var iconCircle = new FrameworkElementFactory(typeof(Border));
            iconCircle.SetValue(Border.WidthProperty, 20.0);
            iconCircle.SetValue(Border.HeightProperty, 20.0);
            iconCircle.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            iconCircle.SetValue(Border.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255)));
            iconCircle.SetValue(Border.MarginProperty, new Thickness(0, 0, 8, 0));
            iconCircle.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            iconCircle.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);

            var iconText = new FrameworkElementFactory(typeof(TextBlock));
            iconText.SetBinding(TextBlock.TextProperty, new Binding("Tag") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            iconText.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            iconText.SetValue(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe MDL2 Assets"));
            iconText.SetValue(TextBlock.FontSizeProperty, 12.0);
            iconText.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            iconText.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            iconCircle.AppendChild(iconText);

            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.MarginProperty, new Thickness(0, 0, 0, 0));

            buttonPanelTemplate.AppendChild(iconCircle);
            buttonPanelTemplate.AppendChild(contentPresenter);
            buttonRoot.AppendChild(buttonPanelTemplate);
            buttonTemplate.VisualTree = buttonRoot;

            var buttonStyle = new Style(typeof(Button));
            buttonStyle.Setters.Add(new Setter(Control.TemplateProperty, buttonTemplate));
            buttonStyle.Setters.Add(new Setter(Control.ForegroundProperty, System.Windows.Media.Brushes.White));
            buttonStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            buttonStyle.Setters.Add(new Setter(Control.HeightProperty, 40.0));
            buttonStyle.Setters.Add(new Setter(Control.WidthProperty, Double.NaN));
            buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            buttonStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            buttonStyle.Setters.Add(new Setter(Control.RenderTransformProperty, new ScaleTransform(1.0, 1.0)));

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Control.OpacityProperty, 0.95));
            hoverTrigger.Setters.Add(new Setter(Control.RenderTransformProperty, new ScaleTransform(1.01, 1.01)));
            buttonStyle.Triggers.Add(hoverTrigger);

            var pressedTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressedTrigger.Setters.Add(new Setter(Control.OpacityProperty, 0.88));
            buttonStyle.Triggers.Add(pressedTrigger);

            var saveButtonStyle = new Style(typeof(Button), buttonStyle);
            saveButtonStyle.Setters.Add(new Setter(Control.BackgroundProperty, accentBrush));
            saveButtonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, accentBrush));
            saveButtonStyle.Setters.Add(new Setter(Control.ForegroundProperty, System.Windows.Media.Brushes.White));

            var cancelButtonStyle = new Style(typeof(Button), buttonStyle);
            cancelButtonStyle.Setters.Add(new Setter(Control.BackgroundProperty, secondaryBrush));
            cancelButtonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 255, 138, 0))));
            cancelButtonStyle.Setters.Add(new Setter(Control.ForegroundProperty, foregroundBrush));

            var saveButton = new Button
            {
                Content = "Save",
                Width = 110,
                Height = 40,
                Margin = new Thickness(0, 0, 0, 0),
                Style = saveButtonStyle,
                Tag = Application.Current.TryFindResource("Icon.Check")
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 110,
                Height = 40,
                Margin = new Thickness(0, 0, 12, 0),
                Style = cancelButtonStyle,
                Tag = Application.Current.TryFindResource("Icon.Cancel")
            };

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            buttonPanel.Children.Add(cancelButton);
            buttonPanel.Children.Add(saveButton);

            var headerIcon = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(14),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(24, 255, 138, 0)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 255, 138, 0)),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "\uE77B",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 20,
                    Foreground = accentBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };

            var headerStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 22)
            };
            headerStack.Children.Add(headerIcon);

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            titleStack.Children.Add(new TextBlock
            {
                Text = "Customize Profile",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = foregroundBrush
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "Customize your profile information.",
                FontSize = 12,
                Foreground = mutedBrush,
                Margin = new Thickness(0, 6, 0, 0)
            });
            headerStack.Children.Add(titleStack);

            var panel = new StackPanel();
            panel.Children.Add(headerStack);
            panel.Children.Add(new TextBlock { Text = "Display Name", FontSize = 12, FontWeight = FontWeights.Medium, Foreground = mutedBrush, Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(nameTextBox);
            panel.Children.Add(new TextBlock { Text = "Bio", FontSize = 12, FontWeight = FontWeights.Medium, Foreground = mutedBrush, Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(bioTextBox);
            panel.Children.Add(validationText);
            panel.Children.Add(buttonPanel);

            var border = new Border
            {
                Width = 420,
                Padding = new Thickness(24),
                CornerRadius = new CornerRadius(18),
                Background = cardBrush,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 255, 138, 0)),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = System.Windows.Media.Color.FromArgb(90, 0, 0, 0), BlurRadius = 28, ShadowDepth = 8, Opacity = 0.18 },
                Child = panel
            };

            var outer = new Grid
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 0, 0, 0)),
                Children = { border }
            };
            outer.HorizontalAlignment = HorizontalAlignment.Stretch;
            outer.VerticalAlignment = VerticalAlignment.Stretch;
            border.HorizontalAlignment = HorizontalAlignment.Center;
            border.VerticalAlignment = VerticalAlignment.Center;

            window.Content = outer;

            cancelButton.Click += (_, _) => window.Close();
            saveButton.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(nameTextBox.Text))
                {
                    validationText.Text = "Display Name tidak boleh kosong.";
                    validationText.Visibility = Visibility.Visible;
                    return;
                }

                validationText.Visibility = Visibility.Collapsed;
                saveButton.IsEnabled = false;
                saveButton.Content = "Saving...";

                profile.DisplayName = nameTextBox.Text.Trim();
                profile.Bio = bioTextBox.Text.Trim();

                await SaveUserProfileAsync(profile);

                if (_currentInstance != null)
                {
                    _currentInstance._currentUserProfile = profile;
                }

                window.Close();
            };

            window.ShowDialog();
        }

        private class UserProfile
        {
            public int? Id { get; set; }

            [JsonPropertyName("device_id")]
            public string DeviceId { get; set; } = string.Empty;

            [JsonPropertyName("display_name")]
            public string DisplayName { get; set; } = string.Empty;

            public string Bio { get; set; } = string.Empty;
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
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Logger.Log($"[DashboardView] GameBypass navigation error: {ex.Message}");
            }
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog { Filter = "SPM Backup (*.spmb)|*.spmb|All Files (*.*)|*.*", Title = "Restore Game Backup", Multiselect = false };
            if (openFileDialog.ShowDialog() != true) return;
            try
            {
                string backupFilePath = openFileDialog.FileName;
                if (!File.Exists(backupFilePath)) { MessageBox.Show("Selected backup file does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error); return; }
                var result = MessageBox.Show($"This will restore the backup from:\n{Path.GetFileName(backupFilePath)}\n\nExisting plugin and manifest files will be overwritten.\nContinue?", "Confirm Restore", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
                _ = RestoreBackupAsync(backupFilePath);
            }
            catch (Exception ex) { MessageBox.Show($"Error opening backup file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
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
                MessageBox.Show($"Failed to check for updates: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void About_Click(object sender, RoutedEventArgs e)
        {
            var version = GetInformationalVersion();
            string licenseStatus = await GetLicenseStatusAsync();
            var dialog = new StyledMessageDialog("About HZ Lua Manager", $"HZ Lua Manager\nVersion {version}\n\n{licenseStatus}\n\nYour comprehensive lua management solution.");
            dialog.Owner = Window.GetWindow(this);
            dialog.ShowDialog();
        }

        private static string GetInformationalVersion()
        {
            var infoVersion = System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVersion))
                return infoVersion;

            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
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
                    MessageBox.Show($"Failed to download and install update:\n{message}", "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during update: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                string steamPath = SteamHelper.GetSteamPath(); if (string.IsNullOrEmpty(steamPath)) { progressWindow.Close(); MessageBox.Show("Could not find Steam installation directory.", "Error", MessageBoxButton.OK, MessageBoxImage.Error); return; }
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
                    MessageBox.Show($"Backup restored successfully!\n\nLua Files: {luaFiles}\nManifest Files: {manifestFiles}\n\nYour game plugins and manifests have been restored.", "Restore Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex) { MessageBox.Show($"Failed to restore backup:\n{ex.Message}", "Restore Error", MessageBoxButton.OK, MessageBoxImage.Error); }
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

        private void InitializeSteamCheckTimer() { _steamCheckTimer = new DispatcherTimer(); _steamCheckTimer.Interval = TimeSpan.FromSeconds(10); _steamCheckTimer.Tick += SteamCheckTimer_Tick; _steamCheckTimer.Start(); UpdateSteamStatus(); }

        private void SteamCheckTimer_Tick(object? sender, EventArgs e) => UpdateSteamStatus();

        private void UpdateSteamStatus(bool forceUpdate = false)
        {
            bool isSteamRunning = SteamHelper.IsSteamRunning();
            if (forceUpdate || _isSteamRunning != isSteamRunning)
            {
                _isSteamRunning = isSteamRunning;
                // Removed dashboard Steam button labels; sidebar shows the current actions now.
            }
        }

        public static async Task<bool> CheckDeviceTokenAsync()
        {
            try
            {
                string deviceId = GetDeviceId();
                var http = SharedHttpClient.Instance;
                var queryDeviceId = Uri.EscapeDataString(deviceId);
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/device_tokens?or=(device_id.eq.{queryDeviceId},device_ids.cs.{{{queryDeviceId}}})&select=device_id,device_ids,is_active";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Accept", "application/json");

                var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
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

        private static string GetDeviceId()
        {
            try
            {
                // Get stable hardware ID (motherboard serial or MAC address)
                // Using ONLY hardware ID so token remains valid even if user changes device name
                return GetMotherboardHardwareId();
            }
            catch { return "UNKNOWN"; }
        }

        private static string GetMotherboardHardwareId()
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
                            return serialNumber;
                        }
                    }
                }

                // Fallback: Get first stable MAC address if motherboard ID fails or is invalid
                var macAddress = NetworkInterface
                    .GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                                  !string.IsNullOrEmpty(nic.GetPhysicalAddress().ToString()))
                    .Select(nic => nic.GetPhysicalAddress().ToString())
                    .FirstOrDefault() ?? "UNKNOWN";

                return macAddress;
            }
            catch { return "UNKNOWN"; }
        }

        private static bool IsValidSerialNumber(string serialNumber)
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
                return false;

            // Reject if only contains zeros or hyphens/spaces
            if (string.IsNullOrWhiteSpace(serialNumber.Replace("0", "").Replace("-", "").Replace(" ", "")))
                return false;

            return true;
        }
    }
}
