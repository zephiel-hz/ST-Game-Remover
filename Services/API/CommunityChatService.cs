using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SteamPluginManager.Models;
using SteamPluginManager.Services.Security;

namespace SteamPluginManager.Services.API
{
    public static class CommunityChatService
    {
        private static DateTime _lastSendTime = DateTime.MinValue;
        private static readonly TimeSpan _minSendInterval = TimeSpan.FromSeconds(1.5);
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        private sealed class MessageReactionRecord
        {
            [JsonPropertyName("message_id")]
            public long MessageId { get; set; }

            [JsonPropertyName("device_id")]
            public string DeviceId { get; set; } = string.Empty;

            [JsonPropertyName("emoji")]
            public string Emoji { get; set; } = string.Empty;
        }

        /// <summary>
        /// Mengambil maksimal 50 pesan obrolan terbaru dalam urutan kronologis (terlama ke terbaru).
        /// Secara otomatis memetakan nama tampilan dan badge level terbaru dari tabel user_profiles.
        /// </summary>
        public static async Task<List<ChatMessage>> GetRecentMessagesAsync(string? currentDeviceId = null)
        {
            try
            {
                var http = SharedHttpClient.Instance;
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/community_messages?device_id=neq.SYSTEM_BROADCAST&select=*&order=created_at.desc&limit=50";
                
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Accept", "application/json");

                var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"[CommunityChatService] GetRecentMessagesAsync failed with status: {response.StatusCode}");
                    return new List<ChatMessage>();
                }

                var content = await response.Content.ReadAsStringAsync();
                var messages = JsonSerializer.Deserialize<List<ChatMessage>>(content, _jsonOptions);
                if (messages == null || messages.Count == 0)
                {
                    return new List<ChatMessage>();
                }

                // Filter out any pinned broadcast messages so they don't render as regular chat bubbles
                messages = messages.Where(m => !string.Equals(m.DeviceId, "SYSTEM_BROADCAST", StringComparison.OrdinalIgnoreCase)).ToList();

                // Ambil data user_profiles terkini agar seluruh riwayat pesan selalu menampilkan username, badge level, dan avatar terbaru
                try
                {
                    var profilesUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?select=id,device_id,display_name,level,avatar_url&display_name=not.is.null&order=last_seen.desc.nullslast&limit=1000";
                    using var profReq = new HttpRequestMessage(HttpMethod.Get, profilesUrl);
                    profReq.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    profReq.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                    profReq.Headers.Add("Accept", "application/json");

                    var profResp = await http.SendAsync(profReq);
                    if (profResp.IsSuccessStatusCode)
                    {
                        var profContent = await profResp.Content.ReadAsStringAsync();
                        var profiles = JsonSerializer.Deserialize<List<UserProfile>>(profContent, _jsonOptions);
                        if (profiles != null && profiles.Count > 0)
                        {
                            var deviceMap = new Dictionary<string, UserProfile>(StringComparer.OrdinalIgnoreCase);
                            var nameMap = new Dictionary<string, UserProfile>(StringComparer.OrdinalIgnoreCase);

                            foreach (var p in profiles)
                            {
                                // Hanya daftarkan ke deviceMap jika DeviceId valid (bukan 'Not applicable' / 'UNKNOWN')
                                if (DeviceIdentifier.IsValidDeviceId(p.DeviceId))
                                {
                                    if (!deviceMap.TryGetValue(p.DeviceId, out var existingDev) || (string.IsNullOrWhiteSpace(existingDev.AvatarUrl) && !string.IsNullOrWhiteSpace(p.AvatarUrl)))
                                    {
                                        deviceMap[p.DeviceId] = p;
                                    }
                                }

                                if (!string.IsNullOrWhiteSpace(p.DisplayName))
                                {
                                    var cleanName = p.DisplayName.Trim().TrimStart('@');
                                    // Utamakan baris yang memiliki AvatarUrl
                                    if (!nameMap.TryGetValue(cleanName, out var existingName) || (string.IsNullOrWhiteSpace(existingName.AvatarUrl) && !string.IsNullOrWhiteSpace(p.AvatarUrl)))
                                    {
                                        nameMap[cleanName] = p;
                                    }
                                }
                            }

                            foreach (var msg in messages)
                            {
                                UserProfile? matched = null;

                                // 1. Coba cocokkan via DeviceId yang valid
                                if (DeviceIdentifier.IsValidDeviceId(msg.DeviceId) && deviceMap.TryGetValue(msg.DeviceId, out var byDev))
                                {
                                    matched = byDev;
                                }

                                // 2. Cocokkan via DisplayName (atau jika byDev tidak memiliki avatar tetapi byName memiliki avatar)
                                if (!string.IsNullOrWhiteSpace(msg.SenderName))
                                {
                                    var cleanSender = msg.SenderName.Trim().TrimStart('@');
                                    if (nameMap.TryGetValue(cleanSender, out var byName))
                                    {
                                        if (matched == null || (string.IsNullOrWhiteSpace(matched.AvatarUrl) && !string.IsNullOrWhiteSpace(byName.AvatarUrl)))
                                        {
                                            matched = byName;
                                        }
                                    }
                                }

                                if (matched != null)
                                {
                                    if (!string.IsNullOrWhiteSpace(matched.DisplayName))
                                    {
                                        msg.SenderName = matched.DisplayName;
                                    }
                                    msg.Level = matched.Level;
                                    msg.UserId = matched.Id;
                                    msg.AvatarUrl = matched.AvatarUrl;
                                }
                            }
                        }
                    }
                }
                catch (Exception pEx)
                {
                    Logger.Log($"[CommunityChatService] Sync user_profiles in GetRecentMessagesAsync error: {pEx.Message}");
                }

                await LoadMessageReactionsAsync(http, messages, currentDeviceId);

                // Balik urutan agar pesan tertua di atas dan pesan terbaru di bawah
                messages.Reverse();
                return messages;
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] GetRecentMessagesAsync error: {ex.Message}");
                return new List<ChatMessage>();
            }
        }

        private static async Task LoadMessageReactionsAsync(HttpClient http, List<ChatMessage> messages, string? currentDeviceId)
        {
            var messageIds = messages.Where(message => message.Id > 0).Select(message => message.Id).Distinct().ToList();
            if (messageIds.Count == 0)
                return;

            string ids = string.Join(",", messageIds);
            string url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/community_message_reactions?select=message_id,device_id,emoji&message_id=in.({ids})";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
            request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
            request.Headers.Add("Accept", "application/json");

            try
            {
                var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Logger.Log($"[CommunityChatService] LoadMessageReactionsAsync failed: HTTP {response.StatusCode} - {error}");
                    return;
                }

                var content = await response.Content.ReadAsStringAsync();
                var reactionRows = JsonSerializer.Deserialize<List<MessageReactionRecord>>(content, _jsonOptions);
                if (reactionRows == null)
                {
                    Logger.Log("[CommunityChatService] LoadMessageReactionsAsync returned an invalid response.");
                    return;
                }

                foreach (var message in messages)
                {
                    var summaries = reactionRows
                        .Where(row => row.MessageId == message.Id && !string.IsNullOrWhiteSpace(row.Emoji))
                        .GroupBy(row => row.Emoji)
                        .Select(group =>
                        {
                            var deviceIds = group
                                .Select(row => row.DeviceId)
                                .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            return new ChatReactionSummary
                            {
                                MessageId = message.Id,
                                Emoji = group.Key,
                                Count = deviceIds.Count,
                                IsSelected = !string.IsNullOrWhiteSpace(currentDeviceId) &&
                                             deviceIds.Contains(currentDeviceId, StringComparer.OrdinalIgnoreCase)
                            };
                        })
                        .Where(summary => summary.Count > 0)
                        .ToList();

                    message.SetReactions(summaries);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] LoadMessageReactionsAsync error: {ex.Message}");
            }
        }

        public static async Task<(bool Success, string? ErrorMessage)> SetMessageReactionAsync(
            long messageId,
            string deviceId,
            string emoji,
            bool addReaction)
        {
            if (messageId <= 0 || string.IsNullOrWhiteSpace(deviceId) ||
                string.IsNullOrWhiteSpace(emoji) || emoji.Length > 32)
            {
                return (false, "Invalid reaction data.");
            }

            try
            {
                var http = SharedHttpClient.Instance;
                if (addReaction)
                {
                    var clearUrl = $"{SupabaseConfig.SupabaseUrl}/rest/v1/community_message_reactions" +
                                   $"?message_id=eq.{messageId}&device_id=eq.{Uri.EscapeDataString(deviceId)}";
                    using var clearRequest = new HttpRequestMessage(HttpMethod.Delete, clearUrl);
                    clearRequest.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    clearRequest.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                    clearRequest.Headers.Add("Prefer", "return=minimal");

                    using var clearResponse = await http.SendAsync(clearRequest);
                    if (!clearResponse.IsSuccessStatusCode)
                    {
                        var error = await clearResponse.Content.ReadAsStringAsync();
                        Logger.Log($"[CommunityChatService] SetMessageReactionAsync clear failed: HTTP {clearResponse.StatusCode} - {error}");
                        return (false, "Could not save the reaction. Please try again.");
                    }
                }

                HttpRequestMessage request;
                if (addReaction)
                {
                    var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/community_message_reactions?on_conflict=message_id,device_id,emoji";
                    var payload = new Dictionary<string, object>
                    {
                        ["message_id"] = messageId,
                        ["device_id"] = deviceId,
                        ["emoji"] = emoji
                    };

                    request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                    };
                    request.Headers.Add("Prefer", "resolution=ignore-duplicates,return=minimal");
                }
                else
                {
                    var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/community_message_reactions" +
                              $"?message_id=eq.{messageId}&device_id=eq.{Uri.EscapeDataString(deviceId)}&emoji=eq.{Uri.EscapeDataString(emoji)}";
                    request = new HttpRequestMessage(HttpMethod.Delete, url);
                    request.Headers.Add("Prefer", "return=minimal");
                }

                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                using (request)
                using (var response = await http.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await response.Content.ReadAsStringAsync();
                        Logger.Log($"[CommunityChatService] SetMessageReactionAsync failed: HTTP {response.StatusCode} - {error}");
                        return (false, "Could not save the reaction. Check that the chat reactions database migration has been applied.");
                    }
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] SetMessageReactionAsync error: {ex.Message}");
                return (false, "Could not save the reaction. Please try again.");
            }
        }

        /// <summary>
        /// Mengirim pesan chat baru ke Supabase, dengan dukungan reply pesan opsional.
        /// </summary>
        public static async Task<(bool Success, string? ErrorMessage, ChatMessage? SentMessage)> SendMessageAsync(
            string deviceId, 
            string senderName, 
            string message,
            int level = 2,
            long? replyToId = null,
            string? replyToSender = null,
            string? replyToText = null)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return (false, "Invalid Device ID.", null);
            }

            if (string.IsNullOrWhiteSpace(senderName))
            {
                return (false, "Please set your display name before sending messages.", null);
            }

            string trimmedMsg = message?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedMsg))
            {
                return (false, "Message cannot be empty.", null);
            }

            if (trimmedMsg.Length > 300)
            {
                trimmedMsg = trimmedMsg.Substring(0, 300);
            }

            // Anti-spam interval check
            var now = DateTime.UtcNow;
            if ((now - _lastSendTime) < _minSendInterval)
            {
                return (false, "Please wait a moment before sending another message.", null);
            }

            try
            {
                var http = SharedHttpClient.Instance;
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/community_messages";

                var payload = new Dictionary<string, object>
                {
                    ["device_id"] = deviceId,
                    ["sender_name"] = senderName.Trim(),
                    ["message"] = trimmedMsg,
                    ["level"] = level
                };

                if (replyToId.HasValue && replyToId.Value > 0 && !string.IsNullOrWhiteSpace(replyToSender))
                {
                    payload["reply_to_id"] = replyToId.Value;
                    payload["reply_to_sender"] = replyToSender.Trim();
                    payload["reply_to_text"] = replyToText ?? string.Empty;
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Prefer", "return=representation");

                var response = await http.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                // If schema doesn't have reply columns yet (e.g. column not found), retry without reply fields as graceful fallback
                if (!response.IsSuccessStatusCode && payload.ContainsKey("reply_to_id"))
                {
                    Logger.Log($"[CommunityChatService] Send with reply failed ({response.StatusCode}), retrying basic payload...");
                    var fallbackPayload = new Dictionary<string, object>
                    {
                        ["device_id"] = deviceId,
                        ["sender_name"] = senderName.Trim(),
                        ["message"] = trimmedMsg,
                        ["level"] = level
                    };

                    using var fallbackReq = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(fallbackPayload), Encoding.UTF8, "application/json")
                    };
                    fallbackReq.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                    fallbackReq.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                    fallbackReq.Headers.Add("Prefer", "return=representation");

                    response = await http.SendAsync(fallbackReq);
                    responseContent = await response.Content.ReadAsStringAsync();
                }

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"[CommunityChatService] SendMessageAsync failed: HTTP {response.StatusCode} - {responseContent}");
                    return (false, "Failed to send message to server.", null);
                }

                _lastSendTime = DateTime.UtcNow;

                var createdMessages = JsonSerializer.Deserialize<List<ChatMessage>>(responseContent, _jsonOptions);
                var sent = createdMessages?.FirstOrDefault();
                return (true, null, sent);
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] SendMessageAsync exception: {ex.Message}");
                return (false, $"Error: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Mencari data profil user dari tabel user_profiles untuk mention suggestion (maksimal sesuai parameter limit, default 3).
        /// </summary>
        public static async Task<List<UserProfile>> SearchUsersAsync(string query, int limit = 3)
        {
            try
            {
                var http = SharedHttpClient.Instance;
                string cleanQuery = query?.Trim() ?? string.Empty;
                string url;

                if (string.IsNullOrWhiteSpace(cleanQuery))
                {
                    url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?select=id,device_id,display_name,bio,level,avatar_url&display_name=not.is.null&order=updated_at.desc&limit={limit}";
                }
                else
                {
                    url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?select=id,device_id,display_name,bio,level,avatar_url&display_name=ilike.*{Uri.EscapeDataString(cleanQuery)}*&order=updated_at.desc&limit={limit}";
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Accept", "application/json");

                var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"[CommunityChatService] SearchUsersAsync failed with status: {response.StatusCode}");
                    return new List<UserProfile>();
                }

                var content = await response.Content.ReadAsStringAsync();
                var profiles = JsonSerializer.Deserialize<List<UserProfile>>(content, _jsonOptions);
                return profiles?.Where(p => !string.IsNullOrWhiteSpace(p.DisplayName)).Take(limit).ToList() ?? new List<UserProfile>();
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] SearchUsersAsync error: {ex.Message}");
                return new List<UserProfile>();
            }
        }

        /// <summary>
        /// Mengambil detail profil user berdasarkan DisplayName untuk ditampilkan di Mini Profile Card.
        /// </summary>
        public static async Task<UserProfile?> GetUserProfileByNameAsync(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return null;

            try
            {
                var http = SharedHttpClient.Instance;
                string cleanName = displayName.Trim().TrimStart('@');
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?display_name=eq.{Uri.EscapeDataString(cleanName)}&select=id,device_id,display_name,bio,level,avatar_url&limit=1";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Accept", "application/json");

                var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                var profiles = JsonSerializer.Deserialize<List<UserProfile>>(content, _jsonOptions);
                return profiles?.FirstOrDefault();
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] GetUserProfileByNameAsync error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Menghapus pesan chat milik sendiri dari tabel community_messages.
        /// Memastikan device_id cocok sehingga user tidak dapat menghapus pesan orang lain.
        /// </summary>
        public static async Task<(bool Success, string? ErrorMessage)> DeleteMessageAsync(long messageId, string deviceId)
        {
            if (messageId <= 0 || string.IsNullOrWhiteSpace(deviceId))
            {
                return (false, "Invalid parameters.");
            }

            try
            {
                var http = SharedHttpClient.Instance;
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/community_messages?id=eq.{messageId}&device_id=eq.{Uri.EscapeDataString(deviceId)}";

                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    Logger.Log($"[CommunityChatService] DeleteMessageAsync failed: {response.StatusCode} - {err}");
                    return (false, "Failed to delete message from server.");
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] DeleteMessageAsync error: {ex.Message}");
                return (false, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Mengirim heartbeat berkala untuk memperbarui timestamp last_seen di tabel user_profiles.
        /// </summary>
        public static async Task<bool> SendHeartbeatAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;

            try
            {
                var http = SharedHttpClient.Instance;
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?device_id=eq.{Uri.EscapeDataString(deviceId)}";

                var payload = new Dictionary<string, object>
                {
                    ["last_seen"] = DateTime.UtcNow.ToString("o")
                };

                using var request = new HttpRequestMessage(HttpMethod.Patch, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                var response = await http.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] SendHeartbeatAsync error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Mengubah status pengguna menjadi offline seketika dengan memperbarui last_seen ke masa lampau di Supabase.
        /// </summary>
        public static async Task<bool> SetUserOfflineAsync(string deviceId, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;

            try
            {
                var http = SharedHttpClient.Instance;
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?device_id=eq.{Uri.EscapeDataString(deviceId)}";

                // Mundurkan last_seen ke 5 menit lalu agar cutoff last_seen >= (now - 10s) langsung bernilai false seketika
                var payload = new Dictionary<string, object>
                {
                    ["last_seen"] = DateTime.UtcNow.AddMinutes(-5).ToString("o")
                };

                using var request = new HttpRequestMessage(HttpMethod.Patch, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                var response = await http.SendAsync(request, ct);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] SetUserOfflineAsync error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Mengambil daftar pengguna yang sedang online (last_seen dalam 10 detik terakhir).
        /// </summary>
        public static async Task<List<UserProfile>> GetOnlineUsersAsync()
        {
            try
            {
                var http = SharedHttpClient.Instance;
                // Threshold 10 detik untuk deteksi online/offline cepat
                var cutoff = DateTime.UtcNow.AddSeconds(-10).ToString("o");
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?last_seen=gte.{Uri.EscapeDataString(cutoff)}&display_name=not.is.null&select=id,device_id,display_name,bio,level,last_seen,avatar_url&order=last_seen.desc&limit=50";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Accept", "application/json");

                var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"[CommunityChatService] GetOnlineUsersAsync failed: {response.StatusCode}");
                    return new List<UserProfile>();
                }

                var content = await response.Content.ReadAsStringAsync();
                var profiles = JsonSerializer.Deserialize<List<UserProfile>>(content, _jsonOptions);
                if (profiles == null) return new List<UserProfile>();

                foreach (var p in profiles)
                {
                    p.IsOnline = true;
                }
                return profiles.Where(p => !string.IsNullOrWhiteSpace(p.DisplayName)).ToList();
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] GetOnlineUsersAsync error: {ex.Message}");
                return new List<UserProfile>();
            }
        }

        /// <summary>
        /// Mengambil seluruh daftar pengguna (online dan offline) dari tabel user_profiles untuk direktori user.
        /// </summary>
        public static async Task<List<UserProfile>> GetAllUsersDirectoryAsync(int limit = 100)
        {
            try
            {
                var http = SharedHttpClient.Instance;
                var cutoffTime = DateTime.UtcNow.AddSeconds(-10);
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?display_name=not.is.null&select=id,device_id,display_name,bio,level,last_seen,avatar_url&order=last_seen.desc.nullslast&limit={limit}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Accept", "application/json");

                var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"[CommunityChatService] GetAllUsersDirectoryAsync failed: {response.StatusCode}");
                    return new List<UserProfile>();
                }

                var content = await response.Content.ReadAsStringAsync();
                var profiles = JsonSerializer.Deserialize<List<UserProfile>>(content, _jsonOptions);
                if (profiles == null) return new List<UserProfile>();

                foreach (var p in profiles)
                {
                    p.IsOnline = p.LastSeen.HasValue && p.LastSeen.Value.ToUniversalTime() >= cutoffTime;
                }
                return profiles.Where(p => !string.IsNullOrWhiteSpace(p.DisplayName)).ToList();
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] GetAllUsersDirectoryAsync error: {ex.Message}");
                return new List<UserProfile>();
            }
        }

        /// <summary>
        /// Mengambil daftar pengguna aktif terbaru dari tabel user_profiles.
        /// </summary>
        public static async Task<List<UserProfile>> GetRecentActiveUsersAsync(int limit = 20)
        {
            try
            {
                var http = SharedHttpClient.Instance;
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/user_profiles?select=id,device_id,display_name,bio,level,last_seen,avatar_url&display_name=not.is.null&order=updated_at.desc&limit={limit}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Accept", "application/json");

                var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return new List<UserProfile>();

                var content = await response.Content.ReadAsStringAsync();
                var profiles = JsonSerializer.Deserialize<List<UserProfile>>(content, _jsonOptions);
                return profiles?.Where(p => !string.IsNullOrWhiteSpace(p.DisplayName)).ToList() ?? new List<UserProfile>();
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] GetRecentActiveUsersAsync error: {ex.Message}");
                return new List<UserProfile>();
            }
        }

        /// <summary>
        /// Retrieves the currently active pinned broadcast announcement (if any).
        /// </summary>
        public static async Task<BroadcastAnnouncement?> GetActiveBroadcastAsync()
        {
            try
            {
                var http = SharedHttpClient.Instance;
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/community_messages?device_id=eq.SYSTEM_BROADCAST&select=*&order=created_at.desc&limit=1";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Accept", "application/json");

                var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                var messages = JsonSerializer.Deserialize<List<ChatMessage>>(content, _jsonOptions);
                var latest = messages?.FirstOrDefault();
                if (latest == null) return null;

                return new BroadcastAnnouncement
                {
                    Id = latest.Id,
                    Title = !string.IsNullOrWhiteSpace(latest.SenderName) ? latest.SenderName : "📢 System Announcement",
                    Message = latest.Message,
                    CreatedAt = latest.CreatedAt
                };
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] GetActiveBroadcastAsync error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Publishes a new pinned broadcast announcement, replacing any existing active broadcast.
        /// </summary>
        public static async Task<(bool Success, string? ErrorMessage, BroadcastAnnouncement? Announcement)> PublishBroadcastAsync(string title, string message)
        {
            string cleanTitle = string.IsNullOrWhiteSpace(title) ? "📢 System Announcement" : title.Trim();
            string cleanMessage = message?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(cleanMessage))
            {
                return (false, "Announcement message content cannot be empty.", null);
            }

            try
            {
                var http = SharedHttpClient.Instance;

                // 1. Delete previous system broadcasts
                await DeleteBroadcastAsync();

                // 2. Insert new pinned announcement
                var url = $"{SupabaseConfig.SupabaseUrl}/rest/v1/community_messages";
                var payload = new Dictionary<string, object>
                {
                    ["device_id"] = "SYSTEM_BROADCAST",
                    ["sender_name"] = cleanTitle,
                    ["message"] = cleanMessage,
                    ["level"] = 0
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");
                request.Headers.Add("Prefer", "return=representation");

                var response = await http.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"[CommunityChatService] PublishBroadcastAsync failed: HTTP {response.StatusCode} - {responseContent}");
                    return (false, "Failed to publish broadcast announcement to server.", null);
                }

                var created = JsonSerializer.Deserialize<List<ChatMessage>>(responseContent, _jsonOptions)?.FirstOrDefault();
                var announcement = created != null ? new BroadcastAnnouncement
                {
                    Id = created.Id,
                    Title = created.SenderName,
                    Message = created.Message,
                    CreatedAt = created.CreatedAt
                } : null;

                return (true, null, announcement);
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] PublishBroadcastAsync exception: {ex.Message}");
                return (false, ex.Message, null);
            }
        }

        /// <summary>
        /// Deletes all pinned broadcast announcements from the server.
        /// </summary>
        public static async Task<(bool Success, string? ErrorMessage)> DeleteBroadcastAsync(long? id = null)
        {
            try
            {
                var http = SharedHttpClient.Instance;
                string url = id.HasValue && id.Value > 0
                    ? $"{SupabaseConfig.SupabaseUrl}/rest/v1/community_messages?id=eq.{id.Value}"
                    : $"{SupabaseConfig.SupabaseUrl}/rest/v1/community_messages?device_id=eq.SYSTEM_BROADCAST";

                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Add("apikey", SupabaseConfig.SupabaseKey);
                request.Headers.Add("Authorization", $"Bearer {SupabaseConfig.SupabaseKey}");

                var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Server returned {response.StatusCode}");
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatService] DeleteBroadcastAsync error: {ex.Message}");
                return (false, ex.Message);
            }
        }
    }
}
