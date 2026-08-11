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

namespace SteamPluginManager.Views
{
    public partial class GameLibraryView : UserControl
    {
        private MainWindow? _mainWindow;
        private string _lastSearchTerm = "";
        private const double MIN_CARD_WIDTH = 240;
        private bool _isInitialized = false;
        private bool _isUpdatingSearchBox = false; // Flag to prevent race conditions
        private string _selectedCategory = "All";
        private ICollectionView? _collectionView; // For sorted view
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

        public GameLibraryView()
        {
            InitializeComponent();
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

        public void Refresh()
        {
            // First, refresh the game list from disk (checks installation status)
            if (_mainWindow != null)
            {
                _mainWindow.RefreshGameList();
            }
            
            // Then refresh UI
            LoadGameLibrary();
        }

        private void RefreshInstallationStatus()
        {
            try
            {
                if (_mainWindow?.Games == null) return;

                foreach (var game in _mainWindow.Games)
                {
                    // Re-check if game is installed
                    string? installPath = _mainWindow.GetGameInstallationPath(game.AppId);
                    if (game.IsInstalled != (installPath != null))
                    {
                        game.IsInstalled = installPath != null;
                        game.InstallationPath = installPath;
                        Logger.Log($"[GameLibraryView] Updated IsInstalled status for {game.Name} (AppID: {game.AppId}): {game.IsInstalled}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error in RefreshInstallationStatus: {ex.Message}");
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
                    
                    SetupCollectionView();
                    
                    // Subscribe to games collection changes for real-time updates
                    _mainWindow.Games.CollectionChanged += Games_CollectionChanged;
                    Logger.Log("[GameLibraryView] Subscribed to Games.CollectionChanged event");
                    
                    UpdateInfoPanel();
                    UpdateGridColumns();
                    
                    // Initialize SearchBox placeholder
                    if (SearchBox != null)
                    {
                        _isUpdatingSearchBox = true;
                        SearchBox.Text = "Search games...";
                        _isUpdatingSearchBox = false;
                        Logger.Log("[GameLibraryView] SearchBox placeholder initialized");
                    }
                    
                    // Register language mappings
                    RegisterGameLibraryElements();
                    
                    // Apply current language
                    RefreshLanguageUI();
                    
                    // Final verification
                    if (GameCards?.ItemsSource == _collectionView)
                    {
                        Logger.Log("[GameLibraryView] ✓ VERIFIED: GameCards.ItemsSource is correctly set to CollectionView");
                    }
                    else
                    {
                        Logger.Log($"[GameLibraryView] ⚠ WARNING: GameCards.ItemsSource verification failed. GameCards={GameCards}, ItemsSource={GameCards?.ItemsSource}, CollectionView={_collectionView}");
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
                MessageBox.Show($"Failed to load games: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Games_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            try
            {
                Logger.Log($"[GameLibraryView] Games collection changed: {e.Action} - Count: {_mainWindow?.Games.Count ?? 0}");
                
                // Ensure ItemsSource is properly set before refresh
                EnsureItemsSourceIsSet();
                
                // Refresh collection view to reflect changes
                if (_collectionView != null)
                {
                    _collectionView.Refresh();
                    Logger.Log($"[GameLibraryView] ✓ Collection view refreshed (real-time)");
                    
                    // Update UI info
                    UpdateInfoPanel();
                }
                else
                {
                    Logger.Log("[GameLibraryView] WARNING: _collectionView is NULL during collection change - attempting to reset");
                    if (_mainWindow != null)
                    {
                        SetupCollectionView();
                        UpdateInfoPanel();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error in Games_CollectionChanged: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void EnsureItemsSourceIsSet()
        {
            if (GameCards == null || _collectionView == null) return;
            
            try
            {
                // Check if ItemsSource is correctly pointing to CollectionView
                if (GameCards.ItemsSource != _collectionView)
                {
                    Logger.Log("[GameLibraryView] Fixing ItemsSource: Re-setting GameCards.ItemsSource to CollectionView");
                    GameCards.ItemsSource = _collectionView;
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
                // Create CollectionViewSource with sorting
                var collectionViewSource = new CollectionViewSource();
                collectionViewSource.Source = _mainWindow.Games;
                
                // Add sorting by Name (A-Z)
                collectionViewSource.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                
                _collectionView = collectionViewSource.View;
                
                // Set ItemsSource to sorted view - CRITICAL: This ensures real-time updates work
                if (GameCards != null)
                {
                    GameCards.ItemsSource = _collectionView;
                    Logger.Log("[GameLibraryView] ✓ GameCards.ItemsSource set to CollectionView with sorting");
                }
                else
                {
                    Logger.Log("[GameLibraryView] WARNING: GameCards is NULL during SetupCollectionView - will retry on next refresh");
                }
                
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
                
                if (_collectionView != null)
                {
                    _collectionView.Refresh();
                    UpdateInfoPanel();
                    
                    Logger.Log("[GameLibraryView] ✓ Collection view refreshed successfully");
                }
                else
                {
                    Logger.Log("[GameLibraryView] WARNING: _collectionView is NULL - attempting to re-setup");
                    if (_mainWindow != null)
                    {
                        SetupCollectionView();
                        UpdateInfoPanel();
                    }
                }
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
                
                // Header section
                if (GameLibraryTitleText != null)
                    LanguageHelper.RegisterTextBlock(GameLibraryTitleText, "GameLibrary.Title");
                else
                    Logger.Log("[GameLibraryView] WARNING: GameLibraryTitleText is NULL");
                    
                if (GameLibrarySubtitleText != null)
                    LanguageHelper.RegisterTextBlock(GameLibrarySubtitleText, "GameLibrary.Subtitle");
                else
                    Logger.Log("[GameLibraryView] WARNING: GameLibrarySubtitleText is NULL");
                    
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

        private void UpdateGridColumns()
        {
            try
            {
                if (CardsScrollViewer == null) return;

                // Get available width
                double availableWidth = CardsScrollViewer.ActualWidth;
                if (availableWidth <= 0) availableWidth = 800; // Default fallback

                // Account for margins and padding
                availableWidth -= 40; // Margins and padding

                // Calculate columns based on available width and minimum card width
                int columns = Math.Max(1, (int)(availableWidth / MIN_CARD_WIDTH));

                // Find CardGrid and update columns
                if (GameCards.ItemsPanel != null)
                {
                    var panel = GameCards.FindName("CardGrid") as UniformGrid;
                    if (panel != null)
                    {
                        panel.Columns = columns;
                    }
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
                foreach (var game in _mainWindow.Games)
                {
                    game.IsSelected = true;
                }
                UpdateInfoPanel();
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_mainWindow != null)
            {
                Refresh();
                UpdateActivityLog("Game list refreshed");
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Title = "Import Lua, Manifest, or Zip files",
                    Multiselect = true,
                    Filter = "Supported Files (*.lua;*.manifest;*.zip)|*.lua;*.manifest;*.zip|Lua Files (*.lua)|*.lua|Manifest Files (*.manifest)|*.manifest|Zip Files (*.zip)|*.zip|All Files (*.*)|*.*"
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
                MessageBox.Show($"Error opening import dialog: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            // Activity log removed — inform user
            MessageBox.Show("Activity log has been disabled.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (_mainWindow != null)
            {
                foreach (var game in _mainWindow.Games)
                {
                    game.IsSelected = false;
                }
                UpdateInfoPanel();
                UpdateActivityLog("Cleared all selections");
            }
        }

        private void BackupSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_mainWindow == null) return;

            var selected = _mainWindow.Games.Where(g => g.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Please select games to backup", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "SPM Backup (*.spmb)|*.spmb",
                    Title = "Save Backup",
                    FileName = $"SteamPluginBackup_{DateTime.Now:yyyyMMdd}.spmb"
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
                    EncryptStream(memoryStream, saveFileDialog.FileName);
                }

                MessageBox.Show(
                    $"Backup created successfully!\n\nGames: {selected.Count}\nFile: {Path.GetFileName(saveFileDialog.FileName)}", 
                    "Backup Complete", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
                
                // Log the backup
                UpdateActivityLog($"Backup created: {Path.GetFileName(saveFileDialog.FileName)} ({selected.Count} game(s))");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Backup failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private MemoryStream EncryptStream(MemoryStream inStream, string outputFile)
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
            return inStream;
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
                MessageBox.Show("Please select games to delete", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                string actionText = dlg.SelectedAction switch
                {
                    DeleteChoiceDialog.DeleteAction.ManifestAndLua => "Deleting Lua & Manifest...",
                    DeleteChoiceDialog.DeleteAction.Uninstall => "Uninstalling Games...",
                    DeleteChoiceDialog.DeleteAction.Both => "Deleting & Uninstalling...",
                    _ => "Processing..."
                };
                
                ShowDeleteProgressOverlay(true, actionText);

                // Call actual delete method from MainWindow
                await _mainWindow.DeleteSelectedGamesAsync(dlg.SelectedAction);
                
                UpdateDeleteProgressStatus("Refreshing library...");
                
                // Refresh GameLibraryView UI after delete completes
                Logger.Log("[GameLibraryView] Auto-refreshing library view after delete");
                LoadGameLibrary();
                
                // Log the deletion
                UpdateActivityLog($"{selected.Count} game(s) deleted");
                
                // Hide progress overlay
                await Task.Delay(500);
                ShowDeleteProgressOverlay(false);
            }
            catch (Exception ex)
            {
                ShowDeleteProgressOverlay(false);
                MessageBox.Show($"Delete failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Search & Filter
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Ignore TextChanged if we're manually updating the searchbox (to prevent race conditions)
            if (_isUpdatingSearchBox)
                return;

            if (sender is TextBox textBox && _mainWindow != null && _collectionView != null)
            {
                string searchTerm = textBox.Text?.Trim() ?? "";
                _lastSearchTerm = searchTerm;

                if (string.IsNullOrWhiteSpace(searchTerm))
                {
                    Logger.Log("[SearchBox] Showing all games");
                }
                else
                {
                    Logger.Log($"[SearchBox] Filtered games for term: {searchTerm}");
                }

                ApplyCollectionFilters();
                
                try
                {
                    UpdateInfoPanel();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SearchBox] Error updating info panel: {ex.Message}");
                }
            }
        }

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                try
                {
                    _isUpdatingSearchBox = true;
                    
                    // If text is the placeholder, clear it
                    if (textBox.Text == "Search games...")
                    {
                        textBox.Text = "";
                        Logger.Log("[SearchBox] GotFocus - cleared placeholder");
                    }
                    
                    // Ensure we can see the text being typed
                    textBox.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["ForegroundBrush"];
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SearchBox] Error in GotFocus: {ex.Message}");
                }
                finally
                {
                    _isUpdatingSearchBox = false;
                    
                    // NOW trigger TextChanged manually since we cleared the text above
                    if (string.IsNullOrWhiteSpace((sender as TextBox)?.Text))
                    {
                        SearchBox_TextChanged(sender, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
                    }
                }
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && string.IsNullOrWhiteSpace(textBox.Text))
            {
                try
                {
                    _isUpdatingSearchBox = true;
                    textBox.Text = "Search games...";
                    Logger.Log("[SearchBox] LostFocus - restored placeholder");
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
        }

        private void CategoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _selectedCategory = tag;
                ApplyCollectionFilters();
            }
        }

        private void ApplyCollectionFilters()
        {
            if (_collectionView == null) return;

            try
            {
                _collectionView.Filter = (obj) =>
                {
                    if (obj is GameEntry game)
                    {
                        bool matchesSearch = string.IsNullOrWhiteSpace(_lastSearchTerm) ||
                                             game.Name.Contains(_lastSearchTerm, StringComparison.OrdinalIgnoreCase) ||
                                             (game.Genre?.Contains(_lastSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false);

                        bool matchesCategory = _selectedCategory switch
                        {
                            "Installed" => game.IsInstalled,
                            "NotInstalled" => !game.IsInstalled,
                            _ => true,
                        };

                        return matchesSearch && matchesCategory;
                    }
                    return false;
                };
                _collectionView.Refresh();
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error applying collection filters: {ex.Message}");
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

        private void GameCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Allow clicking on card to select/deselect
            if (sender is Border border && border.DataContext is GameEntry game)
            {
                game.IsSelected = !game.IsSelected;
                UpdateInfoPanel();
            }
        }

        private void ItemCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // If we're currently showing a filtered CollectionView, just refresh it to preserve the filter
            if (_collectionView != null && GameCards.ItemsSource == _collectionView)
            {
                _collectionView.Refresh();
                Logger.Log("[ItemCheckBox_Changed] CollectionView refreshed (filter preserved)");
            }
            // Otherwise ensure ItemsSource is set to CollectionView
            else if (_mainWindow != null && GameCards.ItemsSource != _collectionView)
            {
                GameCards.ItemsSource = _collectionView;
                Logger.Log("[ItemCheckBox_Changed] ItemsSource reset to CollectionView");
            }
            UpdateInfoPanel();
        }

        // Activity log removed — keep a no-op method so callers don't need changes
        public void UpdateActivityLog(string message)
        {
            // Intentionally left blank
        }

        private void UpdateActionButtons(int selectedCount)
        {
            // Show/hide action buttons based on selection count
            if (BackupBtn != null) BackupBtn.Visibility = selectedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (DeleteBtn != null) DeleteBtn.Visibility = selectedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (ClearAllBtn != null) ClearAllBtn.Visibility = selectedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateInfoPanel()
        {
            if (_mainWindow == null) return;

            var selectedCount = _mainWindow.Games.Count(g => g.IsSelected);
            var totalCount = _mainWindow.Games.Count;
            
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

        private void ShowDeleteProgressOverlay(bool show, string status = "Processing...")
        {
            try
            {
                Logger.Log($"[GameLibraryView] ShowDeleteProgressOverlay called with show={show}, status='{status}'");
                
                var overlay = FindName("DeleteProgressOverlay");
                Logger.Log($"[GameLibraryView] FindName('DeleteProgressOverlay') returned: {overlay?.GetType().Name ?? "NULL"}");
                
                if (overlay is Grid overlayGrid)
                {
                    overlayGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    Logger.Log($"[GameLibraryView] ✓ Delete progress overlay visibility set to: {overlayGrid.Visibility}");
                }
                else
                {
                    Logger.Log($"[GameLibraryView] ✗ ERROR: DeleteProgressOverlay not found or not a Grid");
                }

                if (show)
                {
                    var statusText = FindName("DeleteProgressStatus");
                    Logger.Log($"[GameLibraryView] FindName('DeleteProgressStatus') returned: {statusText?.GetType().Name ?? "NULL"}");
                    
                    if (statusText is TextBlock statusTextBlock)
                    {
                        statusTextBlock.Text = status;
                        Logger.Log($"[GameLibraryView] ✓ Status text updated to: '{status}'");
                    }
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
                if (FindName("DeleteProgressTitle") is TextBlock titleText)
                {
                    titleText.Text = title;
                    Logger.Log($"[GameLibraryView] ✓ Progress overlay title updated to: '{title}'");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Error updating progress overlay title: {ex.Message}");
            }
        }

        private void UpdateDeleteProgressStatus(string message)
        {
            try
            {
                if (FindName("DeleteProgressStatus") is TextBlock statusText)
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


                
                var overlay = FindName("ImportProgressOverlay");
                Logger.Log($"[GameLibraryView] FindName('ImportProgressOverlay') returned: {overlay?.GetType().Name ?? "NULL"}");
                
                if (overlay is Grid overlayGrid)
                {
                    overlayGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    Logger.Log($"[GameLibraryView] ✓ Import progress overlay visibility set to: {overlayGrid.Visibility}");
                }
                else
                {
                    Logger.Log($"[GameLibraryView] ✗ ERROR: ImportProgressOverlay not found or not a Grid");
                }

                if (show)
                {
                    var statusText = FindName("ImportProgressStatus");
                    Logger.Log($"[GameLibraryView] FindName('ImportProgressStatus') returned: {statusText?.GetType().Name ?? "NULL"}");
                    
                    if (statusText is TextBlock statusTextBlock)
                    {
                        statusTextBlock.Text = status;
                        Logger.Log($"[GameLibraryView] ✓ Import status text updated to: '{status}'");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Exception in ShowImportProgressOverlay: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void UpdateImportProgressStatus(string message)
        {
            try
            {
                if (FindName("ImportProgressStatus") is TextBlock statusText)
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
                
                // Check if files are valid (.lua, .manifest, or .zip)
                bool hasValidFiles = files.Any(f => 
                    f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                
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
                MessageBox.Show($"Error processing dropped files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ProcessDroppedFiles(string[] files)
        {
            try
            {
                ShowImportProgressOverlay(true, "Processing dropped files...");
                
                string steamPath = SteamHelper.GetSteamPath();
                if (string.IsNullOrEmpty(steamPath))
                {
                    UpdateActivityLog("Error: Steam path not found");
                    MessageBox.Show("Steam path not found", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

                int successCount = 0;
                int failureCount = 0;
                var processedFiles = new List<string>();

                foreach (string file in files)
                {
                    try
                    {
                        UpdateImportProgressStatus($"Processing: {Path.GetFileName(file)}...");
                        
                        if (file.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                        {
                            // Copy .lua file to both stplug-in and lua folders
                            string destPath = Path.Combine(pluginPath, Path.GetFileName(file));
                            string destLuaPath = Path.Combine(luaPath, Path.GetFileName(file));
                            File.Copy(file, destPath, true);
                            File.Copy(file, destLuaPath, true);
                            processedFiles.Add(Path.GetFileName(file));
                            successCount++;
                            Logger.Log($"[GameLibraryView] Copied .lua file: {Path.GetFileName(file)} → {pluginPath} and {luaPath}");
                        }
                        else if (file.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                        {
                            // Copy .manifest file to both depotcache paths
                            string destPath = Path.Combine(depotCachePath, Path.GetFileName(file));
                            string destPathOld = Path.Combine(depotCachePathOld, Path.GetFileName(file));
                            File.Copy(file, destPath, true);
                            File.Copy(file, destPathOld, true);
                            processedFiles.Add(Path.GetFileName(file));
                            successCount++;
                            Logger.Log($"[GameLibraryView] Copied .manifest file: {Path.GetFileName(file)} → {destPath} and {destPathOld}");
                        }
                        else if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            // Extract zip and process contents
                            if (ProcessZipFile(file, pluginPath, luaPath, depotCachePath, depotCachePathOld))
                            {
                                processedFiles.Add(Path.GetFileName(file));
                                successCount++;
                            }
                            else
                            {
                                failureCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[GameLibraryView] Error processing file {file}: {ex.Message}");
                        failureCount++;
                    }
                }

                // Refresh the game library
                UpdateImportProgressStatus("Refreshing library...");
                await Task.Delay(500);
                
                // Reload games from file system
                Logger.Log("[GameLibraryView] Triggering UI refresh after file drop import");
                if (_mainWindow != null)
                {
                    // Force reload from file system
                    _mainWindow.RefreshGameList();
                }
                
                // Refresh collection view to update UI
                await Task.Delay(300);
                RefreshCollectionView();
                await Task.Delay(200);

                // Show results
                ShowImportProgressOverlay(false);
                
                if (failureCount == 0 && successCount > 0)
                {
                    string message = $"✓ Successfully processed {successCount} file(s):\n" + 
                                   string.Join("\n", processedFiles.Select(f => $"  • {f}"));
                    UpdateActivityLog($"Imported {successCount} file(s)");
                    MessageBox.Show(message, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (failureCount > 0)
                {
                    UpdateActivityLog($"Import failed: {failureCount} file(s) could not be processed");
                    MessageBox.Show(
                        $"Operation failed: {failureCount} file(s) could not be processed.\n\n" +
                        $"Valid files must be:\n" +
                        $"  • .lua files\n" +
                        $"  • .manifest files\n" +
                        $"  • .zip files (containing only .lua and/or .manifest files)",
                        "Operation Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameLibraryView] Exception in ProcessDroppedFiles: {ex.Message}\n{ex.StackTrace}");
                ShowImportProgressOverlay(false);
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
    }
}



