using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SteamPluginManager.Models;

namespace SteamPluginManager.Services.API
{
    public static class SupabaseDlcService
    {
        public static async Task<List<DLCItem>> GetDlcItemsAsync(int gameAppId)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                client.DefaultRequestHeaders.Add("apikey", SupabaseConfig.SupabaseKey);

                string url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/games_dlc?select=dlc_app_id,dlc_name,dlc_fetch_date&game_app_id=eq.{gameAppId}";
                var response = await client.GetAsync(url);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[SupabaseDlcService] GetDlcItemsAsync failed: {(int)response.StatusCode} - {response.ReasonPhrase}");
                    Console.WriteLine($"[SupabaseDlcService] URL: {url}");
                    Console.WriteLine($"[SupabaseDlcService] Response content: {content}");
                    return new List<DLCItem>();
                }

                if (string.IsNullOrWhiteSpace(content) || content == "[]")
                {
                    Console.WriteLine($"[SupabaseDlcService] No DLC items returned for game {gameAppId}. URL: {url}");
                    Console.WriteLine($"[SupabaseDlcService] Response content: {content}");
                    return new List<DLCItem>();
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<List<DLCItem>>(content, options) ?? new List<DLCItem>();
            }
            catch
            {
                return new List<DLCItem>();
            }
        }

        public static async Task<bool> UpsertDlcItemsAsync(int gameAppId, IEnumerable<DLCItem> dlcItems)
        {
            try
            {
                var itemList = new List<object>();
                foreach (var item in dlcItems)
                {
                    itemList.Add(new
                    {
                        game_app_id = gameAppId,
                        dlc_app_id = item.AppId,
                        dlc_name = item.Name,
                        dlc_fetch_date = item.FetchDate
                    });
                }

                string url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/games_dlc?on_conflict=game_app_id,dlc_app_id";
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                client.DefaultRequestHeaders.Add("apikey", SupabaseConfig.SupabaseKey);
                client.DefaultRequestHeaders.Add("Prefer", "resolution=merge-duplicates");

                var contentJson = JsonSerializer.Serialize(itemList);
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(contentJson, System.Text.Encoding.UTF8, "application/json")
                };

                var response = await client.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }
}
