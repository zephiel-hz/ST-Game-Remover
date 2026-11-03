using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using SteamPluginManager;

namespace SteamPluginManager.Helpers
{
    public class ThumbnailDecodeConverter : IValueConverter
    {
        private const int ThumbnailWidth = 320;
        private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, DateTime> _failedUrls = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, BitmapImage> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);

        #region ClearOnFailure Attached Property
        public static readonly DependencyProperty ClearOnFailureProperty =
            DependencyProperty.RegisterAttached(
                "ClearOnFailure",
                typeof(bool),
                typeof(ThumbnailDecodeConverter),
                new PropertyMetadata(false, OnClearOnFailureChanged));

        public static bool GetClearOnFailure(DependencyObject obj) => (bool)obj.GetValue(ClearOnFailureProperty);
        public static void SetClearOnFailure(DependencyObject obj, bool value) => obj.SetValue(ClearOnFailureProperty, value);

        private static void OnClearOnFailureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Image image)
            {
                image.ImageFailed -= OnImageFailed;
                if ((bool)e.NewValue)
                {
                    image.ImageFailed += OnImageFailed;
                }
            }
        }

        private static void OnImageFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            if (sender is not Image image)
                return;

            // 1. Verify current image source
            if (image.Source is not BitmapImage currentBmp || currentBmp.UriSource == null)
                return;

            var uri = currentBmp.UriSource;

            // 2. Only handle remote HTTP/HTTPS failures. Never record or clear local image files.
            bool isRemote = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
            if (!isRemote)
                return;

            string url = uri.OriginalString;

            // 3. Stale event check: verify the failed image still corresponds to the current DataContext
            if (image.DataContext is GameEntry game)
            {
                if (!string.Equals(game.ScreenshotUrl, url, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(game.ScreenshotUrl, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                {
                    // Stale failure event: image or game has already updated to a newer source. Do not clear!
                    return;
                }
            }

            // 4. Record failure with expiring timestamp
            var now = DateTime.UtcNow;
            PruneExpiredFailedUrls(now);

            bool isFirstFailure = !_failedUrls.ContainsKey(url);
            _failedUrls[url] = now;

            if (isFirstFailure)
            {
                string gameName = (image.DataContext as GameEntry)?.Name ?? "unknown";
                Logger.Log($"[ThumbnailDecodeConverter] Remote thumbnail download failed for '{gameName}' ({url}): {e.ErrorException?.Message}");
            }

            // 5. Clear current value safely so DataTriggers fire to display the placeholder
            image.SetCurrentValue(Image.SourceProperty, null);
        }

        private static void PruneExpiredFailedUrls(DateTime now)
        {
            if (_failedUrls.Count <= 200)
                return;

            foreach (var kvp in _failedUrls)
            {
                if (now - kvp.Value >= RetryCooldown)
                {
                    _failedUrls.TryRemove(kvp.Key, out _);
                }
            }

            // Safety limit to keep dictionary strictly bounded
            if (_failedUrls.Count > 500)
            {
                foreach (var key in _failedUrls.Keys)
                {
                    _failedUrls.TryRemove(key, out _);
                    if (_failedUrls.Count <= 250)
                        break;
                }
            }
        }

        public static void ResetFailedUrls() => _failedUrls.Clear();
        #endregion

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string rawPath || string.IsNullOrWhiteSpace(rawPath))
                return null;

            if (_bitmapCache.TryGetValue(rawPath, out var cachedBitmap))
            {
                return cachedBitmap;
            }

            try
            {
                // 1. Check if it is a local file on disk (always preferred, unaffected by remote failed URLs)
                if (File.Exists(rawPath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(Path.GetFullPath(rawPath), UriKind.Absolute);
                    bitmap.DecodePixelWidth = ThumbnailWidth;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _bitmapCache[rawPath] = bitmap;
                    return bitmap;
                }

                // 2. Expiring cooldown check for previously failed remote URLs
                if (_failedUrls.TryGetValue(rawPath, out var failedAt))
                {
                    if (DateTime.UtcNow - failedAt < RetryCooldown)
                    {
                        return null; // Suppress retry during cooldown
                    }

                    _failedUrls.TryRemove(rawPath, out _); // Cooldown expired, allow retry
                }

                // 3. Check if it is a remote URI (fallback when local cache is unavailable)
                if (Uri.TryCreate(rawPath, UriKind.Absolute, out var uri))
                {
                    if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    {
                        // Remote URI: Load asynchronously with BitmapCacheOption.Default to avoid blocking UI thread on network I/O.
                        // Do not call Freeze() while downloading.
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = uri;
                        bitmap.DecodePixelWidth = ThumbnailWidth;
                        bitmap.CacheOption = BitmapCacheOption.Default;
                        bitmap.EndInit();
                        return bitmap;
                    }
                    else if (uri.IsFile)
                    {
                        string localPath = uri.LocalPath;
                        if (File.Exists(localPath))
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.UriSource = uri;
                            bitmap.DecodePixelWidth = ThumbnailWidth;
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.EndInit();
                            bitmap.Freeze();
                            _bitmapCache[rawPath] = bitmap;
                            return bitmap;
                        }
                    }
                }

                // File does not exist locally and is not a valid remote URI
                Logger.Log($"[ThumbnailDecodeConverter] Thumbnail file not found: {rawPath}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ThumbnailDecodeConverter] Failed to decode thumbnail for '{rawPath}': {ex.Message}");
                return null;
            }
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
