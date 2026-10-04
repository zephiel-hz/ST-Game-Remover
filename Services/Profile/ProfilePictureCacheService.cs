using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SteamPluginManager.Services.Profile
{
    /// <summary>
    /// Layanan caching dan manajemen foto profil (PFP) untuk pengguna.
    /// Menyimpan cache di disk dan memory untuk loading instan, serta mengunggah/mengunduh dari Backblaze B2 (folder pfp/).
    /// </summary>
    public static class ProfilePictureCacheService
    {
        private static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamPluginManager",
            "AvatarCache"
        );

        private static readonly ConcurrentDictionary<int, BitmapImage?> _memoryCache = new();
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _userLocks = new();
        private static readonly ConcurrentDictionary<int, DateTime> _negativeCache = new();

        /// <summary>
        /// Event yang dipicu saat foto profil seorang pengguna diperbarui atau dihapus secara realtime.
        /// Argumen: userId, ImageSource (atau null jika avatar dihapus/reset)
        /// </summary>
        public static event Action<int, ImageSource?>? OnAvatarUpdated;

        static ProfilePictureCacheService()
        {
            try
            {
                if (!Directory.Exists(CacheDirectory))
                {
                    Directory.CreateDirectory(CacheDirectory);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProfilePictureCache] Failed to initialize cache directory: {ex.Message}");
            }
        }

        public static string GetDiskCachePath(int userId)
        {
            return Path.Combine(CacheDirectory, $"{userId}.jpg");
        }

        /// <summary>
        /// Mengambil ImageSource avatar pengguna secara sinkron dari memory cache jika tersedia, atau null jika belum ada.
        /// </summary>
        public static ImageSource? GetCachedAvatarInMemory(int userId)
        {
            if (userId <= 0) return null;
            if (_memoryCache.TryGetValue(userId, out var cached) && cached != null)
            {
                return cached;
            }
            return null;
        }

        /// <summary>
        /// Mengambil foto profil pengguna:
        /// 1. Cek Memory Cache (0ms)
        /// 2. Cek Disk Cache
        /// 3. Download dari Backblaze B2 (folder pfp/{userId}.jpg) dan simpan ke disk & memory cache.
        /// </summary>
        public static async Task<ImageSource?> GetAvatarAsync(int? userId, string? avatarUrl = null, bool forceRefresh = false)
        {
            if (!userId.HasValue || userId.Value <= 0) return null;
            int uid = userId.Value;

            if (!forceRefresh)
            {
                // 1. Check memory cache
                if (_memoryCache.TryGetValue(uid, out var memImg))
                {
                    return memImg;
                }

                // Check negative cache (failed recently within 2 minutes)
                if (_negativeCache.TryGetValue(uid, out var failedAt) && DateTime.UtcNow - failedAt < TimeSpan.FromMinutes(2))
                {
                    return null;
                }
            }

            var userLock = _userLocks.GetOrAdd(uid, _ => new SemaphoreSlim(1, 1));
            await userLock.WaitAsync();
            try
            {
                if (!forceRefresh && _memoryCache.TryGetValue(uid, out var memImg))
                {
                    return memImg;
                }

                string diskPath = GetDiskCachePath(uid);

                // 2. Check disk cache
                if (!forceRefresh && File.Exists(diskPath))
                {
                    try
                    {
                        var img = LoadBitmapFromDisk(diskPath);
                        if (img != null)
                        {
                            _memoryCache[uid] = img;
                            return img;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ProfilePictureCache] Error reading disk cache for user {uid}: {ex.Message}");
                    }
                }

                // 3. Download from Backblaze B2 (pfp/{userId}.jpg)
                string remoteKey = !string.IsNullOrWhiteSpace(avatarUrl) ? avatarUrl.TrimStart('/') : $"pfp/{uid}.jpg";
                try
                {
                    var data = await B2Config.DownloadFileAsync(remoteKey);
                    if (data != null && data.Length > 0)
                    {
                        // Save to disk
                        await File.WriteAllBytesAsync(diskPath, data);

                        // Decode and freeze image
                        var img = LoadBitmapFromBytes(data);
                        if (img != null)
                        {
                            _memoryCache[uid] = img;
                            _negativeCache.TryRemove(uid, out _);
                            return img;
                        }
                    }
                }
                catch
                {
                    // If file doesn't exist on B2 or network error, record negative cache
                    _negativeCache[uid] = DateTime.UtcNow;
                    _memoryCache[uid] = null;
                }

                return null;
            }
            finally
            {
                userLock.Release();
            }
        }

        /// <summary>
        /// Menyimpan foto profil yang baru di-crop:
        /// 1. Simpan ke local disk cache
        /// 2. Update memory cache
        /// 3. Upload ke Backblaze B2 (pfp/{userId}.jpg)
        /// 4. Picu event OnAvatarUpdated
        /// </summary>
        public static async Task<bool> SaveAndUploadAvatarAsync(int userId, byte[] imageBytes)
        {
            if (userId <= 0 || imageBytes == null || imageBytes.Length == 0) return false;

            try
            {
                string diskPath = GetDiskCachePath(userId);

                // Save to local disk immediately
                await File.WriteAllBytesAsync(diskPath, imageBytes);

                // Create and freeze memory cache
                var img = LoadBitmapFromBytes(imageBytes);
                if (img != null)
                {
                    _memoryCache[userId] = img;
                }
                _negativeCache.TryRemove(userId, out _);

                // Upload to Backblaze B2 (folder pfp/{userId}.jpg)
                string remoteKey = $"pfp/{userId}.jpg";
                await B2Config.UploadBytesAsync(remoteKey, imageBytes, "image/jpeg");

                Logger.Log($"[ProfilePictureCache] Successfully uploaded avatar for user {userId} to B2: {remoteKey}");

                // Notify UI
                OnAvatarUpdated?.Invoke(userId, img);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProfilePictureCache] Failed to save/upload avatar for user {userId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Menghapus foto profil pengguna dari cache lokal dan Backblaze B2.
        /// </summary>
        public static async Task<bool> DeleteAvatarAsync(int userId)
        {
            if (userId <= 0) return false;

            try
            {
                string diskPath = GetDiskCachePath(userId);
                if (File.Exists(diskPath))
                {
                    try { File.Delete(diskPath); } catch { }
                }

                _memoryCache.TryRemove(userId, out _);
                _negativeCache[userId] = DateTime.UtcNow;

                // Delete from B2
                string remoteKey = $"pfp/{userId}.jpg";
                await B2Config.DeleteFileAsync(remoteKey);

                Logger.Log($"[ProfilePictureCache] Deleted avatar for user {userId} from B2 and cache.");

                // Notify UI
                OnAvatarUpdated?.Invoke(userId, null);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProfilePictureCache] Failed to delete avatar for user {userId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Invalidate cache pengguna tertentu (dipanggil saat ada event Supabase Realtime bahwa user mengganti avatar).
        /// </summary>
        public static void InvalidateCache(int userId)
        {
            if (userId <= 0) return;

            string diskPath = GetDiskCachePath(userId);
            if (File.Exists(diskPath))
            {
                try { File.Delete(diskPath); } catch { }
            }

            _memoryCache.TryRemove(userId, out _);
            _negativeCache.TryRemove(userId, out _);
        }

        /// <summary>
        /// Memicu event OnAvatarUpdated secara manual (dipanggil saat ada pembaruan foto profil via realtime websocket).
        /// </summary>
        public static void TriggerAvatarUpdated(int userId, ImageSource? img)
        {
            if (userId <= 0) return;
            if (img is BitmapImage bmp)
            {
                _memoryCache[userId] = bmp;
                _negativeCache.TryRemove(userId, out _);
            }
            else if (img == null)
            {
                _memoryCache.TryRemove(userId, out _);
            }
            OnAvatarUpdated?.Invoke(userId, img);
        }

        private static BitmapImage? LoadBitmapFromDisk(string filePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze(); // Makes it thread-safe for any UI thread binding
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public static BitmapImage? LoadBitmapFromBytes(byte[] bytes)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze(); // Makes it thread-safe for any UI thread binding
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }
}

