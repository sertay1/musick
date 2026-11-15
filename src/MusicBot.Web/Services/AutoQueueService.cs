using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Models;
using MusicBot.Data;

namespace MusicBot.Services;

public class AutoQueueService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutoQueueService> _logger;
    private string? _lastUri;
    private readonly BannedSongService? _banned;
    private readonly QueueSettingsService? _settings;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _unavailable = new();
    public void DeferUnavailable(string uri, TimeSpan? delay = null) =>
        _unavailable[uri] = DateTimeOffset.UtcNow + (delay ?? TimeSpan.FromMinutes(2));
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly SemaphoreSlim _selection = new(1, 1);
    private readonly Queue<AutoQueueSong> _bag = new();
    private long _version, _bagVersion = -1;

    public AutoQueueService(IServiceScopeFactory scopeFactory, ILogger<AutoQueueService> logger, BannedSongService? banned = null, QueueSettingsService? settings = null)
    {
        _banned = banned; _settings = settings;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task<List<AutoQueueSong>> GetAllAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.AutoQueueSongs.OrderBy(s => s.AddedAt).ToListAsync();
    }

    public async Task<int> CountAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.AutoQueueSongs.CountAsync();
    }

    public async Task<Song?> GetRandomSongAsync(IReadOnlyCollection<string>? excludedUris = null)
    {
        await _selection.WaitAsync();
        try
        {
        var version = Interlocked.Read(ref _version);
        if (_bag.Count == 0 || _bagVersion != version)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            var all = await db.AutoQueueSongs.AsNoTracking().ToListAsync();
            for (var i = all.Count - 1; i > 0; i--) { var j = Random.Shared.Next(i + 1); (all[i], all[j]) = (all[j], all[i]); }
            if (all.Count > 1 && all[0].SpotifyUri == _lastUri) (all[0], all[1]) = (all[1], all[0]);
            _bag.Clear();
            foreach (var item in all) _bag.Enqueue(item);
            _bagVersion = version;
        }
        AutoQueueSong? entry = null;
        while (_bag.TryDequeue(out var candidate))
        {
            if (excludedUris?.Contains(candidate.SpotifyUri) == true || _banned?.IsBanned(candidate.SpotifyUri) == true) continue;
            if (_settings != null && (candidate.SpotifyUri.StartsWith("spotify:") ? !_settings.SpotifyEnabled : !_settings.YouTubeEnabled)) continue;
            if (_unavailable.TryGetValue(candidate.SpotifyUri, out var until))
            {
                if (until > DateTimeOffset.UtcNow) continue;
                _unavailable.TryRemove(candidate.SpotifyUri, out _);
            }
            entry = candidate;
            break;
        }
        if (entry == null) return null;
        _lastUri = entry.SpotifyUri;
        return new Song
        {
            SpotifyUri = entry.SpotifyUri,
            Title      = entry.Title,
            Artist     = entry.Artist,
            CoverUrl   = entry.CoverUrl,
            DurationMs = entry.DurationMs,
        };
        }
        finally { _selection.Release(); }
    }

    public async Task<bool> AddAsync(Song song) => await BulkAddAsync(new[] { song }) > 0;

    public async Task<int> BulkAddAsync(IEnumerable<Song> songs)
    {
        await _writes.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            var known = (await db.AutoQueueSongs.Select(s => s.SpotifyUri).ToListAsync()).ToHashSet();
            int added = 0;
            foreach (var song in songs)
            {
                if (string.IsNullOrWhiteSpace(song.SpotifyUri) || !known.Add(song.SpotifyUri)) continue;
                db.AutoQueueSongs.Add(new AutoQueueSong { SpotifyUri = song.SpotifyUri, Title = song.Title, Artist = song.Artist,
                    CoverUrl = song.CoverUrl, DurationMs = song.DurationMs, AddedAt = DateTime.UtcNow });
                added++;
            }
            await db.SaveChangesAsync();
            if (added > 0) Interlocked.Increment(ref _version);
            return added;
        }
        finally { _writes.Release(); }
    }

    public async Task<bool> RemoveAsync(string spotifyUri)
    {
        using var scope = _scopeFactory.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var row = await db.AutoQueueSongs.FirstOrDefaultAsync(s => s.SpotifyUri == spotifyUri);
        if (row == null) return false;
        db.AutoQueueSongs.Remove(row);
        await db.SaveChangesAsync();
        Interlocked.Increment(ref _version);
        return true;
    }

    public async Task ClearAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        db.AutoQueueSongs.RemoveRange(db.AutoQueueSongs);
        await db.SaveChangesAsync();
        Interlocked.Increment(ref _version);
    }
}
