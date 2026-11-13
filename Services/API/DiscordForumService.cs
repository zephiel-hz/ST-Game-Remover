using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SteamPluginManager.Models;
using SteamPluginManager.Services.Security;

namespace SteamPluginManager.Services.API
{
    public static class DiscordForumService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private sealed class ForumResponse
        {
            [JsonPropertyName("posts")]
            public List<DiscordForumPost> Posts { get; set; } = new();
        }

        private sealed class ForumRepliesResponse
        {
            [JsonPropertyName("replies")]
            public List<DiscordForumReply> Replies { get; set; } = new();

            [JsonPropertyName("hasMore")]
            public bool HasMore { get; set; }

            [JsonPropertyName("beforeMessageId")]
            public string? BeforeMessageId { get; set; }
        }

        public static async Task<List<DiscordForumPost>> GetPostsAsync(CancellationToken cancellationToken = default)
        {
            var result = await InvokeFunctionAsync<ForumResponse>(
                deviceId => new { deviceId },
                "forum request",
                cancellationToken);
            return result.Posts;
        }

        public static async Task<ForumRepliesPage> GetRepliesAsync(
            string threadId,
            string? beforeMessageId = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsDiscordId(threadId))
                throw new ArgumentException("The forum thread ID is invalid.", nameof(threadId));
            if (beforeMessageId != null && !IsDiscordId(beforeMessageId))
                throw new ArgumentException("The reply pagination ID is invalid.", nameof(beforeMessageId));

            var result = await InvokeFunctionAsync<ForumRepliesResponse>(
                deviceId => new { deviceId, threadId, beforeMessageId },
                "forum replies request",
                cancellationToken);
            return new ForumRepliesPage(result.Replies, result.HasMore, result.BeforeMessageId);
        }

        private static async Task<TResponse> InvokeFunctionAsync<TResponse>(
            Func<string, object> createPayload,
            string operation,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(SupabaseConfig.SupabaseUrl) ||
                string.IsNullOrWhiteSpace(SupabaseConfig.AnonKey))
            {
                throw new InvalidOperationException("Supabase URL or public API key is not configured.");
            }
            await DeviceIdentityService.ResolveAndMigrateAsync();
            string deviceId = DeviceIdentifier.GetDeviceId();
            if (!DeviceIdentifier.IsValidDeviceId(deviceId))
                throw new InvalidOperationException("This device does not have a valid device ID.");

            string url = $"{SupabaseConfig.SupabaseUrl.TrimEnd('/')}/functions/v1/discord-forum";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(createPayload(deviceId)),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Add("apikey", SupabaseConfig.AnonKey);

            using var response = await SharedHttpClient.Instance.SendAsync(request, cancellationToken);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string errorMessage = GetErrorMessage(responseBody);
                Logger.Log(
                    $"[DiscordForumService] {operation} failed with HTTP {(int)response.StatusCode}: {errorMessage}");
                throw new HttpRequestException(
                    $"{operation} failed (HTTP {(int)response.StatusCode}). {errorMessage}");
            }

            var result = JsonSerializer.Deserialize<TResponse>(responseBody, JsonOptions);
            if (result == null)
                throw new InvalidDataException("The forum service returned an invalid response.");

            return result;
        }

        private static bool IsDiscordId(string value) =>
            value.Length is >= 17 and <= 20 && value.All(char.IsAsciiDigit);

        public static async Task DownloadAttachmentAsync(
            DiscordForumAttachment attachment,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                (uri.Host != "cdn.discordapp.com" && uri.Host != "media.discordapp.net"))
            {
                throw new InvalidDataException("The attachment URL is not a trusted Discord CDN URL.");
            }

            using var response = await SharedHttpClient.Instance.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);
            await source.CopyToAsync(destination, cancellationToken);
        }

        private static string GetErrorMessage(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                return "Check the Edge Function deployment and configuration.";

            try
            {
                using var document = JsonDocument.Parse(responseBody);
                var root = document.RootElement;
                string? code = root.TryGetProperty("code", out var codeElement) &&
                    codeElement.ValueKind == JsonValueKind.String
                    ? codeElement.GetString()
                    : null;
                string? message = root.TryGetProperty("message", out var messageElement) &&
                    messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(message) &&
                    root.TryGetProperty("error", out var errorElement) &&
                    errorElement.ValueKind == JsonValueKind.String)
                {
                    message = errorElement.GetString();
                }

                if (!string.IsNullOrWhiteSpace(message))
                    return string.IsNullOrWhiteSpace(code) ? message : $"{message} ({code})";
            }
            catch (JsonException)
            {
                return "The forum service returned an unreadable error response.";
            }

            return "The forum service returned an error.";
        }
    }

    public sealed record ForumRepliesPage(
        List<DiscordForumReply> Replies,
        bool HasMore,
        string? BeforeMessageId);
}
