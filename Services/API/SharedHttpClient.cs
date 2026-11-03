using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    /// <summary>
    /// Centralized HttpClient for reuse across the application.
    /// HttpClient should be reused to avoid socket exhaustion and improve performance.
    /// This class provides a singleton instance of HttpClient with optimized settings.
    /// </summary>
    public static class SharedHttpClient
    {
        private static readonly Lazy<HttpClient> _instance = new Lazy<HttpClient>(() =>
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                UseCookies = false,
                AllowAutoRedirect = true,
                MaxConnectionsPerServer = 10
            })
            {
                Timeout = TimeSpan.FromMinutes(2)
            };

            client.DefaultRequestHeaders.Add("User-Agent", AppInfo.UserAgent);
            return client;
        });

        private static readonly Lazy<HttpClient> _storageInstance = new Lazy<HttpClient>(() =>
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                UseCookies = false,
                AllowAutoRedirect = true,
                MaxConnectionsPerServer = 10,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15)
            })
            {
                // Generous timeout for large file uploads & downloads over slow connections
                Timeout = TimeSpan.FromMinutes(10)
            };

            client.DefaultRequestHeaders.Add("User-Agent", AppInfo.UserAgent);
            return client;
        });

        public static HttpClient Instance => _instance.Value;
        public static HttpClient StorageInstance => _storageInstance.Value;

        /// <summary>
        /// Shared throttle for thumbnail downloads across all views (max 4 concurrent requests).
        /// </summary>
        public static SemaphoreSlim ThumbnailThrottle { get; } = new SemaphoreSlim(4, 4);

        private static readonly System.Collections.Generic.Dictionary<string, Task<bool>> _inFlightThumbnailTasks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _thumbnailLock = new();

        /// <summary>
        /// Executes a thumbnail fetch action under the shared 4-worker throttle.
        /// Deduplicates simultaneous requests for the exact same cache key so duplicate downloads are eliminated.
        /// </summary>
        public static async Task<bool> RunThrottledThumbnailFetchAsync(string key, Func<Task<bool>> fetchAction)
        {
            string normKey = key?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(normKey))
            {
                await ThumbnailThrottle.WaitAsync().ConfigureAwait(false);
                try
                {
                    return await fetchAction().ConfigureAwait(false);
                }
                finally
                {
                    ThumbnailThrottle.Release();
                }
            }

            TaskCompletionSource<bool>? tcs = null;
            Task<bool> taskToAwait;

            lock (_thumbnailLock)
            {
                if (_inFlightThumbnailTasks.TryGetValue(normKey, out var existingTask))
                {
                    taskToAwait = existingTask;
                }
                else
                {
                    tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    // Prevent any unobserved task exception on the shared task if no follower awaits it
                    _ = tcs.Task.ContinueWith(static t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    _inFlightThumbnailTasks[normKey] = tcs.Task;
                    taskToAwait = tcs.Task;
                }
            }

            // Another worker is already performing this fetch. Just await the shared task.
            if (tcs == null)
            {
                return await taskToAwait.ConfigureAwait(false);
            }

            // This invocation is the elected leader for 'normKey'
            try
            {
                await ThumbnailThrottle.WaitAsync().ConfigureAwait(false);
                try
                {
                    bool result = await fetchAction().ConfigureAwait(false);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    ThumbnailThrottle.Release();
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                lock (_thumbnailLock)
                {
                    // Only remove if this exact leader task is still the registered task
                    if (_inFlightThumbnailTasks.TryGetValue(normKey, out var currentTask) && ReferenceEquals(currentTask, tcs.Task))
                    {
                        _inFlightThumbnailTasks.Remove(normKey);
                    }
                }
            }

            return await tcs.Task.ConfigureAwait(false);
        }
    }
}
