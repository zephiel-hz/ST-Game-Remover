using System;
using System.IO;
using System.Reflection;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;
using Microsoft.Win32;

namespace SteamPluginManager.Views
{
    public partial class SettingsView : UserControl
    {
        
        private bool _isInitialized = false;
        private bool _isLoadingSettings = false;
        private bool _isSteamtoolsInstalled = false;
        private string? _steamtoolsPath = null;
        private SteamtoolsOverlayWindow? _overlayWindow = null;
        private string? _steamtoolsLatestReleaseHtmlUrl = null;
        private string? _steamtoolsLatestVersion = null;
        private bool _steamtoolsUpdateAvailable = false;
        private string? _steamtoolsInstalledVersion = null;

        public SettingsView()
        {
            InitializeComponent();
            Loaded += SettingsView_Loaded;
            Unloaded += SettingsView_Unloaded;
        }

        private void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized)
            {
                LoadSettings();
                RegisterTextElements();
                _isInitialized = true;
            }
            // Subscribe to language change event
            App.LanguageChanged += LanguageChanged_Handler;
        }

        private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
        {
            // Unsubscribe from language change event to prevent memory leak
            App.LanguageChanged -= LanguageChanged_Handler;
        }

        private void LanguageChanged_Handler(object? sender, EventArgs e)
        {
            RefreshLanguageUI();
        }

        private void RegisterTextElements()
        {
            try
            {
                Logger.Log("[SettingsView] Starting RegisterTextElements");
                
                // Settings title
                LanguageHelper.RegisterTextBlock(SettingsTitle, "Settings.Title");
                
                // Performance section
                LanguageHelper.RegisterTextBlock(SettingsPerformanceTitle, "Settings.Performance");
                LanguageHelper.RegisterTextBlock(SettingsHardwareAccelLabel, "Settings.HardwareAccel");
                LanguageHelper.RegisterTextBlock(SettingsHardwareAccelDesc, "Settings.HardwareAccelDesc");
                
                // Auto-update section
                LanguageHelper.RegisterTextBlock(SettingsAutoUpdateTitle, "Settings.AutoUpdate");
                LanguageHelper.RegisterTextBlock(SettingsAutoUpdateLabel, "Settings.AutoUpdateLabel");
                LanguageHelper.RegisterTextBlock(SettingsAutoUpdateDesc, "Settings.AutoUpdateDesc");
                
                // Cache section
                LanguageHelper.RegisterTextBlock(SettingsCacheTitle, "Settings.Cache");
                LanguageHelper.RegisterTextBlock(SettingsCacheDesc, "Settings.CacheInfo");
                
                // Theme section
                LanguageHelper.RegisterTextBlock(SettingsThemeTitle, "Settings.Theme");
                LanguageHelper.RegisterTextBlock(SettingsThemeDesc, "Settings.ThemeDesc");
                
                // Language section
                LanguageHelper.RegisterTextBlock(SettingsLanguageTitle, "Settings.Language");
                LanguageHelper.RegisterTextBlock(SettingsLanguageDesc, "Settings.LanguageDesc");
                
                // Button texts
                if (BackButtonText != null)
                    LanguageHelper.RegisterTextBlock(BackButtonText, "Settings.Back");
                if (ClearCacheButtonText != null)
                    LanguageHelper.RegisterTextBlock(ClearCacheButtonText, "Settings.ClearCache");

                // Steamtools section
                LanguageHelper.RegisterTextBlock(SettingsSteamtoolsTitle, "Settings.SteamtoolsTitle");
                LanguageHelper.RegisterTextBlock(SteamtoolsActionButtonText, "Settings.SteamtoolsAction");
                
                // Allow 18+ Content section
                LanguageHelper.RegisterTextBlock(SettingsAllow18PlusTitle, "Settings.Allow18Plus");
                LanguageHelper.RegisterTextBlock(SettingsAllow18PlusLabel, "Settings.Allow18PlusLabel");
                LanguageHelper.RegisterTextBlock(SettingsAllow18PlusDesc, "Settings.Allow18PlusDesc");
                
                Logger.Log("[SettingsView] All elements registered, calling RefreshLanguageUI");
                
                // Apply current language
                RefreshLanguageUI();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error registering text elements: {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            try
            {
                // Load cache size
                UpdateCacheSize();

                // Load hardware acceleration setting
                LoadHardwareAccelerationSetting();
                
                // Load auto-update setting
                LoadAutoUpdateSetting();
                
                // Load 18+ content setting
                LoadAllow18PlusSetting();
                
                // Load theme preference
                LoadThemePreference();
                
                // Load language preference
                LoadLanguagePreference();

                // Check steamtools install status
                Task.Run(() => {
                    try
                    {
                        var installed = CheckSteamtoolsInstalled();

                        // Get installed version if present, otherwise fall back to locally saved version
                        try
                        {
                            var detectedVersion = GetInstalledSteamtoolsVersion();
                            if (!string.IsNullOrWhiteSpace(detectedVersion))
                            {
                                _steamtoolsInstalledVersion = detectedVersion;
                                SaveSteamtoolsInstalledVersion(detectedVersion);
                                Logger.Log($"[Steamtools] Installed version detected: {_steamtoolsInstalledVersion}");
                            }
                            else
                            {
                                _steamtoolsInstalledVersion = LoadSavedSteamtoolsInstalledVersion();
                                Logger.Log($"[Steamtools] Loaded saved installed version: {_steamtoolsInstalledVersion}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[Steamtools] Error detecting installed version: {ex.Message}");
                        }

                        Dispatcher.Invoke(() => UpdateSteamtoolsUi(installed));

                        // After checking installation, also check for updates on GitHub
                        Dispatcher.Invoke(async () => await CheckSteamtoolsUpdateAvailabilityAsync());
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[Steamtools] Error checking install status: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool CheckSteamtoolsInstalled()
        {
            try
            {
                // Check if Steamtools registry key exists with iscdkey property (indicates script was run)
                try
                {
                    using (var regKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steamtools"))
                    {
                        if (regKey != null)
                        {
                            var iscdkeyValue = regKey.GetValue("iscdkey");
                            if (iscdkeyValue != null)
                            {
                                Logger.Log($"[Steamtools] Detected via Registry - iscdkey = {iscdkeyValue}");
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Steamtools] Registry check error: {ex.Message}");
                }

                // Also check if xinput1_4.dll exists in Steam folder (indicates script was run)
                try
                {
                    string? steamPath = null;
                    using (var regKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    {
                        if (regKey != null)
                        {
                            var steamPathValue = regKey.GetValue("SteamPath");
                            if (steamPathValue != null)
                                steamPath = steamPathValue.ToString();
                        }
                    }

                    if (!string.IsNullOrEmpty(steamPath))
                    {
                        string hidPath = Path.Combine(steamPath, "xinput1_4.dll");
                        if (File.Exists(hidPath))
                        {
                            Logger.Log($"[Steamtools] Detected via DLL at {hidPath}");
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Steamtools] DLL check error: {ex.Message}");
                }

                _steamtoolsPath = null;
                _isSteamtoolsInstalled = false;
                Logger.Log("[Steamtools] Not installed");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"[Steamtools] Exception during detection: {ex.Message}");
                _steamtoolsPath = null;
                _isSteamtoolsInstalled = false;
                return false;
            }
        }

        private async Task CheckSteamtoolsUpdateAvailabilityAsync()
        {
            try
            {
                var client = SharedHttpClient.Instance;
                const string latestReleaseApi = "https://api.github.com/repos/OpenSteam001/OpenSteamTool/releases/latest";
                using var request = new HttpRequestMessage(HttpMethod.Get, latestReleaseApi);
                request.Headers.UserAgent.ParseAdd("SteamPluginManager/2.1.9");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"[Steamtools] GitHub release check failed: {response.StatusCode}");
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var tagName = doc.RootElement.GetProperty("tag_name").GetString();
                var htmlUrl = doc.RootElement.GetProperty("html_url").GetString();

                if (!string.IsNullOrWhiteSpace(tagName))
                {
                    _steamtoolsLatestVersion = tagName?.Trim();
                    _steamtoolsLatestReleaseHtmlUrl = htmlUrl;
                    _steamtoolsUpdateAvailable = _isSteamtoolsInstalled && (_steamtoolsInstalledVersion == null || IsSteamtoolsVersionLess(_steamtoolsInstalledVersion, _steamtoolsLatestVersion));

                    Logger.Log($"[Steamtools] Installed version: {_steamtoolsInstalledVersion}");
                    Logger.Log($"[Steamtools] Latest release version: {_steamtoolsLatestVersion}");
                    Logger.Log($"[Steamtools] Update available: {_steamtoolsUpdateAvailable}");

                    Dispatcher.Invoke(() => UpdateSteamtoolsUi(_isSteamtoolsInstalled));
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Steamtools] Failed to check updates: {ex.Message}");
            }
        }

        private string? GetInstalledSteamtoolsVersion()
        {
            try
            {
                string? steamPath = null;
                using (var regKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (regKey != null)
                    {
                        var steamPathValue = regKey.GetValue("SteamPath");
                        if (steamPathValue != null)
                            steamPath = steamPathValue.ToString();
                    }
                }

                if (string.IsNullOrWhiteSpace(steamPath))
                    return null;

                string openSteamToolPath = Path.Combine(steamPath, "OpenSteamTool.dll");
                if (!File.Exists(openSteamToolPath))
                    return null;

                try
                {
                    var ver = FileVersionInfo.GetVersionInfo(openSteamToolPath);
                    var version = ver.ProductVersion ?? ver.FileVersion;
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        try
                        {
                            var assemblyName = AssemblyName.GetAssemblyName(openSteamToolPath);
                            version = assemblyName?.Version?.ToString();
                        }
                        catch (Exception assemblyEx)
                        {
                            Logger.Log($"[Steamtools] Failed to read assembly version fallback: {assemblyEx.Message}");
                        }
                    }

                    if (string.IsNullOrWhiteSpace(version))
                        return null;

                    return version;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Steamtools] Failed to read DLL version: {ex.Message}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Steamtools] Error getting installed version: {ex.Message}");
                return null;
            }
        }

        private string? LoadSavedSteamtoolsInstalledVersion()
        {
            try
            {
                var filePath = GetSteamtoolsVersionFilePath();
                if (File.Exists(filePath))
                {
                    var version = File.ReadAllText(filePath).Trim();
                    return string.IsNullOrWhiteSpace(version) ? null : version;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Steamtools] Failed to load saved installed version: {ex.Message}");
            }

            return null;
        }

        private void SaveSteamtoolsInstalledVersion(string? version)
        {
            try
            {
                var filePath = GetSteamtoolsVersionFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                if (string.IsNullOrWhiteSpace(version))
                {
                    if (File.Exists(filePath))
                        File.Delete(filePath);
                }
                else
                {
                    File.WriteAllText(filePath, version.Trim());
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Steamtools] Failed to save installed version: {ex.Message}");
            }
        }

        private string GetSteamtoolsVersionFilePath()
        {
            var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamPluginManager");
            return Path.Combine(appDataPath, "steamtools_installed_version.txt");
        }

        private static string NormalizeSteamtoolsVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return string.Empty;

            version = version.Trim();
            if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                version = version.Substring(1);

            version = version.Trim();
            int suffixIndex = version.IndexOfAny(new[] { '+', '-' });
            if (suffixIndex >= 0)
                version = version.Substring(0, suffixIndex).Trim();

            return version.Trim();
        }

        private static bool IsSteamtoolsVersionLess(string? installedVersion, string? latestVersion)
        {
            var installed = NormalizeSteamtoolsVersion(installedVersion ?? string.Empty);
            var latest = NormalizeSteamtoolsVersion(latestVersion ?? string.Empty);

            if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
                return false;

            if (string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase))
                return false;

            if (Version.TryParse(installed, out var installedVer) && Version.TryParse(latest, out var latestVer))
                return installedVer < latestVer;

            // If parsing failed, compare normalized strings directly
            return CompareVersionComponents(installed, latest) < 0;
        }

        private static int CompareVersionComponents(string installed, string latest)
        {
            var installedParts = installed.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            var latestParts = latest.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

            var length = Math.Max(installedParts.Length, latestParts.Length);
            for (int i = 0; i < length; i++)
            {
                int installedPart = i < installedParts.Length && int.TryParse(installedParts[i], out var ip) ? ip : 0;
                int latestPart = i < latestParts.Length && int.TryParse(latestParts[i], out var lp) ? lp : 0;

                if (installedPart < latestPart)
                    return -1;
                if (installedPart > latestPart)
                    return 1;
            }

            return 0;
        }

        private void UpdateSteamtoolsUi(bool installed)
        {
            try
            {
                _isSteamtoolsInstalled = installed;
                if (installed)
                {
                    // Show installed version if available
                    if (string.IsNullOrWhiteSpace(_steamtoolsInstalledVersion))
                    {
                        _steamtoolsInstalledVersion = GetInstalledSteamtoolsVersion();
                        if (!string.IsNullOrWhiteSpace(_steamtoolsInstalledVersion))
                        {
                            SaveSteamtoolsInstalledVersion(_steamtoolsInstalledVersion);
                            Logger.Log($"[Steamtools] Loaded installed version during UI refresh: {_steamtoolsInstalledVersion}");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(_steamtoolsInstalledVersion))
                    {
                        var displayVer = _steamtoolsInstalledVersion!.Trim();
                        displayVer = displayVer.TrimStart('v', 'V').Trim();
                        displayVer = $"v{displayVer}";
                        SteamtoolsStatusText.Text = $"Installed ( {displayVer} )";
                    }
                    else
                    {
                        SteamtoolsStatusText.Text = "Installed";
                    }
                    SteamtoolsActionButtonText.Text = "UNINSTALL";
                    var delIcon = TryFindResource("Icon.Delete");
                    if (delIcon != null)
                        SteamtoolsActionButton.Tag = delIcon;

                    // If installed and update available, show Update button
                    if (_steamtoolsUpdateAvailable)
                    {
                        SteamtoolsUpdateButton.Visibility = Visibility.Visible;
                        SteamtoolsUpdateButtonText.Text = $"UPDATE ({_steamtoolsLatestVersion})";
                    }
                    else
                    {
                        SteamtoolsUpdateButton.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    SteamtoolsStatusText.Text = "Not installed";
                    SteamtoolsActionButtonText.Text = "INSTALL";
                    var plusIcon = TryFindResource("Icon.Plus");
                    if (plusIcon != null)
                        SteamtoolsActionButton.Tag = plusIcon;

                    // Hide update button when not installed
                    SteamtoolsUpdateButton.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Steamtools] Failed to update UI: {ex.Message}");
            }
        }

        private async void SteamtoolsActionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isSteamtoolsInstalled)
                {
                    // Uninstall: restore Steam to original state
                    var dlg = new StyledMessageDialog("⚠️ Uninstall Steamtools", "Uninstall Steamtools?\n\nThis will remove the modification and restore Steam to original state.\n\nNote: Steam will be restarted.", showCancel: true);
                    dlg.Owner = Window.GetWindow(this);
                    dlg.PrimaryButton.Content = "UNINSTALL";
                    dlg.SecondaryButton.Content = "CANCEL";
                    var res = dlg.ShowDialog();
                    if (res == true)
                    {
                        try
                        {
                            _overlayWindow = new SteamtoolsOverlayWindow();
                            _overlayWindow.Owner = Window.GetWindow(this);
                            _overlayWindow.SetTitle("Uninstalling Steamtools...");
                            _overlayWindow.SetStatus("Starting...");
                            
                            // Disable owner window and show overlay non-modally to allow async operations
                            var ownerWindow = Window.GetWindow(this);
                            if (ownerWindow != null) ownerWindow.IsEnabled = false;
                            _overlayWindow.Show();
                            _overlayWindow.Closed += (s, ev) =>
                            {
                                if (ownerWindow != null) ownerWindow.IsEnabled = true;
                            };

                            // Get Steam path from registry
                            string? steamPath = null;
                            try
                            {
                                using (var regKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                                {
                                    if (regKey != null)
                                    {
                                        var steamPathValue = regKey.GetValue("SteamPath");
                                        if (steamPathValue != null)
                                            steamPath = steamPathValue.ToString();
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[Steamtools] Failed to read Steam path from registry: {ex.Message}");
                            }

                            // Kill Steam process if running
                            try
                            {
                                _overlayWindow?.SetStepInProgress(1);
                                _overlayWindow?.SetStatus("Stopping Steam...");
                                var steamProcs = Process.GetProcessesByName("steam");
                                foreach (var proc in steamProcs)
                                {
                                    try
                                    {
                                        proc.Kill();
                                        proc.WaitForExit(5000);
                                    }
                                    catch { }
                                }
                                await Task.Delay(1000);
                                _overlayWindow?.SetStepComplete(1);
                                _overlayWindow?.SetStatus("Steam stopped");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[Steamtools] Error stopping Steam: {ex.Message}");
                                _overlayWindow?.SetStatus("Failed to stop Steam (continuing)");
                            }

                            // Remove injected DLLs from Steam folder
                            if (!string.IsNullOrEmpty(steamPath))
                            {
                                try
                                {
                                    _overlayWindow?.SetStepInProgress(2);
                                    _overlayWindow?.SetStatus("Removing injected DLLs...");
                                    
                                    // Remove xinput1_4.dll
                                    string xinputPath = Path.Combine(steamPath, "xinput1_4.dll");
                                    if (File.Exists(xinputPath))
                                    {
                                        File.Delete(xinputPath);
                                        Logger.Log($"[Steamtools] Removed {xinputPath}");
                                    }

                                    // Remove dwmapi.dll
                                    string dwapiPath = Path.Combine(steamPath, "dwmapi.dll");
                                    if (File.Exists(dwapiPath))
                                    {
                                        File.Delete(dwapiPath);
                                        Logger.Log($"[Steamtools] Removed {dwapiPath}");
                                    }

                                    // Remove OpenSteamTool.dll
                                    string openSteamToolPath = Path.Combine(steamPath, "OpenSteamTool.dll");
                                    if (File.Exists(openSteamToolPath))
                                    {
                                        File.Delete(openSteamToolPath);
                                        Logger.Log($"[Steamtools] Removed {openSteamToolPath}");
                                    }
                                    
                                    _overlayWindow?.SetStepComplete(2);
                                    _overlayWindow?.SetStatus("Injected DLLs removed");
                                }
                                catch (Exception ex)
                                {
                                    Logger.Log($"[Steamtools] Failed to remove DLLs: {ex.Message}");
                                    _overlayWindow?.SetStatus("Failed to remove DLLs (continuing)");
                                }
                            }

                            // Remove registry entries
                            try
                            {
                                _overlayWindow?.SetStepInProgress(3);
                                _overlayWindow?.SetStatus("Cleaning registry...");
                                
                                // Remove entire Steamtools registry key
                                try
                                {
                                    Registry.CurrentUser.DeleteSubKey(@"Software\Valve\Steamtools", false);
                                    Logger.Log("[Steamtools] Removed entire Steamtools registry key");
                                    _overlayWindow?.SetStepComplete(3);
                                    _overlayWindow?.SetStatus("Registry cleaned");
                                }
                                catch
                                {
                                    // If key doesn't exist, just remove iscdkey property if it exists
                                    using (var regKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steamtools", true))
                                    {
                                        if (regKey != null)
                                        {
                                            regKey.DeleteValue("iscdkey", false);
                                            regKey.DeleteValue("ActivateUnlockMode", false);
                                            regKey.DeleteValue("AlwaysStayUnlocked", false);
                                            regKey.DeleteValue("notUnlockDepot", false);
                                            Logger.Log("[Steamtools] Removed Steamtools registry properties");
                                            _overlayWindow?.SetStepComplete(3);
                                            _overlayWindow?.SetStatus("Registry properties removed");
                                        }
                                        else
                                        {
                                            _overlayWindow?.SetStepComplete(3);
                                            _overlayWindow?.SetStatus("No Steamtools registry found");
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[Steamtools] Failed to clean registry: {ex.Message}");
                                _overlayWindow?.SetStatus("Failed to clean registry (continuing)");
                            }

                            // Restart Steam
                            try
                            {
                                _overlayWindow?.SetStepInProgress(4);
                                _overlayWindow?.SetStatus("Restarting Steam...");
                                bool restarted = RestartSteamProcess(steamPath);
                                if (restarted)
                                {
                                    _overlayWindow?.SetStepComplete(4);
                                    _overlayWindow?.SetStatus("Steam restarted");
                                }
                                else
                                {
                                    _overlayWindow?.SetStatus("Failed to restart Steam");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[Steamtools] Failed to restart Steam: {ex.Message}");
                                _overlayWindow?.SetStatus("Failed to restart Steam");
                            }

                            // show result in overlay and auto-hide
                            _overlayWindow?.ShowResult("Steamtools uninstalled. Steam has been restored to original state.", true);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[Steamtools] Uninstall error: {ex.Message}");
                            SteamtoolsOverlayGrid.Visibility = Visibility.Collapsed;
                            var errorMsg = new StyledMessageDialog("❌ Error", $"Uninstall error: {ex.Message}");
                            errorMsg.Owner = Window.GetWindow(this);
                            errorMsg.ShowDialog();
                        }
                        finally
                        {
                            // Refresh status
                            var installed = CheckSteamtoolsInstalled();
                            if (!installed)
                            {
                                SaveSteamtoolsInstalledVersion(null);
                                _steamtoolsInstalledVersion = null;
                                Logger.Log("[Steamtools] Cleared saved installed version after uninstall");
                            }
                            UpdateSteamtoolsUi(installed);
                        }
                    }
                }
                else
                {
                    // Install flow: set registry, download latest GitHub release, extract ZIP, copy DLLs, setup config\lua, restart Steam
                    string? releaseTag = null;
                    try
                    {
                        _overlayWindow = new SteamtoolsOverlayWindow();
                        _overlayWindow.Owner = Window.GetWindow(this);
                        _overlayWindow.SetTitle("Installing Steamtools...");
                        _overlayWindow.SetStatus("Initializing...");
                        _overlayWindow.ResetSteps();
                        _overlayWindow.SetStepText(1, "Adding registry...");
                        _overlayWindow.SetStepText(2, "Downloading latest release...");
                        _overlayWindow.SetStepText(3, "Extracting release...");
                        _overlayWindow.SetStepText(4, "Copying DLLs...");
                        _overlayWindow.SetStepText(5, "Preparing config\\lua...");
                        _overlayWindow.SetStepText(6, "Restarting Steam...");

                        // Disable owner window and show overlay non-modally to allow async operations
                        var ownerWindow = Window.GetWindow(this);
                        if (ownerWindow != null) ownerWindow.IsEnabled = false;
                        _overlayWindow.Show();
                        _overlayWindow.Closed += (s, ev) =>
                        {
                            if (ownerWindow != null) ownerWindow.IsEnabled = true;
                        };

                        string? steamPath = null;
                        try
                        {
                            using (var regKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                            {
                                if (regKey != null)
                                {
                                    var steamPathValue = regKey.GetValue("SteamPath");
                                    if (steamPathValue != null)
                                        steamPath = steamPathValue.ToString();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[Steamtools] Failed to read Steam path from registry: {ex.Message}");
                            throw;
                        }

                        if (string.IsNullOrWhiteSpace(steamPath))
                        {
                            throw new InvalidOperationException("Steam install path not found in HKCU\\Software\\Valve\\Steam.");
                        }

                        _overlayWindow?.SetStepInProgress(1);
                        _overlayWindow?.SetStatus("Adding Steamtools registry...");
                        using (var regKey = Registry.CurrentUser.CreateSubKey(@"Software\Valve\Steamtools"))
                        {
                            regKey?.SetValue("iscdkey", "true", RegistryValueKind.String);
                        }
                        _overlayWindow?.SetStepComplete(1);
                        Logger.Log("[Steamtools] Added HKCU\\Software\\Valve\\Steamtools\\iscdkey=true");

                        var client = SharedHttpClient.Instance;
                        const string latestReleaseApi = "https://api.github.com/repos/OpenSteam001/OpenSteamTool/releases/latest";

                        _overlayWindow?.SetStepInProgress(2);
                        _overlayWindow?.SetStatus("Downloading latest release...");
                        using var request = new HttpRequestMessage(HttpMethod.Get, latestReleaseApi);
                        request.Headers.UserAgent.ParseAdd("SteamPluginManager/2.1.9");
                        request.Headers.Accept.ParseAdd("application/vnd.github+json");

                        using var releaseResponse = await client.SendAsync(request);
                        releaseResponse.EnsureSuccessStatusCode();

                        var releaseJson = await releaseResponse.Content.ReadAsStringAsync();
                        using var releaseDoc = JsonDocument.Parse(releaseJson);

                        try { releaseTag = releaseDoc.RootElement.GetProperty("tag_name").GetString(); } catch { }

                        string? assetUrl = null;
                        string? assetName = null;
                        foreach (var asset in releaseDoc.RootElement.GetProperty("assets").EnumerateArray())
                        {
                            var currentName = asset.GetProperty("name").GetString();
                            if (!string.IsNullOrWhiteSpace(currentName) &&
                                currentName.Contains("Release", StringComparison.OrdinalIgnoreCase) &&
                                currentName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            {
                                assetUrl = asset.GetProperty("browser_download_url").GetString();
                                assetName = currentName;
                                break;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(assetUrl) || string.IsNullOrWhiteSpace(assetName))
                        {
                            throw new InvalidOperationException("No matching ZIP release asset found on GitHub.");
                        }

                        string tempZipPath = Path.Combine(Path.GetTempPath(), $"OpenSteamTool_{Guid.NewGuid():N}.zip");
                        string extractDir = Path.Combine(Path.GetTempPath(), $"OpenSteamTool_{Guid.NewGuid():N}");

                        try
                        {
                            _overlayWindow?.SetStatus($"Downloading {assetName}...");
                            using var downloadResponse = await client.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead);
                            downloadResponse.EnsureSuccessStatusCode();

                            using (var remoteStream = await downloadResponse.Content.ReadAsStreamAsync())
                            using (var localStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                await remoteStream.CopyToAsync(localStream);
                            }

                            _overlayWindow?.SetStepComplete(2);
                            _overlayWindow?.SetStepInProgress(3);
                            _overlayWindow?.SetStatus("Extracting release...");
                            Directory.CreateDirectory(extractDir);
                            ZipFile.ExtractToDirectory(tempZipPath, extractDir);

                            _overlayWindow?.SetStepComplete(3);
                            _overlayWindow?.SetStepInProgress(4);
                            _overlayWindow?.SetStatus("Copying DLLs...");

                            string[] targetFiles = ["OpenSteamTool.dll", "dwmapi.dll", "xinput1_4.dll"];
                            foreach (var targetFile in targetFiles)
                            {
                                var sourceFile = Directory.GetFiles(extractDir, targetFile, SearchOption.AllDirectories).FirstOrDefault();
                                if (string.IsNullOrWhiteSpace(sourceFile))
                                {
                                    Logger.Log($"[Steamtools] Missing file in extracted release: {targetFile}");
                                    continue;
                                }

                                string targetPath = Path.Combine(steamPath, targetFile);
                                File.Copy(sourceFile, targetPath, true);
                                Logger.Log($"[Steamtools] Copied {sourceFile} -> {targetPath}");
                            }

                            _overlayWindow?.SetStepComplete(4);
                            _overlayWindow?.SetStepInProgress(5);
                            _overlayWindow?.SetStatus("Preparing config\\lua...");

                            string configPath = Path.Combine(steamPath, "config");
                            string luaPath = Path.Combine(configPath, "lua");
                            Directory.CreateDirectory(luaPath);

                            string stplugInPath = Path.Combine(configPath, "stplug-in");
                            if (Directory.Exists(stplugInPath))
                            {
                                CopyDirectoryContents(stplugInPath, luaPath);
                                Logger.Log($"[Steamtools] Copied contents from {stplugInPath} to {luaPath}");
                            }
                            else
                            {
                                Logger.Log($"[Steamtools] No {stplugInPath} folder found, skipping copy");
                            }

                            _overlayWindow?.SetStepComplete(5);
                            _overlayWindow?.SetStepInProgress(6);
                            _overlayWindow?.SetStatus("Restarting Steam...");

                            bool restarted = RestartSteamProcess(steamPath);
                            if (restarted)
                            {
                                Logger.Log("[Steamtools] Restarted Steam after installation");
                            }
                            else
                            {
                                Logger.Log("[Steamtools] Failed to restart Steam after installation");
                            }

                            _overlayWindow?.SetStepComplete(6);
                            _overlayWindow?.ShowResult("Steamtools installed successfully!", true);

                            // If DLL metadata didn't provide a version, fall back to the release tag we downloaded
                            try
                            {
                                if (string.IsNullOrWhiteSpace(_steamtoolsInstalledVersion) && !string.IsNullOrWhiteSpace(releaseTag))
                                {
                                    _steamtoolsInstalledVersion = releaseTag;
                                    SaveSteamtoolsInstalledVersion(_steamtoolsInstalledVersion);
                                    Logger.Log($"[Steamtools] Recorded installed version from release tag: {_steamtoolsInstalledVersion}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[Steamtools] Failed to record installed version from release tag: {ex.Message}");
                            }
                        }
                        finally
                        {
                            if (File.Exists(tempZipPath))
                                File.Delete(tempZipPath);
                            if (Directory.Exists(extractDir))
                                Directory.Delete(extractDir, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[Steamtools] Failed to install Steamtools: {ex.Message}");
                        _overlayWindow?.ShowResult($"Failed to install Steamtools: {ex.Message}", false);
                    }
                    finally
                    {
                        var installed = CheckSteamtoolsInstalled();
                        if (installed)
                        {
                            var detectedVersion = GetInstalledSteamtoolsVersion();
                            if (!string.IsNullOrWhiteSpace(detectedVersion))
                            {
                                _steamtoolsInstalledVersion = detectedVersion;
                            }
                            else if (string.IsNullOrWhiteSpace(_steamtoolsInstalledVersion) && !string.IsNullOrWhiteSpace(releaseTag))
                            {
                                _steamtoolsInstalledVersion = releaseTag;
                            }

                            SaveSteamtoolsInstalledVersion(_steamtoolsInstalledVersion);
                            Logger.Log($"[Steamtools] Saved installed version after install: {_steamtoolsInstalledVersion}");
                        }
                        UpdateSteamtoolsUi(installed);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Steamtools] Action button error: {ex.Message}");
            }
        }

        private void CopyDirectoryContents(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destPath = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, destPath, true);
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                string childTargetDir = Path.Combine(targetDir, Path.GetFileName(subDir));
                CopyDirectoryContents(subDir, childTargetDir);
            }
        }

        private bool RestartSteamProcess(string? steamPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(steamPath))
                {
                    Logger.Log("[Steamtools] Steam path is empty, cannot restart Steam.");
                    return false;
                }

                string steamExe = Path.Combine(steamPath, "steam.exe");
                if (!File.Exists(steamExe))
                {
                    Logger.Log($"[Steamtools] Steam executable not found: {steamExe}");
                    return false;
                }

                foreach (var proc in Process.GetProcessesByName("steam"))
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[Steamtools] Failed to stop existing Steam process: {ex.Message}");
                    }
                }

                var startInfo = new ProcessStartInfo(steamExe)
                {
                    WorkingDirectory = steamPath,
                    UseShellExecute = true
                };

                Process.Start(startInfo);
                Logger.Log($"[Steamtools] Started Steam from {steamExe}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[Steamtools] Failed to restart Steam: {ex.Message}");
                try
                {
                    Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
                    Logger.Log("[Steamtools] Fallback restart triggered via steam://open/main");
                    return true;
                }
                catch (Exception fallbackEx)
                {
                    Logger.Log($"[Steamtools] Fallback Steam restart failed: {fallbackEx.Message}");
                    return false;
                }
            }
        }

        private void LoadThemePreference()
        {
            try
            {
                if (ThemeSelector == null) return;
                
                // First detach any existing handler
                ThemeSelector.SelectionChanged -= ThemeSelector_SelectionChanged;
                
                // Get saved theme preference
                var savedTheme = ThemePreferences.GetSavedThemePreference();
                
                var themeNames = ThemePreferences.AvailableThemes;
                var selectedTheme = savedTheme ?? "Dark";
                var selectedIndex = Array.IndexOf(themeNames, selectedTheme);
                ThemeSelector.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                Logger.Log($"[LoadThemePreference] Set to {themeNames[ThemeSelector.SelectedIndex]} theme");
                
                // Re-attach event handler AFTER setting initial value
                ThemeSelector.SelectionChanged += ThemeSelector_SelectionChanged;
                Logger.Log($"[LoadThemePreference] Event handler re-attached");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load theme preference: {ex.Message}");
            }
        }

        private void LoadLanguagePreference()
        {
            try
            {
                if (LangSelector == null) return;
                
                // First detach any existing handler
                LangSelector.SelectionChanged -= LangSelector_SelectionChanged;
                
                // Check saved language preference first
                string savedLanguage = null;
                try
                {
                    string appDataPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SteamPluginManager"
                    );
                    string langFile = Path.Combine(appDataPath, "language.txt");
                    if (File.Exists(langFile))
                    {
                        savedLanguage = File.ReadAllText(langFile).Trim();
                        Logger.Log($"[LoadLanguagePreference] Loaded saved language: {savedLanguage}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[LoadLanguagePreference] Failed to read saved language: {ex.Message}");
                }
                
                // Use saved language if available, otherwise use system culture
                string languageToUse = savedLanguage ?? System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
                Logger.Log($"[LoadLanguagePreference] Language to use: {languageToUse}");
                
                // Select based on language code
                if (languageToUse == "zh")
                {
                    LangSelector.SelectedIndex = 1; // Chinese
                    Logger.Log($"[LoadLanguagePreference] Set to Chinese (index 1)");
                }
                else
                {
                    LangSelector.SelectedIndex = 0; // English (default)
                    Logger.Log($"[LoadLanguagePreference] Set to English (index 0)");
                }
                
                // Re-attach event handler AFTER setting initial value
                LangSelector.SelectionChanged += LangSelector_SelectionChanged;
                Logger.Log($"[LoadLanguagePreference] Event handler re-attached");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load language preference: {ex.Message}");
            }
        }

        private void LangSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                Logger.Log($"[LangSelector] Selection changed event fired");
                Logger.Log($"[LangSelector] SelectedIndex: {LangSelector.SelectedIndex}");
                
                if (LangSelector.SelectedIndex >= 0)
                {
                    string language = LangSelector.SelectedIndex == 0 ? "en" : "zh";
                    Logger.Log($"[LangSelector] Selected language code: {language}");
                    ApplyLanguage(language);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in LangSelector_SelectionChanged: {ex.Message}");
            }
        }

        private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                Logger.Log($"[ThemeSelector] Selection changed event fired");
                Logger.Log($"[ThemeSelector] SelectedIndex: {ThemeSelector.SelectedIndex}");
                
                if (ThemeSelector.SelectedIndex >= 0)
                {
                    var themeNames = ThemePreferences.AvailableThemes;
                    var themeName = themeNames[ThemeSelector.SelectedIndex];
                    Logger.Log($"[ThemeSelector] Selected theme: {themeName}");
                    ApplyTheme(themeName);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in ThemeSelector_SelectionChanged: {ex.Message}");
            }
        }

        private void ApplyLanguage(string languageCode)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;

                Logger.Log($"[Language Switch] Starting to apply language: {languageCode}");
                Logger.Log($"[Language Switch] Current merged dictionaries count: {app.Resources.MergedDictionaries.Count}");

                // Find and remove existing language resource
                ResourceDictionary? existingLangDict = null;
                foreach (var dict in app.Resources.MergedDictionaries)
                {
                    Logger.Log($"[Language Switch] Checking dict: {dict.Source?.OriginalString}");
                    if (dict.Source?.OriginalString.Contains("Strings/") == true)
                    {
                        existingLangDict = dict;
                        Logger.Log($"[Language Switch] Found existing language dict: {dict.Source.OriginalString}");
                        break;
                    }
                }

                if (existingLangDict != null)
                {
                    app.Resources.MergedDictionaries.Remove(existingLangDict);
                    Logger.Log($"[Language Switch] Removed old language dict");
                }

                // Load and add new language resource
                string langFileName = languageCode == "en" ? "Strings/en.xaml" : "Strings/zh.xaml";
                Logger.Log($"[Language Switch] Loading new language file: {langFileName}");
                
                ResourceDictionary newDict = new ResourceDictionary { Source = new Uri(langFileName, UriKind.Relative) };
                app.Resources.MergedDictionaries.Add(newDict);
                
                Logger.Log($"[Language Switch] Added new language dict. New count: {app.Resources.MergedDictionaries.Count}");

                // Save language preference
                try
                {
                    string appDataPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SteamPluginManager"
                    );
                    Directory.CreateDirectory(appDataPath);
                    File.WriteAllText(Path.Combine(appDataPath, "language.txt"), languageCode);
                    Logger.Log($"[Language Switch] Saved language preference: {languageCode}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to save language preference: {ex.Message}");
                }

                Logger.Log($"Language changed to: {(languageCode == "en" ? "English" : "中文")}");
                
                // Raise event to notify all views about language change
                Logger.Log("[Language Switch] Raising LanguageChanged event");
                App.RaiseLanguageChanged();
                Logger.Log("[Language Switch] LanguageChanged event raised");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to apply language: {ex.Message}");
                MessageBox.Show($"Failed to apply language: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyTheme(string themeName)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;

                Logger.Log($"[Theme Switch] Starting to apply theme: {themeName}");
                Logger.Log($"[Theme Switch] Current merged dictionaries count: {app.Resources.MergedDictionaries.Count}");

                // Find and remove existing theme resource
                ResourceDictionary? existingThemeDict = null;
                foreach (var dict in app.Resources.MergedDictionaries)
                {
                    Logger.Log($"[Theme Switch] Checking dict: {dict.Source?.OriginalString}");
                    if (dict.Source?.OriginalString.Contains("Themes/") == true)
                    {
                        existingThemeDict = dict;
                        Logger.Log($"[Theme Switch] Found existing theme dict: {dict.Source.OriginalString}");
                        break;
                    }
                }

                if (existingThemeDict != null)
                {
                    app.Resources.MergedDictionaries.Remove(existingThemeDict);
                    Logger.Log($"[Theme Switch] Removed old theme dict");
                }

                // Load and add new theme resource
                string themeFileName = $"Themes/{themeName}.xaml";
                Logger.Log($"[Theme Switch] Loading new theme file: {themeFileName}");
                
                ResourceDictionary newDict = new ResourceDictionary { Source = new Uri(themeFileName, UriKind.Relative) };
                app.Resources.MergedDictionaries.Add(newDict);
                App.SetThemeIcon(themeName);
                App.RaiseThemeChanged();
                
                Logger.Log($"[Theme Switch] Added new theme dict. New count: {app.Resources.MergedDictionaries.Count}");

                // Save theme preference
                ThemePreferences.SaveThemePreference(themeName);
                Logger.Log($"[Theme Switch] Saved theme preference: {themeName}");
                
                Logger.Log($"Theme changed to: {themeName}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to apply theme: {ex.Message}");
                MessageBox.Show($"Failed to apply theme: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadHardwareAccelerationSetting()
        {
            try
            {
                var savedPreference = HardwareAccelerationPreferences.GetSavedHardwareAccelerationPreference();
                // Default to true (enabled) if no preference is saved
                HardwareAccelerationCheckBox.IsChecked = savedPreference ?? true;
                
                // Add event handler after loading to prevent firing on initial load
                HardwareAccelerationCheckBox.Checked += HardwareAccelerationCheckBox_Changed;
                HardwareAccelerationCheckBox.Unchecked += HardwareAccelerationCheckBox_Changed;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load hardware acceleration setting: {ex.Message}");
            }
        }

        private void LoadAutoUpdateSetting()
        {
            try
            {
                _isLoadingSettings = true;
                
                // Load saved auto-update preference from file
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SteamPluginManager"
                );
                string autoUpdateFile = Path.Combine(appDataPath, "autoupdate.txt");
                
                bool autoUpdateEnabled = true; // Default to true
                if (File.Exists(autoUpdateFile))
                {
                    string content = File.ReadAllText(autoUpdateFile).Trim().ToLower();
                    autoUpdateEnabled = (content == "true" || content == "1");
                }
                
                AutoUpdateCheckBox.IsChecked = autoUpdateEnabled;
                Logger.Log($"[LoadAutoUpdateSetting] Auto-update setting loaded: {autoUpdateEnabled}");
                
                _isLoadingSettings = false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load auto-update setting: {ex.Message}");
                AutoUpdateCheckBox.IsChecked = true; // Default to true on error
                _isLoadingSettings = false;
            }
        }

        private void HardwareAccelerationCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                bool isEnabled = HardwareAccelerationCheckBox.IsChecked ?? true;
                HardwareAccelerationPreferences.SaveHardwareAccelerationPreference(isEnabled);
                
                MessageBox.Show(
                    "Hardware acceleration setting saved.\nPlease restart the application for changes to take effect.",
                    "Settings Saved",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save hardware acceleration setting: {ex.Message}");
                MessageBox.Show($"Failed to save setting: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AutoUpdateCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // Don't show message when initially loading settings
            if (_isLoadingSettings)
                return;
                
            try
            {
                bool isEnabled = AutoUpdateCheckBox.IsChecked ?? true;
                
                // Save auto-update preference to file
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SteamPluginManager"
                );
                Directory.CreateDirectory(appDataPath);
                
                string autoUpdateFile = Path.Combine(appDataPath, "autoupdate.txt");
                File.WriteAllText(autoUpdateFile, isEnabled.ToString());
                
                Logger.Log($"[AutoUpdateCheckBox_Changed] Auto-update setting saved: {isEnabled}");
                MessageBox.Show(
                    isEnabled ? "Auto-update enabled. The app will check for updates automatically." 
                              : "Auto-update disabled. You can still check for updates manually.",
                    "Settings Saved",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save auto-update setting: {ex.Message}");
                MessageBox.Show($"Failed to save setting: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateCacheSize()
        {
            try
            {
                // Gunakan path yang sama dengan CacheManager (AppData/Local)
                string cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SteamPluginManager",
                    "Cache"
                );

                long totalBytes = 0;
                if (Directory.Exists(cacheDir))
                {
                    var dir = new DirectoryInfo(cacheDir);
                    foreach (var file in dir.GetFiles("*", System.IO.SearchOption.AllDirectories))
                    {
                        try
                        {
                            totalBytes += file.Length;
                        }
                        catch { }
                    }
                }

                // Format cache size
                string sizeText = FormatBytes(totalBytes);
                CacheSizeText.Text = $"{sizeText} of Cache";
            }
            catch (Exception)
            {
                CacheSizeText.Text = "Unable to determine cache size";
            }
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show(
                    "Clear unused/expired cache?\nThis will remove cached game data that is expired (365+ days old) or incomplete.\nValid cached items will be preserved.",
                    "Clear Cache",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result == MessageBoxResult.Yes)
                {
                    // Use CacheManager.ClearCache() to handle cache clearing properly
                    CacheManager.ClearCache();
                    
                    // Show result
                    int cacheSize = CacheManager.GetCacheSize();
                    MessageBox.Show(
                        $"Cache cleared successfully.\nRemaining cache items: {cacheSize}",
                        "Cache Cleared",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error clearing cache: {ex.Message}");
                MessageBox.Show(
                    $"Error clearing cache: {ex.Message}",
                    "Cache Clear Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        

        private void BackToDashboard_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.NavigateToDashboard();
        }

        private async Task PerformSteamtoolsInstallAsync(string title, bool copyStplugInToLua)
        {
            try
            {
                _overlayWindow = new SteamtoolsOverlayWindow();
                _overlayWindow.Owner = Window.GetWindow(this);
                _overlayWindow.SetTitle(title);
                _overlayWindow.SetStatus("Initializing...");
                _overlayWindow.ResetSteps();
                _overlayWindow.SetStepText(1, "Adding registry...");
                _overlayWindow.SetStepText(2, "Downloading latest release...");
                _overlayWindow.SetStepText(3, "Extracting release...");
                _overlayWindow.SetStepText(4, "Copying DLLs...");
                _overlayWindow.SetStepText(5, "Preparing config\\lua...");
                _overlayWindow.SetStepText(6, "Restarting Steam...");

                var ownerWindow = Window.GetWindow(this);
                if (ownerWindow != null) ownerWindow.IsEnabled = false;
                _overlayWindow.Show();
                _overlayWindow.Closed += (s, ev) =>
                {
                    if (ownerWindow != null) ownerWindow.IsEnabled = true;
                };

                string? steamPath = null;
                string? releaseTag = null;
                try
                {
                    using (var regKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    {
                        if (regKey != null)
                        {
                            var steamPathValue = regKey.GetValue("SteamPath");
                            if (steamPathValue != null)
                                steamPath = steamPathValue.ToString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Steamtools] Failed to read Steam path from registry: {ex.Message}");
                    throw;
                }

                if (string.IsNullOrWhiteSpace(steamPath))
                {
                    throw new InvalidOperationException("Steam install path not found in HKCU\\Software\\Valve\\Steam.");
                }

                _overlayWindow?.SetStepInProgress(1);
                _overlayWindow?.SetStatus("Adding Steamtools registry...");
                using (var regKey = Registry.CurrentUser.CreateSubKey(@"Software\Valve\Steamtools"))
                {
                    regKey?.SetValue("iscdkey", "true", RegistryValueKind.String);
                }
                _overlayWindow?.SetStepComplete(1);
                Logger.Log("[Steamtools] Added HKCU\\Software\\Valve\\Steamtools\\iscdkey=true");

                var client = SharedHttpClient.Instance;
                const string latestReleaseApi = "https://api.github.com/repos/OpenSteam001/OpenSteamTool/releases/latest";

                _overlayWindow?.SetStepInProgress(2);
                _overlayWindow?.SetStatus("Downloading latest release...");
                using var request = new HttpRequestMessage(HttpMethod.Get, latestReleaseApi);
                request.Headers.UserAgent.ParseAdd("SteamPluginManager/2.1.9");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var releaseResponse = await client.SendAsync(request);
                releaseResponse.EnsureSuccessStatusCode();

                var releaseJson = await releaseResponse.Content.ReadAsStringAsync();
                using var releaseDoc = JsonDocument.Parse(releaseJson);
                try { releaseTag = releaseDoc.RootElement.GetProperty("tag_name").GetString(); } catch { }

                string? assetUrl = null;
                string? assetName = null;
                foreach (var asset in releaseDoc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    var currentName = asset.GetProperty("name").GetString();
                    if (!string.IsNullOrWhiteSpace(currentName) &&
                        currentName.Contains("Release", StringComparison.OrdinalIgnoreCase) &&
                        currentName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        assetUrl = asset.GetProperty("browser_download_url").GetString();
                        assetName = currentName;
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(assetUrl) || string.IsNullOrWhiteSpace(assetName))
                {
                    throw new InvalidOperationException("No matching ZIP release asset found on GitHub.");
                }

                string tempZipPath = Path.Combine(Path.GetTempPath(), $"OpenSteamTool_{Guid.NewGuid():N}.zip");
                string extractDir = Path.Combine(Path.GetTempPath(), $"OpenSteamTool_{Guid.NewGuid():N}");

                try
                {
                    _overlayWindow?.SetStatus($"Downloading {assetName}...");
                    using var downloadResponse = await client.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead);
                    downloadResponse.EnsureSuccessStatusCode();

                    using (var remoteStream = await downloadResponse.Content.ReadAsStreamAsync())
                    using (var localStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await remoteStream.CopyToAsync(localStream);
                    }

                    _overlayWindow?.SetStepComplete(2);
                    _overlayWindow?.SetStepInProgress(3);
                    _overlayWindow?.SetStatus("Extracting release...");
                    Directory.CreateDirectory(extractDir);
                    ZipFile.ExtractToDirectory(tempZipPath, extractDir);

                    _overlayWindow?.SetStepComplete(3);
                    _overlayWindow?.SetStatus("Stopping Steam...");
                    foreach (var proc in Process.GetProcessesByName("steam"))
                    {
                        try
                        {
                            proc.Kill();
                            proc.WaitForExit(5000);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[Steamtools] Failed to stop existing Steam process: {ex.Message}");
                        }
                    }

                    _overlayWindow?.SetStepInProgress(4);
                    _overlayWindow?.SetStatus("Copying DLLs...");

                    string[] targetFiles = ["OpenSteamTool.dll", "dwmapi.dll", "xinput1_4.dll"];
                    foreach (var targetFile in targetFiles)
                    {
                        var sourceFile = Directory.GetFiles(extractDir, targetFile, SearchOption.AllDirectories).FirstOrDefault();
                        if (string.IsNullOrWhiteSpace(sourceFile))
                        {
                            Logger.Log($"[Steamtools] Missing file in extracted release: {targetFile}");
                            continue;
                        }

                        string targetPath = Path.Combine(steamPath, targetFile);
                        File.Copy(sourceFile, targetPath, true);
                        Logger.Log($"[Steamtools] Copied {sourceFile} -> {targetPath}");
                    }

                    _overlayWindow?.SetStepComplete(4);
                    _overlayWindow?.SetStepInProgress(5);
                    _overlayWindow?.SetStatus("Preparing config\\lua...");

                    string configPath = Path.Combine(steamPath, "config");
                    string luaPath = Path.Combine(configPath, "lua");
                    Directory.CreateDirectory(luaPath);

                    if (copyStplugInToLua)
                    {
                        string stplugInPath = Path.Combine(configPath, "stplug-in");
                        if (Directory.Exists(stplugInPath))
                        {
                            CopyDirectoryContents(stplugInPath, luaPath);
                            Logger.Log($"[Steamtools] Copied contents from {stplugInPath} to {luaPath}");
                        }
                        else
                        {
                            Logger.Log($"[Steamtools] No {stplugInPath} folder found, skipping copy");
                        }
                    }
                    else
                    {
                        Logger.Log("[Steamtools] Skipping stplug-in copy for update flow");
                    }

                    _overlayWindow?.SetStepComplete(5);
                    _overlayWindow?.SetStepInProgress(6);
                    _overlayWindow?.SetStatus("Restarting Steam...");

                    bool restarted = RestartSteamProcess(steamPath);
                    if (restarted)
                    {
                        Logger.Log("[Steamtools] Restarted Steam after installation");
                    }
                    else
                    {
                        Logger.Log("[Steamtools] Failed to restart Steam after installation");
                    }

                    _overlayWindow?.SetStepComplete(6);
                    _overlayWindow?.ShowResult(copyStplugInToLua ? "Steamtools installed successfully!" : "Steamtools updated successfully!", true);

                    try
                    {
                        if (string.IsNullOrWhiteSpace(_steamtoolsInstalledVersion) && !string.IsNullOrWhiteSpace(releaseTag))
                        {
                            _steamtoolsInstalledVersion = releaseTag;
                            SaveSteamtoolsInstalledVersion(_steamtoolsInstalledVersion);
                            Logger.Log($"[Steamtools] Recorded installed version from release tag: {_steamtoolsInstalledVersion}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[Steamtools] Failed to record installed version from release tag: {ex.Message}");
                    }
                }
                finally
                {
                    if (File.Exists(tempZipPath))
                        File.Delete(tempZipPath);
                    if (Directory.Exists(extractDir))
                        Directory.Delete(extractDir, true);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Steamtools] Install/update flow failed: {ex.Message}");
                _overlayWindow?.ShowResult($"Failed to {(copyStplugInToLua ? "install" : "update")} Steamtools: {ex.Message}", false);
            }
            finally
            {
                var installed = CheckSteamtoolsInstalled();
                if (installed)
                {
                    var detectedVersion = GetInstalledSteamtoolsVersion();
                    if (!string.IsNullOrWhiteSpace(detectedVersion))
                    {
                        _steamtoolsInstalledVersion = detectedVersion;
                    }
                    SaveSteamtoolsInstalledVersion(_steamtoolsInstalledVersion);
                    Logger.Log($"[Steamtools] Saved installed version after {(copyStplugInToLua ? "install" : "update")}: {_steamtoolsInstalledVersion}");
                }
                UpdateSteamtoolsUi(installed);
            }
        }

        private async void SteamtoolsUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await PerformSteamtoolsInstallAsync("Updating Steamtools...", copyStplugInToLua: false);
            }
            catch (Exception ex)
            {
                Logger.Log($"[Steamtools] Update button error: {ex.Message}");
            }
        }

        private void LoadAllow18PlusSetting()
        {
            try
            {
                _isLoadingSettings = true;
                
                var savedPreference = Allow18PlusContentPreferences.GetSavedAllow18PlusContentPreference();
                // Default to false (disabled) if no preference is saved
                Allow18PlusCheckBox.IsChecked = savedPreference ?? false;
                
                // Event handlers are already defined in XAML (Checked and Unchecked attributes)
                // No need to subscribe again here
                
                Logger.Log($"[LoadAllow18PlusSetting] 18+ content setting loaded: {savedPreference ?? false}");
                
                _isLoadingSettings = false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load 18+ content setting: {ex.Message}");
                Allow18PlusCheckBox.IsChecked = false; // Default to false on error
                _isLoadingSettings = false;
            }
        }

        private void Allow18PlusCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // Don't show message when initially loading settings
            if (_isLoadingSettings)
                return;
                
            try
            {
                bool isEnabled = Allow18PlusCheckBox.IsChecked ?? false;
                Allow18PlusContentPreferences.SaveAllow18PlusContentPreference(isEnabled);
                
                MessageBox.Show(
                    isEnabled 
                        ? "18+ content filter is now enabled.\n\nManifest files with nudity and sexual content are now visible." 
                        : "18+ content filter is now disabled.\n\nManifest files with nudity and sexual content are hidden.",
                    "18+ Content Setting",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
                
                Logger.Log($"[Allow18PlusCheckBox_Changed] Setting changed to: {isEnabled}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save 18+ content setting: {ex.Message}");
                MessageBox.Show($"Failed to save setting: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void RefreshLanguageUI()
        {
            try
            {
                Logger.Log("[SettingsView] Refreshing language UI");
                LanguageHelper.UpdateAllRegisteredTextBlocks();
                Logger.Log("[SettingsView] Language UI refresh completed");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error refreshing language UI: {ex.Message}");
            }
        }
    }
}

