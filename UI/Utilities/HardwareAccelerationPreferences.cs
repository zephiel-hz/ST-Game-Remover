using Microsoft.Win32;
using System;

namespace SteamPluginManager
{
    public static class HardwareAccelerationPreferences
    {
        private const string RegistryKeyPath = @"Software\SteamPluginManager";
        private const string HardwareAccelerationValueName = "HardwareAcceleration";
        private static bool? _cachedPreference = null; // Cache to avoid registry reads

        public static void SaveHardwareAccelerationPreference(bool isEnabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                key?.SetValue(HardwareAccelerationValueName, isEnabled ? 1 : 0);
                _cachedPreference = isEnabled; // Update cache
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save hardware acceleration preference: {ex.Message}");
            }
        }

        public static bool? GetSavedHardwareAccelerationPreference()
        {
            // Return cached value if available to avoid repeated registry reads
            if (_cachedPreference.HasValue)
            {
                return _cachedPreference.Value;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                if (key?.GetValue(HardwareAccelerationValueName) is int value)
                {
                    _cachedPreference = value == 1;
                    return _cachedPreference.Value;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load hardware acceleration preference: {ex.Message}");
            }
            return null; // No saved preference, use default
        }

        public static bool IsHardwareAccelerationEnabled()
        {
            var pref = GetSavedHardwareAccelerationPreference();
            return pref ?? true; // Default to enabled
        }

        public static void ClearCache()
        {
            _cachedPreference = null;
        }

        public static void ClearHardwareAccelerationPreference()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                key?.DeleteValue(HardwareAccelerationValueName, false);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to clear hardware acceleration preference: {ex.Message}");
            }
        }
    }
}
