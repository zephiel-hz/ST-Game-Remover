using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SteamPluginManager.Models;
using SteamPluginManager.Services.API;
using SteamPluginManager.Services.Profile;

namespace SteamPluginManager.Views
{
    public partial class MainShell : Window
    {
        private bool _isSidebarExpanded = true;
        private bool _isMaximized = false;
        private bool _isTransitioning = false;
        private bool _isInspectorMaximized = false;
        private Point _mouseDownScreenPoint = new();
        private readonly bool? _startupLicenseVerified;

        // View instance caching for instant (0 ms) tab switching without losing state or scroll position
        private readonly Dictionary<string, object> _viewCache = new(StringComparer.OrdinalIgnoreCase);

        // System monitoring
        private DispatcherTimer? _steamMonitorTimer;
        private FileSystemWatcher? _unlockerWatcher;
        private FileSystemWatcher? _configLuaWatcher;
        private FileSystemWatcher? _depotcacheWatcher;
        private DispatcherTimer? _libraryWatcherDebounceTimer;
        private System.Windows.Media.Effects.Effect? _originalMainBorderEffect;

        // Theme switching cycle
        private static readonly string[] AvailableThemeList = new[] { "Dark", "DarkBlue", "DarkPurple", "DarkGreen", "DarkRed", "DarkPink", "DarkYellow" };
        private int _currentThemeIndex = 0;

        public MainShell(bool? startupLicenseVerified = null)
        {
            InitializeComponent();
            VersionBadgeText.Text = AppInfo.FormattedVersion;
            ApplyThemeIcon();
            App.ThemeChanged += ThemeChanged_Handler;
            _startupLicenseVerified = startupLicenseVerified;
            Closing += MainShell_Closing;
            Closed += MainShell_Closed;
        }

        private bool _isClosingHandled = false;

        private async void MainShell_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isClosingHandled) return;

            e.Cancel = true;
            _isClosingHandled = true;

            try
            {
                string deviceId = DashboardView.GetDeviceId();
                string? currentUsername = HeaderProfileName?.Text;

                // 1. Broadcast user offline immediately to connected peers (<10ms)
                CommunityChatRealtimeClient.BroadcastUserOffline(currentUsername, deviceId);

                // 2. Set last_seen to past timestamp in Supabase so subsequent queries see user offline immediately (max 800ms)
                using var cts = new System.Threading.CancellationTokenSource(800);
                await CommunityChatService.SetUserOfflineAsync(deviceId, cts.Token);
            }
            catch { }
            finally
            {
                Close();
            }
        }

        private void MainShell_Closed(object? sender, EventArgs e)
        {
            try
            {
                CommunityChatRealtimeClient.OnMessageReceived -= HandleChatNotification;
                _notificationToastTimer?.Stop();
                _steamMonitorTimer?.Stop();
                _libraryWatcherDebounceTimer?.Stop();
                _unlockerWatcher?.Dispose();
                _configLuaWatcher?.Dispose();
                _depotcacheWatcher?.Dispose();
            }
            catch { }
        }

        private void ThemeChanged_Handler(object? sender, EventArgs e)
        {
            ApplyThemeIcon();
        }

        private void ApplyThemeIcon()
        {
            try
            {
                var icon = new BitmapImage();
                icon.BeginInit();
                icon.UriSource = new Uri(App.GetThemeIconPath(), UriKind.Absolute);
                icon.CacheOption = BitmapCacheOption.OnLoad;
                icon.EndInit();
                icon.Freeze();
                MainShellLogoImage.Source = icon;
                Icon = BitmapFrame.Create(icon);
            }
            catch { }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _originalMainBorderEffect = MainShellBorder?.Effect;
            WindowNavigator.RegisterMainShell(this);
            this.StateChanged += (s, args) => UpdateMaximizeButton();

            // Hidden MainWindow for legacy DataContext
            if (this.DataContext == null)
            {
                try
                {
                    var hiddenMain = new MainWindow();
                    hiddenMain.Hide();
                    WindowNavigator.RegisterMainWindow(hiddenMain);
                    this.DataContext = hiddenMain;
                }
                catch (Exception ex)
                {
                    try { Logger.Log($"[MainShell] Failed to create hidden MainWindow for DataContext: {ex.Message}"); } catch { }
                }
            }

            // Initialize monitors
            InitializeSteamMonitor();
            StartUnlockerWatcher();
            StartLibraryWatcher();
            UpdateUnlockerToggleState();
            InitializeChatNotificationListener();

            ProfilePictureCacheService.OnAvatarUpdated -= HandleAvatarUpdated;
            ProfilePictureCacheService.OnAvatarUpdated += HandleAvatarUpdated;

            _ = LoadUserProfileHeaderAsync();

            await CheckStartupActivationAsync();
        }

        public void UpdateHeaderProfileVisibility(bool isVisible)
        {
            Dispatcher.Invoke(() =>
            {
                if (HeaderProfilePill != null)
                {
                    HeaderProfilePill.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                }

                if (GlobalChatWidget != null)
                {
                    GlobalChatWidget.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                    if (!isVisible)
                    {
                        GlobalChatWidget.Collapse();
                    }
                }
            });
        }

        public async Task LoadUserProfileHeaderAsync()
        {
            try
            {
                bool hasActiveToken = await DashboardView.CheckDeviceTokenAsync();
                if (!hasActiveToken)
                {
                    UpdateHeaderProfileVisibility(false);
                    return;
                }

                UpdateHeaderProfileVisibility(true);

                var profile = await DashboardView.LoadUserProfileForEditingAsync();
                if (profile != null)
                {
                    GlobalSidebar?.SetAdminVisibility(profile.IsAdmin);

                    if (!string.IsNullOrWhiteSpace(profile.DisplayName))
                    {
                        System.Windows.Media.ImageSource? avatarImg = null;
                        if (profile.Id.HasValue && !string.IsNullOrWhiteSpace(profile.AvatarUrl))
                        {
                            avatarImg = await Services.Profile.ProfilePictureCacheService.GetAvatarAsync(profile.Id.Value, profile.AvatarUrl);
                        }
                        SetHeaderProfile(profile.DisplayName, avatarImg);
                    }
                }
            }
            catch (Exception ex)
            {
                try { Logger.Log($"[MainShell] Failed to load user profile: {ex.Message}"); } catch { }
            }
        }

        private async Task CheckStartupActivationAsync()
        {
            try
            {
                bool isVerified = _startupLicenseVerified ?? await DashboardView.CheckDeviceTokenAsync();
                if (!isVerified)
                {
                    UpdateHeaderProfileVisibility(false);
                    WindowNavigator.NextViewAfterVerify = "Dashboard";
                    WindowNavigator.NavigateToVerifyToken();
                    return;
                }

                UpdateHeaderProfileVisibility(true);
                NavigateToDashboard();

                if (this.DataContext is MainWindow mw)
                {
                    Task.Delay(500).ContinueWith(_ => Dispatcher.Invoke(() => mw.ShowWelcomeScreenIfNeeded()));
                }
            }
            catch (Exception ex)
            {
                try { Logger.Log($"[MainShell] Failed startup activation check: {ex.Message}"); } catch { }
                UpdateHeaderProfileVisibility(false);
                NavigateToDashboard();
            }
        }

        #region Navigation Methods (Cached for Instant Switching)

        public void NavigateToDashboard()
        {
            TitleText.Text = "HZ Lua Manager";
            if (!_viewCache.TryGetValue("Dashboard", out var view))
            {
                var db = new DashboardView();
                db.DataContext = this.DataContext;
                view = db;
                _viewCache["Dashboard"] = view;
            }
            TransitionToView(view);
            try { GlobalSidebar?.SetActiveMenu("Dashboard"); } catch { }
        }

        public void NavigateToGameLibrary()
        {
            TitleText.Text = "Game Library";
            if (!_viewCache.TryGetValue("Library", out var view))
            {
                var lv = new GameLibraryView();
                lv.DataContext = this.DataContext;
                view = lv;
                _viewCache["Library"] = view;
            }
            TransitionToView(view);
            try { GlobalSidebar?.SetActiveMenu("Library"); } catch { }
        }

        public void NavigateToSettings()
        {
            TitleText.Text = "Settings";
            if (!_viewCache.TryGetValue("Settings", out var view))
            {
                var sv = new SettingsView();
                sv.DataContext = this.DataContext;
                view = sv;
                _viewCache["Settings"] = view;
            }
            TransitionToView(view);
            try { GlobalSidebar?.SetActiveMenu("Settings"); } catch { }
        }

        public void NavigateToDev()
        {
            TitleText.Text = "Developer Console";
            if (!_viewCache.TryGetValue("Dev", out var view))
            {
                var dv = new DevView();
                dv.DataContext = this.DataContext;
                view = dv;
                _viewCache["Dev"] = view;
            }
            TransitionToView(view);
            try { GlobalSidebar?.SetActiveMenu("Dev"); } catch { }
        }

        public void NavigateToSaweria()
        {
            TitleText.Text = "Saweria";
            if (!_viewCache.TryGetValue("Saweria", out var view))
            {
                view = new SaweriaView();
                _viewCache["Saweria"] = view;
            }
            TransitionToView(view);
            try { GlobalSidebar?.SetActiveMenu("Saweria"); } catch { }
        }

        public void NavigateToGenerateToken()
        {
            TitleText.Text = "Get Token";
            TransitionToView(new GenerateTokenView());
            try { GlobalSidebar?.SetActiveMenu("GetToken"); } catch { }
        }

        public void NavigateToHZManifest()
        {
            NavigateToHZManifest(forceRefresh: false);
        }

        public void NavigateToHZManifest(bool forceRefresh)
        {
            TitleText.Text = "HZ Manifest";
            if (!_viewCache.TryGetValue("HZManifest", out var view))
            {
                var hzManifestView = new HZManifestView();
                hzManifestView.SetForceRefresh(true);
                hzManifestView.SetRequiresDeviceVerificationOnLoad(true);
                view = hzManifestView;
                _viewCache["HZManifest"] = view;
            }
            else if (forceRefresh && view is HZManifestView mv)
            {
                _ = HZManifestView.RefreshActiveManifestViewAsync(true);
            }

            TransitionToView(view);
            try { GlobalSidebar?.SetActiveMenu("HZManifest"); } catch { }
        }

        public void NavigateToHZManifestWithSort(string sortOrder, bool forceRefresh = false)
        {
            TitleText.Text = "HZ Manifest";
            bool isNew = false;
            if (!_viewCache.TryGetValue("HZManifest", out var view))
            {
                var hzManifestView = new HZManifestView();
                hzManifestView.SetForceRefresh(true);
                hzManifestView.SetRequiresDeviceVerificationOnLoad(true);
                view = hzManifestView;
                _viewCache["HZManifest"] = view;
                isNew = true;
            }

            if (view is HZManifestView mv)
            {
                mv.SetSortOrder(sortOrder);
                if (!isNew && forceRefresh)
                {
                    _ = HZManifestView.RefreshActiveManifestViewAsync(true);
                }
            }

            TransitionToView(view);
            try { GlobalSidebar?.SetActiveMenu("HZManifest"); } catch { }
        }

        public void NavigateToOnlineFix()
        {
            TitleText.Text = "Online Fix";
            if (!_viewCache.TryGetValue("OnlineFix", out var view))
            {
                view = new OnlineFixView();
                _viewCache["OnlineFix"] = view;
            }
            TransitionToView(view);
            try { GlobalSidebar?.SetActiveMenu("OnlineFix"); } catch { }
        }

        public void RefreshOnlineFixView()
        {
            Dispatcher.Invoke(async () =>
            {
                try
                {
                    OnlineFixView.InvalidateCache();
                    if (_viewCache.TryGetValue("OnlineFix", out var cachedView) && cachedView is OnlineFixView ofView)
                    {
                        await ofView.ReloadFilesAsync(forceRefresh: true);
                    }
                    else if (OnlineFixView.ActiveInstance != null)
                    {
                        await OnlineFixView.ActiveInstance.ReloadFilesAsync(forceRefresh: true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[MainShell] RefreshOnlineFixView error: {ex.Message}");
                }
            });
        }

        public void NavigateToGameBypass()
        {
            TitleText.Text = "Game Bypass";
            if (!_viewCache.TryGetValue("GameBypass", out var view))
            {
                view = new GameBypassView();
                _viewCache["GameBypass"] = view;
            }
            TransitionToView(view);
            try { GlobalSidebar?.SetActiveMenu("GameBypass"); } catch { }
        }

        public void RefreshGameLibrary()
        {
            if (_viewCache.TryGetValue("Library", out var view) && view is GameLibraryView glv)
            {
                glv.Refresh();
            }
            else if (ContentArea.Content is GameLibraryView currentGlv)
            {
                currentGlv.Refresh();
            }
        }

        #endregion

        #region Master-Detail Sliding Inspector Drawer

        public void OpenInspector(object detailView, string? subtitleBadge = null, string title = "Game Details")
        {
            if (InspectorDrawer == null || InspectorContent == null) return;

            InspectorTitleText.Text = !string.IsNullOrWhiteSpace(title) ? title : "Game Details";

            if (!string.IsNullOrWhiteSpace(subtitleBadge))
            {
                var cleanBadge = subtitleBadge.Trim();
                if (cleanBadge.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    cleanBadge = cleanBadge.Substring(0, cleanBadge.Length - 4);
                }
                InspectorBadgeText.Text = cleanBadge;
                InspectorBadgeBorder.Visibility = Visibility.Visible;
            }
            else
            {
                InspectorBadgeBorder.Visibility = Visibility.Collapsed;
            }

            InspectorContent.Content = detailView;
            InspectorDrawer.Visibility = Visibility.Visible;
            if (InspectorColumn != null)
            {
                InspectorColumn.Width = _isInspectorMaximized ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            }
        }

        public void CloseInspector()
        {
            if (InspectorDrawer == null) return;

            // Reset full view if it was maximized
            if (_isInspectorMaximized)
            {
                ToggleInspectorMaximized();
            }

            InspectorDrawer.Visibility = Visibility.Collapsed;
            if (InspectorColumn != null)
            {
                InspectorColumn.Width = GridLength.Auto;
            }
            if (InspectorContent != null)
            {
                InspectorContent.Content = null;
            }
        }

        private void InspectorCloseBtn_Click(object sender, RoutedEventArgs e)
        {
            CloseInspector();
        }

        private void InspectorMaximizeBtn_Click(object sender, RoutedEventArgs e)
        {
            ToggleInspectorMaximized();
        }

        private void ToggleInspectorMaximized()
        {
            _isInspectorMaximized = !_isInspectorMaximized;
            if (MainViewColumn != null)
            {
                MainViewColumn.Width = _isInspectorMaximized ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            }
            if (InspectorColumn != null)
            {
                InspectorColumn.Width = _isInspectorMaximized ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            }
            if (InspectorDrawer != null)
            {
                InspectorDrawer.Width = _isInspectorMaximized ? double.NaN : 420;
                InspectorDrawer.HorizontalAlignment = _isInspectorMaximized ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            }
            if (InspectorMaximizeBtn != null)
            {
                InspectorMaximizeBtn.Content = _isInspectorMaximized ? "❐" : "⛶";
                InspectorMaximizeBtn.ToolTip = _isInspectorMaximized ? "Restore Split View" : "Maximize to Full View";
            }
        }

        #endregion

        #region Global Centered Modal System

        public OperationProgressModalView GlobalProgressModal { get; } = new OperationProgressModalView();
        public RequestGameModalView GlobalRequestModal { get; } = new RequestGameModalView();
        public CustomizeProfileModalView GlobalProfileModal { get; } = new CustomizeProfileModalView();

        public void ShowGlobalModal(UIElement modalContent)
        {
            Dispatcher.Invoke(() =>
            {
                if (GlobalModalContent != null && GlobalModalHost != null)
                {
                    GlobalModalContent.Content = modalContent;
                    GlobalModalHost.Visibility = Visibility.Visible;
                }
            });
        }

        public void HideGlobalModal()
        {
            Dispatcher.Invoke(() =>
            {
                if (GlobalModalHost != null)
                {
                    GlobalModalHost.Visibility = Visibility.Collapsed;
                }
                if (GlobalModalContent != null)
                {
                    GlobalModalContent.Content = null;
                }
            });
        }

        #endregion



        #region Steam Monitoring & Unlocker Sync

        private void InitializeSteamMonitor()
        {
            _steamMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _steamMonitorTimer.Tick += (s, e) => UpdateSteamStatus();
            _steamMonitorTimer.Start();
            UpdateSteamStatus();
        }

        private async void UpdateSteamStatus()
        {
            try
            {
                bool isRunning = await Task.Run(() => SteamHelper.IsSteamRunning());
                Dispatcher.Invoke(() =>
                {
                    if (SteamStatusDot != null)
                    {
                        SteamStatusDot.Fill = new SolidColorBrush(isRunning ? Color.FromRgb(16, 185, 129) : Color.FromRgb(239, 68, 68));
                    }
                    if (SteamStatusText != null)
                    {
                        SteamStatusText.Text = isRunning ? "Steam Active" : "Steam Inactive";
                    }
                    if (SteamStatusPod != null)
                    {
                        SteamStatusPod.ToolTip = isRunning ? "Steam is running (Click to Restart)" : "Steam is not running (Click to Launch)";
                    }
                });
            }
            catch { }
        }

        private void SteamStatusPod_Click(object sender, MouseButtonEventArgs e)
        {
            if (SteamHelper.IsSteamRunning())
            {
                RestartSteam();
            }
            else
            {
                SteamHelper.LaunchSteam();
                UpdateSteamStatus();
            }
        }

        private void StartUnlockerWatcher()
        {
            try
            {
                string? steamPath = SteamHelper.GetSteamPath();
                if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
                    return;

                _unlockerWatcher?.Dispose();
                _unlockerWatcher = new FileSystemWatcher(steamPath)
                {
                    Filter = "OpenSteamTool.dll*",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false
                };

                _unlockerWatcher.Created += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateUnlockerToggleState));
                _unlockerWatcher.Deleted += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateUnlockerToggleState));
                _unlockerWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateUnlockerToggleState));
                _unlockerWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateUnlockerToggleState));
                _unlockerWatcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        private void StartLibraryWatcher()
        {
            try
            {
                string? steamPath = SteamHelper.GetSteamPath();
                if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
                    return;

                string configPath = Path.Combine(steamPath, "config");
                string depotcachePath = Path.Combine(steamPath, "depotcache");

                Directory.CreateDirectory(configPath);
                Directory.CreateDirectory(depotcachePath);

                _libraryWatcherDebounceTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(800)
                };
                _libraryWatcherDebounceTimer.Tick += (s, e) =>
                {
                    _libraryWatcherDebounceTimer.Stop();
                    Logger.Log("[LibraryWatcher] Lua/Manifest file changes detected on disk. Auto-refreshing library...");
                    WindowNavigator.RefreshGameList();
                    RefreshGameLibrary();
                };

                Action triggerDebounce = () =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _libraryWatcherDebounceTimer.Stop();
                        _libraryWatcherDebounceTimer.Start();
                    }));
                };

                FileSystemEventHandler onFileChanged = (s, e) =>
                {
                    string ext = Path.GetExtension(e.FullPath);
                    if (ext.Equals(".lua", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".manifest", StringComparison.OrdinalIgnoreCase))
                    {
                        triggerDebounce();
                    }
                };

                RenamedEventHandler onFileRenamed = (s, e) =>
                {
                    string ext = Path.GetExtension(e.FullPath);
                    string oldExt = Path.GetExtension(e.OldFullPath);
                    if (ext.Equals(".lua", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".manifest", StringComparison.OrdinalIgnoreCase) ||
                        oldExt.Equals(".lua", StringComparison.OrdinalIgnoreCase) ||
                        oldExt.Equals(".manifest", StringComparison.OrdinalIgnoreCase))
                    {
                        triggerDebounce();
                    }
                };

                // Watch config directory (includes config/stplug-in, config/lua, config/depotcache)
                _configLuaWatcher?.Dispose();
                _configLuaWatcher = new FileSystemWatcher(configPath)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                    IncludeSubdirectories = true
                };

                _configLuaWatcher.Created += onFileChanged;
                _configLuaWatcher.Changed += onFileChanged;
                _configLuaWatcher.Deleted += onFileChanged;
                _configLuaWatcher.Renamed += onFileRenamed;
                _configLuaWatcher.EnableRaisingEvents = true;

                // Watch depotcache directory (depotcache/*.manifest)
                _depotcacheWatcher?.Dispose();
                _depotcacheWatcher = new FileSystemWatcher(depotcachePath)
                {
                    Filter = "*.manifest",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false
                };

                _depotcacheWatcher.Created += onFileChanged;
                _depotcacheWatcher.Changed += onFileChanged;
                _depotcacheWatcher.Deleted += onFileChanged;
                _depotcacheWatcher.Renamed += onFileRenamed;
                _depotcacheWatcher.EnableRaisingEvents = true;

                Logger.Log("[LibraryWatcher] FileSystemWatchers started for Steam lua and manifest directories");
            }
            catch (Exception ex)
            {
                Logger.Log($"[LibraryWatcher] Failed to start watchers: {ex.Message}");
            }
        }

        private void UpdateUnlockerToggleState()
        {
            try
            {
                if (HeaderUnlockerToggle != null)
                {
                    HeaderUnlockerToggle.IsChecked = UnlockerRegistryHelper.IsUnlockerEnabled();
                }
            }
            catch { }
        }

        private void HeaderUnlockerToggle_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                UnlockerRegistryHelper.SetUnlockerEnabled(true);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to enable unlocker: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void HeaderUnlockerToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            try
            {
                UnlockerRegistryHelper.SetUnlockerEnabled(false);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to disable unlocker: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void HeaderThemeBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _currentThemeIndex = (_currentThemeIndex + 1) % AvailableThemeList.Length;
                string theme = AvailableThemeList[_currentThemeIndex];
                ThemePreferences.SaveThemePreference(theme);

                var app = Application.Current;
                ResourceDictionary? existing = null;
                foreach (var dict in app.Resources.MergedDictionaries)
                {
                    if (dict.Source?.OriginalString?.Contains("Themes/") == true)
                    {
                        existing = dict;
                        break;
                    }
                }
                if (existing != null) app.Resources.MergedDictionaries.Remove(existing);

                string themeFile = $"Themes/{theme}.xaml";
                App.SetThemeIcon(theme);
                var themeDict = new ResourceDictionary { Source = new Uri(themeFile, UriKind.Relative) };
                app.Resources.MergedDictionaries.Add(themeDict);
                App.RaiseThemeChanged();
            }
            catch (Exception ex)
            {
                Logger.Log($"[MainShell] Failed to cycle theme: {ex.Message}");
            }
        }

        private async void HeaderProfilePill_Click(object sender, MouseButtonEventArgs e)
        {
            await DashboardView.OpenProfileEditorAsync();
        }

        public void SetHeaderProfile(string displayName, System.Windows.Media.ImageSource? avatarImage = null)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return;
            Dispatcher.Invoke(() =>
            {
                if (HeaderProfileName != null) HeaderProfileName.Text = displayName;
                if (HeaderAvatarText != null && displayName.Length > 0) HeaderAvatarText.Text = displayName[0].ToString().ToUpperInvariant();
                if (HeaderAvatarImage != null)
                {
                    if (avatarImage != null)
                    {
                        HeaderAvatarImage.Source = avatarImage;
                        HeaderAvatarImage.Visibility = Visibility.Visible;
                        if (HeaderAvatarText != null) HeaderAvatarText.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        HeaderAvatarImage.Source = null;
                        HeaderAvatarImage.Visibility = Visibility.Collapsed;
                        if (HeaderAvatarText != null) HeaderAvatarText.Visibility = Visibility.Visible;
                    }
                }
            });
        }

        private void HandleAvatarUpdated(int userId, System.Windows.Media.ImageSource? newAvatar)
        {
            Dispatcher.Invoke(async () =>
            {
                try
                {
                    var profile = await DashboardView.LoadUserProfileForEditingAsync();
                    if (profile != null && profile.Id == userId)
                    {
                        SetHeaderProfile(profile.DisplayName, newAvatar);
                    }
                }
                catch { }
            });
        }

        #endregion

        #region Keyboard Shortcuts

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Escape closes inspector
            if (e.Key == Key.Escape && InspectorDrawer != null && InspectorDrawer.Visibility == Visibility.Visible)
            {
                CloseInspector();
                e.Handled = true;
                return;
            }
        }

        #endregion

        #region Steam & Updates Utilities

        public void RestartSteam()
        {
            try
            {
                if (SteamHelper.IsSteamRunning())
                {
                    SteamHelper.RestartSteam();
                }
                else
                {
                    SteamHelper.LaunchSteam();
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to restart Steam: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void CheckForUpdates()
        {
            _ = CheckForUpdatesAsync();
        }

        public void ShowAboutDialog()
        {
            var version = UpdateChecker.GetCurrentVersion();
            var dialog = new StyledMessageDialog("About HZ Lua Manager", $"HZ Lua Manager\nVersion {version}\n\nYour comprehensive lua management solution.");
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                var updateInfo = await UpdateChecker.CheckForUpdatesAsync();
                if (updateInfo.ErrorMessage != null)
                {
                    ModernMessageBox.Show($"Failed to check for updates: {updateInfo.ErrorMessage}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (updateInfo.HasUpdate)
                {
                    bool shouldUpdate = UpdateAvailableDialog.ShowUpdate(this, updateInfo);
                    if (shouldUpdate && !string.IsNullOrEmpty(updateInfo.DownloadUrl))
                    {
                        var (success, message) = await UpdateChecker.DownloadAndInstallUpdateAsync(updateInfo.DownloadUrl);
                        if (success)
                        {
                            return;
                        }
                        else
                        {
                            ModernMessageBox.Show($"Failed to download and install update: {message}", "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
                else
                {
                    var dialog = new StyledMessageDialog("Up to Date", "You are already running the latest version.");
                    dialog.Owner = this;
                    dialog.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"An error occurred while checking for updates: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region View Transition & Layout

        public void TransitionToView(object newContent)
        {
            if (_isTransitioning) return;
            _isTransitioning = true;

            try
            {
                ApplyLayoutForContent(newContent);

                if (ContentArea.Content != null && ContentArea.Content != newContent)
                {
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120));
                    fadeOut.Completed += async (s, e) =>
                    {
                        try
                        {
                            ContentArea.Content = newContent;
                            ContentArea.UpdateLayout();
                            TriggerViewLayoutRecalculation(newContent);
                            await Task.Delay(10);

                            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
                            fadeIn.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                            fadeIn.Completed += (s2, e2) =>
                            {
                                ContentArea.Opacity = 1.0;
                                ContentArea.BeginAnimation(OpacityProperty, null);
                                _isTransitioning = false;
                            };
                            ContentArea.BeginAnimation(OpacityProperty, fadeIn);
                        }
                        catch
                        {
                            _isTransitioning = false;
                        }
                    };
                    ContentArea.BeginAnimation(OpacityProperty, fadeOut);
                }
                else
                {
                    ContentArea.Content = newContent;
                    ContentArea.UpdateLayout();
                    TriggerViewLayoutRecalculation(newContent);
                    ContentArea.Opacity = 0;
                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
                    fadeIn.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                    fadeIn.Completed += (s, e) =>
                    {
                        ContentArea.Opacity = 1.0;
                        ContentArea.BeginAnimation(OpacityProperty, null);
                        _isTransitioning = false;
                    };
                    ContentArea.BeginAnimation(OpacityProperty, fadeIn);
                }
            }
            catch
            {
                _isTransitioning = false;
            }
        }

        private void ApplyLayoutForContent(object newContent)
        {
            bool isVerifyView = newContent is VerifyTokenView;
            bool isGenerateTokenView = newContent is GenerateTokenView;
            bool isDevView = newContent is DevView;

            if (GlobalChatWidget != null)
            {
                if (isDevView || isVerifyView || isGenerateTokenView)
                {
                    GlobalChatWidget.Visibility = Visibility.Collapsed;
                    GlobalChatWidget.Collapse();
                }
                else
                {
                    GlobalChatWidget.Visibility = Visibility.Visible;
                }
            }

            if (isVerifyView || isGenerateTokenView)
            {
                try
                {
                    GlobalSidebar.Visibility = Visibility.Collapsed;
                    if (MainContentGrid != null)
                    {
                        NavRailColumn.Width = new GridLength(0);
                        MainViewColumn.Width = new GridLength(1, GridUnitType.Star);
                    }
                    if (ContentBorder != null)
                    {
                        ContentBorder.SetValue(Grid.ColumnProperty, 0);
                        ContentBorder.SetValue(Grid.ColumnSpanProperty, 2);
                    }
                }
                catch { }
                return;
            }

            try
            {
                GlobalSidebar.Visibility = Visibility.Visible;
                if (MainContentGrid != null)
                {
                    NavRailColumn.Width = new GridLength(74);
                    MainViewColumn.Width = new GridLength(1, GridUnitType.Star);
                }

                if (ContentBorder != null)
                {
                    ContentBorder.SetValue(Grid.ColumnProperty, 1);
                    ContentBorder.SetValue(Grid.ColumnSpanProperty, 1);
                }
            }
            catch { }
        }

        private void MainShellBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                if (sender is Border border)
                {
                    border.Clip = new RectangleGeometry
                    {
                        Rect = new Rect(0, 0, border.ActualWidth, border.ActualHeight),
                        RadiusX = 18,
                        RadiusY = 18
                    };
                }
            }
            catch { }
        }

        #endregion

        #region Window Controls

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximizeRestore();
            }
            else
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximizeRestore();
        }

        private void ToggleMaximizeRestore()
        {
            if (WindowState == WindowState.Normal)
            {
                WindowState = WindowState.Maximized;
                _isMaximized = true;
                UpdateMaximizeButton();
            }
            else
            {
                WindowState = WindowState.Normal;
                _isMaximized = false;
                UpdateMaximizeButton();
            }
        }

        private void UpdateMaximizeButton()
        {
            if (MaximizeBtn != null)
            {
                MaximizeBtn.Content = WindowState == WindowState.Maximized ? "🗗" : "🔲";
                MaximizeBtn.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
            }

            if (MainShellBorder != null)
            {
                if (WindowState == WindowState.Maximized)
                {
                    MainShellBorder.Effect = null;
                    MainShellBorder.Margin = new Thickness(0);
                    MainShellBorder.CornerRadius = new CornerRadius(0);
                    if (TitleBar != null) TitleBar.CornerRadius = new CornerRadius(0);
                }
                else
                {
                    MainShellBorder.Effect = _originalMainBorderEffect;
                    MainShellBorder.Margin = new Thickness(8);
                    MainShellBorder.CornerRadius = new CornerRadius(18);
                    if (TitleBar != null) TitleBar.CornerRadius = new CornerRadius(18, 18, 0, 0);
                }
            }

            TriggerViewLayoutRecalculation(ContentArea?.Content);
        }

        private static void TriggerViewLayoutRecalculation(object? content)
        {
            try
            {
                if (content is HZManifestView hzView)
                {
                    hzView.Dispatcher.BeginInvoke(new Action(() => hzView.RecalculateActiveLayout()), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                else if (content is GameBypassView gbView)
                {
                    gbView.Dispatcher.BeginInvoke(new Action(() => gbView.RecalculateActiveLayout()), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                else if (content is OnlineFixView ofView)
                {
                    ofView.Dispatcher.BeginInvoke(new Action(() => ofView.RecalculateActiveLayout()), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            catch { }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == this || e.OriginalSource is Border)
            {
                if (e.ClickCount == 2)
                {
                    ToggleMaximizeRestore();
                }
                else if (WindowState == WindowState.Maximized)
                {
                    _mouseDownScreenPoint = e.GetPosition(this);
                    _isSidebarExpanded = false;
                }
            }
        }

        #endregion

        public void ToggleCommunityChat()
        {
            GlobalChatWidget?.Toggle();
        }

        public void OpenCommunityChat()
        {
            GlobalChatWidget?.Expand();
        }

        #region In-App Chat Notification Toast
        private DispatcherTimer? _notificationToastTimer;
        private long? _lastNotificationTargetId;

        private void InitializeChatNotificationListener()
        {
            CommunityChatRealtimeClient.OnMessageReceived -= HandleChatNotification;
            CommunityChatRealtimeClient.OnMessageReceived += HandleChatNotification;
        }

        private void HandleChatNotification(ChatMessage msg)
        {
            Dispatcher.Invoke(async () =>
            {
                if (msg == null || msg.Id <= 0 || string.IsNullOrWhiteSpace(msg.Message)) return;

                // Load current profile
                var profile = await DashboardView.LoadUserProfileForEditingAsync();
                if (profile == null || string.IsNullOrWhiteSpace(profile.DisplayName)) return;

                // Ignore messages from self
                if (string.Equals(msg.DeviceId, profile.DeviceId, StringComparison.OrdinalIgnoreCase)) return;

                string currentName = profile.DisplayName.Trim();
                bool isMentioned = msg.Message.Contains($"@{currentName}", StringComparison.OrdinalIgnoreCase);
                bool isReplied = !string.IsNullOrWhiteSpace(msg.ReplyToSender) &&
                                 string.Equals(msg.ReplyToSender.Trim(), currentName, StringComparison.OrdinalIgnoreCase);

                if (!isMentioned && !isReplied) return;

                if (msg.AvatarImage != null)
                {
                    ToastAvatarImage.Source = msg.AvatarImage;
                    ToastAvatarImage.Visibility = Visibility.Visible;
                    ToastAvatarInitial.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ToastAvatarImage.Source = null;
                    ToastAvatarImage.Visibility = Visibility.Collapsed;
                    ToastAvatarInitial.Text = msg.AvatarInitial;
                    ToastAvatarInitial.Visibility = Visibility.Visible;
                }
                if (isReplied)
                {
                    ToastTitleText.Text = $"↩ @{msg.SenderName} membalas pesan Anda";
                }
                else
                {
                    ToastTitleText.Text = $"💬 @{msg.SenderName} me-mention Anda";
                }

                ToastBodyText.Text = msg.Message.Length > 60 ? msg.Message.Substring(0, 57) + "..." : msg.Message;
                _lastNotificationTargetId = msg.Id;

                InAppNotificationToast.Visibility = Visibility.Visible;

                _notificationToastTimer?.Stop();
                _notificationToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                _notificationToastTimer.Tick += (s, e) =>
                {
                    _notificationToastTimer?.Stop();
                    InAppNotificationToast.Visibility = Visibility.Collapsed;
                };
                _notificationToastTimer.Start();
            });
        }

        private void InAppNotificationToast_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _notificationToastTimer?.Stop();
            InAppNotificationToast.Visibility = Visibility.Collapsed;

            if (GlobalChatWidget != null)
            {
                GlobalChatWidget.Visibility = Visibility.Visible;
                GlobalChatWidget.Expand();
                if (_lastNotificationTargetId.HasValue)
                {
                    GlobalChatWidget.ScrollToAndHighlightMessage(_lastNotificationTargetId.Value);
                }
            }
        }

        private void CloseToastBtn_Click(object sender, RoutedEventArgs e)
        {
            _notificationToastTimer?.Stop();
            InAppNotificationToast.Visibility = Visibility.Collapsed;
        }
        #endregion
    }
}
