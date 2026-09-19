using System;
using System.Net.Http;

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
                Timeout = TimeSpan.FromSeconds(30)
            };

            client.DefaultRequestHeaders.Add("User-Agent", "SteamPluginManager/2.2.2");
            return client;
        });

        public static HttpClient Instance => _instance.Value;
    }
}
