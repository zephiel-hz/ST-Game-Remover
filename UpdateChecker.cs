using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        private const string GitHubApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

        public static async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            try
            {
                var currentVersion = GetCurrentVersion();
                
                using (var client = new HttpClient())
                {
                    // GitHub API requires User-Agent header
                    client.DefaultRequestHeaders.Add("User-Agent", "SteamPluginManager");
                    client.Timeout = TimeSpan.FromSeconds(10);

                    var response = await client.GetAsync(GitHubApiUrl);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        return new UpdateInfo
                        {
                            CurrentVersion = currentVersion,
                            HasUpdate = false,
                            ErrorMessage = $"Failed to fetch release info: {response.StatusCode}"
                        };
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    var release = JsonSerializer.Deserialize<GitHubRelease>(content);

                    if (release == null)
                    {
                        return new UpdateInfo
                        {
                            CurrentVersion = currentVersion,
                            HasUpdate = false,
                            ErrorMessage = "Failed to parse release info"
                        };
                    }

                    // Skip draft and prerelease versions
                    if (release.Draft || release.Prerelease)
                    {
                        return new UpdateInfo
                        {
                            CurrentVersion = currentVersion,
                            LatestVersion = release.TagName,
                            HasUpdate = false,
                            ErrorMessage = "Latest release is a pre-release version"
                        };
                    }

                    var latestVersion = release.TagName?.TrimStart('v') ?? "0.0.0";
                    var hasUpdate = CompareVersions(currentVersion, latestVersion) < 0;

                    // Find .exe or .zip download link
                    string? downloadUrl = null;
                    if (release.Assets != null && release.Assets.Length > 0)
                    {
                        foreach (var asset in release.Assets)
                        {
                            if (asset.Name?.EndsWith(".exe") == true || asset.Name?.EndsWith(".zip") == true)
                            {
                                downloadUrl = asset.BrowserDownloadUrl;
                                break;
                            }
                        }
                    }

                    return new UpdateInfo
                    {
                        CurrentVersion = currentVersion,
                        LatestVersion = latestVersion,
                        HasUpdate = hasUpdate,
                        ReleaseNotes = release.Body,
                        DownloadUrl = downloadUrl
                    };
                }
            }
            catch (HttpRequestException ex)
            {
                return new UpdateInfo
                {
                    HasUpdate = false,
                    ErrorMessage = $"Network error: {ex.Message}"
                };
            }
            catch (TaskCanceledException)
            {
                return new UpdateInfo
                {
                    HasUpdate = false,
                    ErrorMessage = "Request timeout"
                };
            }
            catch (Exception ex)
            {
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
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return $"{version?.Major}.{version?.Minor}.{version?.Build}";
            }
            catch
            {
                return "1.0.0";
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
                var version1 = new Version(v1);
                var version2 = new Version(v2);
                return version1.CompareTo(version2);
            }
            catch
            {
                return 0; // If parsing fails, consider them equal
            }
        }

        public static async Task<bool> DownloadUpdateAsync(string downloadUrl, string savePath, Action<long, long>? progressCallback = null)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "SteamPluginManager");
                    client.Timeout = TimeSpan.FromMinutes(5);

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
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to download update: {ex.Message}");
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

                // Jalankan installer
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true,
                    Verb = "runas" // Request admin privileges
                };

                System.Diagnostics.Process.Start(processInfo);

                return (true, "Update installer started successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to download and install update: {ex.Message}");
                return (false, $"Error: {ex.Message}");
            }
        }
    }
}
