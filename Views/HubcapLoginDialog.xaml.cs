using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace SteamPluginManager.Views
{
    public partial class HubcapLoginDialog : Window
    {
        private bool _isKeyFound = false;
        private bool _isSilent = true;
        private bool _isJoiningDiscordServer = false;
        private bool _hasAttemptedAutoJoin = false;
        private int _joinAttemptTicks = 0;
        private DispatcherTimer? _pollTimer;
        private DispatcherTimer? _safetyTimeoutTimer;
        private DateTime _lastDiscordLoginClickTime = DateTime.MinValue;
        private bool _hasAttemptedKeyGeneration = false;

        public bool SilentMode
        {
            get => _isSilent;
            set
            {
                _isSilent = value;
                if (!_isSilent)
                {
                    MakeVisible();
                }
            }
        }

        public string? ExtractedApiKey { get; private set; }
        public bool UserCanceled { get; private set; } = true;

        public HubcapLoginDialog(bool silentMode = true)
        {
            InitializeComponent();
            _isSilent = silentMode;

            if (_isSilent)
            {
                Opacity = 0;
                ShowInTaskbar = false;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -10000;
                Top = -10000;
            }

            Loaded += async (s, e) => await InitializeBrowserAsync();
            Closing += (s, e) =>
            {
                _pollTimer?.Stop();
                _pollTimer = null;
                _safetyTimeoutTimer?.Stop();
                _safetyTimeoutTimer = null;
            };
        }

        private async Task InitializeBrowserAsync()
        {
            try
            {
                LoadingStatusText.Text = "Initializing browser environment...";

                // Persist session cookies in AppData so Discord session is remembered across app launches
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HZLuaManager",
                    "HubcapBrowserProfile"
                );
                Directory.CreateDirectory(userDataFolder);

                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await Browser.EnsureCoreWebView2Async(environment);

                Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
                Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;

                // Monitor network responses to capture API keys returned by Hubcap's own frontend calls
                Browser.CoreWebView2.WebResourceResponseReceived += CoreWebView2_WebResourceResponseReceived;
                Browser.NavigationCompleted += Browser_NavigationCompleted;

                // Navigate directly to API keys page
                LoadingStatusText.Text = "Connecting to Hubcap Manifest...";
                Browser.Source = new Uri("https://hubcapmanifest.com/api-keys");

                // Start active polling timer (every 1 second) to automate actions & extract key
                _pollTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1.0)
                };
                _pollTimer.Tick += async (s, args) => await CheckForApiKeyAsync();
                _pollTimer.Start();

                // If in silent mode, allow up to 12 seconds for full background automation.
                // If still not resolved (e.g. unknown state), reveal window so user can interact.
                if (_isSilent)
                {
                    _safetyTimeoutTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(12.0)
                    };
                    _safetyTimeoutTimer.Tick += (s, args) =>
                    {
                        _safetyTimeoutTimer?.Stop();
                        if (!_isKeyFound)
                        {
                            Logger.Log("[HubcapLoginDialog] Silent mode safety timeout reached. Showing window to user.");
                            MakeVisible();
                        }
                    };
                    _safetyTimeoutTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HubcapLoginDialog] Failed to initialize WebView2: {ex.Message}");
                StatusText.Text = $"Browser initialization failed: {ex.Message}";
                LoadingOverlay.Visibility = Visibility.Collapsed;
                MakeVisible();
            }
        }

        public void MakeVisible()
        {
            _isSilent = false;
            Dispatcher.Invoke(() =>
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Left = Math.Max(50, (SystemParameters.PrimaryScreenWidth - Width) / 2);
                Top = Math.Max(50, (SystemParameters.PrimaryScreenHeight - Height) / 2);
                Opacity = 1;
                ShowInTaskbar = true;
                Activate();
                Focus();
            });
        }

        private async Task<bool> TryClickContinueWithDiscordAsync()
        {
            if (Browser.CoreWebView2 == null) return false;

            // Debounce clicking to avoid re-triggering while navigation is in progress
            if ((DateTime.Now - _lastDiscordLoginClickTime).TotalSeconds < 4.0)
            {
                return true;
            }

            try
            {
                string script = @"
                    (() => {
                        let elements = Array.from(document.querySelectorAll('a, button, div[role=""button""], [onclick], span, p'));
                        for (let el of elements) {
                            let text = (el.innerText || el.textContent || '').trim().toLowerCase();
                            let href = (el.getAttribute('href') || '').toLowerCase();

                            let isDiscordBtn = false;
                            if (text.includes('continue with discord') ||
                                text.includes('login with discord') ||
                                text.includes('sign in with discord') ||
                                text.includes('log in with discord') ||
                                text.includes('masuk dengan discord') ||
                                href.includes('/auth/discord') ||
                                href.includes('discord.com/oauth2/authorize')) {
                                isDiscordBtn = true;
                            }

                            if (isDiscordBtn) {
                                let clickable = el.closest('a, button, [role=""button""]') || el;
                                try { clickable.focus(); } catch(e) {}
                                try {
                                    clickable.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, view: window }));
                                    clickable.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, view: window }));
                                    clickable.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                                } catch(e) {}
                                clickable.click();

                                let targetHref = clickable.getAttribute('href');
                                if (targetHref && !targetHref.startsWith('#') && !targetHref.startsWith('javascript:')) {
                                    setTimeout(() => {
                                        try {
                                            window.location.href = clickable.href || targetHref;
                                        } catch(e) {}
                                    }, 150);
                                }
                                return 'clicked';
                            }
                        }
                        return 'none';
                    })();
                ";

                string result = await Browser.ExecuteScriptAsync(script);
                if (result != null && result.Contains("clicked"))
                {
                    _lastDiscordLoginClickTime = DateTime.Now;
                    Logger.Log("[HubcapLoginDialog] Detected and auto-clicked 'Continue with Discord' button.");
                    StatusText.Text = "Detected Discord login button. Continuing with Discord...";
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HubcapLoginDialog] TryClickContinueWithDiscordAsync error: {ex.Message}");
            }

            return false;
        }

        private async void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;

            string currentUrl = Browser.Source?.ToString() ?? "";

            // 1. Check if server membership error is displayed on Hubcap page
            if (currentUrl.Contains("hubcapmanifest.com"))
            {
                try
                {
                    string checkScript = @"
                        (() => {
                            let text = (document.body ? document.body.innerText || document.body.textContent || '' : '').toLowerCase();
                            if (text.includes('member of our discord server to access') || text.includes('must be a member of our discord server')) {
                                return 'membership_required';
                            }
                            return 'ok';
                        })();
                    ";
                    string result = await Browser.ExecuteScriptAsync(checkScript);
                    if (result != null && result.Contains("membership_required"))
                    {
                        Logger.Log("[HubcapLoginDialog] Detected Discord server membership error in page text.");
                        await HandleMembershipRequiredAsync();
                        return;
                    }
                }
                catch { }

                // Check if unauthenticated page with "Continue with Discord" button is present
                if (await TryClickContinueWithDiscordAsync())
                {
                    return;
                }
            }

            // 2. If navigated to Discord invite page
            if (currentUrl.Contains("discord.com/invite") || currentUrl.Contains("discord.gg"))
            {
                _isJoiningDiscordServer = true;
                StatusText.Text = "Hubcap Discord invitation detected. Joining server automatically...";
                await RunAutoAcceptDiscordInviteAsync();
                return;
            }

            // 3. If navigated to Discord guild channels (server joined successfully!)
            if (currentUrl.Contains("discord.com/channels/") || currentUrl.Contains("1327856038967246970"))
            {
                if (_isJoiningDiscordServer)
                {
                    await OnDiscordServerJoinedAsync();
                    return;
                }
            }

            // 4. If on Discord login page, user must enter credentials manually -> make window visible
            if (currentUrl.Contains("discord.com/login") || currentUrl.Contains("discord.com/register"))
            {
                StatusText.Text = "Please log in to your Discord account in the window above...";
                GoToApiKeysButton.Visibility = Visibility.Collapsed;
                MakeVisible();
                return;
            }

            // 5. Discord OAuth Authorize page
            if (currentUrl.Contains("discord.com/oauth2/authorize"))
            {
                StatusText.Text = "Authorizing Discord access with Hubcap...";
                _ = CheckForApiKeyAsync();
                return;
            }

            // 6. Hubcap pages
            if (currentUrl.Contains("hubcapmanifest.com"))
            {
                // If user finished Discord login and redirected to home dashboard, navigate to /api-keys immediately
                if (!currentUrl.Contains("/api-keys"))
                {
                    GoToApiKeysButton.Visibility = Visibility.Visible;
                    StatusText.Text = "Login detected! Navigating to API Keys page...";
                    Browser.CoreWebView2?.Navigate("https://hubcapmanifest.com/api-keys");
                    return;
                }

                GoToApiKeysButton.Visibility = Visibility.Collapsed;
                StatusText.Text = "Connected to Hubcap API Keys. Generating and extracting key...";
                _ = CheckForApiKeyAsync();
            }
        }

        private async void CoreWebView2_WebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            if (_isKeyFound) return;

            try
            {
                string uri = e.Request.Uri;

                // Intercept any responses from hubcapmanifest.com
                if (uri.Contains("hubcapmanifest.com"))
                {
                    if (e.Response != null && (e.Response.StatusCode == 200 || e.Response.StatusCode == 201 || e.Response.StatusCode == 400 || e.Response.StatusCode == 403))
                    {
                        try
                        {
                            using var stream = await e.Response.GetContentAsync();
                            if (stream != null)
                            {
                                using var reader = new StreamReader(stream);
                                string body = await reader.ReadToEndAsync();
                                if (!string.IsNullOrWhiteSpace(body))
                                {
                                    // 1. Intercept membership error from Hubcap responses
                                    if (body.Contains("member of our Discord server", StringComparison.OrdinalIgnoreCase) ||
                                        (body.Contains("You must be a member", StringComparison.OrdinalIgnoreCase) && body.Contains("Discord", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        Logger.Log("[HubcapLoginDialog] Intercepted Discord server membership error in HTTP response.");
                                        Dispatcher.InvokeAsync(async () => await HandleMembershipRequiredAsync());
                                        return;
                                    }

                                    // 2. Search for smm_ key in HTTP response
                                    var match = System.Text.RegularExpressions.Regex.Match(body, @"\b(smm_[a-zA-Z0-9_\-]{24,128})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                    if (match.Success)
                                    {
                                        Logger.Log("[HubcapLoginDialog] Captured API key from HTTP response.");
                                        Dispatcher.InvokeAsync(async () => await OnApiKeyFoundAsync(match.Groups[1].Value));
                                        return;
                                    }

                                    // 3. Fallback json parsing
                                    TryExtractKeyFromJson(body);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HubcapLoginDialog] Response interception error: {ex.Message}");
            }
        }

        private async Task CheckForApiKeyAsync()
        {
            if (_isKeyFound || Browser.CoreWebView2 == null) return;

            try
            {
                string currentUrl = Browser.Source?.ToString() ?? "";

                // If on Discord invite page, try to auto-accept invite
                if (currentUrl.Contains("discord.com/invite") || currentUrl.Contains("discord.gg"))
                {
                    await RunAutoAcceptDiscordInviteAsync();
                    return;
                }

                // If on Discord channels page, server is joined
                if (currentUrl.Contains("discord.com/channels/") || currentUrl.Contains("1327856038967246970"))
                {
                    if (_isJoiningDiscordServer)
                    {
                        await OnDiscordServerJoinedAsync();
                        return;
                    }
                }

                // Scenario A: Discord Login page (credentials required) -> Reveal window
                if (currentUrl.Contains("discord.com/login") || currentUrl.Contains("discord.com/register"))
                {
                    MakeVisible();
                    return;
                }

                // Scenario B: Discord OAuth Authorize page -> Auto-click "Authorize" button!
                if (currentUrl.Contains("discord.com/oauth2/authorize"))
                {
                    string authScript = @"
                        (() => {
                            let buttons = Array.from(document.querySelectorAll('button'));
                            for (let b of buttons) {
                                let t = (b.innerText || b.textContent || '').trim().toLowerCase();
                                if (t === 'authorize' || t === 'otorisasikan' || t === 'autoriser' || t === 'autorizar' || b.getAttribute('type') === 'submit') {
                                    b.click();
                                    return { action: 'clicked_authorize' };
                                }
                            }
                            return { action: 'waiting_authorize' };
                        })();
                    ";
                    await Browser.ExecuteScriptAsync(authScript);
                    return;
                }

                // Scenario C & D: Hubcap pages
                if (currentUrl.Contains("hubcapmanifest.com"))
                {
                    // Check if membership error text is present
                    string checkMemScript = @"
                        (() => {
                            let t = (document.body ? document.body.innerText || document.body.textContent || '' : '').toLowerCase();
                            return (t.includes('member of our discord server to access') || t.includes('must be a member of our discord server')) ? 'membership_required' : 'ok';
                        })();
                    ";
                    string checkMem = await Browser.ExecuteScriptAsync(checkMemScript);
                    if (checkMem != null && checkMem.Contains("membership_required"))
                    {
                        await HandleMembershipRequiredAsync();
                        return;
                    }

                    // Check if 'Continue with Discord' is visible (e.g. unauthenticated landing page) and auto-click it
                    if (await TryClickContinueWithDiscordAsync())
                    {
                        return;
                    }

                    // If not yet on /api-keys (e.g. landed on home after OAuth callback), navigate to /api-keys
                    if (!currentUrl.Contains("/api-keys"))
                    {
                        Browser.CoreWebView2?.Navigate("https://hubcapmanifest.com/api-keys");
                        return;
                    }

                    // On hubcapmanifest.com/api-keys and authenticated -> extract or auto-generate key
                    string script = @"
                        (async () => {
                            function triggerClick(el) {
                                if (!el) return;
                                try { el.focus(); } catch(e) {}
                                try {
                                    el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, view: window }));
                                    el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, view: window }));
                                    el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                                } catch(e) {}
                                try { el.click(); } catch(e) {}
                            }

                            // 1. Scan steam://hubcaptools/setapikey/ links in DOM
                            let links = Array.from(document.querySelectorAll('a[href*=""setapikey""]'));
                            for (let l of links) {
                                let h = l.getAttribute('href') || '';
                                let m = h.match(/setapikey\/([a-zA-Z0-9_\-]{20,128})/i);
                                if (m) return { action: 'found', key: m[1] };
                            }

                            // 2. Direct Regex scan for smm_ key across entire page text
                            let bodyText = document.body ? (document.body.innerText || document.body.textContent || '') : '';
                            let keyMatch = bodyText.match(/\b(smm_[a-zA-Z0-9_\-]{24,128})\b/i);
                            if (keyMatch) {
                                return { action: 'found', key: keyMatch[1] };
                            }

                            // 3. Scan entire HTML source for smm_ key
                            let html = document.documentElement ? document.documentElement.innerHTML : '';
                            let htmlMatch = html.match(/\b(smm_[a-zA-Z0-9_\-]{24,128})\b/i);
                            if (htmlMatch) {
                                return { action: 'found', key: htmlMatch[1] };
                            }

                            // 4. Scan all input elements, textareas, code blocks, spans, divs, pre elements
                            let elements = Array.from(document.querySelectorAll('input, textarea, code, pre, div, span, p'));
                            for (let el of elements) {
                                let val = (el.value || el.innerText || el.textContent || '').trim();
                                let m = val.match(/\b(smm_[a-zA-Z0-9_\-]{24,128})\b/i);
                                if (m) {
                                    return { action: 'found', key: m[1] };
                                }
                            }

                            // 5. Check if Copy button has clipboard data or onclick attribute with key
                            let copyBtns = Array.from(document.querySelectorAll('button, a'));
                            for (let cb of copyBtns) {
                                let cText = (cb.innerText || cb.textContent || '').trim().toLowerCase();
                                if (cText.includes('copy') || cText.includes('salin')) {
                                    let attr = cb.getAttribute('data-clipboard-text') || cb.getAttribute('data-copy') || cb.getAttribute('value') || '';
                                    let cm = attr.match(/\b(smm_[a-zA-Z0-9_\-]{24,128})\b/i);
                                    if (cm) return { action: 'found', key: cm[1] };
                                }
                            }

                            // 6. Check if Modal Confirm Button is present (exact text 'Generate' or 'Confirm' inside modal dialog)
                            let allButtons = Array.from(document.querySelectorAll('button, a[role=""button""]'));
                            let modalGenerateBtn = allButtons.find(b => {
                                let t = (b.innerText || b.textContent || '').trim().toLowerCase();
                                if (t === 'generate' || t === 'confirm' || t === 'konfirmasi') {
                                    let parent = b.parentElement;
                                    if (parent && (parent.innerText || '').toLowerCase().includes('cancel')) {
                                        return true;
                                    }
                                    let modal = b.closest('.fixed, [role=""dialog""], .modal, div[style*=""z-index""]');
                                    if (modal) return true;
                                    return true;
                                }
                                return false;
                            });

                            if (modalGenerateBtn) {
                                triggerClick(modalGenerateBtn);
                                return { action: 'clicked_modal_confirm', key: null };
                            }

                            // 7. Auto-click 'Generate Key' or 'Regenerate Key' button on the main page
                            let mainGenBtn = allButtons.find(b => {
                                let t = (b.innerText || b.textContent || '').trim().toLowerCase();
                                return t === 'generate key' || t === 'regenerate key' || t === 'create key';
                            });
                            if (mainGenBtn) {
                                triggerClick(mainGenBtn);
                                return { action: 'clicked_generate_button', key: null };
                            }

                            // 8. Direct fetch API call to generate key immediately
                            try {
                                let genRes = await fetch('/api-keys/generate-key', {
                                    method: 'POST',
                                    headers: { 'Content-Type': 'application/json' },
                                    credentials: 'include',
                                    body: '{}'
                                });
                                if (genRes.ok) {
                                    let genData = await genRes.json();
                                    let genKey = genData.api_key || genData.key;
                                    if (genKey && genKey.length >= 20) {
                                        return { action: 'found', key: genKey };
                                    }
                                }
                            } catch(e) {}

                            return { action: 'waiting', key: null };
                        })();
                    ";

                    string jsonResult = await Browser.ExecuteScriptAsync(script);
                    if (!string.IsNullOrWhiteSpace(jsonResult) && jsonResult != "null")
                    {
                        using var doc = JsonDocument.Parse(jsonResult);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            if (doc.RootElement.TryGetProperty("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
                            {
                                string key = keyEl.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(key) && key.Length >= 20)
                                {
                                    await OnApiKeyFoundAsync(key);
                                    return;
                                }
                            }

                            if (doc.RootElement.TryGetProperty("action", out var actionEl))
                            {
                                string action = actionEl.GetString() ?? "";
                                if (action == "clicked_generate_button" || action == "clicked_modal_confirm" || action == "clicked_initial_generate")
                                {
                                    _hasAttemptedKeyGeneration = true;
                                    StatusText.Text = "Generating and extracting API key on Hubcap...";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HubcapLoginDialog] CheckForApiKeyAsync error: {ex.Message}");
            }
        }

        private void TryExtractKeyFromJson(string json)
        {
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(json, @"\b(smm_[a-zA-Z0-9_\-]{24,128})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    Dispatcher.InvokeAsync(async () => await OnApiKeyFoundAsync(match.Groups[1].Value));
                    return;
                }

                using var doc = JsonDocument.Parse(json);
                string key = "";
                if (doc.RootElement.TryGetProperty("api_key", out var keyProp) && keyProp.ValueKind == JsonValueKind.String)
                {
                    key = keyProp.GetString() ?? "";
                }
                else if (doc.RootElement.TryGetProperty("key", out var keyProp2) && keyProp2.ValueKind == JsonValueKind.String)
                {
                    key = keyProp2.GetString() ?? "";
                }

                if (!string.IsNullOrWhiteSpace(key) && key.Length >= 20)
                {
                    Dispatcher.InvokeAsync(async () => await OnApiKeyFoundAsync(key));
                }
            }
            catch {}
        }

        private async Task OnApiKeyFoundAsync(string key)
        {
            if (_isKeyFound) return;
            _isKeyFound = true;
            UserCanceled = false;
            _pollTimer?.Stop();
            _safetyTimeoutTimer?.Stop();

            ExtractedApiKey = key;
            StatusText.Text = "API Key retrieved successfully! Saving credentials...";

            // Persist to local config and sync to Supabase
            await RemoteConfigService.SaveHubcapApiKeyAsync(key);

            await Task.Delay(400);
            DialogResult = true;
            Close();
        }

        private async Task HandleMembershipRequiredAsync()
        {
            if (_isJoiningDiscordServer) return;

            if (_hasAttemptedAutoJoin)
            {
                // If auto-join was already attempted and Hubcap still returns membership error,
                // reveal the window so the user can verify or click manually
                MakeVisible();
                StatusText.Text = "Hubcap requires Discord server membership. Please join the server or click 'Join Hubcap Discord' below.";
                JoinDiscordServerButton.Visibility = Visibility.Visible;
                RetryHubcapButton.Visibility = Visibility.Visible;
                return;
            }

            _hasAttemptedAutoJoin = true;
            _isJoiningDiscordServer = true;
            _joinAttemptTicks = 0;

            Logger.Log("[HubcapLoginDialog] Discord server membership required detected. Navigating to Hubcap Discord invite...");
            StatusText.Text = "Discord server membership required. Automatically joining Hubcap Discord server...";
            JoinDiscordServerButton.Visibility = Visibility.Visible;
            RetryHubcapButton.Visibility = Visibility.Visible;

            await Task.Delay(300);
            Browser.CoreWebView2?.Navigate("https://discord.com/invite/hubcapsmanifest");
        }

        private async Task RunAutoAcceptDiscordInviteAsync()
        {
            if (Browser.CoreWebView2 == null) return;

            try
            {
                _joinAttemptTicks++;

                string script = @"
                    (() => {
                        // 1. Check if already inside channels
                        if (window.location.href.includes('/channels/1327856038967246970') || 
                            window.location.href.includes('/channels/')) {
                            return { action: 'joined' };
                        }

                        // 2. Check if a CAPTCHA / hCaptcha / Cloudflare challenge is present
                        let hasCaptcha = document.querySelector('iframe[src*=""captcha""], iframe[src*=""hcaptcha""], div[class*=""captcha""]') != null;
                        if (hasCaptcha) {
                            return { action: 'captcha' };
                        }

                        // 3. Search and click 'Accept Invite' button
                        let buttons = Array.from(document.querySelectorAll('button'));
                        for (let b of buttons) {
                            let t = (b.innerText || b.textContent || '').trim().toLowerCase();
                            if (t.includes('accept invite') || 
                                t.includes('terima undangan') || 
                                t.includes('join') || 
                                t.includes('bergabung') || 
                                t.includes('rejoindre') || 
                                t.includes('beitreten') || 
                                t.includes('aceptar') || 
                                t.includes('aceitar') || 
                                t.includes('invite')) {
                                b.click();
                                return { action: 'clicked_accept' };
                            }
                        }

                        // 4. Auto-accept community membership screening rules if present
                        let checkboxes = Array.from(document.querySelectorAll('input[type=""checkbox""]'));
                        for (let cb of checkboxes) {
                            if (!cb.checked) {
                                cb.click();
                            }
                        }
                        for (let b of buttons) {
                            let t = (b.innerText || b.textContent || '').trim().toLowerCase();
                            if (t === 'submit' || t === 'selesai' || t === 'finish' || t === 'complete') {
                                b.click();
                            }
                        }

                        return { action: 'waiting' };
                    })();
                ";

                string jsonResult = await Browser.ExecuteScriptAsync(script);
                if (!string.IsNullOrWhiteSpace(jsonResult) && jsonResult != "null")
                {
                    using var doc = JsonDocument.Parse(jsonResult);
                    if (doc.RootElement.TryGetProperty("action", out var actionEl))
                    {
                        string action = actionEl.GetString() ?? "";
                        if (action == "clicked_accept")
                        {
                            StatusText.Text = "Accepting server invitation... Waiting for Discord confirmation...";
                            await Task.Delay(2500);
                            string currentUrl = Browser.Source?.ToString() ?? "";
                            if (currentUrl.Contains("channels") || currentUrl.Contains("1327856038967246970"))
                            {
                                await OnDiscordServerJoinedAsync();
                            }
                            else if (_joinAttemptTicks >= 3)
                            {
                                await OnDiscordServerJoinedAsync();
                            }
                        }
                        else if (action == "captcha")
                        {
                            MakeVisible();
                            StatusText.Text = "Please complete the Discord verification check above to join the server...";
                        }
                        else if (action == "joined")
                        {
                            await OnDiscordServerJoinedAsync();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HubcapLoginDialog] RunAutoAcceptDiscordInviteAsync error: {ex.Message}");
            }
        }

        private async Task OnDiscordServerJoinedAsync()
        {
            if (!_isJoiningDiscordServer) return;
            _isJoiningDiscordServer = false;

            Logger.Log("[HubcapLoginDialog] Successfully joined Hubcap Discord server! Reconnecting to Hubcap...");
            StatusText.Text = "Joined Hubcap Discord server! Reconnecting to Hubcap...";
            JoinDiscordServerButton.Visibility = Visibility.Collapsed;

            await Task.Delay(1500);
            Browser.CoreWebView2?.Navigate("https://hubcapmanifest.com");
        }

        private void JoinDiscordServerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://discord.gg/hubcapsmanifest",
                    UseShellExecute = true
                });
            }
            catch { }

            Browser.CoreWebView2?.Navigate("https://discord.com/invite/hubcapsmanifest");
        }

        private void RetryHubcapButton_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Retrying connection to Hubcap...";
            Browser.CoreWebView2?.Navigate("https://hubcapmanifest.com");
        }

        private void GoToApiKeysButton_Click(object sender, RoutedEventArgs e)
        {
            Browser.CoreWebView2?.Navigate("https://hubcapmanifest.com/api-keys");
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
