using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SteamPluginManager
{
    public static class SteamHelper
    {
        public static string? GetSteamPath()
        {
            using var key = Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam");
            return key?.GetValue("SteamPath") as string;
        }

        public static string GetPluginFolder()
        {
            var steamPath = GetSteamPath();
            return Path.Combine(steamPath ?? "", "config", "stplug-in");
        }

        public static string GetLuaFolder()
        {
            var steamPath = GetSteamPath();
            return Path.Combine(steamPath ?? "", "config", "lua");
        }

        public static bool IsSteamRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName("steam");
                return processes.Any(p =>
                {
                    try { return !p.HasExited; }
                    catch { return true; }
                });
            }
            catch
            {
                return false;
            }
        }

        public static void RestartSteam()
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("steam"))
                {
                    proc.Kill();
                    proc.WaitForExit();
                }
                Process.Start(Path.Combine(GetSteamPath() ?? "", "steam.exe"));
            }
            catch { }
        }

        public static void LaunchSteam()
        {
            try
            {
                if (!IsSteamRunning())
                {
                    Process.Start(Path.Combine(GetSteamPath() ?? "", "steam.exe"));
                }
            }
            catch { }
        }
    }
}
