using System;
using System.IO;
using System.Text.Json;

namespace SteamPluginManager
{
    public static class ServiceConfiguration
    {
        private static readonly object LockObj = new();
        private static ServiceConfigModel? _cachedConfig;

        static ServiceConfiguration()
        {
            LoadDotEnvIfPresent();
        }

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

                    var config = new ServiceConfigModel();
                    var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "service-config.json");

                    if (File.Exists(configPath))
                    {
                        var json = File.ReadAllText(configPath);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var fileConfig = JsonSerializer.Deserialize<ServiceConfigModel>(json, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });

                            if (fileConfig != null)
                                config = fileConfig;
                        }
                    }

                    ApplyEnvironmentOverrides(config);
                    _cachedConfig = config;
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

        private static void ApplyEnvironmentOverrides(ServiceConfigModel config)
        {
            config.Supabase.Url = GetSetting(Environment.GetEnvironmentVariable("SUPABASE_URL"), config.Supabase.Url);
            config.Supabase.StorageUrl = GetSetting(Environment.GetEnvironmentVariable("SUPABASE_STORAGE_URL"), config.Supabase.StorageUrl);
            config.Supabase.AnonKey = GetSetting(Environment.GetEnvironmentVariable("SUPABASE_ANON_KEY"), config.Supabase.AnonKey);
            config.Supabase.ServiceRoleKey = GetSetting(Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY"), config.Supabase.ServiceRoleKey);
            config.Supabase.StorageBucketName = GetSetting(Environment.GetEnvironmentVariable("SUPABASE_STORAGE_BUCKET_NAME"), config.Supabase.StorageBucketName);

            config.CloudflareR2.Endpoint = GetSetting(Environment.GetEnvironmentVariable("R2_ENDPOINT"), config.CloudflareR2.Endpoint);
            config.CloudflareR2.AccessKey = GetSetting(Environment.GetEnvironmentVariable("R2_ACCESS_KEY"), config.CloudflareR2.AccessKey);
            config.CloudflareR2.SecretKey = GetSetting(Environment.GetEnvironmentVariable("R2_SECRET_KEY"), config.CloudflareR2.SecretKey);
            config.CloudflareR2.Bucket = GetSetting(Environment.GetEnvironmentVariable("R2_BUCKET"), config.CloudflareR2.Bucket);

            config.BackblazeB2.Endpoint = GetSetting(Environment.GetEnvironmentVariable("B2_ENDPOINT"), config.BackblazeB2.Endpoint);
            config.BackblazeB2.AccessKey = GetSetting(Environment.GetEnvironmentVariable("B2_KEY_ID"), config.BackblazeB2.AccessKey);
            config.BackblazeB2.SecretKey = GetSetting(Environment.GetEnvironmentVariable("B2_APPLICATION_KEY"), config.BackblazeB2.SecretKey);
            config.BackblazeB2.Bucket = GetSetting(Environment.GetEnvironmentVariable("B2_BUCKET_NAME"), config.BackblazeB2.Bucket);
            config.BackblazeB2.Region = GetSetting(Environment.GetEnvironmentVariable("B2_REGION"), config.BackblazeB2.Region);

            config.Discord.GameRequestWebhookUrl = GetSetting(Environment.GetEnvironmentVariable("DISCORD_GAME_REQUEST_WEBHOOK_URL"), config.Discord.GameRequestWebhookUrl);
            config.Discord.BugReportWebhookUrl = GetSetting(Environment.GetEnvironmentVariable("DISCORD_BUG_REPORT_WEBHOOK_URL"), config.Discord.BugReportWebhookUrl);

            config.Hubcap.ApiKey = GetSetting(Environment.GetEnvironmentVariable("HUBCAP_API_KEY"), config.Hubcap.ApiKey);
            config.Hubcap.BaseUrl = GetSetting(Environment.GetEnvironmentVariable("HUBCAP_BASE_URL"), config.Hubcap.BaseUrl);

            config.ResourceSource.UploadsBaseUrl = GetSetting(Environment.GetEnvironmentVariable("RESOURCE_UPLOADS_URL"), config.ResourceSource.UploadsBaseUrl);
            config.ResourceSource.Username = GetSetting(Environment.GetEnvironmentVariable("RESOURCE_USERNAME"), config.ResourceSource.Username);
            config.ResourceSource.Password = GetSetting(Environment.GetEnvironmentVariable("RESOURCE_PASSWORD"), config.ResourceSource.Password);
            config.ResourceSource.ArchivePassword = GetSetting(Environment.GetEnvironmentVariable("RESOURCE_ARCHIVE_PASSWORD"), config.ResourceSource.ArchivePassword);
            config.ResourceSource.StoragePrefix = GetSetting(Environment.GetEnvironmentVariable("RESOURCE_STORAGE_PREFIX"), config.ResourceSource.StoragePrefix);
        }

        private static void LoadDotEnvIfPresent()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, ".env"),
                Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env")
            };

            foreach (var candidate in candidates.Distinct())
            {
                if (!File.Exists(candidate))
                    continue;

                foreach (var line in File.ReadAllLines(candidate))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                        continue;

                    var separatorIndex = trimmed.IndexOf('=');
                    if (separatorIndex <= 0)
                        continue;

                    var key = trimmed.Substring(0, separatorIndex).Trim();
                    var value = trimmed.Substring(separatorIndex + 1).Trim();

                    if ((value.StartsWith("\"") && value.EndsWith("\"")) ||
                        (value.StartsWith("'") && value.EndsWith("'")))
                    {
                        value = value.Substring(1, value.Length - 2);
                    }

                    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                    {
                        Environment.SetEnvironmentVariable(key, value);
                    }
                }

                return;
            }
        }

        private static string GetSetting(string? environmentValue, string fallbackValue)
        {
            if (!string.IsNullOrWhiteSpace(environmentValue))
                return environmentValue.Trim();

            return fallbackValue;
        }
    }

    public class ServiceConfigModel
    {
        public SupabaseSettings Supabase { get; set; } = new();
        public CloudflareR2Settings CloudflareR2 { get; set; } = new();
        public BackblazeB2Settings BackblazeB2 { get; set; } = new();
        public DiscordSettings Discord { get; set; } = new();
        public HubcapSettings Hubcap { get; set; } = new();
        public ResourceSourceSettings ResourceSource { get; set; } = new();
    }

    public class ResourceSourceSettings
    {
        public string UploadsBaseUrl { get; set; } = "https://uploads.online-fix.me:2053";
        public string Username { get; set; } = "hzluamanager";
        public string Password { get; set; } = "hzluamanager";
        public string ArchivePassword { get; set; } = "online-fix.me";
        public string StoragePrefix { get; set; } = "onlinefix/";
        public bool AutoProcessOnManifestSuccess { get; set; } = true;
    }

    public class HubcapSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = "https://hubcapmanifest.com/api/v1";
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

    public class BackblazeB2Settings
    {
        public string Endpoint { get; set; } = string.Empty;
        public string AccessKey { get; set; } = string.Empty;
        public string SecretKey { get; set; } = string.Empty;
        public string Bucket { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty; // e.g. "us-east-005"
    }

    public class DiscordSettings
    {
        public string GameRequestWebhookUrl { get; set; } = string.Empty;
        public string BugReportWebhookUrl { get; set; } = string.Empty;
    }
}

