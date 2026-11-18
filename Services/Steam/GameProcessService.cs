using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SteamPluginManager.Services.Steam;

namespace SteamPluginManager.Services.Steam
{
    public static class GameProcessService
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, [Out] System.Text.StringBuilder path, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private const int QueryLimitedInformation = 0x1000;

        public static (bool Success, string Message) LaunchGame(int appId)
        {
            return SteamHelper.TryPlayGame(appId, out string error)
                ? (true, "Launched through Steam.")
                : (false, error);
        }

        public static HashSet<string> GetGameExecutableNames(string? installationPath)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
                return names;

            try
            {
                var root = new DirectoryInfo(installationPath);
                foreach (var file in root.GetFiles("*.exe", SearchOption.TopDirectoryOnly))
                    names.Add(Path.GetFileNameWithoutExtension(file.Name));

                foreach (var directory in root.GetDirectories())
                {
                    if (directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || directory.Name.Equals("binaries", StringComparison.OrdinalIgnoreCase)
                        || directory.Name.Equals("win64", StringComparison.OrdinalIgnoreCase)
                        || directory.Name.Equals("x64", StringComparison.OrdinalIgnoreCase)
                        || directory.Name.Equals("retail", StringComparison.OrdinalIgnoreCase)
                        || directory.Name.Equals("game", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var file in directory.GetFiles("*.exe", SearchOption.AllDirectories))
                            names.Add(Path.GetFileNameWithoutExtension(file.Name));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameProcessService] Could not scan game executable names: {ex.Message}");
            }
            return names;
        }

        public static bool IsGameRunning(
            int appId,
            int? parentAppId,
            string? installationPath,
            Process? trackedProcess = null,
            IEnumerable<string>? knownExecutableNames = null)
        {
            try
            {
                if (trackedProcess != null)
                {
                    try { if (!trackedProcess.HasExited) return true; }
                    catch (InvalidOperationException) { }
                }

                int runningAppId = SteamHelper.GetRunningAppId();
                if (runningAppId == appId || (parentAppId.HasValue && runningAppId == parentAppId.Value))
                    return true;

                if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
                    return false;

                string root = Path.GetFullPath(installationPath).TrimEnd('\\', '/') + "\\";
                foreach (string executable in knownExecutableNames ?? GetGameExecutableNames(installationPath))
                {
                    foreach (var process in Process.GetProcessesByName(executable))
                    {
                        try
                        {
                            string? path = GetProcessPath(process.Id);
                            if (path != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameProcessService] Could not check game process state: {ex.Message}");
            }
            return false;
        }

        public static bool StopGame(
            int appId,
            int? parentAppId,
            string? installationPath,
            Process? trackedProcess = null,
            IEnumerable<string>? knownExecutableNames = null)
        {
            bool stopped = false;
            try
            {
                if (trackedProcess != null)
                {
                    try
                    {
                        if (!trackedProcess.HasExited)
                        {
                            trackedProcess.Kill(entireProcessTree: true);
                            stopped = true;
                        }
                    }
                    catch (InvalidOperationException) { }
                }

                if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
                    return stopped;

                string root = Path.GetFullPath(installationPath).TrimEnd('\\', '/') + "\\";
                foreach (string executable in knownExecutableNames ?? GetGameExecutableNames(installationPath))
                {
                    foreach (var process in Process.GetProcessesByName(executable))
                    {
                        try
                        {
                            string? path = GetProcessPath(process.Id);
                            if (path == null || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                                continue;
                            process.Kill(entireProcessTree: true);
                            stopped = true;
                            Logger.Log($"[GameProcessService] Stopped game process {process.Id}: {path}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[GameProcessService] Could not stop process {process.Id}: {ex.Message}");
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameProcessService] Could not stop game {appId}: {ex.Message}");
            }
            return stopped;
        }

        public static (bool Success, string Message) CreateDesktopShortcut(int appId, string gameName)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string name = string.Join("_", gameName.Split(Path.GetInvalidFileNameChars()));
                string shortcut = Path.Combine(desktop, $"{name}.url");
                using var writer = new StreamWriter(shortcut, false);
                writer.WriteLine("[InternetShortcut]");
                writer.WriteLine($"URL=steam://rungameid/{appId}");
                string? steamPath = SteamHelper.GetSteamPath();
                string steamIcon = string.IsNullOrWhiteSpace(steamPath)
                    ? string.Empty
                    : Path.Combine(steamPath, "steam.exe");
                if (File.Exists(steamIcon))
                    writer.WriteLine($"IconFile={steamIcon}");
                writer.WriteLine("IconIndex=0");

                string oldShortcut = Path.Combine(desktop, $"{name}.lnk");
                if (File.Exists(oldShortcut))
                    File.Delete(oldShortcut);

                return (true, $"Steam shortcut created for '{gameName}'.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[GameProcessService] Could not create Steam shortcut for {appId}: {ex.Message}");
                return (false, $"Could not create shortcut: {ex.Message}");
            }
        }

        private static string? GetProcessPath(int processId)
        {
            if (processId <= 4)
                return null;

            IntPtr handle = OpenProcess(QueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
                return null;

            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                int length = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
