using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    public static class CacheManager
    {
        internal static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamPluginManager",
            "Cache");
        
        private static readonly string CacheFile = Path.Combine(CacheDirectory, "gameCache.json");
        private static readonly string HzmCacheFile = Path.Combine(CacheDirectory, "hzmCache.json");
        private static readonly string ManifestStateFile = Path.Combine(CacheDirectory, "manifest_state.json");
        private static readonly string PendingRequestsFile = Path.Combine(CacheDirectory, "pending_requests.json");

        // Cache expiration settings (in hours)
        private const int DefaultHzmCacheExpirationsHours = 24; // 24 hours default cache duration

        private static ConcurrentDictionary<int, CacheItem>? _cache;
        private static ConcurrentDictionary<string, HzmCacheItem>? _hzmCache;
        private static ConcurrentDictionary<int, WebhookRequestData>? _pendingRequests;

        private static bool _initialized = false;
        private static readonly object _lockObj = new object();

        public static void Initialize()
        {
            if (_initialized) return;

            lock (_lockObj)
            {
                if (_initialized) return; // Double-check locking pattern

                try
                {
                    // Pastikan direktori cache ada
                    Directory.CreateDirectory(CacheDirectory);

                    // Load game cache from file if it exists
                    if (File.Exists(CacheFile))
                    {
                        try
                        {
                            string json = File.ReadAllText(CacheFile, Encoding.UTF8);
                            var loaded = JsonSerializer.Deserialize<Dictionary<int, CacheItem>>(json);

                            if (loaded != null)
                            {
                                _cache = new ConcurrentDictionary<int, CacheItem>(loaded);
                                foreach (var item in _cache.Values)
                                {
                                    if (item.LastUpdated == DateTime.MinValue || item.LastUpdated > DateTime.Now)
                                    {
                                        item.LastUpdated = DateTime.Now;
                                    }
                                }
                                Logger.Log($"Game cache loaded with {_cache.Count} items");
                            }
                            else
                            {
                                _cache = new ConcurrentDictionary<int, CacheItem>();
                                Logger.Log("Game cache file is empty, started with empty cache");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Failed to load game cache: {ex.Message}");
                            _cache = new ConcurrentDictionary<int, CacheItem>();
                        }
                    }
                    else
                    {
                        _cache = new ConcurrentDictionary<int, CacheItem>();
                        Logger.Log("No game cache file found, starting with empty game cache");
                    }

                    // Load HZM cache from file if it exists
                    if (File.Exists(HzmCacheFile))
                    {
                        try
                        {
                            string json = File.ReadAllText(HzmCacheFile, Encoding.UTF8);
                            var loaded = JsonSerializer.Deserialize<Dictionary<string, HzmCacheItem>>(json);

                            if (loaded != null)
                            {
                                _hzmCache = new ConcurrentDictionary<string, HzmCacheItem>(loaded);
                                Logger.Log($"HZM cache loaded with {_hzmCache.Count} items");
                            }
                            else
                            {
                                _hzmCache = new ConcurrentDictionary<string, HzmCacheItem>();
                                Logger.Log("HZM cache file is empty, started with empty cache");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Failed to load HZM cache: {ex.Message}");
                            _hzmCache = new ConcurrentDictionary<string, HzmCacheItem>();
                        }
                    }
                    else
                    {
                        _hzmCache = new ConcurrentDictionary<string, HzmCacheItem>();
                        Logger.Log("No HZM cache file found, starting with empty HZM cache");
                    }

                    // Load pending requests from file if it exists
                    if (File.Exists(PendingRequestsFile))
                    {
                        try
                        {
                            string json = File.ReadAllText(PendingRequestsFile, Encoding.UTF8);
                            var loaded = JsonSerializer.Deserialize<Dictionary<int, WebhookRequestData>>(json);
                            if (loaded != null)
                            {
                                _pendingRequests = new ConcurrentDictionary<int, WebhookRequestData>(loaded);
                                Logger.Log($"Pending requests loaded: {_pendingRequests.Count} items");
                            }
                            else
                            {
                                _pendingRequests = new ConcurrentDictionary<int, WebhookRequestData>();
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Failed to load pending requests: {ex.Message}");
                            _pendingRequests = new ConcurrentDictionary<int, WebhookRequestData>();
                        }
                    }
                    else
                    {
                        _pendingRequests = new ConcurrentDictionary<int, WebhookRequestData>();
                    }

                    _initialized = true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Cache initialization failed: {ex.Message}");
                    _cache = new ConcurrentDictionary<int, CacheItem>();
                    _hzmCache = new ConcurrentDictionary<string, HzmCacheItem>();
                    _initialized = true;
                }
            }
        }

        public static string? GetHzmCache(string manifestId)
        {
            if (!_initialized) Initialize();

            if (_hzmCache != null && _hzmCache.TryGetValue(manifestId, out var cacheItem))
            {
                // Check cache expiration (default 24 hours)
                if (DateTime.Now - cacheItem.LastUpdated < TimeSpan.FromHours(DefaultHzmCacheExpirationsHours))
                {
                    Logger.Log($"Cache hit for {manifestId}. Valid for {(DateTime.Now - cacheItem.LastUpdated).TotalHours:F1} hours more");
                    return cacheItem.Content;
                }
                else
                {
                    // Cache expired, remove it
                    Logger.Log($"Cache expired for {manifestId}. Removing from cache");
                    _hzmCache.TryRemove(manifestId, out _);
                    SaveHzmCacheToFile();
                }
            }

            return null;
        }

        public static ManifestState GetLastSeenManifestState()
        {
            if (!_initialized) Initialize();
            try
            {
                if (!File.Exists(ManifestStateFile))
                    return new ManifestState
                    {
                        LastSeenManifestCount = -1,
                        LastSeenManifestIds = new List<int>(),
                        LastUpdatedUtc = DateTime.MinValue
                    };

                string json = File.ReadAllText(ManifestStateFile, Encoding.UTF8);
                var state = JsonSerializer.Deserialize<ManifestState>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return state ?? new ManifestState
                {
                    LastSeenManifestCount = -1,
                    LastSeenManifestIds = new List<int>(),
                    LastUpdatedUtc = DateTime.MinValue
                };
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to read manifest state: {ex.Message}");
                return new ManifestState
                {
                    LastSeenManifestCount = -1,
                    LastSeenManifestIds = new List<int>(),
                    LastUpdatedUtc = DateTime.MinValue
                };
            }
        }

        public static void SetLastSeenManifestState(ManifestState state)
        {
            if (!_initialized) Initialize();
            try
            {
                state.LastUpdatedUtc = DateTime.UtcNow;
                state.LastSeenManifestIds ??= new List<int>();

                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ManifestStateFile, json, Encoding.UTF8);
                Logger.Log($"Saved manifest state: Count={state.LastSeenManifestCount}, IDs={state.LastSeenManifestIds.Count}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save manifest state: {ex.Message}");
            }
        }

        public static int GetLastSeenManifestCount()
        {
            return GetLastSeenManifestState().LastSeenManifestCount;
        }

        public static void SetLastSeenManifestCount(int count)
        {
            var existingState = GetLastSeenManifestState();
            existingState.LastSeenManifestCount = count;
            SetLastSeenManifestState(existingState);
        }

        public static void SaveHzmCache(string manifestId, string content)
        {
            if (!_initialized) Initialize();
            if (_hzmCache == null) return;

            var newItem = new HzmCacheItem
            {
                Content = content,
                LastUpdated = DateTime.Now
            };

            _hzmCache.AddOrUpdate(manifestId, newItem, (k, v) => newItem);
            SaveHzmCacheToFile();
            Logger.Log($"HZM cache saved for {manifestId} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }

        /// <summary>
        /// Clears the HZM cache for a specific manifest ID
        /// </summary>
        public static void ClearHzmCache(string manifestId)
        {
            if (!_initialized) Initialize();
            if (_hzmCache == null) return;

            if (_hzmCache.TryRemove(manifestId, out _))
            {
                SaveHzmCacheToFile();
                Logger.Log($"HZM cache cleared for {manifestId}");
            }
        }

        /// <summary>
        /// Clears all HZM cache entries
        /// </summary>
        public static void ClearAllHzmCache()
        {
            if (!_initialized) Initialize();
            if (_hzmCache == null) return;

            _hzmCache.Clear();
            SaveHzmCacheToFile();
            Logger.Log("All HZM cache cleared");
        }

        /// <summary>
        /// Gets cache status including size and expiration info
        /// </summary>
        public static string GetHzmCacheStatus(string manifestId)
        {
            if (!_initialized) Initialize();

            if (_hzmCache == null || !_hzmCache.TryGetValue(manifestId, out var cacheItem))
            {
                return $"No cache found for {manifestId}";
            }

            var timeElapsed = DateTime.Now - cacheItem.LastUpdated;
            var timeRemaining = TimeSpan.FromHours(DefaultHzmCacheExpirationsHours) - timeElapsed;

            if (timeRemaining.TotalHours <= 0)
            {
                return $"Cache expired for {manifestId}. Last updated: {cacheItem.LastUpdated:yyyy-MM-dd HH:mm:ss}";
            }

            return $"Cache valid for {manifestId}. Expires in: {timeRemaining.TotalHours:F1} hours. Size: {cacheItem.Content.Length} bytes";
        }

        /// <summary>
        /// Cleanup old thumbnail files (storage optimization)
        /// </summary>
        public static void CleanupOldThumbnails(int daysOld = 30)
        {
            try
            {
                string thumbnailCacheFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SteamPluginManager", "ThumbnailCache");
                
                if (!Directory.Exists(thumbnailCacheFolder))
                    return;
                
                // Find old files (not accessed in X days)
                var oldFiles = Directory.GetFiles(thumbnailCacheFolder)
                    .Where(f => File.GetLastAccessTime(f) < DateTime.Now.AddDays(-daysOld))
                    .ToList();
                
                foreach (var file in oldFiles)
                {
                    try
                    {
                        File.Delete(file);
                        Logger.Log($"Deleted old thumbnail: {Path.GetFileName(file)}");
                    }
                    catch { }
                }
                
                if (oldFiles.Count > 0)
                {
                    Logger.Log($"Thumbnail cleanup completed. Removed {oldFiles.Count} old thumbnails");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Thumbnail cleanup error: {ex.Message}");
            }
        }

        private static void SaveHzmCacheToFile()
        {
            try
            {
                lock (_lockObj)
                {
                    if (_hzmCache == null || _hzmCache.Count == 0) return;

                    var cacheCopy = new Dictionary<string, HzmCacheItem>(_hzmCache);
                    string json = JsonSerializer.Serialize(cacheCopy, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(HzmCacheFile, json, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save HZM cache: {ex.Message}");
            }
        }


        public static string? GetGameName(int appId, string language = "en")
        {
            if (!_initialized) Initialize();
            
            lock (_lockObj)
            {
                if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
                {
                    // Periksa apakah cache masih valid (365 hari untuk extended cache retention)
                    if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(365))
                    {
                        // Support legacy cache shape and treat empty as missing
                        string candidate = language == "zh"
                            ? (string.IsNullOrWhiteSpace(item.GameNameZh) ? item.GameName : item.GameNameZh)
                            : (string.IsNullOrWhiteSpace(item.GameNameEn) ? item.GameName : item.GameNameEn);
                        
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            Logger.Log($"Cache hit for game {appId} ({language})");
                            return candidate;
                        }
                    }
                    else
                    {
                        // Cache expired, hapus dari cache
                        Logger.Log($"Cache expired for game {appId}, removing from cache");
                        _cache.TryRemove(appId, out _);
                    }
                }
            }
            
            return null;
        }

        public static string? GetGameGenre(int appId, string language = "en")
        {
            if (!_initialized) Initialize();
            
            lock (_lockObj)
            {
                if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
                {
                    // Periksa apakah cache masih valid (365 hari untuk extended cache retention)
                    if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(365))
                    {
                        string candidate = language == "zh"
                            ? (string.IsNullOrWhiteSpace(item.GenreZh) ? item.Genre : item.GenreZh)
                            : (string.IsNullOrWhiteSpace(item.GenreEn) ? item.Genre : item.GenreEn);
                        
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            Logger.Log($"Cache hit for genre {appId} ({language})");
                            return candidate;
                        }
                    }
                    else
                    {
                        // Cache expired, hapus dari cache
                        Logger.Log($"Cache expired for game {appId}, removing from cache");
                        _cache.TryRemove(appId, out _);
                    }
                }
            }
            return null;
        }

        public static void SaveGameName(int appId, string gameName, string language = "en")
        {
            if (!_initialized) Initialize();
            if (_cache == null) return;
            
            lock (_lockObj)
            {
                if (_cache.TryGetValue(appId, out CacheItem? existingItem))
                {
                    // Update existing item
                    if (language == "zh") existingItem.GameNameZh = gameName; else existingItem.GameNameEn = gameName;
                    existingItem.LastUpdated = DateTime.Now;
                }
                else
                {
                    // Create new item - use AddOrUpdate untuk ConcurrentDictionary
                    var newItem = new CacheItem
                    {
                        GameNameEn = language == "en" ? gameName : "",
                        GameNameZh = language == "zh" ? gameName : "",
                        LastUpdated = DateTime.Now
                    };
                    _cache.AddOrUpdate(appId, newItem, (k, v) => newItem);
                }
            }
            
            SaveCacheToFile();
        }

        public static void SaveGameData(int appId, string gameName, string genre, string language = "en")
        {
            if (!_initialized) Initialize();
            if (_cache == null) return;
            
            lock (_lockObj)
            {
                if (!_cache.TryGetValue(appId, out var item))
                {
                    item = new CacheItem();
                    _cache.AddOrUpdate(appId, item, (k, v) => item);
                }

                if (language == "zh")
                {
                    item.GameNameZh = gameName;
                    item.GenreZh = genre;
                }
                else
                {
                    item.GameNameEn = gameName;
                    item.GenreEn = genre;
                }
                item.LastUpdated = DateTime.Now;
            }
            
            SaveCacheToFile();
        }

        public static void SaveGameDetails(int appId, string description, string screenshotUrl, string releaseDate, string publisher, string drm, string antiCheat, string language = "en")
        {
            if (!_initialized) Initialize();
            if (_cache == null) return;
            
            lock (_lockObj)
            {
                if (!_cache.TryGetValue(appId, out var item))
                {
                    item = new CacheItem();
                    _cache.AddOrUpdate(appId, item, (k, v) => item);
                }

                if (language == "zh")
                {
                    item.DescriptionZh = description;
                }
                else
                {
                    item.DescriptionEn = description;
                }
                
                // These are language-agnostic, so we can just update them
                item.ScreenshotUrl = screenshotUrl;
                item.ReleaseDate = releaseDate;
                item.Publisher = publisher;
                item.DRM = drm;
                item.AntiCheat = antiCheat;
                
                item.LastUpdated = DateTime.Now;
            }
            SaveCacheToFile();
        }

        public static string? GetGameDescription(int appId, string language = "en")
        {
            if (!_initialized) Initialize();
            
            lock (_lockObj)
            {
                if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
                {
                    if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(365))
                    {
                        string candidate = language == "zh"
                            ? (string.IsNullOrWhiteSpace(item.DescriptionZh) ? item.Description : item.DescriptionZh)
                            : (string.IsNullOrWhiteSpace(item.DescriptionEn) ? item.Description : item.DescriptionEn);
                        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
                    }
                }
            }
            return null;
        }

        public static string? GetGameScreenshotUrl(int appId)
        {
            if (!_initialized) Initialize();
            
            lock (_lockObj)
            {
                if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
                {
                    if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(365))
                    {
                        return string.IsNullOrWhiteSpace(item.ScreenshotUrl) ? null : item.ScreenshotUrl;
                    }
                }
            }
            return null;
        }

        public static string? GetGameReleaseDate(int appId)
        {
            if (!_initialized) Initialize();
            
            lock (_lockObj)
            {
                if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
                {
                    if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(365))
                    {
                        return string.IsNullOrWhiteSpace(item.ReleaseDate) ? null : item.ReleaseDate;
                    }
                }
            }
            return null;
        }

        public static string? GetGamePublisher(int appId)
        {
            if (!_initialized) Initialize();
            
            lock (_lockObj)
            {
                if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
                {
                    if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(365))
                    {
                        return string.IsNullOrWhiteSpace(item.Publisher) ? null : item.Publisher;
                    }
                }
            }
            return null;
        }

        public static string? GetGameDRM(int appId)
        {
            if (!_initialized) Initialize();
            
            lock (_lockObj)
            {
                if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
                {
                    if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(365))
                    {
                        return string.IsNullOrWhiteSpace(item.DRM) ? null : item.DRM;
                    }
                }
            }
            return null;
        }

        public static string? GetGameAntiCheat(int appId)
        {
            if (!_initialized) Initialize();
            
            lock (_lockObj)
            {
                if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
                {
                    if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(365))
                    {
                        return string.IsNullOrWhiteSpace(item.AntiCheat) ? null : item.AntiCheat;
                    }
                }
            }
            return null;
        }

        private static void SaveCacheToFile()
        {
            // Simpan cache ke file (plaintext JSON - no encryption) dengan thread-safe access
            try
            {
                lock (_lockObj)
                {
                    if (_cache == null || _cache.Count == 0) return;
                    
                    // Create snapshot untuk prevent concurrent modification exception
                    var cacheCopy = new Dictionary<int, CacheItem>(_cache);
                    string json = JsonSerializer.Serialize(cacheCopy, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(CacheFile, json, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save cache: {ex.Message}");
            }
        }

        public static void ClearHzmCache()
        {
            if (!_initialized) Initialize();

            lock (_lockObj)
            {
                if (_hzmCache != null)
                {
                    _hzmCache.Clear();
                    Logger.Log("HZM cache cleared.");
                }

                if (File.Exists(HzmCacheFile))
                {
                    try
                    {
                        File.Delete(HzmCacheFile);
                        Logger.Log("HZM cache file deleted.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to delete HZM cache file: {ex.Message}");
                    }
                }
            }
        }

        public static async Task<string?> GetOrFetchGameNameAsync(int appId, Func<Task<string?>> fetchFunction)
        {
            if (!_initialized) Initialize();
            
            // Coba dapatkan dari cache dulu
            string? cachedName = GetGameName(appId);
            if (cachedName != null)
            {
                return cachedName;
            }
            
            // Jika tidak ada di cache, fetch dari API
            string? gameName = await fetchFunction();
            
            // Simpan ke cache
            if (gameName != null && !gameName.StartsWith("Game ") && !gameName.Contains("Failed to load"))
            {
                SaveGameName(appId, gameName);
            }
            
            return gameName;
        }

        public static void ClearCache()
        {
            // ClearCache now removes only expired or incomplete items (not all cache)
            if (!_initialized) Initialize();
            
            lock (_lockObj)
            {
                if (_cache == null || _cache.Count == 0)
                {
                    Logger.Log("Cache is empty, nothing to clear");
                    return;
                }
                
                int removedCount = 0;
                var itemsToRemove = new List<int>();
                
                // Identify expired or incomplete items
                foreach (var kvp in _cache)
                {
                    var item = kvp.Value;
                    bool isExpired = DateTime.Now - item.LastUpdated >= TimeSpan.FromDays(365);
                    bool isIncomplete = string.IsNullOrWhiteSpace(item.GameNameEn) && 
                                       string.IsNullOrWhiteSpace(item.GameName) &&
                                       string.IsNullOrWhiteSpace(item.GameNameZh);
                    
                    if (isExpired || isIncomplete)
                    {
                        itemsToRemove.Add(kvp.Key);
                    }
                }
                
                // Remove identified items
                foreach (var appId in itemsToRemove)
                {
                    _cache.TryRemove(appId, out _);
                    removedCount++;
                }
                
                Logger.Log($"Cache cleared: removed {removedCount} expired/incomplete items, kept {_cache.Count} valid items");
            }
            
            // Save cache to preserve remaining items
            SaveCacheToFile();
        }

        public static int GetCacheSize()
        {
            if (_cache == null) return 0;
            
            lock (_lockObj)
            {
                return _cache?.Count ?? 0;
            }
        }

        // Pending Webhook Requests Management
        public static void SavePendingRequest(int appId, string messageId, string webhookUrl, string gameName)
        {
            if (!_initialized) Initialize();
            if (_pendingRequests == null) return;

            var requestData = new WebhookRequestData
            {
                AppId = appId,
                MessageId = messageId,
                WebhookUrl = webhookUrl,
                GameName = gameName,
                CreatedAt = DateTime.UtcNow
            };

            _pendingRequests.AddOrUpdate(appId, requestData, (k, v) => requestData);
            SavePendingRequestsToFile();
            Logger.Log($"Saved pending request for AppID {appId} with MessageID {messageId}");
        }

        public static WebhookRequestData? GetPendingRequest(int appId)
        {
            if (!_initialized) Initialize();
            if (_pendingRequests != null && _pendingRequests.TryGetValue(appId, out var request))
            {
                return request;
            }
            return null;
        }

        public static bool RemovePendingRequest(int appId)
        {
            if (!_initialized) Initialize();
            if (_pendingRequests != null && _pendingRequests.TryRemove(appId, out _))
            {
                SavePendingRequestsToFile();
                Logger.Log($"Removed pending request for AppID {appId}");
                return true;
            }
            return false;
        }

        public static List<WebhookRequestData> GetAllPendingRequests()
        {
            if (!_initialized) Initialize();
            if (_pendingRequests == null) return new List<WebhookRequestData>();
            return _pendingRequests.Values.ToList();
        }

        private static void SavePendingRequestsToFile()
        {
            try
            {
                lock (_lockObj)
                {
                    if (_pendingRequests == null || _pendingRequests.Count == 0)
                    {
                        if (File.Exists(PendingRequestsFile))
                            File.Delete(PendingRequestsFile);
                        return;
                    }

                    var copy = new Dictionary<int, WebhookRequestData>(_pendingRequests);
                    string json = JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(PendingRequestsFile, json, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save pending requests: {ex.Message}");
            }
        }
    }

    public class WebhookRequestData
    {
        public int AppId { get; set; }
        public string MessageId { get; set; } = "";
        public string WebhookUrl { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string GameName { get; set; } = "";
    }

    public class CacheItem
    {
        // Legacy fields for backward compatibility
        public string GameName { get; set; } = "";
        public string Genre { get; set; } = "";
        // New per-language fields
        public string GameNameEn { get; set; } = "";
        public string GenreEn { get; set; } = "";
        public string GameNameZh { get; set; } = "";
        public string GenreZh { get; set; } = "";
        // Detail fields (language-agnostic or use first available)
        public string Description { get; set; } = "";
        public string DescriptionEn { get; set; } = "";
        public string DescriptionZh { get; set; } = "";
        public string ScreenshotUrl { get; set; } = "";
        public string ReleaseDate { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string DRM { get; set; } = "";
        public string AntiCheat { get; set; } = "";
        public DateTime LastUpdated { get; set; }
    }

    public class HzmCacheItem
    {
        public string Content { get; set; } = "";
        public DateTime LastUpdated { get; set; }
    }

    public class ManifestState
    {
        public int LastSeenManifestCount { get; set; }
        public List<int> LastSeenManifestIds { get; set; } = new List<int>();
        public DateTime LastUpdatedUtc { get; set; }
    }

    public static class Logger
    {
        public static void Log(string message)
        {
            // Logger sederhana untuk debug
            string logFile = Path.Combine(CacheManager.CacheDirectory, "app.log");
            string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            
            try
            {
                // Write to file
                File.AppendAllText(logFile, logMessage + "\n");
            }
            catch
            {
                // Jika gagal menulis log, abaikan
            }

            // Also output to debug console
            System.Diagnostics.Debug.WriteLine(logMessage);
            Console.WriteLine(logMessage);
        }
    }
}
