using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    /// <summary>
    /// Helper class untuk fetch game info dari Steam API dan manage HZ Manifest
    /// </summary>
    public static class SteamManifestHelper
    {
        private static readonly HttpClient _httpClient = new();
        private const string SteamApiUrl = "https://store.steampowered.com/api/appdetails";

        /// <summary>
        /// Extract AppID dari nama file (misal: "730_csgo.zip" → 730)
        /// </summary>
        public static int? ExtractAppIdFromFilename(string filename)
        {
            try
            {
                // Ambil bagian sebelum underscore atau titik
                string[] parts = Path.GetFileNameWithoutExtension(filename).Split('_');
                
                if (int.TryParse(parts[0], out int appId))
                {
                    return appId;
                }
            }
            catch
            {
                // Jika gagal parse, return null
            }
            
            return null;
        }

        /// <summary>
        /// Fetch game info dari Steam API
        /// </summary>
        public static async Task<SteamGameInfo?> GetGameInfoFromSteamAsync(int appId)
        {
            try
            {
                string url = $"{SteamApiUrl}?appids={appId}";
                var response = await _httpClient.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                    return null;

                string content = await response.Content.ReadAsStringAsync();
                var jsonDoc = JsonDocument.Parse(content);
                var root = jsonDoc.RootElement;

                // Navigate: {appId}.data
                if (root.TryGetProperty(appId.ToString(), out var appElement) &&
                    appElement.TryGetProperty("data", out var data))
                {
                    var info = new SteamGameInfo();

                    // Extract name
                    if (data.TryGetProperty("name", out var nameElement))
                    {
                        info.Name = nameElement.GetString();
                    }

                    // Extract short description
                    if (data.TryGetProperty("short_description", out var descElement))
                    {
                        info.Description = descElement.GetString();
                    }

                    // Extract genres
                    if (data.TryGetProperty("genres", out var genresArray) && 
                        genresArray.ValueKind == JsonValueKind.Array)
                    {
                        var genreList = new List<string>();
                        foreach (var genre in genresArray.EnumerateArray())
                        {
                            if (genre.TryGetProperty("description", out var genreDesc))
                            {
                                genreList.Add(genreDesc.GetString() ?? "");
                            }
                        }
                        info.Genre = string.Join(", ", genreList);
                    }

                    // Extract header image
                    if (data.TryGetProperty("header_image", out var imageElement))
                    {
                        info.ImageUrl = imageElement.GetString();
                    }

                    return info;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamManifestHelper] Error fetching Steam info for app {appId}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Fetch game info dengan retry logic
        /// </summary>
        public static async Task<SteamGameInfo?> GetGameInfoWithRetryAsync(int appId, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    var info = await GetGameInfoFromSteamAsync(appId);
                    if (info != null)
                        return info;

                    // Wait sebelum retry untuk avoid rate limit
                    if (i < maxRetries - 1)
                        await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SteamManifestHelper] Retry {i + 1} failed for app {appId}: {ex.Message}");
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Model untuk Steam game info
    /// </summary>
    public class SteamGameInfo
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Genre { get; set; }
        public string? ImageUrl { get; set; }
    }
}
