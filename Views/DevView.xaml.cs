using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SteamPluginManager.Models;
using SteamPluginManager.Services.API;
using SteamPluginManager.Services.Profile;

namespace SteamPluginManager.Views
{
    public partial class DevView : UserControl
    {
        private readonly List<DevManifestGame> _allGames = new();
        private readonly List<DevTokenRevokeItem> _allTokens = new();
        private readonly List<DevDeviceAccessItem> _allDevices = new();
        private readonly List<DevBlockedItem> _allBlocked = new();
        private readonly List<DevUserItem> _allUsers = new();

        private DevUserItem? _selectedUser;
        private UserProfile? _currentAdminProfile;

        private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

        public DevView()
        {
            InitializeComponent();
            Loaded += DevView_Loaded;
        }

        private async void DevView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Security check: ensure user is Admin (Level 0)
                _currentAdminProfile = await DashboardView.LoadUserProfileForEditingAsync();
                if (_currentAdminProfile == null || !_currentAdminProfile.IsAdmin)
                {
                    Logger.Log("[DevView] Unauthorized access attempt blocked.");
                    ModernMessageBox.ShowError(
                        "Halaman Dev Console ini hanya dapat diakses oleh Administrator dengan Level 0.",
                        "Akses Ditolak"
                    );
                    WindowNavigator.NavigateToDashboard();
                    return;
                }

                LogConsole($"[DevConsole] Admin session initialized for '{_currentAdminProfile.DisplayName}' (Level {_currentAdminProfile.Level}).");
                await RefreshAllDataAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"[DevView] Load error: {ex.Message}");
            }
        }

        private async void RefreshAllButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAllDataAsync();
        }

        private async Task RefreshAllDataAsync()
        {
            try
            {
                RefreshAllButton.IsEnabled = false;
                LogConsole("[DevConsole] Refreshing all console datasets...");

                await Task.WhenAll(
                    LoadGamesAsync(),
                    LoadTokensAndDevicesAsync(),
                    LoadBlockedDevicesAsync(),
                    LoadUsersAsync(),
                    LoadActiveBroadcastAsync()
                );

                LogConsole("[DevConsole] All console datasets successfully refreshed.");
            }
            catch (Exception ex)
            {
                LogConsole($"[DevConsole] Error during dataset refresh: {ex.Message}");
            }
            finally
            {
                RefreshAllButton.IsEnabled = true;
            }
        }

        #region 1. Game Catalog & Multi-Select Purge

        private async Task LoadGamesAsync()
        {
            try
            {
                _allGames.Clear();
                var manifests = await SupabaseConfig.GetNewestManifestFilesAsync(2000);
                foreach (var m in manifests)
                {
                    _allGames.Add(new DevManifestGame
                    {
                        IsSelected = false,
                        AppId = m.AppId ?? 0,
                        Name = m.Name,
                        Genre = m.Genre,
                        StorageType = m.StorageType,
                        FolderPath = m.FolderPath,
                        UploadDate = m.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-",
                        RawManifest = m
                    });
                }

                FilterGames();
                UpdateGameSelectionUI();
                LogConsole($"[Catalog] Loaded {_allGames.Count} games from Supabase database.");
            }
            catch (Exception ex)
            {
                LogConsole($"[Catalog] Failed to load games: {ex.Message}");
            }
        }

        private void GameSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterGames();
        }

        private void FilterGames()
        {
            string query = GameSearchTextBox?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                GamesDataGrid.ItemsSource = _allGames.ToList();
            }
            else
            {
                GamesDataGrid.ItemsSource = _allGames.Where(g =>
                    g.AppId.ToString().Contains(query) ||
                    (!string.IsNullOrEmpty(g.Name) && g.Name.ToLowerInvariant().Contains(query)) ||
                    (!string.IsNullOrEmpty(g.Genre) && g.Genre.ToLowerInvariant().Contains(query))
                ).ToList();
            }
        }

        private async void ReloadGames_Click(object sender, RoutedEventArgs e)
        {
            await LoadGamesAsync();
        }

        private void SelectAllGames_Click(object sender, RoutedEventArgs e)
        {
            var visibleGames = GamesDataGrid.ItemsSource as IEnumerable<DevManifestGame> ?? _allGames;
            foreach (var g in visibleGames)
            {
                g.IsSelected = true;
            }
            GamesDataGrid.Items.Refresh();
            UpdateGameSelectionUI();
        }

        private void DeselectAllGames_Click(object sender, RoutedEventArgs e)
        {
            foreach (var g in _allGames)
            {
                g.IsSelected = false;
            }
            GamesDataGrid.Items.Refresh();
            UpdateGameSelectionUI();
        }

        private void GameSelectionCheckBox_Click(object sender, RoutedEventArgs e)
        {
            UpdateGameSelectionUI();
        }

        private void GamesDataGrid_Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                // If user clicked directly on a CheckBox or Button, allow native handling
                DependencyObject? current = dep;
                while (current != null && current != sender)
                {
                    if (current is CheckBox || current is Button)
                    {
                        return;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (sender is DataGridRow row && row.DataContext is DevManifestGame game)
            {
                game.IsSelected = !game.IsSelected;
                GamesDataGrid.Items.Refresh();
                UpdateGameSelectionUI();
            }
        }

        private void UpdateGameSelectionUI()
        {
            int selectedCount = _allGames.Count(g => g.IsSelected);
            SelectedGamesCountLabel.Text = $"{selectedCount} {(selectedCount == 1 ? "Game" : "Games")} Selected";
            ExecutePurgeButton.IsEnabled = selectedCount > 0;

            if (selectedCount == 0)
            {
                PurgeStatusText.Text = "Select one or more games above, then choose the components to purge.";
            }
            else if (selectedCount == 1)
            {
                var single = _allGames.First(g => g.IsSelected);
                PurgeStatusText.Text = $"Ready to purge components for game: {single.Name} (AppID: {single.AppId}).";
            }
            else
            {
                PurgeStatusText.Text = $"Ready to batch purge components for {selectedCount} selected games.";
            }
        }

        private void PurgeDbCatalogCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                DependencyObject? current = dep;
                while (current != null && current != sender)
                {
                    if (current is CheckBox) return;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            PurgeDbCatalogCheck.IsChecked = !PurgeDbCatalogCheck.IsChecked;
        }

        private void PurgeSupabaseStorageCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                DependencyObject? current = dep;
                while (current != null && current != sender)
                {
                    if (current is CheckBox) return;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            PurgeSupabaseStorageCheck.IsChecked = !PurgeSupabaseStorageCheck.IsChecked;
        }

        private void PurgeOnlineFixCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                DependencyObject? current = dep;
                while (current != null && current != sender)
                {
                    if (current is CheckBox) return;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            PurgeOnlineFixCheck.IsChecked = !PurgeOnlineFixCheck.IsChecked;
        }

        private void PurgeGameBypassCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                DependencyObject? current = dep;
                while (current != null && current != sender)
                {
                    if (current is CheckBox) return;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            PurgeGameBypassCheck.IsChecked = !PurgeGameBypassCheck.IsChecked;
        }

        private async void ExecutePurgeButton_Click(object sender, RoutedEventArgs e)
        {
            var targetGames = _allGames.Where(g => g.IsSelected).ToList();
            if (targetGames.Count == 0) return;

            bool doDb = PurgeDbCatalogCheck.IsChecked == true;
            bool doStorage = PurgeSupabaseStorageCheck.IsChecked == true;
            bool doOnlineFix = PurgeOnlineFixCheck.IsChecked == true;
            bool doGameBypass = PurgeGameBypassCheck.IsChecked == true;

            if (!doDb && !doStorage && !doOnlineFix && !doGameBypass)
            {
                ModernMessageBox.ShowWarning("Please select at least one component to purge.", "Warning");
                return;
            }

            var prompt = new StringBuilder();
            prompt.AppendLine($"Are you sure you want to PURGE the following {targetGames.Count} selected games?");
            prompt.AppendLine();
            prompt.AppendLine("Game List:");
            foreach (var g in targetGames.Take(8))
            {
                prompt.AppendLine($"  • {g.Name} (AppID: {g.AppId})");
            }
            if (targetGames.Count > 8)
            {
                prompt.AppendLine($"  ... and {targetGames.Count - 8} other games.");
            }
            prompt.AppendLine();
            prompt.AppendLine("Components to be deleted:");
            if (doDb) prompt.AppendLine("  [✓] 1. Database Catalog (hzmanifest_files & games_dlc)");
            if (doStorage) prompt.AppendLine("  [✓] 2. Supabase Storage (Folder {appId}/)");
            if (doOnlineFix) prompt.AppendLine("  [✓] 3. Backblaze B2 (onlinefix/{appId}.zip)");
            if (doGameBypass) prompt.AppendLine("  [✓] 4. Cloudflare R2 (gamebypass/{appId}.zip)");

            bool confirm = ModernMessageBox.ShowConfirm(
                prompt.ToString(),
                "Confirm Game Purge (Permanent)",
                isDestructive: true
            );

            if (!confirm) return;

            ExecutePurgeButton.IsEnabled = false;
            LogConsole($"\r\n=== STARTING PURGE FOR {targetGames.Count} GAMES ===");

            int processed = 0;
            var http = SharedHttpClient.Instance;

            foreach (var game in targetGames)
            {
                processed++;
                int appId = game.AppId;
                PurgeStatusText.Text = $"Processing [{processed}/{targetGames.Count}]: {game.Name} ({appId})...";
                LogConsole($"[Purge {processed}/{targetGames.Count}] Processing {game.Name} ({appId})...");

                try
                {
                    // 1. Database Purge
                    if (doDb)
                    {
                        var delManifestUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/hzmanifest_files?app_id=eq.{appId}";
                        using var req1 = new HttpRequestMessage(HttpMethod.Delete, delManifestUrl);
                        req1.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                        req1.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                        await http.SendAsync(req1);

                        var delDlcUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/games_dlc?game_app_id=eq.{appId}";
                        using var req2 = new HttpRequestMessage(HttpMethod.Delete, delDlcUrl);
                        req2.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                        req2.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                        await http.SendAsync(req2);
                        LogConsole($"  - Deleted database records for AppID {appId}");
                    }

                    // 2. Supabase Storage Purge
                    if (doStorage)
                    {
                        try
                        {
                            var storageBucket = SupabaseConfig.StorageBucketName;
                            var listUrl = $"{SupabaseConfig.SupabaseUrl}/storage/v1/object/list/{storageBucket}";
                            var listPayload = JsonSerializer.Serialize(new { prefix = $"{appId}/", limit = 100 });
                            
                            using var listReq = new HttpRequestMessage(HttpMethod.Post, listUrl)
                            {
                                Content = new StringContent(listPayload, Encoding.UTF8, "application/json")
                            };
                            listReq.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                            listReq.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                            
                            var listResp = await http.SendAsync(listReq);
                            if (listResp.IsSuccessStatusCode)
                            {
                                var listContent = await listResp.Content.ReadAsStringAsync();
                                using var doc = JsonDocument.Parse(listContent);
                                var prefixesToDelete = new List<string>();
                                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var elem in doc.RootElement.EnumerateArray())
                                    {
                                        if (elem.TryGetProperty("name", out var nEl))
                                        {
                                            var fn = nEl.GetString();
                                            if (!string.IsNullOrEmpty(fn)) prefixesToDelete.Add($"{appId}/{fn}");
                                        }
                                    }
                                }

                                if (prefixesToDelete.Count > 0)
                                {
                                    var deleteObjUrl = $"{SupabaseConfig.SupabaseUrl}/storage/v1/object/{storageBucket}";
                                    var delPayload = JsonSerializer.Serialize(new { prefixes = prefixesToDelete });
                                    using var delReq = new HttpRequestMessage(HttpMethod.Delete, deleteObjUrl)
                                    {
                                        Content = new StringContent(delPayload, Encoding.UTF8, "application/json")
                                    };
                                    delReq.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                                    delReq.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                                    await http.SendAsync(delReq);
                                    LogConsole($"  - Deleted {prefixesToDelete.Count} files from Supabase Storage for {appId}");
                                }
                            }
                        }
                        catch (Exception stEx)
                        {
                            LogConsole($"  - Supabase Storage error for {appId}: {stEx.Message}");
                        }
                    }

                    // 3. Backblaze B2 OnlineFix Purge
                    if (doOnlineFix)
                    {
                        bool b2Res = await B2Config.DeleteFileAsync($"onlinefix/{appId}.zip");
                        LogConsole($"  - Backblaze B2 delete onlinefix/{appId}.zip: {b2Res}");
                    }

                    // 4. Cloudflare R2 GameBypass Purge
                    if (doGameBypass)
                    {
                        bool r2Res = await R2Config.DeleteFileAsync($"gamebypass/{appId}.zip");
                        LogConsole($"  - Cloudflare R2 delete gamebypass/{appId}.zip: {r2Res}");
                    }
                }
                catch (Exception ex)
                {
                    LogConsole($"  - Error purging {game.Name} ({appId}): {ex.Message}");
                }
            }

            LogConsole($"=== PURGE COMPLETED: {targetGames.Count} GAMES PROCESSED ===\r\n");
            PurgeStatusText.Text = $"Purge operation completed for {targetGames.Count} games.";

            ModernMessageBox.ShowInformation(
                $"Purge operation completed. A total of {targetGames.Count} games and their components have been permanently deleted.",
                "Purge Successful"
            );

            await LoadGamesAsync();
        }

        #endregion

        #region 2. Tokens & Device Access (Revoke & Hardware Ban)

        private async Task LoadTokensAndDevicesAsync()
        {
            try
            {
                _allTokens.Clear();
                _allDevices.Clear();

                var http = SharedHttpClient.Instance;

                // 1. Fetch user profiles for mapping
                var userProfiles = new List<UserProfile>();
                try
                {
                    var uUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?select=id,device_id,display_name,avatar_url";
                    using var uReq = new HttpRequestMessage(HttpMethod.Get, uUrl);
                    uReq.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    uReq.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                    var uResp = await http.SendAsync(uReq);
                    if (uResp.IsSuccessStatusCode)
                    {
                        var uContent = await uResp.Content.ReadAsStringAsync();
                        userProfiles = JsonSerializer.Deserialize<List<UserProfile>>(uContent, _jsonOpts) ?? new();
                    }
                }
                catch { }

                var profileByDev = userProfiles
                    .Where(p => !string.IsNullOrWhiteSpace(p.DeviceId))
                    .ToDictionary(p => p.DeviceId, p => p, StringComparer.OrdinalIgnoreCase);

                // 2. Fetch device tokens
                var tUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/device_tokens?select=*&order=created_at.desc&limit=1000";
                using var tReq = new HttpRequestMessage(HttpMethod.Get, tUrl);
                tReq.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                tReq.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                var tResp = await http.SendAsync(tReq);

                if (tResp.IsSuccessStatusCode)
                {
                    var tContent = await tResp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(tContent);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            string token = el.TryGetProperty("token", out var tEl) ? tEl.GetString() ?? "" : "";
                            bool isActive = !el.TryGetProperty("is_active", out var aEl) || aEl.ValueKind != JsonValueKind.False;
                            string verifiedAt = el.TryGetProperty("verified_at", out var vEl) && vEl.ValueKind == JsonValueKind.String ? vEl.GetString() ?? "-" : "-";

                            var devIds = new List<string>();
                            if (el.TryGetProperty("device_ids", out var dIdsEl) && dIdsEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var d in dIdsEl.EnumerateArray())
                                {
                                    var str = d.GetString();
                                    if (!string.IsNullOrWhiteSpace(str)) devIds.Add(str.Trim());
                                }
                            }
                            if (devIds.Count == 0 && el.TryGetProperty("device_id", out var dIdEl) && dIdEl.ValueKind == JsonValueKind.String)
                            {
                                var singleDev = dIdEl.GetString();
                                if (!string.IsNullOrWhiteSpace(singleDev)) devIds.Add(singleDev.Trim());
                            }

                            // Collect Display Names from all bound devices
                            var nameList = new List<string>();
                            for (int i = 0; i < devIds.Count; i++)
                            {
                                string devId = devIds[i];
                                string userDisplayName = profileByDev.TryGetValue(devId, out var p) ? p.DisplayName : $"Device #{i+1}";
                                if (!string.IsNullOrWhiteSpace(userDisplayName))
                                {
                                    nameList.Add(userDisplayName);
                                }

                                // Populate Device Access Item for Tab 3 (Hardware Ban/Unbind)
                                _allDevices.Add(new DevDeviceAccessItem
                                {
                                    DeviceId = devId,
                                    DisplayName = userDisplayName,
                                    Token = token,
                                    SlotName = $"Slot #{i+1}",
                                    SlotIndex = i,
                                    BoundDeviceIds = devIds
                                });
                            }

                            string combinedDisplayNames = nameList.Count > 0 
                                ? string.Join(", ", nameList) 
                                : "Unregistered / Disconnected";

                            // Populate Token Item for Tab 2 (Token Revoke)
                            _allTokens.Add(new DevTokenRevokeItem
                            {
                                Token = token,
                                DisplayNames = combinedDisplayNames,
                                DeviceCountText = $"{devIds.Count} / 2 Devices",
                                IsActive = isActive,
                                StatusBadge = isActive ? "ACTIVE" : "REVOKED",
                                VerifiedAt = verifiedAt,
                                BoundDeviceIds = devIds
                            });
                        }
                    }
                }

                FilterTokens();
                FilterDevices();
                LogConsole($"[Tokens] Loaded {_allTokens.Count} tokens and {_allDevices.Count} active devices.");
            }
            catch (Exception ex)
            {
                LogConsole($"[Tokens] Failed to load tokens: {ex.Message}");
            }
        }

        private void TokenSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterTokens();
        }

        private void FilterTokens()
        {
            string query = TokenSearchTextBox?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                TokensDataGrid.ItemsSource = _allTokens.ToList();
            }
            else
            {
                TokensDataGrid.ItemsSource = _allTokens.Where(t =>
                    t.Token.ToLowerInvariant().Contains(query) ||
                    t.DisplayNames.ToLowerInvariant().Contains(query)
                ).ToList();
            }
        }

        private async void ReloadTokens_Click(object sender, RoutedEventArgs e)
        {
            await LoadTokensAndDevicesAsync();
        }

        private async void ToggleTokenActive_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DevTokenRevokeItem item)
            {
                bool newActive = !item.IsActive;
                string actionStr = newActive ? "REACTIVATE" : "REVOKE";

                bool confirm = ModernMessageBox.ShowConfirm(
                    $"Are you sure you want to {actionStr} the following token?\n\nToken: {item.Token}\nUser: {item.DisplayNames}",
                    "Confirm Token Status"
                );

                if (!confirm) return;

                try
                {
                    var http = SharedHttpClient.Instance;
                    var patchUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/device_tokens?token=eq.{Uri.EscapeDataString(item.Token)}";
                    var payload = JsonSerializer.Serialize(new { is_active = newActive });

                    using var req = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                    };
                    req.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    req.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                    var resp = await http.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        LogConsole($"[Tokens] Token '{item.Token}' updated to {(newActive ? "Active" : "Revoked")}.");
                        await LoadTokensAndDevicesAsync();
                    }
                    else
                    {
                        ModernMessageBox.ShowError($"Failed to update token status: {resp.StatusCode}", "Error");
                    }
                }
                catch (Exception ex)
                {
                    LogConsole($"[Tokens] Toggle token error: {ex.Message}");
                }
            }
        }

        #endregion

        #region 3. Hardware Bans & Device Unbind

        private void DeviceSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterDevices();
        }

        private void FilterDevices()
        {
            string query = DeviceSearchTextBox?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                DevicesDataGrid.ItemsSource = _allDevices.ToList();
            }
            else
            {
                DevicesDataGrid.ItemsSource = _allDevices.Where(d =>
                    d.DisplayName.ToLowerInvariant().Contains(query) ||
                    d.DeviceId.ToLowerInvariant().Contains(query) ||
                    d.Token.ToLowerInvariant().Contains(query)
                ).ToList();
            }
        }

        private async void ReloadDevices_Click(object sender, RoutedEventArgs e)
        {
            await LoadTokensAndDevicesAsync();
            await LoadBlockedDevicesAsync();
        }

        private async void UnbindDeviceItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DevDeviceAccessItem item)
            {
                bool confirm = ModernMessageBox.ShowConfirm(
                    $"Are you sure you want to unbind device '{item.DisplayName}' ({item.DeviceId}) from token '{item.Token}'?\n\nThis device will be removed from its slot and can log in again using the same or another active token.",
                    "Unbind Device from Token"
                );

                if (!confirm) return;

                try
                {
                    var newDeviceIds = item.BoundDeviceIds.Where(d => !string.Equals(d, item.DeviceId, StringComparison.OrdinalIgnoreCase)).ToList();

                    var http = SharedHttpClient.Instance;
                    var patchUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/device_tokens?token=eq.{Uri.EscapeDataString(item.Token)}";

                    object updatePayload;
                    if (newDeviceIds.Count == 0)
                    {
                        updatePayload = new
                        {
                            device_id = (string?)null,
                            device_ids = new string[0],
                            verified_at = (string?)null
                        };
                    }
                    else
                    {
                        updatePayload = new
                        {
                            device_id = newDeviceIds[0],
                            device_ids = newDeviceIds
                        };
                    }

                    using var req = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(updatePayload), Encoding.UTF8, "application/json")
                    };
                    req.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    req.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                    var resp = await http.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        LogConsole($"[Hardware] Device '{item.DeviceId}' unbound from token '{item.Token}'.");
                        await LoadTokensAndDevicesAsync();
                    }
                }
                catch (Exception ex)
                {
                    LogConsole($"[Hardware] Unbind error: {ex.Message}");
                }
            }
        }

        private async void BanDeviceItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DevDeviceAccessItem item)
            {
                bool confirm = ModernMessageBox.ShowConfirm(
                    $"⛔ WARNING: PERMANENT HARDWARE BAN\n\nUser: {item.DisplayName}\nHardware ID: {item.DeviceId}\nToken: {item.Token}\n\nBan Effects:\n• Device cannot open or log into the application.\n• Device cannot generate new tokens.\n• Device cannot use any other active token.\n• Access is permanently revoked unless lifted by Administrator.",
                    "Confirm Hardware Ban",
                    isDestructive: true
                );

                if (!confirm) return;

                try
                {
                    var http = SharedHttpClient.Instance;

                    // 1. Insert into blocked_devices table
                    var blockUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/blocked_devices";
                    var blockPayload = new
                    {
                        device_id = item.DeviceId,
                        reason = $"Banned by admin via device console ({item.DisplayName})",
                        blocked_by = _currentAdminProfile?.DisplayName ?? "Administrator"
                    };

                    using var blockReq = new HttpRequestMessage(HttpMethod.Post, blockUrl)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(blockPayload), Encoding.UTF8, "application/json")
                    };
                    blockReq.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    blockReq.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                    var bResp = await http.SendAsync(blockReq);
                    LogConsole($"[HardwareBan] Blocked device '{item.DeviceId}' status: {bResp.StatusCode}");

                    // 2. Unbind from token
                    var newDeviceIds = item.BoundDeviceIds.Where(d => !string.Equals(d, item.DeviceId, StringComparison.OrdinalIgnoreCase)).ToList();
                    var patchUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/device_tokens?token=eq.{Uri.EscapeDataString(item.Token)}";

                    object updatePayload;
                    if (newDeviceIds.Count == 0)
                    {
                        updatePayload = new { device_id = (string?)null, device_ids = new string[0], verified_at = (string?)null };
                    }
                    else
                    {
                        updatePayload = new { device_id = newDeviceIds[0], device_ids = newDeviceIds };
                    }

                    using var unbindReq = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(updatePayload), Encoding.UTF8, "application/json")
                    };
                    unbindReq.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    unbindReq.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                    await http.SendAsync(unbindReq);

                    ModernMessageBox.ShowInformation(
                        $"Hardware ID '{item.DeviceId}' ({item.DisplayName}) has been permanently banned.",
                        "Device Banned"
                    );

                    await LoadTokensAndDevicesAsync();
                    await LoadBlockedDevicesAsync();
                }
                catch (Exception ex)
                {
                    LogConsole($"[HardwareBan] Error banning device: {ex.Message}");
                    ModernMessageBox.ShowError($"Failed to ban device: {ex.Message}", "Error");
                }
            }
        }

        private async Task LoadBlockedDevicesAsync()
        {
            try
            {
                _allBlocked.Clear();
                var http = SharedHttpClient.Instance;
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/blocked_devices?select=*&order=created_at.desc";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                req.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var content = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            int id = el.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
                            string devId = el.TryGetProperty("device_id", out var dEl) ? dEl.GetString() ?? "" : "";
                            string reason = el.TryGetProperty("reason", out var rEl) ? rEl.GetString() ?? "" : "";
                            string blockedBy = el.TryGetProperty("blocked_by", out var bEl) ? bEl.GetString() ?? "" : "";
                            string createdAt = el.TryGetProperty("created_at", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() ?? "" : "";

                            _allBlocked.Add(new DevBlockedItem
                            {
                                Id = id,
                                DeviceId = devId,
                                Reason = reason,
                                BlockedBy = blockedBy,
                                CreatedAt = createdAt
                            });
                        }
                    }
                }

                BlockedDevicesDataGrid.ItemsSource = _allBlocked.ToList();
                LogConsole($"[BlockedDevices] Loaded {_allBlocked.Count} hardware blacklist records.");
            }
            catch (Exception ex)
            {
                LogConsole($"[BlockedDevices] Failed to load blocked devices: {ex.Message}");
            }
        }

        private async void UnblockDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DevBlockedItem item)
            {
                bool confirm = ModernMessageBox.ShowConfirm(
                    $"Are you sure you want to unblock the following hardware ID?\n\n'{item.DeviceId}'",
                    "Unblock Hardware"
                );

                if (!confirm) return;

                try
                {
                    var http = SharedHttpClient.Instance;
                    var delUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/blocked_devices?id=eq.{item.Id}";

                    using var req = new HttpRequestMessage(HttpMethod.Delete, delUrl);
                    req.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    req.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                    var resp = await http.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        LogConsole($"[BlockedDevices] Unblocked device '{item.DeviceId}'.");
                        ModernMessageBox.ShowInformation($"Hardware ban for '{item.DeviceId}' has been lifted.", "Success");
                        await LoadBlockedDevicesAsync();
                    }
                }
                catch (Exception ex)
                {
                    LogConsole($"[BlockedDevices] Error unblocking device: {ex.Message}");
                }
            }
        }

        #endregion

        #region 4. User Roles & Profiles

        private async Task LoadUsersAsync()
        {
            try
            {
                _allUsers.Clear();
                var http = SharedHttpClient.Instance;
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?select=*&order=created_at.desc&limit=1000";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                req.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var content = await resp.Content.ReadAsStringAsync();
                    var profiles = JsonSerializer.Deserialize<List<UserProfile>>(content, _jsonOpts);
                    if (profiles != null)
                    {
                        foreach (var p in profiles)
                        {
                            _allUsers.Add(new DevUserItem
                            {
                                Id = p.Id ?? 0,
                                DisplayName = p.DisplayName,
                                BadgeText = p.BadgeText,
                                DeviceId = p.DeviceId,
                                Bio = p.Bio,
                                Level = p.Level,
                                AvatarUrl = p.AvatarUrl,
                                CreatedAt = p.LastSeenText
                            });
                        }
                    }
                }

                _allUsers.Sort((a, b) => {
                    int rankCmp = a.RoleRank.CompareTo(b.RoleRank);
                    if (rankCmp != 0) return rankCmp;
                    return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                });

                FilterUsers();
                LogConsole($"[Users] Loaded {_allUsers.Count} user profiles.");
            }
            catch (Exception ex)
            {
                LogConsole($"[Users] Error loading user profiles: {ex.Message}");
            }
        }

        private void UserSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterUsers();
        }

        private void FilterUsers()
        {
            string query = UserSearchTextBox?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            var filtered = string.IsNullOrWhiteSpace(query)
                ? _allUsers
                : _allUsers.Where(u =>
                    u.DisplayName.ToLowerInvariant().Contains(query) ||
                    u.DeviceId.ToLowerInvariant().Contains(query) ||
                    u.Bio.ToLowerInvariant().Contains(query)
                );

            UsersDataGrid.ItemsSource = filtered
                .OrderBy(u => u.RoleRank)
                .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async void ReloadUsers_Click(object sender, RoutedEventArgs e)
        {
            await LoadUsersAsync();
        }

        private void UsersDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedUser = UsersDataGrid.SelectedItem as DevUserItem;
            if (_selectedUser != null)
            {
                foreach (ComboBoxItem item in RoleLevelComboBox.Items)
                {
                    if (item.Tag?.ToString() == _selectedUser.Level.ToString())
                    {
                        RoleLevelComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private async void UpdateRoleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedUser == null)
            {
                ModernMessageBox.ShowWarning("Please select a user from the table first.", "Warning");
                return;
            }

            var selectedItem = RoleLevelComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null || !int.TryParse(selectedItem.Tag?.ToString(), out int newLevel))
                return;

            string roleName = newLevel switch { 0 => "ADMIN", 3 => "MODERATOR", 1 => "SUPPORTER", _ => "MEMBER" };

            bool confirm = ModernMessageBox.ShowConfirm(
                $"Are you sure you want to change user '{_selectedUser.DisplayName}' role to [{roleName}] (Level {newLevel})?",
                "Confirm Role Change"
            );

            if (!confirm) return;

            try
            {
                var http = SharedHttpClient.Instance;
                var patchUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?id=eq.{_selectedUser.Id}";
                var payload = JsonSerializer.Serialize(new { level = newLevel });

                using var req = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                req.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    LogConsole($"[UserRoles] User '{_selectedUser.DisplayName}' (UID {_selectedUser.Id}) level updated to {newLevel} [{roleName}].");
                    ModernMessageBox.ShowInformation($"User '{_selectedUser.DisplayName}' role has been updated to [{roleName}].", "Success");
                    await LoadUsersAsync();
                }
                else
                {
                    ModernMessageBox.ShowError($"Failed to update role: {resp.StatusCode}", "Error");
                }
            }
            catch (Exception ex)
            {
                LogConsole($"[UserRoles] Error updating role: {ex.Message}");
            }
        }

        private async void ResetUserAvatar_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedUser == null)
            {
                ModernMessageBox.ShowWarning("Please select a user first.", "Warning");
                return;
            }

            bool confirm = ModernMessageBox.ShowConfirm(
                $"Are you sure you want to reset profile picture for '{_selectedUser.DisplayName}'?",
                "Reset Profile Picture"
            );

            if (!confirm) return;

            try
            {
                await ProfilePictureCacheService.DeleteAvatarAsync(_selectedUser.Id);

                var http = SharedHttpClient.Instance;
                var patchUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?id=eq.{_selectedUser.Id}";
                var payload = JsonSerializer.Serialize(new { avatar_url = (string?)null });

                using var req = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                req.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    LogConsole($"[UserRoles] Avatar for user '{_selectedUser.DisplayName}' reset.");
                    ModernMessageBox.ShowInformation("User profile picture has been reset to default initial.", "Success");
                    await LoadUsersAsync();
                }
            }
            catch (Exception ex)
            {
                LogConsole($"[UserRoles] Error resetting avatar: {ex.Message}");
            }
        }

        private async void ClearUserBio_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedUser == null)
            {
                ModernMessageBox.ShowWarning("Please select a user first.", "Warning");
                return;
            }

            bool confirm = ModernMessageBox.ShowConfirm(
                $"Are you sure you want to clear bio for '{_selectedUser.DisplayName}'?",
                "Clear User Bio"
            );

            if (!confirm) return;

            try
            {
                var http = SharedHttpClient.Instance;
                var patchUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?id=eq.{_selectedUser.Id}";
                var payload = JsonSerializer.Serialize(new { bio = "" });

                using var req = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                req.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    LogConsole($"[UserRoles] Bio for user '{_selectedUser.DisplayName}' cleared.");
                    ModernMessageBox.ShowInformation("User bio has been cleared.", "Success");
                    await LoadUsersAsync();
                }
            }
            catch (Exception ex)
            {
                LogConsole($"[UserRoles] Error clearing bio: {ex.Message}");
            }
        }

        #endregion

        #region 5. Storage & Orphan Cleaner

        private void ClearAvatarDiskCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamPluginManager", "AvatarCache");
                if (Directory.Exists(cacheDir))
                {
                    var files = Directory.GetFiles(cacheDir, "*.jpg");
                    int count = 0;
                    long bytes = 0;
                    foreach (var f in files)
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            bytes += fi.Length;
                            File.Delete(f);
                            count++;
                        }
                        catch { }
                    }

                    string sizeStr = (bytes / 1024.0).ToString("F1") + " KB";
                    LogConsole($"[Storage] Local avatar cache cleared: {count} files removed ({sizeStr}).");
                    ModernMessageBox.ShowInformation($"Successfully removed {count} local avatar cache files ({sizeStr}).", "Cache Cleared");
                }
                else
                {
                    ModernMessageBox.ShowInformation("Local avatar cache directory is empty or does not exist.", "Info");
                }
            }
            catch (Exception ex)
            {
                LogConsole($"[Storage] Error clearing avatar cache: {ex.Message}");
            }
        }

        private async void ScanOrphanStorage_Click(object sender, RoutedEventArgs e)
        {
            StorageLogsTextBox.Text = "=== STORAGE ORPHAN SCANNER STARTED ===\r\nScanning Backblaze B2 and Cloudflare R2 files...\r\n";
            try
            {
                var knownAppIds = new HashSet<int>(_allGames.Select(g => g.AppId));
                var orphanB2 = new List<string>();
                var orphanR2 = new List<string>();

                // 1. Scan Backblaze B2 onlinefix/
                try
                {
                    var b2Files = await B2Config.ListFilesAsync("onlinefix/");
                    StorageLogsTextBox.AppendText($"\r\n[Backblaze B2] Found {b2Files.Count} files in 'onlinefix/'\r\n");
                    foreach (var f in b2Files)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(f.Key);
                        if (int.TryParse(fileName, out int aid))
                        {
                            if (!knownAppIds.Contains(aid))
                            {
                                orphanB2.Add(f.Key);
                            }
                        }
                    }
                }
                catch (Exception b2Ex)
                {
                    StorageLogsTextBox.AppendText($"[Backblaze B2] Scan error: {b2Ex.Message}\r\n");
                }

                // 2. Scan Cloudflare R2 gamebypass/
                try
                {
                    var r2Files = await R2Config.ListFilesAsync("gamebypass/");
                    StorageLogsTextBox.AppendText($"[Cloudflare R2] Found {r2Files.Count} files in 'gamebypass/'\r\n");
                    foreach (var f in r2Files)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(f.Key);
                        if (int.TryParse(fileName, out int aid))
                        {
                            if (!knownAppIds.Contains(aid))
                            {
                                orphanR2.Add(f.Key);
                            }
                        }
                    }
                }
                catch (Exception r2Ex)
                {
                    StorageLogsTextBox.AppendText($"[Cloudflare R2] Scan error: {r2Ex.Message}\r\n");
                }

                StorageLogsTextBox.AppendText("\r\n=== SCAN RESULTS ===\r\n");
                StorageLogsTextBox.AppendText($"Orphan B2 OnlineFix files (no DB entry): {orphanB2.Count}\r\n");
                foreach (var o in orphanB2) StorageLogsTextBox.AppendText($"  • {o}\r\n");

                StorageLogsTextBox.AppendText($"\r\nOrphan R2 GameBypass files (no DB entry): {orphanR2.Count}\r\n");
                foreach (var o in orphanR2) StorageLogsTextBox.AppendText($"  • {o}\r\n");

                StorageLogsTextBox.AppendText("\r\nScan completed successfully.");
            }
            catch (Exception ex)
            {
                StorageLogsTextBox.AppendText($"\r\nScan failed with exception: {ex.Message}\r\n");
            }
        }

        #endregion

        #region 6. Pinned Announcement

        private BroadcastAnnouncement? _activeBroadcast;

        private async Task LoadActiveBroadcastAsync()
        {
            try
            {
                _activeBroadcast = await CommunityChatService.GetActiveBroadcastAsync();
                Dispatcher.Invoke(() =>
                {
                    if (_activeBroadcast != null)
                    {
                        ActiveBroadcastPanel.Visibility = Visibility.Visible;
                        NoActiveBroadcastText.Visibility = Visibility.Collapsed;
                        DeleteBroadcastBtn.IsEnabled = true;

                        ActiveBroadcastTitleText.Text = _activeBroadcast.Title;
                        ActiveBroadcastMessageText.Text = _activeBroadcast.Message;
                        ActiveBroadcastTimeText.Text = $"Posted: {_activeBroadcast.FormattedTime}";
                    }
                    else
                    {
                        ActiveBroadcastPanel.Visibility = Visibility.Collapsed;
                        NoActiveBroadcastText.Visibility = Visibility.Visible;
                        DeleteBroadcastBtn.IsEnabled = false;
                    }
                });
            }
            catch (Exception ex)
            {
                LogConsole($"[Announcement] Error loading active broadcast: {ex.Message}");
            }
        }

        private async void RefreshBroadcast_Click(object sender, RoutedEventArgs e)
        {
            await LoadActiveBroadcastAsync();
        }

        private async void BroadcastMessage_Click(object sender, RoutedEventArgs e)
        {
            string title = BroadcastTitleInput?.Text?.Trim() ?? "";
            string body = BroadcastBodyInput?.Text?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
            {
                ModernMessageBox.ShowWarning("Announcement title and message body cannot be empty.", "Warning");
                return;
            }

            bool confirm = ModernMessageBox.ShowConfirm(
                $"Are you sure you want to pin this broadcast announcement at the top of Community Chat for all users?\n\nTitle: {title}\n\nMessage:\n{body}",
                "Confirm Announcement"
            );

            if (!confirm) return;

            try
            {
                var result = await CommunityChatService.PublishBroadcastAsync(title, body);
                if (result.Success)
                {
                    LogConsole($"[Announcement] Pinned broadcast successfully published: '{title}'");
                    ModernMessageBox.ShowInformation("Pinned broadcast announcement successfully published.", "Success");
                    await LoadActiveBroadcastAsync();
                }
                else
                {
                    ModernMessageBox.ShowError($"Failed to publish announcement: {result.ErrorMessage}", "Error");
                }
            }
            catch (Exception ex)
            {
                LogConsole($"[Announcement] Error publishing broadcast: {ex.Message}");
            }
        }

        private async void DeleteBroadcast_Click(object sender, RoutedEventArgs e)
        {
            if (_activeBroadcast == null)
            {
                ModernMessageBox.ShowWarning("There is no active pinned announcement to delete.", "Notice");
                return;
            }

            bool confirm = ModernMessageBox.ShowConfirm(
                $"Are you sure you want to delete and unpin the active broadcast announcement?\n\n'{_activeBroadcast.Title}'",
                "Delete Announcement",
                isDestructive: true
            );

            if (!confirm) return;

            try
            {
                var result = await CommunityChatService.DeleteBroadcastAsync(_activeBroadcast.Id);
                if (result.Success)
                {
                    LogConsole("[Announcement] Pinned broadcast announcement deleted and unpinned.");
                    ModernMessageBox.ShowInformation("Broadcast announcement has been removed and unpinned.", "Success");
                    await LoadActiveBroadcastAsync();
                }
                else
                {
                    ModernMessageBox.ShowError($"Failed to delete announcement: {result.ErrorMessage}", "Error");
                }
            }
            catch (Exception ex)
            {
                LogConsole($"[Announcement] Error deleting broadcast: {ex.Message}");
            }
        }

        #endregion

        #region 7. Diagnostics & System Console

        private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsSummaryText.Text = "Running endpoint latency tests...";
            LogConsole("\r\n=== STARTING SYSTEM DIAGNOSTICS & PING TESTS ===");

            var tests = new List<(string Name, Func<Task<long>> Action)>
            {
                ("Supabase Database REST", async () => {
                    var sw = Stopwatch.StartNew();
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{SupabaseConfig.SupabaseUrl}/rest/v1/");
                    req.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    var resp = await http.SendAsync(req);
                    sw.Stop();
                    return resp.IsSuccessStatusCode ? sw.ElapsedMilliseconds : throw new Exception($"HTTP {(int)resp.StatusCode}");
                }),
                ("Supabase Storage API", async () => {
                    var sw = Stopwatch.StartNew();
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{SupabaseConfig.SupabaseUrl}/storage/v1/bucket");
                    req.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    var resp = await http.SendAsync(req);
                    sw.Stop();
                    return resp.IsSuccessStatusCode ? sw.ElapsedMilliseconds : throw new Exception($"HTTP {(int)resp.StatusCode}");
                }),
                ("Backblaze B2 S3 Endpoint", async () => {
                    var sw = Stopwatch.StartNew();
                    await B2Config.ListFilesAsync("test_ping/");
                    sw.Stop();
                    return sw.ElapsedMilliseconds;
                }),
                ("Cloudflare R2 S3 Endpoint", async () => {
                    var sw = Stopwatch.StartNew();
                    await R2Config.ListFilesAsync("test_ping/");
                    sw.Stop();
                    return sw.ElapsedMilliseconds;
                }),
                ("Steam Store API", async () => {
                    var sw = Stopwatch.StartNew();
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    var resp = await http.GetAsync("https://store.steampowered.com/api/appdetails?appids=730");
                    sw.Stop();
                    return resp.IsSuccessStatusCode ? sw.ElapsedMilliseconds : throw new Exception($"HTTP {(int)resp.StatusCode}");
                })
            };

            int successCount = 0;
            foreach (var test in tests)
            {
                try
                {
                    long ms = await test.Action();
                    LogConsole($"  ✅ {test.Name,-30} : SUCCESS ({ms} ms)");
                    successCount++;
                }
                catch (Exception ex)
                {
                    LogConsole($"  ❌ {test.Name,-30} : FAILED ({ex.Message})");
                }
            }

            DiagnosticsSummaryText.Text = $"Diagnostics completed: {successCount}/{tests.Count} services online.";
            LogConsole($"=== DIAGNOSTICS COMPLETED: {successCount}/{tests.Count} ONLINE ===\r\n");
        }

        private void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(DiagnosticsConsoleTextBox.Text))
                {
                    Clipboard.SetText(DiagnosticsConsoleTextBox.Text);
                    ModernMessageBox.ShowInformation("Diagnostic console logs copied to clipboard.", "Copied");
                }
            }
            catch (Exception ex)
            {
                LogConsole($"[Console] Copy error: {ex.Message}");
            }
        }

        private void ClearConsole_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsConsoleTextBox.Text = $"Console cleared at {DateTime.Now:HH:mm:ss}.\r\n";
        }

        private void LogConsole(string message)
        {
            string timeStamp = DateTime.Now.ToString("HH:mm:ss");
            string formatted = $"[{timeStamp}] {message}";
            Logger.Log(formatted);

            Dispatcher.InvokeAsync(() =>
            {
                DiagnosticsConsoleTextBox?.AppendText(formatted + Environment.NewLine);
                DiagnosticsConsoleTextBox?.ScrollToEnd();
            });
        }

        #endregion
    }

    #region View Models for DevView

    public class DevManifestGame : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public int AppId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public string StorageType { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
        public string UploadDate { get; set; } = string.Empty;
        public ManifestFile? RawManifest { get; set; }
    }

    public class DevTokenRevokeItem
    {
        public string Token { get; set; } = string.Empty;
        public string DisplayNames { get; set; } = string.Empty;
        public string DeviceCountText { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public string StatusBadge { get; set; } = string.Empty;
        public string VerifiedAt { get; set; } = string.Empty;
        public List<string> BoundDeviceIds { get; set; } = new();
    }

    public class DevDeviceAccessItem
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string SlotName { get; set; } = string.Empty;
        public int SlotIndex { get; set; }
        public List<string> BoundDeviceIds { get; set; } = new();
    }

    public class DevBlockedItem
    {
        public int Id { get; set; }
        public string DeviceId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string BlockedBy { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
    }

    public class DevUserItem
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string BadgeText { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string Bio { get; set; } = string.Empty;
        public int Level { get; set; }
        public string? AvatarUrl { get; set; }
        public string CreatedAt { get; set; } = string.Empty;

        public int RoleRank => Level switch
        {
            0 => 0, // Admin
            3 => 1, // Moderator
            1 => 2, // Supporter
            2 => 3, // Member
            _ => 4
        };
    }

    #endregion
}
