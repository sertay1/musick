using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicBot.Data;

namespace MusicBot.Services.Platforms;

/// <summary>
/// On startup, auto-connects Kick if AutoConnect = true in the database.
/// </summary>
public class PlatformAutoConnectService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PlatformConnectionManager _manager;
    private readonly KickAuthService _kickAuth;
    private readonly ILogger<PlatformAutoConnectService> _logger;

    public PlatformAutoConnectService(
        IServiceScopeFactory scopeFactory,
        PlatformConnectionManager manager,
        KickAuthService kickAuth,
        ILogger<PlatformAutoConnectService> logger)
    {
        _scopeFactory = scopeFactory;
        _manager      = manager;
        _kickAuth     = kickAuth;
        _logger       = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _ = Task.Run(() => ConnectAllAsync(ct), ct);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task ConnectAllAsync(CancellationToken ct)
    {
        // Brief delay so the rest of the app finishes starting
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            var configs = await db.PlatformConfigs
                .Where(p => p.UserId == LocalUser.Id && p.Platform == "kick")
                .ToListAsync(ct);

            if (configs.Count == 0)
            {
                if (_kickAuth.IsAuthenticated && !string.IsNullOrWhiteSpace(_kickAuth.ChannelName))
                {
                    _manager.SetUserSlug(LocalUser.Id, LocalUser.Slug);
                    _manager.ConnectKick(LocalUser.Id, new PlatformConnectionManager.KickPlatformConfig(_kickAuth.ChannelName, null, null));
                }
                return;
            }

            _manager.SetUserSlug(LocalUser.Id, LocalUser.Slug);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            foreach (var cfg in configs)
            {
                try
                {
                    if (!_kickAuth.IsAuthenticated)
                    {
                        _logger.LogWarning("Auto-connect: Kick account not linked — skipping");
                        continue;
                    }
                    var c = JsonSerializer.Deserialize<KickJson>(cfg.ConfigJson, opts);
                    var channel = !string.IsNullOrWhiteSpace(c?.Channel) ? c.Channel : (_kickAuth.ChannelName ?? "");
                    if (string.IsNullOrWhiteSpace(channel)) continue;
                    _manager.ConnectKick(LocalUser.Id, new PlatformConnectionManager.KickPlatformConfig(channel, c?.CommandRoles, c?.AllowedUsers));
                    _logger.LogInformation("Auto-connect: Kick #{Channel}", channel);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-connect failed for Kick");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlatformAutoConnectService error");
        }
    }

    private sealed class KickJson
    {
        public string? Channel { get; set; }
        public string[]? CommandRoles { get; set; }
        public string[]? AllowedUsers { get; set; }
    }
}
