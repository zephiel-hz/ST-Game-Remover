using Microsoft.Win32;
using System;

namespace SteamPluginManager
{
    public static class ThemePreferences
    {
        private const string RegistryKeyPath = @"Software\SteamPluginManager";
        private const string ThemeValueName = "ThemePreference";

        public static void SaveThemePreference(bool isLightTheme)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                key?.SetValue(ThemeValueName, isLightTheme ? "Light" : "Dark");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save theme preference: {ex.Message}");
            }
        }

        public static bool? GetSavedThemePreference()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                if (key?.GetValue(ThemeValueName) is string themeValue)
                {
                    return themeValue == "Light";
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load theme preference: {ex.Message}");
            }
            return null; // No saved preference
        }

        public static void ClearThemePreference()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                key?.DeleteValue(ThemeValueName, false);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to clear theme preference: {ex.Message}");
            }
        }
    }
}




