using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SteamPluginManager.Models;

namespace SteamPluginManager.Services.Steam
{
    public static class CustomLaunchTutorialService
    {
        private const string R2_TUTORIALS_KEY = "tutorials/custom_launch_tutorials.json";
        private static readonly object _lock = new();

        private static readonly string _localCacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamPluginManager"
        );
        private static readonly string _localCacheFile = Path.Combine(_localCacheDirectory, "cached_tutorials.json");

        private static List<CustomLaunchTutorial>? _inMemoryTutorials;
        private static DateTime _lastFetchTime = DateTime.MinValue;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Retrieves all custom launch tutorials from Cloudflare R2 (or local cache)
        /// </summary>
        public static async Task<List<CustomLaunchTutorial>> GetAllTutorialsAsync(bool forceRefresh = false)
        {
            if (!forceRefresh && _inMemoryTutorials != null && (DateTime.UtcNow - _lastFetchTime) < CacheTtl)
            {
                return _inMemoryTutorials;
            }

            try
            {
                Logger.Log("[CustomLaunchTutorialService] Fetching tutorials from Cloudflare R2...");
                byte[] rawData = await R2Config.DownloadFileAsync(R2_TUTORIALS_KEY);
                if (rawData != null && rawData.Length > 0)
                {
                    string json = System.Text.Encoding.UTF8.GetString(rawData);
                    var list = JsonSerializer.Deserialize<List<CustomLaunchTutorial>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (list != null)
                    {
                        lock (_lock)
                        {
                            _inMemoryTutorials = list;
                            _lastFetchTime = DateTime.UtcNow;
                        }

                        // Save local cache for offline usage
                        try
                        {
                            Directory.CreateDirectory(_localCacheDirectory);
                            File.WriteAllText(_localCacheFile, json);
                        }
                        catch { }

                        Logger.Log($"[CustomLaunchTutorialService] Successfully loaded {list.Count} tutorials from R2.");
                        return list;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomLaunchTutorialService] R2 fetch notice/fallback: {ex.Message}");
            }

            // Fallback: load from local cache if available
            try
            {
                if (File.Exists(_localCacheFile))
                {
                    string localJson = File.ReadAllText(_localCacheFile);
                    var localList = JsonSerializer.Deserialize<List<CustomLaunchTutorial>>(localJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (localList != null)
                    {
                        lock (_lock)
                        {
                            _inMemoryTutorials = localList;
                        }
                        return localList;
                    }
                }
            }
            catch { }

            lock (_lock)
            {
                _inMemoryTutorials ??= GetDefaultFallbackTutorials();
            }

            return _inMemoryTutorials;
        }

        /// <summary>
        /// Finds a tutorial for a specific Steam AppID, optionally filtered by platform ("GAMEBYPASS" or "ONLINEFIX")
        /// </summary>
        public static async Task<CustomLaunchTutorial?> GetTutorialForAppAsync(int appId, string? platform = null, bool forceRefresh = false)
        {
            if (appId <= 0) return null;
            var all = await GetAllTutorialsAsync(forceRefresh);
            var tutorial = all.FirstOrDefault(t => t.AppId == appId);
            if (tutorial == null) return null;

            if (!string.IsNullOrWhiteSpace(platform) &&
                !string.Equals(tutorial.ApplicableTo, "ALL", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tutorial.ApplicableTo, platform, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return tutorial;
        }

        /// <summary>
        /// Saves or updates a tutorial on Cloudflare R2
        /// </summary>
        public static async Task<bool> SaveTutorialAsync(CustomLaunchTutorial tutorial)
        {
            if (tutorial == null || tutorial.AppId <= 0)
                throw new ArgumentException("Tutorial and valid AppId are required.");

            tutorial.UpdatedAt = DateTime.UtcNow;

            var all = await GetAllTutorialsAsync(forceRefresh: true);
            var existingIndex = all.FindIndex(t => t.AppId == tutorial.AppId);
            if (existingIndex >= 0)
            {
                all[existingIndex] = tutorial;
            }
            else
            {
                all.Add(tutorial);
            }

            string json = JsonSerializer.Serialize(all, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            string tempFile = Path.Combine(Path.GetTempPath(), $"tutorials_{Guid.NewGuid():N}.json");
            try
            {
                await File.WriteAllTextAsync(tempFile, json);
                await R2Config.UploadFileAsync(tempFile, R2_TUTORIALS_KEY, "application/json");

                lock (_lock)
                {
                    _inMemoryTutorials = all;
                    _lastFetchTime = DateTime.UtcNow;
                }

                // Update local cache
                try
                {
                    Directory.CreateDirectory(_localCacheDirectory);
                    File.WriteAllText(_localCacheFile, json);
                }
                catch { }

                Logger.Log($"[CustomLaunchTutorialService] Published tutorial for AppID {tutorial.AppId} ({tutorial.GameTitle}) to R2.");
                return true;
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        /// <summary>
        /// Deletes a tutorial from Cloudflare R2
        /// </summary>
        public static async Task<bool> DeleteTutorialAsync(int appId)
        {
            if (appId <= 0) return false;

            var all = await GetAllTutorialsAsync(forceRefresh: true);
            int removed = all.RemoveAll(t => t.AppId == appId);
            if (removed == 0) return true;

            string json = JsonSerializer.Serialize(all, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            string tempFile = Path.Combine(Path.GetTempPath(), $"tutorials_{Guid.NewGuid():N}.json");
            try
            {
                await File.WriteAllTextAsync(tempFile, json);
                await R2Config.UploadFileAsync(tempFile, R2_TUTORIALS_KEY, "application/json");

                lock (_lock)
                {
                    _inMemoryTutorials = all;
                    _lastFetchTime = DateTime.UtcNow;
                }

                try
                {
                    Directory.CreateDirectory(_localCacheDirectory);
                    File.WriteAllText(_localCacheFile, json);
                }
                catch { }

                Logger.Log($"[CustomLaunchTutorialService] Deleted tutorial for AppID {appId} from R2.");
                return true;
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        /// <summary>
        /// Automatically applies the recommended executable and arguments to the local game's Custom Launch configuration
        /// </summary>
        public static bool ApplyTutorialToLocalGame(int appId, CustomLaunchTutorial tutorial, string gameInstallPath)
        {
            if (appId <= 0 || tutorial == null || string.IsNullOrWhiteSpace(gameInstallPath) || !Directory.Exists(gameInstallPath))
                return false;

            string targetExePath = string.Empty;
            string relativePath = (tutorial.RecommendedExecutable ?? string.Empty).Trim().Replace('/', '\\');

            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                string combined = Path.Combine(gameInstallPath, relativePath);
                if (File.Exists(combined))
                {
                    targetExePath = combined;
                }
                else
                {
                    // Search recursively in game directory
                    string exeName = Path.GetFileName(relativePath);
                    try
                    {
                        var found = Directory.GetFiles(gameInstallPath, exeName, SearchOption.AllDirectories).FirstOrDefault();
                        if (!string.IsNullOrEmpty(found))
                        {
                            targetExePath = found;
                            relativePath = Path.GetRelativePath(gameInstallPath, found);
                        }
                    }
                    catch { }
                }
            }

            var existingConfig = CustomLaunchService.GetConfig(appId, gameInstallPath) ?? new GameCustomLaunchConfig
            {
                AppId = appId
            };

            existingConfig.IsEnabled = true;
            if (!string.IsNullOrEmpty(targetExePath))
            {
                existingConfig.ExecutablePath = targetExePath;
                existingConfig.RelativePath = relativePath;
            }
            existingConfig.Arguments = tutorial.RecommendedArguments ?? string.Empty;
            existingConfig.WorkingDirectory = gameInstallPath;

            CustomLaunchService.SaveConfig(existingConfig, gameInstallPath);
            Logger.Log($"[CustomLaunchTutorialService] Applied custom launch config for AppID {appId}: Exe='{existingConfig.ExecutablePath}', Args='{existingConfig.Arguments}'");
            return true;
        }

        /// <summary>
        /// Initial fallback tutorials for popular games requiring custom launch
        /// </summary>
        private static List<CustomLaunchTutorial> GetDefaultFallbackTutorials()
        {
            return new List<CustomLaunchTutorial>
            {
                new CustomLaunchTutorial
                {
                    AppId = 271590, // GTA V
                    GameTitle = "Grand Theft Auto V",
                    ApplicableTo = "ALL",
                    Summary = "This game requires a specific offline bypass launcher to run properly without triggering the Rockstar Games Launcher.",
                    RecommendedExecutable = "PlayGTAV.exe",
                    RecommendedArguments = "-offline -scofflineonly",
                    Steps = new List<TutorialStep>
                    {
                        new TutorialStep
                        {
                            StepNumber = 1,
                            Title = "Extract Bypass / Fix Files",
                            Description = "Ensure bypass/fix files are installed in the game's root directory."
                        },
                        new TutorialStep
                        {
                            StepNumber = 2,
                            Title = "Configure Custom Launch",
                            Description = "Set the target executable to PlayGTAV.exe (or click the 'Auto-Apply to Custom Launch' button)."
                        },
                        new TutorialStep
                        {
                            StepNumber = 3,
                            Title = "Launch the Game",
                            Description = "Launch the game using the PLAY button in Game Library or from the created desktop shortcut."
                        }
                    },
                    Notes = "Ensure you are not currently signed into the Rockstar Games Launcher while running in bypass mode."
                }
            };
        }
    }
}
