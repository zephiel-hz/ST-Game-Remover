using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Threading.Tasks;

namespace SteamPluginManager.Views
{
    public partial class MainShell : Window
    {
        private bool _isSidebarExpanded = true;
        private bool _isMaximized = false;
        private bool _isTransitioning = false; // Prevents view operations during transition
        private Point _mouseDownScreenPoint = new();
        private readonly bool? _startupLicenseVerified;

        public MainShell(bool? startupLicenseVerified = null)
        {
            InitializeComponent();
            _startupLicenseVerified = startupLicenseVerified;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Register this shell with window navigator
            WindowNavigator.RegisterMainShell(this);
            
            // Subscribe to window state changes
            this.StateChanged += (s, args) => UpdateMaximizeButton();
            
            // Ensure there's a MainWindow-style data context for legacy views that expect it
            if (this.DataContext == null)
            {
                try
                {
                    var hiddenMain = new MainWindow();
                    // Do not show the legacy MainWindow UI; use it only as a data/context holder
                    hiddenMain.Hide();
                    WindowNavigator.RegisterMainWindow(hiddenMain);
                    this.DataContext = hiddenMain;
                }
                catch (Exception ex)
                {
                    try { Logger.Log($"[MainShell] Failed to create hidden MainWindow for DataContext: {ex.Message}"); } catch { }
                }
            }

            await CheckStartupActivationAsync();
        }

        private async Task CheckStartupActivationAsync()
        {
            try
            {
                bool isVerified = _startupLicenseVerified ?? await DashboardView.CheckDeviceTokenAsync();
                if (!isVerified)
                {
                    WindowNavigator.NextViewAfterVerify = "Dashboard";
                    WindowNavigator.NavigateToVerifyToken();
                    return;
                }

                NavigateToDashboard();

                if (this.DataContext is MainWindow mw)
                {
                    Task.Delay(500).ContinueWith(_ => Dispatcher.Invoke(() => mw.ShowWelcomeScreenIfNeeded()));
                }
            }
            catch (Exception ex)
            {
                try { Logger.Log($"[MainShell] Failed startup activation check: {ex.Message}"); } catch { }
                NavigateToDashboard();
            }
        }

        public void NavigateToDashboard()
        {
            TitleText.Text = "HZ Lua Manager";
            var dashboardView = new DashboardView();
            dashboardView.DataContext = this.DataContext; // Pass MainWindow context
            TransitionToView(dashboardView);
            // Update sidebar active state
            try { GlobalSidebar?.SetActiveMenu("Dashboard"); } catch { }
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
                        RadiusX = 24,
                        RadiusY = 24
                    };
                }
            }
            catch { }
        }

        public void NavigateToGameLibrary()
        {
            TitleText.Text = "Game Library";
            var gameLibraryView = new GameLibraryView();
            gameLibraryView.DataContext = this.DataContext; // Pass MainWindow context
            TransitionToView(gameLibraryView);
            try { GlobalSidebar?.SetActiveMenu("Library"); } catch { }
        }

        public void NavigateToSettings()
        {
            TitleText.Text = "Settings";
            var settingsView = new SettingsView();
            settingsView.DataContext = this.DataContext; // Pass MainWindow context
            TransitionToView(settingsView);
            try { GlobalSidebar?.SetActiveMenu("Settings"); } catch { }
        }

        public void NavigateToSaweria()
        {
            TitleText.Text = "Saweria";
            TransitionToView(new SaweriaView());
            try { GlobalSidebar?.SetActiveMenu("Saweria"); } catch { }
        }

        public void NavigateToHZManifest()
        {
            NavigateToHZManifest(forceRefresh: true);
        }

        public void NavigateToHZManifest(bool forceRefresh)
        {
            TitleText.Text = "HZ Manifest";
            var hzManifestView = new HZManifestView();
            hzManifestView.SetForceRefresh(forceRefresh);
            hzManifestView.SetRequiresDeviceVerificationOnLoad(true);
            // DO NOT set DataContext - HZManifestView handles its own DataContext
            TransitionToView(hzManifestView);
            try { GlobalSidebar?.SetActiveMenu("HZManifest"); } catch { }
        }

        public void NavigateToOnlineFix()
        {
            TitleText.Text = "Online Fix";
            var onlineFixView = new OnlineFixView();
            TransitionToView(onlineFixView);
            try { GlobalSidebar?.SetActiveMenu("OnlineFix"); } catch { }
        }

        public void NavigateToGameBypass()
        {
            TitleText.Text = "Game Bypass";
            var gameBypassView = new GameBypassView();
            // DO NOT set DataContext - GameBypassView handles its own DataContext
            TransitionToView(gameBypassView);
            try { GlobalSidebar?.SetActiveMenu("GameBypass"); } catch { }
        }

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
                MessageBox.Show($"Failed to restart Steam: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void CheckForUpdates()
        {
            _ = CheckForUpdatesAsync();
        }

        public void ShowAboutDialog()
        {
            var version = GetInformationalVersion();
            var dialog = new StyledMessageDialog("About HZ Lua Manager", $"HZ Lua Manager\nVersion {version}\n\nYour comprehensive lua management solution.");
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        private static string GetInformationalVersion()
        {
            var infoVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVersion))
                return infoVersion;

            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                var updateInfo = await UpdateChecker.CheckForUpdatesAsync();
                if (updateInfo.ErrorMessage != null)
                {
                    MessageBox.Show($"Failed to check for updates: {updateInfo.ErrorMessage}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (updateInfo.HasUpdate)
                {
                    var dialog = new StyledMessageDialog(
                        "Update Available",
                        $"New Version Available!\n\nCurrent: {updateInfo.CurrentVersion}\nLatest: {updateInfo.LatestVersion}\n\n{updateInfo.ReleaseNotes}\n\nDownload and install the latest version?",
                        showCancel: true
                    );
                    dialog.Owner = this;
                    dialog.PrimaryButton.Content = "Download";
                    dialog.SecondaryButton.Content = "Cancel";
                    var result = dialog.ShowDialog();
                    if (result == true && !string.IsNullOrEmpty(updateInfo.DownloadUrl))
                    {
                        var (success, message) = await UpdateChecker.DownloadAndInstallUpdateAsync(updateInfo.DownloadUrl);
                        if (success)
                        {
                            // Silent update; current process will terminate after installer starts.
                            return;
                        }
                        else
                        {
                            MessageBox.Show($"Failed to download and install update: {message}", "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show($"An error occurred while checking for updates: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void RefreshGameLibrary()
        {
            if (ContentArea.Content is GameLibraryView gameLibraryView)
            {
                gameLibraryView.Refresh();
            }
        }

        public void TransitionToView(object newContent)
        {
            if (_isTransitioning) return; // Prevent overlapping transitions
            _isTransitioning = true;

            try
            {
                ApplyLayoutForContent(newContent);

                // Fade out current content
                if (ContentArea.Content != null && ContentArea.Content != newContent)
                {
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
                    fadeOut.Completed += async (s, e) =>
                    {
                        try
                        {
                            // Set new content
                            ContentArea.Content = newContent;
                            
                            // Force layout update to calculate sizes/positions
                            ContentArea.UpdateLayout();
                            
                            // Small delay to allow Loaded events to be queued but not execute
                            await Task.Delay(10);
                            
                            // Fade in with proper opacity animation
                            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                            fadeIn.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                            fadeIn.Completed += (s2, e2) =>
                            {
                                // Ensure opacity is fully set
                                ContentArea.Opacity = 1.0;
                                // Clear animation
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
                    // First load or same content
                    ContentArea.Content = newContent;
                    ContentArea.UpdateLayout();
                    
                    ContentArea.Opacity = 0;
                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                    fadeIn.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                    fadeIn.Completed += (s, e) =>
                    {
                        // Ensure opacity is fully set
                        ContentArea.Opacity = 1.0;
                        // Clear animation
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

            if (isVerifyView)
            {
                GlobalSidebar.Visibility = Visibility.Collapsed;
                ContentArea.SetValue(System.Windows.Controls.Grid.ColumnProperty, 0);
                ContentArea.SetValue(System.Windows.Controls.Grid.ColumnSpanProperty, 2);
                return;
            }

            GlobalSidebar.Visibility = Visibility.Visible;
            ContentArea.SetValue(System.Windows.Controls.Grid.ColumnProperty, 1);
            ContentArea.SetValue(System.Windows.Controls.Grid.ColumnSpanProperty, 1);
        }

        // Window Control Handlers
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
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
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.OriginalSource == this || e.OriginalSource is System.Windows.Controls.Border)
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
    }
}
