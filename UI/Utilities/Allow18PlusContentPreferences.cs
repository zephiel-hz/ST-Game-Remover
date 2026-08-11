using Microsoft.Win32;
using System;

namespace SteamPluginManager
{
    public static class Allow18PlusContentPreferences
    {
        private const string RegistryKeyPath = @"Software\SteamPluginManager";
        private const string Allow18PlusValueName = "Allow18PlusContent";
        private static bool? _cachedPreference = null; // Cache to avoid registry reads

        // Static event that fires when 18+ content preference changes
        public static event EventHandler<bool>? Allow18PlusContentChanged;

        public static void SaveAllow18PlusContentPreference(bool isEnabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                key?.SetValue(Allow18PlusValueName, isEnabled ? 1 : 0);
                _cachedPreference = isEnabled; // Update cache
                Logger.Log($"[Allow18PlusContentPreferences] Saved preference: {isEnabled}");
                
                // Raise event to notify subscribers (views) that setting changed
                Allow18PlusContentChanged?.Invoke(null, isEnabled);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save 18+ content preference: {ex.Message}");
            }
        }

        public static bool? GetSavedAllow18PlusContentPreference()
        {
            // Return cached value if available to avoid repeated registry reads
            if (_cachedPreference.HasValue)
            {
                return _cachedPreference.Value;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                if (key?.GetValue(Allow18PlusValueName) is int value)
                {
                    _cachedPreference = value == 1;
                    return _cachedPreference.Value;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load 18+ content preference: {ex.Message}");
            }
            return null; // No saved preference, return false as default (18+ content is OFF by default)
        }

        public static bool IsAllow18PlusContentEnabled()
        {
            var preference = GetSavedAllow18PlusContentPreference();
            return preference ?? false; // Default to false (disabled)
        }

        public static void ClearCache()
        {
            _cachedPreference = null;
        }
    }
}
