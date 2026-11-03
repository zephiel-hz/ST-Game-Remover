using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Wpf;

namespace SteamPluginManager.Helpers
{
    public static class WebViewHelper
    {
        private static readonly ConcurrentDictionary<int, byte> _trackedProcessIds = new();

        /// <summary>
        /// Registers a WebView2 browser process ID for lifecycle tracking.
        /// </summary>
        public static void TrackBrowserProcess(WebView2? webView)
        {
            if (webView?.CoreWebView2 == null) return;
            try
            {
                int pid = (int)webView.CoreWebView2.BrowserProcessId;
                if (pid > 0)
                {
                    _trackedProcessIds.TryAdd(pid, 0);
                }
            }
            catch { }
        }

        /// <summary>
        /// Gracefully stops and disposes a WebView2 control, ensuring that its
        /// underlying msedgewebview2.exe process and child processes do not linger in the background.
        /// </summary>
        public static void TerminateBrowserProcess(WebView2? webView)
        {
            if (webView == null) return;

            int pid = 0;
            try
            {
                if (webView.CoreWebView2 != null)
                {
                    pid = (int)webView.CoreWebView2.BrowserProcessId;
                }
            }
            catch { }

            try
            {
                webView.CoreWebView2?.Stop();
            }
            catch { }

            try
            {
                webView.Dispose();
            }
            catch { }

            if (pid > 0)
            {
                _trackedProcessIds.TryRemove(pid, out _);
                KillProcessTreeAsync(pid);
            }
        }

        /// <summary>
        /// Terminates all known WebView2 browser processes tracked by this helper.
        /// Typically invoked on application exit.
        /// </summary>
        public static void TerminateAllTrackedProcesses()
        {
            foreach (var pid in _trackedProcessIds.Keys)
            {
                _trackedProcessIds.TryRemove(pid, out _);
                try
                {
                    var proc = Process.GetProcessById(pid);
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch { }
            }
        }

        private static void KillProcessTreeAsync(int pid)
        {
            Task.Run(async () =>
            {
                try
                {
                    // Allow up to 400ms for WebView2 runtime to gracefully terminate itself after Dispose()
                    await Task.Delay(400);
                    var proc = Process.GetProcessById(pid);
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch { }
            });
        }
    }
}

