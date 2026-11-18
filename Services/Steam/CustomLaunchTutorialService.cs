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
        private static bool _isUsingDefaultFallbackTutorials;
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
                            _isUsingDefaultFallbackTutorials = false;
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
                            _isUsingDefaultFallbackTutorials = false;
                        }
                        return localList;
                    }
                }
            }
            catch { }

            lock (_lock)
            {
                if (_inMemoryTutorials == null)
                {
                    _inMemoryTutorials = GetDefaultFallbackTutorials();
                    _isUsingDefaultFallbackTutorials = true;
                }
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

        public static async Task<bool> ApplyAdminConfiguredTutorialToLocalGameAsync(
            int appId,
            string platform,
            string gameInstallPath,
            Func<bool>? confirmApply = null,
            Action<CustomLaunchTutorial>? onTutorialFound = null,
            Action<SteamLaunchProgress>? onProgress = null)
        {
            if (appId <= 0 || string.IsNullOrWhiteSpace(gameInstallPath) || !Directory.Exists(gameInstallPath))
            {
                Logger.Log($"[CustomLaunchTutorialService] Cannot apply admin launch options for AppID {appId}: game installation path is invalid.");
                return false;
            }

            var allTutorials = await GetAllTutorialsAsync(forceRefresh: true);
            bool isFallback;
            lock (_lock)
            {
                isFallback = _isUsingDefaultFallbackTutorials;
            }

            if (isFallback)
            {
                Logger.Log($"[CustomLaunchTutorialService] Cannot apply admin launch options for AppID {appId}: only built-in fallback tutorials are available.");
                return false;
            }

            var tutorial = allTutorials.FirstOrDefault(t =>
                t.AppId == appId &&
                (string.IsNullOrWhiteSpace(platform) ||
                 string.Equals(t.ApplicableTo, "ALL", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t.ApplicableTo, platform, StringComparison.OrdinalIgnoreCase)));

            if (tutorial == null)
            {
                Logger.Log($"[CustomLaunchTutorialService] No admin tutorial matches AppID {appId} and platform '{platform}'.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(tutorial.RecommendedExecutable))
            {
                Logger.Log($"[CustomLaunchTutorialService] Admin tutorial for AppID {appId} has no recommended executable; applying Steam launch options was skipped.");
                return false;
            }

            onTutorialFound?.Invoke(tutorial);

            if (confirmApply != null && !confirmApply())
                return false;

            bool applied = await ApplyTutorialToLocalGameAsync(appId, tutorial, gameInstallPath, onProgress);
            if (!applied)
                Logger.Log($"[CustomLaunchTutorialService] Failed to apply admin Steam launch options for AppID {appId}.");

            return applied;
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
                    _isUsingDefaultFallbackTutorials = false;
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
                    _isUsingDefaultFallbackTutorials = false;
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

        public static async Task<bool> ApplyTutorialToLocalGameAsync(
            int appId,
            CustomLaunchTutorial tutorial,
            string gameInstallPath,
            Action<SteamLaunchProgress>? onProgress = null)
        {
            if (appId <= 0 || tutorial == null || string.IsNullOrWhiteSpace(gameInstallPath) || !Directory.Exists(gameInstallPath))
                return false;

            string relativePath = (tutorial.RecommendedExecutable ?? string.Empty).Trim().Replace('/', '\\');
            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            string executablePath = Path.Combine(gameInstallPath, relativePath);
            if (!File.Exists(executablePath))
            {
                string exeName = Path.GetFileName(relativePath);
                try
                {
                    string? found = Directory.GetFiles(gameInstallPath, exeName, SearchOption.AllDirectories).FirstOrDefault();
                    if (found == null)
                    {
                        Logger.Log($"[CustomLaunchTutorialService] Tutorial executable was not found for AppID {appId}: {relativePath}");
                        onProgress?.Invoke(new SteamLaunchProgress(2, false, "Custom launch executable not found", $"Could not find '{relativePath}' in the game folder.", Failed: true));
                        return false;
                    }
                    executablePath = found;
                    relativePath = Path.GetRelativePath(gameInstallPath, found);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[CustomLaunchTutorialService] Could not resolve tutorial executable for AppID {appId}: {ex.Message}");
                    return false;
                }
            }

            try
            {
                var service = SteamLaunchOptionsService.Shared;
                var state = await Task.Run(() => service.Read(appId));
                if (state == null)
                {
                    Logger.Log($"[CustomLaunchTutorialService] Steam appinfo entry {appId} was not found.");
                    onProgress?.Invoke(new SteamLaunchProgress(2, false, "Steam launch data not found", $"Steam has no appinfo entry for AppID {appId}.", Failed: true));
                    return false;
                }

                var desired = service.GetStagedDesired(appId)?.Select(option => option.Copy()).ToList()
                    ?? state.Current.Select(option => option.Copy()).ToList();
                if (desired.Count == 0)
                    desired.Add(new SteamLaunchOption { Index = "0", Type = "default" });

                var launchCommand = SteamBatchLaunchCommand.Create(
                    executablePath,
                    relativePath,
                    tutorial.RecommendedArguments ?? string.Empty,
                    gameInstallPath);
                desired[0].Executable = launchCommand.Executable;
                desired[0].Arguments = launchCommand.Arguments;
                desired[0].WorkingDirectory = launchCommand.WorkingDirectory;
                desired[0].SourceIndex = state.Current.FirstOrDefault()?.SourceIndex ?? string.Empty;
                for (int i = 0; i < desired.Count; i++)
                    desired[i].Index = i.ToString();

                service.Stage(appId, state.ChangeNumber, state.Current, desired);
                var result = await Task.Run(() => service.ApplyStaged(appId, onProgress));
                if (!result.Success)
                {
                    Logger.Log($"[CustomLaunchTutorialService] Steam launch options were staged but not applied for AppID {appId}: {result.Error}");
                    return false;
                }

                Logger.Log($"[CustomLaunchTutorialService] Applied Steam launch options for AppID {appId}: executable='{launchCommand.Executable}', arguments='{launchCommand.Arguments}'.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomLaunchTutorialService] Could not apply Steam launch options for AppID {appId}: {ex}");
                onProgress?.Invoke(new SteamLaunchProgress(2, false, "Could not apply custom launch", ex.Message, Failed: true));
                return false;
            }
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
