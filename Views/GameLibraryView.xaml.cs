using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Microsoft.Win32;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Media;
using SteamPluginManager;
using SteamPluginManager.Services.Security;
using SteamPluginManager.Services.Steam;

namespace SteamPluginManager.Views
{
    public partial class GameLibraryView : UserControl
    {
        private MainWindow? _mainWindow;
        private string _lastSearchTerm = "";
        private const double MIN_CARD_WIDTH = 230;
        private ScrollViewer? _cardsScrollViewer;
        private bool _isInitialized = false;
        private bool _isUpdatingSearchBox = false; // Flag to prevent race conditions
        private bool _isUpdatingSelectionBatch = false; // Flag to prevent cascading UI refreshes during batch selection
        private string _selectedCategory = "All";
        private ICollectionView? _collectionView; // For sorted view
        private List<GameEntry> _lastFilteredGames = new();
        private int _projectedColumnCount = 0;
        private List<GameEntry>? _lastProjectedItems;

        public static readonly DependencyProperty ColumnCountProperty =
            DependencyProperty.Register(
                nameof(ColumnCount),
                typeof(int),
                typeof(GameLibraryView),
                new PropertyMetadata(3));

        public int ColumnCount
        {
            get => (int)GetValue(ColumnCountProperty);
            set => SetValue(ColumnCountProperty, value);
        }

        public ObservableCollection<GameRow> Rows { get; } = new ObservableCollection<GameRow>();

        // Long-press / Hold gesture for selection mode
        private readonly System.Windows.Threading.DispatcherTimer _holdTimer = new System.Windows.Threading.DispatcherTimer();
        private System.Windows.Threading.DispatcherTimer? _collectionChangedDebounceTimer;
        private System.Windows.Threading.DispatcherTimer? _searchDebounceTimer;
        private GameEntry? _holdTargetGame;
        private Border? _holdTargetBorder;
        private Point _holdStartPoint;
        private bool _isHoldTriggered;

        public static readonly DependencyProperty IsSelectionModeProperty =
            DependencyProperty.Register(
                nameof(IsSelectionMode),
                typeof(bool),
                typeof(GameLibraryView),
                new PropertyMetadata(false, OnIsSelectionModeChanged));

        public bool IsSelectionMode
        {
            get => (bool)GetValue(IsSelectionModeProperty);
            set => SetValue(IsSelectionModeProperty, value);
        }

        private static void OnIsSelectionModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is GameLibraryView view)
            {
                view.UpdateInfoPanel(autoExitIfEmpty: false);
            }
        }

        public GameLibraryView()
        {
            InitializeComponent();
            _holdTimer.Interval = TimeSpan.FromMilliseconds(450);
            _holdTimer.Tick += HoldTimer_Tick;
            Loaded += GameLibraryView_Loaded;
            Unloaded += GameLibraryView_Unloaded;
            SizeChanged += (s, e) => UpdateGridColumns();
        }

        private void GameLibraryView_Loaded(object sender, RoutedEventArgs e)
        {
            LoadGameLibrary();
            // Subscribe to language change event
            App.LanguageChanged += LanguageChanged_Handler;
        }

        private void GameLibraryView_Unloaded(object sender, RoutedEventArgs e)
        {
            CancelHoldGesture();
            _collectionChangedDebounceTimer?.Stop();
            _searchDebounceTimer?.Stop();
            _cardsScrollViewer = null;
            // Unsubscribe from language change event to prevent memory leak
            App.LanguageChanged -= LanguageChanged_Handler;
            
            // Unsubscribe from games collection changes
            if (_mainWindow?.Games != null)
            {
                _mainWindow.Games.CollectionChanged -= Games_CollectionChanged;
            }
        }

        private void LanguageChanged_Handler(object? sender, EventArgs e)
        {
            RefreshLanguageUI();
        }

        public async Task RefreshAsync()
        {
            // First, refresh the game list from disk asynchronously
            if (_mainWindow != null)
            {
                await _mainWindow.RefreshGameListAsync();
            }
            
            // Re-check installation status off the UI thread
            await RefreshInstallationStatusAsync();

            // Then refresh view and respect current filter (All / Installed / NotInstalled)
            if (_mainWindow != null)
            {
                ApplyCollectionFilters();
                UpdateInfoPanel();
                UpdateGridColumns();
            }
            else
            {
                LoadGameLibrary();
            }
        }

        public void Refresh()
        {
            _ = RefreshAsync();
        }

        private long _installStatusGeneration;

        private void RefreshInstallationStatus()
        {
            _ = RefreshInstallationStatusAsync();
        }

        private async Task RefreshInstallationStatusAsync(bool forceRefresh = false, System.Threading.CancellationToken ct = default)
        {
            long generation = System.Threading.Interlocked.Increment(ref _installStatusGeneration);

            try
            {
                if (_mainWindow?.Games == null) return;

                var installedMap = await SteamHelper.GetInstalledGamesMapAsync(forceRefresh, ct);

                // Generation guard: discard if a newer scan or refresh was started
                if (generation != System.Threading.Volatile.Read(ref _installStatusGeneration))
                {
                    Logger.Log($"[GameLibraryView] Discarding superseded installation status scan (generation {generation} vs current {_installStatusGeneration})");
                    return;
                }

                bool anyChanged = false;
                foreach (var game in _mainWindow.Games)
                {
                    if (generation != System.Threading.Volatile.Read(ref _installStatusGeneration))
                        return;

                    bool isInstalled = installedMap.TryGetValue(game.AppId, out string? installPath);
                    if (!isInstalled && game.ParentAppId.HasValue && installedMap.TryGetValue(game.ParentAppId.Value, out var parentPath))
                    {
                        isInstalled = true;
                        installPath = parentPath;
                    }

                    // Fallback safety check: Never demote an installed game to not installed if its files still exist on disk.
                    // This protects against Custom Launch (non-Steam), OnlineFix, cloud save sync, or transient Steam scanner states.
                    if (!isInstalled && game.IsInstalled)
                    {
                        if (!string.IsNullOrWhiteSpace(game.InstallationPath) && Directory.Exists(game.InstallationPath))
                        {
                            isInstalled = true;
                            installPath = game.InstallationPath;
                            installedMap[game.AppId] = installPath;
                        }
                        else
                        {
                            var customCfg = CustomLaunchService.GetConfig(game.AppId);
                            if (customCfg != null && customCfg.IsEnabled && !string.IsNullOrWhiteSpace(customCfg.ExecutablePath) && File.Exists(customCfg.ExecutablePath))
                            {
                                isInstalled = true;
                                installPath = !string.IsNullOrWhiteSpace(customCfg.WorkingDirectory) && Directory.Exists(customCfg.WorkingDirectory)
                                    ? customCfg.WorkingDirectory
                                    : Path.GetDirectoryName(customCfg.ExecutablePath);
                                installedMap[game.AppId] = installPath ?? "";
                            }
                            else
                            {
                                string? directPath = SteamHelper.GetGameInstallationPath(game.AppId, game.Name);
                                if (!string.IsNullOrWhiteSpace(directPath) && Directory.Exists(directPath))
                                {
                                    isInstalled = true;
                                    installPath = directPath;
                                    installedMap[game.AppId] = directPath;
                                }
                            }
                        }
                    }

                    if (game.IsInstalled != isInstalled || game.InstallationPath != installPath)
                    {
                        game.IsInstalled = isInstalled;
                        game.InstallationPath = installPath;
                        anyChanged = true;
                        Logger.Log($"[GameLibraryView] Updated IsInstalled status for {game.Name} (AppID: {game.AppId}): {game.IsInstalled}");
                    }
                }

                if (anyChanged)
                {
                    ApplyCollectionFilters();
                    UpdateInfoPanel();
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error in RefreshInstallationStatusAsync: {ex.Message}");
            }
        }

        private void LoadGameLibrary()
        {
            try
            {
                // DataContext sudah set oleh MainShell
                if (this.DataContext is MainWindow mainWindow)
                {
                    _mainWindow = mainWindow;
                    Logger.Log($"[GameLibraryView] LoadGameLibrary: DataContext retrieved, Games count: {_mainWindow.Games.Count}");
                    
                    _ = RefreshInstallationStatusAsync();
                    UpdateGridColumns();
                    SetupCollectionView();
                    
                    // Subscribe to games collection changes for real-time updates (prevent duplicate subscriptions)
                    _mainWindow.Games.CollectionChanged -= Games_CollectionChanged;
                    _mainWindow.Games.CollectionChanged += Games_CollectionChanged;
                    Logger.Log("[GameLibraryView] Subscribed to Games.CollectionChanged event");
                    
                    // Initialize SearchBox placeholder only on first initial load
                    if (SearchBox != null && !_isInitialized)
                    {
                        _isUpdatingSearchBox = true;
                        SearchBox.Text = "";
                        _isUpdatingSearchBox = false;
                        Logger.Log("[GameLibraryView] SearchBox initialized");
                    }
                    _isInitialized = true;
                    
                    // Register language mappings
                    RegisterGameLibraryElements();
                    
                    // Apply current language
                    RefreshLanguageUI();
                    
                    // Final verification
                    if (GameCards?.ItemsSource == Rows)
                    {
                        Logger.Log("[GameLibraryView] ✓ VERIFIED: GameCards.ItemsSource is correctly set to Rows");
                    }
                    else
                    {
                        Logger.Log($"[GameLibraryView] ⚠ WARNING: GameCards.ItemsSource verification failed. GameCards={GameCards}, ItemsSource={GameCards?.ItemsSource}, Rows={Rows}");
                    }
                    
                    Logger.Log("[GameLibraryView] LoadGameLibrary complete with real-time monitoring enabled");
                }
                else
                {
                    Logger.Log("[GameLibraryView] ERROR: DataContext is not MainWindow!");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Exception in LoadGameLibrary: {ex.Message}\n{ex.StackTrace}");
                ModernMessageBox.Show($"Failed to load games: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Games_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            try
            {
                // Ensure ItemsSource is properly set
                EnsureItemsSourceIsSet();
                
                // Debounce UI info and column update to prevent repeated expensive operations during batch changes
                if (_collectionChangedDebounceTimer == null)
                {
                    _collectionChangedDebounceTimer = new System.Windows.Threading.DispatcherTimer(
                        System.Windows.Threading.DispatcherPriority.Normal,
                        Dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(50)
                    };
                    _collectionChangedDebounceTimer.Tick += (s, args) =>
                    {
                        _collectionChangedDebounceTimer.Stop();
                        ApplyCollectionFilters();
                        UpdateInfoPanel();
                        UpdateGridColumns();
                    };
                }

                _collectionChangedDebounceTimer.Stop();
                _collectionChangedDebounceTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error in Games_CollectionChanged: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void EnsureItemsSourceIsSet()
        {
            if (GameCards == null) return;
            
            try
            {
                // In row-based virtualization, GameCards binds to Rows
                if (GameCards.ItemsSource != Rows)
                {
                    Logger.Log("[GameLibraryView] Fixing ItemsSource: Re-setting GameCards.ItemsSource to Rows");
                    GameCards.ItemsSource = Rows;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error in EnsureItemsSourceIsSet: {ex.Message}");
            }
        }

        private void SetupCollectionView()
        {
            if (_mainWindow == null) return;

            try
            {
                // Set ItemsSource to Rows - CRITICAL: This ensures row-based virtualization works
                if (GameCards != null)
                {
                    GameCards.ItemsSource = Rows;
                    Logger.Log("[GameLibraryView] ✓ GameCards.ItemsSource set to Rows projection");
                }
                else
                {
                    Logger.Log("[GameLibraryView] WARNING: GameCards is NULL during SetupCollectionView - will retry on next refresh");
                }

                // Also initialize _collectionView as default view for backward compatibility
                try
                {
                    _collectionView = CollectionViewSource.GetDefaultView(_mainWindow.Games);
                }
                catch { }

                // Apply active filters immediately (Category and Search)
                ApplyCollectionFilters();
                
                Logger.Log("[GameLibraryView] CollectionView setup complete");
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] ERROR in SetupCollectionView: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Public method to refresh collection view - now with real-time monitoring via CollectionChanged event
        /// </summary>
        public void RefreshCollectionView()
        {
            try
            {
                Logger.Log("[GameLibraryView] Manual refresh triggered");
                
                // First ensure ItemsSource is set
                EnsureItemsSourceIsSet();
                
                ApplyCollectionFilters();
                
                Logger.Log("[GameLibraryView] ✓ Collection view refreshed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error refreshing collection view: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void RegisterGameLibraryElements()
        {
            try
            {
                Logger.Log("[GameLibraryView] Starting RegisterGameLibraryElements");
                
                // Toolbar action buttons
                if (SelectAllText != null)
                    LanguageHelper.RegisterTextBlock(SelectAllText, "GameLibrary.SelectAll");
                else
                    Logger.Log("[GameLibraryView] WARNING: SelectAllText is NULL");
                    
                if (RefreshText != null)
                    LanguageHelper.RegisterTextBlock(RefreshText, "GameLibrary.Refresh");
                else
                    Logger.Log("[GameLibraryView] WARNING: RefreshText is NULL");

                if (ImportText != null)
                    LanguageHelper.RegisterTextBlock(ImportText, "GameLibrary.Import");
                else
                    Logger.Log("[GameLibraryView] WARNING: ImportText is NULL");
                    
                if (BackButtonText != null)
                    LanguageHelper.RegisterTextBlock(BackButtonText, "GameLibrary.Back");
                else
                    Logger.Log("[GameLibraryView] WARNING: BackButtonText is NULL");
                    
                if (HeaderSelectionCounterText != null)
                    LanguageHelper.RegisterTextBlock(HeaderSelectionCounterText, "GameLibrary.SelectionCounter");
                else
                    Logger.Log("[GameLibraryView] WARNING: HeaderSelectionCounterText is NULL");
                
                // Buttons in game list area
                if (BackupBtnText != null)
                    LanguageHelper.RegisterTextBlock(BackupBtnText, "GameLibrary.Backup");
                else
                    Logger.Log("[GameLibraryView] WARNING: BackupBtnText is NULL");
                
                // Activity log removed — no registration needed
                
                Logger.Log("[GameLibraryView] All text elements registered successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error registering text elements: {ex.Message}");
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    return typedChild;
                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void UpdateGridColumns()
        {
            try
            {
                // Get available width
                var scrollViewer = _cardsScrollViewer ??= FindVisualChild<ScrollViewer>(GameCards);
                double availableWidth = scrollViewer != null && scrollViewer.ViewportWidth > 0
                    ? scrollViewer.ViewportWidth
                    : (GameCards?.ActualWidth > 0 ? GameCards.ActualWidth : (ActualWidth > 0 ? ActualWidth - 28 : 0));
                if (availableWidth <= 0)
                    return;

                const double cardMarginRight = 14;
                const double targetCardWidth = 230;
                const double rightClearance = 36;
                double usableWidth = Math.Max(targetCardWidth + cardMarginRight, availableWidth - rightClearance);





                // Calculate columns based on available width and minimum card width
                int columns = Math.Max(1, (int)(usableWidth / (targetCardWidth + cardMarginRight)));

                bool columnCountChanged = columns != ColumnCount;
                ColumnCount = columns;

                if (columnCountChanged)
                {
                    UpdateRowsProjection();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating grid columns: {ex.Message}");
            }
        }

        // Selection & Management
        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_mainWindow != null)
            {
                _isUpdatingSelectionBatch = true;
                try
                {
                    foreach (var game in _mainWindow.Games)
                    {
                        game.IsSelected = true;
                    }
                }
                finally
                {
                    _isUpdatingSelectionBatch = false;
                }
            }

            if (!IsSelectionMode)
            {
                IsSelectionMode = true;
            }
            else
            {
                UpdateInfoPanel();
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_mainWindow != null)
            {
                if (RefreshBtn != null) RefreshBtn.IsEnabled = false;
                try
                {
                    await RefreshAsync();
                    UpdateActivityLog("Game list refreshed");
                }
                finally
                {
                    if (RefreshBtn != null) RefreshBtn.IsEnabled = true;
                }
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Title = "Import Lua, Manifest, Zip, or Backup files",
                    Multiselect = true,
                    Filter = "Supported Files (*.lua;*.manifest;*.zip;*.hzbak;*.spmb)|*.lua;*.manifest;*.zip;*.hzbak;*.spmb|HZ Backup (*.hzbak)|*.hzbak|SPM Backup (*.spmb)|*.spmb|Lua Files (*.lua)|*.lua|Manifest Files (*.manifest)|*.manifest|Zip Files (*.zip)|*.zip|All Files (*.*)|*.*"
                };

                if (openFileDialog.ShowDialog() != true)
                {
                    return;
                }

                if (openFileDialog.FileNames == null || openFileDialog.FileNames.Length == 0)
                {
                    return;
                }

                ProcessDroppedFiles(openFileDialog.FileNames);
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error in ImportButton_Click: {ex.Message}");
                ModernMessageBox.Show($"Error opening import dialog: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            // Activity log removed — inform user
            ModernMessageBox.Show("Activity log has been disabled.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (_mainWindow != null)
            {
                _isUpdatingSelectionBatch = true;
                try
                {
                    foreach (var game in _mainWindow.Games)
                    {
                        game.IsSelected = false;
                    }
                }
                finally
                {
                    _isUpdatingSelectionBatch = false;
                }

                IsSelectionMode = false;
                UpdateActivityLog("Cleared all selections");
            }
        }

        private void BackupSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_mainWindow == null) return;

            var selected = _mainWindow.Games.Where(g => g.IsSelected).ToList();
            if (selected.Count == 0)
            {
                ModernMessageBox.Show("Please select games to backup", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "HZ Backup (*.hzbak)|*.hzbak|SPM Backup (*.spmb)|*.spmb|All Files (*.*)|*.*",
                    Title = "Save Backup",
                    FileName = $"HZBackup_{DateTime.Now:yyyyMMdd}.hzbak"
                };

                if (saveFileDialog.ShowDialog() != true) return;

                // Create a zip archive in memory
                using (var memoryStream = new MemoryStream())
                {
                    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                    {
                        foreach (var game in selected)
                        {
                            // Add the lua file
                            if (File.Exists(game.FilePath))
                            {
                                archive.CreateEntryFromFile(game.FilePath, Path.GetFileName(game.FilePath));
                            }
                            
                            // Add associated manifest files
                            foreach (var manifest in GetManifestsForLuaFile(game.FilePath))
                            {
                                if (File.Exists(manifest))
                                {
                                    archive.CreateEntryFromFile(manifest, Path.GetFileName(manifest));
                                }
                            }
                        }
                    }

                    // Encrypt the memory stream to the file
                    BackupCryptoService.EncryptStream(memoryStream, saveFileDialog.FileName);
                }

                ModernMessageBox.Show(
                    $"Backup created successfully!\n\nGames: {selected.Count}\nFile: {Path.GetFileName(saveFileDialog.FileName)}", 
                    "Backup Complete", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
                
                // Log the backup
                UpdateActivityLog($"Backup created: {Path.GetFileName(saveFileDialog.FileName)} ({selected.Count} game(s))");
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Backup failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool ProcessBackupFile(string backupFilePath, string pluginPath, string luaPath, string depotCachePath, string depotCachePathOld)
        {
            try
            {
                using (var memoryStream = BackupCryptoService.DecryptStream(backupFilePath))
                {
                    int luaFiles = 0;
                    int manifestFiles = 0;

                    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;

                            if (entry.FullName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                            {
                                string targetPlugin = Path.Combine(pluginPath, entry.Name);
                                string targetLua = Path.Combine(luaPath, entry.Name);
                                entry.ExtractToFile(targetPlugin, overwrite: true);
                                entry.ExtractToFile(targetLua, overwrite: true);
                                luaFiles++;
                                Logger.Log($"[GameLibraryView] Restored backup lua file: {entry.Name}");
                            }
                            else if (entry.FullName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                            {
                                string targetDepot = Path.Combine(depotCachePath, entry.Name);
                                string targetDepotOld = Path.Combine(depotCachePathOld, entry.Name);
                                entry.ExtractToFile(targetDepot, overwrite: true);
                                entry.ExtractToFile(targetDepotOld, overwrite: true);
                                manifestFiles++;
                                Logger.Log($"[GameLibraryView] Restored backup manifest file: {entry.Name}");
                            }
                        }
                    }

                    Logger.Log($"[GameLibraryView] Backup restored: {Path.GetFileName(backupFilePath)} ({luaFiles} lua, {manifestFiles} manifests)");
                    return luaFiles > 0 || manifestFiles > 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Failed to process backup file {backupFilePath}: {ex.Message}");
                return false;
            }
        }

        private IEnumerable<string> GetManifestsForLuaFile(string luaFilePath)
        {
            var manifests = new List<string>();
            try
            {
                string content = File.ReadAllText(luaFilePath);
                var matches = System.Text.RegularExpressions.Regex.Matches(content, @"setManifestid\((\d+),""(\d+)""\)");
                
                string steamPath = SteamHelper.GetSteamPath();
                if (!string.IsNullOrEmpty(steamPath))
                {
                    // Check both new and old paths
                    string depotCachePath = Path.Combine(steamPath, "depotcache");
                    string depotCachePathOld = Path.Combine(steamPath, "config", "depotcache");
                    
                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        if (match.Groups.Count > 2)
                        {
                            string manifestId = match.Groups[2].Value;
                            
                            // Check new path first
                            string manifestPath = Path.Combine(depotCachePath, $"{manifestId}.manifest");
                            if (File.Exists(manifestPath))
                            {
                                manifests.Add(manifestPath);
                            }
                            // If not found in new path, check old path
                            else
                            {
                                string manifestPathOld = Path.Combine(depotCachePathOld, $"{manifestId}.manifest");
                                if (File.Exists(manifestPathOld))
                                {
                                    manifests.Add(manifestPathOld);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            
            return manifests;
        }

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_mainWindow == null) return;

            var selected = _mainWindow.Games.Where(g => g.IsSelected).ToList();
            if (selected.Count == 0)
            {
                ModernMessageBox.Show("Please select games to delete", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var dlg = new DeleteChoiceDialog(selected.Count) { Owner = Window.GetWindow(this) };
                if (dlg.ShowDialog() != true)
                {
                    return;
                }

                // Show progress overlay
                string titleText = dlg.SelectedAction switch
                {
                    DeleteChoiceDialog.DeleteAction.ManifestAndLua => "Deleting Manifest & Lua",
                    DeleteChoiceDialog.DeleteAction.Uninstall => "Uninstalling Games",
                    DeleteChoiceDialog.DeleteAction.Both => "Deleting & Uninstalling",
                    _ => "Processing"
                };

                string actionText = dlg.SelectedAction switch
                {
                    DeleteChoiceDialog.DeleteAction.ManifestAndLua => $"Deleting Lua & Manifest for {selected.Count} game(s)...",
                    DeleteChoiceDialog.DeleteAction.Uninstall => $"Triggering uninstall for {selected.Count} game(s)...",
                    DeleteChoiceDialog.DeleteAction.Both => $"Deleting files and uninstalling {selected.Count} game(s)...",
                    _ => "Processing..."
                };
                
                ShowDeleteProgressOverlay(true, actionText, titleText);
                UpdateDeleteProgressStatus(actionText, 2);

                // Call actual delete method from MainWindow
                await _mainWindow.DeleteSelectedGamesAsync(dlg.SelectedAction);
                
                UpdateDeleteProgressStatus("Refreshing library...", 4);
                
                // Refresh GameLibraryView UI after delete completes while respecting active filter
                Logger.Log("[GameLibraryView] Auto-refreshing library view after delete");
                await RefreshInstallationStatusAsync();
                ApplyCollectionFilters();
                UpdateInfoPanel();
                UpdateGridColumns();
                
                // Log the deletion
                UpdateActivityLog($"{selected.Count} game(s) deleted");
                
                // Hide progress overlay
                await Task.Delay(500);
                ShowDeleteProgressOverlay(false);
            }
            catch (Exception ex)
            {
                ShowDeleteProgressOverlay(false);
                ModernMessageBox.Show($"Delete failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Search & Filter
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Ignore TextChanged if we're manually updating the searchbox (to prevent race conditions)
            if (_isUpdatingSearchBox)
                return;

            if (sender is TextBox textBox && _mainWindow != null)
            {
                string text = textBox.Text?.Trim() ?? "";
                if (text == "Search games..." || text == "Search games by title or genre...")
                {
                    text = "";
                }
                _lastSearchTerm = text;

                if (_searchDebounceTimer == null)
                {
                    _searchDebounceTimer = new System.Windows.Threading.DispatcherTimer(
                        System.Windows.Threading.DispatcherPriority.Normal,
                        Dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(250)
                    };
                    _searchDebounceTimer.Tick += (s, args) =>
                    {
                        _searchDebounceTimer.Stop();
                        ExecuteSearchFilter();
                    };
                }

                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
            }
        }

        private void ExecuteSearchFilter()
        {
            if (_mainWindow == null) return;

            if (string.IsNullOrWhiteSpace(_lastSearchTerm))
            {
                Logger.Log("[SearchBox] Showing all games");
            }
            else
            {
                Logger.Log($"[SearchBox] Filtered games for term: {_lastSearchTerm}");
            }

            ApplyCollectionFilters();
        }

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                if (textBox.Text == "Search games..." || textBox.Text == "Search games by title or genre...")
                {
                    try
                    {
                        _isUpdatingSearchBox = true;
                        textBox.Text = "";
                        Logger.Log("[SearchBox] GotFocus - cleared placeholder");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[SearchBox] Error in GotFocus: {ex.Message}");
                    }
                    finally
                    {
                        _isUpdatingSearchBox = false;
                    }
                }
                textBox.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["ForegroundBrush"];
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && (textBox.Text == "Search games..." || textBox.Text == "Search games by title or genre..."))
            {
                try
                {
                    _isUpdatingSearchBox = true;
                    textBox.Text = "";
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SearchBox] Error in LostFocus: {ex.Message}");
                }
                finally
                {
                    _isUpdatingSearchBox = false;
                }
            }
            // Handled cleanly by modern XAML overlay placeholder
        }

        private void CategoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _selectedCategory = tag;
                Logger.Log($"[CategoryFilter] Selected category changed to: {tag}");
                ApplyCollectionFilters();
            }
        }

        private void ApplyCollectionFilters()
        {
            if (_mainWindow?.Games == null) return;

            // Ensure _selectedCategory is always in sync with CategoryFilterComboBox
            if (CategoryFilterComboBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _selectedCategory = tag;
            }

            try
            {
                string effectiveSearch = _lastSearchTerm;
                if (effectiveSearch == "Search games..." || effectiveSearch == "Search games by title or genre...")
                {
                    effectiveSearch = "";
                }

                var filtered = _mainWindow.Games
                    .Where(game =>
                    {
                        if (game == null) return false;

                        bool matchesSearch = string.IsNullOrWhiteSpace(effectiveSearch) ||
                                             (!string.IsNullOrEmpty(game.Name) && game.Name.Contains(effectiveSearch, StringComparison.OrdinalIgnoreCase)) ||
                                             (!string.IsNullOrEmpty(game.Genre) && game.Genre.Contains(effectiveSearch, StringComparison.OrdinalIgnoreCase));

                        bool matchesCategory = _selectedCategory switch
                        {
                            "Installed" => game.IsInstalled,
                            "NotInstalled" => !game.IsInstalled,
                            _ => true,
                        };

                        return matchesSearch && matchesCategory;
                    })
                    .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Logger.Log($"[GameLibraryView] Filter applied: Category={_selectedCategory}, Search='{effectiveSearch}', Matches={filtered.Count}/{_mainWindow.Games.Count}");

                _lastFilteredGames = filtered;
                UpdateRowsProjection();

                if (Rows.Count > 0 && GameCards != null)
                {
                    GameCards.ScrollIntoView(Rows[0]);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error applying collection filters: {ex.Message}\n{ex.StackTrace}");
            }

            try
            {
                UpdateInfoPanel();
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error updating info panel after applying filters: {ex.Message}");
            }
        }

        // Event Handlers
        private void BackToDashboard_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToDashboard();
        }

        private void CancelHoldGesture()
        {
            if (_holdTimer.IsEnabled)
            {
                _holdTimer.Stop();
            }
            _holdTargetGame = null;
            _holdTargetBorder = null;
            _isHoldTriggered = false;
        }

        private void GameCards_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ViewportWidthChange != 0)
            {
                UpdateGridColumns();
            }

            if (e.VerticalChange != 0)
            {
                CancelHoldGesture();
            }
        }

        private void GameCard_Unloaded(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, _holdTargetBorder))
            {
                CancelHoldGesture();
            }
        }

        private void HoldTimer_Tick(object? sender, EventArgs e)
        {
            _holdTimer.Stop();

            // Guard against recycled containers: verify that border is still loaded and still represents the target game
            if (_holdTargetBorder != null && _holdTargetGame != null)
            {
                if (!ReferenceEquals(_holdTargetBorder.DataContext, _holdTargetGame) || !_holdTargetBorder.IsLoaded)
                {
                    CancelHoldGesture();
                    return;
                }

                _isHoldTriggered = true;
                _holdTargetGame.IsSelected = true;
                if (!IsSelectionMode)
                {
                    IsSelectionMode = true;
                }
                UpdateInfoPanel();
            }
        }

        private void GameCard_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isHoldTriggered = false;

            if (sender is Border border && border.DataContext is GameEntry game)
            {
                // Only start hold timer if not already in selection mode
                if (!IsSelectionMode)
                {
                    _holdTargetBorder = border;
                    _holdTargetGame = game;
                    _holdStartPoint = e.GetPosition(border);
                    _holdTimer.Stop();
                    _holdTimer.Start();
                }
            }
        }

        private void GameCard_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_holdTimer.IsEnabled && _holdTargetBorder != null)
            {
                var currentPoint = e.GetPosition(_holdTargetBorder);
                var diff = currentPoint - _holdStartPoint;
                if (Math.Abs(diff.X) > 8 || Math.Abs(diff.Y) > 8)
                {
                    // User moved/dragged mouse away, cancel hold
                    CancelHoldGesture();
                }
            }
        }

        private void GameCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            CancelHoldGesture();
        }

        private void GameCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            bool holdTriggered = _isHoldTriggered;
            CancelHoldGesture();

            if (holdTriggered)
            {
                // Hold gesture completed and activated selection mode, suppress regular click
                e.Handled = true;
                return;
            }

            if (sender is Border border && border.DataContext is GameEntry game)
            {
                if (IsSelectionMode)
                {
                    game.IsSelected = !game.IsSelected;
                    UpdateInfoPanel();
                }
                else
                {
                    WindowNavigator.NavigateToGameLibraryDetail(game);
                }
            }
        }

        private void ItemCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingSelectionBatch) return;
            UpdateInfoPanel();
        }

        // Activity log removed — keep a no-op method so callers don't need changes
        public void UpdateActivityLog(string message)
        {
            // Intentionally left blank
        }

        private void UpdateActionButtons(int selectedCount)
        {
            bool hasSelection = selectedCount > 0;

            // Show/hide contextual action buttons based on selection count
            if (BackupBtn != null) BackupBtn.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            if (DeleteBtn != null) DeleteBtn.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            if (ClearAllBtn != null) ClearAllBtn.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

            // Show Select All only if in selection mode
            if (SelectAllBtn != null) SelectAllBtn.Visibility = IsSelectionMode ? Visibility.Visible : Visibility.Collapsed;

            // Temporarily hide global action buttons when selection is active to keep the toolbar clean
            var defaultActionsVisibility = IsSelectionMode ? Visibility.Collapsed : Visibility.Visible;
            if (RefreshBtn != null) RefreshBtn.Visibility = defaultActionsVisibility;
            if (ImportBtn != null) ImportBtn.Visibility = defaultActionsVisibility;
        }

        private void UpdateInfoPanel(bool autoExitIfEmpty = true)
        {
            if (_mainWindow == null) return;

            var selectedCount = _mainWindow.Games.Count(g => g.IsSelected);
            var totalCount = _mainWindow.Games.Count;

            // When all selections are cleared, exit selection mode automatically
            if (autoExitIfEmpty && IsSelectionMode && selectedCount == 0)
            {
                IsSelectionMode = false;
                return;
            }
            
            // Update selection counter texts with current language
            UpdateSelectionCounterText(selectedCount, totalCount);
            
            // Update button visibility
            UpdateActionButtons(selectedCount);
        }

        private void UpdateSelectionCounterText(int selectedCount, int totalCount)
        {
            try
            {
                string counterText = string.Format(
                    Application.Current.Resources["GameLibrary.SelectionCounter"]?.ToString() ?? "{0}/{1} games selected",
                    selectedCount,
                    totalCount
                );
                
                if (HeaderSelectionCounterText != null)
                    HeaderSelectionCounterText.Text = counterText;

                if (HeaderSelectionBadge != null)
                    HeaderSelectionBadge.Visibility = selectedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating selection counter: {ex.Message}");
            }
        }

        public void RefreshLanguageUI()
        {
            try
            {
                Logger.Log("[GameLibraryView] Refreshing language UI");
                LanguageHelper.UpdateAllRegisteredTextBlocks();
                
                // Update selection counter with new language
                if (_mainWindow != null)
                {
                    var selectedCount = _mainWindow.Games.Count(g => g.IsSelected);
                    var totalCount = _mainWindow.Games.Count;
                    UpdateSelectionCounterText(selectedCount, totalCount);
                }
                
                Logger.Log("[GameLibraryView] Language UI refresh completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error refreshing language UI: {ex.Message}");
            }
        }

        private ObservableCollection<GameEntry> GetSortedGames(IEnumerable<GameEntry> games)
        {
            // Deprecated: Now binding directly to MainWindow.Games collection
            // This method kept for backward compatibility if needed
            var sorted = games.OrderBy(g => g.Name ?? "").ToList();
            return new ObservableCollection<GameEntry>(sorted);
        }

        private void ShowDeleteProgressOverlay(bool show, string status = "Processing...", string title = "Deleting Games")
        {
            try
            {
                Logger.Log($"[GameLibraryView] ShowDeleteProgressOverlay called with show={show}, status='{status}', title='{title}'");

                // 1. Unified Modern Global Modal System (OperationProgressModalView)
                var modal = WindowNavigator.GetProgressModal();
                if (modal != null)
                {
                    if (show)
                    {
                        string step1 = "Analyze Games";
                        string step2 = "Delete Manifest";
                        string step3 = "Remove Lua";
                        string step4 = "Update Library";
                        string glyph = "\uE74D";

                        if (status.Contains("Uninstall", StringComparison.OrdinalIgnoreCase))
                        {
                            title = "Uninstalling Games";
                            step1 = "Verify Install";
                            step2 = "Trigger Steam";
                            step3 = "Clean Directory";
                            step4 = "Update Library";
                        }
                        else if (status.Contains("Both", StringComparison.OrdinalIgnoreCase) || status.Contains("&", StringComparison.OrdinalIgnoreCase))
                        {
                            title = "Uninstall & Delete";
                            step1 = "Trigger Steam";
                            step2 = "Delete Manifest";
                            step3 = "Remove Lua";
                            step4 = "Update Library";
                        }

                        modal.Configure(title, step1, step2, step3, step4, glyph);
                        modal.ShowModal(status, "Please wait while the operation completes...");
                        modal.UpdateProgressStep(1);
                    }
                    else
                    {
                        modal.HideModal();
                    }
                }

                // 2. In-view fallback overlay
                if (DeleteProgressOverlay != null)
                {
                    DeleteProgressOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (FindName("DeleteProgressOverlay") is Grid overlayGrid)
                {
                    overlayGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                }

                if (show)
                {
                    if (DeleteProgressStatus != null) DeleteProgressStatus.Text = status;
                    if (DeleteProgressTitle != null && !string.IsNullOrWhiteSpace(title)) DeleteProgressTitle.Text = title;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Exception in ShowDeleteProgressOverlay: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void UpdateProgressOverlayTitle(string title)
        {
            try
            {
                if (DeleteProgressTitle != null)
                {
                    DeleteProgressTitle.Text = title;
                }
                else if (FindName("DeleteProgressTitle") is TextBlock titleText)
                {
                    titleText.Text = title;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error updating progress overlay title: {ex.Message}");
            }
        }

        private void UpdateDeleteProgressStatus(string message, int step = 0)
        {
            try
            {
                var modal = WindowNavigator.GetProgressModal();
                if (modal != null && modal.IsOpen)
                {
                    if (step > 0) modal.UpdateProgressStep(step);
                    modal.UpdateStatus(message, message);
                    modal.SetProgress(step > 0 ? (step * 25) : 50, 100, step == 0);
                }

                if (DeleteProgressStatus != null)
                {
                    DeleteProgressStatus.Text = message;
                }
                else if (FindName("DeleteProgressStatus") is TextBlock statusText)
                {
                    statusText.Text = message;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error updating delete progress status: {ex.Message}");
            }
        }

        private void ShowImportProgressOverlay(bool show, string status = "Processing...")
        {
            try
            {
                Logger.Log($"[GameLibraryView] ShowImportProgressOverlay called with show={show}, status='{status}'");

                // 1. Unified Modern Global Modal System (OperationProgressModalView)
                var modal = WindowNavigator.GetProgressModal();
                if (modal != null)
                {
                    if (show)
                    {
                        modal.Configure("Importing Files", "Validate Files", "Extract Archives", "Deploy Lua & Manifest", "Update Library", "\uE896");
                        modal.ShowModal(status, "Please wait while files are processed and deployed...");
                        modal.UpdateProgressStep(1);
                    }
                    else
                    {
                        modal.HideModal();
                    }
                }

                // 2. In-view fallback overlay
                if (ImportProgressOverlay != null)
                {
                    ImportProgressOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (FindName("ImportProgressOverlay") is Grid overlayGrid)
                {
                    overlayGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                }

                if (show)
                {
                    if (ImportProgressStatus != null) ImportProgressStatus.Text = status;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Exception in ShowImportProgressOverlay: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void UpdateImportProgressStatus(string message, int step = 0)
        {
            try
            {
                var modal = WindowNavigator.GetProgressModal();
                if (modal != null && modal.IsOpen)
                {
                    if (step > 0) modal.UpdateProgressStep(step);
                    modal.UpdateStatus(message, message);
                    modal.SetProgress(step > 0 ? (step * 25) : 50, 100, step == 0);
                }

                if (ImportProgressStatus != null)
                {
                    ImportProgressStatus.Text = message;
                }
                else if (FindName("ImportProgressStatus") is TextBlock statusText)
                {
                    statusText.Text = message;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error updating import progress status: {ex.Message}");
            }
        }

        // Drag & Drop Handlers
        private void UserControl_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                
                // Check if files are valid (.lua, .manifest, .zip, .hzbak, or .spmb)
                bool hasValidFiles = files.Any(f => 
                    f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".hzbak", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".spmb", StringComparison.OrdinalIgnoreCase));
                
                if (hasValidFiles)
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                }
            }
        }

        private void UserControl_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    ProcessDroppedFiles(files);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error in Drop handler: {ex.Message}");
                ModernMessageBox.Show($"Error processing dropped files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ProcessDroppedFiles(string[] files)
        {
            try
            {
                ShowImportProgressOverlay(true, "Validating dropped files...");
                UpdateImportProgressStatus("Validating dropped files...", 1);
                
                string steamPath = SteamHelper.GetSteamPath();
                if (string.IsNullOrEmpty(steamPath))
                {
                    UpdateActivityLog("Error: Steam path not found");
                    ModernMessageBox.Show("Steam path not found", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ShowImportProgressOverlay(false);
                    return;
                }

                string pluginPath = Path.Combine(steamPath, "config", "stplug-in");
                string luaPath = Path.Combine(steamPath, "config", "lua");
                string depotCachePath = Path.Combine(steamPath, "depotcache");
                string depotCachePathOld = Path.Combine(steamPath, "config", "depotcache");

                // Ensure directories exist
                Directory.CreateDirectory(pluginPath);
                Directory.CreateDirectory(luaPath);
                Directory.CreateDirectory(depotCachePath);
                Directory.CreateDirectory(depotCachePathOld);

                var (successCount, failureCount, processedFiles) = await Task.Run(() =>
                {
                    int sCount = 0;
                    int fCount = 0;
                    var pFiles = new List<string>();

                    foreach (string file in files)
                    {
                        try
                        {
                            if (file.EndsWith(".hzbak", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".spmb", StringComparison.OrdinalIgnoreCase))
                            {
                                Dispatcher.Invoke(() => UpdateImportProgressStatus($"Restoring backup: {Path.GetFileName(file)}...", 2));
                                if (ProcessBackupFile(file, pluginPath, luaPath, depotCachePath, depotCachePathOld))
                                {
                                    pFiles.Add(Path.GetFileName(file));
                                    sCount++;
                                }
                                else
                                {
                                    fCount++;
                                }
                            }
                            else if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            {
                                Dispatcher.Invoke(() => UpdateImportProgressStatus($"Extracting archive: {Path.GetFileName(file)}...", 2));
                                // Extract zip and process contents
                                if (ProcessZipFile(file, pluginPath, luaPath, depotCachePath, depotCachePathOld))
                                {
                                    pFiles.Add(Path.GetFileName(file));
                                    sCount++;
                                }
                                else
                                {
                                    fCount++;
                                }
                            }
                            else if (file.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                            {
                                Dispatcher.Invoke(() => UpdateImportProgressStatus($"Deploying: {Path.GetFileName(file)}...", 3));
                                // Copy .lua file to both stplug-in and lua folders
                                string destPath = Path.Combine(pluginPath, Path.GetFileName(file));
                                string destLuaPath = Path.Combine(luaPath, Path.GetFileName(file));
                                File.Copy(file, destPath, true);
                                File.Copy(file, destLuaPath, true);
                                pFiles.Add(Path.GetFileName(file));
                                sCount++;
                                Logger.Log($"[GameLibraryView] Copied .lua file: {Path.GetFileName(file)} → {pluginPath} and {luaPath}");
                            }
                            else if (file.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                            {
                                Dispatcher.Invoke(() => UpdateImportProgressStatus($"Deploying: {Path.GetFileName(file)}...", 3));
                                // Copy .manifest file to both depotcache paths
                                string destPath = Path.Combine(depotCachePath, Path.GetFileName(file));
                                string destPathOld = Path.Combine(depotCachePathOld, Path.GetFileName(file));
                                File.Copy(file, destPath, true);
                                File.Copy(file, destPathOld, true);
                                pFiles.Add(Path.GetFileName(file));
                                sCount++;
                                Logger.Log($"[GameLibraryView] Copied .manifest file: {Path.GetFileName(file)} → {destPath} and {destPathOld}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[GameLibraryView] Error processing file {file}: {ex.Message}");
                            fCount++;
                        }
                    }

                    return (sCount, fCount, pFiles);
                });

                // Refresh the game library
                UpdateImportProgressStatus("Refreshing library...", 4);
                
                // Reload games from file system and refresh view respecting filters
                Logger.Log("[GameLibraryView] Triggering UI refresh after file drop import");
                await RefreshAsync();

                // Show results
                ShowImportProgressOverlay(false);
                
                if (failureCount == 0 && successCount > 0)
                {
                    string message = $"✓ Successfully processed {successCount} file(s):\n" + 
                                   string.Join("\n", processedFiles.Select(f => $"  • {f}"));
                    UpdateActivityLog($"Imported {successCount} file(s)");
                    ModernMessageBox.Show(message, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (failureCount > 0)
                {
                    UpdateActivityLog($"Import failed: {failureCount} file(s) could not be processed");
                    ModernMessageBox.Show(
                        $"Operation failed: {failureCount} file(s) could not be processed.\n\n" +
                        $"Valid files must be:\n" +
                        $"  • .lua files\n" +
                        $"  • .manifest files\n" +
                        $"  • .zip files (containing only .lua and/or .manifest files)\n" +
                        $"  • .hzbak / .spmb backup files",
                        "Operation Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Exception in ProcessDroppedFiles: {ex.Message}\n{ex.StackTrace}");
                ShowImportProgressOverlay(false);
                ModernMessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool ProcessZipFile(string zipFilePath, string pluginPath, string luaPath, string depotCachePath, string depotCachePathOld)
        {
            try
            {
                string tempExtractPath = Path.Combine(Path.GetTempPath(), $"spm_extract_{Guid.NewGuid()}");
                Directory.CreateDirectory(tempExtractPath);

                try
                {
                    // Extract zip file
                    ZipFile.ExtractToDirectory(zipFilePath, tempExtractPath);
                    Logger.Log($"[GameLibraryView] Extracted zip file to: {tempExtractPath}");

                    // Find and process .lua and .manifest files
                    var luaFiles = Directory.GetFiles(tempExtractPath, "*.lua", SearchOption.AllDirectories);
                    var manifestFiles = Directory.GetFiles(tempExtractPath, "*.manifest", SearchOption.AllDirectories);

                    if (luaFiles.Length == 0 && manifestFiles.Length == 0)
                    {
                        Logger.Log($"[GameLibraryView] Zip file contains no .lua or .manifest files");
                        return false;
                    }

                    // Copy .lua files
                    foreach (var luaFile in luaFiles)
                    {
                        string destPath = Path.Combine(pluginPath, Path.GetFileName(luaFile));
                        string destLuaPath = Path.Combine(luaPath, Path.GetFileName(luaFile));
                        File.Copy(luaFile, destPath, true);
                        File.Copy(luaFile, destLuaPath, true);
                        Logger.Log($"[GameLibraryView] Extracted and copied .lua file: {Path.GetFileName(luaFile)} → {pluginPath} and {luaPath}");
                    }

                    // Copy .manifest files to both paths
                    foreach (var manifestFile in manifestFiles)
                    {
                        string destPath = Path.Combine(depotCachePath, Path.GetFileName(manifestFile));
                        string destPathOld = Path.Combine(depotCachePathOld, Path.GetFileName(manifestFile));
                        File.Copy(manifestFile, destPath, true);
                        File.Copy(manifestFile, destPathOld, true);
                        Logger.Log($"[GameLibraryView] Extracted and copied .manifest file: {Path.GetFileName(manifestFile)} → {destPath} and {destPathOld}");
                    }

                    return true;
                }
                finally
                {
                    // Clean up temp directory
                    try
                    {
                        if (Directory.Exists(tempExtractPath))
                        {
                            Directory.Delete(tempExtractPath, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[GameLibraryView] Warning: Failed to clean up temp directory: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error processing zip file: {ex.Message}");
                return false;
            }
        }

        private void UpdateRowsProjection()
        {
            if (_mainWindow?.Games == null)
            {
                if (Rows.Count > 0)
                {
                    CancelHoldGesture();
                    Rows.Clear();
                    _lastProjectedItems = null;
                    _projectedColumnCount = 0;
                }
                return;
            }

            try
            {
                var currentFiltered = _lastFilteredGames;

                if (ColumnCount == _projectedColumnCount && !HasSequenceChanged(currentFiltered))
                {
                    return;
                }

                CancelHoldGesture();

                int columns = Math.Max(1, ColumnCount);
                int totalCount = currentFiltered.Count;
                int rowIndex = 0;
                var newRows = new List<GameRow>((totalCount + columns - 1) / columns);

                for (int i = 0; i < totalCount; i += columns)
                {
                    int count = Math.Min(columns, totalCount - i);
                    var rowItems = new GameEntry[count];
                    for (int j = 0; j < count; j++)
                    {
                        rowItems[j] = currentFiltered[i + j];
                    }
                    newRows.Add(new GameRow(rowIndex++, rowItems, columns));
                }

                Rows.Clear();
                foreach (var row in newRows)
                {
                    Rows.Add(row);
                }

                _projectedColumnCount = ColumnCount;
                _lastProjectedItems = currentFiltered;
                Logger.Log($"[GameLibraryView] Rows projection updated: {Rows.Count} rows ({totalCount} games, {columns} cols)");
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error updating rows projection: {ex.Message}");
            }
        }

        private bool HasSequenceChanged(IReadOnlyList<GameEntry> currentFiltered)
        {
            if (_lastProjectedItems == null || _lastProjectedItems.Count != currentFiltered.Count)
                return true;

            for (int i = 0; i < currentFiltered.Count; i++)
            {
                if (!ReferenceEquals(_lastProjectedItems[i], currentFiltered[i]))
                    return true;
            }
            return false;
        }
    }

    public sealed class GameRow
    {
        public int RowIndex { get; }
        public IReadOnlyList<GameEntry> Items { get; }
        public int Columns { get; }

        public GameRow(int rowIndex, IReadOnlyList<GameEntry> items, int columns = 3)
        {
            RowIndex = rowIndex;
            Items = items ?? Array.Empty<GameEntry>();
            Columns = columns;
        }

        public override string ToString() => $"GameRow {RowIndex} ({Items.Count} items, {Columns} cols)";
    }
}



