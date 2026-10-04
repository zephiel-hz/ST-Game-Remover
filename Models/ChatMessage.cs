using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SteamPluginManager.Models
{
    public class ChatMessage : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        [JsonPropertyName("id")]
        public long Id { get; set; }

        private string _deviceId = string.Empty;
        [JsonPropertyName("device_id")]
        public string DeviceId
        {
            get => _deviceId;
            set
            {
                if (_deviceId != value)
                {
                    _deviceId = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _senderName = string.Empty;
        [JsonPropertyName("sender_name")]
        public string SenderName
        {
            get => _senderName;
            set
            {
                if (_senderName != value)
                {
                    _senderName = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AvatarInitial));
                }
            }
        }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        private int _level = 2; // 0 = Admin, 1 = Supporter, 2 = Member, 3 = Moderator
        [JsonPropertyName("level")]
        public int Level
        {
            get => _level;
            set
            {
                if (_level != value)
                {
                    _level = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsAdmin));
                    OnPropertyChanged(nameof(IsSupporter));
                    OnPropertyChanged(nameof(IsMember));
                    OnPropertyChanged(nameof(IsModerator));
                    OnPropertyChanged(nameof(HasBadge));
                    OnPropertyChanged(nameof(BadgeText));
                }
            }
        }

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("reply_to_id")]
        public long? ReplyToId { get; set; }

        [JsonPropertyName("reply_to_sender")]
        public string? ReplyToSender { get; set; }

        [JsonPropertyName("reply_to_text")]
        public string? ReplyToText { get; set; }

        // UI Helpers
        private bool _isOwnMessage;
        [JsonIgnore]
        public bool IsOwnMessage
        {
            get => _isOwnMessage;
            set
            {
                if (_isOwnMessage != value)
                {
                    _isOwnMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isHighlighted;
        [JsonIgnore]
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set
            {
                if (_isHighlighted != value)
                {
                    _isHighlighted = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isOnline;
        [JsonIgnore]
        public bool IsOnline
        {
            get => _isOnline;
            set
            {
                if (_isOnline != value)
                {
                    _isOnline = value;
                    OnPropertyChanged();
                }
            }
        }

        [JsonIgnore]
        public bool HasReply => !string.IsNullOrWhiteSpace(ReplyToSender) && !string.IsNullOrWhiteSpace(ReplyToText);

        [JsonIgnore]
        public string ReplySnippet
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ReplyToText)) return string.Empty;
                var singleLine = ReplyToText.Replace("\r", " ").Replace("\n", " ").Trim();
                return singleLine.Length > 50 ? singleLine.Substring(0, 47) + "..." : singleLine;
            }
        }

        [JsonIgnore]
        public bool IsAdmin => Level == 0;

        [JsonIgnore]
        public bool IsSupporter => Level == 1;

        [JsonIgnore]
        public bool IsMember => Level == 2;

        [JsonIgnore]
        public bool IsModerator => Level == 3;

        [JsonIgnore]
        public bool HasBadge => true;

        [JsonIgnore]
        public string BadgeText => Level switch
        {
            0 => "ADMIN",
            1 => "SUPPORTER",
            3 => "MODERATOR",
            2 => "MEMBER",
            _ => "MEMBER"
        };

        [JsonIgnore]
        public string AvatarInitial => !string.IsNullOrWhiteSpace(SenderName) 
            ? SenderName.Trim().Substring(0, 1).ToUpper() 
            : "?";

        private int? _userId;
        [JsonPropertyName("user_id")]
        public int? UserId
        {
            get => _userId;
            set
            {
                if (_userId != value)
                {
                    _userId = value;
                    OnPropertyChanged();
                    _ = LoadAvatarImageAsync();
                }
            }
        }

        private string? _avatarUrl;
        [JsonPropertyName("avatar_url")]
        public string? AvatarUrl
        {
            get => _avatarUrl;
            set
            {
                if (_avatarUrl != value)
                {
                    _avatarUrl = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasCustomAvatar));
                    _ = LoadAvatarImageAsync();
                }
            }
        }

        private System.Windows.Media.ImageSource? _avatarImage;
        [JsonIgnore]
        public System.Windows.Media.ImageSource? AvatarImage
        {
            get
            {
                if (_avatarImage == null && UserId.HasValue && !string.IsNullOrWhiteSpace(AvatarUrl))
                {
                    var inMem = Services.Profile.ProfilePictureCacheService.GetCachedAvatarInMemory(UserId.Value);
                    if (inMem != null)
                    {
                        _avatarImage = inMem;
                    }
                    else
                    {
                        _ = LoadAvatarImageAsync();
                    }
                }
                return _avatarImage;
            }
            set
            {
                if (_avatarImage != value)
                {
                    _avatarImage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasCustomAvatar));
                }
            }
        }

        [JsonIgnore]
        public bool HasCustomAvatar => AvatarImage != null;

        public async System.Threading.Tasks.Task LoadAvatarImageAsync()
        {
            if (!UserId.HasValue || string.IsNullOrWhiteSpace(AvatarUrl))
            {
                AvatarImage = null;
                return;
            }

            var img = await Services.Profile.ProfilePictureCacheService.GetAvatarAsync(UserId.Value, AvatarUrl);
            AvatarImage = img;
        }

        [JsonIgnore]
        public string FormattedTime => CreatedAt.ToLocalTime().ToString("HH:mm");
    }
}
