namespace SteamPluginManager
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using SteamPluginManager.Views;

    /// <summary>
    /// Supabase configuration for HZ Manifest integration
    /// </summary>
    public static class SupabaseConfig
    {
        private class DownloadCountRecord
        {
            [JsonPropertyName("download_count")]
            public int? DownloadCount { get; set; }
        }

        // Supabase project URL
        public static string SupabaseUrl => ServiceConfiguration.Current.Supabase.Url;

        // Supabase Storage endpoint (S3-compatible)
        public static string StorageUrl => ServiceConfiguration.Current.Supabase.StorageUrl;

        // Service Role Key (SECRET) - Full permissions for storage operations
        public static string SupabaseKey => ServiceConfiguration.Current.Supabase.ServiceRoleKey;

        // Anon Key - Public key for client-side REST API calls
        public static string AnonKey => ServiceConfiguration.Current.Supabase.AnonKey;

        // Storage bucket name
        public static string StorageBucketName => ServiceConfiguration.Current.Supabase.StorageBucketName;

        public static async Task<int?> GetDownloadCountAsync(int? recordId, int? appId, string? fileName)
        {
            try
            {
                if (!recordId.HasValue && !appId.HasValue && string.IsNullOrWhiteSpace(fileName))
                    return null;

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {SupabaseKey}");
                client.DefaultRequestHeaders.Add("apikey", SupabaseKey);

                string filter;
                if (recordId.HasValue)
                {
                    filter = $"id=eq.{recordId.Value}";
                }
                else if (appId.HasValue)
                {
                    filter = $"app_id=eq.{appId.Value}";
                }
                else
                {
                    filter = $"file_name=eq.{Uri.EscapeDataString(fileName!)}";
                }

                var url = $"{SupabaseUrl}/rest/v1/hzmanifest_files?select=download_count&{filter}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseKey}");

                var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                    return null;

                var content = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(content) || content == "[]")
                    return null;

                var records = JsonSerializer.Deserialize<List<DownloadCountRecord>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return records?.FirstOrDefault()?.DownloadCount;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SupabaseConfig] GetDownloadCountAsync error: {ex.Message}");
                return null;
            }
        }

        public static async Task<bool> IncrementDownloadCountAsync(int? recordId, int? appId, string? fileName)
        {
            try
            {
                if (!recordId.HasValue && !appId.HasValue && string.IsNullOrWhiteSpace(fileName))
                {
                    Console.WriteLine("[SupabaseConfig] IncrementDownloadCountAsync skipped: no identifier provided");
                    return false;
                }

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {SupabaseKey}");
                client.DefaultRequestHeaders.Add("apikey", SupabaseKey);

                string filter;
                if (recordId.HasValue)
                {
                    filter = $"id=eq.{recordId.Value}";
                }
                else if (appId.HasValue)
                {
                    filter = $"app_id=eq.{appId.Value}";
                }
                else
                {
                    filter = $"file_name=eq.{Uri.EscapeDataString(fileName!)}";
                }

                var selectUrl = $"{SupabaseUrl}/rest/v1/hzmanifest_files?select=download_count&{filter}";
                var selectRequest = new HttpRequestMessage(HttpMethod.Get, selectUrl);
                selectRequest.Headers.Add("apikey", SupabaseKey);
                selectRequest.Headers.Add("Authorization", $"Bearer {SupabaseKey}");

                var selectResponse = await client.SendAsync(selectRequest);
                if (!selectResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[SupabaseConfig] Failed to read current download count: {(int)selectResponse.StatusCode}");
                    return false;
                }

                var content = await selectResponse.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(content) || content == "[]")
                {
                    Console.WriteLine("[SupabaseConfig] No matching manifest record found for download count update");
                    return false;
                }

                var records = JsonSerializer.Deserialize<List<DownloadCountRecord>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                int currentCount = records?.FirstOrDefault()?.DownloadCount ?? 0;

                var updatePayload = new { download_count = currentCount + 1 };
                var updateJson = JsonSerializer.Serialize(updatePayload);
                var updateUrl = $"{SupabaseUrl}/rest/v1/hzmanifest_files?{filter}";
                var patchRequest = new HttpRequestMessage(HttpMethod.Patch, updateUrl)
                {
                    Content = new StringContent(updateJson, System.Text.Encoding.UTF8, "application/json")
                };
                patchRequest.Headers.Add("apikey", SupabaseKey);
                patchRequest.Headers.Add("Authorization", $"Bearer {SupabaseKey}");

                var patchResponse = await client.SendAsync(patchRequest);
                Console.WriteLine($"[SupabaseConfig] IncrementDownloadCountAsync status: {(int)patchResponse.StatusCode}");
                return patchResponse.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SupabaseConfig] IncrementDownloadCountAsync error: {ex.Message}");
                Console.WriteLine($"[SupabaseConfig] Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        public static async Task<List<ManifestFile>> GetMostDownloadedManifestFilesAsync(int count)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {SupabaseKey}");
                    client.DefaultRequestHeaders.Add("apikey", SupabaseKey);

                    string url = $"{SupabaseUrl}/rest/v1/hzmanifest_files?select=*&order=download_count.desc.nullslast,created_at.desc&limit={count}";
                    var response = await client.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[SupabaseConfig] GetMostDownloadedManifestFilesAsync failed with status {response.StatusCode}");
                        return new List<ManifestFile>();
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    return ParseManifestFilesFromJson(content);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SupabaseConfig] GetMostDownloadedManifestFilesAsync error: {ex.Message}");
                return new List<ManifestFile>();
            }
        }

        public static async Task<List<ManifestFile>> GetNewestManifestFilesAsync(int count)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {SupabaseKey}");
                    client.DefaultRequestHeaders.Add("apikey", SupabaseKey);
                    client.DefaultRequestHeaders.Add("Prefer", "count=exact");

                    string url = $"{SupabaseUrl}/rest/v1/hzmanifest_files?select=*&order=created_at.desc&limit={count}";
                    var response = await client.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[SupabaseConfig] GetNewestManifestFilesAsync failed with status {response.StatusCode}");
                        return new List<ManifestFile>();
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    return ParseManifestFilesFromJson(content);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SupabaseConfig] GetNewestManifestFilesAsync error: {ex.Message}");
                return new List<ManifestFile>();
            }
        }

        internal static List<ManifestFile> ParseManifestFilesFromJson(string content)
        {
            var manifests = new List<ManifestFile>();
            if (string.IsNullOrWhiteSpace(content))
                return manifests;

            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return manifests;

                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    var manifest = new ManifestFile();
                    if (item.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
                        manifest.Id = id;

                    if (item.TryGetProperty("app_id", out var appIdElement) && appIdElement.TryGetInt32(out var appId))
                        manifest.AppId = appId;

                    if (item.TryGetProperty("file_name", out var fileNameElement))
                        manifest.FileName = fileNameElement.GetString() ?? "";

                    if (item.TryGetProperty("name", out var nameElement))
                        manifest.Name = nameElement.GetString() ?? "";

                    if (item.TryGetProperty("version", out var versionElement))
                        manifest.Version = versionElement.GetString() ?? "";

                    if (item.TryGetProperty("file_size", out var fileSizeElement))
                        manifest.FileSize = fileSizeElement.GetString() ?? "";

                    if (item.TryGetProperty("updated_date", out var updatedDateElement))
                        manifest.UpdatedDate = updatedDateElement.GetString() ?? "";

                    if (item.TryGetProperty("url", out var urlElement))
                        manifest.FileUrl = urlElement.GetString() ?? "";

                    if (item.TryGetProperty("storage_type", out var storageTypeElement))
                        manifest.StorageType = storageTypeElement.GetString() ?? "zip";

                    if (item.TryGetProperty("folder_path", out var folderPathElement))
                        manifest.FolderPath = folderPathElement.GetString() ?? "";

                    if (item.TryGetProperty("description", out var descriptionElement))
                        manifest.Description = descriptionElement.GetString() ?? "";

                    if (item.TryGetProperty("genre", out var genreElement))
                        manifest.Genre = genreElement.GetString() ?? "";

                    if (item.TryGetProperty("download_count", out var downloadCountElement) && downloadCountElement.TryGetInt32(out var downloadCount))
                        manifest.DownloadCount = downloadCount;

                    if (item.TryGetProperty("minimum_requirements", out var minimumRequirementsElement))
                        manifest.MinimumRequirements = minimumRequirementsElement.GetString() ?? "";

                    if (item.TryGetProperty("recommended_requirements", out var recommendedRequirementsElement))
                        manifest.RecommendedRequirements = recommendedRequirementsElement.GetString() ?? "";

                    if (item.TryGetProperty("is_adult", out var adultElement))
                    {
                        manifest.IsAdult = TryParseBooleanLike(adultElement);
                    }

                    if (item.TryGetProperty("created_at", out var createdAtElement) && createdAtElement.ValueKind == JsonValueKind.String)
                    {
                        if (DateTime.TryParse(createdAtElement.GetString(), out var createdAt))
                            manifest.CreatedAt = createdAt;
                    }

                    if (item.TryGetProperty("thumbnail_path", out var thumbnailPathElement))
                        manifest.ThumbnailPath = thumbnailPathElement.GetString() ?? "";

                    manifests.Add(manifest);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SupabaseConfig] ParseManifestFilesFromJson failed: {ex.Message}");
            }

            return manifests;
        }

        private static bool TryParseBooleanLike(JsonElement element)
        {
            try
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.String:
                        if (bool.TryParse(element.GetString(), out var boolValue))
                            return boolValue;

                        if (int.TryParse(element.GetString(), out var intValue))
                            return intValue != 0;
                        break;
                    case JsonValueKind.Number:
                        if (element.TryGetInt32(out var intNumber))
                            return intNumber != 0;
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SupabaseConfig] Failed to parse boolean-like value: {ex.Message}");
            }

            return false;
        }

        public static async Task<List<string>> ListBucketFilesAsync(string prefix = "")
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {SupabaseKey}");
                client.DefaultRequestHeaders.Add("apikey", SupabaseKey);

                string cleanPrefix = (prefix ?? string.Empty).Trim().Trim('/', '\\');
                var result = new List<string>();

                // Method 1: Use Supabase Storage native object list API: POST /storage/v1/object/list/{bucket}
                try
                {
                    string storageListUrl = $"{SupabaseUrl}/storage/v1/object/list/{StorageBucketName}";
                    string searchPrefix = string.IsNullOrEmpty(cleanPrefix) ? "" : cleanPrefix + "/";

                    var payload = new
                    {
                        prefix = searchPrefix,
                        limit = 1000,
                        offset = 0,
                        sortBy = new { column = "name", order = "asc" }
                    };

                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(storageListUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in doc.RootElement.EnumerateArray())
                            {
                                if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.Null &&
                                    item.TryGetProperty("name", out var nameProp))
                                {
                                    var name = nameProp.GetString();
                                    if (!string.IsNullOrWhiteSpace(name) && !name.EndsWith("/"))
                                    {
                                        string prefixSlash = string.IsNullOrEmpty(cleanPrefix) ? "" : cleanPrefix + "/";
                                        string fullPath = string.IsNullOrEmpty(cleanPrefix)
                                            ? name
                                            : (name.StartsWith(prefixSlash, StringComparison.OrdinalIgnoreCase)
                                                ? name
                                                : $"{cleanPrefix}/{name}");
                                        result.Add(fullPath);
                                    }
                                }
                            }
                        }
                    }

                    // If searching for a folder returned no files, and cleanPrefix was non-empty, try exact prefix without trailing slash
                    if (result.Count == 0 && !string.IsNullOrEmpty(cleanPrefix))
                    {
                        var fallbackPayload = new
                        {
                            prefix = cleanPrefix,
                            limit = 1000,
                            offset = 0,
                            sortBy = new { column = "name", order = "asc" }
                        };

                        var fbContent = new StringContent(JsonSerializer.Serialize(fallbackPayload), Encoding.UTF8, "application/json");
                        var fbResponse = await client.PostAsync(storageListUrl, fbContent);
                        if (fbResponse.IsSuccessStatusCode)
                        {
                            var json = await fbResponse.Content.ReadAsStringAsync();
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in doc.RootElement.EnumerateArray())
                                {
                                    if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.Null &&
                                        item.TryGetProperty("name", out var nameProp))
                                    {
                                        var name = nameProp.GetString();
                                        if (!string.IsNullOrWhiteSpace(name) && !name.EndsWith("/"))
                                        {
                                            string prefixSlash = string.IsNullOrEmpty(cleanPrefix) ? "" : cleanPrefix + "/";
                                            string fullPath = string.IsNullOrEmpty(cleanPrefix)
                                                ? name
                                                : (name.StartsWith(prefixSlash, StringComparison.OrdinalIgnoreCase)
                                                    ? name
                                                    : $"{cleanPrefix}/{name}");
                                            result.Add(fullPath);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (result.Count > 0)
                    {
                        return result;
                    }
                }
                catch (Exception storageEx)
                {
                    Console.WriteLine($"[SupabaseConfig] Storage API list failed: {storageEx.Message}, trying RPC fallback...");
                }

                // Method 2 (Fallback): RPC list_bucket_files
                string rpcUrl = $"{SupabaseUrl}/rest/v1/rpc/list_bucket_files?bucket_name={StorageBucketName}";
                var rpcResponse = await client.GetAsync(rpcUrl);
                if (rpcResponse.IsSuccessStatusCode)
                {
                    var rpcContent = await rpcResponse.Content.ReadAsStringAsync();
                    using var document = JsonDocument.Parse(rpcContent);
                    var root = document.RootElement;

                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in root.EnumerateArray())
                        {
                            if (item.TryGetProperty("name", out var nameElement))
                            {
                                var fileName = nameElement.GetString();
                                if (string.IsNullOrWhiteSpace(fileName))
                                    continue;

                                if (!string.IsNullOrEmpty(prefix))
                                {
                                    if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                                        (!string.IsNullOrEmpty(cleanPrefix) && fileName.StartsWith(cleanPrefix, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        result.Add(fileName);
                                    }
                                }
                                else if (!fileName.EndsWith("/"))
                                {
                                    result.Add(fileName);
                                }
                            }
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SupabaseConfig] ListBucketFilesAsync error: {ex.Message}");
                return new List<string>();
            }
        }

        public static async Task<int> GetHzManifestFilesCountAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {SupabaseKey}");
                    client.DefaultRequestHeaders.Add("apikey", SupabaseKey);
                    client.DefaultRequestHeaders.Add("Prefer", "count=exact");

                    string url = $"{SupabaseUrl}/rest/v1/hzmanifest_files?select=id";
                    Console.WriteLine($"[SupabaseConfig] Requesting URL: {url}");
                    var response = await client.GetAsync(url);

                    Console.WriteLine($"[SupabaseConfig] Response Status: {response.StatusCode}");
                    var content = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[SupabaseConfig] Response content: {content}");

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[SupabaseConfig] Request failed with status {response.StatusCode}");
                        return 0;
                    }

                    if (response.Headers.TryGetValues("Content-Range", out var values))
                    {
                        var range = values.FirstOrDefault();
                        if (range != null)
                        {
                            var parts = range.Split('/');
                            if (parts.Length == 2 && int.TryParse(parts[1], out int total))
                            {
                                Console.WriteLine($"[SupabaseConfig] HZ manifest count from Content-Range: {total}");
                                return total;
                            }
                        }
                    }

                    var manifests = JsonSerializer.Deserialize<List<ManifestFile>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return manifests?.Count ?? 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SupabaseConfig] GetHzManifestFilesCountAsync error: {ex.Message}");
                Console.WriteLine($"[SupabaseConfig] Stack trace: {ex.StackTrace}");
                return 0;
            }
        }
    }
}
