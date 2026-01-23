using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SteamPluginManager.Helpers;

namespace SteamPluginManager
{
    public static class CacheManager
    {
        internal static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamPluginManager",
            "Cache");
        
        private static readonly string CacheFile = Path.Combine(CacheDirectory, "gameCache.json");
        
        private static Dictionary<int, CacheItem>? _cache;
        private static bool _initialized = false;

        public static void Initialize()
        {
            if (_initialized) return;
            
            try
            {
                // Pastikan direktori cache ada
                Directory.CreateDirectory(CacheDirectory);
                
                // Load cache dari file jika ada
                if (File.Exists(CacheFile))
                {
                    try
                    {
                        byte[] fileBytes = File.ReadAllBytes(CacheFile);
                        Dictionary<int, CacheItem>? loaded = null;
                        bool loadedOk = false;

                        // Try decrypt first
                        try
                        {
                            byte[] plain = SecureCache.Unprotect(fileBytes);
                            string json = Encoding.UTF8.GetString(plain);
                            loaded = JsonSerializer.Deserialize<Dictionary<int, CacheItem>>(json);
                            loadedOk = loaded != null;
                        }
                        catch
                        {
                            // Decrypt failed — we'll try plaintext fallback
                        }

                        // Fallback to plaintext JSON (migrate to encrypted)
                        if (!loadedOk)
                        {
                            try
                            {
                                string json = Encoding.UTF8.GetString(fileBytes);
                                loaded = JsonSerializer.Deserialize<Dictionary<int, CacheItem>>(json);
                                if (loaded != null)
                                {
                                    _cache = loaded;
                                    SaveCacheToFile(); // will write encrypted copy
                                    Logger.Log("Migrated plaintext cache to encrypted cache");
                                }
                            }
                            catch
                            {
                                // ignore; will be handled below
                            }
                        }

                        if (loadedOk || _cache != null)
                        {
                            if (_cache == null) _cache = loaded ?? new Dictionary<int, CacheItem>();
                            Logger.Log($"Cache loaded with {(_cache?.Count ?? 0)} items");
                        }
                        else
                        {
                            _cache = new Dictionary<int, CacheItem>();
                            Logger.Log("No valid cache content found, started with empty cache");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to load cache: {ex.Message}");
                        _cache = new Dictionary<int, CacheItem>();
                    }
                }
                else
                {
                    _cache = new Dictionary<int, CacheItem>();
                    Logger.Log("No cache file found, starting with empty cache");
                }
                
                _initialized = true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Cache initialization failed: {ex.Message}");
                _cache = new Dictionary<int, CacheItem>();
                _initialized = true;
            }
        }

        public static string? GetGameName(int appId, string language = "en")
        {
            if (!_initialized) Initialize();
            if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
            {
                // Periksa apakah cache masih valid (30 hari untuk portable)
                if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(30))
                {
                    // Support legacy cache shape and treat empty as missing
                    string candidate = language == "zh"
                        ? (string.IsNullOrWhiteSpace(item.GameNameZh) ? item.GameName : item.GameNameZh)
                        : (string.IsNullOrWhiteSpace(item.GameNameEn) ? item.GameName : item.GameNameEn);
                    return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
                }
                else
                {
                    // Cache expired, hapus dari cache
                    _cache.Remove(appId);
                    SaveCacheToFile();
                }
            }
            return null;
        }

        public static string? GetGameGenre(int appId, string language = "en")
        {
            if (!_initialized) Initialize();
            if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
            {
                // Periksa apakah cache masih valid (30 hari untuk portable)
                if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(30))
                {
                    string candidate = language == "zh"
                        ? (string.IsNullOrWhiteSpace(item.GenreZh) ? item.Genre : item.GenreZh)
                        : (string.IsNullOrWhiteSpace(item.GenreEn) ? item.Genre : item.GenreEn);
                    return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
                }
                else
                {
                    // Cache expired, hapus dari cache
                    _cache.Remove(appId);
                    SaveCacheToFile();
                }
            }
            return null;
        }

        public static void SaveGameName(int appId, string gameName, string language = "en")
        {
            if (!_initialized) Initialize();
            if (_cache == null) return;
            
            if (_cache.TryGetValue(appId, out CacheItem? existingItem))
            {
                // Update existing item
                if (language == "zh") existingItem.GameNameZh = gameName; else existingItem.GameNameEn = gameName;
                existingItem.LastUpdated = DateTime.Now;
            }
            else
            {
                // Create new item
                _cache[appId] = new CacheItem
                {
                    GameNameEn = language == "en" ? gameName : "",
                    GameNameZh = language == "zh" ? gameName : "",
                    LastUpdated = DateTime.Now
                };
            }
            
            SaveCacheToFile();
        }

        public static void SaveGameData(int appId, string gameName, string genre, string language = "en")
        {
            if (!_initialized) Initialize();
            if (_cache == null) return;
            
            if (!_cache.TryGetValue(appId, out var item))
            {
                item = new CacheItem();
                _cache[appId] = item;
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
            
            SaveCacheToFile();
        }

        public static void SaveGameDetails(int appId, string description, string screenshotUrl, string releaseDate, string publisher, string drm, string antiCheat, string language = "en")
        {
            if (!_initialized) Initialize();
            if (_cache == null) return;
            
            if (!_cache.TryGetValue(appId, out var item))
            {
                item = new CacheItem();
                _cache[appId] = item;
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
            SaveCacheToFile();
        }

        public static string? GetGameDescription(int appId, string language = "en")
        {
            if (!_initialized) Initialize();
            if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
            {
                if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(30))
                {
                    string candidate = language == "zh"
                        ? (string.IsNullOrWhiteSpace(item.DescriptionZh) ? item.Description : item.DescriptionZh)
                        : (string.IsNullOrWhiteSpace(item.DescriptionEn) ? item.Description : item.DescriptionEn);
                    return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
                }
            }
            return null;
        }

        public static string? GetGameScreenshotUrl(int appId)
        {
            if (!_initialized) Initialize();
            if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
            {
                if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(30))
                {
                    return string.IsNullOrWhiteSpace(item.ScreenshotUrl) ? null : item.ScreenshotUrl;
                }
            }
            return null;
        }

        public static string? GetGameReleaseDate(int appId)
        {
            if (!_initialized) Initialize();
            if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
            {
                if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(30))
                {
                    return string.IsNullOrWhiteSpace(item.ReleaseDate) ? null : item.ReleaseDate;
                }
            }
            return null;
        }

        public static string? GetGamePublisher(int appId)
        {
            if (!_initialized) Initialize();
            if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
            {
                if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(30))
                {
                    return string.IsNullOrWhiteSpace(item.Publisher) ? null : item.Publisher;
                }
            }
            return null;
        }

        public static string? GetGameDRM(int appId)
        {
            if (!_initialized) Initialize();
            if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
            {
                if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(30))
                {
                    return string.IsNullOrWhiteSpace(item.DRM) ? null : item.DRM;
                }
            }
            return null;
        }

        public static string? GetGameAntiCheat(int appId)
        {
            if (!_initialized) Initialize();
            if (_cache != null && _cache.TryGetValue(appId, out CacheItem? item))
            {
                if (DateTime.Now - item.LastUpdated < TimeSpan.FromDays(30))
                {
                    return string.IsNullOrWhiteSpace(item.AntiCheat) ? null : item.AntiCheat;
                }
            }
            return null;
        }

        private static void SaveCacheToFile()
        {
            // Simpan cache ke file
            try
            {
                string json = JsonSerializer.Serialize(_cache);
                byte[] plain = Encoding.UTF8.GetBytes(json);
                byte[] encrypted = SecureCache.Protect(plain);
                File.WriteAllBytes(CacheFile, encrypted);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save cache: {ex.Message}");
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
            if (File.Exists(CacheFile))
            {
                try
                {
                    File.Delete(CacheFile);
                    Logger.Log("Cache file deleted");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to delete cache file: {ex.Message}");
                }
            }
            _cache = new Dictionary<int, CacheItem>();
            Logger.Log("Cache cleared in memory");
        }

        public static int GetCacheSize()
        {
            return _cache?.Count ?? 0;
        }
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

    public static class Logger
    {
        public static void Log(string message)
        {
            // Logger sederhana untuk debug
            string logFile = Path.Combine(CacheManager.CacheDirectory, "app.log");
            try
            {
                File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
            catch
            {
                // Jika gagal menulis log, abaikan
            }
        }
    }
}