using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace SteamPluginManager.Services.ResourcePipeline
{
    public class OnlineFixResourceService : IResourceService
    {
        public string ServiceName => "Online-Fix";

        private readonly HttpClient _client;
        private readonly CookieContainer _cookieContainer;

        public OnlineFixResourceService()
        {
            _cookieContainer = new CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = _cookieContainer,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            _client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        public async Task<ResourceSearchResult?> SearchResourceAsync(string gameName, int appId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                return null;

            var settings = ServiceConfiguration.Current.ResourceSource;
            var baseUrl = settings.UploadsBaseUrl.TrimEnd('/');

            // Generate search candidates for the directory name
            var candidates = GenerateDirectoryCandidates(gameName);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dirUrl = $"{baseUrl}/uploads/{Uri.EscapeDataString(candidate)}/Fix Repair/";
                var result = await TryInspectDirectoryAsync(dirUrl, candidate, cancellationToken);
                if (result != null)
                    return result;

                // Also try without "Fix Repair/" in case the archive is directly in the game folder
                var directUrl = $"{baseUrl}/uploads/{Uri.EscapeDataString(candidate)}/";
                result = await TryInspectDirectoryAsync(directUrl, candidate, cancellationToken);
                if (result != null)
                    return result;
            }

            return null;
        }

        private async Task<ResourceSearchResult?> TryInspectDirectoryAsync(string dirUrl, string gameTitle, CancellationToken cancellationToken)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, dirUrl);
                using var res = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken);

                if (!res.IsSuccessStatusCode)
                    return null;

                var html = await res.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(html))
                    return null;

                // Match anchor tags href
                var matches = Regex.Matches(html, @"href\s*=\s*[""']([^""']+\.(?:rar|zip|7z))[""']", RegexOptions.IgnoreCase);
                if (matches.Count == 0)
                    return null;

                // STRICT FILTER: Hanya ambil berkas yang memuat kata "Steam" (tanpa alternatif berkas lain)
                string? steamFile = null;
                foreach (Match match in matches)
                {
                    var file = match.Groups[1].Value.Trim();
                    if (file.StartsWith("../") || file.StartsWith("/"))
                        continue;

                    // Wajib mengandung kata "Steam"
                    if (file.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        steamFile = file;
                        break; // Ditemukan berkas Steam, langsung gunakan tanpa mencari alternatif lain
                    }
                }

                // Jika TIDAK ADA berkas yang mengandung kata "Steam", langsung batalkan (jangan cari alternatif)
                if (string.IsNullOrWhiteSpace(steamFile))
                {
                    Logger.Log($"[OnlineFixResourceService] Tidak ditemukan berkas arsip dengan kata 'Steam' di {dirUrl}. Proses dihentikan.");
                    return null;
                }

                var downloadUrl = dirUrl.TrimEnd('/') + "/" + steamFile.TrimStart('/');
                var ext = Path.GetExtension(steamFile).TrimStart('.').ToLowerInvariant();

                return new ResourceSearchResult(
                    Title: gameTitle,
                    DownloadUrl: downloadUrl,
                    DirectoryUrl: dirUrl,
                    FileName: steamFile,
                    FileSizeBytes: -1,
                    SourceName: ServiceName,
                    ArchiveFormat: ext
                );
            }
            catch (Exception ex)
            {
                Logger.Log($"[OnlineFixResourceService] Inspect directory failed for {dirUrl}: {ex.Message}");
                return null;
            }
        }

        private static List<string> GenerateDirectoryCandidates(string gameName)
        {
            var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var raw = gameName.Trim();
            list.Add(raw);

            // Without colon or special punctuation
            var noPunctuation = Regex.Replace(raw, @"[:;?!]", "");
            if (!string.Equals(noPunctuation, raw, StringComparison.OrdinalIgnoreCase))
                list.Add(noPunctuation.Trim());

            // Replace special dashes / hyphens with space
            var dashToSpace = Regex.Replace(noPunctuation, @"[-–—]", " ");
            dashToSpace = Regex.Replace(dashToSpace, @"\s+", " ").Trim();
            if (!string.Equals(dashToSpace, raw, StringComparison.OrdinalIgnoreCase))
                list.Add(dashToSpace);

            // If contains trademark symbols ™, ®, etc.
            var cleanTrade = Regex.Replace(raw, @"[™®©]", "").Trim();
            cleanTrade = Regex.Replace(cleanTrade, @"\s+", " ").Trim();
            if (!string.Equals(cleanTrade, raw, StringComparison.OrdinalIgnoreCase))
                list.Add(cleanTrade);

            return list.ToList();
        }

        public async Task<string> DownloadResourceAsync(
            ResourceSearchResult resource, 
            string targetDirectory, 
            IProgress<ResourceWorkflowProgress>? progress = null, 
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(targetDirectory);
            var safeFileName = Path.GetFileName(Uri.UnescapeDataString(resource.FileName));
            if (string.IsNullOrWhiteSpace(safeFileName))
                safeFileName = $"resource_{Guid.NewGuid():N}.{resource.ArchiveFormat}";

            var destinationPath = Path.Combine(targetDirectory, safeFileName);

            using var req = new HttpRequestMessage(HttpMethod.Get, resource.DownloadUrl);
            if (!string.IsNullOrWhiteSpace(resource.DirectoryUrl))
            {
                req.Headers.Referrer = new Uri(resource.DirectoryUrl);
            }

            using var response = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Failed to download resource from {resource.DownloadUrl}: HTTP {response.StatusCode}");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var localFile = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;

            while ((read = await remoteStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await localFile.WriteAsync(buffer, 0, read, cancellationToken);
                totalRead += read;

                if (totalBytes > 0 && progress != null)
                {
                    int pct = (int)((totalRead * 100L) / totalBytes);
                    progress.Report(new ResourceWorkflowProgress(
                        WorkflowStage.Downloading,
                        pct,
                        $"Downloading ({totalRead / (1024 * 1024)}MB / {totalBytes / (1024 * 1024)}MB)...",
                        totalRead,
                        totalBytes));
                }
            }

            progress?.Report(new ResourceWorkflowProgress(
                WorkflowStage.Downloading,
                100,
                $"Download completed ({totalRead / (1024 * 1024)}MB)",
                totalRead,
                totalRead));

            return destinationPath;
        }

        public async Task<string> ExtractArchiveAsync(
            string archiveFilePath, 
            string outputDirectory, 
            string? password = null, 
            IProgress<ResourceWorkflowProgress>? progress = null, 
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(archiveFilePath))
                throw new FileNotFoundException($"Archive file to extract does not exist: {archiveFilePath}");

            Directory.CreateDirectory(outputDirectory);

            await Task.Run(() =>
            {
                var readerOptions = new ReaderOptions();
                if (!string.IsNullOrEmpty(password))
                {
                    readerOptions.Password = password;
                }

                using var archive = ArchiveFactory.Open(archiveFilePath, readerOptions);
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                int totalCount = entries.Count;
                int current = 0;

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    entry.WriteToDirectory(outputDirectory, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });

                    current++;
                    if (progress != null && totalCount > 0)
                    {
                        int pct = (current * 100) / totalCount;
                        progress.Report(new ResourceWorkflowProgress(
                            WorkflowStage.Extracting,
                            pct,
                            $"Extracting ({current}/{totalCount}): {entry.Key}"));
                    }
                }
            }, cancellationToken);

            return outputDirectory;
        }

        private static readonly HashSet<string> ProtectedGameFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "engine",
            "bin",
            "binaries",
            "bin64",
            "bin32",
            "bin_x64",
            "content",
            "data",
            "assets",
            "resources",
            "plugins",
            "easyanticheat",
            "battleye",
            "anticheat",
            "nativemods",
            "mods",
            "system",
            "client",
            "server",
            "packages",
            "media",
            "sound",
            "audio",
            "scripts"
        };

        /// <summary>
        /// Mengecek apakah suatu subdirektori adalah wrapper folder yang aman di-unwrap, atau folder game asli yang wajib dipertahankan.
        /// </summary>
        public static bool IsWrapperDirectory(string dirPath, string? archiveFileName = null, string? gameTitle = null)
        {
            string dirName = Path.GetFileName(dirPath);
            if (string.IsNullOrWhiteSpace(dirName))
                return false;

            // 1. DILARANG UNWRAP: Jangan pernah unwrap folder game asli yang berada dalam whitelist atau berakhiran _Data (Unity)
            if (ProtectedGameFolderNames.Contains(dirName) || dirName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 2. Keyword check: Pola penamaan khas scene/onlinefix/wrapper
            string lower = dirName.ToLowerInvariant();
            if (lower.Contains("fix_repair") || 
                lower.Contains("fix repair") || 
                lower.Contains("_fix_") || 
                lower.EndsWith("_fix") || 
                lower.EndsWith("-fix") || 
                lower.Contains("fix_steam") || 
                lower.Contains("steam_generic") || 
                lower.Contains("onlinefix") || 
                lower.Contains("online-fix") || 
                lower.Contains("goldberg") || 
                lower.Contains("crack") || 
                lower.Contains("nodvd"))
            {
                return true;
            }

            // 3. Nama folder sama persis dengan nama file arsip sumber tanpa ekstensi (misal: BOMBANANA!_Fix_Repair_Steam_Generic)
            if (!string.IsNullOrWhiteSpace(archiveFileName))
            {
                string archiveBase = Path.GetFileNameWithoutExtension(archiveFileName);
                if (string.Equals(dirName, archiveBase, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 4. Nama folder sama persis dengan judul game
            if (!string.IsNullOrWhiteSpace(gameTitle))
            {
                if (string.Equals(dirName, gameTitle.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 5. Memeriksa apakah di dalam folder tersebut terdapat file crack/eksekusi root secara langsung
            try
            {
                var files = Directory.GetFiles(dirPath);
                bool hasRootCrackFiles = files.Any(f =>
                {
                    var fn = Path.GetFileName(f).ToLowerInvariant();
                    return fn == "onlinefix.ini" || 
                           fn == "onlinefix64.dll" || 
                           fn == "onlinefix.dll" || 
                           fn == "steamoverlay64.dll" || 
                           fn == "steamoverlay.dll" || 
                           fn == "steam_interfaces.txt" ||
                           fn.StartsWith("steam_api");
                });

                if (hasRootCrackFiles)
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Menemukan direktori konten efektif dengan mengabaikan wrapper folder tingkat atas jika ada
        /// (misal: "BOMBANANA!_Fix_Repair_Steam_Generic" hasil ekstrak RAR yang membungkus semua file),
        /// namun tetap mempertahankan folder game asli (seperti "Engine", "Game_Data", "Binaries").
        /// </summary>
        public static string ResolveEffectiveContentDirectory(string baseDirectory, string? archiveFileName = null, string? gameTitle = null)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
                return baseDirectory;

            string current = baseDirectory;

            while (true)
            {
                var dirs = Directory.GetDirectories(current);
                var files = Directory.GetFiles(current);

                // Filter out non-essential metadata files at root (e.g. .url, .txt, .nfo, .html)
                var meaningfulFiles = files.Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext != ".url" && ext != ".txt" && ext != ".nfo" && ext != ".html";
                }).ToArray();

                // If exactly ONE directory, NO meaningful files alongside it, and it qualifies as a wrapper directory
                if (dirs.Length == 1 && meaningfulFiles.Length == 0 && IsWrapperDirectory(dirs[0], archiveFileName, gameTitle))
                {
                    current = dirs[0];
                }
                else
                {
                    break;
                }
            }

            return current;
        }

        public async Task<string> CreateZipArchiveAsync(
            string sourceDirectory, 
            string outputZipPath, 
            string? archiveFileName,
            string? gameTitle,
            IProgress<ResourceWorkflowProgress>? progress = null, 
            CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException($"Source directory to compress does not exist: {sourceDirectory}");

            var outDir = Path.GetDirectoryName(outputZipPath);
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            if (File.Exists(outputZipPath))
                File.Delete(outputZipPath);

            var effectiveDir = ResolveEffectiveContentDirectory(sourceDirectory, archiveFileName, gameTitle);
            if (!string.Equals(effectiveDir, sourceDirectory, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log($"[OnlineFixResourceService] Unwrapped single wrapper directory. Compressing effective directory: {Path.GetFileName(effectiveDir)}");
            }

            progress?.Report(new ResourceWorkflowProgress(WorkflowStage.Compressing, 10, "Creating standardized ZIP archive..."));

            await Task.Run(() =>
            {
                ZipFile.CreateFromDirectory(effectiveDir, outputZipPath, CompressionLevel.Optimal, false);
            }, cancellationToken);

            progress?.Report(new ResourceWorkflowProgress(WorkflowStage.Compressing, 100, "ZIP packaging completed."));
            return outputZipPath;
        }

        public Task<string> CreateZipArchiveAsync(
            string sourceDirectory, 
            string outputZipPath, 
            IProgress<ResourceWorkflowProgress>? progress = null, 
            CancellationToken cancellationToken = default)
        {
            return CreateZipArchiveAsync(sourceDirectory, outputZipPath, null, null, progress, cancellationToken);
        }
    }
}
