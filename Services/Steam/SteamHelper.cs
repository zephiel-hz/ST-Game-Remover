using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SteamPluginManager.Services.Steam;

namespace SteamPluginManager
{
    public static class SteamHelper
    {
        public static string? GetSteamPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam");
                var path = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return Path.GetFullPath(path.Trim().Replace('/', '\\'));
                }
            }
            catch { }
            return null;
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
                try
                {
                    return processes.Any(p =>
                    {
                        try { return !p.HasExited; }
                        catch { return true; }
                    });
                }
                finally
                {
                    foreach (var p in processes)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reads the Steam AppID of the currently running game from registry. Returns 0 if no game is running or if Steam is closed.
        /// </summary>
        public static int GetRunningAppId()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var val = key?.GetValue("RunningAppId") ?? key?.GetValue("RunningAppID");
                if (val is int intVal) return intVal;
                if (val != null && int.TryParse(val.ToString(), out int parsed)) return parsed;
            }
            catch { }
            return 0;
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

        public static void PlayGame(int appId)
        {
            try
            {
                LaunchSteam();
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://rungameid/{appId}",
                    UseShellExecute = true
                });
                string msg = $"[SteamHelper] Launched game via steam://rungameid/{appId}";
                System.Diagnostics.Debug.WriteLine(msg);
                Logger.Log(msg);
            }
            catch (Exception ex)
            {
                string errMsg = $"[SteamHelper] Error playing game {appId}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine(errMsg);
                Logger.Log(errMsg);
            }
        }

        public static void InstallGame(int appId)
        {
            try
            {
                LaunchSteam();
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://rungameid/{appId}",
                    UseShellExecute = true
                });
                string msg = $"[SteamHelper] Triggered install via steam://rungameid/{appId}";
                System.Diagnostics.Debug.WriteLine(msg);
                Logger.Log(msg);
            }
            catch (Exception ex)
            {
                string errMsg = $"[SteamHelper] Error installing game {appId}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine(errMsg);
                Logger.Log(errMsg);
            }
        }

        private static readonly ConcurrentDictionary<int, DateTime> _recentValidationTriggers = new();

        public static void RegisterRecentValidationTrigger(int appId)
        {
            if (appId > 0)
            {
                _recentValidationTriggers[appId] = DateTime.UtcNow;
            }
        }

        public static void VerifyGame(int appId)
        {
            try
            {
                RegisterRecentValidationTrigger(appId);
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://validate/{appId}",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamHelper] Error verifying game {appId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks whether Steam is currently validating (verifying integrity) or restoring files for the given AppID.
        /// Inspects recent in-memory triggers, ACF manifest StateFlags (StateValidating = 131072),
        /// and real-time Steam content_log.txt events.
        /// </summary>
        public static bool IsGameValidating(int appId)
        {
            if (appId <= 0) return false;

            try
            {
                // 1. Check if validation was recently triggered in-app within a 5-second grace window
                if (_recentValidationTriggers.TryGetValue(appId, out DateTime triggerTime))
                {
                    if (DateTime.UtcNow - triggerTime < TimeSpan.FromSeconds(5))
                    {
                        return true;
                    }
                    else if (DateTime.UtcNow - triggerTime > TimeSpan.FromMinutes(30))
                    {
                        _recentValidationTriggers.TryRemove(appId, out _);
                    }
                }

                // 2. Check direct progress state
                var progress = GetGameDownloadProgress(appId);
                if (progress.State == SteamAppState.Validating)
                    return true;

                // 3. Check ACF manifests directly across all libraries
                foreach (var library in EnumerateSteamLibraries())
                {
                    string manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                    if (File.Exists(manifest))
                    {
                        try
                        {
                            string content;
                            using (var fs = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var reader = new StreamReader(fs))
                            {
                                content = reader.ReadToEnd();
                            }

                            var stateMatch = System.Text.RegularExpressions.Regex.Match(content, "\"StateFlags\"\\s*\"([^\"]+)\"");
                            if (stateMatch.Success && int.TryParse(stateMatch.Groups[1].Value, out int stateFlags))
                            {
                                // 131072 = StateValidating
                                if ((stateFlags & 131072) != 0)
                                    return true;
                            }
                        }
                        catch { }
                    }
                }

                // 4. Inspect real-time content_log.txt for active validation session
                string logTail = GetContentLogTail(65536);
                if (!string.IsNullOrEmpty(logTail))
                {
                    int idxValStart = Math.Max(
                        logTail.LastIndexOf($"Start validating appID {appId}", StringComparison.OrdinalIgnoreCase),
                        logTail.LastIndexOf($"AppID {appId} App update changed : Running Update,Verifying Installed,", StringComparison.OrdinalIgnoreCase)
                    );

                    if (idxValStart >= 0)
                    {
                        int idxFinished = -1;

                        int idxSchedFinish = logTail.LastIndexOf($"AppID {appId} scheduler finished", StringComparison.OrdinalIgnoreCase);
                        if (idxSchedFinish > idxFinished) idxFinished = idxSchedFinish;

                        int idxUpdateNone = logTail.LastIndexOf($"AppID {appId} App update changed : None", StringComparison.OrdinalIgnoreCase);
                        if (idxUpdateNone > idxFinished) idxFinished = idxUpdateNone;

                        var stateMatches = System.Text.RegularExpressions.Regex.Matches(logTail, $@"AppID\s+{appId}\s+state changed\s*:\s*([^\r\n]+)");
                        if (stateMatches.Count > 0)
                        {
                            var lastState = stateMatches[stateMatches.Count - 1];
                            if (lastState.Index > idxValStart)
                            {
                                string stateLine = lastState.Groups[1].Value.Trim();
                                if (stateLine.StartsWith("Fully Installed", StringComparison.OrdinalIgnoreCase) &&
                                    !stateLine.Contains("Update Running", StringComparison.OrdinalIgnoreCase) &&
                                    !stateLine.Contains("Update Queued", StringComparison.OrdinalIgnoreCase) &&
                                    !stateLine.Contains("Files Missing", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (lastState.Index > idxFinished)
                                        idxFinished = lastState.Index;
                                }
                            }
                        }

                        if (idxValStart > idxFinished)
                        {
                            return true;
                        }
                    }
                }

                // 5. Check if parent game is validating if this is a DLC
                int? parentAppId = GetParentAppId(appId);
                if (parentAppId.HasValue && parentAppId.Value > 0 && parentAppId.Value != appId)
                {
                    return IsGameValidating(parentAppId.Value);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamHelper] Error checking validation state for AppID {appId}: {ex.Message}");
            }

            return false;
        }

        public static void UninstallGame(int appId)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://uninstall/{appId}",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamHelper] Error triggering uninstall for {appId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks whether the Steam app is registered as installed in Steam's library manifests (appmanifest_{appId}.acf).
        /// Does NOT depend on whether leftover files exist on disk, making it reliable for checking uninstalled state.
        /// </summary>
        public static bool IsSteamAppInstalled(int appId)
        {
            if (appId <= 0) return false;

            try
            {
                foreach (var library in EnumerateSteamLibraries())
                {
                    string manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                    if (File.Exists(manifestPath))
                    {
                        try
                        {
                            string content;
                            using (var fs = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var reader = new StreamReader(fs))
                            {
                                content = reader.ReadToEnd();
                            }

                            var match = System.Text.RegularExpressions.Regex.Match(content, "\"StateFlags\"\\s*\"([^\"]+)\"");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out int stateFlags))
                            {
                                // 1 = StateUninstalled
                                // If uninstalled or missing installed/downloading bits
                                if (stateFlags == 1 || ((stateFlags & 4) == 0 && (stateFlags & (256 | 1024)) == 0))
                                {
                                    return false;
                                }
                            }
                            return true;
                        }
                        catch
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamHelper] Error checking IsSteamAppInstalled for {appId}: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Recursively and cleanly deletes a game installation folder from disk.
        /// Strips Read-Only and Hidden attributes from all files and subdirectories to prevent Access Denied errors.
        /// Includes safety checks to prevent deleting drive roots or library base folders.
        /// </summary>
        public static bool DeleteGameFolderCleanly(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return true;

            try
            {
                string fullPath = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

                // Safety checks
                string? root = Path.GetPathRoot(fullPath)?.TrimEnd('\\', '/');
                if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log($"[SteamHelper] Safety abort: Attempted to delete root drive: {fullPath}");
                    return false;
                }

                string dirName = Path.GetFileName(fullPath);
                if (string.Equals(dirName, "steamapps", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dirName, "common", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log($"[SteamHelper] Safety abort: Attempted to delete Steam library directory: {fullPath}");
                    return false;
                }

                Logger.Log($"[SteamHelper] Cleanly deleting game folder: {fullPath}");

                int maxRetries = 5;
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(fullPath);
                        if (dirInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
                        {
                            dirInfo.Attributes &= ~FileAttributes.ReadOnly;
                        }

                        // Strip attributes and delete files
                        foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                if (file.Attributes.HasFlag(FileAttributes.ReadOnly) || file.Attributes.HasFlag(FileAttributes.Hidden))
                                {
                                    file.Attributes = FileAttributes.Normal;
                                }
                                file.Delete();
                            }
                            catch { }
                        }

                        // Strip attributes and delete subdirectories from deepest first
                        var subDirs = dirInfo.GetDirectories("*", SearchOption.AllDirectories)
                                             .OrderByDescending(d => d.FullName.Length);
                        foreach (var subDir in subDirs)
                        {
                            try
                            {
                                if (subDir.Attributes.HasFlag(FileAttributes.ReadOnly) || subDir.Attributes.HasFlag(FileAttributes.Hidden))
                                {
                                    subDir.Attributes = FileAttributes.Normal;
                                }
                                subDir.Delete(true);
                            }
                            catch { }
                        }

                        dirInfo.Delete(true);

                        if (!Directory.Exists(fullPath))
                        {
                            Logger.Log($"[SteamHelper] Successfully deleted game folder: {fullPath}");
                            return true;
                        }
                    }
                    catch (Exception ex) when (attempt < maxRetries - 1)
                    {
                        Logger.Log($"[SteamHelper] Attempt {attempt + 1} to delete {fullPath} failed: {ex.Message}. Retrying...");
                        System.Threading.Thread.Sleep(500 * (attempt + 1));
                    }
                }

                return !Directory.Exists(fullPath);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamHelper] Failed to delete game folder {folderPath}: {ex.Message}");
                return false;
            }
        }

        public static void OpenGameFolder(string? path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamHelper] Error opening game folder: {ex.Message}");
            }
        }

        public static void OpenSteamStore(int appId)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"https://store.steampowered.com/app/{appId}",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public static void OpenSteamDb(int appId)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"https://steamdb.info/app/{appId}/",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public static void CreateDesktopShortcut(int appId, string gameName)
        {
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string cleanName = string.Join("_", gameName.Split(Path.GetInvalidFileNameChars()));
                string shortcutPath = Path.Combine(desktopPath, $"{cleanName}.url");

                using var writer = new StreamWriter(shortcutPath);
                writer.WriteLine("[InternetShortcut]");
                writer.WriteLine($"URL=steam://rungameid/{appId}");
                writer.WriteLine($"IconIndex=0");
                string steamExe = Path.Combine(GetSteamPath() ?? "", "steam.exe");
                if (File.Exists(steamExe))
                {
                    writer.WriteLine($"IconFile={steamExe}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamHelper] Error creating desktop shortcut: {ex.Message}");
            }
        }

        private static System.Collections.Generic.List<string>? _cachedLibraries;
        private static DateTime _lastLibrariesCacheTime = DateTime.MinValue;
        private static readonly object _librariesLock = new();

        public static System.Collections.Generic.List<string> EnumerateSteamLibraries()
        {
            lock (_librariesLock)
            {
                if (_cachedLibraries != null && (DateTime.UtcNow - _lastLibrariesCacheTime).TotalSeconds < 10)
                {
                    return new System.Collections.Generic.List<string>(_cachedLibraries);
                }

                var results = new System.Collections.Generic.List<string>();
                var steamPath = GetSteamPath();
                if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
                    return results;

                results.Add(steamPath);
                try
                {
                    string libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(libraryFile))
                    {
                        foreach (var line in File.ReadAllLines(libraryFile))
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(line, "\\\"path\\\"\\s*\\\"(?<p>.+?)\\\"");
                            if (m.Success)
                            {
                                string raw = m.Groups["p"].Value;
                                string unescaped = raw.Replace("\\\\", "\\").Replace('/', '\\');
                                if (Directory.Exists(unescaped) && !results.Contains(unescaped, StringComparer.OrdinalIgnoreCase))
                                {
                                    try { results.Add(Path.GetFullPath(unescaped)); }
                                    catch { results.Add(unescaped); }
                                }
                            }
                        }
                    }
                }
                catch { }

                _cachedLibraries = results;
                _lastLibrariesCacheTime = DateTime.UtcNow;
                return new System.Collections.Generic.List<string>(results);
            }
        }

        /// <summary>
        /// Fast batch scanner that locates all currently installed Steam games across all libraries in a single pass.
        /// </summary>
        public static System.Collections.Generic.Dictionary<int, string> GetInstalledGamesMap()
        {
            var map = new System.Collections.Generic.Dictionary<int, string>();
            try
            {
                var libraries = EnumerateSteamLibraries();
                foreach (var library in libraries)
                {
                    string steamapps = Path.Combine(library, "steamapps");
                    if (!Directory.Exists(steamapps)) continue;

                    string[] acfFiles;
                    try
                    {
                        acfFiles = Directory.GetFiles(steamapps, "appmanifest_*.acf");
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var acf in acfFiles)
                    {
                        try
                        {
                            string fileName = Path.GetFileNameWithoutExtension(acf);
                            int underscore = fileName.IndexOf('_');
                            if (underscore < 0 || !int.TryParse(fileName.AsSpan(underscore + 1), out int appId))
                                continue;

                            string content;
                            using (var fs = new FileStream(acf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var reader = new StreamReader(fs))
                            {
                                content = reader.ReadToEnd();
                            }

                            int stateFlags = 0;
                            var stateMatch = System.Text.RegularExpressions.Regex.Match(content, "\"StateFlags\"\\s*\"([^\"]+)\"");
                            if (stateMatch.Success)
                            {
                                int.TryParse(stateMatch.Groups[1].Value, out stateFlags);
                            }

                            bool isDownloadingOrPaused = (stateFlags & (256 | 512 | 1024)) != 0;
                            bool isInstalled = (stateFlags & 4) != 0 && !isDownloadingOrPaused;

                            if (isInstalled)
                            {
                                var dirMatch = System.Text.RegularExpressions.Regex.Match(content, "\"installdir\"\\s*\"([^\"]+)\"");
                                if (dirMatch.Success)
                                {
                                    string installDir = dirMatch.Groups[1].Value;
                                    string rawFolder = Path.Combine(library, "steamapps", "common", installDir).Replace('/', '\\');
                                    string gameFolder = rawFolder;
                                    try { gameFolder = Path.GetFullPath(rawFolder); } catch { }
                                    if (Directory.Exists(gameFolder))
                                    {
                                        map[appId] = gameFolder;

                                        // Also map any installed DLCs / sub-apps under this parent game (e.g. Call of Duty MW4 under Call of Duty HQ)
                                        try
                                        {
                                            var disabledDlcSet = new HashSet<int>();
                                            var disabledMatches = System.Text.RegularExpressions.Regex.Matches(content, "\"DisabledDLC\"\\s*\"([^\"]*)\"");
                                            foreach (System.Text.RegularExpressions.Match dm in disabledMatches)
                                            {
                                                var ids = dm.Groups[1].Value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                                foreach (var idStr in ids)
                                                {
                                                    if (int.TryParse(idStr, out int disId))
                                                        disabledDlcSet.Add(disId);
                                                }
                                            }

                                            var dlcMatches = System.Text.RegularExpressions.Regex.Matches(content, "\"dlcappid\"\\s*\"(\\d+)\"");
                                            foreach (System.Text.RegularExpressions.Match dlcMatch in dlcMatches)
                                            {
                                                if (int.TryParse(dlcMatch.Groups[1].Value, out int dlcAppId) && dlcAppId > 0)
                                                {
                                                    if (!disabledDlcSet.Contains(dlcAppId) && !map.ContainsKey(dlcAppId))
                                                    {
                                                        map[dlcAppId] = gameFolder;
                                                    }
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamHelper] Error getting installed games map: {ex.Message}");
            }
            return map;
        }

        /// <summary>
        /// Locates the game installation folder on disk by AppID and optional GameName.
        /// Searches all Steam libraries directly (inspecting ACF manifests and 'common' folders),
        /// resolves parent apps/DLCs, checks cached install records, and matches folder names.
        /// Does NOT depend on WPF Window instances or block on temporary download/validating states.
        /// </summary>
        public static string? GetGameInstallationPath(int appId, string? gameName = null)
        {
            if (appId <= 0 && string.IsNullOrWhiteSpace(gameName))
                return null;

            try
            {
                var libraries = EnumerateSteamLibraries();

                // 1. Check directly for appmanifest_{appId}.acf in each Steam library
                if (appId > 0)
                {
                    foreach (var library in libraries)
                    {
                        string manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                        if (File.Exists(manifestPath))
                        {
                            try
                            {
                                string content;
                                using (var fs = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                using (var reader = new StreamReader(fs))
                                {
                                    content = reader.ReadToEnd();
                                }

                                var dirMatch = System.Text.RegularExpressions.Regex.Match(content, "\"installdir\"\\s*\"([^\"]+)\"");
                                if (dirMatch.Success)
                                {
                                    string installDir = dirMatch.Groups[1].Value;
                                    string rawFolder = Path.Combine(library, "steamapps", "common", installDir).Replace('/', '\\');
                                    string gameFolder = rawFolder;
                                    try { gameFolder = Path.GetFullPath(rawFolder); } catch { }
                                    if (Directory.Exists(gameFolder))
                                    {
                                        Logger.Log($"[SteamHelper] Found installation folder for AppID {appId} via ACF: {gameFolder}");
                                        return gameFolder;
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    // 2. Check installed games map (handles parent games and DLC sub-apps)
                    var map = GetInstalledGamesMap();
                    if (map.TryGetValue(appId, out string? mappedFolder) && Directory.Exists(mappedFolder))
                    {
                        Logger.Log($"[SteamHelper] Found installation folder for AppID {appId} via InstalledGamesMap: {mappedFolder}");
                        return mappedFolder;
                    }

                    // 3. Check OnlineFix tracking record if previously registered
                    var installRecord = OnlineFixTrackerService.GetInstallRecord(appId);
                    if (installRecord != null && !string.IsNullOrEmpty(installRecord.TargetFolder) && Directory.Exists(installRecord.TargetFolder))
                    {
                        Logger.Log($"[SteamHelper] Found installation folder for AppID {appId} via OnlineFixTracker: {installRecord.TargetFolder}");
                        return installRecord.TargetFolder;
                    }
                }

                // 4. Check steamapps/common by folder name matching
                if (!string.IsNullOrWhiteSpace(gameName))
                {
                    string cleanedName = gameName.Trim();
                    foreach (var library in libraries)
                    {
                        string common = Path.Combine(library, "steamapps", "common");
                        if (!Directory.Exists(common)) continue;

                        // Exact folder match
                        string exactFolder = Path.Combine(common, cleanedName);
                        if (Directory.Exists(exactFolder))
                        {
                            Logger.Log($"[SteamHelper] Found installation folder by exact name '{cleanedName}': {exactFolder}");
                            return exactFolder;
                        }

                        // Try without apostrophes/special characters
                        string strippedName = cleanedName.Replace("'", "").Replace("’", "").Replace(":", "").Replace(" - ", " ");
                        string strippedFolder = Path.Combine(common, strippedName);
                        if (Directory.Exists(strippedFolder))
                        {
                            Logger.Log($"[SteamHelper] Found installation folder by stripped name '{strippedName}': {strippedFolder}");
                            return strippedFolder;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamHelper] Error resolving game installation path for {appId}: {ex.Message}");
            }

            return null;
        }

        public static void OpenSteamDownloads()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "steam://open/downloads",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamHelper] Error opening Steam downloads: {ex.Message}");
            }
        }

        public static string GetContentLogTail(int maxBytes = 32768)
        {
            try
            {
                var steamPath = GetSteamPath();
                if (string.IsNullOrWhiteSpace(steamPath)) return string.Empty;

                string logPath = Path.Combine(steamPath, "logs", "content_log.txt");
                if (!File.Exists(logPath)) return string.Empty;

                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length == 0) return string.Empty;

                long seekPos = Math.Max(0, fs.Length - maxBytes);
                fs.Seek(seekPos, SeekOrigin.Begin);

                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch { }
            return string.Empty;
        }

        public static double GetLiveDownloadRateMbps()
        {
            try
            {
                string tail = GetContentLogTail(16384);
                if (string.IsNullOrEmpty(tail)) return 0.0;

                var matches = System.Text.RegularExpressions.Regex.Matches(tail, @"Current download rate:\s*([\d\.]+)\s*Mbps");
                if (matches.Count > 0)
                {
                    var lastMatch = matches[matches.Count - 1];
                    if (double.TryParse(lastMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double rate))
                    {
                        return rate;
                    }
                }
            }
            catch { }
            return 0.0;
        }

        public static SteamDownloadProgress GetGameDownloadProgress(int appId)
        {
            var result = new SteamDownloadProgress();
            try
            {
                string logTail = GetContentLogTail(32768);

                foreach (var library in EnumerateSteamLibraries())
                {
                    string manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                    if (File.Exists(manifest))
                    {
                        try
                        {
                            string content;
                            using (var fs = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var reader = new StreamReader(fs))
                            {
                                content = reader.ReadToEnd();
                            }

                            int stateFlags = 0;
                            var stateMatch = System.Text.RegularExpressions.Regex.Match(content, "\"StateFlags\"\\s*\"([^\"]+)\"");
                            if (stateMatch.Success)
                            {
                                int.TryParse(stateMatch.Groups[1].Value, out stateFlags);
                            }
                            result.StateFlags = stateFlags;

                            int updateResult = 0;
                            var updateResultMatch = System.Text.RegularExpressions.Regex.Match(content, "\"UpdateResult\"\\s*\"([^\"]+)\"");
                            if (updateResultMatch.Success)
                            {
                                int.TryParse(updateResultMatch.Groups[1].Value, out updateResult);
                            }
                            result.UpdateResult = updateResult;

                            long bytesDownloaded = 0;
                            var dlMatch = System.Text.RegularExpressions.Regex.Match(content, "\"BytesDownloaded\"\\s*\"([^\"]+)\"");
                            if (dlMatch.Success)
                            {
                                long.TryParse(dlMatch.Groups[1].Value, out bytesDownloaded);
                            }

                            long bytesTotal = 0;
                            var totalMatch = System.Text.RegularExpressions.Regex.Match(content, "\"BytesToDownload\"\\s*\"([^\"]+)\"");
                            if (totalMatch.Success)
                            {
                                long.TryParse(totalMatch.Groups[1].Value, out bytesTotal);
                            }

                            // Reconcile with latest Steam content_log.txt events if available
                            if (!string.IsNullOrEmpty(logTail))
                            {
                                var startMatches = System.Text.RegularExpressions.Regex.Matches(logTail, $@"AppID\s+{appId}\s+update started\s*:\s*download\s+(\d+)/(\d+)");
                                if (startMatches.Count > 0)
                                {
                                    var lastStart = startMatches[startMatches.Count - 1];
                                    if (long.TryParse(lastStart.Groups[1].Value, out long logDl) && logDl > bytesDownloaded)
                                    {
                                        bytesDownloaded = logDl;
                                    }
                                    if (long.TryParse(lastStart.Groups[2].Value, out long logTotal) && logTotal > bytesTotal)
                                    {
                                        bytesTotal = logTotal;
                                    }
                                }

                                int idxCanceled = logTail.LastIndexOf($"AppID {appId} update canceled");
                                int idxStarted = logTail.LastIndexOf($"AppID {appId} update started");
                                if (idxCanceled > idxStarted && idxCanceled >= 0)
                                {
                                    updateResult = 4; // Suspended / Paused
                                }
                                else
                                {
                                    int idxState = logTail.LastIndexOf($"AppID {appId} state changed : ");
                                    if (idxState > idxStarted && idxState >= 0)
                                    {
                                        int endOfLine = logTail.IndexOf('\n', idxState);
                                        string line = endOfLine > idxState ? logTail.Substring(idxState, endOfLine - idxState) : logTail.Substring(idxState);
                                        if (line.Contains("(Suspended)"))
                                        {
                                            updateResult = 4;
                                        }
                                    }
                                }
                            }

                            result.BytesDownloaded = bytesDownloaded;
                            result.BytesTotal = bytesTotal;

                            string? installDir = null;
                            var dirMatch = System.Text.RegularExpressions.Regex.Match(content, "\"installdir\"\\s*\"([^\"]+)\"");
                            if (dirMatch.Success)
                            {
                                installDir = dirMatch.Groups[1].Value;
                                string rawFolder = Path.Combine(library, "steamapps", "common", installDir).Replace('/', '\\');
                                string gameFolder = rawFolder;
                                try { gameFolder = Path.GetFullPath(rawFolder); } catch { }
                                if (Directory.Exists(gameFolder))
                                {
                                    result.InstallPath = gameFolder;
                                }
                            }

                            bool isExplicitlyPaused = (stateFlags & 512) != 0 || updateResult == 4 || updateResult == 2;
                            bool isValidating = (stateFlags & 131072) != 0;

                            if (!isValidating && !string.IsNullOrEmpty(logTail))
                            {
                                int idxValStart = Math.Max(
                                    logTail.LastIndexOf($"Start validating appID {appId}", StringComparison.OrdinalIgnoreCase),
                                    logTail.LastIndexOf($"AppID {appId} App update changed : Running Update,Verifying Installed,", StringComparison.OrdinalIgnoreCase)
                                );
                                if (idxValStart >= 0)
                                {
                                    int idxFinished = Math.Max(
                                        logTail.LastIndexOf($"AppID {appId} scheduler finished", StringComparison.OrdinalIgnoreCase),
                                        logTail.LastIndexOf($"AppID {appId} App update changed : None", StringComparison.OrdinalIgnoreCase)
                                    );
                                    if (idxValStart > idxFinished)
                                    {
                                        isValidating = true;
                                    }
                                }
                            }

                            if (isValidating)
                            {
                                result.State = SteamAppState.Validating;
                                return result;
                            }

                            if (isExplicitlyPaused && (bytesTotal > 0 && bytesDownloaded < bytesTotal))
                            {
                                result.State = SteamAppState.Paused;
                                result.DownloadRateMbps = 0.0;
                                return result;
                            }

                            bool isActivelyDownloading = (stateFlags & (256 | 1024 | 524288 | 1048576 | 2097152 | 4194304)) != 0
                                || (bytesTotal > 0 && bytesDownloaded < bytesTotal);

                            if (isActivelyDownloading && !isExplicitlyPaused)
                            {
                                result.State = SteamAppState.Downloading;
                                result.DownloadRateMbps = GetLiveDownloadRateMbps();
                                return result;
                            }

                            if ((stateFlags & 4) != 0 && (stateFlags & (256 | 1024)) == 0 && !string.IsNullOrWhiteSpace(result.InstallPath))
                            {
                                result.State = SteamAppState.Installed;
                                return result;
                            }
                        }
                        catch { }
                    }

                    // Check if downloading staging folder exists even if manifest is still initializing
                    string downloadingFolder = Path.Combine(library, "steamapps", "downloading", appId.ToString());
                    if (Directory.Exists(downloadingFolder))
                    {
                        result.State = SteamAppState.Downloading;
                        result.DownloadRateMbps = GetLiveDownloadRateMbps();
                        return result;
                    }
                }

                // If direct appmanifest_{appId}.acf was not found, check if this appId is a sub-DLC / component of an installed parent game
                try
                {
                    int? parentAppId = GetParentAppId(appId);
                    string? parentManifestPath = null;
                    string? parentLibrary = null;

                    var allLibraries = EnumerateSteamLibraries();

                    if (parentAppId.HasValue)
                    {
                        foreach (var library in allLibraries)
                        {
                            string pManifest = Path.Combine(library, "steamapps", $"appmanifest_{parentAppId.Value}.acf");
                            if (File.Exists(pManifest))
                            {
                                parentManifestPath = pManifest;
                                parentLibrary = library;
                                break;
                            }
                        }
                    }

                    if (parentManifestPath == null)
                    {
                        foreach (var library in allLibraries)
                        {
                            string steamapps = Path.Combine(library, "steamapps");
                            if (!Directory.Exists(steamapps)) continue;

                            string[] acfFiles;
                            try { acfFiles = Directory.GetFiles(steamapps, "appmanifest_*.acf"); }
                            catch { continue; }

                            foreach (var acf in acfFiles)
                            {
                                try
                                {
                                    string content;
                                    using (var fs = new FileStream(acf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                    using (var reader = new StreamReader(fs))
                                    {
                                        content = reader.ReadToEnd();
                                    }

                                    if (content.Contains($"\"{appId}\"") && System.Text.RegularExpressions.Regex.IsMatch(content, $"\"dlcappid\"\\s*\"{appId}\""))
                                    {
                                        parentManifestPath = acf;
                                        parentLibrary = library;
                                        string fileName = Path.GetFileNameWithoutExtension(acf);
                                        int underscore = fileName.IndexOf('_');
                                        if (underscore >= 0 && int.TryParse(fileName.AsSpan(underscore + 1), out int pId))
                                        {
                                            parentAppId = pId;
                                        }
                                        break;
                                    }
                                }
                                catch { }
                            }
                            if (parentManifestPath != null) break;
                        }
                    }

                    if (parentManifestPath != null && parentLibrary != null)
                    {
                        string parentContent;
                        using (var fs = new FileStream(parentManifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(fs))
                        {
                            parentContent = reader.ReadToEnd();
                        }

                        bool isDlcDisabled = false;
                        var disabledMatches = System.Text.RegularExpressions.Regex.Matches(parentContent, "\"DisabledDLC\"\\s*\"([^\"]*)\"");
                        foreach (System.Text.RegularExpressions.Match dm in disabledMatches)
                        {
                            var ids = dm.Groups[1].Value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            if (ids.Contains(appId.ToString()))
                            {
                                isDlcDisabled = true;
                                break;
                            }
                        }

                        if (!isDlcDisabled)
                        {
                            int pStateFlags = 0;
                            var stateMatch = System.Text.RegularExpressions.Regex.Match(parentContent, "\"StateFlags\"\\s*\"([^\"]+)\"");
                            if (stateMatch.Success) int.TryParse(stateMatch.Groups[1].Value, out pStateFlags);

                            var dirMatch = System.Text.RegularExpressions.Regex.Match(parentContent, "\"installdir\"\\s*\"([^\"]+)\"");
                            if (dirMatch.Success)
                            {
                                string installDir = dirMatch.Groups[1].Value;
                                string rawFolder = Path.Combine(parentLibrary, "steamapps", "common", installDir).Replace('/', '\\');
                                string gameFolder = rawFolder;
                                try { gameFolder = Path.GetFullPath(rawFolder); } catch { }
                                if (Directory.Exists(gameFolder))
                                {
                                    result.InstallPath = gameFolder;
                                    result.ParentAppId = parentAppId;
                                    result.StateFlags = pStateFlags;

                                    bool isDownloadingOrPaused = (pStateFlags & (256 | 512 | 1024)) != 0;
                                    if ((pStateFlags & 4) != 0 && !isDownloadingOrPaused)
                                    {
                                        result.State = SteamAppState.Installed;
                                        return result;
                                    }
                                    else if (isDownloadingOrPaused)
                                    {
                                        result.State = (pStateFlags & 512) != 0 ? SteamAppState.Paused : SteamAppState.Downloading;
                                        result.DownloadRateMbps = GetLiveDownloadRateMbps();
                                        return result;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SteamHelper] Error resolving parent manifest for DLC {appId}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamHelper] Error reading download progress for {appId}: {ex.Message}");
            }

            result.State = SteamAppState.NotInstalled;
            return result;
        }

        /// <summary>
        /// Reads a .lua file and checks if it specifies a parent / main application that differs from its own AppID.
        /// </summary>
        public static int? GetParentAppIdFromLuaFile(string luaFilePath)
        {
            try
            {
                if (!File.Exists(luaFilePath)) return null;

                string fileName = Path.GetFileNameWithoutExtension(luaFilePath);
                if (!int.TryParse(fileName, out int thisAppId)) return null;

                using var fs = new FileStream(luaFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);

                string? line;
                bool nextIsMainApp = false;
                int count = 0;

                while ((line = reader.ReadLine()) != null && count++ < 60)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Contains("MAIN APPLICATION", StringComparison.OrdinalIgnoreCase))
                    {
                        nextIsMainApp = true;
                        continue;
                    }

                    if (nextIsMainApp || trimmed.StartsWith("addappid", StringComparison.OrdinalIgnoreCase))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"addappid\s*\(\s*(\d+)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int foundAppId))
                        {
                            if (foundAppId != thisAppId)
                            {
                                return foundAppId;
                            }
                            if (nextIsMainApp)
                            {
                                return null;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Attempts to locate the .lua file for the given appId in Steam's config directories and extract its parent AppID.
        /// </summary>
        public static int? GetParentAppId(int appId)
        {
            try
            {
                string steamPath = GetSteamPath();
                if (string.IsNullOrEmpty(steamPath)) return null;

                string pluginLua = Path.Combine(steamPath, "config", "stplug-in", $"{appId}.lua");
                if (File.Exists(pluginLua))
                {
                    var parent = GetParentAppIdFromLuaFile(pluginLua);
                    if (parent.HasValue) return parent;
                }

                string configLua = Path.Combine(steamPath, "config", "lua", $"{appId}.lua");
                if (File.Exists(configLua))
                {
                    var parent = GetParentAppIdFromLuaFile(configLua);
                    if (parent.HasValue) return parent;
                }
            }
            catch { }
            return null;
        }

        public static bool CheckIsGameInstalled(int appId, out string? installPath)
        {
            var progress = GetGameDownloadProgress(appId);
            if (progress.State == SteamAppState.Installed)
            {
                installPath = progress.InstallPath;
                return true;
            }
            installPath = null;
            return false;
        }
    }

    public static class SteamProcessMonitor
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

        private static Process? _cachedSteamProcess;
        private static ulong _lastReadBytes = 0;
        private static DateTime _lastSampleTime = DateTime.MinValue;

        public static (double speedMBps, long bytesDelta) SampleDownloadTraffic()
        {
            try
            {
                if (_cachedSteamProcess == null || _cachedSteamProcess.HasExited)
                {
                    var procs = Process.GetProcessesByName("steam");
                    if (procs.Length == 0) return (0.0, 0);
                    _cachedSteamProcess = procs[0];
                    _lastReadBytes = 0;
                    _lastSampleTime = DateTime.MinValue;
                }

                if (GetProcessIoCounters(_cachedSteamProcess.Handle, out var counters))
                {
                    var now = DateTime.UtcNow;
                    if (_lastSampleTime == DateTime.MinValue || _lastReadBytes == 0)
                    {
                        _lastReadBytes = counters.ReadTransferCount;
                        _lastSampleTime = now;
                        return (0.0, 0);
                    }

                    double elapsed = (now - _lastSampleTime).TotalSeconds;
                    if (elapsed <= 0.05) return (0.0, 0);

                    long diff = (long)(counters.ReadTransferCount - _lastReadBytes);
                    _lastReadBytes = counters.ReadTransferCount;
                    _lastSampleTime = now;

                    if (diff > 0)
                    {
                        double bytesPerSec = diff / elapsed;
                        double mbps = bytesPerSec / (1024.0 * 1024.0);
                        return (mbps, diff);
                    }
                }
            }
            catch
            {
                _cachedSteamProcess = null;
            }

            return (0.0, 0);
        }
    }

    public enum SteamAppState
    {
        NotInstalled,
        Downloading,
        Paused,
        Validating,
        Installed
    }

    public class SteamDownloadProgress
    {
        public SteamAppState State { get; set; } = SteamAppState.NotInstalled;
        public long BytesDownloaded { get; set; }
        public long BytesTotal { get; set; }
        public int StateFlags { get; set; }
        public int UpdateResult { get; set; }
        public double DownloadRateMbps { get; set; }
        public string? InstallPath { get; set; }
        public int? ParentAppId { get; set; }

        public double ProgressPercentage => BytesTotal > 0
            ? Math.Min(100.0, Math.Round((double)BytesDownloaded / BytesTotal * 100.0, 1))
            : 0.0;

        public double DownloadRateMBps => (DownloadRateMbps * 1_000_000.0) / (8.0 * 1024.0 * 1024.0);

        public string FormattedSpeed => DownloadRateMBps > 0.05 ? $"{DownloadRateMBps:F1} MB/s" : "0 MB/s";

        public string FormattedProgress => $"{FormatBytes(BytesDownloaded)} / {FormatBytes(BytesTotal)} ({ProgressPercentage:F1}%)";

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int digitGroups = (int)(Math.Log10(bytes) / Math.Log10(1024));
            digitGroups = Math.Min(units.Length - 1, digitGroups);
            return $"{bytes / Math.Pow(1024, digitGroups):F2} {units[digitGroups]}";
        }
    }
}
