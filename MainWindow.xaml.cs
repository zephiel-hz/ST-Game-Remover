#pragma warning disable CS0103 // The name does not exist
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls.Primitives;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Media.Effects;

namespace SteamPluginManager
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private bool _isPendingRestoreDrag;
        private Point _mouseDownScreenPoint;
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
        
        // Fields to store original effects for performance optimization
        private Effect? _gameListBorderEffect;
        private Effect? _gameDetailsBorderEffect;
        private Effect? _logBorderEffect;

        private void GameList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // If the click originated over empty space (not over any item), ignore
            if (e.OriginalSource is DependencyObject source)
            {
                // If it was over CheckBox, let it pass
                DependencyObject? current = source;
                while (current != null)
                {
                    if (current is CheckBox)
                    {
                        return;
                    }
                    // If we hit a ListViewItem, we'll toggle in that handler below
                    if (current is ListViewItem)
                    {
                        break;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (sender is ListView list && e.OriginalSource is DependencyObject dep)
            {
                var item = ItemsControl.ContainerFromElement(list, dep) as ListViewItem;
                if (item?.DataContext is GameEntry game)
                {
                    // Avoid toggling when the checkbox handled it
                    if (!(dep is CheckBox) && !IsClickOnCheckbox(dep))
                    {
                        // Toggle checkbox state
                        game.IsSelected = !game.IsSelected;
                        UpdateStatus();
                        
                        // Set ListView selection to show details
                        item.IsSelected = true;
                        list.SelectedItem = game;
                        
                        // Load game details
                        _ = LoadGameDetailsAsync(game);
                        
                        e.Handled = true;
                    }
                }
            }
        }
        private void EncryptStream(MemoryStream inStream, string outputFile)
        {
            byte[] salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.GenerateIV();
                var key = new Rfc2898DeriveBytes(GetBackupPassword(), salt, 10000, HashAlgorithmName.SHA256).GetBytes(aes.KeySize / 8);
                aes.Key = key;

                using (var outStream = new FileStream(outputFile, FileMode.Create))
                {
                    outStream.Write(salt, 0, salt.Length);
                    outStream.Write(aes.IV, 0, aes.IV.Length);
                    using (var cryptoStream = new CryptoStream(outStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        inStream.Position = 0;
                        inStream.CopyTo(cryptoStream);
                    }
                }
            }
        }


        private static bool IsClickOnCheckbox(DependencyObject source)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (current is CheckBox) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }
        private ObservableCollection<GameEntry> _games = new ObservableCollection<GameEntry>();
        private readonly string steamPath;
        private bool _isBusy;
        private ResourceDictionary? _darkTheme;
        private string _currentLanguage = "en"; // "en" or "zh"
        private string _lastPlaceholder = "Search...";

        // Helper method to get localized string
        private string GetLocalizedString(string key, params object[] args)
        {
            var resource = TryFindResource(key) as string;
            if (string.IsNullOrEmpty(resource))
                return key; // Fallback to key if resource not found
            
            return args.Length > 0 ? string.Format(resource, args) : resource;
        }

        // Method to get saved language preference or detect Windows language
        private string GetLanguagePreference()
        {
            try
            {
                // First, try to get saved language preference
                var savedLanguage = GetSavedLanguagePreference();
                if (!string.IsNullOrEmpty(savedLanguage))
                {
                    Log($"Using saved language preference: {savedLanguage}");
                    return savedLanguage;
                }
                
                // If no saved preference, detect Windows language
                var culture = System.Globalization.CultureInfo.CurrentCulture;
                
                // Check if the language is Chinese (Simplified or Traditional)
                if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Detected Windows language: Chinese ({culture.Name})");
                    return "zh"; // Chinese
                }
                
                // Default to English for all other languages
                Log($"Detected Windows language: English ({culture.Name})");
                return "en"; // English
            }
            catch (Exception ex)
            {
                // Log error and default to English
                try { SteamPluginManager.Logger.Log($"Failed to get language preference: {ex.Message}"); } catch { }
                return "en"; // Fallback to English
            }
        }

        // Method to save language preference
        private void SaveLanguagePreference(string language)
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var appFolder = System.IO.Path.Combine(appDataPath, "SteamPluginManager");
                Directory.CreateDirectory(appFolder);
                
                var configFile = System.IO.Path.Combine(appFolder, "language.txt");
                File.WriteAllText(configFile, language);
                
                Log($"Language preference saved: {language}");
            }
            catch (Exception ex)
            {
                try { SteamPluginManager.Logger.Log($"Failed to save language preference: {ex.Message}"); } catch { }
            }
        }

        // Method to get saved language preference
        private string GetSavedLanguagePreference()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var appFolder = System.IO.Path.Combine(appDataPath, "SteamPluginManager");
                var configFile = System.IO.Path.Combine(appFolder, "language.txt");
                
                if (File.Exists(configFile))
                {
                    var savedLanguage = File.ReadAllText(configFile).Trim();
                    if (savedLanguage == "en" || savedLanguage == "zh")
                    {
                        return savedLanguage;
                    }
                }
            }
            catch (Exception ex)
            {
                try { SteamPluginManager.Logger.Log($"Failed to read language preference: {ex.Message}"); } catch { }
            }
            
            return null; // No saved preference
        }

        // Method to set ComboBox language selection
        private void SetLanguageComboBoxSelection(string language)
        {
            try
            {
                var langSelector = FindName("LangSelector") as ComboBox;
                if (langSelector == null) return;

                // Find the ComboBoxItem with the matching tag
                foreach (ComboBoxItem item in langSelector.Items)
                {
                    if (item.Tag?.ToString() == language)
                    {
                        langSelector.SelectedItem = item;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                try { SteamPluginManager.Logger.Log($"Failed to set language ComboBox selection: {ex.Message}"); } catch { }
            }
        }

        public ObservableCollection<GameEntry> Games
        {
            get => _games;
            set
            {
                _games = value;
                OnPropertyChanged(nameof(Games));
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                OnPropertyChanged(nameof(IsBusy));
            }
        }

        private bool _isAnyItemSelected;
        public bool IsAnyItemSelected
        {
            get => _isAnyItemSelected;
            set
            {
                if (_isAnyItemSelected != value)
                {
                    _isAnyItemSelected = value;
                    OnPropertyChanged(nameof(IsAnyItemSelected));
                }
            }
        }

        private bool _isSidebarExpanded = true;
        private string _currentView = "Library";
        private string _currentPageTitle = "Game Library";

        // Pseudo-maximize padding used for inset behavior (kept for non-OS maximize layouts)
        private const double _pseudoMaximizePadding = 12;
        // Default restore size when un-maximizing (shrink to this size)
        private const double _defaultRestoreWidth = 1200;
        private const double _defaultRestoreHeight = 700;
        private bool _isPseudoMaximized = false;
        private bool _isTransitioning = false; // Prevents overlapping animations
        private double _restoreLeft, _restoreTop, _restoreWidth, _restoreHeight;

        public string CurrentPageTitle
        {
            get => _currentPageTitle;
            set
            {
                if (_currentPageTitle != value)
                {
                    _currentPageTitle = value;
                    OnPropertyChanged(nameof(CurrentPageTitle));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                // Register dengan WindowNavigator
                WindowNavigator.RegisterMainWindow(this);
                
                // Store original effects for performance optimization during drag
                var gameListBorder = FindName("GameListBorder") as Border;
                var gameDetailsBorder = FindName("GameDetailsBorder") as Border;
                var logBorder = FindName("LogBorder") as Border;
                
                if (gameListBorder != null) _gameListBorderEffect = gameListBorder.Effect;
                if (gameDetailsBorder != null) _gameDetailsBorderEffect = gameDetailsBorder.Effect;
                if (logBorder != null) _logBorderEffect = logBorder.Effect;

                // Log panel is always visible (collapse logic removed)

                // Start the window at the default restore size so it matches the restore behavior
                try { this.Width = _defaultRestoreWidth; this.Height = _defaultRestoreHeight; } catch { }
                CenterWindow();
                // Update maximize/restore icon when window state changes
                this.StateChanged += MainWindow_StateChanged;
                this.MouseMove += Window_MouseMove;
                this.MouseLeftButtonUp += Window_MouseLeftButtonUp;

                // Get language preference (saved or detected from Windows)
                _currentLanguage = GetLanguagePreference();

                steamPath = DetectSteamPath();
            Log($"Steam path: {steamPath}");

            // Subscribe to SearchBox events safely
            var searchBox = FindName("SearchBox") as TextBox;
            if (searchBox != null)
            {
                searchBox.GotFocus += SearchBox_GotFocus;
                searchBox.LostFocus += SearchBox_LostFocus;
                searchBox.TextChanged += SearchBox_TextChanged;
            }

            // Set DataContext ke window ini sendiri
            DataContext = this;

            // Tampilkan versi aplikasi
            DisplayVersion();

            // Start running gradient animation with delay to ensure UI is ready
            Task.Delay(500).ContinueWith(_ => 
            {
                Dispatcher.Invoke(() => StartRunningGradientAnimation());
            });

            // Apply the detected language
            ApplyLanguageResources(_currentLanguage);
            
            // Set ComboBox selection after UI is loaded
            this.Loaded += (_, __) => {
                SetLanguageComboBoxSelection(_currentLanguage);
                // ShowWelcomeScreenIfNeeded() is now handled in App.xaml.cs during startup
                
                // Disable effects if hardware acceleration is off (reduce GPU usage)
                if (!HardwareAccelerationPreferences.IsHardwareAccelerationEnabled())
                {
                    SetShadowsEnabled(false);
                }
                
                // Initialize game details panel after UI is fully loaded
                if (GameDetailsPanel != null)
                {
                    GameDetailsPanel.Visibility = Visibility.Collapsed;
                }
                if (GameDetailsEmpty != null)
                {
                    GameDetailsEmpty.Visibility = Visibility.Visible;
                }
                
                // Initialize sidebar and navigation
                InitializeSidebar();
                UpdateNavButtonStates("Library");
                _currentView = "Library";
                
                // Auto-check for updates on startup
                _ = CheckForUpdatesOnStartupAsync();
                
                // Start periodic auto-update check (every 2 hours)
                _ = StartAutoUpdateCheckAsync();
            };

            RefreshGameList();
            UpdateStatus();

            // Preload theme dictionaries for fast switching
            try
            {
                _darkTheme = new ResourceDictionary { Source = new Uri("Themes/Dark.xaml", UriKind.Relative) };
                
                // Set initial toggle state based on saved preference or current theme
                var savedTheme = ThemePreferences.GetSavedThemePreference();
                bool isDarkTheme = string.IsNullOrEmpty(savedTheme) || savedTheme == "Dark";
                
                // Ensure current app resources use the correct theme and keep strings dictionary
                var md = Application.Current.Resources.MergedDictionaries;
                var initialTheme = new ResourceDictionary { Source = new Uri($"Themes/{(isDarkTheme ? "Dark" : savedTheme)}.xaml", UriKind.Relative) };
                if (md.Count > 0)
                {
                    // Put theme at index 0
                    if (md[0].Source == null || !md[0].Source.OriginalString.StartsWith("Themes/"))
                    {
                        md.Insert(0, initialTheme);
                    }
                    else
                    {
                        md[0] = initialTheme;
                    }
                }
                else
                {
                    md.Add(initialTheme);
                }
                // Ensure strings dictionary exists at index 1 (default English)
                if (md.Count < 2 || md[1].Source == null || !md[1].Source.OriginalString.StartsWith("Strings/"))
                {
                    md.Insert(md.Count >= 1 ? 1 : md.Count, new ResourceDictionary { Source = new Uri("Strings/en.xaml", UriKind.Relative) });
                }

                // Set toggle state to match the theme (light = checked, dark = unchecked)
                if (ThemeToggle != null)
                    ThemeToggle.IsChecked = !isDarkTheme;
            }
            catch (Exception ex)
            {
                // Avoid touching UI controls if not ready
                try { SteamPluginManager.Logger.Log($"Failed to preload themes: {ex}"); } catch { }
            }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize MainWindow: {ex.Message}", GetLocalizedString("Dialog.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private void CenterWindow()
        {
            // Center the window on the screen
            this.WindowStartupLocation = WindowStartupLocation.Manual;
            this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
            this.Top = (SystemParameters.PrimaryScreenHeight - this.Height) / 2;
        }

        /// <summary>
        /// Expand the window at startup to better fill the screen while keeping the pseudo-maximize inset.
        /// </summary>
        private void AdjustStartupSize()
        {
            try
            {
                var workArea = SystemParameters.WorkArea;
                var availableW = Math.Max(400, workArea.Width - _pseudoMaximizePadding * 2);
                var availableH = Math.Max(300, workArea.Height - _pseudoMaximizePadding * 2);

                // Only grow if the available area is larger than current window size
                if (availableW > this.Width) this.Width = availableW;
                if (availableH > this.Height) this.Height = availableH;

                // Center within the work area
                this.Left = workArea.Left + (workArea.Width - this.Width) / 2;
                this.Top = workArea.Top + (workArea.Height - this.Height) / 2;
            }
            catch { }
        }
        
        /// <summary>
        /// Toggles expensive DropShadowEffects on and off to improve performance during window drag/resize.
        /// </summary>
        private void SetShadowsEnabled(bool enabled)
        {
            try
            {
                if (enabled)
                {
                    // Restore the original effects
                    GameListBorder.Effect = _gameListBorderEffect;
                    GameDetailsBorder.Effect = _gameDetailsBorderEffect;
                    LogBorder.Effect = _logBorderEffect;
                }
                else
                {
                    // Disable effects
                    GameListBorder.Effect = null;
                    GameDetailsBorder.Effect = null;
                    LogBorder.Effect = null;
                }
            }
            catch (Exception ex)
            {
                Log($"Error toggling shadows: {ex.Message}");
            }
        }

        // ================= Custom Title Bar =================
        private void TitleBarArea_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (e.ClickCount == 2)
                {
                    ToggleMaximizeRestore();
                    return;
                }
                if (e.ClickCount == 1)
                {
                    if (WindowState == WindowState.Maximized)
                    {
                        // Arm pending restore-drag, but only trigger on movement beyond threshold
                        _isPendingRestoreDrag = true;
                        _mouseDownScreenPoint = PointToScreen(e.GetPosition(this));
                        return;
                    }

                    SetShadowsEnabled(false);
                    DragMove();
                }
            }
            catch { }
            finally
            {
                SetShadowsEnabled(true);
            }
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                // Allow dragging the window when clicking on empty surface
                // but avoid starting a drag from interactive controls
                if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                {
                    DependencyObject? current = e.OriginalSource as DependencyObject;
                    while (current != null)
                    {
                        if (current is Button || current is ToggleButton || current is TextBox || current is ListViewItem || current is CheckBox)
                        {
                            return;
                        }
                        current = VisualTreeHelper.GetParent(current);
                    }
                    // Handle double-click anywhere on non-interactive surface to toggle maximize/restore
                    if (e.ClickCount == 2)
                    {
                        ToggleMaximizeRestore();
                        return;
                    }
                    if (WindowState == WindowState.Maximized)
                    {
                        // Arm pending restore-drag, only trigger when mouse moves
                        _isPendingRestoreDrag = true;
                        _mouseDownScreenPoint = PointToScreen(e.GetPosition(this));
                        return;
                    }
                    // Handle pseudo-maximize mode: arm pending restore-drag when in fullscreen-like mode
                    if (_isPseudoMaximized)
                    {
                        _isPendingRestoreDrag = true;
                        _mouseDownScreenPoint = PointToScreen(e.GetPosition(this));
                        return;
                    }
                    
                    SetShadowsEnabled(false);
                    DragMove();
                }
            }
            catch { }
            finally
            {
                SetShadowsEnabled(true);
            }
        }

        private void Window_MouseMove(object? sender, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                if (_isPendingRestoreDrag && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                {
                    var currentScreen = PointToScreen(e.GetPosition(this));
                    double dx = Math.Abs(currentScreen.X - _mouseDownScreenPoint.X);
                    double dy = Math.Abs(currentScreen.Y - _mouseDownScreenPoint.Y);
                    if (dx >= SystemParameters.MinimumHorizontalDragDistance || dy >= SystemParameters.MinimumVerticalDragDistance)
                    {
                        _isPendingRestoreDrag = false;
                        BeginRestoreDragAt(e.GetPosition(this));
                    }
                }
            }
            catch { }
        }

        private void Window_MouseLeftButtonUp(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isPendingRestoreDrag = false;
        }

        private void BeginRestoreDragAt(Point positionInWindow)
        {
            SetShadowsEnabled(false);
            try
            {
                // Ratio of the point within the window
                double xRatio = positionInWindow.X / this.ActualWidth;
                double yRatio = positionInWindow.Y / this.ActualHeight;

                // Convert to screen coordinates
                var screenPoint = PointToScreen(positionInWindow);

                // Restore from pseudo-maximize or OS-maximize
                if (_isPseudoMaximized)
                {
                    _isPseudoMaximized = false;
                }
                else if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                }

                // Set window to default restore size
                this.Width = _defaultRestoreWidth;
                this.Height = _defaultRestoreHeight;

                // Ensure layout updated before using Width/Height
                this.UpdateLayout();

                // Calculate new top-left to keep cursor over the same relative point
                this.Left = screenPoint.X - (this.ActualWidth * xRatio);
                this.Top = screenPoint.Y - (this.ActualHeight * yRatio);

                // Update UI state after restore
                MainWindow_StateChanged(this, EventArgs.Empty);

                // Start dragging
                DragMove();
            }
            catch { }
            finally
            {
                SetShadowsEnabled(true);
            }
        }

        private void TitleBarArea_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                ToggleMaximizeRestore();
            }
            catch { }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            try { WindowState = WindowState.Minimized; } catch { }
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximizeRestore();
        }

        private void ToggleMaximizeRestore()
        {
            try
            {
                // Prevent overlapping animations
                if (_isTransitioning) return;

                // If we're currently in our pseudo-maximized mode, clear that state and shrink to default restore size
                if (_isPseudoMaximized)
                {
                    _isPseudoMaximized = false;
                    FadeAndTransition(() => 
                    {
                        try { this.Width = _defaultRestoreWidth; this.Height = _defaultRestoreHeight; CenterWindow(); } catch { }
                        try { RootGrid.Margin = new Thickness(_pseudoMaximizePadding); } catch { }
                        MainWindow_StateChanged(this, EventArgs.Empty);
                    });
                    return;
                }
                // If currently OS-maximized, restore to the default smaller size
                if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                    FadeAndTransition(() => 
                    {
                        try { this.Width = _defaultRestoreWidth; this.Height = _defaultRestoreHeight; CenterWindow(); } catch { }
                        try { RootGrid.Margin = new Thickness(_pseudoMaximizePadding); } catch { }
                        MainWindow_StateChanged(this, EventArgs.Empty);
                    });
                    return;
                }
                // Not maximized: perform manual maximize to the work area so it fits the screen precisely
                try
                {
                    // Save current bounds in case we later want to restore them
                    _restoreLeft = this.Left; _restoreTop = this.Top; _restoreWidth = this.Width; _restoreHeight = this.Height;

                    var workArea = SystemParameters.WorkArea;
                    FadeAndTransition(() => 
                    {
                        this.Left = workArea.Left;
                        this.Top = workArea.Top;
                        this.Width = workArea.Width;
                        this.Height = workArea.Height;
                        _isPseudoMaximized = true;
                        try { RootGrid.Margin = new Thickness(0); } catch { }
                        MainWindow_StateChanged(this, EventArgs.Empty);
                    });
                }
                catch { }
            }
            catch { }
        }

        private void CloseButtonWin_Click(object sender, RoutedEventArgs e)
        {
            try { Close(); } catch { }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            // Optionally update maximize icon shape based on state
            try
            {
                var path = this.FindName("MaxIcon") as System.Windows.Shapes.Path;
                if (path == null) return;
                if (WindowState == WindowState.Maximized)
                {
                    // Restore icon (overlapping squares)
                    path.Data = Geometry.Parse("M3,7 L11,7 L11,15 L3,15 Z M7,3 L15,3 L15,11 L13,11 L13,5 L7,5 Z");
                    // When OS-maximized, remove outer margin to use full screen
                    try { RootGrid.Margin = new Thickness(0); } catch { }
                    // Increase inner padding to avoid game card clipping at edges
                    try { CardsArea.Padding = new Thickness(12,12,20,12); } catch { }
                }
                else if (_isPseudoMaximized)
                {
                    // Restore icon (overlapping squares) for pseudo-maximize
                    path.Data = Geometry.Parse("M3,7 L11,7 L11,15 L3,15 Z M7,3 L15,3 L15,11 L13,11 L13,5 L7,5 Z");
                    // Keep small inset margin for pseudo-maximize so content doesn't touch screen edge
                    try { RootGrid.Margin = new Thickness(_pseudoMaximizePadding); } catch { }
                    // Slightly larger inner padding to keep game cards visible and not clipped
                    try { CardsArea.Padding = new Thickness(_pseudoMaximizePadding, _pseudoMaximizePadding, _pseudoMaximizePadding + 8, _pseudoMaximizePadding); } catch { }
                }
                else
                {
                    // Maximize icon (single square)
                    path.Data = Geometry.Parse("M5,5 L15,5 L15,15 L5,15 Z");
                    try { RootGrid.Margin = new Thickness(_pseudoMaximizePadding); } catch { }
                    try { CardsArea.Padding = new Thickness(12,12,12,6); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Provides smooth visual feedback during state transitions by fading content in/out.
        /// </summary>
        private void FadeAndTransition(Action transitionAction)
        {
            try
            {
                _isTransitioning = true;

                // Fade out completely (to 0.0) to hide content during transition
                var fadeOutAnimation = new DoubleAnimation
                {
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(100),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };

                fadeOutAnimation.Completed += (s, a) =>
                {
                    try
                    {
                        // Perform the state transition instantly
                        transitionAction.Invoke();
                        
                        // Force layout update to ensure all sizing/positioning is calculated
                        this.UpdateLayout();

                        // Fade back in smoothly
                        var fadeInAnimation = new DoubleAnimation
                        {
                            To = 1.0,
                            Duration = TimeSpan.FromMilliseconds(100),
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        
                        // Ensure animation completes and opacity is properly set
                        fadeInAnimation.Completed += (s2, a2) =>
                        {
                            try
                            {
                                // Explicitly set opacity to 1.0 to ensure it's not stuck
                                RootGrid.Opacity = 1.0;
                                // Clear animation to avoid conflicts with future state changes
                                RootGrid.BeginAnimation(UIElement.OpacityProperty, null);
                            }
                            finally
                            {
                                _isTransitioning = false;
                            }
                        };
                        
                        RootGrid.BeginAnimation(UIElement.OpacityProperty, fadeInAnimation);
                    }
                    catch
                    {
                        _isTransitioning = false;
                    }
                };

                RootGrid.BeginAnimation(UIElement.OpacityProperty, fadeOutAnimation);
            }
            catch
            {
                _isTransitioning = false;
            }
        }

        private void ThemeToggle_Checked(object sender, RoutedEventArgs e)
        {
            SwitchTheme(isLight: false);
            AnimateThemeToggle(isLight: false);
        }

        private void ThemeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            SwitchTheme(isLight: false);
            AnimateThemeToggle(isLight: false);
        }

        private void SwitchTheme(bool isLight)
        {
            try
            {
                var fadeOutAnimation = new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                fadeOutAnimation.Completed += (s, a) =>
                {
                    // This runs after the fade-out is complete
                    
                    // Reuse preloaded dictionaries to avoid re-reading XAML from disk
                    ResourceDictionary? target = _darkTheme;
                    if (target == null)
                    {
                        target = new ResourceDictionary { Source = new Uri("Themes/Dark.xaml", UriKind.Relative) };
                        _darkTheme = target;
                    }
                    
                    var dictionaries = Application.Current.Resources.MergedDictionaries;
                    if (dictionaries.Count > 0)
                    {
                        dictionaries[0] = target;
                    }
                    else
                    {
                        dictionaries.Add(target);
                    }

                    // Save preference
                    ThemePreferences.SaveThemePreference("Dark");

                    // Now, fade back in
                    var fadeInAnimation = new DoubleAnimation
                    {
                        To = 1,
                        Duration = TimeSpan.FromMilliseconds(300),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                    };
                    RootGrid.BeginAnimation(UIElement.OpacityProperty, fadeInAnimation);
                };

                // Start the fade-out animation on the root grid
                RootGrid.BeginAnimation(UIElement.OpacityProperty, fadeOutAnimation);
            }
            catch (Exception ex)
            {
                Log($"Failed to switch theme with animation: {ex.Message}");
                // In case of error, restore opacity
                RootGrid.Opacity = 1;
            }
        }

        private void ApplyLanguageResources(string language)
        {
            try
            {
                var dictionaries = Application.Current.Resources.MergedDictionaries;
                var stringsUri = new Uri(language == "zh" ? "Strings/zh.xaml" : "Strings/en.xaml", UriKind.Relative);
                if (dictionaries.Count >= 2 && dictionaries[1].Source != null && dictionaries[1].Source.OriginalString.StartsWith("Strings/"))
                {
                    dictionaries[1] = new ResourceDictionary { Source = stringsUri };
                }
                else
                {
                    dictionaries.Insert(dictionaries.Count >= 1 ? 1 : dictionaries.Count, new ResourceDictionary { Source = stringsUri });
                }

                // Update placeholder text if it's currently placeholder
                var placeholder = TryFindResource("Search.Placeholder") as string ?? "Search...";
                if (string.IsNullOrWhiteSpace(SearchBox.Text) || SearchBox.Text == _lastPlaceholder)
                {
                    SearchBox.Text = placeholder;
                    SearchBox.Foreground = (Brush)TryFindResource("PlaceholderBrush");
                }
                _lastPlaceholder = placeholder;
            }
            catch (Exception ex)
            {
                Log($"Failed to apply language resources: {ex.Message}");
            }
        }

        private void AnimateThemeToggle(bool isLight) { /* Visual states handle animations in XAML */ }

        private void StartRunningGradientAnimation()
        {
            try
            {
                // Skip animation if hardware acceleration is disabled (to reduce GPU usage)
                if (!HardwareAccelerationPreferences.IsHardwareAccelerationEnabled())
                {
                    Log("Skipping gradient animation - hardware acceleration disabled");
                    return;
                }

                var storyboard = (Storyboard)FindResource("RunningGradientStoryboard");
                storyboard?.Begin();
            }
            catch (Exception ex)
            {
                Log($"Failed to start gradient animation: {ex.Message}");
            }
        }

        public void ShowWelcomeScreenIfNeeded()
        {
            try
            {
                if (WelcomeScreen.ShouldShowWelcome())
                {
                    // Show welcome screen with a small delay to ensure main window is ready
                    Task.Delay(1000).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var welcomeScreen = new WelcomeScreen();
                            if (this.IsVisible)
                            {
                                welcomeScreen.Owner = this;
                            }
                            welcomeScreen.ShowDialog();
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to show welcome screen: {ex.Message}");
            }
        }
        
        public void ForceShowWelcomeScreen()
        {
            try
            {
                var welcomeScreen = new WelcomeScreen();
                welcomeScreen.Owner = this;
                welcomeScreen.ShowDialog();
            }
            catch (Exception ex)
            {
                Log($"Failed to show welcome screen: {ex.Message}");
            }
        }

        // ================= Version Display =================
        private void DisplayVersion()
        {
            try
            {
                var versionText = FindName("VersionText") as TextBlock;
                if (versionText == null) return;

                var version = Assembly.GetExecutingAssembly().GetName().Version;
                if (version != null)
                {
                    versionText.Text = $"{version.Major}.{version.Minor}.{version.Build}";
                }
                else
                {
                    versionText.Text = "2.2.0";
                }
            }
            catch
            {
                var versionText = FindName("VersionText") as TextBlock;
                if (versionText != null)
                    versionText.Text = "2.2.0";
            }
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void UpdateWindowTitle(string newTitle)
        {
            CurrentPageTitle = newTitle;
            this.Title = newTitle;
        }

        // ================= Steam Path =================
        private string DetectSteamPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key != null)
                {
                    var path = key.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(path)) return path.Replace('/', '\\');
                }
            }
            catch { }
            return @"C:\Program Files (x86)\Steam";
        }

        // Public static method to get Steam path (accessible from other classes)
        public static string GetSteamPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key != null)
                {
                    var path = key.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(path)) return path.Replace('/', '\\');
                }
            }
            catch { }
            return @"C:\Program Files (x86)\Steam";
        }

        // ================= Manifest Processing =================
        private int ProcessManifestFile(string luaFilePath)
        {
            int cleanedCount = 0;
            try
            {
                string content = File.ReadAllText(luaFilePath);
                var matches = Regex.Matches(content, @"setManifestid\((\d+),""(\d+)""\)");

                foreach (Match match in matches)
                {
                    if (match.Groups.Count == 3)
                    {
                        string appId = match.Groups[1].Value;
                        string manifestId = match.Groups[2].Value;
                        cleanedCount += DeleteManifestFile(appId, manifestId);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error processing {System.IO.Path.GetFileName(luaFilePath)}: {ex.Message}");
            }
            return cleanedCount;
        }

        private int DeleteManifestFile(string appId, string manifestId)
        {
            try
            {
                string manifestFileName = $"{appId}_{manifestId}.manifest";
                // Check new path first
                string depotCachePath = System.IO.Path.Combine(steamPath, "depotcache", manifestFileName);
                string depotCachePathOld = System.IO.Path.Combine(steamPath, "config", "depotcache", manifestFileName);

                if (File.Exists(depotCachePath))
                {
                    File.Delete(depotCachePath);
                    Log($"Deleted: {manifestFileName} (from new path)");
                    return 1;
                }
                // If not in new path, check old path
                else if (File.Exists(depotCachePathOld))
                {
                    File.Delete(depotCachePathOld);
                    Log($"Deleted: {manifestFileName} (from old path)");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to delete manifest {appId}_{manifestId}: {ex.Message}");
            }
            return 0;
        }

        // ================= Game Loading =================
        public void RefreshGameList()
        {
            Log($"[RefreshGameList] START - Current Games collection: {Games?.Count ?? 0} items");
            
            if (Games == null)
            {
                Log("[RefreshGameList] ERROR: Games collection is null");
                return;
            }
            
            Games.Clear();
            IsBusy = true;

            string pluginPath = System.IO.Path.Combine(steamPath, "config", "stplug-in");
            string luaPath = System.IO.Path.Combine(steamPath, "config", "lua");
            if (!Directory.Exists(pluginPath) && !Directory.Exists(luaPath))
            {
                Log("Plugin folder and lua folder not found.");
                IsBusy = false;
                UpdateStatus();
                return;
            }

            Log("Refreshing game list..");
            
            // Get lua files from both plugin and lua folders with retry mechanism to ensure file system cache is updated
            var luaFilesDictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(pluginPath))
            {
                foreach (var file in Directory.GetFiles(pluginPath, "*.lua"))
                {
                    string fileName = Path.GetFileName(file);
                    luaFilesDictionary[fileName] = file; // prefer stplug-in path if duplicate exists later
                }
            }
            if (Directory.Exists(luaPath))
            {
                foreach (var file in Directory.GetFiles(luaPath, "*.lua"))
                {
                    string fileName = Path.GetFileName(file);
                    if (!luaFilesDictionary.ContainsKey(fileName))
                        luaFilesDictionary[fileName] = file;
                }
            }
            var luaFiles = luaFilesDictionary.Values.ToArray();
            Log($"[RefreshGameList] Found {luaFiles.Length} unique lua files in {pluginPath} and {luaPath}");
            
            // If we just added files, do retries to ensure file system has caught up
            if (luaFiles.Length == 0)
            {
                Log("[RefreshGameList] No files found on first scan, retrying with delays...");
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    System.Threading.Thread.Sleep(300); // Wait 300ms between retries
                    luaFilesDictionary.Clear();
                    if (Directory.Exists(pluginPath))
                    {
                        foreach (var file in Directory.GetFiles(pluginPath, "*.lua"))
                        {
                            string fileName = Path.GetFileName(file);
                            luaFilesDictionary[fileName] = file;
                        }
                    }
                    if (Directory.Exists(luaPath))
                    {
                        foreach (var file in Directory.GetFiles(luaPath, "*.lua"))
                        {
                            string fileName = Path.GetFileName(file);
                            if (!luaFilesDictionary.ContainsKey(fileName))
                                luaFilesDictionary[fileName] = file;
                        }
                    }
                    luaFiles = luaFilesDictionary.Values.ToArray();
                    Log($"[RefreshGameList] Retry {attempt + 1}: Found {luaFiles.Length} unique lua files");
                    if (luaFiles.Length > 0)
                    {
                        break;
                    }
                }
            }
            
            Log($"Found {luaFiles.Length} lua file(s) in {pluginPath}");
            
            int addedCount = 0;
            foreach (var file in luaFiles)
            {
                try
                {
                    // Extra verification - check file actually exists before processing
                    if (!File.Exists(file))
                    {
                        Log($"  ⚠ File disappeared during scan (file system race condition): {file}");
                        continue;
                    }
                    
                    string fileName = System.IO.Path.GetFileNameWithoutExtension(file);
                    if (int.TryParse(fileName, out int appId))
                    {
                        // Coba dapatkan nama dari cache dulu dengan current language
                        string? cachedName = CacheManager.GetGameName(appId, _currentLanguage);
                        string initialName = cachedName ?? $"Game {appId}";
                        
                        string? installPath = GetGameInstallationPath(appId);
                        var gameEntry = new GameEntry
                        {
                            AppId = appId,
                            Name = initialName,
                            FilePath = file,
                            Genre = "Loading...",
                            IsSelected = false,
                            IsInstalled = installPath != null,
                            InstallationPath = installPath,
                            ScreenshotUrl = CacheManager.GetGameScreenshotUrl(appId)
                        };
                        
                        Games.Add(gameEntry);
                        addedCount++;
                        Log($"  ✓ Loaded game: {initialName} (AppID: {appId})");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error loading {file}: {ex.Message}");
                }
            }

            ApplyFilterAndSort();
            Log($"[RefreshGameList] COMPLETE - Added {addedCount} new games, Total: {Games.Count} game(s). Cache: {CacheManager.GetCacheSize()} items");

            // Update nama game secara asynchronous (dengan cache)
            _ = UpdateGameNamesAsync();
            UpdateStatus();
            
        }

        private async Task UpdateGameNamesAsync()
        {
            IsBusy = true;
            var client = SharedHttpClient.Instance;
            
            // Buat daftar task untuk game yang belum di-cache
            var updateTasks = new List<Task>();
            
            foreach (var game in Games.ToList())
            {
                // Cek apakah game sudah ada di cache
                string? cachedName = CacheManager.GetGameName(game.AppId, _currentLanguage);
                string? cachedGenre = CacheManager.GetGameGenre(game.AppId, _currentLanguage);
                string? cachedScreenshot = CacheManager.GetGameScreenshotUrl(game.AppId);
                if (cachedName != null)
                {
                    // Game sudah di-cache, skip API call
                    Dispatcher.Invoke(() => { 
                        game.Name = cachedName;
                        game.Genre = cachedGenre ?? "Cached";
                        if (!string.IsNullOrWhiteSpace(cachedScreenshot)) game.ScreenshotUrl = cachedScreenshot;
                        // Trigger property change notification for sorting
                        game.OnPropertyChanged(nameof(game.Name));
                    });
                    continue;
                }
                
                // Buat task terpisah untuk fetch data lengkap (nama + genre + screenshot) hanya untuk game yang belum di-cache
                var task = Task.Run(async () =>
                {
                    try
                    {
                        string langParam = _currentLanguage == "zh" ? "schinese" : "english";
                        string url = $"https://store.steampowered.com/api/appdetails?appids={game.AppId}&l={langParam}";
                        string json = await client.GetStringAsync(url);

                        using var doc = JsonDocument.Parse(json);
                        
                        if (doc.RootElement.TryGetProperty(game.AppId.ToString(), out var app) &&
                            app.TryGetProperty("success", out var success) && success.GetBoolean() &&
                            app.TryGetProperty("data", out var data))
                        {
                            string gameName = $"Game {game.AppId}";
                            string genre = "Unknown";
                            string? screenshotUrl = null;
                            
                            // Update nama
                            if (data.TryGetProperty("name", out var nameProp))
                            {
                                gameName = nameProp.GetString() ?? $"Game {game.AppId}";
                                Dispatcher.Invoke(() => { 
                                    game.Name = gameName;
                                    // Trigger property change notification for sorting
                                    game.OnPropertyChanged(nameof(game.Name));
                                });
                            }

                            // Get screenshot/header image if available
                            if (data.TryGetProperty("header_image", out var headerProp))
                            {
                                screenshotUrl = headerProp.GetString();
                            }

                            // Detailed fields (publisher) intentionally skipped per current scope
                            
                            // Update genre
                            try
                            {
                                if (data.TryGetProperty("genres", out var genresArray) && genresArray.ValueKind == JsonValueKind.Array)
                                {
                                    var names = new List<string>();
                                    foreach (var g in genresArray.EnumerateArray())
                                    {
                                        if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("description", out var descEl))
                                        {
                                            var genreName = descEl.GetString();
                                            if (!string.IsNullOrWhiteSpace(genreName)) names.Add(genreName!);
                                        }
                                    }
                                    genre = string.Join(", ", names.Distinct());
                                    Dispatcher.Invoke(() => { game.Genre = genre; });
                                }
                                else if (data.TryGetProperty("categories", out var categoriesArray) && categoriesArray.ValueKind == JsonValueKind.Array)
                                {
                                    var names = new List<string>();
                                    foreach (var c in categoriesArray.EnumerateArray())
                                    {
                                        if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("description", out var descEl))
                                        {
                                            var catName = descEl.GetString();
                                            if (!string.IsNullOrWhiteSpace(catName)) names.Add(catName!);
                                        }
                                    }
                                    genre = string.Join(", ", names.Distinct());
                                    Dispatcher.Invoke(() => { game.Genre = genre; });
                                }
                                else
                                {
                                    Dispatcher.Invoke(() => { game.Genre = genre; });
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"Error processing genre/categories for {game.AppId}: {ex.Message}");
                                Dispatcher.Invoke(() => { game.Genre = "Error"; });
                            }

                            // If no header_image provided, try CDN fallback
                            if (string.IsNullOrWhiteSpace(screenshotUrl))
                            {
                                var cdnUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{game.AppId}/header.jpg";
                                try
                                {
                                    var headResp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, cdnUrl));
                                    if (headResp.IsSuccessStatusCode)
                                    {
                                        screenshotUrl = cdnUrl;
                                    }
                                }
                                catch { /* ignore */ }
                            }


                            // Download and cache screenshot locally if we have a URL
                            if (!string.IsNullOrWhiteSpace(screenshotUrl))
                            {
                                try
                                {
                                    string? localPath = await EnsureCachedScreenshotAsync(game.AppId, screenshotUrl!, client);
                                    if (!string.IsNullOrWhiteSpace(localPath))
                                    {
                                        // Save to cache and update UI
                                        CacheManager.SaveGameDetails(game.AppId, "", localPath, "", "", "", "", _currentLanguage);
                                        Dispatcher.Invoke(() => { game.ScreenshotUrl = localPath; });
                                    }
                                    else
                                    {
                                        // Fallback to remote URL in cache for future attempts
                                        CacheManager.SaveGameDetails(game.AppId, "", screenshotUrl!, "", "", "", "", _currentLanguage);
                                        Dispatcher.Invoke(() => { game.ScreenshotUrl = screenshotUrl; });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"Screenshot download failed for {game.AppId}: {ex.Message}");
                                }
                            }
                            
                            // Simpan nama dan genre ke cache
                            CacheManager.SaveGameData(game.AppId, gameName, genre, _currentLanguage);
                        }
                        else
                        {
                            Dispatcher.Invoke(() => { game.Genre = "API Error"; });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to fetch data for {game.AppId}: {ex.Message}");
                        Dispatcher.Invoke(() => { game.Genre = "Error"; });
                    }
                });
                
                updateTasks.Add(task);
            }
            
            // Tunggu semua task selesai
            await Task.WhenAll(updateTasks);
            
            // Refresh ListView setelah semua nama diupdate
            ApplyFilterAndSort();
            
            // Force a final sort to ensure proper alphabetical ordering
            Dispatcher.Invoke(() => {
                ApplyFilterAndSort();
            });
            
            Log($"Game names updated. Cache: {CacheManager.GetCacheSize()} items");
            IsBusy = false;
            UpdateStatus();
        }

        private void LangSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                {
                    _currentLanguage = tag == "zh" ? "zh" : "en";
                    
                    // Save language preference
                    SaveLanguagePreference(_currentLanguage);
                    
                    ApplyLanguageResources(_currentLanguage);
                    // Refresh display from cache first, then fetch missing
                    foreach (var g in Games)
                    {
                        string? cachedName = CacheManager.GetGameName(g.AppId, _currentLanguage);
                        string? cachedGenre = CacheManager.GetGameGenre(g.AppId, _currentLanguage);
                        if (!string.IsNullOrWhiteSpace(cachedName)) g.Name = cachedName!; else g.Name = $"Game {g.AppId}";
                        if (!string.IsNullOrWhiteSpace(cachedGenre)) g.Genre = cachedGenre!; else g.Genre = "Unknown";
                        // Update installation status
                        string? installPath = GetGameInstallationPath(g.AppId);
                        g.IsInstalled = installPath != null;
                        g.InstallationPath = installPath;
                    }
                    ApplyFilterAndSort();
                    
                    // Reload details if a game is currently selected
                    if (_selectedGameForDetails != null)
                    {
                        // Update installation status for selected game
                        string? installPath = GetGameInstallationPath(_selectedGameForDetails.AppId);
                        _selectedGameForDetails.IsInstalled = installPath != null;
                        _selectedGameForDetails.InstallationPath = installPath;
                        _ = LoadGameDetailsAsync(_selectedGameForDetails);
                    }
                    
                    _ = UpdateGameNamesAsync();
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to change language: {ex.Message}");
            }
        }

        // ================= Filter & Sort =================
        private void ApplyFilterAndSort()
        {
            if (Games == null || GameList == null) return;

            // Sort the ObservableCollection directly using a more efficient method
            SortObservableCollection();

            // Apply search filter using CollectionView
            ICollectionView view = CollectionViewSource.GetDefaultView(GameList.ItemsSource);
            if (view != null)
            {
                var placeholder = _lastPlaceholder;
                string query = SearchBox.Text;

                view.Filter = obj =>
                {
                    if (obj is not GameEntry g) return false;
                    var placeholder = _lastPlaceholder;
                    if (string.IsNullOrWhiteSpace(query) || query == placeholder)
                        return true;

                    return g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           g.AppId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
                };

                view.Refresh();
            }
        }

        private void SortObservableCollection()
        {
            if (Games == null || Games.Count <= 1) return;

            // Get sorted list
            var sortedGames = Games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            
            // Check if already sorted
            bool isAlreadySorted = true;
            for (int i = 0; i < Games.Count; i++)
            {
                if (Games[i] != sortedGames[i])
                {
                    isAlreadySorted = false;
                    break;
                }
            }
            
            if (isAlreadySorted) return;

            // Clear and re-add in sorted order
            Games.Clear();
            foreach (var game in sortedGames)
            {
                Games.Add(game);
            }
        }

        // ================= SearchBox Events =================
        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            var placeholder = TryFindResource("Search.Placeholder") as string ?? "Search...";
            _lastPlaceholder = placeholder;
            if (SearchBox.Text == placeholder)
            {
                SearchBox.Text = "";
                SearchBox.Foreground = (Brush)TryFindResource("InputForegroundBrush");
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var placeholder = TryFindResource("Search.Placeholder") as string ?? "Search...";
            _lastPlaceholder = placeholder;
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                SearchBox.Text = placeholder;
                SearchBox.Foreground = (Brush)TryFindResource("PlaceholderBrush");
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsInitialized) return;
            
            // Update warna teks jika bukan placeholder
            var placeholder = _lastPlaceholder;
            if (SearchBox.Text != placeholder && !string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                SearchBox.Foreground = (Brush)TryFindResource("InputForegroundBrush");
            }
            
            ApplyFilterAndSort();
            UpdateStatus();
        }

        private async Task<string?> EnsureCachedScreenshotAsync(int appId, string imageUrl, HttpClient client)
        {
            try
            {
                var imagesDir = System.IO.Path.Combine(CacheManager.CacheDirectory, "Images");
                Directory.CreateDirectory(imagesDir);

                // Determine file extension from URL path
                string ext = System.IO.Path.GetExtension(new Uri(imageUrl).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                string safeFile = $"{appId}{ext}";
                string destPath = System.IO.Path.Combine(imagesDir, safeFile);

                // If already exists and not empty, return it
                if (File.Exists(destPath) && new FileInfo(destPath).Length > 0) return destPath;

                using var resp = await client.GetAsync(imageUrl);
                if (!resp.IsSuccessStatusCode) return null;

                using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await resp.Content.CopyToAsync(fs);
                }

                return destPath;
            }
            catch (Exception ex)
            {
                Log($"Failed to cache screenshot for {appId}: {ex.Message}");
                return null;
            }
        }

        // ================= Row Click Toggle =================
        private void ListItem_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Ignore if the click originates from the CheckBox
            if (e.OriginalSource is DependencyObject source)
            {
                DependencyObject? current = source;
                while (current != null)
                {
                    if (current is CheckBox)
                    {
                        return;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (sender is ListViewItem item && item.DataContext is GameEntry game)
            {
                game.IsSelected = !game.IsSelected;
                item.IsSelected = game.IsSelected;
                UpdateStatus();
                e.Handled = true;
            }
        }

        private void ItemCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateStatus();
        }

        // ================= Card Click Toggle =================
        private void GameCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Ignore clicks that originate from buttons or checkboxes inside the card
            if (e.OriginalSource is DependencyObject source)
            {
                DependencyObject? current = source;
                while (current != null)
                {
                    if (current is Button || current is CheckBox)
                    {
                        return;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (sender is Border border && border.DataContext is GameEntry game)
            {
                game.IsSelected = !game.IsSelected;
                UpdateStatus();
                e.Handled = true;
            }
        }

        // ================= Image Refresh / Cache Helpers =================
        private async void RefreshImages_Click(object sender, RoutedEventArgs e)
        {
            // If there are selected items, refresh only those; otherwise refresh all
            var targets = Games.Where(g => g.IsSelected).ToList();
            bool onlySelected = targets.Count > 0;
            _ = RefreshScreenshotsAsync(onlySelected);
        }

        private async Task RefreshScreenshotsAsync(bool onlySelected)
        {
            try
            {
                IsBusy = true;
                var client = SharedHttpClient.Instance;

                var list = onlySelected ? Games.Where(g => g.IsSelected).ToList() : Games.ToList();
                foreach (var game in list)
                {
                    try
                    {
                        string langParam = _currentLanguage == "zh" ? "schinese" : "english";
                        string url = $"https://store.steampowered.com/api/appdetails?appids={game.AppId}&l={langParam}";
                        string json = await client.GetStringAsync(url);

                        using var doc = JsonDocument.Parse(json);
                        string? screenshotUrl = null;

                        if (doc.RootElement.TryGetProperty(game.AppId.ToString(), out var app) &&
                            app.TryGetProperty("success", out var success) && success.GetBoolean() &&
                            app.TryGetProperty("data", out var data))
                        {
                            if (data.TryGetProperty("header_image", out var headerProp))
                            {
                                screenshotUrl = headerProp.GetString();
                            }
                        }

                        // If still null, try CDN fallback
                        if (string.IsNullOrWhiteSpace(screenshotUrl))
                        {
                            var cdnUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{game.AppId}/header.jpg";
                            var headResp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, cdnUrl));
                            if (headResp.IsSuccessStatusCode) screenshotUrl = cdnUrl;
                        }

                        if (!string.IsNullOrWhiteSpace(screenshotUrl))
                        {
                            // Force re-download by deleting any existing cache file for this app
                            try
                            {
                                var imagesDir = System.IO.Path.Combine(CacheManager.CacheDirectory, "Images");
                                string ext = System.IO.Path.GetExtension(new Uri(screenshotUrl).AbsolutePath);
                                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                                string destPath = System.IO.Path.Combine(imagesDir, $"{game.AppId}{ext}");
                                if (File.Exists(destPath)) File.Delete(destPath);
                            }
                            catch { }

                            var localPath = await EnsureCachedScreenshotAsync(game.AppId, screenshotUrl, client);
                            if (!string.IsNullOrWhiteSpace(localPath))
                            {
                                Dispatcher.Invoke(() => { game.ScreenshotUrl = localPath; });
                                CacheManager.SaveGameDetails(game.AppId, game.Name, localPath, "", "", "", "", _currentLanguage);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed refreshing image for {game.AppId}: {ex.Message}");
                    }
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OpenCacheFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = CacheManager.CacheDirectory;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer", dir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"Failed to open cache folder: {ex.Message}");
            }
        }

        // Log collapsing logic removed; log panel stays visible permanently.





        // ================= ListView Column Auto-Fit =================
        private void GameList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                if (GameGridView == null || NameColumn == null || SelectColumn == null) return;

                // Calculate width for NameColumn to fill remaining space
                double listWidth = GameList.ActualWidth;
                var padding = 6; // minor safety
                double occupied = SelectColumn.Width + padding;
                double remaining = Math.Max(200, listWidth - occupied);
                NameColumn.Width = remaining;
            }
            catch { }
        }

        private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateStatus();
            
            // Load details for selected game
            if (GameList != null && GameList.SelectedItem is GameEntry selectedGame)
            {
                _ = LoadGameDetailsAsync(selectedGame);
            }
            else if (GameList == null || GameList.SelectedItems.Count == 0)
            {
                // No selection, show empty state
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (GameDetailsPanel != null)
                        {
                            GameDetailsPanel.Visibility = Visibility.Collapsed;
                        }
                        if (GameDetailsEmpty != null)
                        {
                            GameDetailsEmpty.Visibility = Visibility.Visible;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error hiding game details: {ex.Message}");
                    }
                });
            }
        }

        private void GameList_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // This handler is kept for compatibility but SelectionChanged is primary
            // Only handle if clicking on a ListViewItem (not checkbox)
            if (e.OriginalSource is DependencyObject source)
            {
                DependencyObject? current = source;
                bool isCheckbox = false;
                
                while (current != null)
                {
                    if (current is CheckBox)
                    {
                        isCheckbox = true;
                        break;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }
                
                if (isCheckbox) return; // Let checkbox handle it
                
                // Find the ListViewItem
                current = source;
                while (current != null)
                {
                    if (current is ListViewItem item && item.DataContext is GameEntry game)
                    {
                        // Select the item to trigger SelectionChanged
                        item.IsSelected = true;
                        GameList.SelectedItem = game;
                        return;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }
            }
        }

        // ================= Hover to show details =================
        private void GameListItem_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is ListViewItem item && item.DataContext is GameEntry game)
            {
                _hoverCts?.Cancel();
                _hoverCts = new CancellationTokenSource();
                var token = _hoverCts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(150, token);
                        if (token.IsCancellationRequested) return;
                        await Dispatcher.InvokeAsync(async () => await LoadGameDetailsAsync(game));
                    }
                    catch (TaskCanceledException) { }
                    catch (Exception ex) { Log($"Hover error: {ex.Message}"); }
                });
            }
        }

        private void GameListItem_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Cancel pending hover load for this item
            _hoverCts?.Cancel();
        }

        private void GameList_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Cancel any pending hover loads. Do NOT hide details panel — keep last hovered details visible.
            _hoverCts?.Cancel();
        }

        // ================= Buttons =================
        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshGameList();

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var game in Games)
            {
                game.IsSelected = true;
            }
            UpdateStatus();
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var game in Games)
            {
                game.IsSelected = false;
            }
            UpdateStatus();
        }

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            // Disable the button while processing to avoid re-entry
            button?.SetValue(System.Windows.Controls.Button.IsEnabledProperty, false);
            bool cancelled = false;
            try
            {
                var selected = Games.Where(g => g.IsSelected).ToList();
                if (selected.Count == 0)
                {
                    MessageBox.Show(GetLocalizedString("Dialog.NoGameSelected"), GetLocalizedString("Dialog.Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var dlg = new DeleteChoiceDialog(selected.Count) { Owner = this };
                if (dlg.ShowDialog() != true || dlg.SelectedAction == DeleteChoiceDialog.DeleteAction.Cancel) { cancelled = true; return; }
                var action = dlg.SelectedAction;

                int totalManifestsDeleted = 0;
                int totalUninstallTriggered = 0;
                int totalLuaDeleted = 0;

                foreach (var game in selected)
                {
                    try
                    {
                        // If we will delete manifest & lua, read the LUA first (used by ProcessManifestFile)
                        if (action == DeleteChoiceDialog.DeleteAction.ManifestAndLua || action == DeleteChoiceDialog.DeleteAction.Both)
                        {
                            _ = File.ReadAllText(game.FilePath);
                        }

                        // Uninstall if requested
                        if ((action == DeleteChoiceDialog.DeleteAction.Uninstall || action == DeleteChoiceDialog.DeleteAction.Both) && IsGameInstalled(game.AppId))
                        {
                            if (TriggerSteamUninstall(game.AppId))
                            {
                                totalUninstallTriggered++;
                                Log($"Uninstall triggered for {game.AppId}.");

                                // Wait for uninstall to complete before continuing
                                Log($"Waiting for uninstall of {game.AppId} to complete...");
                                bool uninstalled = await WaitForGameUninstalledAsync(game.AppId, TimeSpan.FromMinutes(10));
                                if (uninstalled)
                                {
                                    Log($"Uninstall completed for {game.AppId}.");
                                }
                                else
                                {
                                    Log($"Timed out waiting for uninstall of {game.AppId}. Proceeding to next item.");
                                }
                            }
                            else
                            {
                                Log($"Failed to trigger uninstall for {game.AppId}.");
                            }
                        }

                        // Delete manifests & lua if requested
                        if (action == DeleteChoiceDialog.DeleteAction.ManifestAndLua || action == DeleteChoiceDialog.DeleteAction.Both)
                        {
                            int manifestsDeleted = ProcessManifestFile(game.FilePath);
                            totalManifestsDeleted += manifestsDeleted;

                            if (File.Exists(game.FilePath))
                            {
                                File.Delete(game.FilePath);
                                totalLuaDeleted++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to delete {game.FilePath}: {ex.Message}");
                    }
                }

                Log($"Total: Triggered uninstall for {totalUninstallTriggered} app(s), deleted {totalLuaDeleted} Lua file(s) and {totalManifestsDeleted} manifest file(s)");
            }
            finally
            {
                // Re-enable button and refresh UI unless the user cancelled the dialog
                button?.SetValue(System.Windows.Controls.Button.IsEnabledProperty, true);
                if (!cancelled)
                {
                    RefreshGameList();
                    UpdateStatus();
                }
            }
        }

        /// <summary>
        /// Public method to delete selected games - can be called from other views
        /// </summary>
        public async Task DeleteSelectedGamesAsync(DeleteChoiceDialog.DeleteAction action)
        {
            var selected = Games.Where(g => g.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(GetLocalizedString("Dialog.NoGameSelected"), GetLocalizedString("Dialog.Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                int totalManifestsDeleted = 0;
                int totalUninstallTriggered = 0;
                int totalLuaDeleted = 0;
                int totalFoldersDeleted = 0;

                foreach (var game in selected)
                {
                    try
                    {
                        // If we will delete manifest & lua, read the LUA first (used by ProcessManifestFile)
                        if (action == DeleteChoiceDialog.DeleteAction.ManifestAndLua || action == DeleteChoiceDialog.DeleteAction.Both)
                        {
                            _ = File.ReadAllText(game.FilePath);
                        }

                        // Uninstall if requested
                        if ((action == DeleteChoiceDialog.DeleteAction.Uninstall || action == DeleteChoiceDialog.DeleteAction.Both) && IsGameInstalled(game.AppId))
                        {
                            // Get installation path before uninstall
                            string? installationPath = GetGameInstallationPath(game.AppId);
                            
                            if (TriggerSteamUninstall(game.AppId))
                            {
                                totalUninstallTriggered++;
                                Log($"Uninstall triggered for {game.AppId}.");

                                // Wait for uninstall to complete before continuing
                                Log($"Waiting for uninstall of {game.AppId} to complete...");
                                bool uninstalled = await WaitForGameUninstalledAsync(game.AppId, TimeSpan.FromMinutes(10));
                                if (uninstalled)
                                {
                                    Log($"Uninstall completed for {game.AppId}.");
                                    
                                    // Delete installation folder after uninstall completes
                                    if (!string.IsNullOrEmpty(installationPath) && Directory.Exists(installationPath))
                                    {
                                        Log($"Deleting installation folder for {game.AppId}: {installationPath}");
                                        if (DeleteGameFolder(installationPath))
                                        {
                                            totalFoldersDeleted++;
                                            Log($"Successfully deleted installation folder for {game.AppId}.");
                                        }
                                        else
                                        {
                                            Log($"Failed to completely delete installation folder for {game.AppId}.");
                                        }
                                    }
                                }
                                else
                                {
                                    Log($"Timed out waiting for uninstall of {game.AppId}. Proceeding to next item.");
                                }
                            }
                            else
                            {
                                Log($"Failed to trigger uninstall for {game.AppId}.");
                            }
                        }

                        // Delete manifests & lua if requested
                        if (action == DeleteChoiceDialog.DeleteAction.ManifestAndLua || action == DeleteChoiceDialog.DeleteAction.Both)
                        {
                            int manifestsDeleted = ProcessManifestFile(game.FilePath);
                            totalManifestsDeleted += manifestsDeleted;

                            string deletedLuaFileName = Path.GetFileName(game.FilePath);
                            string? steamPath = SteamHelper.GetSteamPath();

                            if (!string.IsNullOrEmpty(steamPath) && !string.IsNullOrEmpty(deletedLuaFileName))
                            {
                                string pluginLuaFile = Path.Combine(steamPath, "config", "stplug-in", deletedLuaFileName);
                                string luaFolderLuaFile = Path.Combine(steamPath, "config", "lua", deletedLuaFileName);

                                if (File.Exists(pluginLuaFile))
                                {
                                    File.Delete(pluginLuaFile);
                                    totalLuaDeleted++;
                                }

                                if (File.Exists(luaFolderLuaFile) && !string.Equals(luaFolderLuaFile, pluginLuaFile, StringComparison.OrdinalIgnoreCase))
                                {
                                    File.Delete(luaFolderLuaFile);
                                    totalLuaDeleted++;
                                }
                            }
                            else if (File.Exists(game.FilePath))
                            {
                                File.Delete(game.FilePath);
                                totalLuaDeleted++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to delete {game.FilePath}: {ex.Message}");
                    }
                }

                Log($"Total: Triggered uninstall for {totalUninstallTriggered} app(s), deleted {totalLuaDeleted} Lua file(s), {totalManifestsDeleted} manifest file(s), and {totalFoldersDeleted} installation folder(s)");
                
                // Refresh game list after deletion
                RefreshGameList();
                UpdateStatus();
            }
            catch (Exception ex)
            {
                Log($"Delete operation error: {ex.Message}");
                MessageBox.Show($"Delete operation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BackupSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = Games.Where(g => g.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(GetLocalizedString("Dialog.NoGameSelected"), GetLocalizedString("Dialog.Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "SPM Backup (*.spmb)|*.spmb",
                Title = GetLocalizedString("Dialog.BackupTitle"),
                FileName = $"SteamPluginBackup_{DateTime.Now:yyyyMMdd}.spmb"
            };

            if (saveFileDialog.ShowDialog() != true) return;

            try
            {
                // Create a zip archive in memory
                using (var memoryStream = new MemoryStream())
                {
                    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                    {
                        foreach (var game in selected)
                        {
                            if (File.Exists(game.FilePath))
                            {
                                archive.CreateEntryFromFile(game.FilePath, System.IO.Path.GetFileName(game.FilePath));
                            }
                            foreach (var manifest in GetManifestsForLuaFile(game.FilePath))
                            {
                                if (File.Exists(manifest))
                                {
                                    archive.CreateEntryFromFile(manifest, System.IO.Path.GetFileName(manifest));
                                }
                            }
                        }
                    }

                    // Encrypt the memory stream to the file
                    EncryptStream(memoryStream, saveFileDialog.FileName);
                }

                Log(GetLocalizedString("Log.BackupSuccess", selected.Count, System.IO.Path.GetFileName(saveFileDialog.FileName)));
            }
            catch (Exception ex)
            {
                Log(GetLocalizedString("Log.BackupFailed", ex.Message));
            }
        }



        private void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "SPM Backup (*.spmb)|*.spmb",
                Title = GetLocalizedString("Dialog.RestoreTitle")
            };

            if (openFileDialog.ShowDialog() != true) return;

            try
            {
                using (var memoryStream = DecryptStream(openFileDialog.FileName))
                {
                    string stplugInPath = System.IO.Path.Combine(steamPath, "config", "stplug-in");
                    string depotCachePath = System.IO.Path.Combine(steamPath, "depotcache");
                    string depotCachePathOld = System.IO.Path.Combine(steamPath, "config", "depotcache");
                    Directory.CreateDirectory(stplugInPath);
                    Directory.CreateDirectory(depotCachePath);
                    Directory.CreateDirectory(depotCachePathOld);

                    int luaFiles = 0;
                    int manifestFiles = 0;

                    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (entry.FullName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                            {
                                entry.ExtractToFile(System.IO.Path.Combine(stplugInPath, entry.FullName), true);
                                luaFiles++;
                            }
                            else if (entry.FullName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                            {
                                entry.ExtractToFile(System.IO.Path.Combine(depotCachePath, entry.FullName), true);
                                entry.ExtractToFile(System.IO.Path.Combine(depotCachePathOld, entry.FullName), true);
                                manifestFiles++;
                            }
                        }
                    }
                    Log(GetLocalizedString("Log.RestoreSuccess", luaFiles, manifestFiles));
                }
                RefreshGameList();
            }
            catch (Exception ex)
            {
                Log(GetLocalizedString("Log.RestoreFailed", ex.Message));
            }
        }

        private MemoryStream DecryptStream(string inputFile)
        {
            var memoryStream = new MemoryStream();
            using (var inStream = new FileStream(inputFile, FileMode.Open))
            {
                byte[] salt = new byte[SaltSize];
                inStream.Read(salt, 0, salt.Length);

                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    byte[] iv = new byte[aes.IV.Length];
                    inStream.Read(iv, 0, iv.Length);
                    aes.IV = iv;

                    var key = new Rfc2898DeriveBytes(GetBackupPassword(), salt, 10000, HashAlgorithmName.SHA256).GetBytes(aes.KeySize / 8);
                    aes.Key = key;

                    using (var cryptoStream = new CryptoStream(inStream, aes.CreateDecryptor(), CryptoStreamMode.Read))
                    {
                        cryptoStream.CopyTo(memoryStream);
                    }
                }
            }
            memoryStream.Position = 0;
            return memoryStream;
        }
        private IEnumerable<string> GetManifestsForLuaFile(string luaFilePath)
        {
            var manifests = new List<string>();
            try
            {
                string content = File.ReadAllText(luaFilePath);
                var matches = Regex.Matches(content, @"setManifestid\((\d+),""(\d+)""\)");

                foreach (Match match in matches)
                {
                    if (match.Groups.Count == 3)
                    {
                        string appId = match.Groups[1].Value;
                        string manifestId = match.Groups[2].Value;
                        string manifestFileName = $"{appId}_{manifestId}.manifest";
                        manifests.Add(System.IO.Path.Combine(steamPath, "config", "depotcache", manifestFileName));
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error processing {System.IO.Path.GetFileName(luaFilePath)}: {ex.Message}");
            }
            return manifests;
        }


        private void RestartSteam_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("steam"))
                {
                    proc.Kill();
                    proc.WaitForExit();
                }

                Process.Start(System.IO.Path.Combine(steamPath, "steam.exe"));
                Log("Steam restarted.");
            }
            catch (Exception ex)
            {
                Log($"Failed to restart Steam: {ex.Message}");
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = LogBox.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    Clipboard.SetText(text);
                    Log(GetLocalizedString("Log.CopiedToClipboard"));
                }
                else
                {
                    Log(GetLocalizedString("Log.IsEmpty"));
                }
            }
            catch (Exception ex)
            {
                Log(GetLocalizedString("Log.FailedToCopy", ex.Message));
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settingsWindow = new SettingsWindow();
                settingsWindow.Owner = this;
                settingsWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                Log($"Failed to open settings: {ex.Message}");
            }
        }

        private void BackToDashboard_Click(object sender, RoutedEventArgs e)
        {
            // Register MainWindow dengan WindowNavigator sebelum navigasi
            WindowNavigator.RegisterMainWindow(this);
            // Buka Dashboard dan hide MainWindow
            WindowNavigator.OpenDashboard();
            this.Hide();
        }
        
        private void ResetWelcomeScreen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WelcomeScreen.ResetWelcomePreference();
                Log(GetLocalizedString("Log.WelcomeReset"));
                
                // Show welcome screen immediately for testing
                var result = MessageBox.Show(GetLocalizedString("Dialog.WelcomeResetConfirm"), 
                    GetLocalizedString("Dialog.ResetComplete"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    ForceShowWelcomeScreen();
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to reset welcome screen preference: {ex.Message}");
            }
        }

        // ================= Log Helper =================
        private void Log(string msg)
        {
            try
            {
                if (LogBox == null)
                {
                    // Window controls may not be initialized yet; write to file instead
                    SteamPluginManager.Logger.Log(msg);
                    return;
                }
                Dispatcher.Invoke(() =>
                {
                    if (LogBox == null)
                    {
                        SteamPluginManager.Logger.Log(msg);
                        return;
                    }
                    LogBox.AppendText($"{msg}\n--------------------\n");
                    LogBox.ScrollToEnd();
                });
            }
            catch
            {
                try { SteamPluginManager.Logger.Log(msg); } catch { }
            }
        }

        // ================= Steam Uninstall Helpers =================
        private bool IsGameInstalled(int appId)
        {
            return GetGameInstallationPath(appId) != null;
        }

        public string? GetGameInstallationPath(int appId)
        {
            try
            {
                foreach (var library in EnumerateSteamLibraries())
                {
                    string manifest = System.IO.Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                    if (File.Exists(manifest))
                    {
                        try
                        {
                            string manifestContent = File.ReadAllText(manifest);
                            var stateMatch = System.Text.RegularExpressions.Regex.Match(manifestContent, "\"StateFlags\"\\s*\"([^\"]+)\"");
                            if (stateMatch.Success)
                            {
                                string stateFlags = stateMatch.Groups[1].Value;
                                if (stateFlags == "0")
                                {
                                    Log($"[GetGameInstallationPath] Game {appId} manifest StateFlags=0 (uninstalled)");
                                    continue;
                                }
                            }
                            if (manifestContent.Contains("\"installdir\""))
                            {
                                var match = System.Text.RegularExpressions.Regex.Match(manifestContent, "\"installdir\"\\s*\"([^\"]+)\"");
                                if (match.Success)
                                {
                                    string installDir = match.Groups[1].Value;
                                    string gameFolder = System.IO.Path.Combine(library, "steamapps", "common", installDir);
                                    if (Directory.Exists(gameFolder))
                                    {
                                        Log($"[GetGameInstallationPath] Game {appId} confirmed installed at: {gameFolder}");
                                        return gameFolder;
                                    }
                                    else
                                    {
                                        Log($"[GetGameInstallationPath] Game {appId} manifest exists but game folder missing: {gameFolder}");
                                    }
                                }
                            }
                            else
                            {
                                Log($"[GetGameInstallationPath] Game {appId} manifest found but appears uninstalled (no installdir)");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"[GetGameInstallationPath] Error parsing manifest for {appId}: {ex.Message}");
                        }
                    }
                }
                Log($"[GetGameInstallationPath] Game {appId} NOT found in any Steam library");
            }
            catch (Exception ex)
            {
                Log($"[GetGameInstallationPath] Error checking game {appId}: {ex.Message}");
            }
            return null;
        }

        private string GetDriveLetter(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return "";
                var driveInfo = new System.IO.DriveInfo(path);
                return driveInfo.Name.TrimEnd('\\');
            }
            catch
            {
                // Fallback: extract drive letter from path
                if (path.Length >= 2 && path[1] == ':')
                {
                    return path.Substring(0, 2).ToUpper();
                }
                return "";
            }
        }

        private IEnumerable<string> EnumerateSteamLibraries()
        {
            var results = new List<string> { steamPath };
            try
            {
                string libraryFile = System.IO.Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libraryFile))
                {
                    foreach (var line in File.ReadAllLines(libraryFile))
                    {
                        var m = Regex.Match(line, "\\\"path\\\"\\s*\\\"(?<p>.+?)\\\"");
                        if (m.Success)
                        {
                            string raw = m.Groups["p"].Value;
                            string unescaped = raw.Replace("\\\\", "\\");
                            if (Directory.Exists(unescaped)) results.Add(unescaped);
                        }
                    }
                }
            }
            catch { }
            return results;
        }

        private bool TriggerSteamUninstall(int appId)
        {
            try
            {
                if (IsSteamRunning())
                {
                    // Gunakan protocol handler saat Steam aktif
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = $"steam://uninstall/{appId}",
                        UseShellExecute = true
                    });
                    return true;
                }
                else
                {
                    // Jika Steam belum berjalan, jalankan Steam dengan argumen URI langsung (lebih andal)
                    var steamExe = System.IO.Path.Combine(steamPath, "steam.exe");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = steamExe,
                        Arguments = $"steam://uninstall/{appId}",
                        UseShellExecute = true
                    });
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to start uninstall uri for {appId}: {ex.Message}");
                // Terakhir: coba start Steam saja, beri jeda, lalu kirim protocol
                try
                {
                    var steamExe = System.IO.Path.Combine(steamPath, "steam.exe");
                    Process.Start(new ProcessStartInfo { FileName = steamExe, UseShellExecute = true });
                    // Tunggu singkat agar Steam mendaftarkan handler/terbuka
                    System.Threading.Thread.Sleep(3000);
                    Process.Start(new ProcessStartInfo { FileName = $"steam://uninstall/{appId}", UseShellExecute = true });
                    return true;
                }
                catch (Exception ex2)
                {
                    Log($"Fallback uninstall failed for {appId}: {ex2.Message}");
                }
            }
            return false;
        }

        private async Task<bool> WaitForGameUninstalledAsync(int appId, TimeSpan timeout)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < timeout)
                {
                    if (!IsGameInstalled(appId)) return true;
                    await Task.Delay(2000);
                }
                // Final check after timeout
                return !IsGameInstalled(appId);
            }
            catch (Exception ex)
            {
                Log($"Error while waiting for uninstall of {appId}: {ex.Message}");
                return false;
            }
        }

        private bool DeleteGameFolder(string folderPath)
        {
            try
            {
                if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                {
                    Log($"[DeleteGameFolder] Folder does not exist: {folderPath}");
                    return false;
                }

                Log($"[DeleteGameFolder] Attempting to delete folder: {folderPath}");
                
                // Attempt to delete with retries for locked files
                int maxRetries = 5;
                int retryDelay = 500; // milliseconds
                
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    try
                    {
                        // Delete directory recursively
                        Directory.Delete(folderPath, true);
                        Log($"[DeleteGameFolder] Successfully deleted folder: {folderPath}");
                        return true;
                    }
                    catch (IOException ex) when (attempt < maxRetries - 1)
                    {
                        Log($"[DeleteGameFolder] Attempt {attempt + 1} failed: {ex.Message}. Retrying in {retryDelay}ms...");
                        System.Threading.Thread.Sleep(retryDelay);
                        retryDelay *= 2; // Exponential backoff
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Log($"[DeleteGameFolder] Access denied when deleting {folderPath}: {ex.Message}");
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log($"[DeleteGameFolder] Unexpected error: {ex.Message}");
                        return false;
                    }
                }
                
                Log($"[DeleteGameFolder] Failed to delete folder after {maxRetries} attempts: {folderPath}");
                return false;
            }
            catch (Exception ex)
            {
                Log($"[DeleteGameFolder] Error: {ex.Message}");
                return false;
            }
        }

        private static bool IsSteamRunning()
        {
            try
            {
                return Process.GetProcessesByName("steam").Any();
            }
            catch { return false; }
        }

        private void UpdateStatus()
        {
            try
            {
                int total = Games.Count;
                int selected = Games.Count(g => g.IsSelected);
                StatusText.Text = GetLocalizedString("Status.SelectedCount", selected, total);
                IsAnyItemSelected = selected > 0;
                
                // Update button states
                UpdateSelectButtonsState(selected, total);
            }
            catch { }
        }

        private void UpdateSelectButtonsState(int selectedCount, int totalCount)
        {
            if (SelectAllBtn != null && SelectNoneBtn != null)
            {
                // Enable Select All if not all games are selected
                SelectAllBtn.IsEnabled = selectedCount < totalCount;
                
                // Enable Select None if any games are selected
                SelectNoneBtn.IsEnabled = selectedCount > 0;
                
                // Update button text to show current state
                if (selectedCount == totalCount && totalCount > 0)
                {
                    SelectAllBtn.Content = "All ✓";
                }
                else
                {
                    SelectAllBtn.Content = "All";
                }
                
                if (selectedCount == 0)
                {
                    SelectNoneBtn.Content = "None ✓";
                }
                else
                {
                    SelectNoneBtn.Content = "None";
                }
            }
        }

        // ================= Game Details =================
        private GameEntry? _selectedGameForDetails;
        private CancellationTokenSource? _hoverCts;

        private async Task LoadGameDetailsAsync(GameEntry game)
        {
            if (game == null) return;
            
            _selectedGameForDetails = game;
            
            // Show loading state
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (GameDetailsPanel != null)
                    {
                        GameDetailsPanel.Visibility = Visibility.Visible;
                    }
                    
                    if (GameDetailsEmpty != null)
                    {
                        GameDetailsEmpty.Visibility = Visibility.Collapsed;
                    }
                    
                    if (GameDetailName != null) GameDetailName.Text = game.Name;
                    if (GameDetailAppId != null) GameDetailAppId.Text = game.AppId.ToString();
                    if (GameDetailGenre != null) GameDetailGenre.Text = game.Genre;
                    if (GameDetailInstallationStatus != null) 
                    {
                        string statusText = game.IsInstalled 
                            ? GetLocalizedString("Status.Installed") 
                            : GetLocalizedString("Status.NotInstalled");
                        
                        // Add drive letter if installed
                        if (game.IsInstalled && !string.IsNullOrWhiteSpace(game.InstallationPath))
                        {
                            string driveLetter = GetDriveLetter(game.InstallationPath);
                            if (!string.IsNullOrWhiteSpace(driveLetter))
                            {
                                statusText += $" ({GetLocalizedString("Label.Drive")} {driveLetter})";
                            }
                        }
                        
                        GameDetailInstallationStatus.Text = statusText;
                        // Set color: green for installed, default for not installed
                        GameDetailInstallationStatus.Foreground = game.IsInstalled 
                            ? (Brush)TryFindResource("SuccessButtonBrush") 
                            : (Brush)TryFindResource("ForegroundBrush");
                    }
                    if (GameDetailDescription != null) GameDetailDescription.Text = GetLocalizedString("Label.Loading") + "...";
                    if (GameScreenshot != null) GameScreenshot.Source = null;
                    if (GameDetailReleaseDate != null)
                    {
                        GameDetailReleaseDate.Text = "";
                        GameDetailReleaseDate.Visibility = Visibility.Collapsed;
                    }
                    if (GameDetailReleaseDateLabel != null)
                    {
                        GameDetailReleaseDateLabel.Visibility = Visibility.Collapsed;
                    }
                    if (GameDetailPublisher != null) GameDetailPublisher.Text = "";
                    if (GameDetailPublisherPanel != null) GameDetailPublisherPanel.Visibility = Visibility.Collapsed;
                    if (GameDetailDRM != null) GameDetailDRM.Text = GetLocalizedString("Label.Loading") + "...";
                    if (GameDetailDRMPanel != null) GameDetailDRMPanel.Visibility = Visibility.Collapsed;
                    if (GameDetailAntiCheat != null) GameDetailAntiCheat.Text = "";
                    if (GameDetailAntiCheatPanel != null) GameDetailAntiCheatPanel.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    Log($"Error setting up game details UI: {ex.Message}");
                }
            });

            try
            {
                // Try to get from cache first
                string? cachedDescription = CacheManager.GetGameDescription(game.AppId, _currentLanguage);
                string? cachedScreenshot = CacheManager.GetGameScreenshotUrl(game.AppId);
                string? cachedReleaseDate = CacheManager.GetGameReleaseDate(game.AppId);
                string? cachedPublisher = CacheManager.GetGamePublisher(game.AppId);
                string? cachedDRM = CacheManager.GetGameDRM(game.AppId);
                string? cachedAntiCheat = CacheManager.GetGameAntiCheat(game.AppId);

                if (cachedDescription != null && cachedScreenshot != null)
                {
                    // Use cached data
                    Dispatcher.Invoke(() =>
                    {
                        UpdateGameDetailsUI(game, cachedDescription, cachedScreenshot, cachedReleaseDate, cachedPublisher, cachedDRM, cachedAntiCheat);
                    });
                    return;
                }

                // Fetch from API
                var client = SharedHttpClient.Instance;
                string langParam = _currentLanguage == "zh" ? "schinese" : "english";
                string url = $"https://store.steampowered.com/api/appdetails?appids={game.AppId}&l={langParam}";
                string json = await client.GetStringAsync(url);

                using var doc = JsonDocument.Parse(json);
                
                if (doc.RootElement.TryGetProperty(game.AppId.ToString(), out var app) &&
                    app.TryGetProperty("success", out var success) && success.GetBoolean() &&
                    app.TryGetProperty("data", out var data))
                {
                    string description = "";
                    string screenshotUrl = "";
                    string releaseDate = "";
                    string publisher = "";
                    string drm = "";
                    string antiCheat = "";

                    // Get description
                    if (data.TryGetProperty("short_description", out var descProp))
                    {
                        description = descProp.GetString() ?? "";
                    }
                    else if (data.TryGetProperty("detailed_description", out var detailedDescProp))
                    {
                        // Strip HTML tags for display
                        description = System.Text.RegularExpressions.Regex.Replace(
                            detailedDescProp.GetString() ?? "", 
                            "<.*?>", 
                            string.Empty);
                    }

                    // Get screenshot (use header image or first screenshot)
                    if (data.TryGetProperty("header_image", out var headerImg))
                    {
                        screenshotUrl = headerImg.GetString() ?? "";
                    }
                    else if (data.TryGetProperty("screenshots", out var screenshots) && 
                             screenshots.ValueKind == JsonValueKind.Array)
                    {
                        try
                        {
                            int screenshotCount = screenshots.GetArrayLength();
                            if (screenshotCount > 0)
                            {
                                var firstScreenshot = screenshots[0];
                                if (firstScreenshot.TryGetProperty("path_full", out var pathFull))
                                {
                                    screenshotUrl = pathFull.GetString() ?? "";
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Error extracting screenshot for {game.AppId}: {ex.Message}");
                        }
                    }

                    // Get release date
                    if (data.TryGetProperty("release_date", out var releaseDateObj))
                    {
                        try
                        {
                            if (releaseDateObj.TryGetProperty("date", out var dateProp))
                            {
                                releaseDate = dateProp.GetString() ?? "";
                            }
                        }
                        catch { /* Silently handle release date extraction errors */ }
                    }

                    // Get publisher
                    if (data.TryGetProperty("publishers", out var publishers) && 
                        publishers.ValueKind == JsonValueKind.Array)
                    {
                        try
                        {
                            int publisherCount = publishers.GetArrayLength();
                            if (publisherCount > 0)
                            {
                                var publisherObj = publishers[0];
                                // Publisher is object with name property
                                if (publisherObj.ValueKind == JsonValueKind.Object && publisherObj.TryGetProperty("name", out var nameProperty))
                                {
                                    publisher = nameProperty.GetString() ?? "";
                                }
                                else if (publisherObj.ValueKind == JsonValueKind.String)
                                {
                                    // Fallback if publisher is just a string
                                    publisher = publisherObj.GetString() ?? "";
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Error extracting publisher for {game.AppId}: {ex.Message}");
                        }
                    }

                    // Get DRM notice
                    if (data.TryGetProperty("drm_notice", out var drmProp))
                    {
                        drm = drmProp.GetString() ?? "";
                    }

                    // Get Anti-Cheat notice from categories
                    if (data.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
                    {
                        var antiCheatList = new List<string>();
                        foreach (var category in categories.EnumerateArray())
                        {
                            if (category.TryGetProperty("description", out var catDescProp))
                            {
                                var catDesc = catDescProp.GetString();
                                if (catDesc != null && (catDesc.Contains("Anti-Cheat") || catDesc.Contains("VAC")))
                                {
                                    antiCheatList.Add(catDesc);
                                }
                            }
                        }
                        if (antiCheatList.Count > 0)
                        {
                            antiCheat = string.Join(", ", antiCheatList);
                        }
                    }


                    // Save to cache
                    CacheManager.SaveGameDetails(game.AppId, description, screenshotUrl, releaseDate, publisher, drm, antiCheat, _currentLanguage);

                    // Update UI
                    Dispatcher.Invoke(() =>
                    {
                        if (_selectedGameForDetails == game) // Make sure user hasn't selected another game
                        {
                            UpdateGameDetailsUI(game, description, screenshotUrl, releaseDate, publisher, drm, antiCheat);
                        }
                    });
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        GameDetailDescription.Text = GetLocalizedString("Label.NoDescriptionAvailable");
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to load game details for {game.AppId}: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    GameDetailDescription.Text = GetLocalizedString("Label.ErrorLoadingDetails");
                });
            }
        }

        private void UpdateGameDetailsUI(GameEntry game, string description, string screenshotUrl, string? releaseDate, string? publisher, string? drm, string? antiCheat)
        {
            if (GameDetailsPanel == null) return;
            
            GameDetailsPanel.Visibility = Visibility.Visible;
            GameDetailsEmpty.Visibility = Visibility.Collapsed;
            
            GameDetailName.Text = game.Name;
            GameDetailAppId.Text = game.AppId.ToString();
            GameDetailGenre.Text = game.Genre;
            if (GameDetailInstallationStatus != null)
            {
                string statusText = game.IsInstalled 
                    ? GetLocalizedString("Status.Installed") 
                    : GetLocalizedString("Status.NotInstalled");
                
                // Add drive letter if installed
                if (game.IsInstalled && !string.IsNullOrWhiteSpace(game.InstallationPath))
                {
                    string driveLetter = GetDriveLetter(game.InstallationPath);
                    if (!string.IsNullOrWhiteSpace(driveLetter))
                    {
                        statusText += $" ({GetLocalizedString("Label.Drive")} {driveLetter})";
                    }
                }
                
                GameDetailInstallationStatus.Text = statusText;
                // Set color: green for installed, default for not installed
                GameDetailInstallationStatus.Foreground = game.IsInstalled 
                    ? (Brush)TryFindResource("SuccessButtonBrush") 
                    : (Brush)TryFindResource("ForegroundBrush");
            }
            GameDetailDescription.Text = string.IsNullOrWhiteSpace(description) 
                ? GetLocalizedString("Label.NoDescriptionAvailable") 
                : description;

            // Load screenshot
            if (!string.IsNullOrWhiteSpace(screenshotUrl))
            {
                try
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(screenshotUrl, UriKind.Absolute);
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    GameScreenshot.Source = bitmap;
                }
                catch (Exception ex)
                {
                    Log($"Failed to load screenshot: {ex.Message}");
                }
            }

            // Release date
            if (!string.IsNullOrWhiteSpace(releaseDate))
            {
                GameDetailReleaseDate.Text = releaseDate;
                GameDetailReleaseDate.Visibility = Visibility.Visible;
                GameDetailReleaseDateLabel.Visibility = Visibility.Visible;
            }
            else
            {
                GameDetailReleaseDate.Visibility = Visibility.Collapsed;
                GameDetailReleaseDateLabel.Visibility = Visibility.Collapsed;
            }

            // Publisher
            if (!string.IsNullOrWhiteSpace(publisher))
            {
                GameDetailPublisher.Text = publisher;
                GameDetailPublisherPanel.Visibility = Visibility.Visible;
            }
            else
            {
                GameDetailPublisherPanel.Visibility = Visibility.Collapsed;
            }

            // DRM
            game.DRM = drm ?? GetLocalizedString("DRM.Steam"); // Default to Steam if null or empty
            if (string.IsNullOrWhiteSpace(game.DRM))
            {
                game.DRM = GetLocalizedString("DRM.Steam");
            }

            if (GameDetailDRMPanel != null)
            {
                GameDetailDRM.Text = game.DRM;
                GameDetailDRMPanel.Visibility = Visibility.Visible;
            }

            // Anti-Cheat
            game.AntiCheat = antiCheat ?? "";
            if (!string.IsNullOrWhiteSpace(game.AntiCheat))
            {
                if (GameDetailAntiCheatPanel != null)
                {
                    GameDetailAntiCheat.Text = game.AntiCheat;
                    GameDetailAntiCheatPanel.Visibility = Visibility.Visible;
                }
            }
            else
            {
                if (GameDetailAntiCheatPanel != null)
                {
                    GameDetailAntiCheatPanel.Visibility = Visibility.Collapsed;
                }
            }
        }





        // Sidebar removed — no-op initialization kept for compatibility
        private void InitializeSidebar() { /* sidebar removed */ }

        private void LibraryNavButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToView("Library");
        }

        private void BackupRestoreNavButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToView("BackupRestore");
        }

        private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToView("Settings");
        }

        private void NavigateToView(string viewName)
        {
            try
            {
                _currentView = viewName;
                
                // Update button states
                UpdateNavButtonStates(viewName);
                
                // Show/hide content based on view
                // For now, Library view shows the main content, other views can be added later
                if (viewName == "Library")
                {
                    // Show main content (game list, details, log)
                    // Content is already visible by default
                    Log($"Navigated to Library view");
                }
                else if (viewName == "BackupRestore")
                {
                    // Show backup/restore interface
                    // For now, trigger the backup/restore dialog directly
                    Log($"Navigated to Backup & Restore view");
                    // You can add a dedicated backup/restore panel here later
                }
                else if (viewName == "Settings")
                {
                    // Show settings interface
                    // For now, just log - can be expanded later with a settings panel
                    Log($"Navigated to Settings view");
                    // You can add a dedicated settings panel here later
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to navigate to view {viewName}: {ex.Message}");
            }
        }

        private void UpdateNavButtonStates(string activeView)
        {
            // Sidebar and nav buttons removed — nothing to update here.
            return;
        }

        private void RootGrid_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void RootGrid_DragOver(object sender, DragEventArgs e)
        {
            // Handle DragOver to ensure continuous feedback
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void RootGrid_Drop(object sender, DragEventArgs e)
        {
            try
            {
                e.Handled = true;
                
                if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    return;
                }

                string[]? files = null;
                try
                {
                    files = (string[])e.Data.GetData(DataFormats.FileDrop);
                }
                catch (Exception ex)
                {
                    Log($"Error accessing dropped files: {ex.Message}");
                    MessageBox.Show($"Failed to access dropped files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (files == null || files.Length == 0) return;

                // Check if Steam path is valid
                if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
                {
                    MessageBox.Show("Steam path not found. Please check your Steam installation.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Log("Error: Steam path is invalid or not set.");
                    return;
                }

                string stplugInPath = System.IO.Path.Combine(steamPath, "config", "stplug-in");
                string depotCachePath = System.IO.Path.Combine(steamPath, "depotcache");
                
                // Create directories if they don't exist
                try
                {
                    if (!Directory.Exists(stplugInPath))
                    {
                        Directory.CreateDirectory(stplugInPath);
                        Log($"Created directory: {stplugInPath}");
                    }
                    
                    if (!Directory.Exists(depotCachePath))
                    {
                        Directory.CreateDirectory(depotCachePath);
                        Log($"Created directory: {depotCachePath}");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to create required directories: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Log($"Error creating directories: {ex.Message}");
                    return;
                }

                int luaCount = 0;
                int manifestCount = 0;
                int failedCount = 0;

                foreach (var file in files)
                {
                    try
                    {
                        // Check if file exists
                        if (!File.Exists(file))
                        {
                            Log($"File not found: {file}");
                            failedCount++;
                            continue;
                        }

                        string extension = System.IO.Path.GetExtension(file).ToLower();
                        string fileName = System.IO.Path.GetFileName(file);

                        if (extension == ".lua")
                        {
                            string destPath = System.IO.Path.Combine(stplugInPath, fileName);
                            CopyFileWithRetry(file, destPath);
                            luaCount++;
                            Log($"Copied {fileName} to stplug-in folder.");
                        }
                        else if (extension == ".manifest")
                        {
                            string destPath = System.IO.Path.Combine(depotCachePath, fileName);
                            CopyFileWithRetry(file, destPath);
                            manifestCount++;
                            Log($"Copied {fileName} to depotcache folder.");
                        }
                        else
                        {
                            Log($"Skipped {fileName} - unsupported file type (only .lua and .manifest are supported).");
                            failedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to copy file {System.IO.Path.GetFileName(file)}: {ex.Message}");
                        failedCount++;
                    }
                }

                // Show summary message
                if (luaCount > 0 || manifestCount > 0)
                {
                    string summary = $"Successfully imported:\n";
                    if (luaCount > 0) summary += $"  • {luaCount} Lua file(s)\n";
                    if (manifestCount > 0) summary += $"  • {manifestCount} Manifest file(s)";
                    if (failedCount > 0) summary += $"\n\nFailed to import: {failedCount} file(s)";
                    
                    MessageBox.Show(summary, "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    Log(GetLocalizedString("Log.DragDropComplete", luaCount, manifestCount));
                    
                    // Refresh game list
                    RefreshGameList();
                }
                else if (failedCount > 0)
                {
                    MessageBox.Show($"Failed to import {failedCount} file(s). Check log for details.", "Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show("No .lua or .manifest files were found in the dropped items.", "No Valid Files", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log($"Error in RootGrid_Drop: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"An error occurred during drag and drop: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyFileWithRetry(string sourcePath, string destPath)
        {
            try
            {
                // First try direct copy
                File.Copy(sourcePath, destPath, true);
            }
            catch (UnauthorizedAccessException)
            {
                // If direct copy fails due to permissions, try reading and writing separately
                try
                {
                    byte[] fileData = File.ReadAllBytes(sourcePath);
                    File.WriteAllBytes(destPath, fileData);
                }
                catch (Exception ex)
                {
                    // If that also fails, try using FileOptions.SequentialScan
                    try
                    {
                        using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
                        using (var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Write, 65536, FileOptions.SequentialScan))
                        {
                            sourceStream.CopyTo(destStream);
                        }
                    }
                    catch
                    {
                        throw new Exception($"Failed to copy file after multiple attempts: {ex.Message}");
                    }
                }
            }
        }

        // ================= Download and Install Update =================
        private async Task DownloadAndInstallUpdateAsync(string downloadUrl)
        {
            try
            {
                // Create a simple progress window
                var progressWindow = new Window
                {
                    Title = "Downloading Update",
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Width = 400,
                    Height = 150,
                    WindowStyle = WindowStyle.ToolWindow,
                    ShowInTaskbar = false
                };

                var textBlock = new System.Windows.Controls.TextBlock
                {
                    Text = "Downloading update...",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = System.Windows.TextAlignment.Center,
                    Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(20, 0, 20, 20)
                };

                var progressBar = new System.Windows.Controls.ProgressBar
                {
                    Height = 20,
                    IsIndeterminate = true,
                    Margin = new Thickness(20, 50, 20, 20)
                };

                var panel = new System.Windows.Controls.StackPanel
                {
                    Background = System.Windows.Media.Brushes.DarkGray,
                    Margin = new Thickness(20, 0, 20, 20)
                };

                panel.Children.Add(textBlock);
                panel.Children.Add(progressBar);
                progressWindow.Content = panel;
                progressWindow.Show();

                void OnProgressChanged(long downloaded, long total)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (total > 0)
                        {
                            progressBar.IsIndeterminate = false;
                            progressBar.Maximum = total;
                            progressBar.Value = downloaded;
                            textBlock.Text = $"Downloading update... ({((double)downloaded / total):P0})";
                        }
                    });
                }

                var (success, message) = await UpdateChecker.DownloadAndInstallUpdateAsync(downloadUrl, OnProgressChanged);

                progressWindow.Close();

                if (success)
                {
                    // Installer started and application will terminate silently.
                    return;
                }
                else
                {
                    MessageBox.Show(
                        $"Failed to download and install update:\n{message}",
                        "Update Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during update: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ================= Auto-Check for Updates on Startup =================
        private async Task CheckForUpdatesOnStartupAsync()
        {
            try
            {
                var updateInfo = await UpdateChecker.CheckForUpdatesAsync();

                if (updateInfo.HasUpdate)
                {
                    var dialog = new StyledMessageDialog(
                        "Update Available",
                        $"New Version Available!\n\nCurrent: {updateInfo.CurrentVersion}\nLatest: {updateInfo.LatestVersion}\n\n{updateInfo.ReleaseNotes}\n\nWould you like to download and install the update now?",
                        showCancel: true
                    );
                    dialog.Owner = this;
                    dialog.PrimaryButton.Content = "Download";
                    dialog.SecondaryButton.Content = "Cancel";
                    var result = dialog.ShowDialog();

                    if (result == true && !string.IsNullOrEmpty(updateInfo.DownloadUrl))
                    {
                        await DownloadAndInstallUpdateAsync(updateInfo.DownloadUrl);
                    }
                }
                else
                {
                    // Silent success - no popup if already up-to-date.
                    // Optionally, you can add a message box for testing:
                    // MessageBox.Show("You are already running the latest version.", "Up to Date", MessageBoxButton.OK, MessageBoxImage.Information);
                    Logger.Log("App is up to date (checked on startup)");
                }
            }
            catch (Exception ex)
            {
                // Silent fail on startup, but log it.
                Logger.Log($"Auto-check for updates failed on startup: {ex.Message}");
            }
        }

        // ================= Periodic Auto-Update Check =================
        private async Task StartAutoUpdateCheckAsync()
        {
            try
            {
                // Check if auto-update is enabled
                bool autoUpdateEnabled = IsAutoUpdateEnabled();
                if (!autoUpdateEnabled)
                {
                    Logger.Log("Periodic auto-update check is disabled");
                    return;
                }

                Logger.Log("Starting periodic auto-update check service...");

                // Initial delay of 5 minutes after startup (reduced from 30 for testing), then every 2 hours
                await Task.Delay(TimeSpan.FromMinutes(5));

                while (!Application.Current.Dispatcher.HasShutdownStarted)
                {
                    try
                    {
                        // Re-check if auto-update is still enabled before checking
                        if (!IsAutoUpdateEnabled())
                        {
                            Logger.Log("Auto-update disabled, stopping periodic checks");
                            break;
                        }

                        Logger.Log("Running periodic update check...");
                        var updateInfo = await UpdateChecker.CheckForUpdatesAsync();

                        if (updateInfo.HasUpdate)
                        {
                            Logger.Log($"Update found! Version: {updateInfo.LatestVersion}");
                            
                            // Show notification about available update
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                var dialog = new StyledMessageDialog(
                                    "Update Available",
                                    $"New Version Available!\n\nCurrent: {updateInfo.CurrentVersion}\nLatest: {updateInfo.LatestVersion}\n\n{updateInfo.ReleaseNotes}\n\nDownload and install now?",
                                    showCancel: true
                                );
                                dialog.Owner = this;
                                dialog.PrimaryButton.Content = "Download";
                                dialog.SecondaryButton.Content = "Cancel";
                                var result = dialog.ShowDialog();

                                if (result == true && !string.IsNullOrEmpty(updateInfo.DownloadUrl))
                                {
                                    _ = DownloadAndInstallUpdateAsync(updateInfo.DownloadUrl);
                                }
                            });

                            // Don't check again for 24 hours after finding an update
                            await Task.Delay(TimeSpan.FromHours(24));
                        }
                        else
                        {
                            Logger.Log("Periodic update check: App is up to date");
                            // Check again in 2 hours
                            await Task.Delay(TimeSpan.FromHours(2));
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Periodic update check failed: {ex.Message}");
                        // Retry after 1 hour on error
                        await Task.Delay(TimeSpan.FromHours(1));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error starting auto-update service: {ex.Message}");
            }
        }

        // ================= Helper method to check if auto-update is enabled =================
        private bool IsAutoUpdateEnabled()
        {
            try
            {
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SteamPluginManager"
                );
                string autoUpdateFile = Path.Combine(appDataPath, "autoupdate.txt");

                if (File.Exists(autoUpdateFile))
                {
                    string content = File.ReadAllText(autoUpdateFile).Trim().ToLower();
                    return (content == "true" || content == "1");
                }

                // Default to true if file doesn't exist
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error reading auto-update setting: {ex.Message}");
                return true; // Default to true on error
            }
        }
    }

    public class GameEntry : INotifyPropertyChanged
    {
        private string _name = "";
        private bool _isSelected;
        private bool _isInstalled;
        private string? _installationPath;
        private string? _screenshotUrl;
        // Simplified fields (publisher/image removed per request)

        public int AppId { get; set; }
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }
        public string? ScreenshotUrl
        {
            get => _screenshotUrl;
            set { _screenshotUrl = value; OnPropertyChanged(nameof(ScreenshotUrl)); }
        }
        public string FilePath { get; set; } = "";
        public string Genre { get; set; } = "";
        public string DRM { get; set; } = "";
        public string AntiCheat { get; set; } = "";
        // Removed detailed fields
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }
        public bool IsInstalled
        {
            get => _isInstalled;
            set { _isInstalled = value; OnPropertyChanged(nameof(IsInstalled)); OnPropertyChanged(nameof(InstallationStatus)); }
        }
        public string? InstallationPath
        {
            get => _installationPath;
            set { _installationPath = value; OnPropertyChanged(nameof(InstallationPath)); OnPropertyChanged(nameof(InstallationStatus)); }
        }
        public string InstallationStatus => IsInstalled ? "Installed" : "Not Installed";

        public event PropertyChangedEventHandler? PropertyChanged;
        public virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        
        public override string ToString()
        {
            return $"{AppId} - {Name}";
        }
    }
}
#pragma warning restore CS0103 // The name does not exist