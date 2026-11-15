using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicBot.Data;

namespace MusicBot.Services.Player;

public sealed class SpotifyAudioRestoreService(IServiceScopeFactory scopes, ILogger<SpotifyAudioRestoreService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var applied = new HashSet<int>();
        string? lastConfig = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
                var config = await db.PlatformConfigs.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "spotify_output", stoppingToken);
                if (config != null)
                {
                    if (lastConfig != config.ConfigJson) { applied.Clear(); lastConfig = config.ConfigJson; }
                    var active = SpotifyAudioRouting.SpotifyProcesses().Concat(SpotifyAudioRouting.AppAudioProcesses()).Distinct().ToArray();
                    applied.IntersectWith(active);
                    var pending = active.Where(id => !applied.Contains(id)).ToArray();
                    if (pending.Length > 0)
                    {
                        SpotifyAudioRouting.Apply(JsonSerializer.Deserialize<string?>(config.ConfigJson), pending);
                        applied.UnionWith(pending);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogDebug(ex, "Spotify audio routing will be retried"); }
            try { await Task.Delay(10000, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
