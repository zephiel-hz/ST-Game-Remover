using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamPluginManager.Services.API
{
    public class ReplacementFileInfo
    {
        public string LocalFilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public string FormattedSize => FormatSize(FileSizeBytes);

        public string FileType => FileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
            ? "Lua Script"
            : (FileName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) ? "Depot Manifest" : "Data File");

        public string Icon => FileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
            ? "📜"
            : (FileName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) ? "⚙️" : "📄");

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024)):F2} MB";
            return $"{(bytes / (1024.0 * 1024 * 1024)):F2} GB";
        }
    }

    public static class SupabaseStorageService
    {
        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024)):F2} MB";
            return $"{(bytes / (1024.0 * 1024 * 1024)):F2} GB";
        }

        public static async Task<List<string>> ListAppIdFilesAsync(int appId)
        {
            if (appId <= 0) return new List<string>();
            return await SupabaseConfig.ListBucketFilesAsync($"{appId}/");
        }

        public static async Task<(bool success, int deletedCount, string message)> DeleteAppIdFolderFilesAsync(int appId)
        {
            if (appId <= 0) return (false, 0, "Invalid AppID.");

            var existingFiles = await ListAppIdFilesAsync(appId);
            if (existingFiles.Count == 0)
            {
                return (true, 0, "Folder is already empty.");
            }

            var storageBucket = SupabaseConfig.StorageBucketName;
            var deleteObjUrl = $"{SupabaseConfig.SupabaseUrl}/storage/v1/object/{storageBucket}";
            var client = SharedHttpClient.StorageInstance;

            int totalDeleted = 0;
            for (int i = 0; i < existingFiles.Count; i += 50)
            {
                var batch = existingFiles.Skip(i).Take(50).ToList();
                var delPayload = JsonSerializer.Serialize(new { prefixes = batch });
                using var delReq = new HttpRequestMessage(HttpMethod.Delete, deleteObjUrl)
                {
                    Content = new StringContent(delPayload, Encoding.UTF8, "application/json")
                };
                delReq.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                delReq.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                var resp = await client.SendAsync(delReq);
                if (!resp.IsSuccessStatusCode)
                {
                    string err = await resp.Content.ReadAsStringAsync();
                    Logger.Log($"[SupabaseStorageService] Failed to delete batch: {err}");
                    return (false, totalDeleted, $"Failed to delete existing files (HTTP {(int)resp.StatusCode}): {(!string.IsNullOrWhiteSpace(err) ? err : resp.ReasonPhrase)}");
                }
                totalDeleted += batch.Count;
            }

            Logger.Log($"[SupabaseStorageService] Successfully deleted {totalDeleted} old files for AppID {appId}.");
            return (true, totalDeleted, $"Successfully deleted {totalDeleted} old files.");
        }

        public static async Task<(bool success, int uploadedCount, long totalBytes, string message)> UploadReplacementFilesAsync(
            int appId,
            List<ReplacementFileInfo> files,
            IProgress<(int current, int total, string fileName)>? progress = null)
        {
            if (appId <= 0) return (false, 0, 0, "Invalid AppID.");
            if (files == null || files.Count == 0) return (false, 0, 0, "No files to upload.");

            string supabaseUrl = SupabaseConfig.SupabaseUrl;
            string serviceKey = SupabaseConfig.SupabaseKey;
            string bucketName = SupabaseConfig.StorageBucketName;
            var client = SharedHttpClient.StorageInstance;

            long totalBytes = 0;
            int uploadedCount = 0;

            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                progress?.Report((i + 1, files.Count, file.FileName));

                if (!File.Exists(file.LocalFilePath))
                {
                    return (false, uploadedCount, totalBytes, $"Local file not found: {file.LocalFilePath}");
                }

                byte[] fileBytes = await File.ReadAllBytesAsync(file.LocalFilePath);
                totalBytes += fileBytes.Length;

                string safeFileName = Uri.EscapeDataString(file.FileName);
                string storagePath = $"{appId}/{file.FileName}";
                string uploadEndpoint = $"{supabaseUrl}/storage/v1/object/{bucketName}/{appId}/{safeFileName}";

                using var request = new HttpRequestMessage(HttpMethod.Post, uploadEndpoint);
                request.Headers.Add("apikey", serviceKey);
                request.Headers.Add("Authorization", $"Bearer {serviceKey}");
                request.Headers.Add("x-upsert", "true");
                request.Content = new ByteArrayContent(fileBytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    using var putRequest = new HttpRequestMessage(HttpMethod.Put, uploadEndpoint);
                    putRequest.Headers.Add("apikey", serviceKey);
                    putRequest.Headers.Add("Authorization", $"Bearer {serviceKey}");
                    putRequest.Headers.Add("x-upsert", "true");
                    putRequest.Content = new ByteArrayContent(fileBytes);
                    putRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    response = await client.SendAsync(putRequest);
                }

                if (!response.IsSuccessStatusCode)
                {
                    string errContent = await response.Content.ReadAsStringAsync();
                    Logger.Log($"[SupabaseStorageService] Failed to upload {file.FileName}: HTTP {(int)response.StatusCode} - {errContent}");
                    return (false, uploadedCount, totalBytes, $"Failed to upload '{file.FileName}' (HTTP {(int)response.StatusCode}): {(!string.IsNullOrWhiteSpace(errContent) ? errContent : response.ReasonPhrase)}");
                }

                uploadedCount++;
                Logger.Log($"[SupabaseStorageService] ✓ Uploaded {storagePath} ({fileBytes.Length} bytes)");
            }

            return (true, uploadedCount, totalBytes, $"Successfully uploaded {uploadedCount} files.");
        }

        public static async Task<bool> UpdateCatalogMetadataAsync(int appId, long totalBytes)
        {
            try
            {
                string supabaseUrl = SupabaseConfig.SupabaseUrl;
                string serviceKey = SupabaseConfig.SupabaseKey;
                string bucketName = SupabaseConfig.StorageBucketName;
                var client = SharedHttpClient.Instance;

                string formattedSize = FormatSize(totalBytes);
                var updatePayload = new Dictionary<string, object?>
                {
                    ["file_size"] = formattedSize,
                    ["storage_type"] = "folder",
                    ["folder_path"] = $"{appId}/",
                    ["file_name"] = $"{appId}.lua",
                    ["url"] = $"{supabaseUrl}/storage/v1/object/public/{bucketName}/{appId}/{appId}.lua"
                };

                string updateUrl = $"{supabaseUrl}/rest/v1/hzmanifest_files?app_id=eq.{appId}";
                using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, updateUrl);
                patchRequest.Headers.Add("apikey", serviceKey);
                patchRequest.Headers.Add("Authorization", $"Bearer {serviceKey}");
                patchRequest.Headers.Add("Prefer", "return=minimal");
                patchRequest.Content = new StringContent(JsonSerializer.Serialize(updatePayload), Encoding.UTF8, "application/json");

                var resp = await client.SendAsync(patchRequest);
                if (resp.IsSuccessStatusCode)
                {
                    Logger.Log($"[SupabaseStorageService] Updated catalog metadata for AppID {appId}: size={formattedSize}");
                    return true;
                }
                else
                {
                    string err = await resp.Content.ReadAsStringAsync();
                    Logger.Log($"[SupabaseStorageService] Catalog update failed: HTTP {(int)resp.StatusCode} - {err}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SupabaseStorageService] Error updating catalog metadata: {ex.Message}");
                return false;
            }
        }

        public static (string tempDir, List<ReplacementFileInfo> files) ExtractAndScanZip(string zipFilePath)
        {
            var list = new List<ReplacementFileInfo>();
            if (!File.Exists(zipFilePath)) return (string.Empty, list);

            string tempDir = Path.Combine(Path.GetTempPath(), "HZ_Replace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            ZipFile.ExtractToDirectory(zipFilePath, tempDir, true);

            var di = new DirectoryInfo(tempDir);
            var allFiles = di.GetFiles("*.*", SearchOption.AllDirectories);

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fi in allFiles)
            {
                string ext = fi.Extension.ToLowerInvariant();
                if (ext is ".lua" or ".manifest")
                {
                    if (!seenNames.Contains(fi.Name))
                    {
                        seenNames.Add(fi.Name);
                        list.Add(new ReplacementFileInfo
                        {
                            LocalFilePath = fi.FullName,
                            FileName = fi.Name,
                            FileSizeBytes = fi.Length
                        });
                    }
                }
            }

            return (tempDir, list);
        }

        public static List<ReplacementFileInfo> ScanDirectFiles(IEnumerable<string> filePaths)
        {
            var list = new List<ReplacementFileInfo>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in filePaths)
            {
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    if (!seenNames.Contains(fi.Name))
                    {
                        seenNames.Add(fi.Name);
                        list.Add(new ReplacementFileInfo
                        {
                            LocalFilePath = fi.FullName,
                            FileName = fi.Name,
                            FileSizeBytes = fi.Length
                        });
                    }
                }
            }
            return list;
        }

        public static List<ReplacementFileInfo> ScanFolderFiles(string folderPath)
        {
            var list = new List<ReplacementFileInfo>();
            if (!Directory.Exists(folderPath)) return list;

            var di = new DirectoryInfo(folderPath);
            var allFiles = di.GetFiles("*.*", SearchOption.AllDirectories);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var fi in allFiles)
            {
                string ext = fi.Extension.ToLowerInvariant();
                if (ext is ".lua" or ".manifest")
                {
                    if (!seenNames.Contains(fi.Name))
                    {
                        seenNames.Add(fi.Name);
                        list.Add(new ReplacementFileInfo
                        {
                            LocalFilePath = fi.FullName,
                            FileName = fi.Name,
                            FileSizeBytes = fi.Length
                        });
                    }
                }
            }
            return list;
        }

        public static void CleanupTempDir(string tempDir)
        {
            try
            {
                if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch { }
        }
    }
}
