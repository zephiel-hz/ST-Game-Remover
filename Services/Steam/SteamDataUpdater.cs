using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;

namespace SteamPluginManager
{
    /// <summary>
    /// Fix for Supabase RLS issue - direct table update utility
    /// </summary>
    public class SteamDataUpdater
    {
        private static readonly string SUPABASE_URL = SupabaseConfig.SupabaseUrl;
        private static readonly string SERVICE_ROLE_KEY = SupabaseConfig.SupabaseKey;
        private const string STEAM_API_URL = "https://store.steampowered.com/api/appdetails";

        private static readonly HttpClient _client = new HttpClient();

        public class GameInfo
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string Genre { get; set; }
        }

        public class Record
        {
            public int Id { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("app_id")]
            public int AppId { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("file_name")]
            public string FileName { get; set; }
            public string Name { get; set; }
            public string? Description { get; set; }
            public string? Genre { get; set; }
        }

        /// <summary>
        /// Get game info from Steam API
        /// </summary>
        public static async Task<GameInfo> GetSteamGameInfoAsync(int appId)
        {
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
                            if (appElement.TryGetProperty("success", out var successElement) && successElement.GetBoolean())
                            {
                                if (appElement.TryGetProperty("data", out var dataElement))
                                {
                                    var name = dataElement.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "";
                                    var description = dataElement.TryGetProperty("short_description", out var descEl) ? descEl.GetString() : "";

                                    var genres = "";
                                    if (dataElement.TryGetProperty("genres", out var genresArr))
                                    {
                                        var genreList = new List<string>();
                                        foreach (var genre in genresArr.EnumerateArray())
                                        {
                                            if (genre.TryGetProperty("description", out var genreDesc))
                                            {
                                                genreList.Add(genreDesc.GetString());
                                            }
                                        }
                                        genres = string.Join(",", genreList);
                                    }

                                    return new GameInfo { Name = name, Description = description, Genre = genres };
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] GetSteamGameInfoAsync: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Get all records from Supabase table
        /// </summary>
        public static async Task<List<Record>> GetAllRecordsAsync()
        {
            try
            {
                // Fetch all fields needed for metadata display
                var url = $"{SUPABASE_URL}/rest/v1/hzmanifest_files?select=id,app_id,file_name,name,description,genre";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SERVICE_ROLE_KEY);

                var response = await _client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<List<Record>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] GetAllRecordsAsync: {ex.Message}");
            }

            return new List<Record>();
        }

        /// <summary>
        /// Try PATCH update on a single record
        /// </summary>
        public static async Task<bool> PatchRecordAsync(int recordId, string name, string description, string genre)
        {
            try
            {
                var url = $"{SUPABASE_URL}/rest/v1/hzmanifest_files?id=eq.{recordId}";
                var data = new { name, description, genre };
                var json = JsonSerializer.Serialize(data);

                var request = new HttpRequestMessage(HttpMethod.Patch, url)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.Add("apikey", SERVICE_ROLE_KEY);

                var response = await _client.SendAsync(request);
                Console.WriteLine($"  [PATCH] ID {recordId}: Status {(int)response.StatusCode}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [PATCH ERROR] {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Main update routine
        /// </summary>
        public static async Task UpdateAllRecordsAsync()
        {
            Console.WriteLine("=== SteamDataUpdater (C#) ===\n");

            var records = await GetAllRecordsAsync();
            Console.WriteLine($"[LOAD] Found {records.Count} records\n");

            int success = 0, failed = 0;

            for (int i = 0; i < Math.Min(records.Count, 5); i++)  // Test first 5
            {
                var record = records[i];
                Console.Write($"[{i + 1}/{records.Count}] AppID {record.AppId}: ");

                var steamInfo = await GetSteamGameInfoAsync(record.AppId);
                if (steamInfo != null && !string.IsNullOrEmpty(steamInfo.Name))
                {
                    if (await PatchRecordAsync(record.Id, steamInfo.Name, steamInfo.Description, steamInfo.Genre))
                    {
                        Console.WriteLine($"✅ {steamInfo.Name}");
                        success++;
                    }
                    else
                    {
                        Console.WriteLine($"❌ PATCH failed");
                        failed++;
                    }
                }
                else
                {
                    Console.WriteLine($"⏭ Not found on Steam");
                }

                await Task.Delay(300);
            }

            Console.WriteLine($"\n[SUMMARY] Success: {success} | Failed: {failed}");
        }
    }
}
