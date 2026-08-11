using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    /// <summary>
    /// Helper to display game names with Steam API fallback
    /// Works around Supabase RLS limitation on 'name' column
    /// </summary>
    public class GameDisplayNameResolver
    {
        private const string STEAM_API_URL = "https://store.steampowered.com/api/appdetails";
        private static readonly HttpClient _client = new HttpClient();
        private static readonly Dictionary<int, string> _cache = new Dictionary<int, string>();

        /// <summary>
        /// Get display name for a game (from Steam API)
        /// </summary>
        public static async Task<string> GetGameDisplayNameAsync(int appId)
        {
            // Check cache first
            if (_cache.ContainsKey(appId))
                return _cache[appId];

            try
            {
                var url = $"{STEAM_API_URL}?appids={appId}&json=1";
                var response = await _client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using (var doc = JsonDocument.Parse(content))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty(appId.ToString(), out var appElement))
                        {
                            if (appElement.TryGetProperty("success", out var success) && success.GetBoolean())
                            {
                                if (appElement.TryGetProperty("data", out var data) && 
                                    data.TryGetProperty("name", out var nameEl))
                                {
                                    var name = nameEl.GetString();
                                    _cache[appId] = name;
                                    return name;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching Steam name for {appId}: {ex.Message}");
            }

            // Fallback
            return $"App {appId}";
        }

        /// <summary>
        /// Get display name (sync version, uses cached value or fallback)
        /// </summary>
        public static string GetGameDisplayName(int appId, string dbName = null)
        {
            // If we have a cached value, use it
            if (_cache.ContainsKey(appId))
                return _cache[appId];

            // If DB name is not "App XXXXX", use it (may have been updated)
            if (!string.IsNullOrEmpty(dbName) && !dbName.StartsWith("App "))
                return dbName;

            // Otherwise, return fallback or fetch async
            return dbName ?? $"App {appId}";
        }

        /// <summary>
        /// Pre-warm cache with popular AppIDs
        /// </summary>
        public static async Task PreloadPopularGamesAsync()
        {
            var popularAppIds = new int[]
            {
                264710,  // Subnautica
                413150,  // Stardew Valley
                851850,  // DRAGON BALL Z: KAKAROT
                2909400, // FINAL FANTASY VII REBIRTH
                2490990, // Visions of Mana
                1371980, // No Rest for the Wicked
            };

            var tasks = popularAppIds.Select(id => GetGameDisplayNameAsync(id)).ToList();
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Clear cache
        /// </summary>
        public static void ClearCache()
        {
            _cache.Clear();
        }
    }
}
