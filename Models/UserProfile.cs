using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SteamPluginManager.Models
{
    public class UserProfile : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        [JsonPropertyName("id")]
        public int? Id { get; set; }

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

        private string _displayName = string.Empty;
        [JsonPropertyName("display_name")]
        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName != value)
                {
                    _displayName = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AvatarInitial));
                }
            }
        }

        private string _bio = string.Empty;
        [JsonPropertyName("bio")]
        public string Bio
        {
            get => _bio;
            set
            {
                if (_bio != value)
                {
                    _bio = value;
                    OnPropertyChanged();
                }
            }
        }

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
                    OnPropertyChanged(nameof(BadgeText));
                    OnPropertyChanged(nameof(RoleRank));
                }
            }
        }

        private DateTime? _lastSeen;
        [JsonPropertyName("last_seen")]
        public DateTime? LastSeen
        {
            get => _lastSeen;
            set
            {
                if (_lastSeen != value)
                {
                    _lastSeen = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LastSeenText));
                }
            }
        }

        // UI Helpers (defaults to false unless actively determined from heartbeat/online set)
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
                    OnPropertyChanged(nameof(LastSeenText));
                }
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
        public string BadgeText => Level switch
        {
            0 => "ADMIN",
            1 => "SUPPORTER",
            3 => "MODERATOR",
            2 => "MEMBER",
            _ => "MEMBER"
        };

        /// <summary>
        /// Hierarki pengurutan Role: Admin (0) -> Moderator (1) -> Supporter (2) -> Member (3)
        /// </summary>
        [JsonIgnore]
        public int RoleRank => Level switch
        {
            0 => 0, // Admin
            3 => 1, // Moderator
            1 => 2, // Supporter
            2 => 3, // Member
            _ => 4
        };

        [JsonIgnore]
        public string LastSeenText
        {
            get
            {
                if (IsOnline) return "Online now";
                if (!LastSeen.HasValue) return "Offline";
                var diff = DateTime.UtcNow - LastSeen.Value.ToUniversalTime();
                if (diff.TotalSeconds < 45) return "Just now";
                if (diff.TotalMinutes < 60) return $"{Math.Max(1, (int)diff.TotalMinutes)}m ago";
                if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
                if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
                return LastSeen.Value.ToLocalTime().ToString("dd MMM yyyy");
            }
        }

        [JsonIgnore]
        public string AvatarInitial => !string.IsNullOrWhiteSpace(DisplayName)
            ? DisplayName.Trim().Substring(0, 1).ToUpper()
            : "?";

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
                if (_avatarImage == null && Id.HasValue && !string.IsNullOrWhiteSpace(AvatarUrl))
                {
                    var inMem = Services.Profile.ProfilePictureCacheService.GetCachedAvatarInMemory(Id.Value);
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
            if (!Id.HasValue || string.IsNullOrWhiteSpace(AvatarUrl))
            {
                AvatarImage = null;
                return;
            }

            var img = await Services.Profile.ProfilePictureCacheService.GetAvatarAsync(Id.Value, AvatarUrl);
            AvatarImage = img;
        }
    }
}
