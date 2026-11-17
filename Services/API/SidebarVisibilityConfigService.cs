using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SteamPluginManager.Services.API
{
    public sealed class SidebarFeatureVisibility
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsVisible { get; set; } = true;
    }

    public static class SidebarVisibilityConfigService
    {
        public const string ManifestKey = "hz_manifest";
        public const string GameBypassKey = "game_bypass";
        public const string OnlineFixKey = "online_fix";
        public const string LibraryKey = "game_library";
        public const string ToolsKey = "tools";
        public const string SaweriaKey = "saweria";

        private const string ConfigKey = "sidebar_feature_visibility";

        private static readonly (string Key, string Label, string Description)[] FeatureDefinitions =
        {
            (ManifestKey, "HZ Manifest", "Browse and install HZ Manifest entries."),
            (GameBypassKey, "Game Bypass", "Browse Game Bypass patches."),
            (OnlineFixKey, "OnlineFix", "Browse OnlineFix multiplayer files."),
            (LibraryKey, "Game Library", "View installed games."),
            (ToolsKey, "Tools", "Open community tools."),
            (SaweriaKey, "Saweria", "Open the support and donation page.")
        };

        private sealed class ConfigRow
        {
            [JsonPropertyName("value")]
            public string Value { get; set; } = string.Empty;
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static event Action<IReadOnlyDictionary<string, bool>>? SettingsChanged;

        public static async Task<List<SidebarFeatureVisibility>> LoadAsync()
        {
            string url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/app_remote_config?key=eq.{Uri.EscapeDataString(ConfigKey)}&select=value";
            using var request = CreateRequest(HttpMethod.Get, url);
            using var response = await SharedHttpClient.Instance.SendAsync(request);
            string responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Failed to load sidebar settings (HTTP {(int)response.StatusCode}): {responseContent}");
            }

            var rows = JsonSerializer.Deserialize<List<ConfigRow>>(responseContent, JsonOptions);
            string? serializedSettings = rows?.FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(serializedSettings))
                return CreateSettings(null);

            var savedSettings = JsonSerializer.Deserialize<Dictionary<string, bool>>(serializedSettings, JsonOptions)
                ?? throw new JsonException("Sidebar settings must be a JSON object.");
            return CreateSettings(savedSettings);
        }

        public static async Task SaveAsync(IEnumerable<SidebarFeatureVisibility> settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var settingList = settings.ToList();
            var values = FeatureDefinitions.ToDictionary(
                feature => feature.Key,
                feature => settingList.FirstOrDefault(item =>
                    string.Equals(item.Key, feature.Key, StringComparison.OrdinalIgnoreCase))?.IsVisible ?? true,
                StringComparer.OrdinalIgnoreCase);

            string url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/app_remote_config?on_conflict=key";
            using var request = CreateRequest(HttpMethod.Post, url);
            request.Headers.Add("Prefer", "resolution=merge-duplicates,return=minimal");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    key = ConfigKey,
                    value = JsonSerializer.Serialize(values),
                    description = "Global visibility settings for user-facing sidebar feature buttons."
                }),
                Encoding.UTF8,
                "application/json");

            using var response = await SharedHttpClient.Instance.SendAsync(request);
            string responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Failed to save sidebar settings (HTTP {(int)response.StatusCode}): {responseContent}");
            }

            SettingsChanged?.Invoke(values);
        }

        private static List<SidebarFeatureVisibility> CreateSettings(
            IReadOnlyDictionary<string, bool>? savedSettings)
        {
            return FeatureDefinitions.Select(feature => new SidebarFeatureVisibility
            {
                Key = feature.Key,
                Label = feature.Label,
                Description = feature.Description,
                IsVisible = savedSettings == null ||
                    !savedSettings.TryGetValue(feature.Key, out bool isVisible) ||
                    isVisible
            }).ToList();
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
        {
            string key = SupabaseConfig.SupabaseKey;
            if (string.IsNullOrWhiteSpace(SupabaseConfig.SupabaseUrl) || string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Supabase URL or service key is not configured.");

            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("apikey", key);
            request.Headers.Add("Authorization", $"Bearer {key}");
            request.Headers.Add("Accept", "application/json");
            return request;
        }
    }
}
