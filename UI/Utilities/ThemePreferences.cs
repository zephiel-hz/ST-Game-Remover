using Microsoft.Win32;
using System;

namespace SteamPluginManager
{
    public static class ThemePreferences
    {
        private const string RegistryKeyPath = @"Software\SteamPluginManager";
        private const string ThemeValueName = "ThemePreference";
        public static readonly string[] AvailableThemes = { "Dark", "DarkBlue", "DarkGreen", "DarkPink", "DarkPurple", "DarkYellow", "DarkRed" };

        public static string NormalizeThemeName(string? themeName)
        {
            return Array.IndexOf(AvailableThemes, themeName) >= 0 ? themeName! : "Dark";
        }

        public static void SaveThemePreference(string themeName)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                key?.SetValue(ThemeValueName, themeName);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save theme preference: {ex.Message}");
            }
        }

        public static string? GetSavedThemePreference()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                if (key?.GetValue(ThemeValueName) is string themeValue)
                {
                    return NormalizeThemeName(themeValue);
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




