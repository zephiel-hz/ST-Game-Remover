using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    public static class SteamApiHelper
    {
        public static async Task<string> GetGameNameAsync(int appId)
        {
            try
            {
                var client = SharedHttpClient.Instance;
                string url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
                var response = await client.GetStringAsync(url);

                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty(appId.ToString(), out var appData) &&
                    appData.GetProperty("success").GetBoolean())
                {
                    return appData.GetProperty("data").GetProperty("name").GetString() ?? $"App {appId}";
                }
            }
            catch { }
            return $"App {appId}";
        }
    }
}
