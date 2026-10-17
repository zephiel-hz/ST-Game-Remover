using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    public static class HubcapManifestService
    {
        private static readonly object _lock = new();
        private static string? _sessionCookieHeader;

        public static void SetSessionCookieHeader(string? cookieHeader)
        {
            _sessionCookieHeader = cookieHeader;
        }

        /// <summary>
        /// Validates whether a given Hubcap API key is active and authorized.
        /// Returns true if the key is valid, false if 401 Unauthorized or 403 Forbidden.
        /// </summary>
        public static async Task<bool> IsApiKeyValidAsync(string? apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return false;

            try
            {
                // Query a lightweight DRM endpoint (e.g. AppID 730 / CS2) to verify authorization
                string url = "https://hubcapmanifest.com/manifest-history/api/apps/730/drm";
                var client = SharedHttpClient.Instance;
                using var req = CreateAuthenticatedRequest(HttpMethod.Get, url, apiKey);
                using var res = await client.SendAsync(req);

                if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
                {
                    Logger.Log($"[HubcapManifestService] API key validation failed with status {res.StatusCode}. Key is expired or invalid.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[HubcapManifestService] Transient error while checking API key validity: {ex.Message}");
                // If there is a transient network error, do not immediately invalidate to prevent offline breakage
                return true;
            }
        }

        /// <summary>
        /// Ensures a valid Hubcap API key is available. If missing, invalid, or expired, automatically
        /// regenerates a fresh API key via hubcapmanifest.com (first silently, then visibly if interaction is needed).
        /// </summary>
        public static async Task<string?> EnsureValidApiKeyAsync(
            System.Windows.Window? owner = null,
            bool forceRegenerate = false,
            string? expiredKey = null,
            Action<string>? statusCallback = null)
        {
            try
            {
                if (!forceRegenerate)
                {
                    statusCallback?.Invoke("Verifying Hubcap API key...");
                    string existingKey = await RemoteConfigService.GetHubcapApiKeyAsync();

                    if (!string.IsNullOrWhiteSpace(existingKey))
                    {
                        bool isValid = await IsApiKeyValidAsync(existingKey);
                        if (isValid)
                        {
                            return existingKey;
                        }
                        expiredKey = existingKey;
                    }
                }

                Logger.Log($"[HubcapManifestService] Hubcap API key needs regeneration (forceRegenerate={forceRegenerate}). Generating new key from hubcapmanifest.com...");
                statusCallback?.Invoke("Generating new Hubcap API key...");

                // Invalidate local and memory caches for the old key
                RemoteConfigService.InvalidateCache("hubcap_api_key");

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    Logger.Log("[HubcapManifestService] Dispatcher is null. Cannot open HubcapLoginDialog.");
                    return null;
                }

                // Attempt 1: Background silent mode generation using existing Discord session
                string? newKey = await dispatcher.InvokeAsync(() =>
                {
                    var dialog = new Views.HubcapLoginDialog(silentMode: true, forceRegenerate: true, expiredKeyToAvoid: expiredKey)
                    {
                        Owner = owner
                    };
                    bool? res = dialog.ShowDialog();
                    if (res == true && !string.IsNullOrWhiteSpace(dialog.ExtractedApiKey))
                    {
                        return dialog.ExtractedApiKey;
                    }
                    return null;
                }).Task;

                if (!string.IsNullOrWhiteSpace(newKey))
                {
                    Logger.Log("[HubcapManifestService] Successfully generated new Hubcap API key via background automation.");
                    await RemoteConfigService.SaveHubcapApiKeyAsync(newKey);
                    return newKey;
                }

                // Attempt 2: If silent mode did not succeed (e.g. Discord login required), open visible dialog for user
                statusCallback?.Invoke("Hubcap authorization required. Opening login dialog...");
                newKey = await dispatcher.InvokeAsync(() =>
                {
                    var dialog = new Views.HubcapLoginDialog(silentMode: false, forceRegenerate: true, expiredKeyToAvoid: expiredKey)
                    {
                        Owner = owner
                    };
                    bool? res = dialog.ShowDialog();
                    if (res == true && !string.IsNullOrWhiteSpace(dialog.ExtractedApiKey))
                    {
                        return dialog.ExtractedApiKey;
                    }
                    return null;
                }).Task;

                if (!string.IsNullOrWhiteSpace(newKey))
                {
                    Logger.Log("[HubcapManifestService] Successfully obtained new Hubcap API key from visible dialog.");
                    await RemoteConfigService.SaveHubcapApiKeyAsync(newKey);
                    return newKey;
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"[HubcapManifestService] EnsureValidApiKeyAsync error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Queries Hubcap's DRM status endpoint (/manifest-history/api/apps/{appId}/drm)
        /// which combines Steam Store DRM notices and the Denuvo Watch curator.
        /// Returns true if Denuvo is detected, false if not Denuvo, or null if the request failed/unauthorized.
        /// </summary>
        public static async Task<bool?> GetDenuvoStatusAsync(int appId, string? apiKey = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    apiKey = await EnsureValidApiKeyAsync();
                }

                string url = $"https://hubcapmanifest.com/manifest-history/api/apps/{appId}/drm";
                var client = SharedHttpClient.Instance;
                using var req = CreateAuthenticatedRequest(HttpMethod.Get, url, apiKey);
                using var res = await client.SendAsync(req);

                // If unauthorized (401/403), regenerate key and retry
                if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
                {
                    Logger.Log($"[HubcapManifestService] Hubcap DRM status returned HTTP {res.StatusCode}. Regenerating API key...");
                    string? newKey = await EnsureValidApiKeyAsync(forceRegenerate: true, expiredKey: apiKey);
                    if (!string.IsNullOrWhiteSpace(newKey))
                    {
                        using var retryReq = CreateAuthenticatedRequest(HttpMethod.Get, url, newKey);
                        using var retryRes = await client.SendAsync(retryReq);
                        if (retryRes.IsSuccessStatusCode)
                        {
                            return await ParseDenuvoResponseAsync(retryRes);
                        }
                    }
                }
                else if (res.IsSuccessStatusCode)
                {
                    return await ParseDenuvoResponseAsync(res);
                }
                else
                {
                    Logger.Log($"[HubcapManifestService] Hubcap DRM status returned HTTP {res.StatusCode} for AppID {appId}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HubcapManifestService] Error checking Denuvo status from Hubcap: {ex.Message}");
            }

            return null;
        }

        private static async Task<bool?> ParseDenuvoResponseAsync(HttpResponseMessage res)
        {
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("denuvo", out var denuvoEl))
                {
                    if (denuvoEl.ValueKind == JsonValueKind.True) return true;
                    if (denuvoEl.ValueKind == JsonValueKind.False) return false;
                }

                if (doc.RootElement.TryGetProperty("drm_notice", out var noticeEl) && noticeEl.ValueKind == JsonValueKind.String)
                {
                    string notice = noticeEl.GetString() ?? "";
                    if (notice.IndexOf("denuvo", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }

                return false;
            }
            return null;
        }

        /// <summary>
        /// Attempts to search and download game files (.lua and/or .manifest) from Hubcap Manifest (hubcapmanifest.com).
        /// If the key is rejected with 401/403, automatically regenerates a new key from hubcapmanifest.com and retries.
        /// </summary>
        public static async Task<List<DownloadedGameFile>?> FindAndDownloadFilesAsync(
            int appId,
            bool isDenuvo,
            Action<string>? statusCallback = null,
            Func<Task<string?>>? loginPromptCallback = null)
        {
            try
            {
                statusCallback?.Invoke("Retrieving Hubcap Manifest credentials...");
                string? apiKey = await EnsureValidApiKeyAsync(statusCallback: statusCallback);

                if (string.IsNullOrWhiteSpace(apiKey) && loginPromptCallback != null)
                {
                    statusCallback?.Invoke("Hubcap authentication required. Opening login...");
                    apiKey = await loginPromptCallback();
                }

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    Logger.Log("[HubcapManifestService] Hubcap API key is not configured. Skipping Hubcap search.");
                    return null;
                }

                statusCallback?.Invoke("Searching Hubcap Manifest...");
                var (files, isUnauthorized) = await QueryHubcapAsync(appId, apiKey, isDenuvo, statusCallback);

                // If unauthorized (401/403), key is invalid or expired -> regenerate key and retry!
                if (isUnauthorized)
                {
                    Logger.Log("[HubcapManifestService] Received 401/403 Unauthorized from Hubcap. Regenerating API key...");
                    statusCallback?.Invoke("Hubcap key expired. Generating new API key from hubcapmanifest.com...");

                    string? newKey = await EnsureValidApiKeyAsync(forceRegenerate: true, expiredKey: apiKey, statusCallback: statusCallback);
                    if (string.IsNullOrWhiteSpace(newKey) && loginPromptCallback != null)
                    {
                        newKey = await loginPromptCallback();
                    }

                    if (!string.IsNullOrWhiteSpace(newKey) && !string.Equals(newKey, apiKey, StringComparison.Ordinal))
                    {
                        Logger.Log("[HubcapManifestService] Retrying Hubcap search with newly generated key...");
                        var retryResult = await QueryHubcapAsync(appId, newKey, isDenuvo, statusCallback);
                        files = retryResult.files;
                    }
                }

                if (files != null && files.Count > 0)
                {
                    // Enforce rule: Non-Denuvo games must only have .lua files
                    if (!isDenuvo)
                    {
                        files = files.Where(f => f.FileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToList();
                    }

                    if (files.Count > 0)
                    {
                        Logger.Log($"[HubcapManifestService] Successfully downloaded {files.Count} files for AppID {appId} from Hubcap Manifest.");
                        return files;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"[HubcapManifestService] Error during Hubcap Manifest search: {ex.Message}");
                return null;
            }
        }

        private static async Task<(List<DownloadedGameFile>? files, bool isUnauthorized)> QueryHubcapAsync(
            int appId,
            string apiKey,
            bool isDenuvo,
            Action<string>? statusCallback)
        {
            var result = new List<DownloadedGameFile>();
            var client = SharedHttpClient.Instance;

            string baseUrl = ServiceConfiguration.Current.Hubcap.BaseUrl?.TrimEnd('/') 
                             ?? "https://hubcapmanifest.com/api/v1";

            // 1. Fetch Lua file(s)
            string luaUrl = $"{baseUrl}/lua/{appId}";
            using var luaReq = CreateAuthenticatedRequest(HttpMethod.Get, luaUrl, apiKey);

            using var luaRes = await client.SendAsync(luaReq);

            if (luaRes.StatusCode == HttpStatusCode.Unauthorized || luaRes.StatusCode == HttpStatusCode.Forbidden)
            {
                return (null, true);
            }

            if (luaRes.IsSuccessStatusCode)
            {
                var contentBytes = await luaRes.Content.ReadAsByteArrayAsync();
                var mediaType = luaRes.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";

                ProcessPayload(contentBytes, mediaType, $"{appId}.lua", result);
            }

            // 2. If Denuvo, also fetch manifests
            if (isDenuvo)
            {
                statusCallback?.Invoke("Fetching manifests from Hubcap...");
                string manifestUrl = $"{baseUrl}/manifest/{appId}";
                using var manifestReq = CreateAuthenticatedRequest(HttpMethod.Get, manifestUrl, apiKey);

                using var manifestRes = await client.SendAsync(manifestReq);
                if (manifestRes.IsSuccessStatusCode)
                {
                    var manifestBytes = await manifestRes.Content.ReadAsByteArrayAsync();
                    var manifestMediaType = manifestRes.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";

                    ProcessPayload(manifestBytes, manifestMediaType, $"{appId}.manifest", result);
                }
            }

            return (result.Count > 0 ? result : null, false);
        }

        private static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url, string? apiKey = null)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Add("User-Agent", "HZLuaManager/1.0");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
                req.Headers.Add("X-API-Key", apiKey);
            }
            if (!string.IsNullOrWhiteSpace(_sessionCookieHeader))
            {
                req.Headers.Add("Cookie", _sessionCookieHeader);
            }
            return req;
        }

        private static void ProcessPayload(byte[] bytes, string mediaType, string defaultFileName, List<DownloadedGameFile> result)
        {
            if (bytes == null || bytes.Length == 0)
                return;

            // Check if payload is a Zip file (magic number PK\x03\x04)
            if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Name))
                            continue;

                        using var entryStream = entry.Open();
                        using var memStream = new MemoryStream();
                        entryStream.CopyTo(memStream);
                        var entryBytes = memStream.ToArray();

                        if (entryBytes.Length > 0)
                        {
                            result.Add(new DownloadedGameFile
                            {
                                FileName = entry.Name,
                                Bytes = entryBytes
                            });
                        }
                    }
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[HubcapManifestService] Failed to extract zip payload: {ex.Message}");
                }
            }

            // Check if JSON response containing base64 or file list
            if (mediaType.Contains("json"))
            {
                try
                {
                    var jsonStr = Encoding.UTF8.GetString(bytes);
                    using var doc = JsonDocument.Parse(jsonStr);

                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        // Check common JSON formats: {"data": "..."} or {"content": "..."} or {"lua": "..."}
                        if (doc.RootElement.TryGetProperty("lua", out var luaProp) && luaProp.ValueKind == JsonValueKind.String)
                        {
                            var luaText = luaProp.GetString() ?? "";
                            result.Add(new DownloadedGameFile
                            {
                                FileName = defaultFileName,
                                Bytes = Encoding.UTF8.GetBytes(luaText)
                            });
                            return;
                        }

                        if (doc.RootElement.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                        {
                            var contentStr = contentProp.GetString() ?? "";
                            byte[] decoded;
                            try
                            {
                                decoded = Convert.FromBase64String(contentStr);
                            }
                            catch
                            {
                                decoded = Encoding.UTF8.GetBytes(contentStr);
                            }

                            result.Add(new DownloadedGameFile
                            {
                                FileName = defaultFileName,
                                Bytes = decoded
                            });
                            return;
                        }
                    }
                }
                catch
                {
                    // Fall through to treat as raw bytes
                }
            }

            // Treat directly as raw file bytes
            result.Add(new DownloadedGameFile
            {
                FileName = defaultFileName,
                Bytes = bytes
            });
        }
    }
}
