using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MusicBot.Services.Platforms;

/// <summary>
/// Native Pusher WebSocket reader for Kick live chat.
/// Replaces the outdated KickChatSpy NuGet library with direct Pusher protocol implementation
/// supporting current and rotating Pusher app keys.
/// </summary>
public class KickChatSpyReader : IKickChatReader
{
    private static readonly string[] PusherKeys =
    [
        "34bf7a0ff419a2a775b9", // Current active Kick Pusher key
        "eb1d5f283ac4084b60b1",
        "32cbd69e4b950bf97679"
    ];

    private const string PusherCluster = "us2";

    private readonly ILogger<KickChatSpyReader> _logger;
    private readonly HttpClient _http;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private volatile bool _subscribed;
    private string? _currentChannel;
    private int _chatroomId;

    public event Action<KickChatMessage>? OnMessageReceived;
    public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open && _subscribed;

    public KickChatSpyReader(ILogger<KickChatSpyReader> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task ConnectAsync(string channel, CancellationToken ct = default)
    {
        await DisconnectAsync();

        _currentChannel = channel.Trim().TrimStart('@');
        _cts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        _logger.LogInformation("Resolving chatroom ID for Kick channel '{Channel}'...", _currentChannel);
        _chatroomId = await ResolveChatroomIdAsync(_currentChannel, linkedCts.Token);
        if (_chatroomId <= 0)
        {
            throw new InvalidOperationException($"Could not resolve chatroom ID for Kick channel '{_currentChannel}'");
        }

        _logger.LogInformation("Kick channel '{Channel}' chatroom ID: {Id}", _currentChannel, _chatroomId);

        Exception? lastEx = null;
        foreach (var key in PusherKeys)
        {
            try
            {
                await ConnectWithKeyAsync(key, _chatroomId, linkedCts.Token);
                _logger.LogInformation("Kick chat successfully connected to channel '{Channel}' (Chatroom {Id})", _currentChannel, _chatroomId);
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger.LogWarning("Failed to connect Kick chat using Pusher key {Key}: {Message}", key, ex.Message);
                await CleanupWebSocketAsync();
            }
        }

        throw lastEx ?? new InvalidOperationException("Failed to connect to Kick chatroom with available Pusher keys");
    }

    private async Task ConnectWithKeyAsync(string pusherKey, int chatroomId, CancellationToken ct)
    {
        var uri = new Uri($"wss://ws-{PusherCluster}.pusher.com/app/{pusherKey}?protocol=7&client=js&version=8.4.0-rc2&flash=false");
        var ws = new ClientWebSocket();
        _ws = ws;
        _subscribed = false;

        await ws.ConnectAsync(uri, ct);

        // Start receive loop
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled());

        _receiveTask = Task.Run(() => ReceiveLoopAsync(ws, chatroomId, tcs, _cts!.Token));

        // Wait for subscription confirmation or failure
        var subSuccess = await tcs.Task;
        if (!subSuccess)
        {
            throw new IOException("Pusher subscription to Kick chatroom was rejected or closed");
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, int chatroomId, TaskCompletionSource<bool> subTcs, CancellationToken ct)
    {
        var buffer = new byte[16384];
        var ms = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Kick Pusher WebSocket closed by server ({Status}: {Desc})", ws.CloseStatus, ws.CloseStatusDescription);
                    break;
                }

                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);

                    await ProcessPusherMessageAsync(ws, json, chatroomId, subTcs, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Kick chat receive loop error: {Message}", ex.Message);
        }
        finally
        {
            _subscribed = false;
            subTcs.TrySetResult(false);
            ms.Dispose();
        }
    }

    private async Task ProcessPusherMessageAsync(
        ClientWebSocket ws,
        string json,
        int chatroomId,
        TaskCompletionSource<bool> subTcs,
        CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("event", out var evProp))
                return;

            var evName = evProp.GetString();

            if (evName == "pusher:ping")
            {
                var pong = JsonSerializer.Serialize(new { @event = "pusher:pong", data = new { } });
                await ws.SendAsync(Encoding.UTF8.GetBytes(pong), WebSocketMessageType.Text, true, ct);
                return;
            }

            if (evName == "pusher:connection_established")
            {
                // Subscribe to chatroom channel
                var subPayload = JsonSerializer.Serialize(new
                {
                    @event = "pusher:subscribe",
                    data = new { auth = "", channel = $"chatrooms.{chatroomId}.v2" }
                });
                await ws.SendAsync(Encoding.UTF8.GetBytes(subPayload), WebSocketMessageType.Text, true, ct);
                return;
            }

            if (evName == "pusher_internal:subscription_succeeded")
            {
                _subscribed = true;
                subTcs.TrySetResult(true);
                _logger.LogInformation("Kick chatroom subscription confirmed for chatroom {Id}", chatroomId);
                return;
            }

            if (evName == "pusher:error")
            {
                _logger.LogWarning("Kick Pusher error event received: {Payload}", json);
                subTcs.TrySetResult(false);
                return;
            }

            // Chat message event: App\Events\ChatMessageEvent
            if (evName is "App\\Events\\ChatMessageEvent" or "ChatMessageEvent")
            {
                ParseAndDispatchChatMessage(root);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error handling Pusher message: {Json}", json);
        }
    }

    private void ParseAndDispatchChatMessage(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("data", out var dataProp)) return;

            string dataJson;
            if (dataProp.ValueKind == JsonValueKind.String)
            {
                dataJson = dataProp.GetString() ?? "";
            }
            else
            {
                dataJson = dataProp.GetRawText();
            }

            if (string.IsNullOrWhiteSpace(dataJson)) return;

            using var messageDoc = JsonDocument.Parse(dataJson);
            var msgRoot = messageDoc.RootElement;

            var content = msgRoot.TryGetProperty("content", out var cProp) ? cProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(content)) return;

            string username = "viewer";
            var badges = new List<string>();

            if (msgRoot.TryGetProperty("sender", out var senderProp))
            {
                if (senderProp.TryGetProperty("username", out var uProp))
                    username = uProp.GetString() ?? "viewer";
                else if (senderProp.TryGetProperty("slug", out var sProp))
                    username = sProp.GetString() ?? "viewer";

                if (senderProp.TryGetProperty("identity", out var identProp) &&
                    identProp.TryGetProperty("badges", out var badgesProp) &&
                    badgesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var badge in badgesProp.EnumerateArray())
                    {
                        if (badge.TryGetProperty("type", out var typeProp))
                        {
                            var bType = typeProp.GetString();
                            if (!string.IsNullOrEmpty(bType))
                                badges.Add(bType.ToLowerInvariant());
                        }
                    }
                }
            }

            OnMessageReceived?.Invoke(new KickChatMessage(username, content, badges));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error parsing Kick chat message");
        }
    }

    private async Task<int> ResolveChatroomIdAsync(string channel, CancellationToken ct)
    {
        if (int.TryParse(channel, out var directId))
            return directId;

        // Strategy 2: Kick v2 channels API
        try
        {
            var v2Url = $"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channel)}";
            var res = await _http.GetStringAsync(v2Url, ct);
            using var doc = JsonDocument.Parse(res);
            if (doc.RootElement.TryGetProperty("chatroom", out var chatroomProp) &&
                chatroomProp.TryGetProperty("id", out var idProp))
            {
                return idProp.GetInt32();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Chatroom lookup via kick v2 API failed: {Msg}", ex.Message);
        }

        // Strategy 3: Kick v1 channels API
        try
        {
            var v1Url = $"https://kick.com/api/v1/channels/{Uri.EscapeDataString(channel)}";
            var res = await _http.GetStringAsync(v1Url, ct);
            using var doc = JsonDocument.Parse(res);
            if (doc.RootElement.TryGetProperty("chatroom", out var chatroomProp) &&
                chatroomProp.TryGetProperty("id", out var idProp))
            {
                return idProp.GetInt32();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Chatroom lookup via kick v1 API failed: {Msg}", ex.Message);
        }

        return 0;
    }

    public async Task DisconnectAsync()
    {
        _subscribed = false;
        if (_cts != null)
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
            _cts = null;
        }

        await CleanupWebSocketAsync();

        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { }
            _receiveTask = null;
        }
    }

    private async Task CleanupWebSocketAsync()
    {
        if (_ws != null)
        {
            try
            {
                if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", closeCts.Token);
                }
            }
            catch { }
            finally
            {
                _ws.Dispose();
                _ws = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
