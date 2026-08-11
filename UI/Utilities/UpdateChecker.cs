using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    public class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime PublishedAt { get; set; }
    }

    public class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    public class UpdateInfo
    {
        public string? LatestVersion { get; set; }
        public string? CurrentVersion { get; set; }
        public string? ReleaseNotes { get; set; }
        public string? DownloadUrl { get; set; }
        public bool HasUpdate { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public static class UpdateChecker
    {
        private const string GitHubOwner = "zephiel-hz"; // Change this to your GitHub username
        private const string GitHubRepo = "ST-Game-Remover"; // Change this to your repo name
        // NOTE: Cannot use const with string interpolation; using readonly to allow proper string interpolation
        private static readonly string GitHubApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
        private static readonly string GitHubChangelogFileUrl = $"https://raw.githubusercontent.com/{GitHubOwner}/{GitHubRepo}/main/CHANGELOG.md";

        public static async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            try
            {
                Logger.Log("[UpdateChecker] Starting update check...");
                var currentVersion = GetCurrentVersion();
                Logger.Log($"[UpdateChecker] Current version: {currentVersion}");
                
                var release = await GetLatestReleaseAsync();
                if (release == null)
                {
                    Logger.Log("[UpdateChecker] Failed to fetch release info from GitHub");
                    return new UpdateInfo
                    {
                        CurrentVersion = currentVersion,
                        HasUpdate = false,
                        ErrorMessage = "Failed to fetch release info"
                    };
                }

                // Skip draft and prerelease versions
                if (release.Draft || release.Prerelease)
                {
                    Logger.Log($"[UpdateChecker] Latest release is a pre-release: {release.TagName}");
                    return new UpdateInfo
                    {
                        CurrentVersion = currentVersion,
                        LatestVersion = release.TagName,
                        HasUpdate = false,
                        ErrorMessage = "Latest release is a pre-release version"
                    };
                }

                var latestVersion = release.TagName?.TrimStart('v') ?? "0.0.0";
                Logger.Log($"[UpdateChecker] Latest version: {latestVersion}");
                var hasUpdate = CompareVersions(currentVersion, latestVersion) < 0;
                Logger.Log($"[UpdateChecker] Has update: {hasUpdate}");

                // Find .exe or .zip download link
                string? downloadUrl = null;
                if (release.Assets != null && release.Assets.Length > 0)
                {
                    foreach (var asset in release.Assets)
                    {
                        // Prefer BrowserDownloadUrl (GitHub CDN), fallback to DownloadUrl
                        if (asset.Name?.EndsWith(".exe") == true || asset.Name?.EndsWith(".zip") == true)
                        {
                            downloadUrl = !string.IsNullOrEmpty(asset.BrowserDownloadUrl) 
                                ? asset.BrowserDownloadUrl 
                                : asset.DownloadUrl;
                            Logger.Log($"[UpdateChecker] Found download URL for: {asset.Name}");
                            break;
                        }
                    }
                }

                var releaseNotes = !string.IsNullOrWhiteSpace(release.Body)
                    ? release.Body
                    : await GetLatestChangelogAsync();

                // If no valid download URL found, still return update info.
                // Don't treat missing asset as fatal when a newer release exists;
                // dashboard can still notify the user even if `DownloadUrl` is null.
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    Logger.Log("[UpdateChecker] No valid download URL found in release assets");
                    return new UpdateInfo
                    {
                        CurrentVersion = currentVersion,
                        LatestVersion = latestVersion,
                        HasUpdate = hasUpdate,
                        ReleaseNotes = releaseNotes,
                        DownloadUrl = null,
                        ErrorMessage = hasUpdate ? null : "No valid download URL found in release assets"
                    };
                }

                Logger.Log("[UpdateChecker] Update check completed successfully");
                return new UpdateInfo
                {
                    CurrentVersion = currentVersion,
                    LatestVersion = latestVersion,
                    HasUpdate = hasUpdate,
                    ReleaseNotes = releaseNotes,
                    DownloadUrl = downloadUrl
                };
            }
            catch (HttpRequestException ex)
            {
                Logger.Log($"[UpdateChecker] Network error: {ex.Message}");
                return new UpdateInfo
                {
                    HasUpdate = false,
                    ErrorMessage = $"Network error: {ex.Message}"
                };
            }
            catch (TaskCanceledException)
            {
                Logger.Log("[UpdateChecker] Request timeout");
                return new UpdateInfo
                {
                    HasUpdate = false,
                    ErrorMessage = "Request timeout"
                };
            }
            catch (Exception ex)
            {
                Logger.Log($"[UpdateChecker] Error: {ex.GetType().Name}: {ex.Message}");
                return new UpdateInfo
                {
                    HasUpdate = false,
                    ErrorMessage = $"Error checking for updates: {ex.Message}"
                };
            }
        }

        public static string GetCurrentVersion()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? System.Reflection.Assembly.GetExecutingAssembly();

                var informationalVersionAttribute = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault();
                var informationalVersion = informationalVersionAttribute?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informationalVersion))
                {
                    return NormalizeVersionString(informationalVersion);
                }

                var fileVersionAttribute = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyFileVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyFileVersionAttribute>()
                    .FirstOrDefault();
                var fileVersion = fileVersionAttribute?.Version;
                if (!string.IsNullOrWhiteSpace(fileVersion))
                {
                    return NormalizeVersionString(fileVersion);
                }

                var version = assembly.GetName().Version;
                if (version != null)
                {
                    return $"{version.Major}.{version.Minor}.{version.Build}";
                }

                return "0.0.0";
            }
            catch
            {
                return "0.0.0";
            }
        }

        /// <summary>
        /// Compares two version strings.
        /// Returns: -1 if v1 < v2, 0 if v1 == v2, 1 if v1 > v2
        /// </summary>
        private static int CompareVersions(string v1, string v2)
        {
            try
            {
                v1 = NormalizeVersionString(v1);
                v2 = NormalizeVersionString(v2);

                // Try normal Version parsing first
                if (Version.TryParse(v1, out var ver1) && Version.TryParse(v2, out var ver2))
                {
                    return ver1.CompareTo(ver2);
                }

                // Fallback: extract numeric parts and compare component-wise
                int[] p1 = ParseVersionParts(v1);
                int[] p2 = ParseVersionParts(v2);
                int len = Math.Max(p1.Length, p2.Length);
                for (int i = 0; i < len; i++)
                {
                    int a = i < p1.Length ? p1[i] : 0;
                    int b = i < p2.Length ? p2[i] : 0;
                    if (a < b) return -1;
                    if (a > b) return 1;
                }

                return 0;
            }
            catch
            {
                return 0; // If anything unexpected happens, treat as equal
            }
        }

        private static int[] ParseVersionParts(string v)
        {
            try
            {
                if (string.IsNullOrEmpty(v)) return new int[0];
                // Remove leading 'v' or other non-digit prefixes
                var cleaned = v.Trim();
                if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned.Substring(1);
                // Keep only digits and dots
                var parts = cleaned.Split(new[] { '.', '-' , '+' }, StringSplitOptions.RemoveEmptyEntries);
                var nums = parts.Select(p => {
                    // extract leading number
                    var digits = new string(p.TakeWhile(c => char.IsDigit(c)).ToArray());
                    if (int.TryParse(digits, out var n)) return n;
                    return 0;
                }).ToArray();
                return nums;
            }
            catch
            {
                return new int[0];
            }
        }

        private static string NormalizeVersionString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "0.0.0";
            }

            var cleaned = value.Trim();
            if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(1);
            }

            // Take only the first numeric portion and dots, remove prerelease suffixes
            var match = Regex.Match(cleaned, "^(\\d+(?:\\.\\d+){0,3})");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return cleaned;
        }

        private static async Task<GitHubRelease?> GetLatestReleaseAsync()
        {
            try
            {
                var client = SharedHttpClient.Instance;
                // Ensure User-Agent is set (required by GitHub API)
                if (!client.DefaultRequestHeaders.Contains("User-Agent"))
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "SteamPluginManager");
                }

                Logger.Log($"[UpdateChecker] Fetching latest release from: {GitHubApiUrl}");
                var response = await client.GetAsync(GitHubApiUrl);
                
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"[UpdateChecker] GitHub API error: {response.StatusCode} {response.ReasonPhrase}");
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        Logger.Log("[UpdateChecker] Possibly rate limited by GitHub API (60 requests/hour for unauthenticated)");
                    }
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                Logger.Log("[UpdateChecker] Successfully fetched release info from GitHub");
                return JsonSerializer.Deserialize<GitHubRelease>(content);
            }
            catch (HttpRequestException ex)
            {
                Logger.Log($"[UpdateChecker] Network error fetching release: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"[UpdateChecker] Error fetching release: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public static async Task<string?> GetLatestChangelogAsync()
        {
            try
            {
                var release = await GetLatestReleaseAsync();
                if (release != null && !string.IsNullOrWhiteSpace(release.Body))
                {
                    Logger.Log("[UpdateChecker] Using release notes from GitHub release body");
                    return release.Body.Trim();
                }

                Logger.Log($"[UpdateChecker] Fetching changelog from: {GitHubChangelogFileUrl}");
                var client = SharedHttpClient.Instance;
                if (!client.DefaultRequestHeaders.Contains("User-Agent"))
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "SteamPluginManager");
                }

                var response = await client.GetAsync(GitHubChangelogFileUrl);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"[UpdateChecker] Failed to fetch changelog: {response.StatusCode} {response.ReasonPhrase}");
                    return null;
                }

                var changelog = await response.Content.ReadAsStringAsync();
                Logger.Log("[UpdateChecker] Successfully fetched changelog from repository");
                return string.IsNullOrWhiteSpace(changelog) ? null : changelog.Trim();
            }
            catch (HttpRequestException ex)
            {
                Logger.Log($"[UpdateChecker] Network error fetching changelog: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"[UpdateChecker] Error fetching changelog: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public static async Task<bool> DownloadUpdateAsync(string downloadUrl, string savePath, Action<long, long>? progressCallback = null)
        {
            try
            {
                // Validate input
                if (string.IsNullOrEmpty(downloadUrl) || string.IsNullOrEmpty(savePath))
                {
                    return false;
                }

                var client = SharedHttpClient.Instance;
                if (!client.DefaultRequestHeaders.Contains("User-Agent"))
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "SteamPluginManager");
                }

                using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return false;
                    }

                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var totalRead = 0L;
                        var buffer = new byte[8192];
                        int read;

                        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, read);
                            totalRead += read;
                            progressCallback?.Invoke(totalRead, totalBytes);
                        }
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                // Log the exception for debugging
                System.Diagnostics.Debug.WriteLine($"Download failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Download dan instal update secara langsung
        /// </summary>
        public static async Task<(bool success, string message)> DownloadAndInstallUpdateAsync(string downloadUrl, Action<long, long>? progressCallback = null)
        {
            string tempPath = "";
            try
            {
                // Buat folder temp untuk download
                string tempDir = Path.Combine(Path.GetTempPath(), "SteamPluginManagerUpdate");
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }

                // Tentukan nama file berdasarkan URL
                var uri = new Uri(downloadUrl);
                string fileName = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = "update.exe";
                }

                tempPath = Path.Combine(tempDir, fileName);

                // Hapus file lama jika ada
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                // Download file
                bool downloadSuccess = await DownloadUpdateAsync(downloadUrl, tempPath, progressCallback);
                if (!downloadSuccess)
                {
                    return (false, "Failed to download update file");
                }

                // Verifikasi file terunduh
                if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                {
                    return (false, "Downloaded file is invalid");
                }

                // Jalankan installer.
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true,
                    Verb = "runas" // Request admin privileges
                };

                System.Diagnostics.Process.Start(processInfo);

                // Immediately terminate this application so the installer can update files.
                try
                {
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                }
                catch
                {
                    Environment.Exit(0);
                }

                return (true, "Update installer started successfully");
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}");
            }
        }
    }
}
