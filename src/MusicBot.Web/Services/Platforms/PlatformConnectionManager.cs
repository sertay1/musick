using System.Collections.Concurrent;
using MusicBot.Core.Models;
using Microsoft.AspNetCore.SignalR;
using MusicBot.Hubs;
using MusicBot.Services.Commands;

namespace MusicBot.Services.Platforms;

public class PlatformConnectionManager
{
    public record KickPlatformConfig(string Channel, string[]? CommandRoles = null, string[]? AllowedUsers = null);

    public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }

    public class ConnectionState
    {
        public ConnectionStatus Status { get; set; } = ConnectionStatus.Disconnected;
        public string? ErrorMessage { get; set; }
        public CancellationTokenSource? Cts { get; set; }
    }

    private readonly ConcurrentDictionary<string, ConnectionState> _states = new();
    private readonly ConcurrentDictionary<Guid, KickPlatformConfig> _kickConfigs = new();
    private readonly ConcurrentDictionary<string, DateTime> _deniedNotifiedAt = new();
    private static readonly TimeSpan DeniedCooldown = TimeSpan.FromSeconds(60);

    private readonly CommandRouterService _router;
    private readonly UserContextManager _userContext;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly IntegrationStatusTracker _tracker;
    private readonly ChatResponseService _chat;
    private readonly ChatActivityTracker _activity;
    private readonly KickAuthService _kickAuth;
    private readonly IKickChatReader _chatReader;
    private readonly CommandConfigService _cmdConfig;
    private readonly ILogger<PlatformConnectionManager> _logger;

    private readonly ConcurrentDictionary<Guid, string> _slugCache = new();

    public PlatformConnectionManager(
        CommandRouterService router,
        UserContextManager userContext,
        IHubContext<OverlayHub> hub,
        IntegrationStatusTracker tracker,
        ChatResponseService chat,
        ChatActivityTracker activity,
        KickAuthService kickAuth,
        IKickChatReader chatReader,
        CommandConfigService cmdConfig,
        ILogger<PlatformConnectionManager> logger)
    {
        _router      = router;
        _userContext = userContext;
        _hub         = hub;
        _tracker     = tracker;
        _chat        = chat;
        _activity    = activity;
        _kickAuth    = kickAuth;
        _chatReader  = chatReader;
        _cmdConfig   = cmdConfig;
        _logger      = logger;
    }

    private static string Key(Guid userId, string platform) => $"{userId}:{platform.ToLower()}";

    public ConnectionState GetState(Guid userId, string platform)
    {
        return _states.GetOrAdd(Key(userId, platform), _ => new ConnectionState());
    }

    public void ConnectKick(Guid userId, KickPlatformConfig config)
    {
        var key = Key(userId, "kick");
        Disconnect(userId, "kick");

        _kickConfigs[userId] = config;

        var cts = new CancellationTokenSource();
        var state = _states.AddOrUpdate(key, _ => new ConnectionState { Cts = cts }, (_, s) => { s.Cts = cts; return s; });
        state.Status = ConnectionStatus.Connecting;
        state.ErrorMessage = null;
        SetStatus(key, ConnectionStatus.Connecting);

        _ = Task.Run(() => RunWithReconnect(key, ct => KickLoop(userId, config, ct), cts.Token));
    }

    public void UpdateKickSettings(Guid userId, string[] commandRoles, string[] allowedUsers)
    {
        if (!_kickConfigs.TryGetValue(userId, out var current))
        {
            _logger.LogDebug("UpdateKickSettings: no active Kick connection for user {UserId}", userId);
            return;
        }

        _kickConfigs[userId] = current with
        {
            CommandRoles = commandRoles,
            AllowedUsers = allowedUsers,
        };
        ClearDeniedCooldownsForPlatform("kick");
        _logger.LogInformation("Kick live config updated: roles=[{Roles}]", string.Join(",", commandRoles));
    }

    public void Disconnect(Guid userId, string platform = "kick")
    {
        var key = Key(userId, platform);
        if (_states.TryRemove(key, out var state))
        {
            try
            {
                state.Cts?.Cancel();
                state.Cts?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cancelling {Key}", key);
            }
        }

        _chat.RegisterSender("kick", null);
        _kickConfigs.TryRemove(userId, out _);
        SetStatus(key, ConnectionStatus.Disconnected);
    }

    private async Task RunWithReconnect(string key, Func<CancellationToken, Task> loop, CancellationToken ct)
    {
        const int maxBackoffSec = 30;
        var backoffSec = 2;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await loop(ct);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Key}] Connection error, reconnecting in {Sec}s...", key, backoffSec);
                SetStatus(key, ConnectionStatus.Error, ex.Message);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoffSec = Math.Min(backoffSec * 2, maxBackoffSec);
                SetStatus(key, ConnectionStatus.Connecting);
            }
        }

        SetStatus(key, ConnectionStatus.Disconnected);
    }

    private async Task KickLoop(Guid userId, KickPlatformConfig config, CancellationToken ct)
    {
        var key = Key(userId, "kick");

        void OnMessage(KickChatMessage msg)
        {
            var content = msg.Content?.Trim();
            if (string.IsNullOrEmpty(content)) return;
            var sender = msg.SenderUsername;
            _activity.RecordMessage(sender);

            if (content[0] is ('!' or '.' or '/'))
            {
                _ = RouteCommand(userId, sender, content, "kick", msg);
            }
        }

        _chatReader.OnMessageReceived += OnMessage;

        try
        {
            _logger.LogInformation("Kick connecting to channel {Channel}", config.Channel);
            await _chatReader.ConnectAsync(config.Channel, ct);
            _activity.SetIgnored(config.Channel, true);
            SetStatus(key, ConnectionStatus.Connected);
            _logger.LogInformation("Kick connected to channel {Channel}", config.Channel);

            if (_kickAuth.IsAuthenticated)
            {
                _ = _kickAuth.ResolveBroadcasterUserIdAsync(config.Channel);
                _chat.RegisterSender("kick", async msg => await _kickAuth.SendChatMessageAsync(msg));
                _chat.GuardedKickSender = (msg, current) => _kickAuth.SendChatMessageAsync(msg, current);
                _logger.LogInformation("Kick chat sender registered for channel {Channel}", config.Channel);
            }

            while (!ct.IsCancellationRequested && _chatReader.IsConnected)
            {
                await Task.Delay(1000, ct);
            }

            if (!ct.IsCancellationRequested)
            {
                throw new IOException($"Kick chat connection to '{config.Channel}' was lost unexpectedly");
            }
        }
        finally
        {
            _chatReader.OnMessageReceived -= OnMessage;
            _activity.SetIgnored(config.Channel, false);
            _chat.RegisterSender("kick", null); _chat.GuardedKickSender = null;
            await _chatReader.DisconnectAsync();
        }
    }

    private static bool IsInAllowList(string? username, string[]? allowedUsers)
        => !string.IsNullOrEmpty(username)
           && allowedUsers != null
           && allowedUsers.Any(u => string.Equals(u.TrimStart('@'), username, StringComparison.OrdinalIgnoreCase));

    private bool IsKickRoleAllowed(KickChatMessage msg, string[]? roles, string[]? allowedUsers, string? channel)
    {
        var username = msg.SenderUsername?.TrimStart('@');
        var channelClean = channel?.TrimStart('@');
        var botClean = _kickAuth.ChannelName?.TrimStart('@');

        // Broadcaster and bot account are ALWAYS exempt
        if (!string.IsNullOrEmpty(username))
        {
            if (!string.IsNullOrEmpty(channelClean) && string.Equals(username, channelClean, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(botClean) && string.Equals(username, botClean, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var badges = msg.Badges.Select(b => b.ToLowerInvariant()).ToHashSet();
        if (badges.Contains("broadcaster") || badges.Contains("streamer") || badges.Contains("channel_owner") || badges.Contains("moderator"))
            return true;

        if (roles == null || roles.Length == 0 || roles.Contains("all")) return true;

        var listMatch = roles.Contains("list") && IsInAllowList(msg.SenderUsername, allowedUsers);
        if (listMatch) return true;

        if (roles.Contains("moderator") && badges.Contains("moderator")) return true;
        if (roles.Contains("vip") && (badges.Contains("vip") || badges.Contains("og"))) return true;
        if (roles.Contains("og") && badges.Contains("og")) return true;
        if (roles.Contains("subscriber") && (badges.Contains("subscriber") || badges.Contains("founder") || badges.Contains("sub"))) return true;

        _logger.LogInformation(
            "Kick role DENIED for @{User} | roles=[{Roles}] | badges=[{Badges}] | ListMatch={LM}",
            msg.SenderUsername, string.Join(",", roles ?? []), string.Join(",", msg.Badges), listMatch);

        return false;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _viewerCommands = new(StringComparer.OrdinalIgnoreCase);
    private async Task<CommandResult?> RouteCommand(Guid userId, string username, string message, string platform, KickChatMessage msg)
    {
        var gate = _viewerCommands.GetOrAdd($"{userId}:{platform}:{username}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await RouteCommandCore(userId, username, message, platform, msg); }
        finally { gate.Release(); }
    }

    private async Task<CommandResult?> RouteCommandCore(Guid userId, string username, string message, string platform, KickChatMessage msg)
    {
        var parts = message.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        ActivityJournal.Record("kick", "Komut", username, message);
        var rawTrigger = parts[0][1..].ToLowerInvariant(); // strip prefix char (! . /)
        var args  = parts.Length > 1 ? parts[1].Trim() : "";

        bool isCustom = false;
        // Resolve canonical command definition from commands.json
        if (!_cmdConfig.TryResolveCommand(rawTrigger, out var commandKey, out var cmdDef) || cmdDef == null)
        {
            if (!_cmdConfig.TryResolveCustomCommand(rawTrigger, out var custom) || custom == null) return null;
            isCustom = true;
            commandKey = custom.Name;
            cmdDef = new MusicBot.Core.Models.CommandDefinition
            {
                Name = custom.Name, Enabled = custom.Enabled, RequiredRole = custom.RequiredRole,
                AllowedRoles = custom.AllowedRoles, CooldownSeconds = custom.CooldownSeconds,
                RoleCooldowns = custom.RoleCooldowns, ExemptRoles = custom.ExemptRoles
            };
        }

        if (!cmdDef.Enabled)
        {
            _logger.LogDebug("[{Platform}] Command '{CommandKey}' is disabled", platform, commandKey);
            return null;
        }

        if (!_kickConfigs.TryGetValue(userId, out var liveCfg)) return null;

        var isBroadcasterOrBot = string.Equals(username.TrimStart('@'), liveCfg.Channel?.TrimStart('@'), StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(username.TrimStart('@'), _kickAuth.ChannelName?.TrimStart('@'), StringComparison.OrdinalIgnoreCase)
                                 || msg.Badges.Any(b => b.Equals("broadcaster", StringComparison.OrdinalIgnoreCase) || b.Equals("streamer", StringComparison.OrdinalIgnoreCase));

        // Channel allowlist check if configured
        if (!isBroadcasterOrBot && liveCfg.CommandRoles?.Contains("list") == true && !IsInAllowList(username, liveCfg.AllowedUsers))
        {
            await NotifyPermissionDeniedAsync(platform, username, cmdDef);
            return null;
        }

        // Role check against command's required role
        if (!CommandConfigService.IsCommandAllowed(cmdDef.RequiredRole, cmdDef.AllowedRoles, msg.Badges, username, liveCfg.Channel, _kickAuth.ChannelName))
        {
            await NotifyPermissionDeniedAsync(platform, username, cmdDef);
            return null;
        }

        var roleProfile = _cmdConfig.GetRoleProfile(msg.Badges, username, liveCfg.Channel, _kickAuth.ChannelName);

        // Check if command is a song request and role is disallowed from requesting
        if (commandKey is "play" or "sr" or "istek")
        {
            if (cmdDef.AllowedRoles == null && (!IsKickRoleAllowed(msg, liveCfg.CommandRoles, liveCfg.AllowedUsers, liveCfg.Channel) || !roleProfile.CanRequestSongs))
            {
                var denyMsg = $"@{username}, şarkı isteme yetkiniz bulunmuyor.";
                await _chat.SendChatMessageAsync(username, denyMsg, platform);
                return CommandResult.Fail(denyMsg);
            }
        }

        // All commands use the same explicit per-role cooldown rules.
        var baseCooldown = commandKey == "play" ? roleProfile.CooldownSeconds : cmdDef.CooldownSeconds;
        var exemptions = commandKey == "play" && roleProfile.BypassCooldown
            ? new List<string> { roleProfile.Role } : cmdDef.ExemptRoles;
        if (!isCustom && !_cmdConfig.CheckCooldownWithRole(commandKey, username, baseCooldown, cmdDef.RoleCooldowns,
            exemptions, msg.Badges, liveCfg.Channel, out var remainingSeconds, _kickAuth.ChannelName))
        {
            var text = Core.Services.CommandTemplateEngine.Render(cmdDef.GetTemplate("cooldown", "@{user}, {wait_time} bekleyin."),
                new Core.Services.TemplateContext { User = username, WaitTime = $"{remainingSeconds}s" });
            await _chat.SendChatMessageAsync(username, text, platform);
            return CommandResult.Fail(text);
        }

        var slug = await GetUserSlugAsync(userId);
        if (slug == null)
        {
            _logger.LogWarning("[{Platform}] No slug cached for user {UserId} — call SetUserSlug first", platform, userId);
            return null;
        }

        var services = _userContext.GetOrCreate(userId);
        if (services == null)
        {
            _logger.LogWarning("[{Platform}] No services found for slug '{Slug}'", platform, slug);
            return null;
        }

        var command = new BotCommand
        {
            Type = commandKey,
            Query = args,
            RequestedBy = username,
            Platform = platform,
            Channel = liveCfg.Channel,
            Badges = msg.Badges.ToList(),
            Definition = cmdDef
        };

        var result = await _router.HandleAsync(command, services);
        if (!isCustom && result?.Success == true) _cmdConfig.RecordSuccessfulCommand(commandKey, username);
        if (result == null || string.IsNullOrWhiteSpace(result.Message))
            return result;

        _logger.LogInformation("[{Platform}] [{Cmd}] @{User}: {Result}", platform, rawTrigger, username, result.Message);

        _ = _hub.Clients.Group($"user:{userId}")
            .SendAsync("integration:event", new
            {
                source   = platform,
                type     = "play",
                platform = "Kick",
                user     = username,
                query    = command.Query ?? "",
                success  = result.Success,
                message  = result.Message,
            });

        await _chat.SendChatMessageAsync(username, result.Message, platform);

        return result;
    }

    private Task NotifyPermissionDeniedAsync(string platform, string username, CommandDefinition? cmdDef = null)
    {
        var key = $"{platform}:{username.ToLowerInvariant()}";
        var now = DateTime.UtcNow;

        if (_deniedNotifiedAt.TryGetValue(key, out var last) && now - last < DeniedCooldown)
            return Task.CompletedTask;

        _deniedNotifiedAt[key] = now;
        var template = cmdDef?.GetTemplate("permission_denied", "@{user}, bu komutu kullanma yetkiniz yok.")
            ?? "@{user}, bu komutu kullanma yetkiniz yok.";
        var msg = Core.Services.CommandTemplateEngine.Render(template, new Core.Services.TemplateContext { User = username });
        return _chat.SendChatMessageAsync(username, msg, platform);
    }

    private void ClearDeniedCooldownsForPlatform(string platform)
    {
        var prefix = $"{platform}:";
        foreach (var k in _deniedNotifiedAt.Keys.Where(k => k.StartsWith(prefix)))
            _deniedNotifiedAt.TryRemove(k, out _);
    }

    private void SetStatus(string key, ConnectionStatus status, string? message = null)
    {
        if (_states.TryGetValue(key, out var s))
        {
            s.Status       = status;
            s.ErrorMessage = message;
        }

        var parts = key.Split(':', 2);
        if (parts.Length == 2)
        {
            var platform = parts[1];
            var statusStr = status.ToString().ToLower();

            if (platform == "kick")
            {
                if (_tracker.KickStatus != statusStr)
                    ActivityJournal.Record("kick", "Bağlantı", "", statusStr switch
                    {
                        "connected" => "Kick sohbetine bağlandı.", "connecting" => "Kick bağlantısı kuruluyor…",
                        "disconnected" => "Kick bağlantısı kapandı.", "error" => "Kick bağlantısında hata oluştu.", _ => statusStr
                    }, statusStr != "error");
                _tracker.KickStatus = statusStr;
            }

            _ = _hub.Clients.Group($"user:{parts[0]}")
                .SendAsync("integration:status", new { source = platform, status = statusStr });
        }
    }

    public void SetUserSlug(Guid userId, string slug) => _slugCache[userId] = slug;

    private Task<string?> GetUserSlugAsync(Guid userId)
        => Task.FromResult(_slugCache.TryGetValue(userId, out var slug) ? slug : null);
}
