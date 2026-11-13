using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SteamPluginManager.Models
{
    public sealed class DiscordForumPost : INotifyPropertyChanged
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("replyCount")]
        public int ReplyCount { get; set; }

        [JsonPropertyName("archived")]
        public bool Archived { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new();

        [JsonPropertyName("attachments")]
        public List<DiscordForumAttachment> Attachments { get; set; } = new();

        [JsonIgnore]
        public ObservableCollection<DiscordForumReply> Replies { get; } = new();

        [JsonIgnore]
        public bool RepliesLoaded { get; set; }

        [JsonIgnore]
        public bool IsLoadingReplies { get; set; }

        [JsonIgnore]
        public string? BeforeReplyId { get; set; }

        private bool _hasMoreReplies;
        [JsonIgnore]
        public bool HasMoreReplies
        {
            get => _hasMoreReplies;
            set => SetProperty(ref _hasMoreReplies, value);
        }

        private string _repliesStatus = string.Empty;
        [JsonIgnore]
        public string RepliesStatus
        {
            get => _repliesStatus;
            set => SetProperty(ref _repliesStatus, value);
        }

        [JsonIgnore]
        public string CreatedAtText => CreatedAt.ToLocalTime().ToString("dd MMM yyyy, HH:mm", CultureInfo.CurrentCulture);

        [JsonIgnore]
        public string ReplyCountText => ReplyCount == 1 ? "1 reply" : $"{ReplyCount} replies";

        [JsonIgnore]
        public string DisplayContent => string.IsNullOrWhiteSpace(Content) ? "No text in the original post." : Content;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public bool MatchesSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return true;

            return Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Content.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Author.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase))
                || Attachments.Any(attachment =>
                    attachment.FileName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class DiscordForumReply
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("attachments")]
        public List<DiscordForumAttachment> Attachments { get; set; } = new();

        [JsonIgnore]
        public string CreatedAtText => CreatedAt.ToLocalTime().ToString("dd MMM yyyy, HH:mm", CultureInfo.CurrentCulture);

        [JsonIgnore]
        public string DisplayContent => string.IsNullOrWhiteSpace(Content) ? "Attachment" : Content;
    }

    public sealed class DiscordForumAttachment
    {
        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("contentType")]
        public string ContentType { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonIgnore]
        public string SizeText => FormatSize(Size);

        private static string FormatSize(long size)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = Math.Max(0, size);
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }
    }
}
