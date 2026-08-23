using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Linq;

namespace SteamPluginManager.Views
{
    public partial class GenerateTokenView : UserControl
    {
        private const string TokenGeneratorUrl = "https://hzluamanager-get-token.lovable.app";
        private CoreWebView2Environment? _environment;
        // Single WebView2 instance; no core-to-control mapping required.

        public GenerateTokenView()
        {
            InitializeComponent();
            Loaded += GenerateTokenView_Loaded;
        }

        private async void GenerateTokenView_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= GenerateTokenView_Loaded;

            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SteamPluginManager",
                    "WebView2");
                Directory.CreateDirectory(userDataFolder);

                _environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder);

                // Initialize webview
                await GenerateTokenBrowser.EnsureCoreWebView2Async(_environment);
                await AddHideLovableBadgeScriptAsync();
                HookWebViewEvents(GenerateTokenBrowser);
                GenerateTokenBrowser.Source = new Uri(TokenGeneratorUrl);
            }
            catch (Exception ex)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                MessageBox.Show(
                    $"WebView2 Runtime is required to display token generator inside the app.\n\n{ex.Message}",
                    "Token Generator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void GenerateTokenBrowser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        private void GenerateTokenBrowser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            UpdateNavigationButtons();
        }

        private void CoreWebView2_HistoryChanged(object? sender, object e)
        {
            Dispatcher.BeginInvoke(UpdateNavigationButtons);
        }


        private void UpdateNavigationButtons()
        {
            var active = GetActiveWebView();
            if (active?.CoreWebView2 is null)
                return;

            BackButton.IsEnabled = active.CoreWebView2.CanGoBack;
            ForwardButton.IsEnabled = active.CoreWebView2.CanGoForward;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            var active = GetActiveWebView();
            if (active?.CoreWebView2?.CanGoBack == true)
                active.CoreWebView2.GoBack();
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            var active = GetActiveWebView();
            if (active?.CoreWebView2?.CanGoForward == true)
                active.CoreWebView2.GoForward();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            var active = GetActiveWebView();
            active?.CoreWebView2?.Reload();
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WindowNavigator.NavigateToVerifyToken();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to return to token entry: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = e.TryGetWebMessageAsString();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    // Assume message is the token string
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            WindowNavigator.NavigateToVerifyToken(message);
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }

        private WebView2? GetActiveWebView()
        {
            return GenerateTokenBrowser;
        }


        private void HookWebViewEvents(WebView2 webView)
        {
            webView.NavigationStarting += GenerateTokenBrowser_NavigationStarting;
            webView.NavigationCompleted += GenerateTokenBrowser_NavigationCompleted;
            webView.CoreWebView2.HistoryChanged += CoreWebView2_HistoryChanged;
            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            webView.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;
            webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompletedForScript;

            try
            {
                var core = webView.CoreWebView2;

                // Ensure common settings don't block ads/popup/scripts
                var settings = core.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultScriptDialogsEnabled = true;
                settings.AreDefaultContextMenusEnabled = true;
                settings.AreDevToolsEnabled = true;
                settings.IsZoomControlEnabled = true;
                settings.AreBrowserAcceleratorKeysEnabled = true;
            }
            catch { }
        }

        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(e.Uri))
                {
                    GenerateTokenBrowser.Source = new Uri(e.Uri);
                }

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[GenerateTokenView] NewWindowRequested navigation fallback error: {ex.Message}");
            }
        }

        private void CoreWebView2_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var core = sender as CoreWebView2;
                    if (core == null)
                        return;

                    Logger.Log($"[GenerateTokenView] WebView2 process failed. Kind={e.ProcessFailedKind}. Restarting control.");

                    await ReplaceCrashedWebViewAsync();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[GenerateTokenView] Failed to recover from WebView2 crash: {ex.Message}");
                }
            });
        }

        private async void CoreWebView2_NavigationCompletedForScript(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                await AddHideLovableBadgeScriptAsync();
            }
            catch { }
        }

        private async System.Threading.Tasks.Task AddHideLovableBadgeScriptAsync()
        {
            try
            {
                if (GenerateTokenBrowser?.CoreWebView2 is null)
                    return;

                const string script = @"
(function() {
    var selector = '[class*=""lovable""], [id*=""lovable""], [href*=""lovable""], [data-testid*=""lovable""], [title*=""lovable""], [aria-label*=""lovable""]';
    var hideElement = function(el) { if (el) el.style.setProperty('display', 'none', 'important'); };

    var removeBadges = function() {
        var els = document.querySelectorAll(selector);
        els.forEach(function(el) { hideElement(el); });
        var inline = Array.from(document.querySelectorAll('style, link[rel=""stylesheet""'));
        inline.forEach(function(el) {
            if (el.textContent && el.textContent.indexOf('lovable') >= 0) {
                el.parentNode && el.parentNode.removeChild(el);
            }
        });
    };

    hideElement(document.querySelector(selector));
    removeBadges();
    var observer = new MutationObserver(removeBadges);
    observer.observe(document.documentElement || document.body, { childList: true, subtree: true });
})();
";

                await GenerateTokenBrowser.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Logger.Log($"[GenerateTokenView] Failed to inject hide badge script: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task ReplaceCrashedWebViewAsync()
        {
            try
            {
                var parentGrid = GenerateTokenBrowser.Parent as Grid;
                if (parentGrid == null)
                    return;

                var oldBrowser = GenerateTokenBrowser;
                GenerateTokenBrowser = new WebView2();
                parentGrid.Children.Add(GenerateTokenBrowser);
                HookWebViewEvents(GenerateTokenBrowser);

                if (_environment != null)
                {
                    await GenerateTokenBrowser.EnsureCoreWebView2Async(_environment);
                }

                GenerateTokenBrowser.Source = new Uri(TokenGeneratorUrl);

                try
                {
                    parentGrid.Children.Remove(oldBrowser);
                }
                catch { }

                try
                {
                    oldBrowser.Dispose();
                }
                catch { }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GenerateTokenView] ReplaceCrashedWebViewAsync failed: {ex.Message}");
            }
        }


    }
}
