using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SteamPluginManager.Services.API
{
    public static class SteamDlcNameFetcher
    {
        private const string SteamApiUrl = "https://store.steampowered.com/api/appdetails";

        public static async Task<Dictionary<int, string>> FetchDlcNamesAsync(IEnumerable<int> dlcAppIds)
        {
            var result = new Dictionary<int, string>();
            var ids = dlcAppIds.Distinct().ToList();
            if (ids.Count == 0)
                return result;

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

            const int chunkSize = 100;
            for (int i = 0; i < ids.Count; i += chunkSize)
            {
                var chunk = ids.Skip(i).Take(chunkSize).ToList();
                var url = $"{SteamApiUrl}?appids={string.Join(',', chunk)}&l=english";

                try
                {
                    var response = await httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var content = await response.Content.ReadAsStringAsync();
                    using var document = JsonDocument.Parse(content);
                    var root = document.RootElement;
                    foreach (var id in chunk)
                    {
                        if (!root.TryGetProperty(id.ToString(), out var appElement))
                            continue;

                        if (!appElement.TryGetProperty("success", out var successElement) || !successElement.GetBoolean())
                            continue;

                        if (!appElement.TryGetProperty("data", out var dataElement))
                            continue;

                        if (dataElement.TryGetProperty("name", out var nameElement))
                        {
                            var name = nameElement.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(name))
                                result[id] = name;
                        }
                    }
                }
                catch
                {
                    // Safely ignore and continue; partial names may be missing
                }
            }

            return result;
        }
    }
}
