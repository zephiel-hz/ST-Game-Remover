using Microsoft.Win32;
using System;

namespace SteamPluginManager
{
    public static class SystemTrayPreferences
    {
        private const string RegistryKeyPath = @"Software\SteamPluginManager";
        private const string CloseToTrayValueName = "CloseToTray";
        private const string CloseToTrayNoticeShownValueName = "CloseToTrayNoticeShown";

        private static bool? _cachedCloseToTray = null;
        private static bool? _cachedNoticeShown = null;

        public static event EventHandler<bool>? CloseToTrayPreferenceChanged;

        /// <summary>
        /// Gets whether closing the application window should minimize it to the system tray.
        /// Defaults to true.
        /// </summary>
        public static bool IsCloseToTrayEnabled()
        {
            if (_cachedCloseToTray.HasValue)
            {
                return _cachedCloseToTray.Value;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                if (key?.GetValue(CloseToTrayValueName) is int value)
                {
                    _cachedCloseToTray = value == 1;
                    return _cachedCloseToTray.Value;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayPreferences] Failed to load CloseToTray preference: {ex.Message}");
            }

            // Default to enabled (true)
            _cachedCloseToTray = true;
            return true;
        }

        /// <summary>
        /// Saves whether closing the application window should minimize it to the system tray.
        /// </summary>
        public static void SaveCloseToTrayPreference(bool isEnabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                key?.SetValue(CloseToTrayValueName, isEnabled ? 1 : 0);
                _cachedCloseToTray = isEnabled;
                Logger.Log($"[SystemTrayPreferences] Saved CloseToTray preference: {isEnabled}");

                CloseToTrayPreferenceChanged?.Invoke(null, isEnabled);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayPreferences] Failed to save CloseToTray preference: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets whether the first-time notice about running in the system tray has already been shown.
        /// Defaults to false.
        /// </summary>
        public static bool HasShownCloseToTrayNotice()
        {
            if (_cachedNoticeShown.HasValue)
            {
                return _cachedNoticeShown.Value;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                if (key?.GetValue(CloseToTrayNoticeShownValueName) is int value)
                {
                    _cachedNoticeShown = value == 1;
                    return _cachedNoticeShown.Value;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayPreferences] Failed to load CloseToTrayNoticeShown preference: {ex.Message}");
            }

            _cachedNoticeShown = false;
            return false;
        }

        /// <summary>
        /// Saves whether the first-time notice about running in the system tray has been shown.
        /// </summary>
        public static void SaveShownCloseToTrayNotice(bool hasShown)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                key?.SetValue(CloseToTrayNoticeShownValueName, hasShown ? 1 : 0);
                _cachedNoticeShown = hasShown;
                Logger.Log($"[SystemTrayPreferences] Saved CloseToTrayNoticeShown preference: {hasShown}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayPreferences] Failed to save CloseToTrayNoticeShown preference: {ex.Message}");
            }
        }

        private const string TrayChatNotificationValueName = "TrayChatNotification";
        private static bool? _cachedTrayChatNotification = null;
        public static event EventHandler<bool>? TrayChatNotificationPreferenceChanged;

        /// <summary>
        /// Gets whether desktop notifications should be shown for mentions and replies while in system tray mode.
        /// Defaults to true.
        /// </summary>
        public static bool IsTrayChatNotificationEnabled()
        {
            if (_cachedTrayChatNotification.HasValue)
            {
                return _cachedTrayChatNotification.Value;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                if (key?.GetValue(TrayChatNotificationValueName) is int value)
                {
                    _cachedTrayChatNotification = value == 1;
                    return _cachedTrayChatNotification.Value;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayPreferences] Failed to load TrayChatNotification preference: {ex.Message}");
            }

            // Default to enabled (true)
            _cachedTrayChatNotification = true;
            return true;
        }

        /// <summary>
        /// Saves whether desktop notifications should be shown for mentions and replies while in system tray mode.
        /// </summary>
        public static void SaveTrayChatNotificationPreference(bool isEnabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                key?.SetValue(TrayChatNotificationValueName, isEnabled ? 1 : 0);
                _cachedTrayChatNotification = isEnabled;
                Logger.Log($"[SystemTrayPreferences] Saved TrayChatNotification preference: {isEnabled}");

                TrayChatNotificationPreferenceChanged?.Invoke(null, isEnabled);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SystemTrayPreferences] Failed to save TrayChatNotification preference: {ex.Message}");
            }
        }

        public static void ClearCache()
        {
            _cachedCloseToTray = null;
            _cachedNoticeShown = null;
            _cachedTrayChatNotification = null;
        }
    }
}
