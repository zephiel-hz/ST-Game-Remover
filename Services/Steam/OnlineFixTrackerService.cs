using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SteamPluginManager.Services.Steam
{
    public class OnlineFixInstallRecord
    {
        public int AppId { get; set; }
        public string GameName { get; set; } = string.Empty;
        public string TargetFolder { get; set; } = string.Empty;
        public List<string> InstalledRelativePaths { get; set; } = new();
        public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Service to track and manage files installed by Online-Fix packages into game folders.
    /// Supports saving/loading manifests in both AppData and local game directories,
    /// as well as heuristic detection of known Online-Fix signatures.
    /// </summary>
    public static class OnlineFixTrackerService
    {
        private static readonly object _fileLock = new();
        private static readonly string _trackingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamPluginManager",
            "OnlineFixManifests"
        );

        public const string GameFolderManifestFileName = ".onlinefix_manifest.json";

        private static readonly string[] KnownOnlineFixFileNames = new[]
        {
            "OnlineFix.ini",
            "OnlineFix64.ini",
            "OnlineFix.dll",
            "OnlineFix64.dll",
            "SteamOverlay.dll",
            "SteamOverlay64.dll",
            "OnlineFix.url",
            "steam_interfaces.txt",
            "steam_settings.ini"
        };

        private static readonly string[] KnownOnlineFixDirectories = new[]
        {
            "steam_settings"
        };

        /// <summary>
        /// Saves an install record in AppData and in the game target folder as a hidden file.
        /// </summary>
        public static void SaveInstallRecord(int appId, string gameName, string targetFolder, IEnumerable<string> relativePaths)
        {
            if (appId <= 0 || string.IsNullOrWhiteSpace(targetFolder))
                return;

            try
            {
                var record = new OnlineFixInstallRecord
                {
                    AppId = appId,
                    GameName = gameName ?? string.Empty,
                    TargetFolder = Path.GetFullPath(targetFolder),
                    InstalledRelativePaths = relativePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    InstalledAtUtc = DateTime.UtcNow
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(record, options);

                lock (_fileLock)
                {
                    // 1. Save in AppData
                    if (!Directory.Exists(_trackingDir))
                    {
                        Directory.CreateDirectory(_trackingDir);
                    }
                    string appDataPath = Path.Combine(_trackingDir, $"{appId}.json");
                    File.WriteAllText(appDataPath, json);
                    Logger.Log($"[OnlineFixTrackerService] Saved manifest to AppData: {appDataPath}");

                    // 2. Save in TargetFolder
                    if (Directory.Exists(targetFolder))
                    {
                        string localPath = Path.Combine(targetFolder, GameFolderManifestFileName);
                        File.WriteAllText(localPath, json);
                        try
                        {
                            File.SetAttributes(localPath, FileAttributes.Hidden);
                        }
                        catch { }
                        Logger.Log($"[OnlineFixTrackerService] Saved manifest to game folder: {localPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[OnlineFixTrackerService] Error saving install record for AppID {appId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieves the install record for an AppID from AppData or from the game folder.
        /// </summary>
        public static OnlineFixInstallRecord? GetInstallRecord(int appId, string? gameFolder = null)
        {
            if (appId <= 0) return null;

            lock (_fileLock)
            {
                try
                {
                    // 1. Try AppData
                    string appDataPath = Path.Combine(_trackingDir, $"{appId}.json");
                    if (File.Exists(appDataPath))
                    {
                        string json = File.ReadAllText(appDataPath);
                        var record = JsonSerializer.Deserialize<OnlineFixInstallRecord>(json);
                        if (record != null && record.InstalledRelativePaths.Count > 0)
                        {
                            Logger.Log($"[OnlineFixTrackerService] Loaded install record from AppData for AppID {appId} ({record.InstalledRelativePaths.Count} files)");
                            return record;
                        }
                    }

                    // 2. Try gameFolder
                    if (!string.IsNullOrWhiteSpace(gameFolder) && Directory.Exists(gameFolder))
                    {
                        string localPath = Path.Combine(gameFolder, GameFolderManifestFileName);
                        if (File.Exists(localPath))
                        {
                            string json = File.ReadAllText(localPath);
                            var record = JsonSerializer.Deserialize<OnlineFixInstallRecord>(json);
                            if (record != null)
                            {
                                Logger.Log($"[OnlineFixTrackerService] Loaded install record from game folder for AppID {appId}");
                                return record;
                            }
                        }

                        // Search for .onlinefix_manifest.json in immediate subdirectories (e.g. bin / executable folders)
                        try
                        {
                            var matchingFiles = Directory.GetFiles(gameFolder, GameFolderManifestFileName, SearchOption.AllDirectories);
                            if (matchingFiles.Length > 0)
                            {
                                string json = File.ReadAllText(matchingFiles[0]);
                                var record = JsonSerializer.Deserialize<OnlineFixInstallRecord>(json);
                                if (record != null)
                                {
                                    Logger.Log($"[OnlineFixTrackerService] Loaded install record from subfolder {matchingFiles[0]}");
                                    return record;
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[OnlineFixTrackerService] Error reading install record for AppID {appId}: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Deletes the install records from AppData and any manifest files in the game folder.
        /// </summary>
        public static void DeleteInstallRecord(int appId, string? gameFolder = null)
        {
            lock (_fileLock)
            {
                try
                {
                    string appDataPath = Path.Combine(_trackingDir, $"{appId}.json");
                    if (File.Exists(appDataPath))
                    {
                        File.Delete(appDataPath);
                        Logger.Log($"[OnlineFixTrackerService] Deleted AppData manifest: {appDataPath}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[OnlineFixTrackerService] Error deleting AppData manifest for {appId}: {ex.Message}");
                }

                if (!string.IsNullOrWhiteSpace(gameFolder) && Directory.Exists(gameFolder))
                {
                    try
                    {
                        var matchingFiles = Directory.GetFiles(gameFolder, GameFolderManifestFileName, SearchOption.AllDirectories);
                        foreach (var f in matchingFiles)
                        {
                            try
                            {
                                File.Delete(f);
                                Logger.Log($"[OnlineFixTrackerService] Deleted game folder manifest: {f}");
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Checks whether an install record exists for this AppID.
        /// </summary>
        public static bool HasInstallRecord(int appId, string? gameFolder = null)
        {
            return GetInstallRecord(appId, gameFolder) != null;
        }

        /// <summary>
        /// Heuristically finds known Online-Fix files and folders in the game installation directory.
        /// Returns a list of absolute file paths and directory paths that match known Online-Fix signatures.
        /// </summary>
        public static (List<string> Files, List<string> Directories) ScanKnownOnlineFixSignatures(string gameFolder)
        {
            var foundFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var foundDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
                return (foundFiles.ToList(), foundDirs.ToList());

            try
            {
                // 1. Search for specific known Online-Fix file names
                foreach (var fileName in KnownOnlineFixFileNames)
                {
                    try
                    {
                        var matches = Directory.GetFiles(gameFolder, fileName, SearchOption.AllDirectories);
                        foreach (var m in matches)
                        {
                            foundFiles.Add(m);
                            Logger.Log($"[OnlineFixTrackerService] Identified known Online-Fix file: {m}");
                        }
                    }
                    catch { }
                }

                // 2. Search for known directories (e.g. steam_settings)
                foreach (var dirName in KnownOnlineFixDirectories)
                {
                    try
                    {
                        var matches = Directory.GetDirectories(gameFolder, dirName, SearchOption.AllDirectories);
                        foreach (var m in matches)
                        {
                            foundDirs.Add(m);
                            Logger.Log($"[OnlineFixTrackerService] Identified known Online-Fix directory: {m}");
                        }
                    }
                    catch { }
                }

                // 3. Inspect steam_api.dll / steam_api64.dll to verify if they are Online-Fix crack wrappers
                try
                {
                    var steamApiFiles = Directory.GetFiles(gameFolder, "steam_api*.dll", SearchOption.AllDirectories);
                    foreach (var dllPath in steamApiFiles)
                    {
                        if (DoesFileContainOnlineFixMarkers(dllPath))
                        {
                            foundFiles.Add(dllPath);
                            Logger.Log($"[OnlineFixTrackerService] steam_api DLL contains Online-Fix marker: {dllPath}");
                        }
                    }
                }
                catch { }

                // 4. Inspect proxy DLLs (winmm.dll, version.dll) if OnlineFix.ini is adjacent
                try
                {
                    var proxyCandidates = new[] { "winmm.dll", "version.dll", "d3d11.dll", "dxgi.dll" };
                    foreach (var proxy in proxyCandidates)
                    {
                        var matches = Directory.GetFiles(gameFolder, proxy, SearchOption.AllDirectories);
                        foreach (var m in matches)
                        {
                            string? dir = Path.GetDirectoryName(m);
                            if (dir != null && (File.Exists(Path.Combine(dir, "OnlineFix.ini")) || File.Exists(Path.Combine(dir, "OnlineFix64.ini"))))
                            {
                                foundFiles.Add(m);
                                Logger.Log($"[OnlineFixTrackerService] Proxy DLL next to OnlineFix.ini identified: {m}");
                            }
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Logger.Log($"[OnlineFixTrackerService] Error scanning signatures: {ex.Message}");
            }

            return (foundFiles.ToList(), foundDirs.ToList());
        }

        /// <summary>
        /// Reads up to 4MB of the file to check for Online-Fix embedded text strings (ASCII / UTF-8).
        /// </summary>
        public static bool DoesFileContainOnlineFixMarkers(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var fi = new FileInfo(filePath);
                if (fi.Length > 20 * 1024 * 1024) // Skip unusually huge files
                    return false;

                using var fs = File.OpenRead(filePath);
                int bufferSize = (int)Math.Min(fi.Length, 4 * 1024 * 1024);
                byte[] buffer = new byte[bufferSize];
                int bytesRead = fs.Read(buffer, 0, bufferSize);
                if (bytesRead <= 0) return false;

                string asciiContent = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                if (asciiContent.IndexOf("OnlineFix", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    asciiContent.IndexOf("online-fix.me", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    asciiContent.IndexOf("SteamOverlay64", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Cleans empty directories inside rootFolder recursively, but never deletes rootFolder itself.
        /// </summary>
        public static void CleanEmptySubdirectories(string rootFolder)
        {
            if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
                return;

            try
            {
                foreach (var directory in Directory.GetDirectories(rootFolder))
                {
                    CleanEmptySubdirectories(directory);
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        try
                        {
                            Directory.Delete(directory, false);
                            Logger.Log($"[OnlineFixTrackerService] Deleted empty directory: {directory}");
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }
}
