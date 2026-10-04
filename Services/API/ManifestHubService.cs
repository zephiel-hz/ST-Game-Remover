using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    public class DownloadedGameFile
    {
        public string FileName { get; set; } = string.Empty;
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
    }

    public static class ManifestHubService
    {
        private static HashSet<int>? _denuvoAppIds;
        private static readonly ConcurrentDictionary<int, bool> _denuvoCache = new();
        private static LuaMapIndex? _luaMapCache;
        private static readonly object _lock = new();

        private class LuaMapIndex
        {
            [JsonPropertyName("f")]
            public List<string> Folders { get; set; } = new();

            [JsonPropertyName("a")]
            public Dictionary<string, int> Apps { get; set; } = new();
        }

        private class SteamAppDetailsResponse
        {
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string Genre { get; set; } = string.Empty;
            public string MinimumRequirements { get; set; } = string.Empty;
            public string RecommendedRequirements { get; set; } = string.Empty;
            public bool IsAdult { get; set; } = false;
        }

        /// <summary>
        /// Centralized Denuvo detection engine.
        /// Priority:
        /// 1. Hubcap DRM API (/manifest-history/api/apps/{appId}/drm) - combines Store + Denuvo Watch curator.
        /// 2. Live Steam Store API (https://store.steampowered.com/api/appdetails?appids={appId}) - detects live DRM removals.
        /// 3. Static ManifestHub GitHub database fallback.
        /// </summary>
        public static async Task<bool> DetectDenuvoAsync(int appId, Action<string>? statusCallback = null)
        {
            if (_denuvoCache.TryGetValue(appId, out bool cached))
            {
                return cached;
            }

            // Tier 1: Hubcap DRM API
            statusCallback?.Invoke("Checking Denuvo status via Hubcap...");
            try
            {
                bool? hubcapDenuvo = await HubcapManifestService.GetDenuvoStatusAsync(appId);
                if (hubcapDenuvo.HasValue)
                {
                    Logger.Log($"[ManifestHubService] AppID {appId} - Denuvo status from Hubcap: {hubcapDenuvo.Value}");
                    _denuvoCache[appId] = hubcapDenuvo.Value;
                    return hubcapDenuvo.Value;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManifestHubService] Hubcap DRM check failed: {ex.Message}");
            }

            // Tier 2: Real-time Steam Store API
            statusCallback?.Invoke("Checking Denuvo status via Steam Store...");
            try
            {
                bool? steamDenuvo = await CheckSteamStoreDenuvoAsync(appId);
                if (steamDenuvo.HasValue)
                {
                    Logger.Log($"[ManifestHubService] AppID {appId} - Denuvo status from Steam Store API: {steamDenuvo.Value}");
                    _denuvoCache[appId] = steamDenuvo.Value;
                    return steamDenuvo.Value;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManifestHubService] Steam Store DRM check failed: {ex.Message}");
            }

            // Tier 3: Static GitHub fallback
            statusCallback?.Invoke("Checking Denuvo status via database fallback...");
            bool fallbackDenuvo = await CheckStaticDenuvoListAsync(appId);
            Logger.Log($"[ManifestHubService] AppID {appId} - Denuvo status from static fallback: {fallbackDenuvo}");
            _denuvoCache[appId] = fallbackDenuvo;
            return fallbackDenuvo;
        }

        private static async Task<bool?> CheckSteamStoreDenuvoAsync(int appId)
        {
            try
            {
                var client = SharedHttpClient.Instance;
                string url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
                using var res = await client.GetAsync(url);
                if (!res.IsSuccessStatusCode)
                    return null;

                var json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("success", out var successEl) && successEl.GetBoolean())
                    {
                        if (prop.Value.TryGetProperty("data", out var dataEl))
                        {
                            if (dataEl.TryGetProperty("drm_notice", out var drmNoticeEl) && drmNoticeEl.ValueKind == JsonValueKind.String)
                            {
                                string notice = drmNoticeEl.GetString() ?? "";
                                if (notice.IndexOf("denuvo", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    return true;
                                }
                            }
                            // Store returned success, and drm_notice doesn't mention Denuvo (or is absent)
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManifestHubService] Steam Store DRM check error: {ex.Message}");
            }

            return null;
        }

        private static async Task<bool> CheckStaticDenuvoListAsync(int appId)
        {
            try
            {
                if (_denuvoAppIds == null)
                {
                    var client = SharedHttpClient.Instance;
                    string denuvoUrl = "https://raw.githubusercontent.com/trionine/ManifestHub/main/data/denuvo-games.json";
                    using var response = await client.GetAsync(denuvoUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var ids = JsonSerializer.Deserialize<List<int>>(json);
                        if (ids != null)
                        {
                            lock (_lock)
                            {
                                _denuvoAppIds = new HashSet<int>(ids);
                            }
                        }
                    }
                }

                lock (_lock)
                {
                    if (_denuvoAppIds != null)
                        return _denuvoAppIds.Contains(appId);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManifestHubService] Error checking static Denuvo list: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Backward-compatible alias for DetectDenuvoAsync.
        /// </summary>
        public static async Task<bool> IsDenuvoGameAsync(int appId)
        {
            return await DetectDenuvoAsync(appId);
        }

        /// <summary>
        /// Searches ManifestHub archives and database for the game without re-checking Denuvo.
        /// Uses the pre-determined isDenuvo status directly.
        /// </summary>
        public static async Task<List<DownloadedGameFile>?> FindAndDownloadFilesAsync(
            int appId,
            bool isDenuvo,
            Action<string>? statusCallback = null)
        {
            Logger.Log($"[ManifestHubService] AppID {appId} - Executing download with isDenuvo: {isDenuvo}");

            var client = SharedHttpClient.Instance;

            // List of archive repos to check in order: ManifestHub3 (newer), then ManifestHub2
            string[] archiveRepos = new[] { "steamtools-games/ManifestHub3", "SSMGAlt/ManifestHub2" };

            foreach (var repo in archiveRepos)
            {
                statusCallback?.Invoke($"Searching {repo}...");
                try
                {
                    string probeUrl = $"https://raw.githubusercontent.com/{repo}/{appId}/{appId}.lua";
                    using var probeReq = new HttpRequestMessage(HttpMethod.Head, probeUrl);
                    using var probeRes = await client.SendAsync(probeReq);

                    if (probeRes.IsSuccessStatusCode)
                    {
                        // For non-Denuvo games, we only need the Lua file!
                        // Fetch the Lua file directly to avoid downloading heavy branch archives
                        if (!isDenuvo)
                        {
                            statusCallback?.Invoke($"Downloading Lua from {repo}...");
                            using var luaRes = await client.GetAsync(probeUrl);
                            if (luaRes.IsSuccessStatusCode)
                            {
                                var luaBytes = await luaRes.Content.ReadAsByteArrayAsync();
                                if (luaBytes != null && luaBytes.Length > 0)
                                {
                                    Logger.Log($"[ManifestHubService] ✓ Downloaded Non-Denuvo Lua directly from {repo}");
                                    return new List<DownloadedGameFile>
                                    {
                                        new DownloadedGameFile
                                        {
                                            FileName = $"{appId}.lua",
                                            Bytes = luaBytes
                                        }
                                    };
                                }
                            }
                        }

                        // For Denuvo games (or fallback if direct Lua download failed), download the archive zip
                        statusCallback?.Invoke($"Downloading files from {repo}...");
                        string zipUrl = $"https://codeload.github.com/{repo}/zip/refs/heads/{appId}";
                        using var zipRes = await client.GetAsync(zipUrl);

                        if (zipRes.IsSuccessStatusCode)
                        {
                            var zipBytes = await zipRes.Content.ReadAsByteArrayAsync();
                            var extractedFiles = ExtractFilesFromZip(zipBytes, appId);

                            bool hasLua = extractedFiles.Any(f => f.FileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
                            bool hasManifest = extractedFiles.Any(f => f.FileName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase));

                            if (isDenuvo)
                            {
                                if (hasLua && hasManifest)
                                {
                                    Logger.Log($"[ManifestHubService] ✓ Found Denuvo files in {repo}: {extractedFiles.Count} files ({extractedFiles.Count(f => f.FileName.EndsWith(".manifest"))} manifests)");
                                    return extractedFiles;
                                }
                                else
                                {
                                    Logger.Log($"[ManifestHubService] AppID {appId} is Denuvo but {repo} does not have required manifest files (hasLua={hasLua}, hasManifest={hasManifest}).");
                                }
                            }
                            else
                            {
                                if (hasLua)
                                {
                                    // User requirement: Only upload Lua file if not Denuvo! Filter out all manifests
                                    var luaOnly = extractedFiles.Where(f => f.FileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToList();
                                    Logger.Log($"[ManifestHubService] ✓ Found Non-Denuvo files in {repo}, filtered to Lua only: {luaOnly.Count} files");
                                    return luaOnly;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ManifestHubService] Warning checking repo {repo}: {ex.Message}");
                }
            }

            // If game is Denuvo and no archive branch had manifests, do not fallback to lua-only (useless for Denuvo)
            if (isDenuvo)
            {
                Logger.Log($"[ManifestHubService] AppID {appId} is Denuvo but no manifest files could be found across archives.");
                return null;
            }

            // For Non-Denuvo games, fallback to KeySteam Lua database (bsinwhg/ManifestHubLua with 86,500+ games)
            statusCallback?.Invoke("Searching KeySteam Lua database...");
            try
            {
                var luaFile = await FindKeySteamLuaAsync(appId);
                if (luaFile != null)
                {
                    Logger.Log($"[ManifestHubService] ✓ Found Lua in KeySteam database for AppID {appId}");
                    return new List<DownloadedGameFile> { luaFile };
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManifestHubService] Error searching KeySteam: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Backward-compatible overload that detects Denuvo status first, then searches ManifestHub.
        /// </summary>
        public static async Task<List<DownloadedGameFile>?> FindAndDownloadFilesAsync(int appId, Action<string>? statusCallback = null)
        {
            bool isDenuvo = await DetectDenuvoAsync(appId, statusCallback);
            return await FindAndDownloadFilesAsync(appId, isDenuvo, statusCallback);
        }

        private static List<DownloadedGameFile> ExtractFilesFromZip(byte[] zipBytes, int appId)
        {
            var result = new List<DownloadedGameFile>();
            using var memStream = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(memStream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    continue;

                string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (extension == ".lua" || extension == ".manifest")
                {
                    using var entryStream = entry.Open();
                    using var ms = new MemoryStream();
                    entryStream.CopyTo(ms);

                    result.Add(new DownloadedGameFile
                    {
                        FileName = entry.Name,
                        Bytes = ms.ToArray()
                    });
                }
            }

            // Ensure the main .lua file has the standard name "{appId}.lua"
            var luaEntry = result.FirstOrDefault(f => f.FileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            if (luaEntry != null && !luaEntry.FileName.Equals($"{appId}.lua", StringComparison.OrdinalIgnoreCase))
            {
                luaEntry.FileName = $"{appId}.lua";
            }

            return result;
        }

        private static async Task<DownloadedGameFile?> FindKeySteamLuaAsync(int appId)
        {
            var client = SharedHttpClient.Instance;
            if (_luaMapCache == null)
            {
                string mapUrl = "https://raw.githubusercontent.com/trionine/ManifestHub/main/data/lua-map.json";
                using var mapRes = await client.GetAsync(mapUrl);
                if (mapRes.IsSuccessStatusCode)
                {
                    var mapJson = await mapRes.Content.ReadAsStringAsync();
                    var parsed = JsonSerializer.Deserialize<LuaMapIndex>(mapJson);
                    lock (_lock)
                    {
                        _luaMapCache = parsed;
                    }
                }
            }

            string appIdStr = appId.ToString();
            string? folderName = null;
            lock (_lock)
            {
                if (_luaMapCache != null &&
                    _luaMapCache.Apps.TryGetValue(appIdStr, out int folderIdx) &&
                    folderIdx >= 0 && folderIdx < _luaMapCache.Folders.Count)
                {
                    folderName = _luaMapCache.Folders[folderIdx];
                }
            }

            if (!string.IsNullOrEmpty(folderName))
            {
                string luaUrl = $"https://raw.githubusercontent.com/bsinwhg/ManifestHubLua/main/luas/{folderName}/{appIdStr}.lua";
                using var luaRes = await client.GetAsync(luaUrl);
                if (luaRes.IsSuccessStatusCode)
                {
                    var bytes = await luaRes.Content.ReadAsByteArrayAsync();
                    if (bytes != null && bytes.Length > 0)
                    {
                        return new DownloadedGameFile
                        {
                            FileName = $"{appId}.lua",
                            Bytes = bytes
                        };
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Uploads each game file into folder {appId}/ on Supabase Storage,
        /// fetches Steam metadata, and inserts a new record into public.hzmanifest_files.
        /// </summary>
        public static async Task<(bool success, string message, string gameName)> UploadFolderToSupabaseAsync(
            int appId,
            List<DownloadedGameFile> files,
            Action<string>? statusCallback = null,
            string? fallbackGameName = null)
        {
            // Enforce requirement: non-Denuvo games must only have Lua files uploaded
            bool isDenuvo = await IsDenuvoGameAsync(appId);
            if (!isDenuvo)
            {
                files = files.Where(f => f.FileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (files == null || files.Count == 0)
            {
                return (false, "No valid files to upload.", string.Empty);
            }

            var client = SharedHttpClient.Instance;
            string supabaseUrl = SupabaseConfig.SupabaseUrl;
            string serviceKey = SupabaseConfig.SupabaseKey;
            string bucketName = SupabaseConfig.StorageBucketName;

            if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(serviceKey) || string.IsNullOrWhiteSpace(bucketName))
            {
                return (false, "Supabase service configuration is missing.", string.Empty);
            }

            long totalBytes = 0;

            // 1. Upload each file to Supabase Storage folder: {appId}/{fileName}
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                totalBytes += file.Bytes.Length;
                statusCallback?.Invoke($"Uploading {file.FileName} ({i + 1}/{files.Count})...");

                string storagePath = $"{appId}/{file.FileName}";
                string uploadEndpoint = $"{supabaseUrl}/storage/v1/object/{bucketName}/{storagePath}";

                using var request = new HttpRequestMessage(HttpMethod.Post, uploadEndpoint);
                request.Headers.Add("apikey", serviceKey);
                request.Headers.Add("Authorization", $"Bearer {serviceKey}");
                request.Headers.Add("x-upsert", "true");
                request.Content = new ByteArrayContent(file.Bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                var response = await client.SendAsync(request);

                // If POST returned conflict, attempt PUT overwrite
                if (!response.IsSuccessStatusCode)
                {
                    using var putRequest = new HttpRequestMessage(HttpMethod.Put, uploadEndpoint);
                    putRequest.Headers.Add("apikey", serviceKey);
                    putRequest.Headers.Add("Authorization", $"Bearer {serviceKey}");
                    putRequest.Headers.Add("x-upsert", "true");
                    putRequest.Content = new ByteArrayContent(file.Bytes);
                    putRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                    response = await client.SendAsync(putRequest);
                }

                if (!response.IsSuccessStatusCode)
                {
                    string errContent = await response.Content.ReadAsStringAsync();
                    Logger.Log($"[ManifestHubService] Failed to upload {file.FileName}: HTTP {(int)response.StatusCode} - {errContent}");
                    return (false, $"Failed to upload file '{file.FileName}' to storage: {response.ReasonPhrase}", string.Empty);
                }

                Logger.Log($"[ManifestHubService] ✓ Uploaded {storagePath} ({file.Bytes.Length} bytes)");
            }

            // 2. Fetch Steam game details (name, description, genre, requirements, adult rating)
            statusCallback?.Invoke("Fetching game metadata from Steam...");
            var steamData = await FetchSteamDetailsAsync(appId);
            string gameName = string.IsNullOrWhiteSpace(steamData.Name) || steamData.Name == $"AppID {appId}"
                ? (!string.IsNullOrWhiteSpace(fallbackGameName) ? fallbackGameName : $"AppID {appId}")
                : steamData.Name;

            // 3. Insert record into public.hzmanifest_files
            statusCallback?.Invoke("Registering game to catalog...");
            string mainFileUrl = $"{supabaseUrl}/storage/v1/object/public/{bucketName}/{appId}/{appId}.lua";
            string formattedSize = FormatFileSize(totalBytes);

            var dbRecord = new Dictionary<string, object?>
            {
                ["app_id"] = appId,
                ["file_name"] = $"{appId}.lua",
                ["name"] = gameName,
                ["description"] = steamData.Description,
                ["genre"] = steamData.Genre,
                ["url"] = mainFileUrl,
                ["file_size"] = formattedSize,
                ["is_adult"] = steamData.IsAdult ? "true" : "false",
                ["download_count"] = 0,
                ["minimum_requirements"] = steamData.MinimumRequirements,
                ["recommended_requirements"] = steamData.RecommendedRequirements,
                ["storage_type"] = "folder",
                ["folder_path"] = $"{appId}/"
            };

            string insertUrl = $"{supabaseUrl}/rest/v1/hzmanifest_files";
            string jsonBody = JsonSerializer.Serialize(dbRecord);

            using var insertRequest = new HttpRequestMessage(HttpMethod.Post, insertUrl);
            insertRequest.Headers.Add("apikey", serviceKey);
            insertRequest.Headers.Add("Authorization", $"Bearer {serviceKey}");
            insertRequest.Headers.Add("Prefer", "return=representation");
            insertRequest.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var insertResponse = await client.SendAsync(insertRequest);

            // If app_id already exists, update via PATCH
            if (!insertResponse.IsSuccessStatusCode && insertResponse.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                string patchUrl = $"{supabaseUrl}/rest/v1/hzmanifest_files?app_id=eq.{appId}";
                using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, patchUrl);
                patchRequest.Headers.Add("apikey", serviceKey);
                patchRequest.Headers.Add("Authorization", $"Bearer {serviceKey}");
                patchRequest.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                insertResponse = await client.SendAsync(patchRequest);
            }

            if (!insertResponse.IsSuccessStatusCode)
            {
                string insertErr = await insertResponse.Content.ReadAsStringAsync();
                Logger.Log($"[ManifestHubService] Failed to insert record into hzmanifest_files: HTTP {(int)insertResponse.StatusCode} - {insertErr}");
                return (false, $"Failed to save catalog record: {insertResponse.ReasonPhrase}", gameName);
            }

            Logger.Log($"[ManifestHubService] ✓ Successfully registered '{gameName}' (AppID: {appId}) into hzmanifest_files");
            return (true, "Success", gameName);
        }

        private static readonly string[] ExcludedCategoryKeywords = new[]
        {
            "achievement", "achievements", "camera", "controller", "cloud",
            "workshop", "trading cards", "anti-cheat", "remote play",
            "vr", "virtual reality", "soundtrack", "stats", "caption",
            "subtitle", "level editor", "mod", "controller support", "steam",
            "in-app purchases", "difficult", "includes", "recommended",
            "early access", "partial controller support", "full controller support",
            "remote play together", "cross-platform", "leaderboard",
            "multi-player", // Exclude generic multiplayer, keep co-op and single-player
            // Input related
            "mouse", "keyboard", "gamepad", "joystick", "touch", "steam controller",
            "partial keyboard support", "full keyboard support", "partial mouse support",
            "full mouse support", "partial gamepad support", "full gamepad support",
            // Audio related
            "volume", "stereo", "surround", "dolby", "mono", "audio", "sound",
            "headphone", "speaker", "volume levels",
            // Other accessibility/feature tags
            "family sharing", "save anytime", "saved anytime", "hdr", "adjustable text size",
            "color blind", "color alternative", "narrated game menus", 
            "playable without timed input", "timed input"
        };

        private static bool IsRelevantCategory(string? description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return false;

            string lower = description.ToLowerInvariant();
            if (lower.Contains("horror"))
                return true;

            foreach (var kw in ExcludedCategoryKeywords)
            {
                if (lower.Contains(kw))
                    return false;
            }

            return true;
        }

        private static async Task<List<string>> GetSteamPageTagsAsync(int appId)
        {
            var tags = new List<string>();
            try
            {
                var client = SharedHttpClient.Instance;
                string url = $"https://store.steampowered.com/app/{appId}/";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Cookie", "birthtime=568022401; mature_content=1");
                using var res = await client.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                    return tags;

                var html = await res.Content.ReadAsStringAsync();
                var matches = Regex.Matches(html, @"<a\b([^>]*)class=""([^""]*app_tag[^""]*)""([^>]*)>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in matches)
                {
                    string classAttr = m.Groups[2].Value;
                    if (classAttr.IndexOf("add_button", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    string rawText = m.Groups[4].Value;
                    string cleanText = Regex.Replace(rawText, @"<[^>]+>", string.Empty);
                    cleanText = System.Net.WebUtility.HtmlDecode(cleanText).Trim();

                    if (!string.IsNullOrWhiteSpace(cleanText) && cleanText != "+" && !seen.Contains(cleanText))
                    {
                        seen.Add(cleanText);
                        tags.Add(cleanText);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManifestHubService] Error fetching Steam page tags for {appId}: {ex.Message}");
            }
            return tags;
        }

        private static string CleanHtmlTags(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // Remove <br/> and <br > tags -> newline (matching fetch_steam_requirements.py)
            string cleaned = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            // Remove all other HTML tags
            cleaned = Regex.Replace(cleaned, @"<[^>]+>", string.Empty);
            // Clean up entities and whitespace
            cleaned = System.Net.WebUtility.HtmlDecode(cleaned);
            return cleaned.Trim();
        }

        private static async Task<SteamAppDetailsResponse> FetchSteamDetailsAsync(int appId)
        {
            var result = new SteamAppDetailsResponse
            {
                Name = $"AppID {appId}"
            };

            var collectedGenres = new List<string>();

            try
            {
                var client = SharedHttpClient.Instance;
                string url = $"https://store.steampowered.com/api/appdetails?appids={appId}&cc=US&l=english";
                using var res = await client.GetAsync(url);
                if (res.IsSuccessStatusCode)
                {
                    var content = await res.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(content);

                    JsonElement appElement = default;
                    bool hasAppElement = false;

                    // 1. Try matching the exact appId string first
                    if (doc.RootElement.TryGetProperty(appId.ToString(), out appElement))
                    {
                        hasAppElement = true;
                    }
                    else
                    {
                        // Fallback: Steam API sometimes redirects regional or bundle appids,
                        // returning a different root key (e.g. 3575180 instead of 2668510 for Red Dead Redemption).
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.TryGetProperty("success", out var sEl) && sEl.GetBoolean())
                            {
                                appElement = prop.Value;
                                hasAppElement = true;
                                break;
                            }
                        }
                    }

                    if (hasAppElement &&
                        appElement.TryGetProperty("success", out var successEl) &&
                        successEl.GetBoolean() &&
                        appElement.TryGetProperty("data", out var dataEl))
                    {
                        if (dataEl.TryGetProperty("name", out var nameEl))
                            result.Name = nameEl.GetString() ?? result.Name;

                        if (dataEl.TryGetProperty("short_description", out var descEl))
                            result.Description = descEl.GetString() ?? "";

                        // 1. Official genres from data.genres (reference: refresh_game_genres.py and update_is_adult_field.py)
                        var officialGenres = new List<string>();
                        if (dataEl.TryGetProperty("genres", out var genresArr) && genresArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var g in genresArr.EnumerateArray())
                            {
                                if (g.TryGetProperty("description", out var gDesc))
                                {
                                    var val = gDesc.GetString()?.Trim();
                                    if (!string.IsNullOrWhiteSpace(val))
                                    {
                                        officialGenres.Add(val);
                                        collectedGenres.Add(val);
                                    }
                                }
                            }
                        }

                        // 2. Categories from data.categories filtered by IsRelevantCategory (reference: refresh_game_genres.py)
                        if (dataEl.TryGetProperty("categories", out var catArr) && catArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var c in catArr.EnumerateArray())
                            {
                                if (c.TryGetProperty("description", out var cDesc))
                                {
                                    var val = cDesc.GetString()?.Trim();
                                    if (!string.IsNullOrWhiteSpace(val) && IsRelevantCategory(val))
                                    {
                                        collectedGenres.Add(val);
                                    }
                                }
                            }
                        }

                        // 3. User-defined tags from store page HTML filtered by IsRelevantCategory (reference: refresh_game_genres.py)
                        var pageTags = await GetSteamPageTagsAsync(appId);
                        foreach (var tag in pageTags)
                        {
                            if (IsRelevantCategory(tag))
                            {
                                collectedGenres.Add(tag);
                            }
                        }

                        // De-duplicate genres while preserving order (reference: refresh_game_genres.py)
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var genresFiltered = new List<string>();
                        foreach (var item in collectedGenres)
                        {
                            if (!string.IsNullOrWhiteSpace(item) && !seen.Contains(item))
                            {
                                genresFiltered.Add(item);
                                seen.Add(item);
                            }
                        }
                        result.Genre = string.Join(", ", genresFiltered);

                        // 4. System requirements (reference: fetch_steam_requirements.py)
                        if (dataEl.TryGetProperty("pc_requirements", out var pcReqEl) && pcReqEl.ValueKind == JsonValueKind.Object)
                        {
                            if (pcReqEl.TryGetProperty("minimum", out var minEl))
                                result.MinimumRequirements = CleanHtmlTags(minEl.GetString());

                            if (pcReqEl.TryGetProperty("recommended", out var recEl))
                                result.RecommendedRequirements = CleanHtmlTags(recEl.GetString());
                        }

                        // 5. Is Adult detection (exact reference: update_is_adult_field.py)
                        // Uses ONLY content_descriptors [3, 4] and official Steam store genres (not community tags)
                        bool isAdult = false;

                        // Check content descriptors: IDs 3 = Sexual Content, 4 = Nudity
                        if (dataEl.TryGetProperty("content_descriptors", out var cdEl) && cdEl.ValueKind == JsonValueKind.Object)
                        {
                            if (cdEl.TryGetProperty("ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var id in idsEl.EnumerateArray())
                                {
                                    if (id.TryGetInt32(out int cid) && (cid == 3 || cid == 4))
                                    {
                                        isAdult = true;
                                        break;
                                    }
                                }
                            }
                        }

                        // Check ONLY official genres (not user tags) for adult keywords
                        if (!isAdult && officialGenres.Count > 0)
                        {
                            var adultKeywords = new[] { "nudity", "sexual content", "erotic", "adults only" };
                            string genresLower = string.Join(", ", officialGenres).ToLowerInvariant();
                            if (adultKeywords.Any(kw => genresLower.Contains(kw)))
                            {
                                isAdult = true;
                            }
                        }

                        result.IsAdult = isAdult;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManifestHubService] Error fetching Steam details for {appId}: {ex.Message}");
            }

            // Fallback: If name or genre is still missing, scrape the Steam store page directly
            if (string.IsNullOrWhiteSpace(result.Name) || result.Name == $"AppID {appId}" || string.IsNullOrWhiteSpace(result.Genre))
            {
                await EnrichFromSteamStorePageAsync(appId, result);
            }

            return result;
        }

        private static async Task EnrichFromSteamStorePageAsync(int appId, SteamAppDetailsResponse result)
        {
            try
            {
                var client = SharedHttpClient.Instance;
                string storeUrl = $"https://store.steampowered.com/app/{appId}/";
                using var req = new HttpRequestMessage(HttpMethod.Get, storeUrl);
                req.Headers.Add("Cookie", "birthtime=568022401; mature_content=1");
                using var res = await client.SendAsync(req);
                if (!res.IsSuccessStatusCode) return;

                var html = await res.Content.ReadAsStringAsync();

                // Extract Game Name if missing
                if (string.IsNullOrWhiteSpace(result.Name) || result.Name == $"AppID {appId}")
                {
                    var nameMatch = Regex.Match(html, @"<div[^>]*class=['""]apphub_AppName['""][^>]*>([^<]+)</div>", RegexOptions.IgnoreCase);
                    if (nameMatch.Success)
                    {
                        result.Name = System.Net.WebUtility.HtmlDecode(nameMatch.Groups[1].Value.Trim());
                    }
                    else
                    {
                        var titleMatch = Regex.Match(html, @"<meta[^>]*property=['""]og:title['""][^>]*content=['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
                        if (titleMatch.Success)
                        {
                            result.Name = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
                        }
                    }
                }

                // Extract Description if missing
                if (string.IsNullOrWhiteSpace(result.Description))
                {
                    var descMatch = Regex.Match(html, @"<div[^>]*class=['""]game_description_snippet['""][^>]*>([^<]+)</div>", RegexOptions.IgnoreCase);
                    if (descMatch.Success)
                    {
                        result.Description = System.Net.WebUtility.HtmlDecode(descMatch.Groups[1].Value.Trim());
                    }
                }

                // Extract Genre if missing (using IsRelevantCategory and user-defined tags)
                if (string.IsNullOrWhiteSpace(result.Genre))
                {
                    var pageTags = await GetSteamPageTagsAsync(appId);
                    var relevantTags = pageTags.Where(IsRelevantCategory).ToList();
                    if (relevantTags.Count > 0)
                    {
                        result.Genre = string.Join(", ", relevantTags);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ManifestHubService] Error scraping Steam store page for {appId}: {ex.Message}");
            }
        }

        private static string FormatFileSize(long bytes)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;

            if (bytes >= GB)
                return $"{(double)bytes / GB:F2} GB";
            if (bytes >= MB)
                return $"{(double)bytes / MB:F2} MB";
            if (bytes >= KB)
                return $"{(double)bytes / KB:F2} KB";

            return $"{bytes} B";
        }
    }
}
