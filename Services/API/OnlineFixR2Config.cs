using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    public static class OnlineFixR2Config
    {
        private static CloudflareR2Settings Settings => ServiceConfiguration.Current.OnlineFixCloudflareR2;

        public static string Endpoint => Settings.Endpoint;
        public static string Bucket => Settings.Bucket;

        public static void InvalidateListCache() => R2Config.InvalidateListCache(Settings);

        public static Task<List<R2Config.R2FileInfo>> ListFilesAsync(string folderPrefix = "onlinefix/", bool forceRefresh = false) =>
            R2Config.ListFilesAsync(folderPrefix, forceRefresh, Settings);

        public static Task<string> UploadFileAsync(
            string localFilePath,
            string remoteKey,
            string contentType = "application/zip",
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) =>
            R2Config.UploadFileAsync(localFilePath, remoteKey, contentType, progress, cancellationToken, Settings);

        public static Task<string> UploadBytesAsync(
            string key,
            byte[] data,
            string contentType = "application/octet-stream",
            CancellationToken cancellationToken = default) =>
            R2Config.UploadBytesAsync(key, data, contentType, cancellationToken, Settings);

        public static Task<byte[]> DownloadFileAsync(
            string key,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default,
            Func<Task>? pauseCheck = null) =>
            R2Config.DownloadFileAsync(key, progress, cancellationToken, pauseCheck, Settings);

        public static Task DownloadFileToPathAsync(
            string key,
            string destinationFilePath,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default,
            Func<Task>? pauseCheck = null) =>
            R2Config.DownloadFileToPathAsync(key, destinationFilePath, progress, cancellationToken, pauseCheck, Settings);

        public static Task<bool> DeleteFileAsync(string key, CancellationToken cancellationToken = default) =>
            R2Config.DeleteFileAsync(key, cancellationToken, Settings);
    }
}
