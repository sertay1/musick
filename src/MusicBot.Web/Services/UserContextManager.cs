using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Services;
using MusicBot.Data;
using MusicBot.Hubs;
using MusicBot.Services.Player;
using MusicBot.Services.Spotify;

namespace MusicBot.Services;

public class UserContextManager
{
    private readonly ConcurrentDictionary<Guid, UserServices> _users = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _configuration;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly QueueSettingsService? _queueSettings;
    private readonly BannedSongService? _banned;

    public event Action<Guid, UserServices>? OnUserCreated;
    public event Action<Guid>? OnUserRemoved;

    public UserContextManager(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        IHubContext<OverlayHub> hub, QueueSettingsService? queueSettings = null, BannedSongService? banned = null)
    {
        _queueSettings = queueSettings;
        _banned = banned;
        _scopeFactory  = scopeFactory;
        _httpFactory   = httpFactory;
        _loggerFactory = loggerFactory;
        _configuration = configuration;
        _hub           = hub;
    }

    public UserServices GetOrCreate(Guid userId)
    {
        return _users.GetOrAdd(userId, id =>
        {
            var queueConfig = _configuration.GetSection("Queue");
            var maxSizeEnabled = queueConfig.GetValue("MaxQueueSizeEnabled", queueConfig.GetValue("MaxSizeEnabled", true));
            var maxSize     = maxSizeEnabled ? queueConfig.GetValue("MaxSize", 50) : 0;
            var maxPerUser  = queueConfig.GetValue("MaxSongsPerUserEnabled", true)
                ? queueConfig.GetValue("MaxSongsPerUser", 10) : 0;
            var queue       = new QueueService(0, 0, uri => _banned?.IsBanned(uri) == true);

            var spotifySettings = _configuration.GetSection("Spotify");
            var settings = new SpotifySettings
            {
                ClientId     = spotifySettings["ClientId"]     ?? "",
                ClientSecret = spotifySettings["ClientSecret"] ?? "",
                RedirectUri  = spotifySettings["RedirectUri"]  ?? ""
            };

            var spotify = new SpotifyService(
                Options.Create(settings),
                _httpFactory,
                _loggerFactory.CreateLogger<SpotifyService>(),
                _scopeFactory,
                _hub,
                id,
                Path.Combine(_configuration["DataDirectory"] ?? AppContext.BaseDirectory, "spotify-cooldown-" + id.ToString("N") + ".txt"));

            var player = new HybridPlayerService(
                _loggerFactory.CreateLogger<LocalPlayerService>(), _hub, _queueSettings);

            var services = new UserServices(id, queue, spotify, player);
            OnUserCreated?.Invoke(id, services);
            return services;
        });
    }

    public UserServices? Get(Guid userId)
    {
        return _users.TryGetValue(userId, out var services) ? services : null;
    }

    public IEnumerable<(Guid UserId, UserServices Services)> GetAllActive()
    {
        return _users.Select(kv => (kv.Key, kv.Value));
    }

    public void Remove(Guid userId)
    {
        if (_users.TryRemove(userId, out var services))
        {
            services.Player.Dispose();
            OnUserRemoved?.Invoke(userId);
        }
    }

    public async Task<UserServices?> GetBySlugAsync(string slug)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Slug == slug);
        return user != null ? GetOrCreate(user.Id) : null;
    }

    public async Task<UserServices?> GetByOverlayTokenAsync(string overlayToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.OverlayToken == overlayToken);
        return user != null ? GetOrCreate(user.Id) : null;
    }
}
