using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    public static class RemoteConfigService
    {
        private class ConfigRow
        {
            [JsonPropertyName("value")]
            public string Value { get; set; } = string.Empty;
        }

        private class CacheEntry
        {
            public string Value { get; set; } = string.Empty;
            public DateTime ExpiresAt { get; set; }
        }

        private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        /// <summary>
        /// Retrieves a remote configuration value by key from Supabase's public.app_remote_config table.
        /// Employs a 30-minute in-memory cache to minimize network calls.
        /// </summary>
        public static async Task<string> GetConfigValueAsync(string key, bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(key))
                return string.Empty;

            if (!forceRefresh && _cache.TryGetValue(key, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
            {
                return entry.Value;
            }

            await _semaphore.WaitAsync();
            try
            {
                // Double-check cache inside semaphore
                if (!forceRefresh && _cache.TryGetValue(key, out entry) && DateTime.UtcNow < entry.ExpiresAt)
                {
                    return entry.Value;
                }

                string supabaseUrl = SupabaseConfig.SupabaseUrl;
                string supabaseKey = !string.IsNullOrWhiteSpace(SupabaseConfig.SupabaseKey) 
                    ? SupabaseConfig.SupabaseKey 
                    : SupabaseConfig.AnonKey;

                if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(supabaseKey))
                {
                    Logger.Log("[RemoteConfigService] Supabase configuration is missing.");
                    return string.Empty;
                }

                string url = $"{supabaseUrl}/rest/v1/app_remote_config?key=eq.{Uri.EscapeDataString(key)}&select=value";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", supabaseKey);
                request.Headers.Add("Authorization", $"Bearer {supabaseKey}");

                var client = SharedHttpClient.Instance;
                using var response = await client.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        Logger.Log($"[RemoteConfigService] Table 'public.app_remote_config' does not exist in Supabase yet (HTTP 404). Run 'Database/create_app_remote_config_table.sql' in your Supabase SQL Editor, or configure 'Hubcap:ApiKey' in 'Config/service-config.json'.");
                    }
                    else
                    {
                        Logger.Log($"[RemoteConfigService] Failed to fetch key '{key}' from Supabase - HTTP status {(int)response.StatusCode}");
                    }
                    return string.Empty;
                }

                var content = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(content) || content == "[]")
                {
                    Logger.Log($"[RemoteConfigService] Key '{key}' not found in app_remote_config.");
                    return string.Empty;
                }

                var rows = JsonSerializer.Deserialize<List<ConfigRow>>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                string value = rows?.FirstOrDefault()?.Value ?? string.Empty;

                _cache[key] = new CacheEntry
                {
                    Value = value,
                    ExpiresAt = DateTime.UtcNow.Add(DefaultTtl)
                };

                return value;
            }
            catch (Exception ex)
            {
                Logger.Log($"[RemoteConfigService] Error retrieving key '{key}': {ex.Message}");
                return string.Empty;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        private static string GetLocalCredentialsFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return System.IO.Path.Combine(appData, "HZLuaManager", "hubcap_credentials.json");
        }

        private class HubcapCredentialStore
        {
            [JsonPropertyName("apiKey")]
            public string ApiKey { get; set; } = string.Empty;

            [JsonPropertyName("updatedAt")]
            public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Invalidates the local cache for a specific configuration key (e.g. when an API key is rejected with 401).
        /// </summary>
        public static void InvalidateCache(string key)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                _cache.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Retrieves the Hubcap Manifest API key, checking local isolated credentials file / in-memory config first,
        /// then falling back to remote config from Supabase.
        /// </summary>
        public static async Task<string> GetHubcapApiKeyAsync(bool forceRefresh = false)
        {
            // 1. Check in-memory configuration / environment variable
            string memoryKey = ServiceConfiguration.Current.Hubcap.ApiKey;
            if (!string.IsNullOrWhiteSpace(memoryKey))
            {
                return memoryKey;
            }

            // 2. Check local user AppData credential file (never committed / built)
            try
            {
                var candidatePaths = new[]
                {
                    GetLocalCredentialsFilePath(),
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamPluginManager", "hubcap_credentials.json")
                };

                foreach (var path in candidatePaths)
                {
                    if (System.IO.File.Exists(path))
                    {
                        var json = await System.IO.File.ReadAllTextAsync(path);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var creds = JsonSerializer.Deserialize<HubcapCredentialStore>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (!string.IsNullOrWhiteSpace(creds?.ApiKey))
                            {
                                ServiceConfiguration.Current.Hubcap.ApiKey = creds.ApiKey;
                                return creds.ApiKey;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[RemoteConfigService] Error reading local Hubcap credentials: {ex.Message}");
            }

            // 3. Fetch from Supabase remote config table
            string remoteKey = await GetConfigValueAsync("hubcap_api_key", forceRefresh);
            if (!string.IsNullOrWhiteSpace(remoteKey))
            {
                ServiceConfiguration.Current.Hubcap.ApiKey = remoteKey;
            }
            return remoteKey;
        }

        /// <summary>
        /// Saves the Hubcap API key into local user AppData credentials file (isolated from build/source) and in-memory cache.
        /// </summary>
        public static async Task SaveHubcapApiKeyAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;

            try
            {
                // Update in-memory configuration
                ServiceConfiguration.Current.Hubcap.ApiKey = key;

                // Update cache
                _cache["hubcap_api_key"] = new CacheEntry
                {
                    Value = key,
                    ExpiresAt = DateTime.UtcNow.Add(DefaultTtl)
                };

                // Persist strictly to isolated AppData credentials file (NOT service-config.json)
                string credFilePath = GetLocalCredentialsFilePath();
                string? dir = System.IO.Path.GetDirectoryName(credFilePath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                var store = new HubcapCredentialStore
                {
                    ApiKey = key,
                    UpdatedAt = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(credFilePath, json);
                Logger.Log($"[RemoteConfigService] Saved Hubcap API key to user credentials file: {credFilePath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[RemoteConfigService] Error in SaveHubcapApiKeyAsync: {ex.Message}");
            }
        }
    }
}
