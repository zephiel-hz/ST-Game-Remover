using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamPluginManager.Models;

namespace SteamPluginManager.Services.API
{
    public static class CommunityChatRealtimeClient
    {
        private static ClientWebSocket? _webSocket;
        private static CancellationTokenSource? _cts;
        private static bool _isRunning;
        private static readonly object _lock = new();
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
        private static readonly HashSet<string> _onlineUsers = new(StringComparer.OrdinalIgnoreCase);

        public static event Action<ChatMessage>? OnMessageReceived;
        public static event Action<long>? OnMessageDeleted;
        public static event Action<UserProfile>? OnUserProfileUpdated;
        public static event Action<bool>? OnConnectionStatusChanged;
        public static event Action<HashSet<string>>? OnOnlineUsersChanged;
        public static event Action<string?, string?>? OnUserOffline;

        public static bool IsConnected => _webSocket != null && _webSocket.State == WebSocketState.Open;

        public static bool IsUserOnline(string? username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            lock (_onlineUsers)
            {
                return _onlineUsers.Contains(username.Trim().TrimStart('@'));
            }
        }

        public static void RegisterOnlineUser(string? username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            string clean = username.Trim().TrimStart('@');
            bool changed = false;
            lock (_onlineUsers)
            {
                if (_onlineUsers.Add(clean))
                {
                    changed = true;
                }
            }
            if (changed)
            {
                HashSet<string> copy;
                lock (_onlineUsers) { copy = new HashSet<string>(_onlineUsers, StringComparer.OrdinalIgnoreCase); }
                OnOnlineUsersChanged?.Invoke(copy);
            }
        }

        public static void UnregisterOnlineUser(string? username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            string clean = username.Trim().TrimStart('@');
            bool changed = false;
            lock (_onlineUsers)
            {
                if (_onlineUsers.Remove(clean))
                {
                    changed = true;
                }
            }
            if (changed)
            {
                HashSet<string> copy;
                lock (_onlineUsers) { copy = new HashSet<string>(_onlineUsers, StringComparer.OrdinalIgnoreCase); }
                OnOnlineUsersChanged?.Invoke(copy);
            }
        }

        public static void SetOnlineUsers(IEnumerable<string> users)
        {
            lock (_onlineUsers)
            {
                _onlineUsers.Clear();
                foreach (var u in users)
                {
                    if (!string.IsNullOrWhiteSpace(u))
                    {
                        _onlineUsers.Add(u.Trim().TrimStart('@'));
                    }
                }
            }
            HashSet<string> copy;
            lock (_onlineUsers) { copy = new HashSet<string>(_onlineUsers, StringComparer.OrdinalIgnoreCase); }
            OnOnlineUsersChanged?.Invoke(copy);
        }

        public static HashSet<string> GetOnlineUsersSnapshot()
        {
            lock (_onlineUsers)
            {
                return new HashSet<string>(_onlineUsers, StringComparer.OrdinalIgnoreCase);
            }
        }

        public static void Start()
        {
            lock (_lock)
            {
                if (_isRunning) return;
                _isRunning = true;
                _cts = new CancellationTokenSource();
                Task.Run(() => ConnectionLoopAsync(_cts.Token));
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                _isRunning = false;
                try
                {
                    _cts?.Cancel();
                    _cts?.Dispose();
                }
                catch { }
                _cts = null;

                try
                {
                    if (_webSocket != null)
                    {
                        if (_webSocket.State == WebSocketState.Open)
                        {
                            _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait(1000);
                        }
                        _webSocket.Dispose();
                    }
                }
                catch { }
                _webSocket = null;
                OnConnectionStatusChanged?.Invoke(false);
            }
        }

        public static async Task BroadcastProfileUpdatedAsync(UserProfile profile)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open || profile == null) return;
            try
            {
                var msg = new
                {
                    topic = "realtime:public:community_messages",
                    @event = "broadcast",
                    payload = new
                    {
                        type = "broadcast",
                        @event = "user_profile_updated",
                        record = new
                        {
                            id = profile.Id,
                            device_id = profile.DeviceId,
                            display_name = profile.DisplayName,
                            bio = profile.Bio,
                            avatar_url = profile.AvatarUrl,
                            level = profile.Level,
                            updated_at = DateTime.UtcNow.ToString("o")
                        }
                    },
                    @ref = $"broadcast_prof_{DateTime.UtcNow.Ticks}"
                };
                string json = JsonSerializer.Serialize(msg);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                Logger.Log($"[CommunityChatRealtime] Broadcasted profile update for {profile.DisplayName}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatRealtime] BroadcastProfileUpdatedAsync error: {ex.Message}");
            }
        }

        public static void BroadcastUserOffline(string? username, string? deviceId)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;
            try
            {
                var msg = new
                {
                    topic = "realtime:public:community_messages",
                    @event = "broadcast",
                    payload = new
                    {
                        type = "broadcast",
                        @event = "user_offline",
                        username = username ?? string.Empty,
                        device_id = deviceId ?? string.Empty
                    },
                    @ref = $"broadcast_off_{DateTime.UtcNow.Ticks}"
                };
                string json = JsonSerializer.Serialize(msg);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                _ = _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                Logger.Log($"[CommunityChatRealtime] Broadcasted user offline: {username} ({deviceId})");
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatRealtime] BroadcastUserOffline error: {ex.Message}");
            }
        }

        private static async Task ConnectionLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                try
                {
                    string rawUrl = SupabaseConfig.SupabaseUrl?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(rawUrl))
                    {
                        await Task.Delay(5000, ct);
                        continue;
                    }

                    string wsUrl = rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        ? "wss://" + rawUrl.Substring(8)
                        : (rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            ? "ws://" + rawUrl.Substring(7)
                            : "wss://" + rawUrl);

                    wsUrl = wsUrl.TrimEnd('/') + $"/realtime/v1/websocket?apikey={SupabaseConfig.SupabaseKey}&vsn=1.0.0";

                    using var ws = new ClientWebSocket();
                    _webSocket = ws;

                    Logger.Log("[CommunityChatRealtime] Connecting to Supabase Realtime WebSocket...");
                    await ws.ConnectAsync(new Uri(wsUrl), ct);

                    if (ws.State == WebSocketState.Open)
                    {
                        Logger.Log("[CommunityChatRealtime] Connected to Supabase Realtime!");
                        OnConnectionStatusChanged?.Invoke(true);

                        // Join community_messages channel (INSERT and DELETE events)
                        await JoinChannelAsync(ws, ct);

                        // Start heartbeat task
                        using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var hbTask = Task.Run(() => HeartbeatLoopAsync(ws, hbCts.Token), hbCts.Token);

                        // Receive loop
                        await ReceiveLoopAsync(ws, ct);

                        hbCts.Cancel();
                        try { await hbTask; } catch { }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[CommunityChatRealtime] WebSocket error: {ex.Message}");
                    OnConnectionStatusChanged?.Invoke(false);
                }

                if (!ct.IsCancellationRequested && _isRunning)
                {
                    // Reconnect delay
                    await Task.Delay(3000, ct);
                }
            }

            OnConnectionStatusChanged?.Invoke(false);
        }

        private static async Task JoinChannelAsync(ClientWebSocket ws, CancellationToken ct)
        {
            // 1. Join community_messages (INSERT & DELETE & BROADCAST)
            var joinMessages = new
            {
                topic = "realtime:public:community_messages",
                @event = "phx_join",
                payload = new
                {
                    config = new
                    {
                        broadcast = new { ack = false, self = false },
                        postgres_changes = new object[]
                        {
                            new
                            {
                                @event = "INSERT",
                                schema = "public",
                                table = "community_messages"
                            },
                            new
                            {
                                @event = "DELETE",
                                schema = "public",
                                table = "community_messages"
                            }
                        }
                    }
                },
                @ref = "join_messages"
            };

            string jsonMessages = JsonSerializer.Serialize(joinMessages);
            byte[] bytesMessages = Encoding.UTF8.GetBytes(jsonMessages);
            await ws.SendAsync(new ArraySegment<byte>(bytesMessages), WebSocketMessageType.Text, true, ct);
            Logger.Log("[CommunityChatRealtime] Sent phx_join for community_messages (INSERT & DELETE)");

            // 2. Join user_profiles (INSERT & UPDATE) for realtime name and badge updates
            var joinProfiles = new
            {
                topic = "realtime:public:user_profiles",
                @event = "phx_join",
                payload = new
                {
                    config = new
                    {
                        postgres_changes = new object[]
                        {
                            new
                            {
                                @event = "INSERT",
                                schema = "public",
                                table = "user_profiles"
                            },
                            new
                            {
                                @event = "UPDATE",
                                schema = "public",
                                table = "user_profiles"
                            }
                        }
                    }
                },
                @ref = "join_profiles"
            };

            string jsonProfiles = JsonSerializer.Serialize(joinProfiles);
            byte[] bytesProfiles = Encoding.UTF8.GetBytes(jsonProfiles);
            await ws.SendAsync(new ArraySegment<byte>(bytesProfiles), WebSocketMessageType.Text, true, ct);
            Logger.Log("[CommunityChatRealtime] Sent phx_join for user_profiles (INSERT & UPDATE)");
        }

        private static async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            int hbCount = 0;
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try
                {
                    await Task.Delay(25000, ct);
                    if (ws.State != WebSocketState.Open) break;

                    hbCount++;
                    var hb = new
                    {
                        topic = "phoenix",
                        @event = "heartbeat",
                        payload = new { },
                        @ref = $"hb_{hbCount}"
                    };

                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hb));
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                }
                catch { break; }
            }
        }

        private static async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[8192];
            using var ms = new MemoryStream();

            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                ms.SetLength(0);

                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", ct);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string messageText = Encoding.UTF8.GetString(ms.ToArray());
                    ProcessIncomingMessage(messageText);
                }
            }
        }

        private static void ProcessIncomingMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string topic = root.TryGetProperty("topic", out var topicProp) ? topicProp.GetString() ?? "" : "";
                string eventName = root.TryGetProperty("event", out var ev) ? ev.GetString() ?? "" : "";

                if (!string.Equals(eventName, "postgres_changes", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(eventName, "broadcast", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(eventName, "INSERT", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(eventName, "UPDATE", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(eventName, "DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!root.TryGetProperty("payload", out var payload))
                    return;

                // ==========================================
                // 0. Handle Broadcast Events (Direct WebSocket relay)
                // ==========================================
                if (string.Equals(eventName, "broadcast", StringComparison.OrdinalIgnoreCase) ||
                    (payload.TryGetProperty("type", out var typeCheck) && string.Equals(typeCheck.GetString(), "broadcast", StringComparison.OrdinalIgnoreCase)))
                {
                    string bEvent = "";
                    if (payload.TryGetProperty("event", out var bEv))
                        bEvent = bEv.GetString() ?? "";

                    if (string.Equals(bEvent, "user_profile_updated", StringComparison.OrdinalIgnoreCase))
                    {
                        JsonElement recordElem;
                        if (payload.TryGetProperty("record", out var rec1))
                            recordElem = rec1;
                        else if (payload.TryGetProperty("payload", out var pl) && pl.TryGetProperty("record", out var rec2))
                            recordElem = rec2;
                        else
                            return;

                        var profile = JsonSerializer.Deserialize<UserProfile>(recordElem.GetRawText(), _jsonOptions);
                        if (profile != null && (profile.Id.HasValue || !string.IsNullOrWhiteSpace(profile.DeviceId)))
                        {
                            Logger.Log($"[CommunityChatRealtime] Broadcast UserProfile updated: {profile.DisplayName} (Level: {profile.Level})");
                            OnUserProfileUpdated?.Invoke(profile);
                        }
                        return;
                    }
                    else if (string.Equals(bEvent, "user_offline", StringComparison.OrdinalIgnoreCase))
                    {
                        string? username = payload.TryGetProperty("username", out var u) ? u.GetString() : null;
                        string? devId = payload.TryGetProperty("device_id", out var d) ? d.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(username))
                        {
                            UnregisterOnlineUser(username);
                        }
                        Logger.Log($"[CommunityChatRealtime] Broadcast UserOffline received: {username} ({devId})");
                        OnUserOffline?.Invoke(username, devId);
                        return;
                    }
                }

                string changeType = "";
                string tableName = "";

                if (payload.TryGetProperty("data", out var dataElement))
                {
                    if (dataElement.TryGetProperty("type", out var typeProp))
                        changeType = typeProp.GetString() ?? "";
                    if (dataElement.TryGetProperty("table", out var tableProp))
                        tableName = tableProp.GetString() ?? "";
                }
                else
                {
                    if (payload.TryGetProperty("type", out var rootTypeProp))
                        changeType = rootTypeProp.GetString() ?? "";
                    if (payload.TryGetProperty("table", out var rootTableProp))
                        tableName = rootTableProp.GetString() ?? "";
                }

                if (string.IsNullOrEmpty(tableName))
                {
                    if (topic.Contains("user_profiles", StringComparison.OrdinalIgnoreCase))
                        tableName = "user_profiles";
                    else if (topic.Contains("community_messages", StringComparison.OrdinalIgnoreCase))
                        tableName = "community_messages";
                }

                // ==========================================
                // 1. Table: user_profiles (INSERT & UPDATE)
                // ==========================================
                if (string.Equals(tableName, "user_profiles", StringComparison.OrdinalIgnoreCase))
                {
                    JsonElement recordElement;
                    if (payload.TryGetProperty("data", out var d) && d.TryGetProperty("record", out var rec1))
                    {
                        recordElement = rec1;
                    }
                    else if (payload.TryGetProperty("record", out var rec2))
                    {
                        recordElement = rec2;
                    }
                    else
                    {
                        return;
                    }

                    var profile = JsonSerializer.Deserialize<UserProfile>(recordElement.GetRawText(), _jsonOptions);
                    if (profile != null && (profile.Id.HasValue || !string.IsNullOrWhiteSpace(profile.DeviceId)))
                    {
                        Logger.Log($"[CommunityChatRealtime] PostgresChanges UserProfile updated: {profile.DisplayName} (Level: {profile.Level})");
                        OnUserProfileUpdated?.Invoke(profile);
                    }
                    return;
                }

                // ==========================================
                // 2. Table: community_messages
                // ==========================================
                // Handle DELETE event
                if (string.Equals(changeType, "DELETE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(eventName, "DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    JsonElement oldRec;
                    if (payload.TryGetProperty("data", out var d) && d.TryGetProperty("old_record", out var o1))
                    {
                        oldRec = o1;
                    }
                    else if (payload.TryGetProperty("old_record", out var o2))
                    {
                        oldRec = o2;
                    }
                    else
                    {
                        return;
                    }

                    if (oldRec.TryGetProperty("id", out var idProp) && idProp.TryGetInt64(out long deletedId))
                    {
                        Logger.Log($"[CommunityChatRealtime] Message deleted: {deletedId}");
                        OnMessageDeleted?.Invoke(deletedId);
                    }
                    return;
                }

                // Handle INSERT event
                JsonElement msgRecordElement;
                if (payload.TryGetProperty("data", out var data) && data.TryGetProperty("record", out var mrec1))
                {
                    msgRecordElement = mrec1;
                }
                else if (payload.TryGetProperty("record", out var mrec2))
                {
                    msgRecordElement = mrec2;
                }
                else
                {
                    return;
                }

                var msg = JsonSerializer.Deserialize<ChatMessage>(msgRecordElement.GetRawText(), _jsonOptions);
                if (msg != null && msg.Id > 0 && !string.IsNullOrWhiteSpace(msg.Message))
                {
                    if (!string.IsNullOrWhiteSpace(msg.SenderName))
                    {
                        RegisterOnlineUser(msg.SenderName);
                    }
                    OnMessageReceived?.Invoke(msg);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[CommunityChatRealtime] ProcessIncomingMessage error: {ex.Message}");
            }
        }
    }
}
