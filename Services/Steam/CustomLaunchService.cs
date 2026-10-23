using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using SteamPluginManager.Models;

namespace SteamPluginManager.Services.Steam
{
    public class LaunchableFileInfo
    {
        public string DisplayName { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsBatch { get; set; }
        public bool IsPrimary { get; set; }
        public string TypeBadge => IsBatch ? "BAT" : "EXE";

        public override string ToString()
        {
            return $"[{TypeBadge}] {RelativePath}";
        }
    }

    public static class CustomLaunchService
    {
        private static readonly object _fileLock = new();
        private static readonly string _configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamPluginManager"
        );
        private static readonly string _configFilePath = Path.Combine(_configDirectory, "custom_launch_configs.json");

        private static Dictionary<int, GameCustomLaunchConfig>? _cachedConfigs;

        #region Configuration Management

        private static Dictionary<int, GameCustomLaunchConfig> LoadAllConfigs()
        {
            lock (_fileLock)
            {
                if (_cachedConfigs != null)
                {
                    return _cachedConfigs;
                }

                try
                {
                    if (File.Exists(_configFilePath))
                    {
                        string json = File.ReadAllText(_configFilePath);
                        var loaded = JsonSerializer.Deserialize<Dictionary<string, GameCustomLaunchConfig>>(json);
                        if (loaded != null)
                        {
                            var dict = new Dictionary<int, GameCustomLaunchConfig>();
                            foreach (var kvp in loaded)
                            {
                                if (int.TryParse(kvp.Key, out int appId))
                                {
                                    kvp.Value.AppId = appId;
                                    dict[appId] = kvp.Value;
                                }
                            }
                            _cachedConfigs = dict;
                            return _cachedConfigs;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[CustomLaunchService] Error loading config file: {ex.Message}");
                }

                _cachedConfigs = new Dictionary<int, GameCustomLaunchConfig>();
                return _cachedConfigs;
            }
        }

        private static void SaveAllConfigs(Dictionary<int, GameCustomLaunchConfig> configs)
        {
            lock (_fileLock)
            {
                try
                {
                    if (!Directory.Exists(_configDirectory))
                    {
                        Directory.CreateDirectory(_configDirectory);
                    }

                    var exportDict = new Dictionary<string, GameCustomLaunchConfig>();
                    foreach (var kvp in configs)
                    {
                        exportDict[kvp.Key.ToString()] = kvp.Value;
                    }

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string json = JsonSerializer.Serialize(exportDict, options);
                    File.WriteAllText(_configFilePath, json);
                    _cachedConfigs = configs;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[CustomLaunchService] Error saving config file: {ex.Message}");
                }
            }
        }

        public static GameCustomLaunchConfig? GetConfig(int appId, string? installationPath = null)
        {
            var all = LoadAllConfigs();
            if (!all.TryGetValue(appId, out var config) || config == null)
            {
                return null;
            }

            // Verify or fix path if game directory moved
            if (!string.IsNullOrWhiteSpace(installationPath) && Directory.Exists(installationPath))
            {
                if (!File.Exists(config.ExecutablePath) && !string.IsNullOrWhiteSpace(config.RelativePath))
                {
                    string resolved = Path.Combine(installationPath, config.RelativePath);
                    if (File.Exists(resolved))
                    {
                        config.ExecutablePath = resolved;
                    }
                }
            }

            return config;
        }

        public static void SaveConfig(GameCustomLaunchConfig config, string? installationPath = null)
        {
            if (config == null || config.AppId <= 0) return;

            if (!string.IsNullOrWhiteSpace(installationPath) && !string.IsNullOrWhiteSpace(config.ExecutablePath))
            {
                try
                {
                    string fullNorm = Path.GetFullPath(config.ExecutablePath);
                    string installNorm = Path.GetFullPath(installationPath);
                    if (fullNorm.StartsWith(installNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        config.RelativePath = Path.GetRelativePath(installNorm, fullNorm);
                    }
                }
                catch { }
            }

            var all = LoadAllConfigs();
            all[config.AppId] = config;
            SaveAllConfigs(all);
            Logger.Log($"[CustomLaunchService] Saved custom launch config for AppID {config.AppId}: {config.ExecutablePath}");
        }

        public static void RemoveConfig(int appId)
        {
            var all = LoadAllConfigs();
            if (all.Remove(appId))
            {
                SaveAllConfigs(all);
                Logger.Log($"[CustomLaunchService] Removed custom launch config for AppID {appId}");
            }
        }

        public static bool IsCustomLaunchConfigured(int appId, string? installationPath = null)
        {
            var config = GetConfig(appId, installationPath);
            return config != null && config.IsEnabled && !string.IsNullOrWhiteSpace(config.ExecutablePath);
        }

        #endregion

        #region Scanning Game Launchables

        public static List<LaunchableFileInfo> FindGameLaunchables(string? installationPath)
        {
            var results = new List<LaunchableFileInfo>();
            if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
            {
                return results;
            }

            try
            {
                var directoryInfo = new DirectoryInfo(installationPath);
                string folderName = directoryInfo.Name;

                // Scan root files first
                var rootFiles = directoryInfo.GetFiles("*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => IsLaunchableExtension(f.Extension))
                    .ToList();

                foreach (var file in rootFiles)
                {
                    bool isBatch = IsBatchExtension(file.Extension);
                    bool isPrimary = !isBatch && file.Name.Contains(folderName, StringComparison.OrdinalIgnoreCase);

                    results.Add(new LaunchableFileInfo
                    {
                        DisplayName = $"{file.Name} (Root)",
                        RelativePath = file.Name,
                        FullPath = file.FullName,
                        IsBatch = isBatch,
                        IsPrimary = isPrimary
                    });
                }

                // Scan subdirectories up to 3 levels deep
                try
                {
                    var subFiles = EnumerateSubFiles(directoryInfo, currentDepth: 1, maxDepth: 3);
                    foreach (var file in subFiles)
                    {
                        string rel = Path.GetRelativePath(installationPath, file.FullName);
                        bool isBatch = IsBatchExtension(file.Extension);
                        bool isShipping = file.Name.Contains("Shipping", StringComparison.OrdinalIgnoreCase);

                        results.Add(new LaunchableFileInfo
                        {
                            DisplayName = $"{file.Name} ({Path.GetDirectoryName(rel)})",
                            RelativePath = rel,
                            FullPath = file.FullName,
                            IsBatch = isBatch,
                            IsPrimary = isShipping
                        });
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[CustomLaunchService] Error scanning subdirectories: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomLaunchService] Error enumerating launchables: {ex.Message}");
            }

            // Sort: Primary first, Root executables next, Batch scripts next, other sub-executables
            return results
                .OrderByDescending(x => x.IsPrimary)
                .ThenBy(x => x.RelativePath.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(x => x.IsBatch)
                .ThenBy(x => x.DisplayName)
                .ToList();
        }

        private static bool IsLaunchableExtension(string ext)
        {
            string clean = ext.Trim().ToLowerInvariant();
            return clean is ".exe" or ".bat" or ".cmd";
        }

        private static bool IsBatchExtension(string ext)
        {
            string clean = ext.Trim().ToLowerInvariant();
            return clean is ".bat" or ".cmd";
        }

        private static List<FileInfo> EnumerateSubFiles(DirectoryInfo dir, int currentDepth, int maxDepth)
        {
            var files = new List<FileInfo>();
            if (currentDepth > maxDepth) return files;

            foreach (var subDir in dir.GetDirectories())
            {
                // Skip common non-game subfolders
                string name = subDir.Name.ToLowerInvariant();
                if (name is ".venv" or "__pycache__" or "$recycle.bin" or "node_modules" or ".git")
                    continue;

                try
                {
                    files.AddRange(subDir.GetFiles("*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => IsLaunchableExtension(f.Extension)));

                    if (currentDepth < maxDepth)
                    {
                        files.AddRange(EnumerateSubFiles(subDir, currentDepth + 1, maxDepth));
                    }
                }
                catch { }
            }

            return files;
        }

        #endregion

        #region Game Icon Resolution

        /// <summary>
        /// Resolves the authentic game icon (original game exe or .ico) to preserve on desktop shortcuts.
        /// </summary>
        public static string? ResolveGameOriginalIcon(int appId, string gameName, string? installationPath, string? explicitIconPath = null)
        {
            try
            {
                // 1. If explicit icon path provided and valid, use it
                if (!string.IsNullOrWhiteSpace(explicitIconPath) && File.Exists(explicitIconPath))
                {
                    return explicitIconPath;
                }

                // 2. Search installation folder for primary game .exe
                if (!string.IsNullOrWhiteSpace(installationPath) && Directory.Exists(installationPath))
                {
                    var dir = new DirectoryInfo(installationPath);
                    var exes = dir.GetFiles("*.exe", SearchOption.TopDirectoryOnly);
                    if (exes.Length == 0)
                    {
                        try { exes = dir.GetFiles("*.exe", SearchOption.AllDirectories); }
                        catch { }
                    }

                    // Prefer an exe that matches game name or folder name
                    string cleanGameName = new string(gameName.Where(char.IsLetterOrDigit).ToArray());
                    var match = exes.FirstOrDefault(e =>
                    {
                        string cleanExe = new string(Path.GetFileNameWithoutExtension(e.Name).Where(char.IsLetterOrDigit).ToArray());
                        return cleanExe.Equals(cleanGameName, StringComparison.OrdinalIgnoreCase)
                            || cleanExe.Contains(cleanGameName, StringComparison.OrdinalIgnoreCase)
                            || cleanGameName.Contains(cleanExe, StringComparison.OrdinalIgnoreCase);
                    });

                    if (match != null)
                    {
                        return match.FullName;
                    }

                    // Otherwise pick any non-uninstaller, non-crashhandler root exe
                    var fallbackExe = exes.FirstOrDefault(e =>
                    {
                        string name = e.Name.ToLowerInvariant();
                        return !name.StartsWith("unins") && !name.Contains("crash") && !name.Contains("setup");
                    });

                    if (fallbackExe != null)
                    {
                        return fallbackExe.FullName;
                    }

                    // Check for any .ico in game folder
                    var ico = dir.GetFiles("*.ico", SearchOption.AllDirectories).FirstOrDefault();
                    if (ico != null)
                    {
                        return ico.FullName;
                    }
                }

                // 3. Search Steam's downloaded game icon folder
                string? steamPath = SteamHelper.GetSteamPath();
                if (!string.IsNullOrWhiteSpace(steamPath))
                {
                    string steamGamesDir = Path.Combine(steamPath, "steam", "games");
                    if (Directory.Exists(steamGamesDir))
                    {
                        // Check if an icon file exists in steam/games
                        var icoFiles = Directory.GetFiles(steamGamesDir, "*.ico");
                        if (icoFiles.Length > 0)
                        {
                            // In many cases, steam/games has the icon
                            var recentIco = icoFiles
                                .Select(f => new FileInfo(f))
                                .OrderByDescending(f => f.LastWriteTime)
                                .FirstOrDefault();
                            if (recentIco != null)
                            {
                                return recentIco.FullName;
                            }
                        }
                    }

                    // Check librarycache for app icon or logo
                    string appCacheIcon = Path.Combine(steamPath, "appcache", "librarycache", appId.ToString(), "logo.png");
                    if (File.Exists(appCacheIcon))
                    {
                        return appCacheIcon;
                    }

                    string steamExe = Path.Combine(steamPath, "steam.exe");
                    if (File.Exists(steamExe))
                    {
                        return steamExe;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomLaunchService] Error resolving original game icon: {ex.Message}");
            }

            return null;
        }

        #endregion

        #region Game Launch & Process Lifecycle Execution

        public static (bool Success, string Message, Process? Process) LaunchGameWithProcess(int appId, string? installationPath)
        {
            try
            {
                var config = GetConfig(appId, installationPath);

                if (config != null && config.IsEnabled && !string.IsNullOrWhiteSpace(config.ExecutablePath))
                {
                    if (!File.Exists(config.ExecutablePath))
                    {
                        // Try resolving relative path if install dir exists
                        if (!string.IsNullOrWhiteSpace(installationPath) && !string.IsNullOrWhiteSpace(config.RelativePath))
                        {
                            string resolved = Path.Combine(installationPath, config.RelativePath);
                            if (File.Exists(resolved))
                            {
                                config.ExecutablePath = resolved;
                            }
                        }
                    }

                    if (!File.Exists(config.ExecutablePath))
                    {
                        string missingMsg = $"Target executable not found: {config.ExecutablePath}\n\nFalling back to Steam default launch.";
                        Logger.Log($"[CustomLaunchService] {missingMsg}");
                        SteamHelper.PlayGame(appId);
                        return (false, missingMsg, null);
                    }

                    // Launch custom target
                    SteamHelper.LaunchSteam();

                    string workDir = !string.IsNullOrWhiteSpace(config.WorkingDirectory) && Directory.Exists(config.WorkingDirectory)
                        ? config.WorkingDirectory
                        : Path.GetDirectoryName(config.ExecutablePath) ?? "";

                    var psi = new ProcessStartInfo
                    {
                        FileName = config.ExecutablePath,
                        Arguments = config.Arguments ?? "",
                        WorkingDirectory = workDir,
                        UseShellExecute = true
                    };

                    var proc = Process.Start(psi);
                    string logMsg = $"[CustomLaunchService] Successfully launched custom executable for AppID {appId}: {config.ExecutablePath} (Args: '{config.Arguments}')";
                    System.Diagnostics.Debug.WriteLine(logMsg);
                    Logger.Log(logMsg);
                    return (true, $"Launched {Path.GetFileName(config.ExecutablePath)}", proc);
                }
                else
                {
                    // Default Steam launch
                    SteamHelper.PlayGame(appId);
                    return (true, "Launched via Steam default", null);
                }
            }
            catch (Exception ex)
            {
                string errMsg = $"[CustomLaunchService] Error launching game AppID {appId}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine(errMsg);
                Logger.Log(errMsg);
                return (false, ex.Message, null);
            }
        }

        public static (bool Success, string Message) LaunchGame(int appId, string? installationPath)
        {
            var res = LaunchGameWithProcess(appId, installationPath);
            return (res.Success, res.Message);
        }

        /// <summary>
        /// Collects all executable file names (*.exe) in the game directory (root and common binary subfolders)
        /// for fast process monitoring without inspecting every process on Windows.
        /// </summary>
        public static HashSet<string> GetGameExecutableNames(string? installationPath)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
            {
                return names;
            }

            try
            {
                var dir = new DirectoryInfo(installationPath);
                foreach (var f in dir.GetFiles("*.exe", SearchOption.TopDirectoryOnly))
                {
                    names.Add(Path.GetFileNameWithoutExtension(f.Name));
                }

                // Check common subfolders like Binaries, Win64, bin, retail
                try
                {
                    foreach (var subDir in dir.GetDirectories())
                    {
                        string subName = subDir.Name.ToLowerInvariant();
                        if (subName is "bin" or "binaries" or "win64" or "x64" or "retail" or "game")
                        {
                            foreach (var f in subDir.GetFiles("*.exe", SearchOption.AllDirectories))
                            {
                                names.Add(Path.GetFileNameWithoutExtension(f.Name));
                            }
                        }
                    }
                }
                catch { }
            }
            catch { }

            return names;
        }

        /// <summary>
        /// Checks whether the game is currently running.
        /// Checks Steam's RunningAppID registry, the given tracked process,
        /// or running processes originating from the game's installation directory.
        /// </summary>
        public static bool IsGameRunning(
            int appId,
            int? parentAppId,
            string? installationPath,
            Process? trackedProcess = null,
            IEnumerable<string>? knownExeNames = null)
        {
            try
            {
                // 1. Direct process object check
                if (trackedProcess != null)
                {
                    try
                    {
                        if (!trackedProcess.HasExited)
                        {
                            return true;
                        }
                    }
                    catch { }
                }

                // 2. Steam Registry RunningAppID check
                int runningSteamAppId = SteamHelper.GetRunningAppId();
                if (runningSteamAppId > 0)
                {
                    if (runningSteamAppId == appId || (parentAppId.HasValue && runningSteamAppId == parentAppId.Value))
                    {
                        return true;
                    }
                }

                // 3. Fast process lookup using known executable names matching installation path
                if (!string.IsNullOrWhiteSpace(installationPath) && Directory.Exists(installationPath))
                {
                    string normInstallPath = Path.GetFullPath(installationPath).TrimEnd('\\', '/') + "\\";

                    var namesToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (knownExeNames != null)
                    {
                        foreach (var name in knownExeNames) namesToCheck.Add(name);
                    }

                    var config = GetConfig(appId, installationPath);
                    if (config != null && !string.IsNullOrWhiteSpace(config.ExecutablePath) && File.Exists(config.ExecutablePath))
                    {
                        namesToCheck.Add(Path.GetFileNameWithoutExtension(config.ExecutablePath));
                    }

                    foreach (var exeName in namesToCheck)
                    {
                        try
                        {
                            var procs = Process.GetProcessesByName(exeName);
                            foreach (var p in procs)
                            {
                                try
                                {
                                    string? path = p.MainModule?.FileName;
                                    if (!string.IsNullOrEmpty(path) && path.StartsWith(normInstallPath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        return true;
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomLaunchService] Error checking game running state: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Stops running game processes.
        /// </summary>
        public static bool StopGame(
            int appId,
            int? parentAppId,
            string? installationPath,
            Process? trackedProcess = null,
            IEnumerable<string>? knownExeNames = null)
        {
            bool anyKilled = false;
            try
            {
                // 1. Kill tracked process
                if (trackedProcess != null)
                {
                    try
                    {
                        if (!trackedProcess.HasExited)
                        {
                            trackedProcess.Kill(entireProcessTree: true);
                            anyKilled = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[CustomLaunchService] Error killing tracked process: {ex.Message}");
                    }
                }

                // 2. Kill any processes running from game directory
                if (!string.IsNullOrWhiteSpace(installationPath) && Directory.Exists(installationPath))
                {
                    string normInstallPath = Path.GetFullPath(installationPath).TrimEnd('\\', '/') + "\\";

                    var namesToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (knownExeNames != null)
                    {
                        foreach (var name in knownExeNames) namesToCheck.Add(name);
                    }

                    var config = GetConfig(appId, installationPath);
                    if (config != null && !string.IsNullOrWhiteSpace(config.ExecutablePath))
                    {
                        namesToCheck.Add(Path.GetFileNameWithoutExtension(config.ExecutablePath));
                    }

                    foreach (var exeName in namesToCheck)
                    {
                        try
                        {
                            var procs = Process.GetProcessesByName(exeName);
                            foreach (var p in procs)
                            {
                                try
                                {
                                    string? path = p.MainModule?.FileName;
                                    if (!string.IsNullOrEmpty(path) && path.StartsWith(normInstallPath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        p.Kill(entireProcessTree: true);
                                        anyKilled = true;
                                        Logger.Log($"[CustomLaunchService] Terminated game process: {path} (PID: {p.Id})");
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomLaunchService] Error stopping game: {ex.Message}");
            }

            return anyKilled;
        }

        #endregion

        #region Desktop Shortcut Creation

        public static (bool Success, string Message) CreateDesktopShortcut(int appId, string gameName, string? installationPath)
        {
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string cleanName = string.Join("_", gameName.Split(Path.GetInvalidFileNameChars()));
                string shortcutPathLnk = Path.Combine(desktopPath, $"{cleanName}.lnk");
                string shortcutPathUrl = Path.Combine(desktopPath, $"{cleanName}.url");

                var config = GetConfig(appId, installationPath);
                string? originalIcon = ResolveGameOriginalIcon(appId, gameName, installationPath, config?.OriginalIconPath);

                if (config != null && config.IsEnabled && !string.IsNullOrWhiteSpace(config.ExecutablePath) && File.Exists(config.ExecutablePath))
                {
                    // Create Windows Shell Shortcut (.lnk) pointing to custom executable or batch script
                    string workDir = !string.IsNullOrWhiteSpace(config.WorkingDirectory) && Directory.Exists(config.WorkingDirectory)
                        ? config.WorkingDirectory
                        : Path.GetDirectoryName(config.ExecutablePath) ?? "";

                    bool lnkCreated = CreateShellShortcut(shortcutPathLnk, config.ExecutablePath, config.Arguments, workDir, originalIcon);

                    if (lnkCreated)
                    {
                        // Clean up old .url shortcut if it exists to avoid desktop duplication
                        try
                        {
                            if (File.Exists(shortcutPathUrl))
                            {
                                File.Delete(shortcutPathUrl);
                            }
                        }
                        catch { }

                        string msg = $"Desktop shortcut created for '{gameName}'!\nTarget: {Path.GetFileName(config.ExecutablePath)}\nIcon: Game Original Icon";
                        Logger.Log($"[CustomLaunchService] Created custom shortcut: {shortcutPathLnk} -> {config.ExecutablePath} (Icon: {originalIcon})");
                        return (true, msg);
                    }
                }

                // Default Steam launch shortcut (.url)
                using (var writer = new StreamWriter(shortcutPathUrl))
                {
                    writer.WriteLine("[InternetShortcut]");
                    writer.WriteLine($"URL=steam://rungameid/{appId}");
                    writer.WriteLine("IconIndex=0");
                    if (!string.IsNullOrWhiteSpace(originalIcon) && File.Exists(originalIcon))
                    {
                        writer.WriteLine($"IconFile={originalIcon}");
                    }
                    else
                    {
                        string steamExe = Path.Combine(SteamHelper.GetSteamPath() ?? "", "steam.exe");
                        if (File.Exists(steamExe))
                        {
                            writer.WriteLine($"IconFile={steamExe}");
                        }
                    }
                }

                // Clean up old .lnk shortcut if it exists
                try
                {
                    if (File.Exists(shortcutPathLnk))
                    {
                        File.Delete(shortcutPathLnk);
                    }
                }
                catch { }

                Logger.Log($"[CustomLaunchService] Created default Steam shortcut: {shortcutPathUrl} (Icon: {originalIcon})");
                return (true, $"Desktop shortcut created for '{gameName}'!");
            }
            catch (Exception ex)
            {
                string errMsg = $"Failed to create shortcut: {ex.Message}";
                Logger.Log($"[CustomLaunchService] Error creating desktop shortcut: {ex.Message}");
                return (false, errMsg);
            }
        }

        private static bool CreateShellShortcut(string shortcutPath, string targetPath, string? arguments, string workingDirectory, string? iconLocation)
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    shortcut.TargetPath = targetPath;
                    if (!string.IsNullOrWhiteSpace(arguments))
                    {
                        shortcut.Arguments = arguments;
                    }
                    if (!string.IsNullOrWhiteSpace(workingDirectory))
                    {
                        shortcut.WorkingDirectory = workingDirectory;
                    }
                    if (!string.IsNullOrWhiteSpace(iconLocation) && File.Exists(iconLocation))
                    {
                        shortcut.IconLocation = $"{iconLocation},0";
                    }
                    shortcut.Save();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomLaunchService] COM WScript.Shell failed: {ex.Message}. Attempting PowerShell fallback...");
            }

            // Fallback via PowerShell command
            try
            {
                string psScript = $@"$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('{shortcutPath.Replace("'", "''")}'); $s.TargetPath = '{targetPath.Replace("'", "''")}'; $s.WorkingDirectory = '{workingDirectory.Replace("'", "''")}';";
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    psScript += $" $s.Arguments = '{arguments.Replace("'", "''")}';";
                }
                if (!string.IsNullOrWhiteSpace(iconLocation) && File.Exists(iconLocation))
                {
                    psScript += $" $s.IconLocation = '{iconLocation.Replace("'", "''")},0';";
                }
                psScript += " $s.Save();";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -Command \"{psScript}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
                return File.Exists(shortcutPath);
            }
            catch (Exception exPs)
            {
                Logger.Log($"[CustomLaunchService] PowerShell shortcut fallback failed: {exPs.Message}");
                return false;
            }
        }

        #endregion
    }
}
