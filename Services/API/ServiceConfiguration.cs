using System;
using System.IO;
using System.Text.Json;

namespace SteamPluginManager
{
    public static class ServiceConfiguration
    {
        private static readonly object LockObj = new();
        private static ServiceConfigModel? _cachedConfig;

        public static ServiceConfigModel Current
        {
            get
            {
                if (_cachedConfig != null)
                    return _cachedConfig;

                lock (LockObj)
                {
                    if (_cachedConfig != null)
                        return _cachedConfig;

                    var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "service-config.json");
                    if (!File.Exists(configPath))
                    {
                        throw new FileNotFoundException($"Service configuration file not found: {configPath}");
                    }

                    var json = File.ReadAllText(configPath);
                    _cachedConfig = JsonSerializer.Deserialize<ServiceConfigModel>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? throw new InvalidOperationException("Failed to parse service configuration.");

                    return _cachedConfig;
                }
            }
        }

        public static void Reload()
        {
            lock (LockObj)
            {
                _cachedConfig = null;
            }
        }
    }

    public class ServiceConfigModel
    {
        public SupabaseSettings Supabase { get; set; } = new();
        public CloudflareR2Settings CloudflareR2 { get; set; } = new();
    }

    public class SupabaseSettings
    {
        public string Url { get; set; } = string.Empty;
        public string StorageUrl { get; set; } = string.Empty;
        public string AnonKey { get; set; } = string.Empty;
        public string ServiceRoleKey { get; set; } = string.Empty;
        public string StorageBucketName { get; set; } = string.Empty;
    }

    public class CloudflareR2Settings
    {
        public string Endpoint { get; set; } = string.Empty;
        public string AccessKey { get; set; } = string.Empty;
        public string SecretKey { get; set; } = string.Empty;
        public string Bucket { get; set; } = string.Empty;
    }
}
