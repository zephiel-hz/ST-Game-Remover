using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SteamPluginManager.Helpers;
using SteamPluginManager.Services;
using SteamPluginManager.Services.ResourcePipeline;

namespace SteamPluginManager.Views
{
    public partial class RequestGameModalView : UserControl
    {
        private readonly HttpClient _httpClient;
        private Func<int, bool>? _isGameAvailableInApp;
        private Action? _onGameAddedOrRequested;

        private static readonly SolidColorBrush StageCompletedBrush = new((Color)ColorConverter.ConvertFromString("#10B981"));
        private static readonly SolidColorBrush StageActiveBrush = new((Color)ColorConverter.ConvertFromString("#3B82F6"));
        private static readonly SolidColorBrush StageInactiveBgBrush = new((Color)ColorConverter.ConvertFromString("#1A202C"));
        private static readonly SolidColorBrush StageInactiveBorderBrush = new((Color)ColorConverter.ConvertFromString("#2D3748"));
        private static readonly SolidColorBrush StageMutedTextBrush = new((Color)ColorConverter.ConvertFromString("#94A3B8"));
        private static readonly SolidColorBrush StageWhiteBrush = new(Colors.White);

        public bool IsOpen { get; private set; }

        public RequestGameModalView()
        {
            InitializeComponent();
            _httpClient = SharedHttpClient.Instance;
        }

        public void ShowModal(Func<int, bool>? isGameAvailableInApp = null, Action? onGameAddedOrRequested = null)
        {
            _isGameAvailableInApp = isGameAvailableInApp;
            _onGameAddedOrRequested = onGameAddedOrRequested;

            Dispatcher.Invoke(() =>
            {
                IsOpen = true;
                HideRequestProgress();
                ResetRequestStages();

                if (RequestFlowGuideContent != null)
                {
                    RequestFlowGuideContent.Visibility = Visibility.Collapsed;
                    if (GuideToggleChevron != null)
                        GuideToggleChevron.Text = "\uE70D";
                }

                if (RequestValidationText != null)
                {
                    RequestValidationText.Visibility = Visibility.Collapsed;
                    RequestValidationText.Text = string.Empty;
                }

                if (SubmitRequestButton != null)
                {
                    SubmitRequestButton.IsEnabled = true;
                    SubmitRequestButton.Content = "Submit";
                }

                if (CancelRequestButton != null)
                {
                    CancelRequestButton.IsEnabled = true;
                    CancelRequestButton.Content = "Cancel";
                }

                if (RequestUrlTextBox != null)
                {
                    RequestUrlTextBox.Text = string.Empty;
                }

                var mainShell = WindowNavigator.GetMainShell();
                if (mainShell != null)
                {
                    mainShell.ShowGlobalModal(this);
                }
                else
                {
                    this.Visibility = Visibility.Visible;
                }

                RequestUrlTextBox?.Focus();
            });
        }

        public void HideModal()
        {
            Dispatcher.Invoke(() =>
            {
                IsOpen = false;
                HideRequestProgress();

                var mainShell = WindowNavigator.GetMainShell();
                if (mainShell != null)
                {
                    mainShell.HideGlobalModal();
                }
                else
                {
                    this.Visibility = Visibility.Collapsed;
                }
            });
        }

        private void ToggleRequestGuideButton_Click(object sender, RoutedEventArgs e)
        {
            if (RequestFlowGuideContent == null) return;

            bool isCurrentlyVisible = RequestFlowGuideContent.Visibility == Visibility.Visible;
            RequestFlowGuideContent.Visibility = isCurrentlyVisible ? Visibility.Collapsed : Visibility.Visible;
            if (GuideToggleChevron != null)
            {
                // Up chevron \uE70E when open, Down chevron \uE70D when closed
                GuideToggleChevron.Text = isCurrentlyVisible ? "\uE70D" : "\uE70E";
            }
        }

        private void CancelRequestButton_Click(object sender, RoutedEventArgs e)
        {
            HideModal();
        }

        private async void SubmitRequestButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SubmitRequestButton?.Content?.ToString() == "Done" || SubmitRequestButton?.Content?.ToString() == "Close")
                {
                    HideModal();
                    return;
                }

                var input = RequestUrlTextBox?.Text?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(input))
                {
                    ShowRequestValidation("Please enter a Steam AppID or URL.");
                    return;
                }

                // Check if display name is set
                var displayName = await DashboardView.GetCurrentDisplayNameAsync();
                Logger.Log($"[RequestGameModalView] Display name check: '{displayName}'");
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    ShowRequestValidation("Display name must be set before requesting a game. Please set your display name in the Dashboard.");
                    return;
                }

                int appId = 0;
                string steamUrl = string.Empty;

                // Check if the input is a pure numeric AppID (e.g. 730310)
                if (int.TryParse(input, out int parsedAppId) && parsedAppId > 0)
                {
                    appId = parsedAppId;
                    steamUrl = $"https://store.steampowered.com/app/{appId}/";
                }
                else
                {
                    // Extract AppID from Steam Store or SteamDB URL
                    var appIdMatch = Regex.Match(input, @"(?:store\.steampowered\.com/app|steamdb\.info/app)/(\d+)", RegexOptions.IgnoreCase);
                    if (appIdMatch.Success && int.TryParse(appIdMatch.Groups[1].Value, out int extractedAppId) && extractedAppId > 0)
                    {
                        appId = extractedAppId;
                        steamUrl = input;
                    }
                    else
                    {
                        ShowRequestValidation("Invalid input. Please enter a numeric Steam AppID (e.g. 730310) or a valid Steam Store / SteamDB URL.");
                        return;
                    }
                }

                // Check if game already exists in-app
                if (_isGameAvailableInApp != null && _isGameAvailableInApp(appId))
                {
                    ShowRequestValidation("Game already available in-app.");
                    return;
                }

                SubmitRequestButton.IsEnabled = false;
                CancelRequestButton.IsEnabled = false;
                RequestValidationText.Visibility = Visibility.Collapsed;

                // Collapse guide card during progress to keep view focused on the live stages
                if (RequestFlowGuideContent != null)
                {
                    RequestFlowGuideContent.Visibility = Visibility.Collapsed;
                    if (GuideToggleChevron != null)
                        GuideToggleChevron.Text = "\uE70D";
                }

                // Stage 1: Verify AppID is valid and exists on Steam Store or SteamDB
                SetRequestStage(1, $"Verifying AppID {appId} on Steam...");
                var (isValidApp, verifiedGameName) = await ValidateSteamAppIdAsync(appId);
                if (!isValidApp)
                {
                    HideRequestProgress();
                    SubmitRequestButton.IsEnabled = true;
                    CancelRequestButton.IsEnabled = true;
                    ShowRequestValidation($"AppID {appId} was not found on Steam Store or SteamDB. Please verify the AppID and try again.");
                    return;
                }

                // Stage 2: Early Hubcap Authentication & Session Preparation (Mandatory before DRM check)
                SetRequestStage(2, "Verifying Hubcap session...");
                string apiKey = await RemoteConfigService.GetHubcapApiKeyAsync();
                bool isUserCanceled = false;

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    apiKey = await Dispatcher.InvokeAsync(() =>
                    {
                        var loginDialog = new HubcapLoginDialog(silentMode: false)
                        {
                            Owner = Window.GetWindow(this)
                        };
                        bool? dialogResult = loginDialog.ShowDialog();
                        if (loginDialog.UserCanceled || dialogResult != true)
                        {
                            isUserCanceled = true;
                        }
                        if (dialogResult == true && !string.IsNullOrWhiteSpace(loginDialog.ExtractedApiKey))
                        {
                            return loginDialog.ExtractedApiKey;
                        }
                        return null;
                    }).Task ?? string.Empty;
                }

                if (isUserCanceled || string.IsNullOrWhiteSpace(apiKey))
                {
                    Logger.Log($"[RequestGameModalView] User canceled Hubcap login/verification for AppID {appId}. Aborting request.");
                    HideRequestProgress();
                    SubmitRequestButton.IsEnabled = true;
                    CancelRequestButton.IsEnabled = true;
                    ShowRequestValidation("Hubcap Discord authorization is required to verify DRM status and process game requests.");
                    return;
                }

                // Stage 2 (cont): Single Denuvo Detection via Hubcap (with Steam Store fallback)
                SetRequestStage(2, "Checking Denuvo status via Hubcap...");
                bool isDenuvo = await ManifestHubService.DetectDenuvoAsync(appId, status => Dispatcher.Invoke(() => SetRequestStage(2, status)));
                Logger.Log($"[RequestGameModalView] AppID {appId} - Denuvo check result: {isDenuvo}");

                // Stage 3: Search ManifestHub (executes directly with isDenuvo, no re-checking)
                SetRequestStage(3, "Searching ManifestHub...");
                string sourceName = "ManifestHub";
                var downloadedFiles = await ManifestHubService.FindAndDownloadFilesAsync(appId, isDenuvo, status =>
                {
                    Dispatcher.Invoke(() => SetRequestStage(3, status));
                });

                // Stage 3 (cont): Fallback to Hubcap Manifest if not found on ManifestHub
                if (downloadedFiles == null || downloadedFiles.Count == 0)
                {
                    Logger.Log($"[RequestGameModalView] AppID {appId} not found in ManifestHub. Checking Hubcap Manifest...");
                    SetRequestStage(3, "Searching Hubcap Manifest...");
                    downloadedFiles = await HubcapManifestService.FindAndDownloadFilesAsync(
                        appId,
                        isDenuvo,
                        status => Dispatcher.Invoke(() => SetRequestStage(3, status)));

                    if (downloadedFiles != null && downloadedFiles.Count > 0)
                    {
                        sourceName = "Hubcap Manifest";
                    }
                }

                // Stage 4: Deploy / Discord
                if (downloadedFiles != null && downloadedFiles.Count > 0)
                {
                    SetRequestStage(4, "Uploading to Cloud...");
                    var (uploadSuccess, uploadMsg, gameName) = await ManifestHubService.UploadFolderToSupabaseAsync(appId, downloadedFiles, status =>
                    {
                        Dispatcher.Invoke(() => SetRequestStage(4, status));
                    });

                    if (uploadSuccess)
                    {
                        _onGameAddedOrRequested?.Invoke();

                        // Stage 5: Online-Fix Automated Processing
                        SetRequestStage(5, "Checking Online-Fix resources...");

                        bool onlineFixFound = false;
                        string onlineFixDetail = string.Empty;

                        if (ServiceConfiguration.Current.ResourceSource.AutoProcessOnManifestSuccess)
                        {
                            try
                            {
                                var coordinator = new ResourceWorkflowCoordinator();
                                var progressReporter = new Progress<ResourceWorkflowProgress>(p =>
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        SetRequestStage(5, p.Message);
                                        if (RequestProgressBar != null)
                                        {
                                            if (p.Percentage > 0 && p.Percentage <= 100)
                                            {
                                                RequestProgressBar.IsIndeterminate = false;
                                                RequestProgressBar.Value = p.Percentage;
                                            }
                                            else
                                            {
                                                RequestProgressBar.IsIndeterminate = true;
                                            }
                                        }
                                    });
                                });

                                var result = await coordinator.ExecuteWorkflowAsync(appId, gameName, progressReporter);
                                if (result.Success)
                                {
                                    onlineFixFound = true;
                                    onlineFixDetail = "Online-Fix archive successfully repacked and uploaded to Backblaze B2.";
                                    Logger.Log($"[RequestGameModalView] Companion resource successfully processed and uploaded to B2: {result.RemoteStorageKey}");
                                }
                                else if (result.FinalStage == WorkflowStage.NotFound)
                                {
                                    onlineFixDetail = "No Online-Fix file available for this game on source.";
                                    Logger.Log($"[RequestGameModalView] Companion Online-Fix not found for '{gameName}'.");
                                }
                                else
                                {
                                    onlineFixDetail = $"Online-Fix processing: {result.ErrorMessage}";
                                    Logger.Log($"[RequestGameModalView] Companion Online-Fix status: {result.FinalStage} - {result.ErrorMessage}");
                                }
                            }
                            catch (Exception ex)
                            {
                                onlineFixDetail = $"Online-Fix note: {ex.Message}";
                                Logger.Log($"[RequestGameModalView] Companion resource pipeline exception: {ex.Message}");
                            }
                        }

                        // Mark all stages 1-5 as completed
                        SetRequestStage(6, onlineFixFound ? "All completed! Manifest and Online-Fix are ready." : "Completed! Manifest is ready in catalog.");

                        if (RequestProgressBar != null)
                        {
                            RequestProgressBar.IsIndeterminate = false;
                            RequestProgressBar.Value = 100;
                        }

                        // Auto-refresh views and caches immediately
                        OnlineFixView.InvalidateCache();
                        WindowNavigator.RefreshOnlineFixView();
                        _ = DashboardView.RefreshNewestManifestsAsync();

                        // Jeda singkat agar pengguna dapat melihat semua indikator hijau (completed)
                        await Task.Delay(1400);

                        // Auto-close modal permintaan game
                        HideModal();
                        return;
                    }
                    else
                    {
                        Logger.Log($"[RequestGameModalView] Upload to Supabase failed: {uploadMsg}. Proceeding with Discord webhook fallback.");
                    }
                }
                else
                {
                    Logger.Log($"[RequestGameModalView] Game {appId} not found in ManifestHub or Hubcap Manifest. Proceeding with Discord webhook fallback.");
                }

                // Fallback to Discord Webhook
                SetRequestStage(4, "Checking pending queue...");
                var alreadyRequested = await GetPendingRequestsFromSupabaseAsync();
                var duplicateRequest = alreadyRequested.FirstOrDefault(r => r.AppId == appId);
                if (duplicateRequest != null)
                {
                    HideRequestProgress();
                    var requesterLabel = string.IsNullOrWhiteSpace(duplicateRequest.Requester) ? "another user" : duplicateRequest.Requester;
                    ShowRequestValidation($"This game has already been requested by {requesterLabel}.");
                    return;
                }

                var webhookUrl = DiscordWebhookUrl;
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    HideRequestProgress();
                    ShowRequestValidation("Discord webhook is not available in the application.");
                    return;
                }

                SetRequestStage(4, "Sending request to Discord Admin...");
                var (success, messageId) = await SendDiscordRequestAsync(steamUrl, appId, webhookUrl);
                HideRequestProgress();
                if (success)
                {
                    ModernMessageBox.Show("Game was not found automatically on ManifestHub or Hubcap Manifest.\n\nYour game request has been successfully sent to the Discord Admin.", "Request Submitted", MessageBoxButton.OK, MessageBoxImage.Information);
                    HideModal();
                }
                else
                {
                    ShowRequestValidation("Failed to send request. Please try again later or check the webhook URL.");
                }
            }
            catch (Exception ex)
            {
                HideRequestProgress();
                ShowRequestValidation($"Request failed: {ex.Message}");
            }
            finally
            {
                SubmitRequestButton.IsEnabled = true;
                CancelRequestButton.IsEnabled = true;
                SubmitRequestButton.Content = "Submit";
            }
        }

        #region Progress & Live Stage Stepper

        private void ResetRequestStages()
        {
            try
            {
                var circles = new[] { Stage1Circle, Stage2Circle, Stage3Circle, Stage4Circle, Stage5Circle };
                var icons = new[] { Stage1Icon, Stage2Icon, Stage3Icon, Stage4Icon, Stage5Icon };
                var labels = new[] { Stage1Label, Stage2Label, Stage3Label, Stage4Label, Stage5Label };
                var conns = new[] { StageConn1, StageConn2, StageConn3, StageConn4 };

                for (int i = 0; i < 5; i++)
                {
                    if (circles[i] != null)
                    {
                        circles[i].Background = (Brush)FindResource("InputBackgroundBrush") ?? StageInactiveBgBrush;
                        circles[i].BorderBrush = (Brush)FindResource("BorderBrush") ?? StageInactiveBorderBrush;
                    }
                    if (icons[i] != null)
                    {
                        icons[i].Text = (i + 1).ToString();
                        icons[i].Foreground = (Brush)FindResource("MutedForegroundBrush") ?? StageMutedTextBrush;
                    }
                    if (labels[i] != null)
                    {
                        labels[i].Foreground = (Brush)FindResource("MutedForegroundBrush") ?? StageMutedTextBrush;
                        labels[i].FontWeight = FontWeights.SemiBold;
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    if (conns[i] != null)
                    {
                        conns[i].Background = (Brush)FindResource("BorderBrush") ?? StageInactiveBorderBrush;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[RequestGameModalView] ResetRequestStages error: {ex.Message}");
            }
        }

        private void SetRequestStage(int activeStage, string detailMessage)
        {
            try
            {
                var circles = new[] { Stage1Circle, Stage2Circle, Stage3Circle, Stage4Circle, Stage5Circle };
                var icons = new[] { Stage1Icon, Stage2Icon, Stage3Icon, Stage4Icon, Stage5Icon };
                var labels = new[] { Stage1Label, Stage2Label, Stage3Label, Stage4Label, Stage5Label };
                var conns = new[] { StageConn1, StageConn2, StageConn3, StageConn4 };

                var accent = (Brush)FindResource("AccentBrush") ?? StageActiveBrush;
                var muted = (Brush)FindResource("MutedForegroundBrush") ?? StageMutedTextBrush;
                var border = (Brush)FindResource("BorderBrush") ?? StageInactiveBorderBrush;
                var inputBg = (Brush)FindResource("InputBackgroundBrush") ?? StageInactiveBgBrush;
                var foreground = (Brush)FindResource("ForegroundBrush") ?? StageWhiteBrush;

                for (int i = 0; i < 5; i++)
                {
                    int stageNum = i + 1;
                    if (stageNum < activeStage)
                    {
                        // Completed stage
                        if (circles[i] != null)
                        {
                            circles[i].Background = StageCompletedBrush;
                            circles[i].BorderBrush = StageCompletedBrush;
                        }
                        if (icons[i] != null)
                        {
                            icons[i].Text = "\u2713"; // Checkmark
                            icons[i].Foreground = StageWhiteBrush;
                        }
                        if (labels[i] != null)
                        {
                            labels[i].Foreground = foreground;
                            labels[i].FontWeight = FontWeights.SemiBold;
                        }
                    }
                    else if (stageNum == activeStage)
                    {
                        // Current active stage
                        if (circles[i] != null)
                        {
                            circles[i].Background = accent;
                            circles[i].BorderBrush = accent;
                        }
                        if (icons[i] != null)
                        {
                            icons[i].Text = stageNum.ToString();
                            icons[i].Foreground = StageWhiteBrush;
                        }
                        if (labels[i] != null)
                        {
                            labels[i].Foreground = accent;
                            labels[i].FontWeight = FontWeights.Bold;
                        }
                    }
                    else
                    {
                        // Pending stage
                        if (circles[i] != null)
                        {
                            circles[i].Background = inputBg;
                            circles[i].BorderBrush = border;
                        }
                        if (icons[i] != null)
                        {
                            icons[i].Text = stageNum.ToString();
                            icons[i].Foreground = muted;
                        }
                        if (labels[i] != null)
                        {
                            labels[i].Foreground = muted;
                            labels[i].FontWeight = FontWeights.SemiBold;
                        }
                    }
                }

                // Connectors
                for (int i = 0; i < 4; i++)
                {
                    if (conns[i] != null)
                    {
                        if (i + 1 < activeStage)
                        {
                            conns[i].Background = StageCompletedBrush;
                        }
                        else if (i + 1 == activeStage)
                        {
                            conns[i].Background = accent;
                        }
                        else
                        {
                            conns[i].Background = border;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(detailMessage))
                {
                    ShowRequestProgress(detailMessage);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[RequestGameModalView] SetRequestStage error: {ex.Message}");
            }
        }

        private void ShowRequestValidation(string message)
        {
            HideRequestProgress();
            if (RequestValidationText != null)
            {
                RequestValidationText.Text = message;
                RequestValidationText.Visibility = Visibility.Visible;
            }
        }

        private void ShowRequestProgress(string message)
        {
            if (RequestValidationText != null)
                RequestValidationText.Visibility = Visibility.Collapsed;

            if (RequestProgressContainer != null)
            {
                RequestProgressContainer.Visibility = Visibility.Visible;
                if (RequestProgressText != null)
                    RequestProgressText.Text = message;

                StartRequestProgressAnimation();
            }
        }

        private void HideRequestProgress()
        {
            if (RequestProgressContainer != null)
                RequestProgressContainer.Visibility = Visibility.Collapsed;

            StopRequestProgressAnimation();
        }

        private void StartRequestProgressAnimation()
        {
            try
            {
                if (RequestProgressRotate != null)
                {
                    var animation = new DoubleAnimation
                    {
                        From = 0,
                        To = 360,
                        Duration = new Duration(TimeSpan.FromSeconds(1.5)),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    RequestProgressRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
                }
            }
            catch { }
        }

        private void StopRequestProgressAnimation()
        {
            try
            {
                if (RequestProgressRotate != null)
                {
                    RequestProgressRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                }
            }
            catch { }
        }

        #endregion

        #region Steam Verification & Discord Request

        private static string DiscordWebhookUrl
        {
            get
            {
                var configuredUrl = ServiceConfiguration.Current.Discord.GameRequestWebhookUrl;
                if (!string.IsNullOrWhiteSpace(configuredUrl))
                    return configuredUrl.Trim();

                var environmentUrl = Environment.GetEnvironmentVariable("DISCORD_GAME_REQUEST_WEBHOOK_URL");
                return string.IsNullOrWhiteSpace(environmentUrl) ? string.Empty : environmentUrl.Trim();
            }
        }

        private async Task<(bool isValid, string gameName)> ValidateSteamAppIdAsync(int appId)
        {
            if (appId <= 0)
                return (false, string.Empty);

            // 1. Primary check: Official Steam Store AppDetails API
            try
            {
                string url = $"https://store.steampowered.com/api/appdetails?appids={appId}&cc=US&l=english";
                using var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty(appId.ToString(), out var appElement) &&
                        appElement.TryGetProperty("success", out var successEl) &&
                        successEl.GetBoolean())
                    {
                        string name = string.Empty;
                        if (appElement.TryGetProperty("data", out var dataEl) &&
                            dataEl.TryGetProperty("name", out var nameEl))
                        {
                            name = nameEl.GetString()?.Trim() ?? string.Empty;
                        }
                        return (true, !string.IsNullOrWhiteSpace(name) ? name : $"AppID {appId}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[RequestGameModalView] Steam Store API check error for {appId}: {ex.Message}");
            }

            // 2. Secondary check: SteamApiHelper
            try
            {
                var helperName = await SteamApiHelper.GetGameNameAsync(appId);
                if (!string.IsNullOrWhiteSpace(helperName) &&
                    !helperName.Equals($"App {appId}", StringComparison.OrdinalIgnoreCase) &&
                    !helperName.Equals($"AppID {appId}", StringComparison.OrdinalIgnoreCase))
                {
                    return (true, helperName);
                }
            }
            catch { }

            // 3. Tertiary check: Steam Store Web Page / App Title scraping
            try
            {
                var storeUrl = $"https://store.steampowered.com/app/{appId}";
                using var response = await _httpClient.GetAsync(storeUrl);
                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync();

                    var titleMatch = Regex.Match(html, "<meta[^>]*property=['\"]og:title['\"][^>]*content=['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
                    if (titleMatch.Success)
                    {
                        var title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
                        if (!title.Equals("Steam Store", StringComparison.OrdinalIgnoreCase) &&
                            !title.Equals("Welcome to Steam", StringComparison.OrdinalIgnoreCase))
                        {
                            var cleaned = CleanSteamPageTitle(title);
                            if (!string.IsNullOrWhiteSpace(cleaned))
                                return (true, cleaned);
                        }
                    }

                    titleMatch = Regex.Match(html, "<div[^>]*class=['\"]apphub_AppName['\"][^>]*>([^<]+)</div>", RegexOptions.IgnoreCase);
                    if (titleMatch.Success)
                    {
                        var title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
                        if (!string.IsNullOrWhiteSpace(title))
                            return (true, title);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[RequestGameModalView] Steam Store Web check error for {appId}: {ex.Message}");
            }

            // 4. Fallback: Steam CDN Header image check
            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg");
                using var imgRes = await _httpClient.SendAsync(headReq);
                if (imgRes.IsSuccessStatusCode)
                {
                    return (true, $"AppID {appId}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[RequestGameModalView] Steam CDN check error for {appId}: {ex.Message}");
            }

            return (false, string.Empty);
        }

        private async Task<string> GetSteamGameNameAsync(string steamUrl, int appId)
        {
            if (TryParseSteamAppNameFromUrl(steamUrl, out var parsedName) && !string.IsNullOrWhiteSpace(parsedName))
            {
                return parsedName;
            }

            try
            {
                var apiName = await SteamApiHelper.GetGameNameAsync(appId);
                if (!string.IsNullOrWhiteSpace(apiName) && !apiName.Equals($"App {appId}", StringComparison.OrdinalIgnoreCase))
                {
                    return apiName;
                }
            }
            catch { }

            try
            {
                var storeUrl = $"https://store.steampowered.com/app/{appId}";
                using var response = await _httpClient.GetAsync(storeUrl);
                if (!response.IsSuccessStatusCode)
                    return $"AppID {appId}";

                var html = await response.Content.ReadAsStringAsync();

                var titleMatch = Regex.Match(html, "<meta[^>]*property=['\"]og:title['\"][^>]*content=['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
                if (titleMatch.Success)
                {
                    var title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
                    if (!title.Equals("Steam Store", StringComparison.OrdinalIgnoreCase) &&
                        !title.Equals("Welcome to Steam", StringComparison.OrdinalIgnoreCase))
                    {
                        var cleaned = CleanSteamPageTitle(title);
                        if (!string.IsNullOrWhiteSpace(cleaned))
                            return cleaned;
                    }
                }

                titleMatch = Regex.Match(html, "<div[^>]*class=['\"]apphub_AppName['\"][^>]*>([^<]+)</div>", RegexOptions.IgnoreCase);
                if (titleMatch.Success)
                {
                    var title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
                    if (!string.IsNullOrWhiteSpace(title))
                        return title;
                }

                titleMatch = Regex.Match(html, @"<title>([^<]+?)</title>", RegexOptions.IgnoreCase);
                if (titleMatch.Success)
                {
                    var cleaned = CleanSteamPageTitle(System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim()));
                    if (!string.IsNullOrWhiteSpace(cleaned) &&
                        !cleaned.Equals("Steam Store", StringComparison.OrdinalIgnoreCase) &&
                        !cleaned.Equals("Welcome to Steam", StringComparison.OrdinalIgnoreCase))
                        return cleaned;
                }
            }
            catch { }

            return $"AppID {appId}";
        }

        private static string CleanSteamPageTitle(string pageTitle)
        {
            if (string.IsNullOrWhiteSpace(pageTitle))
                return pageTitle;

            var title = pageTitle.Trim();

            if (title.EndsWith(" on Steam", StringComparison.OrdinalIgnoreCase))
                title = title[..^" on Steam".Length].Trim();

            title = Regex.Replace(title, @"^Save\s+\d+%?\s+on\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
            title = Regex.Replace(title, @"^(.+?)\s+on\s+Steam$", "$1", RegexOptions.IgnoreCase).Trim();

            return title;
        }

        private static bool TryParseSteamAppNameFromUrl(string steamUrl, out string appName)
        {
            appName = string.Empty;
            if (!Uri.TryCreate(steamUrl, UriKind.Absolute, out var uri))
                return false;

            if (!uri.Host.Contains("store.steampowered.com", StringComparison.OrdinalIgnoreCase))
                return false;

            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length < 3 || !string.Equals(segments[0], "app", StringComparison.OrdinalIgnoreCase))
                return false;

            var rawName = segments[2];
            if (string.IsNullOrWhiteSpace(rawName))
                return false;

            appName = Uri.UnescapeDataString(rawName).Replace('_', ' ').Trim();
            return !string.IsNullOrWhiteSpace(appName);
        }

        private async Task<(bool success, string? messageId)> SendDiscordRequestAsync(string steamUrl, int appId, string webhookUrl)
        {
            try
            {
                var displayName = await DashboardView.GetCurrentDisplayNameAsync();
                var gameName = await GetSteamGameNameAsync(steamUrl, appId);
                var imageUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
                var payload = new
                {
                    content = "<@&958992653591117854>",
                    embeds = new[]
                    {
                        new
                        {
                            title = "🎮 Request Game",
                            description = $"**{gameName}**",
                            color = 0x1ABC9C,
                            image = new { url = imageUrl },
                            fields = new[]
                            {
                                new { name = "Requester", value = displayName, inline = true },
                                new { name = "AppID", value = appId.ToString(), inline = true },
                                new { name = "Steam URL", value = steamUrl, inline = false },
                                new { name = "Time", value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), inline = true }
                            },
                            footer = new { text = "HZ Lua Manager" }
                        }
                    }
                };

                var json = JsonSerializer.Serialize(payload);
                var webhookEndpoint = webhookUrl.Contains("?") ? $"{webhookUrl}&wait=true" : $"{webhookUrl}?wait=true";
                using var request = new HttpRequestMessage(HttpMethod.Post, webhookEndpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                using var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                    return (false, null);

                string responseContent = await response.Content.ReadAsStringAsync();
                string? messageId = null;
                if (!string.IsNullOrWhiteSpace(responseContent))
                {
                    try
                    {
                        var responseJson = JsonDocument.Parse(responseContent);
                        if (responseJson.RootElement.TryGetProperty("id", out var idProperty))
                        {
                            messageId = idProperty.GetString() ?? string.Empty;
                            Logger.Log($"[RequestGameModalView] ✓ Discord message sent successfully. MessageID: {messageId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[RequestGameModalView] Warning: Could not parse MessageID from response: {ex.Message}");
                    }
                }

                var saveSuccess = await SavePendingRequestToSupabaseAsync(appId, gameName, steamUrl, displayName, webhookUrl, messageId);
                if (!saveSuccess)
                {
                    Logger.Log($"[RequestGameModalView] Warning: Discord request sent but failed to save pending request for AppID {appId} to Supabase");
                }

                return (true, messageId);
            }
            catch (Exception ex)
            {
                Logger.Log($"[RequestGameModalView] SendDiscordRequestAsync failed: {ex.Message}");
                return (false, null);
            }
        }

        #endregion

        #region Supabase Storage for Requests

        private class ManifestRequestRecord
        {
            [JsonPropertyName("app_id")]
            public int AppId { get; set; }

            [JsonPropertyName("steam_url")]
            public string? SteamUrl { get; set; }

            [JsonPropertyName("requester")]
            public string? Requester { get; set; }

            [JsonPropertyName("discord_message_id")]
            public string? DiscordMessageId { get; set; }

            [JsonPropertyName("webhook_url")]
            public string? WebhookUrl { get; set; }

            [JsonPropertyName("requested_at")]
            public DateTime? RequestedAt { get; set; }
        }

        private async Task<bool> SavePendingRequestToSupabaseAsync(int appId, string appName, string steamUrl, string requester, string webhookUrl, string? messageId)
        {
            try
            {
                var payload = new
                {
                    app_id = appId,
                    app_name = appName,
                    steam_url = steamUrl,
                    requester = requester,
                    requester_id = GetRequesterUuid(GetDeviceId()).ToString(),
                    webhook_url = webhookUrl,
                    discord_message_id = messageId
                };

                string url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/hzmanifest_requests";
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Prefer", "return=representation");

                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    Logger.Log($"[RequestGameModalView] Failed to save pending request to Supabase: {response.StatusCode} - {responseBody}");
                    return false;
                }

                Logger.Log($"[RequestGameModalView] Saved pending request for AppID {appId} to Supabase");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[RequestGameModalView] Error saving pending request to Supabase: {ex.Message}");
                return false;
            }
        }

        private async Task<List<ManifestRequestRecord>> GetPendingRequestsFromSupabaseAsync()
        {
            try
            {
                string url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/hzmanifest_requests?select=app_id,steam_url,requester,webhook_url,discord_message_id,requested_at";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Accept", "application/json");

                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"[RequestGameModalView] Failed to fetch pending requests from Supabase: {response.StatusCode}");
                    return new List<ManifestRequestRecord>();
                }

                var content = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<List<ManifestRequestRecord>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return result ?? new List<ManifestRequestRecord>();
            }
            catch (Exception ex)
            {
                Logger.Log($"[RequestGameModalView] Error fetching pending requests from Supabase: {ex.Message}");
                return new List<ManifestRequestRecord>();
            }
        }

        #endregion

        #region Hardware ID & Device UUID Helpers

        private static string GetDeviceId()
        {
            try
            {
                return GetMotherboardHardwareId();
            }
            catch (Exception ex)
            {
                Logger.Log($"[RequestGameModalView] [GetDeviceId] Error: {ex.Message}");
                return "UNKNOWN";
            }
        }

        private static Guid GetRequesterUuid(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                deviceId = "UNKNOWN";

            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(deviceId));
            var guidBytes = new byte[16];
            Array.Copy(hash, guidBytes, 16);
            return new Guid(guidBytes);
        }

        private static string GetMotherboardHardwareId()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("Select SerialNumber from Win32_BaseBoard"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string? serialNumber = obj["SerialNumber"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(serialNumber) && IsValidSerialNumber(serialNumber))
                        {
                            return serialNumber;
                        }
                    }
                }

                var macAddress = NetworkInterface
                    .GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                                  !string.IsNullOrEmpty(nic.GetPhysicalAddress().ToString()))
                    .Select(nic => nic.GetPhysicalAddress().ToString())
                    .FirstOrDefault() ?? "UNKNOWN";

                return macAddress;
            }
            catch (Exception ex)
            {
                Logger.Log($"[RequestGameModalView] [GetMotherboardHardwareId] Error: {ex.Message}");
                return "UNKNOWN";
            }
        }

        private static bool IsValidSerialNumber(string serialNumber)
        {
            string lower = serialNumber.ToLower().Trim();

            var invalidValues = new[]
            {
                "0000000", "00000000", "000000000000",
                "default string", "default", "system default",
                "not available", "not specified", "unknown",
                "system reserved", "pending",
                "to be filled by o.e.m.", "empty", "none", "n/a"
            };

            if (invalidValues.Contains(lower))
                return false;

            if (string.IsNullOrWhiteSpace(serialNumber.Replace("0", "").Replace("-", "").Replace(" ", "")))
                return false;

            return true;
        }

        #endregion
    }
}
