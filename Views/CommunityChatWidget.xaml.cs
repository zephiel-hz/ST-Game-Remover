using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SteamPluginManager.Models;
using SteamPluginManager.Services.API;

namespace SteamPluginManager.Views
{
    public partial class CommunityChatWidget : UserControl
    {
        public static CommunityChatWidget? CurrentInstance { get; private set; }

        public class MentionCandidate
        {
            public string SenderName { get; set; } = string.Empty;
            public string DisplayNameWithAt => $"@{SenderName}";
            public string AvatarInitial => !string.IsNullOrWhiteSpace(SenderName) ? SenderName.Trim().Substring(0, 1).ToUpper() : "?";
            public int Level { get; set; } = 2;

            public bool IsAdmin => Level == 0;
            public bool IsSupporter => Level == 1;
            public bool IsModerator => Level == 3;
            public bool IsMember => Level == 2;
            public bool HasBadge => Level == 0 || Level == 1 || Level == 3;
            public string BadgeText => Level switch
            {
                0 => "ADMIN",
                1 => "SUPPORTER",
                3 => "MODERATOR",
                2 => "MEMBER",
                _ => "MEMBER"
            };
        }

        #region FormattedMessage Attached Property
        public static readonly DependencyProperty FormattedMessageProperty =
            DependencyProperty.RegisterAttached(
                "FormattedMessage",
                typeof(ChatMessage),
                typeof(CommunityChatWidget),
                new PropertyMetadata(null, OnFormattedMessageChanged));

        public static ChatMessage GetFormattedMessage(DependencyObject obj) => (ChatMessage)obj.GetValue(FormattedMessageProperty);
        public static void SetFormattedMessage(DependencyObject obj, ChatMessage value) => obj.SetValue(FormattedMessageProperty, value);

        private static readonly Regex _mentionOrUrlRegex = new(
            @"(@[a-zA-Z0-9_.-]+|https?://[^\s]+|www\.[^\s]+)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Sisipkan zero-width space (\u200B) pada URL atau token panjang agar line wrapping WPF tidak meluap ke samping.
        /// </summary>
        private static string InsertSoftBreaks(string text, int maxTokenLength = 25)
        {
            if (string.IsNullOrEmpty(text) || text.Length < maxTokenLength) return text;
            var sb = new StringBuilder(text.Length * 2);
            int runLength = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                sb.Append(c);
                runLength++;
                if (c == '/' || c == '?' || c == '&' || c == '=' || c == '-' || c == '_' || c == '.')
                {
                    sb.Append('\u200B');
                    runLength = 0;
                }
                else if (runLength >= maxTokenLength && !char.IsWhiteSpace(c))
                {
                    sb.Append('\u200B');
                    runLength = 0;
                }
                else if (char.IsWhiteSpace(c))
                {
                    runLength = 0;
                }
            }
            return sb.ToString();
        }

        private static void OnFormattedMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not ChatMessage chatMsg) return;

            if (d is TextBlock textBlock)
            {
                textBlock.Inlines.Clear();
                if (!string.IsNullOrEmpty(chatMsg.Message))
                {
                    RenderFormattedInlines(textBlock, chatMsg);
                }
            }
        }

        private static void RenderFormattedInlines(TextBlock tb, ChatMessage msg)
        {
            string text = msg.Message ?? string.Empty;
            bool isOwn = msg.IsOwnMessage;

            Brush textBrush = isOwn 
                ? Brushes.White 
                : (Application.Current?.TryFindResource("ForegroundBrush") as Brush ?? Brushes.White);

            Brush mentionBrush = isOwn
                ? new SolidColorBrush(Color.FromRgb(224, 242, 254))
                : (Application.Current?.TryFindResource("AccentBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(56, 189, 248)));

            Brush urlBrush = isOwn
                ? new SolidColorBrush(Color.FromRgb(186, 230, 253))
                : new SolidColorBrush(Color.FromRgb(56, 189, 248));

            var parts = _mentionOrUrlRegex.Split(text);

            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;

                if (part.StartsWith("@") && part.Length > 1)
                {
                    string username = part.Substring(1);
                    var hyperlink = new Hyperlink(new Run(part))
                    {
                        TextDecorations = null,
                        FontWeight = FontWeights.Bold,
                        Foreground = mentionBrush,
                        Cursor = Cursors.Hand,
                        ToolTip = $"View @{username}'s profile"
                    };

                    hyperlink.Click += (s, e) =>
                    {
                        CurrentInstance?.ShowMiniProfileCard(username);
                    };

                    tb.Inlines.Add(hyperlink);
                }
                else if (part.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         part.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                         part.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    string cleanUrl = part;
                    string trailingPunctuation = string.Empty;
                    while (cleanUrl.Length > 0 && ".,;:!?)'\"".IndexOf(cleanUrl[^1]) >= 0)
                    {
                        trailingPunctuation = cleanUrl[^1] + trailingPunctuation;
                        cleanUrl = cleanUrl.Substring(0, cleanUrl.Length - 1);
                    }

                    string navUrl = cleanUrl;
                    if (navUrl.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                    {
                        navUrl = "https://" + navUrl;
                    }

                    string displayUrl = InsertSoftBreaks(cleanUrl);

                    var hyperlink = new Hyperlink(new Run(displayUrl))
                    {
                        TextDecorations = TextDecorations.Underline,
                        FontWeight = FontWeights.Normal,
                        Foreground = urlBrush,
                        Cursor = Cursors.Hand,
                        ToolTip = $"Open link: {navUrl}"
                    };

                    hyperlink.Click += (s, e) =>
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(navUrl) { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[CommunityChatWidget] Failed to open URL '{navUrl}': {ex.Message}");
                        }
                    };

                    tb.Inlines.Add(hyperlink);

                    if (!string.IsNullOrEmpty(trailingPunctuation))
                    {
                        tb.Inlines.Add(new Run(trailingPunctuation) { Foreground = textBrush });
                    }
                }
                else
                {
                    string displayText = InsertSoftBreaks(part);
                    tb.Inlines.Add(new Run(displayText) { Foreground = textBrush });
                }
            }
        }
        #endregion

        private readonly ObservableCollection<ChatMessage> _messages = new();
        private readonly ObservableCollection<UserProfile> _onlineUsersList = new();
        private readonly ObservableCollection<UserProfile> _offlineUsersList = new();
        private readonly DispatcherTimer _pollTimer;
        private readonly DispatcherTimer _mentionDebounceTimer;
        private readonly DispatcherTimer _presenceTimer;
        private UserProfile? _currentProfile;
        private string _currentDeviceId = string.Empty;
        private ChatMessage? _activeReplyMessage;
        private BroadcastAnnouncement? _activeAnnouncement;
        private Storyboard? _marqueeStoryboard;
        private bool _isSending;
        private bool _isLoading;
        private bool _isUpdatingMentionList;
        private string _pendingMentionQuery = string.Empty;

        public bool IsExpanded => ExpandedDrawer.Visibility == Visibility.Visible;

        public CommunityChatWidget()
        {
            InitializeComponent();
            CurrentInstance = this;
            MessagesItemsControl.ItemsSource = _messages;
            OnlineUsersItemsControl.ItemsSource = _onlineUsersList;
            OfflineUsersItemsControl.ItemsSource = _offlineUsersList;

            // Subscribe to local/realtime avatar updates
            Services.Profile.ProfilePictureCacheService.OnAvatarUpdated += (userId, img) =>
            {
                Dispatcher.Invoke(() =>
                {
                    foreach (var msg in _messages)
                    {
                        if (msg.UserId == userId)
                        {
                            msg.AvatarImage = img;
                        }
                    }
                    foreach (var u in _onlineUsersList)
                    {
                        if (u.Id == userId)
                        {
                            u.AvatarImage = img;
                        }
                    }
                    foreach (var u in _offlineUsersList)
                    {
                        if (u.Id == userId)
                        {
                            u.AvatarImage = img;
                        }
                    }
                    if (MiniProfileCardOverlay.Visibility == Visibility.Visible)
                    {
                        if (img != null)
                        {
                            MiniCardAvatarImage.Source = img;
                            MiniCardAvatarImage.Visibility = Visibility.Visible;
                            MiniCardAvatarInitial.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            MiniCardAvatarImage.Source = null;
                            MiniCardAvatarImage.Visibility = Visibility.Collapsed;
                            MiniCardAvatarInitial.Visibility = Visibility.Visible;
                        }
                    }
                });
            };

            // Setup polling timer (every 5 seconds when expanded as fallback)
            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _pollTimer.Tick += async (s, e) =>
            {
                if (IsExpanded && !_isSending && !_isLoading)
                {
                    await RefreshMessagesSilentlyAsync();
                }
            };

            // Setup debounce timer for searching users in mention popup
            _mentionDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _mentionDebounceTimer.Tick += async (s, e) =>
            {
                _mentionDebounceTimer.Stop();
                await ExecuteMentionSearchAsync(_pendingMentionQuery);
            };

            // Setup presence refresh timer (every 4 seconds for fast 10s offline detection)
            _presenceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(4)
            };
            _presenceTimer.Tick += async (s, e) =>
            {
                await RefreshOnlinePresenceAsync();
            };

            Loaded += CommunityChatWidget_Loaded;
            Unloaded += CommunityChatWidget_Unloaded;
        }

        private async void CommunityChatWidget_Loaded(object sender, RoutedEventArgs e)
        {
            CurrentInstance = this;
            try
            {
                bool hasActiveToken = await DashboardView.CheckDeviceTokenAsync();
                if (!hasActiveToken)
                {
                    Visibility = Visibility.Collapsed;
                    Collapse();
                    return;
                }

                Visibility = Visibility.Visible;
                Collapse();
                await CheckUserProfileStatusAsync();

                // Setup realtime listeners
                CommunityChatRealtimeClient.OnMessageReceived -= HandleIncomingRealtimeMessage;
                CommunityChatRealtimeClient.OnMessageReceived += HandleIncomingRealtimeMessage;

                CommunityChatRealtimeClient.OnMessageDeleted -= HandleRealtimeMessageDeleted;
                CommunityChatRealtimeClient.OnMessageDeleted += HandleRealtimeMessageDeleted;

                CommunityChatRealtimeClient.OnUserProfileUpdated -= HandleRealtimeUserProfileUpdated;
                CommunityChatRealtimeClient.OnUserProfileUpdated += HandleRealtimeUserProfileUpdated;

                CommunityChatRealtimeClient.OnOnlineUsersChanged -= HandleOnlineUsersChanged;
                CommunityChatRealtimeClient.OnOnlineUsersChanged += HandleOnlineUsersChanged;

                CommunityChatRealtimeClient.OnUserOffline -= HandleUserOffline;
                CommunityChatRealtimeClient.OnUserOffline += HandleUserOffline;

                CommunityChatRealtimeClient.Start();
                _presenceTimer.Start();

                await RefreshOnlinePresenceAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatWidget] Loaded error: {ex.Message}");
            }
        }

        private void CommunityChatWidget_Unloaded(object sender, RoutedEventArgs e)
        {
            _pollTimer.Stop();
            _mentionDebounceTimer.Stop();
            _presenceTimer.Stop();

            CommunityChatRealtimeClient.OnMessageReceived -= HandleIncomingRealtimeMessage;
            CommunityChatRealtimeClient.OnMessageDeleted -= HandleRealtimeMessageDeleted;
            CommunityChatRealtimeClient.OnUserProfileUpdated -= HandleRealtimeUserProfileUpdated;
            CommunityChatRealtimeClient.OnOnlineUsersChanged -= HandleOnlineUsersChanged;
            CommunityChatRealtimeClient.OnUserOffline -= HandleUserOffline;
            CommunityChatRealtimeClient.Stop();

            if (CurrentInstance == this)
            {
                CurrentInstance = null;
            }
        }

        private void HandleIncomingRealtimeMessage(ChatMessage msg)
        {
            Dispatcher.Invoke(() =>
            {
                if (msg == null || msg.Id <= 0) return;

                // If message is a system broadcast, update the pinned announcement banner instead of adding to chat list
                if (string.Equals(msg.DeviceId, "SYSTEM_BROADCAST", StringComparison.OrdinalIgnoreCase))
                {
                    _ = RefreshBroadcastAnnouncementAsync();
                    return;
                }

                if (_messages.Any(m => m.Id == msg.Id)) return;

                // Match sender with existing online/offline users or existing messages to resolve UserId, AvatarUrl, and AvatarImage
                var userMatch = _onlineUsersList.Concat(_offlineUsersList).FirstOrDefault(u =>
                    (!string.IsNullOrWhiteSpace(msg.DeviceId) && !string.IsNullOrWhiteSpace(u.DeviceId) && string.Equals(u.DeviceId, msg.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(msg.SenderName) && !string.IsNullOrWhiteSpace(u.DisplayName) && string.Equals(u.DisplayName, msg.SenderName, StringComparison.OrdinalIgnoreCase)));

                var existingMatch = _messages.FirstOrDefault(m =>
                    (!string.IsNullOrWhiteSpace(msg.DeviceId) && !string.IsNullOrWhiteSpace(m.DeviceId) && string.Equals(m.DeviceId, msg.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(msg.SenderName) && !string.IsNullOrWhiteSpace(m.SenderName) && string.Equals(m.SenderName, msg.SenderName, StringComparison.OrdinalIgnoreCase)));

                if (userMatch != null)
                {
                    if (userMatch.Id.HasValue) msg.UserId = userMatch.Id;
                    msg.AvatarUrl = userMatch.AvatarUrl;
                    msg.AvatarImage = userMatch.AvatarImage;
                    msg.Level = userMatch.Level;
                }
                else if (existingMatch != null)
                {
                    if (existingMatch.UserId.HasValue) msg.UserId = existingMatch.UserId;
                    msg.AvatarUrl = existingMatch.AvatarUrl;
                    msg.AvatarImage = existingMatch.AvatarImage;
                    msg.Level = existingMatch.Level;
                }

                if (msg.UserId.HasValue && !string.IsNullOrWhiteSpace(msg.AvatarUrl) && msg.AvatarImage == null)
                {
                    _ = msg.LoadAvatarImageAsync();
                }

                // Sync all older messages with newest sender name & badge for this device
                if (!string.IsNullOrWhiteSpace(msg.DeviceId))
                {
                    foreach (var m in _messages)
                    {
                        if (string.Equals(m.DeviceId, msg.DeviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(msg.SenderName)) m.SenderName = msg.SenderName;
                            m.Level = msg.Level;
                        }
                    }
                }

                msg.IsOwnMessage = !string.IsNullOrWhiteSpace(_currentDeviceId) &&
                                   string.Equals(msg.DeviceId, _currentDeviceId, StringComparison.OrdinalIgnoreCase);

                msg.IsOnline = CommunityChatRealtimeClient.IsUserOnline(msg.SenderName);

                _messages.Add(msg);
                while (_messages.Count > 50)
                {
                    _messages.RemoveAt(0);
                }

                EmptyChatText.Visibility = Visibility.Collapsed;
                ScrollToBottom();
            });
        }

        private void HandleRealtimeUserProfileUpdated(UserProfile updatedProfile)
        {
            Dispatcher.Invoke(async () =>
            {
                if (updatedProfile == null || (!updatedProfile.Id.HasValue && string.IsNullOrWhiteSpace(updatedProfile.DeviceId))) return;

                // 1. Fetch updated avatar if any
                System.Windows.Media.ImageSource? updatedAvatarImg = null;
                if (updatedProfile.Id.HasValue)
                {
                    Services.Profile.ProfilePictureCacheService.InvalidateCache(updatedProfile.Id.Value);
                    if (!string.IsNullOrWhiteSpace(updatedProfile.AvatarUrl))
                    {
                        updatedAvatarImg = await Services.Profile.ProfilePictureCacheService.GetAvatarAsync(updatedProfile.Id.Value, updatedProfile.AvatarUrl, forceRefresh: true);
                    }
                    Services.Profile.ProfilePictureCacheService.TriggerAvatarUpdated(updatedProfile.Id.Value, updatedAvatarImg);
                }

                // 2. Update sender name, level, and avatar across all chat messages from this user in realtime
                foreach (var msg in _messages)
                {
                    bool isMatch = (msg.UserId.HasValue && updatedProfile.Id.HasValue && msg.UserId.Value == updatedProfile.Id.Value) ||
                                   (!string.IsNullOrWhiteSpace(msg.DeviceId) && !string.IsNullOrWhiteSpace(updatedProfile.DeviceId) && string.Equals(msg.DeviceId, updatedProfile.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
                                   (!string.IsNullOrWhiteSpace(msg.SenderName) && !string.IsNullOrWhiteSpace(updatedProfile.DisplayName) && string.Equals(msg.SenderName, updatedProfile.DisplayName, StringComparison.OrdinalIgnoreCase));

                    if (isMatch)
                    {
                        if (!string.IsNullOrWhiteSpace(updatedProfile.DisplayName))
                        {
                            msg.SenderName = updatedProfile.DisplayName;
                        }
                        msg.Level = updatedProfile.Level;
                        if (updatedProfile.Id.HasValue)
                        {
                            msg.UserId = updatedProfile.Id;
                        }
                        msg.AvatarUrl = updatedProfile.AvatarUrl;
                        msg.AvatarImage = updatedAvatarImg;
                    }
                }

                // 3. If current user's profile was updated, refresh header and input status
                if (_currentProfile != null &&
                    ((!string.IsNullOrWhiteSpace(_currentProfile.DeviceId) && !string.IsNullOrWhiteSpace(updatedProfile.DeviceId) && string.Equals(_currentProfile.DeviceId, updatedProfile.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
                     (_currentProfile.Id.HasValue && updatedProfile.Id.HasValue && _currentProfile.Id.Value == updatedProfile.Id.Value)))
                {
                    _currentProfile.DisplayName = updatedProfile.DisplayName;
                    _currentProfile.Level = updatedProfile.Level;
                    _currentProfile.Bio = updatedProfile.Bio;
                    _currentProfile.AvatarUrl = updatedProfile.AvatarUrl;
                    _currentProfile.AvatarImage = updatedAvatarImg;
                    await CheckUserProfileStatusAsync();
                }

                // 4. Update Mini Profile Card if open for this user
                if (MiniProfileCardOverlay.Visibility == Visibility.Visible &&
                    (string.Equals(MiniCardDisplayName.Text, updatedProfile.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                     _messages.Any(m => (m.UserId.HasValue && updatedProfile.Id.HasValue && m.UserId.Value == updatedProfile.Id.Value) ||
                                        (!string.IsNullOrWhiteSpace(m.DeviceId) && !string.IsNullOrWhiteSpace(updatedProfile.DeviceId) && string.Equals(m.DeviceId, updatedProfile.DeviceId, StringComparison.OrdinalIgnoreCase) && string.Equals(m.SenderName, MiniCardDisplayName.Text, StringComparison.OrdinalIgnoreCase)))))
                {
                    MiniCardDisplayName.Text = updatedProfile.DisplayName;
                    MiniCardAvatarInitial.Text = updatedProfile.AvatarInitial;
                    MiniCardBioText.Text = !string.IsNullOrWhiteSpace(updatedProfile.Bio) ? updatedProfile.Bio : "No bio description.";
                    SetMiniCardBadge(updatedProfile.Level, CommunityChatRealtimeClient.IsUserOnline(updatedProfile.DisplayName));

                    if (updatedAvatarImg != null)
                    {
                        MiniCardAvatarImage.Source = updatedAvatarImg;
                        MiniCardAvatarImage.Visibility = Visibility.Visible;
                        MiniCardAvatarInitial.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        MiniCardAvatarImage.Source = null;
                        MiniCardAvatarImage.Visibility = Visibility.Collapsed;
                        MiniCardAvatarInitial.Visibility = Visibility.Visible;
                    }
                }

                // 5. Update online / offline directory lists
                bool found = false;
                foreach (var u in _onlineUsersList)
                {
                    if ((u.Id.HasValue && updatedProfile.Id.HasValue && u.Id.Value == updatedProfile.Id.Value) ||
                        (!string.IsNullOrWhiteSpace(u.DeviceId) && !string.IsNullOrWhiteSpace(updatedProfile.DeviceId) && string.Equals(u.DeviceId, updatedProfile.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(u.DisplayName) && !string.IsNullOrWhiteSpace(updatedProfile.DisplayName) && string.Equals(u.DisplayName, updatedProfile.DisplayName, StringComparison.OrdinalIgnoreCase)))
                    {
                        u.DisplayName = updatedProfile.DisplayName;
                        u.Level = updatedProfile.Level;
                        u.Bio = updatedProfile.Bio;
                        if (updatedProfile.Id.HasValue) u.Id = updatedProfile.Id;
                        u.AvatarUrl = updatedProfile.AvatarUrl;
                        u.AvatarImage = updatedAvatarImg;
                        if (updatedProfile.LastSeen.HasValue) u.LastSeen = updatedProfile.LastSeen;
                        found = true;
                        break;
                    }
                }

                foreach (var u in _offlineUsersList)
                {
                    if ((u.Id.HasValue && updatedProfile.Id.HasValue && u.Id.Value == updatedProfile.Id.Value) ||
                        (!string.IsNullOrWhiteSpace(u.DeviceId) && !string.IsNullOrWhiteSpace(updatedProfile.DeviceId) && string.Equals(u.DeviceId, updatedProfile.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(u.DisplayName) && !string.IsNullOrWhiteSpace(updatedProfile.DisplayName) && string.Equals(u.DisplayName, updatedProfile.DisplayName, StringComparison.OrdinalIgnoreCase)))
                    {
                        u.DisplayName = updatedProfile.DisplayName;
                        u.Level = updatedProfile.Level;
                        u.Bio = updatedProfile.Bio;
                        if (updatedProfile.Id.HasValue) u.Id = updatedProfile.Id;
                        u.AvatarUrl = updatedProfile.AvatarUrl;
                        u.AvatarImage = updatedAvatarImg;
                        if (updatedProfile.LastSeen.HasValue) u.LastSeen = updatedProfile.LastSeen;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    await RefreshOnlinePresenceAsync();
                }
                else
                {
                    ReorderDirectoryLists();
                }
            });
        }

        private void HandleUserOffline(string? username, string? deviceId)
        {
            Dispatcher.Invoke(() =>
            {
                // 1. Mark offline on existing messages
                foreach (var m in _messages)
                {
                    if ((!string.IsNullOrWhiteSpace(deviceId) && string.Equals(m.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(username) && string.Equals(m.SenderName, username, StringComparison.OrdinalIgnoreCase)))
                    {
                        m.IsOnline = false;
                    }
                }

                // 2. Move user from online list to offline list
                var user = _onlineUsersList.FirstOrDefault(u =>
                    (!string.IsNullOrWhiteSpace(deviceId) && string.Equals(u.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(username) && string.Equals(u.DisplayName, username, StringComparison.OrdinalIgnoreCase)));

                if (user != null)
                {
                    user.IsOnline = false;
                    user.LastSeen = DateTime.UtcNow;
                    _onlineUsersList.Remove(user);
                    if (!_offlineUsersList.Any(o => (o.Id.HasValue && user.Id.HasValue && o.Id.Value == user.Id.Value) ||
                                                    (!string.IsNullOrWhiteSpace(o.DeviceId) && string.Equals(o.DeviceId, user.DeviceId, StringComparison.OrdinalIgnoreCase))))
                    {
                        _offlineUsersList.Insert(0, user);
                    }
                    OnlineSectionTitle.Text = $"ONLINE - {_onlineUsersList.Count}";
                    OnlineSectionHeader.Visibility = _onlineUsersList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    UpdateOnlineCountDisplay(_onlineUsersList.Count);
                }
            });
        }

        private void ReorderDirectoryLists()
        {
            var sortedOnline = _onlineUsersList
                .OrderBy(u => u.RoleRank)
                .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sortedOffline = _offlineUsersList
                .OrderBy(u => u.RoleRank)
                .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _onlineUsersList.Clear();
            foreach (var u in sortedOnline) _onlineUsersList.Add(u);

            _offlineUsersList.Clear();
            foreach (var u in sortedOffline) _offlineUsersList.Add(u);

            OnlineSectionTitle.Text = $"ONLINE — {_onlineUsersList.Count}";
            OfflineSectionTitle.Text = $"OFFLINE — {_offlineUsersList.Count}";
            OnlineSectionHeader.Visibility = _onlineUsersList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            OfflineSectionHeader.Visibility = _offlineUsersList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateOnlineCountDisplay(_onlineUsersList.Count);
        }

        private void HandleRealtimeMessageDeleted(long deletedId)
        {
            Dispatcher.Invoke(() =>
            {
                if (_activeAnnouncement != null && _activeAnnouncement.Id == deletedId)
                {
                    _activeAnnouncement = null;
                    _marqueeStoryboard?.Stop();
                    PinnedAnnouncementBanner.Visibility = Visibility.Collapsed;
                }

                var existing = _messages.FirstOrDefault(m => m.Id == deletedId);
                if (existing != null)
                {
                    _messages.Remove(existing);
                    EmptyChatText.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            });
        }

        private async Task RefreshBroadcastAnnouncementAsync()
        {
            try
            {
                var announcement = await CommunityChatService.GetActiveBroadcastAsync();
                Dispatcher.Invoke(() =>
                {
                    if (announcement != null)
                    {
                        bool isNew = _activeAnnouncement == null || _activeAnnouncement.Id != announcement.Id || _activeAnnouncement.Message != announcement.Message;
                        _activeAnnouncement = announcement;

                        string cleanMessage = announcement.Message.Replace("\r\n", " ").Replace("\n", " ");
                        string combinedText = string.IsNullOrWhiteSpace(announcement.Title)
                            ? cleanMessage
                            : $"{announcement.Title}  •  {cleanMessage}";

                        PinnedMarqueeTextBlock.Text = combinedText;
                        PinnedAnnouncementBanner.ToolTip = string.IsNullOrWhiteSpace(announcement.Title)
                            ? announcement.Message
                            : $"📢 {announcement.Title}\n{announcement.Message}";
                        PinnedAnnouncementBanner.Visibility = Visibility.Visible;

                        if (isNew)
                        {
                            Dispatcher.InvokeAsync(StartMarqueeAnimation, DispatcherPriority.Loaded);
                        }
                    }
                    else
                    {
                        _activeAnnouncement = null;
                        _marqueeStoryboard?.Stop();
                        PinnedAnnouncementBanner.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatWidget] RefreshBroadcastAnnouncementAsync error: {ex.Message}");
            }
        }

        private void StartMarqueeAnimation()
        {
            try
            {
                if (PinnedAnnouncementBanner.Visibility != Visibility.Visible || _activeAnnouncement == null)
                    return;

                PinnedMarqueeTextBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double textWidth = PinnedMarqueeTextBlock.DesiredSize.Width;
                double canvasWidth = MarqueeCanvas.ActualWidth;

                if (canvasWidth <= 0) canvasWidth = 230;

                // Duration based on text length + canvas width so speed is smooth and readable (~55 px/sec)
                double totalDistance = canvasWidth + textWidth + 30;
                double durationSec = Math.Max(7, totalDistance / 55.0);

                var animation = new DoubleAnimation
                {
                    From = canvasWidth,
                    To = -(textWidth + 20),
                    Duration = new Duration(TimeSpan.FromSeconds(durationSec)),
                    RepeatBehavior = RepeatBehavior.Forever
                };

                _marqueeStoryboard?.Stop();
                _marqueeStoryboard = new Storyboard();
                Storyboard.SetTarget(animation, PinnedMarqueeTextBlock);
                Storyboard.SetTargetProperty(animation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));
                _marqueeStoryboard.Children.Add(animation);
                _marqueeStoryboard.Begin();
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatWidget] StartMarqueeAnimation error: {ex.Message}");
            }
        }

        private void MarqueeCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (PinnedAnnouncementBanner.Visibility == Visibility.Visible && _activeAnnouncement != null)
            {
                StartMarqueeAnimation();
            }
        }

        private void PinnedAnnouncementBanner_MouseEnter(object sender, MouseEventArgs e)
        {
            try
            {
                _marqueeStoryboard?.Pause();
            }
            catch { }
        }

        private void PinnedAnnouncementBanner_MouseLeave(object sender, MouseEventArgs e)
        {
            try
            {
                _marqueeStoryboard?.Resume();
            }
            catch { }
        }

        private void HandleOnlineUsersChanged(HashSet<string> onlineUsers)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateOnlineStatusOnMessages(onlineUsers);
                UpdateOnlineCountDisplay(onlineUsers.Count);
            });
        }

        public async void Expand()
        {
            CollapsedPill.Visibility = Visibility.Collapsed;
            ExpandedDrawer.Visibility = Visibility.Visible;

            await CheckUserProfileStatusAsync();
            await RefreshMessagesAsync();
            await RefreshOnlinePresenceAsync();

            _pollTimer.Start();

            // Focus chat input if user has display name
            if (ActiveInputPanel.Visibility == Visibility.Visible)
            {
                ChatInputTextBox.Focus();
            }
        }

        public void Collapse()
        {
            _pollTimer.Stop();
            _mentionDebounceTimer.Stop();
            ExpandedDrawer.Visibility = Visibility.Collapsed;
            CollapsedPill.Visibility = Visibility.Visible;
            MentionSuggestionPopup.IsOpen = false;
            MiniProfileCardOverlay.Visibility = Visibility.Collapsed;
        }

        public void Toggle()
        {
            if (IsExpanded)
            {
                Collapse();
            }
            else
            {
                Expand();
            }
        }

        public async Task CheckUserProfileStatusAsync()
        {
            try
            {
                _currentProfile = await DashboardView.LoadUserProfileForEditingAsync();
                _currentDeviceId = _currentProfile?.DeviceId ?? string.Empty;
                bool hasName = !string.IsNullOrWhiteSpace(_currentProfile?.DisplayName);

                if (hasName)
                {
                    NoNameBanner.Visibility = Visibility.Collapsed;
                    ActiveInputPanel.Visibility = Visibility.Visible;
                    CommunityChatRealtimeClient.RegisterOnlineUser(_currentProfile!.DisplayName);
                }
                else
                {
                    NoNameBanner.Visibility = Visibility.Visible;
                    ActiveInputPanel.Visibility = Visibility.Collapsed;
                }

                // Update IsOwnMessage flags on existing messages
                foreach (var msg in _messages)
                {
                    msg.IsOwnMessage = !string.IsNullOrWhiteSpace(_currentDeviceId) &&
                                       string.Equals(msg.DeviceId, _currentDeviceId, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatWidget] CheckUserProfileStatusAsync error: {ex.Message}");
            }
        }

        private void StartLoadingAnimation()
        {
            try
            {
                var animation = new DoubleAnimation
                {
                    From = 0,
                    To = 360,
                    Duration = new Duration(TimeSpan.FromSeconds(0.9)),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                ChatSpinnerRotate?.BeginAnimation(RotateTransform.AngleProperty, animation);
                RefreshIconRotate?.BeginAnimation(RotateTransform.AngleProperty, animation);
            }
            catch { }
        }

        private void StopLoadingAnimation()
        {
            try
            {
                ChatSpinnerRotate?.BeginAnimation(RotateTransform.AngleProperty, null);
                RefreshIconRotate?.BeginAnimation(RotateTransform.AngleProperty, null);
            }
            catch { }
        }

        public async Task RefreshMessagesAsync()
        {
            if (_isLoading) return;
            _isLoading = true;

            try
            {
                if (_messages.Count == 0)
                {
                    ChatLoadingPanel.Visibility = Visibility.Visible;
                    EmptyChatText.Visibility = Visibility.Collapsed;
                }
                StartLoadingAnimation();

                _ = RefreshBroadcastAnnouncementAsync();

                var fetched = await CommunityChatService.GetRecentMessagesAsync();
                _messages.Clear();
                foreach (var msg in fetched)
                {
                    msg.IsOwnMessage = !string.IsNullOrWhiteSpace(_currentDeviceId) &&
                                       string.Equals(msg.DeviceId, _currentDeviceId, StringComparison.OrdinalIgnoreCase);
                    msg.IsOnline = CommunityChatRealtimeClient.IsUserOnline(msg.SenderName);
                    _messages.Add(msg);
                }

                EmptyChatText.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatWidget] RefreshMessagesAsync error: {ex.Message}");
            }
            finally
            {
                ChatLoadingPanel.Visibility = Visibility.Collapsed;
                StopLoadingAnimation();
                _isLoading = false;
            }
        }

        private async Task RefreshMessagesSilentlyAsync()
        {
            try
            {
                _ = RefreshBroadcastAnnouncementAsync();

                var fetched = await CommunityChatService.GetRecentMessagesAsync();
                if (fetched == null || fetched.Count == 0) return;

                long lastCurrentId = _messages.LastOrDefault()?.Id ?? 0;
                long lastFetchedId = fetched.LastOrDefault()?.Id ?? 0;

                if (lastCurrentId != lastFetchedId || _messages.Count != fetched.Count)
                {
                    _messages.Clear();
                    foreach (var msg in fetched)
                    {
                        msg.IsOwnMessage = !string.IsNullOrWhiteSpace(_currentDeviceId) &&
                                           string.Equals(msg.DeviceId, _currentDeviceId, StringComparison.OrdinalIgnoreCase);
                        msg.IsOnline = CommunityChatRealtimeClient.IsUserOnline(msg.SenderName);
                        _messages.Add(msg);
                    }
                    EmptyChatText.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    ScrollToBottom();
                }
                else
                {
                    // Sync individual item properties (SenderName, Level, IsOnline) in case profile changed without new messages
                    for (int i = 0; i < _messages.Count && i < fetched.Count; i++)
                    {
                        if (_messages[i].Id == fetched[i].Id)
                        {
                            if (_messages[i].SenderName != fetched[i].SenderName)
                                _messages[i].SenderName = fetched[i].SenderName;
                            if (_messages[i].Level != fetched[i].Level)
                                _messages[i].Level = fetched[i].Level;
                            _messages[i].IsOnline = CommunityChatRealtimeClient.IsUserOnline(_messages[i].SenderName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatWidget] RefreshMessagesSilentlyAsync error: {ex.Message}");
            }
        }

        private void ScrollToBottom()
        {
            Dispatcher.InvokeAsync(() =>
            {
                MessagesScrollViewer.ScrollToEnd();
            }, DispatcherPriority.Background);
        }

        private async void SendCurrentMessage()
        {
            if (_isSending) return;

            string text = ChatInputTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return;

            if (_currentProfile == null || string.IsNullOrWhiteSpace(_currentProfile.DisplayName))
            {
                await CheckUserProfileStatusAsync();
                if (string.IsNullOrWhiteSpace(_currentProfile?.DisplayName))
                {
                    ModernMessageBox.Show("Please set your profile display name before sending messages.", "Attention", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _isSending = true;
            SendChatBtn.IsEnabled = false;

            try
            {
                long? replyId = _activeReplyMessage?.Id;
                string? replySender = _activeReplyMessage?.SenderName;
                string? replyText = _activeReplyMessage?.Message;

                var (success, errorMsg, sentMessage) = await CommunityChatService.SendMessageAsync(
                    _currentProfile.DeviceId,
                    _currentProfile.DisplayName,
                    text,
                    _currentProfile.Level,
                    replyId,
                    replySender,
                    replyText
                );

                if (success)
                {
                    ChatInputTextBox.Text = string.Empty;
                    ClearReplyTarget();

                    CommunityChatRealtimeClient.RegisterOnlineUser(_currentProfile.DisplayName);

                    if (sentMessage != null)
                    {
                        sentMessage.IsOwnMessage = true;
                        sentMessage.IsOnline = true;
                        _messages.Add(sentMessage);
                        while (_messages.Count > 50)
                        {
                            _messages.RemoveAt(0);
                        }
                        EmptyChatText.Visibility = Visibility.Collapsed;
                        ScrollToBottom();
                    }
                    else
                    {
                        await RefreshMessagesAsync();
                    }
                }
                else
                {
                    ModernMessageBox.Show(errorMsg ?? "Failed to send message.", "Chat Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatWidget] SendCurrentMessage error: {ex.Message}");
            }
            finally
            {
                _isSending = false;
                SendChatBtn.IsEnabled = true;
                ChatInputTextBox.Focus();
            }
        }

        // ========================================================
        // Delete Message Feature
        // ========================================================
        private async void DeleteCurrentMessage(ChatMessage? msg)
        {
            if (msg == null || msg.Id <= 0) return;

            if (string.IsNullOrWhiteSpace(_currentDeviceId) ||
                !string.Equals(msg.DeviceId, _currentDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                ShowChatToast("You can only delete your own messages.");
                return;
            }

            try
            {
                var (success, errorMsg) = await CommunityChatService.DeleteMessageAsync(msg.Id, _currentDeviceId);
                if (success)
                {
                    _messages.Remove(msg);
                    EmptyChatText.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    ShowChatToast("Message deleted successfully");
                }
                else
                {
                    ShowChatToast(errorMsg ?? "Failed to delete message.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatWidget] DeleteCurrentMessage error: {ex.Message}");
                ShowChatToast("An error occurred while deleting the message.");
            }
        }

        private void DeleteMiniButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage msg)
            {
                DeleteCurrentMessage(msg);
            }
        }

        private void DeleteContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage msg)
            {
                DeleteCurrentMessage(msg);
            }
        }

        // ========================================================
        // Mini User Profile Card Features
        // ========================================================
        public void ShowMiniProfileCard(string? username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;

            string cleanName = username.Trim().TrimStart('@');
            MiniCardDisplayName.Text = cleanName;
            MiniCardAvatarInitial.Text = cleanName.Length > 0 ? cleanName.Substring(0, 1).ToUpper() : "?";

            MiniCardAvatarImage.Source = null;
            MiniCardAvatarImage.Visibility = Visibility.Collapsed;
            MiniCardAvatarInitial.Visibility = Visibility.Visible;

            // Set default initial state from cached messages if available
            var cachedMsg = _messages.FirstOrDefault(m => string.Equals(m.SenderName, cleanName, StringComparison.OrdinalIgnoreCase));
            int initialLevel = cachedMsg?.Level ?? 2;
            bool isOnline = CommunityChatRealtimeClient.IsUserOnline(cleanName);
            SetMiniCardBadge(initialLevel, isOnline);
            if (cachedMsg?.AvatarImage != null)
            {
                MiniCardAvatarImage.Source = cachedMsg.AvatarImage;
                MiniCardAvatarImage.Visibility = Visibility.Visible;
                MiniCardAvatarInitial.Visibility = Visibility.Collapsed;
            }
            MiniCardBioText.Text = "Loading user profile...";

            // Show in-drawer overlay
            MiniProfileCardOverlay.Visibility = Visibility.Visible;

            // Fetch full profile info asynchronously
            Task.Run(async () =>
            {
                var profile = await CommunityChatService.GetUserProfileByNameAsync(cleanName);
                System.Windows.Media.ImageSource? avatarImg = null;
                if (profile != null && profile.Id.HasValue && !string.IsNullOrWhiteSpace(profile.AvatarUrl))
                {
                    avatarImg = await Services.Profile.ProfilePictureCacheService.GetAvatarAsync(profile.Id.Value, profile.AvatarUrl);
                }

                Dispatcher.Invoke(() =>
                {
                    if (MiniProfileCardOverlay.Visibility == Visibility.Visible && 
                        string.Equals(MiniCardDisplayName.Text, cleanName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (profile != null)
                        {
                            MiniCardBioText.Text = !string.IsNullOrWhiteSpace(profile.Bio) ? profile.Bio : "No bio description.";
                            SetMiniCardBadge(profile.Level, CommunityChatRealtimeClient.IsUserOnline(cleanName));
                            if (avatarImg != null)
                            {
                                MiniCardAvatarImage.Source = avatarImg;
                                MiniCardAvatarImage.Visibility = Visibility.Visible;
                                MiniCardAvatarInitial.Visibility = Visibility.Collapsed;
                            }
                        }
                        else
                        {
                            MiniCardBioText.Text = "No bio description available.";
                        }
                    }
                });
            });
        }

        private void SetMiniCardBadge(int level, bool isOnline)
        {
            MiniCardOnlineDot.Visibility = isOnline ? Visibility.Visible : Visibility.Collapsed;

            switch (level)
            {
                case 0: // Admin
                    MiniCardLevelText.Text = "ADMIN";
                    MiniCardLevelBadge.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                    MiniCardAvatarBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    MiniCardLevelBadge.Visibility = Visibility.Visible;
                    break;
                case 1: // Supporter
                    MiniCardLevelText.Text = "SUPPORTER";
                    MiniCardLevelBadge.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                    MiniCardAvatarBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                    MiniCardLevelBadge.Visibility = Visibility.Visible;
                    break;
                case 3: // Moderator
                    MiniCardLevelText.Text = "MODERATOR";
                    MiniCardLevelBadge.Background = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Blue
                    MiniCardAvatarBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                    MiniCardLevelBadge.Visibility = Visibility.Visible;
                    break;
                case 2: // Member
                default:
                    MiniCardLevelText.Text = "MEMBER";
                    MiniCardLevelBadge.Background = new SolidColorBrush(Color.FromRgb(107, 114, 128)); // Gray / Abu
                    MiniCardAvatarBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(156, 163, 175));
                    MiniCardLevelBadge.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void MiniProfileCardOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            MiniProfileCardOverlay.Visibility = Visibility.Collapsed;
        }

        private void MiniProfileCardContent_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // Prevent closing when clicking card body
        }

        private void MiniCardCloseBtn_Click(object sender, RoutedEventArgs e)
        {
            MiniProfileCardOverlay.Visibility = Visibility.Collapsed;
        }

        private void MiniCardMentionBtn_Click(object sender, RoutedEventArgs e)
        {
            string name = MiniCardDisplayName.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name))
            {
                SwitchToChatTab();
                TagUser(name);
            }
            MiniProfileCardOverlay.Visibility = Visibility.Collapsed;
        }

        private void MiniCardCopyNameBtn_Click(object sender, RoutedEventArgs e)
        {
            string name = MiniCardDisplayName.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name))
            {
                try
                {
                    Clipboard.SetText(name);
                    ShowChatToast("Name copied to clipboard");
                }
                catch { }
            }
            MiniProfileCardOverlay.Visibility = Visibility.Collapsed;
        }

        private void SenderAvatar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage msg && !string.IsNullOrWhiteSpace(msg.SenderName))
            {
                ShowMiniProfileCard(msg.SenderName);
                e.Handled = true;
            }
        }

        private void SenderName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage msg && !string.IsNullOrWhiteSpace(msg.SenderName))
            {
                ShowMiniProfileCard(msg.SenderName);
                e.Handled = true;
            }
        }

        private void ViewProfileContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage msg && !string.IsNullOrWhiteSpace(msg.SenderName))
            {
                ShowMiniProfileCard(msg.SenderName);
            }
        }

        // ========================================================
        // Reply, Copy & Jump Navigation Features
        // ========================================================
        private DispatcherTimer? _toastTimer;

        public void ShowChatToast(string message)
        {
            if (ChatToastNotification == null || ChatToastText == null) return;

            ChatToastText.Text = message;
            ChatToastNotification.Visibility = Visibility.Visible;

            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer?.Stop();
                ChatToastNotification.Visibility = Visibility.Collapsed;
            };
            _toastTimer.Start();
        }

        private void SetReplyTarget(ChatMessage? msg)
        {
            if (msg == null) return;
            _activeReplyMessage = msg;
            ReplyTargetSenderText.Text = $"Replying to @{msg.SenderName}";
            ReplyTargetSnippetText.Text = msg.ReplySnippet.Length > 0 ? msg.ReplySnippet : msg.Message;
            ReplyPreviewBar.Visibility = Visibility.Visible;
            SwitchToChatTab();
            ChatInputTextBox.Focus();
        }

        private void ClearReplyTarget()
        {
            _activeReplyMessage = null;
            ReplyPreviewBar.Visibility = Visibility.Collapsed;
        }

        private void CopyMessage(ChatMessage? msg)
        {
            if (msg == null || string.IsNullOrWhiteSpace(msg.Message)) return;
            try
            {
                Clipboard.SetText(msg.Message);
                ShowChatToast("Message copied to clipboard");
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatWidget] CopyMessage error: {ex.Message}");
            }
        }

        private void ReplyMiniButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage msg)
            {
                SetReplyTarget(msg);
            }
        }

        private void CopyMiniButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage msg)
            {
                CopyMessage(msg);
            }
        }

        private void ReplyContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage msg)
            {
                SetReplyTarget(msg);
            }
        }

        private void CopyContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage msg)
            {
                CopyMessage(msg);
            }
        }

        private void CancelReplyBtn_Click(object sender, RoutedEventArgs e)
        {
            ClearReplyTarget();
        }

        private void ReplyQuoteBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatMessage msg && msg.ReplyToId.HasValue && msg.ReplyToId.Value > 0)
            {
                ScrollToAndHighlightMessage(msg.ReplyToId.Value);
                e.Handled = true;
            }
        }

        public void ScrollToAndHighlightMessage(long targetMessageId)
        {
            var targetMsg = _messages.FirstOrDefault(m => m.Id == targetMessageId);
            if (targetMsg == null)
            {
                ShowChatToast("Pesan yang dibalas berada di luar riwayat pesan terkini.");
                return;
            }

            SwitchToChatTab();

            var container = MessagesItemsControl.ItemContainerGenerator.ContainerFromItem(targetMsg) as FrameworkElement;
            if (container == null)
            {
                MessagesItemsControl.UpdateLayout();
                container = MessagesItemsControl.ItemContainerGenerator.ContainerFromItem(targetMsg) as FrameworkElement;
            }

            if (container != null)
            {
                container.BringIntoView();
            }

            // Animate highlight
            foreach (var m in _messages)
            {
                m.IsHighlighted = (m.Id == targetMessageId);
            }

            Task.Delay(1800).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    targetMsg.IsHighlighted = false;
                });
            });
        }

        public void TagUser(string senderName)
        {
            if (string.IsNullOrWhiteSpace(senderName)) return;

            SwitchToChatTab();

            string cleanName = senderName.Trim().TrimStart('@');
            string currentText = ChatInputTextBox.Text ?? string.Empty;

            if (string.IsNullOrWhiteSpace(currentText))
            {
                ChatInputTextBox.Text = $"@{cleanName} ";
            }
            else
            {
                if (!currentText.EndsWith(" "))
                {
                    ChatInputTextBox.Text += " ";
                }
                ChatInputTextBox.Text += $"@{cleanName} ";
            }

            ChatInputTextBox.CaretIndex = ChatInputTextBox.Text.Length;
            ChatInputTextBox.Focus();
        }

        // ========================================================
        // Online Presence & Header Tabs
        // ========================================================
        private async Task RefreshOnlinePresenceAsync()
        {
            try
            {
                // 1. Send heartbeat for current user
                if (!string.IsNullOrWhiteSpace(_currentDeviceId))
                {
                    _ = CommunityChatService.SendHeartbeatAsync(_currentDeviceId);
                }

                // 2. Fetch all users directory (online + offline)
                var allUsers = await CommunityChatService.GetAllUsersDirectoryAsync(100);

                // 3. Ensure current user is present if logged in and marked online
                if (_currentProfile != null && !string.IsNullOrWhiteSpace(_currentProfile.DisplayName))
                {
                    var existingCurrent = allUsers.FirstOrDefault(u => string.Equals(u.DisplayName, _currentProfile.DisplayName, StringComparison.OrdinalIgnoreCase));
                    if (existingCurrent != null)
                    {
                        existingCurrent.IsOnline = true;
                        existingCurrent.LastSeen = DateTime.UtcNow;
                    }
                    else
                    {
                        var selfCopy = new UserProfile
                        {
                            Id = _currentProfile.Id,
                            DeviceId = _currentProfile.DeviceId,
                            DisplayName = _currentProfile.DisplayName,
                            Bio = _currentProfile.Bio,
                            Level = _currentProfile.Level,
                            IsOnline = true,
                            LastSeen = DateTime.UtcNow
                        };
                        allUsers.Insert(0, selfCopy);
                    }
                }

                // 4. Update online usernames set for fast lookup
                var onlineUsers = allUsers.Where(u => u.IsOnline).ToList();
                var offlineUsers = allUsers.Where(u => !u.IsOnline).ToList();

                var onlineNames = onlineUsers
                    .Where(u => !string.IsNullOrWhiteSpace(u.DisplayName))
                    .Select(u => u.DisplayName.Trim().TrimStart('@'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                CommunityChatRealtimeClient.SetOnlineUsers(onlineNames);

                // 5. Sort online and offline users: Role rank (Admin > Mod > Supporter > Member), then DisplayName A-Z
                var sortedOnline = onlineUsers
                    .OrderBy(u => u.RoleRank)
                    .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var sortedOffline = offlineUsers
                    .OrderBy(u => u.RoleRank)
                    .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 6. Update Collections
                _onlineUsersList.Clear();
                foreach (var u in sortedOnline)
                {
                    _onlineUsersList.Add(u);
                }

                _offlineUsersList.Clear();
                foreach (var u in sortedOffline)
                {
                    _offlineUsersList.Add(u);
                }

                // 7. Update UI headers & counters
                OnlineSectionTitle.Text = $"ONLINE — {_onlineUsersList.Count}";
                OfflineSectionTitle.Text = $"OFFLINE — {_offlineUsersList.Count}";
                OnlineSectionHeader.Visibility = _onlineUsersList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                OfflineSectionHeader.Visibility = _offlineUsersList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                UpdateOnlineCountDisplay(_onlineUsersList.Count);
                UpdateOnlineStatusOnMessages(onlineNames);

                EmptyOnlineUsersText.Visibility = (_onlineUsersList.Count == 0 && _offlineUsersList.Count == 0)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatWidget] RefreshOnlinePresenceAsync error: {ex.Message}");
            }
        }

        private void UpdateOnlineStatusOnMessages(HashSet<string> onlineUsers)
        {
            foreach (var m in _messages)
            {
                m.IsOnline = !string.IsNullOrWhiteSpace(m.SenderName) && onlineUsers.Contains(m.SenderName.Trim().TrimStart('@'));
            }
        }

        private void UpdateOnlineCountDisplay(int count)
        {
            OnlineCountText.Text = $"Online ({count})";
        }

        public void SwitchToChatTab()
        {
            ChatViewPanel.Visibility = Visibility.Visible;
            OnlineUsersViewPanel.Visibility = Visibility.Collapsed;

            TabChatBtn.Background = Application.Current?.TryFindResource("ButtonBackgroundBrush") as Brush ?? Brushes.Transparent;
            TabOnlineBtn.Background = Brushes.Transparent;
        }

        public void SwitchToOnlineTab()
        {
            ChatViewPanel.Visibility = Visibility.Collapsed;
            OnlineUsersViewPanel.Visibility = Visibility.Visible;

            TabOnlineBtn.Background = Application.Current?.TryFindResource("ButtonBackgroundBrush") as Brush ?? Brushes.Transparent;
            TabChatBtn.Background = Brushes.Transparent;

            Task.Run(async () =>
            {
                await Dispatcher.InvokeAsync(async () => await RefreshOnlinePresenceAsync());
            });
        }

        private void TabChatBtn_Click(object sender, RoutedEventArgs e)
        {
            SwitchToChatTab();
        }

        private void TabOnlineBtn_Click(object sender, RoutedEventArgs e)
        {
            SwitchToOnlineTab();
        }

        private void OnlineUserTagBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is UserProfile user && !string.IsNullOrWhiteSpace(user.DisplayName))
            {
                TagUser(user.DisplayName);
            }
        }

        private void OnlineUserItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is UserProfile user && !string.IsNullOrWhiteSpace(user.DisplayName))
            {
                ShowMiniProfileCard(user.DisplayName);
                e.Handled = true;
            }
        }

        // ========================================================
        // Mention Popup Autocomplete Logic
        // ========================================================
        private void ChatInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Handle placeholder text visibility
            if (ChatInputPlaceholder != null)
            {
                ChatInputPlaceholder.Visibility = string.IsNullOrEmpty(ChatInputTextBox.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (_isUpdatingMentionList) return;

            try
            {
                string text = ChatInputTextBox.Text ?? string.Empty;
                int caret = ChatInputTextBox.CaretIndex;

                if (caret > 0 && caret <= text.Length)
                {
                    int lastAt = text.LastIndexOf('@', caret - 1);
                    if (lastAt >= 0)
                    {
                        // Ensure '@' is at start of line or preceded by whitespace
                        if (lastAt == 0 || char.IsWhiteSpace(text[lastAt - 1]))
                        {
                            string query = text.Substring(lastAt + 1, caret - (lastAt + 1));
                            if (!query.Contains(' ') && !query.Contains('\n') && !query.Contains('\t'))
                            {
                                _pendingMentionQuery = query;
                                _mentionDebounceTimer.Stop();
                                _mentionDebounceTimer.Start();
                                return;
                            }
                        }
                    }
                }

                _mentionDebounceTimer.Stop();
                MentionSuggestionPopup.IsOpen = false;
            }
            catch
            {
                MentionSuggestionPopup.IsOpen = false;
            }
        }

        private async Task ExecuteMentionSearchAsync(string query)
        {
            try
            {
                // 1. Search from Supabase user_profiles (all database users)
                var dbProfiles = await CommunityChatService.SearchUsersAsync(query, 3);
                var candidates = new List<MentionCandidate>();

                if (dbProfiles != null && dbProfiles.Count > 0)
                {
                    foreach (var p in dbProfiles)
                    {
                        if (!string.IsNullOrWhiteSpace(p.DisplayName) &&
                            !candidates.Any(c => string.Equals(c.SenderName, p.DisplayName, StringComparison.OrdinalIgnoreCase)))
                        {
                            candidates.Add(new MentionCandidate
                            {
                                SenderName = p.DisplayName,
                                Level = p.Level
                            });
                        }
                    }
                }

                // 2. Fallback to recent in-memory messages if DB query yielded fewer than 3
                if (candidates.Count < 3)
                {
                    var localMatches = _messages
                        .Where(m => !string.IsNullOrWhiteSpace(m.SenderName))
                        .Where(m => string.IsNullOrWhiteSpace(query) || m.SenderName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(m => m.SenderName, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new MentionCandidate { SenderName = g.Key, Level = g.First().Level });

                    foreach (var m in localMatches)
                    {
                        if (!candidates.Any(c => string.Equals(c.SenderName, m.SenderName, StringComparison.OrdinalIgnoreCase)))
                        {
                            candidates.Add(m);
                            if (candidates.Count >= 3) break;
                        }
                    }
                }

                _isUpdatingMentionList = true;
                try
                {
                    if (candidates.Count > 0)
                    {
                        MentionListBox.ItemsSource = candidates.Take(3).ToList();
                        MentionListBox.SelectedIndex = 0;
                        MentionSuggestionPopup.IsOpen = true;
                    }
                    else
                    {
                        MentionSuggestionPopup.IsOpen = false;
                    }
                }
                finally
                {
                    _isUpdatingMentionList = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatWidget] ExecuteMentionSearchAsync error: {ex.Message}");
                MentionSuggestionPopup.IsOpen = false;
            }
        }

        private void InsertMentionCandidate(string senderName)
        {
            if (string.IsNullOrWhiteSpace(senderName)) return;

            _isUpdatingMentionList = true;
            try
            {
                int caret = ChatInputTextBox.CaretIndex;
                string text = ChatInputTextBox.Text ?? string.Empty;
                int lastAt = -1;
                if (caret > 0 && caret <= text.Length)
                {
                    lastAt = text.LastIndexOf('@', caret - 1);
                }

                if (lastAt >= 0)
                {
                    string before = text.Substring(0, lastAt);
                    string after = (caret < text.Length) ? text.Substring(caret) : string.Empty;
                    string inserted = $"@{senderName} ";
                    ChatInputTextBox.Text = before + inserted + after;
                    ChatInputTextBox.CaretIndex = (before + inserted).Length;
                }
                else
                {
                    TagUser(senderName);
                }

                MentionSuggestionPopup.IsOpen = false;
                ChatInputTextBox.Focus();
            }
            finally
            {
                _isUpdatingMentionList = false;
            }
        }

        private void MentionItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is MentionCandidate cand)
            {
                InsertMentionCandidate(cand.SenderName);
                e.Handled = true;
            }
        }

        private void ChatInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (MentionSuggestionPopup.IsOpen)
            {
                if (e.Key == Key.Down)
                {
                    if (MentionListBox.SelectedIndex < MentionListBox.Items.Count - 1)
                    {
                        MentionListBox.SelectedIndex++;
                        MentionListBox.ScrollIntoView(MentionListBox.SelectedItem);
                    }
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Up)
                {
                    if (MentionListBox.SelectedIndex > 0)
                    {
                        MentionListBox.SelectedIndex--;
                        MentionListBox.ScrollIntoView(MentionListBox.SelectedItem);
                    }
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    if (MentionListBox.SelectedItem is MentionCandidate sel)
                    {
                        InsertMentionCandidate(sel.SenderName);
                        e.Handled = true;
                        return;
                    }
                }
                if (e.Key == Key.Escape)
                {
                    MentionSuggestionPopup.IsOpen = false;
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SendCurrentMessage();
            }
        }

        private void MentionListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                if (MentionListBox.SelectedItem is MentionCandidate sel)
                {
                    InsertMentionCandidate(sel.SenderName);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                MentionSuggestionPopup.IsOpen = false;
                ChatInputTextBox.Focus();
                e.Handled = true;
            }
        }

        // ========================================================
        // Widget Controls & Profile Settings
        // ========================================================
        private void CollapsedPill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Expand();
        }

        private void CollapseChatBtn_Click(object sender, RoutedEventArgs e)
        {
            Collapse();
        }

        private async void RefreshChatBtn_Click(object sender, RoutedEventArgs e)
        {
            await RefreshMessagesAsync();
            await RefreshOnlinePresenceAsync();
        }

        private void SendChatBtn_Click(object sender, RoutedEventArgs e)
        {
            SendCurrentMessage();
        }

        private void SetProfileNameBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowNavigator.ShowCustomizeProfileModal(onSaved: async () =>
            {
                await CheckUserProfileStatusAsync();
            });
        }
    }
}
