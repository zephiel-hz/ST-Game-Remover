using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Net.NetworkInformation;
using System.Management;
using System.Text.Json.Serialization;
using SteamPluginManager;

namespace SteamPluginManager.Views
{
    public partial class VerifyTokenView : UserControl
    {
        private MainWindow? _mainWindow;

        public VerifyTokenView()
        {
            InitializeComponent();
            VerifyLogoImage.Source = CreateThemeIcon();
            App.ThemeChanged += ThemeChanged_Handler;
            Loaded += VerifyTokenView_Loaded;
            Unloaded += VerifyTokenView_Unloaded;
        }

        private void ThemeChanged_Handler(object? sender, EventArgs e)
        {
            VerifyLogoImage.Source = CreateThemeIcon();
        }

        private static BitmapImage CreateThemeIcon()
        {
            var icon = new BitmapImage();
            icon.BeginInit();
            icon.UriSource = new Uri(App.GetThemeIconPath(), UriKind.Absolute);
            icon.CacheOption = BitmapCacheOption.OnLoad;
            icon.EndInit();
            icon.Freeze();
            return icon;
        }

        private void VerifyTokenView_Unloaded(object sender, RoutedEventArgs e)
        {
            App.ThemeChanged -= ThemeChanged_Handler;
        }

        public void SetToken(string token)
        {
            try
            {
                TokenInput.Text = token;
                TokenInput.Focus();
                TokenInput.CaretIndex = TokenInput.Text?.Length ?? 0;
            }
            catch { }
        }

        private void VerifyTokenView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _mainWindow = Application.Current.MainWindow as MainWindow;

                // Enable input field and set focus
                TokenInput.IsEnabled = true;
                TokenInput.Focus();
                LoadingGrid.Visibility = Visibility.Collapsed;

                Logger.Log("[VerifyTokenView] UI initialized");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VerifyTokenView] Error in Loaded: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnClickHere(object sender, RoutedEventArgs e)
        {
            Logger.Log("[VerifyTokenView] Click here link clicked - opening in-app token generator");
            try
            {
                WindowNavigator.NavigateToGenerateToken();
            }
            catch (Exception ex)
            {
                Logger.Log($"[VerifyTokenView] Failed to open in-app token generator: {ex.Message}");
                MessageBox.Show("Unable to open the token generator. Please visit: https://hzluamanager-get-token.lovable.app", "Open Link Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BackToDashboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Log("[VerifyTokenView] Back button clicked - Navigating to Dashboard");
                WindowNavigator.NavigateToDashboard();
            }
            catch (Exception ex)
            {
                Logger.Log($"[VerifyTokenView] Error in BackToDashboard_Click: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void VerifyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? token = TokenInput.Text?.Trim();
                
                if (string.IsNullOrEmpty(token))
                {
                    StatusMessage.Foreground = (System.Windows.Media.Brush)TryFindResource("DangerButtonBrush");
                    StatusMessage.Text = "❌ Please enter a token";
                    return;
                }

                // Show loading state
                VerifyButton.IsEnabled = false;
                LoadingGrid.Visibility = Visibility.Visible;
                LoadingText.Text = "Verifying token...";
                StatusMessage.Text = "";

                Logger.Log($"[VerifyTokenView] Verifying token: {token.Substring(0, Math.Min(20, token.Length))}...");

                // Get device ID
                string deviceId = GetDeviceId();
                Logger.Log($"[VerifyTokenView] Device ID: {deviceId}");

                // Call Supabase to verify token
                VerificationResult verification = await VerifyTokenAsync(token, deviceId);

                if (verification.IsExpiredOrRevoked)
                {
                    LoadingGrid.Visibility = Visibility.Collapsed;
                    VerifyButton.IsEnabled = true;
                    StatusMessage.Foreground = (System.Windows.Media.Brush)TryFindResource("DangerButtonBrush");
                    StatusMessage.Text = "❌ Token is expired/revoked";
                    Logger.Log($"[VerifyTokenView] Token is expired or revoked, redirecting to VerifyTokenView");

                    WindowNavigator.NextViewAfterVerify = null;
                    WindowNavigator.NavigateToVerifyToken();
                }
                else if (verification.IsValid)
                {
                    LoadingGrid.Visibility = Visibility.Collapsed;
                    StatusMessage.Foreground = (System.Windows.Media.Brush)TryFindResource("SuccessButtonBrush");
                    StatusMessage.Text = "✓ Token verified successfully! Redirecting...";
                    Logger.Log($"[VerifyTokenView] Token verified successfully");

                    // Navigate to the originally requested view (if any)
                    await System.Threading.Tasks.Task.Delay(1500);
                    try
                    {
                        var next = WindowNavigator.NextViewAfterVerify;
                        WindowNavigator.NextViewAfterVerify = null;
                        if (string.Equals(next, "GameBypass", StringComparison.OrdinalIgnoreCase))
                            WindowNavigator.NavigateToGameBypass();
                        else if (string.Equals(next, "OnlineFix", StringComparison.OrdinalIgnoreCase))
                            WindowNavigator.NavigateToOnlineFix();
                        else if (string.Equals(next, "Dashboard", StringComparison.OrdinalIgnoreCase))
                            WindowNavigator.NavigateToDashboard();
                        else // default to HZManifest
                            WindowNavigator.NavigateToHZManifest();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[VerifyTokenView] Navigation after verify failed: {ex.Message}");
                        WindowNavigator.NavigateToHZManifest();
                    }
                }
                else
                {
                    LoadingGrid.Visibility = Visibility.Collapsed;
                    VerifyButton.IsEnabled = true;
                    StatusMessage.Foreground = (System.Windows.Media.Brush)TryFindResource("DangerButtonBrush");
                    StatusMessage.Text = "❌ Invalid token or token has reached the two-device limit";
                    Logger.Log($"[VerifyTokenView] Token verification failed");
                }
            }
            catch (Exception ex)
            {
                VerifyButton.IsEnabled = true;
                LoadingGrid.Visibility = Visibility.Collapsed;
                StatusMessage.Foreground = (System.Windows.Media.Brush)TryFindResource("DangerButtonBrush");
                StatusMessage.Text = $"Error: {ex.Message}";
                Logger.Log($"[VerifyTokenView] Error: {ex.Message}");
            }
        }

        private string GetDeviceId()
        {
            try
            {
                // Get stable hardware ID (motherboard serial or MAC address)
                // NOTE: Using ONLY hardware ID (not machine name) so token remains valid
                // even if user changes their device name
                string deviceId = GetMotherboardHardwareId();
                Logger.Log($"[VerifyTokenView] Generated device ID: {deviceId}");
                
                return deviceId;
            }
            catch (Exception ex)
            {
                Logger.Log($"[VerifyTokenView] Error generating device ID: {ex.Message}");
                return "UNKNOWN";
            }
        }

        private string GetMotherboardHardwareId()
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
                            Logger.Log($"[GetMotherboardHardwareId] Found Motherboard Serial: {serialNumber}");
                            return serialNumber;
                        }
                        else if (!string.IsNullOrWhiteSpace(serialNumber))
                        {
                            Logger.Log($"[GetMotherboardHardwareId] Motherboard serial is invalid/default: '{serialNumber}', will use MAC address instead");
                        }
                    }
                }

                // Fallback: Get first stable MAC address if motherboard ID fails or is invalid
                Logger.Log("[GetMotherboardHardwareId] Motherboard serial not available or invalid, using MAC address fallback");
                var macAddress = NetworkInterface
                    .GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                                  !string.IsNullOrEmpty(nic.GetPhysicalAddress().ToString()))
                    .Select(nic => nic.GetPhysicalAddress().ToString())
                    .FirstOrDefault() ?? "UNKNOWN";

                if (macAddress == "UNKNOWN")
                {
                    Logger.Log("[GetMotherboardHardwareId] WARNING: Could not get MAC address, using fallback UNKNOWN");
                }

                return macAddress;
            }
            catch (Exception ex)
            {
                Logger.Log($"[GetMotherboardHardwareId] Error: {ex.Message}");
                return "UNKNOWN";
            }
        }

        private bool IsValidSerialNumber(string serialNumber)
        {
            // Reject common default/invalid values
            string lower = serialNumber.ToLower().Trim();
            
            // List of invalid/default values to reject
            var invalidValues = new[]
            {
                "0000000",
                "00000000",
                "000000000000",
                "default string",
                "default",
                "system default",
                "not available",
                "not specified",
                "unknown",
                "system reserved",
                "pending",
                "to be filled by o.e.m.",
                "empty",
                "none",
                "n/a"
            };

            // Check if serial number matches any invalid pattern
            if (invalidValues.Contains(lower))
            {
                Logger.Log($"[IsValidSerialNumber] Rejected invalid serial: {serialNumber}");
                return false;
            }

            // Reject if only contains zeros or hyphens/spaces
            if (string.IsNullOrWhiteSpace(serialNumber.Replace("0", "").Replace("-", "").Replace(" ", "")))
            {
                Logger.Log($"[IsValidSerialNumber] Rejected all-zeros serial: {serialNumber}");
                return false;
            }

            return true;
        }

        private async System.Threading.Tasks.Task<VerificationResult> VerifyTokenAsync(string token, string deviceId)
        {
            try
            {
                // Query Supabase to check if token exists and is not used
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(15);
                    
                    // URL encode the token for the query
                    string escapedToken = Uri.EscapeDataString(token);
                    var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/device_tokens?token=eq.{escapedToken}&select=token,device_id,device_ids,verified_at,is_active";
                    
                    var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                    request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                    Logger.Log($"[VerifyTokenView] Querying Supabase for token: {escapedToken}");
                    Logger.Log($"[VerifyTokenView] URL: {url}");
                    
                    var response = await http.SendAsync(request);
                    var content = await response.Content.ReadAsStringAsync();

                    Logger.Log($"[VerifyTokenView] Supabase response status: {response.StatusCode}");
                    Logger.Log($"[VerifyTokenView] Supabase response content: {content}");

                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.Log($"[VerifyTokenView] Failed to query Supabase: {content}");
                        return new VerificationResult(false, false);
                    }

                    // Parse response
                    var options = new System.Text.Json.JsonSerializerOptions 
                    { 
                        PropertyNameCaseInsensitive = true 
                    };
                    var tokens = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<TokenRecord>>(content, options);
                    
                    Logger.Log($"[VerifyTokenView] Parsed tokens count: {tokens?.Count ?? 0}");

                    if (tokens == null || tokens.Count == 0)
                    {
                        Logger.Log($"[VerifyTokenView] Token not found in database");
                        return new VerificationResult(false, false);
                    }

                    var tokenRecord = tokens[0];
                    var boundDeviceIds = GetBoundDeviceIds(tokenRecord);
                    Logger.Log($"[VerifyTokenView] Token found - device_id: {tokenRecord.device_id ?? "NULL"}, device_ids: {string.Join(",", boundDeviceIds)}, is_active: {tokenRecord.is_active?.ToString() ?? "NULL"}");

                    if (tokenRecord.is_active == false)
                    {
                        Logger.Log($"[VerifyTokenView] Token is inactive/revoked");
                        return new VerificationResult(false, true);
                    }

                    if (boundDeviceIds.Any(existingDeviceId => string.Equals(existingDeviceId, deviceId, StringComparison.OrdinalIgnoreCase)))
                    {
                        Logger.Log($"[VerifyTokenView] Token already bound to this device: {deviceId}");
                        return new VerificationResult(true, false);
                    }

                    if (boundDeviceIds.Count >= 2)
                    {
                        Logger.Log($"[VerifyTokenView] Token has reached the maximum of two devices");
                        return new VerificationResult(false, false);
                    }

                    // Token is valid for a second device - update the bound devices in Supabase
                    var updatedDeviceIds = boundDeviceIds
                        .Concat(new[] { deviceId })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    string escapedTokenForUpdate = Uri.EscapeDataString(token);
                    var updateUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/device_tokens?token=eq.{escapedTokenForUpdate}";
                    var updateBody = new
                    {
                        device_id = tokenRecord.device_id ?? deviceId,
                        device_ids = updatedDeviceIds,
                        verified_at = DateTime.UtcNow
                    };

                    var updateRequest = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Patch, updateUrl);
                    updateRequest.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    updateRequest.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                    updateRequest.Headers.Add("Prefer", "return=minimal");
                    
                    var jsonBody = System.Text.Json.JsonSerializer.Serialize(updateBody);
                    updateRequest.Content = new System.Net.Http.StringContent(
                        jsonBody,
                        System.Text.Encoding.UTF8,
                        "application/json"
                    );

                    Logger.Log($"[VerifyTokenView] Updating token with device ID: {deviceId}");
                    Logger.Log($"[VerifyTokenView] Update body: {jsonBody}");
                    
                    var updateResponse = await http.SendAsync(updateRequest);
                    var updateContent = await updateResponse.Content.ReadAsStringAsync();
                    
                    Logger.Log($"[VerifyTokenView] Update response status: {updateResponse.StatusCode}");
                    Logger.Log($"[VerifyTokenView] Update response content: {updateContent}");
                    
                    if (updateResponse.IsSuccessStatusCode)
                    {
                        Logger.Log($"[VerifyTokenView] Token updated successfully");
                        return new VerificationResult(true, false);
                    }
                    else
                    {
                        Logger.Log($"[VerifyTokenView] Failed to update token: {updateContent}");
                        return new VerificationResult(false, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[VerifyTokenView] Exception in VerifyTokenAsync: {ex.Message}");
                Logger.Log($"[VerifyTokenView] Stack trace: {ex.StackTrace}");
                return new VerificationResult(false, false);
            }
        }

        private static List<string> GetBoundDeviceIds(TokenRecord tokenRecord)
        {
            var boundDeviceIds = tokenRecord.device_ids?
                .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
                .Select(deviceId => deviceId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (boundDeviceIds.Count == 0 && !string.IsNullOrWhiteSpace(tokenRecord.device_id))
            {
                boundDeviceIds.Add(tokenRecord.device_id.Trim());
            }

            return boundDeviceIds;
        }

        private class VerificationResult
        {
            public VerificationResult(bool isValid, bool isExpiredOrRevoked)
            {
                IsValid = isValid;
                IsExpiredOrRevoked = isExpiredOrRevoked;
            }

            public bool IsValid { get; }
            public bool IsExpiredOrRevoked { get; }
        }

        private class TokenRecord
        {
            [JsonPropertyName("token")]
            public string token { get; set; } = "";

            [JsonPropertyName("device_id")]
            public string? device_id { get; set; }

            [JsonPropertyName("device_ids")]
            public List<string>? device_ids { get; set; }

            [JsonPropertyName("verified_at")]
            public DateTime? verified_at { get; set; }

            [JsonPropertyName("is_active")]
            public bool? is_active { get; set; }
        }
    }
}
