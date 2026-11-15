using System.IO;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Data;

namespace MusicBot.Services;

/// <summary>
/// Persists the queue to SQLite so it survives app restarts.
/// – On startup: loads saved items and seeds the local user's queue,
///   and restores the active background playlist.
/// – While running: saves the queue to DB whenever it changes.
/// </summary>
public class QueuePersistenceService : IHostedService
{
    private readonly UserContextManager  _userContext;
    private readonly ILocalLibraryService _library;
    private readonly PlaylistLibraryService _playlists;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QueuePersistenceService> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Channel<bool> _saveRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private UserServices? _services;
    private Task? _saveWorker;

    public QueuePersistenceService(
        UserContextManager userContext,
        ILocalLibraryService library,
        PlaylistLibraryService playlists,
        IServiceScopeFactory scopeFactory,
        ILogger<QueuePersistenceService> logger)
    {
        _userContext  = userContext;
        _library      = library;
        _playlists    = playlists;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // Restore before accepting requests or subscribing: restoration itself emits updates.
        _services = _userContext.GetOrCreate(LocalUser.Id);
        await RestoreAsync(_services);
        _services.Queue.OnQueueUpdated += RequestSave;
        _saveWorker = ProcessSavesAsync(_services);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_services == null) return;
        _services.Queue.OnQueueUpdated -= RequestSave;
        _saveRequests.Writer.TryWrite(true);
        _saveRequests.Writer.TryComplete();
        if (_saveWorker != null) await _saveWorker.WaitAsync(ct);
    }

    // ── Subscribe ────────────────────────────────────────────────────────────

    private void RequestSave(QueueState _) => _saveRequests.Writer.TryWrite(true);

    private async Task ProcessSavesAsync(UserServices services)
    {
        await foreach (var _ in _saveRequests.Reader.ReadAllAsync())
        {
            // Coalesce bursts (playlist imports/reorders) into one SQLite write.
            await Task.Delay(100);
            while (_saveRequests.Reader.TryRead(out var ignored)) { }
            await SaveAsync(services);
        }
    }

    // ── Restore ──────────────────────────────────────────────────────────────

    private async Task RestoreAsync(UserServices services)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            // ── 1. Read persisted queue items FIRST, before any OnQueueUpdated
            //       fires (SetBackgroundPlaylist below triggers SaveAsync which
            //       would wipe the DB rows before we get to read them).
            var rows = await db.PersistedQueueItems
                .Where(p => p.UserId == LocalUser.Id)
                .OrderBy(p => p.Position)
                .ToListAsync();

            // ── 2. Restore the active background playlist ────────────────────
            var activePlaylist = (await _playlists.GetAllAsync()).FirstOrDefault(p => p.IsActive);
            if (activePlaylist != null)
            {
                var dbSongs = await _playlists.GetSongsAsync(activePlaylist.Id);
                var songs   = await MapToSongsAsync(dbSongs);
                services.Queue.SetBackgroundPlaylist(songs, activePlaylist.Name);
                _logger.LogInformation(
                    "Aktif çalma listesi geri yüklendi: \"{Name}\" ({Count} şarkı)",
                    activePlaylist.Name, songs.Count);
            }

            // ── 3. Seed the explicit queue items ─────────────────────────────
            if (rows.Count == 0) return;

            var currentRow   = rows.FirstOrDefault(r => r.Position == 0);
            var upcomingRows = rows.Where(r => r.Position > 0).ToList();

            QueueItem? current = currentRow != null
                ? await ToQueueItemAsync(currentRow)
                : null;

            var upcoming = new List<QueueItem>();
            foreach (var row in upcomingRows)
                upcoming.Add(await ToQueueItemAsync(row));

            services.Queue.Seed(current, upcoming);

            _logger.LogInformation(
                "Kuyruk geri yüklendi: {Current} çalan + {Count} sırada bekleyen",
                current != null ? $"\"{current.Song.Title}\"" : "hiçbiri",
                upcoming.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kuyruk geri yüklenirken hata oluştu");
        }
    }

    private async Task<List<Song>> MapToSongsAsync(List<PlaylistLibrarySong> dbSongs)
    {
        var songs = new List<Song>(dbSongs.Count);
        foreach (var s in dbSongs)
        {
            var song = new Song
            {
                SpotifyUri = s.SpotifyUri,
                Title      = s.Title,
                Artist     = s.Artist,
                CoverUrl   = s.CoverUrl,
                DurationMs = s.DurationMs,
            };
            var cached = await _library.FindByTrackIdAsync(s.SpotifyUri);
            if (cached?.FilePath != null && File.Exists(cached.FilePath))
                song.LocalFilePath = cached.FilePath;
            songs.Add(song);
        }
        return songs;
    }

    private async Task<QueueItem> ToQueueItemAsync(PersistedQueueItem row)
    {
        var cached = await _library.FindByTrackIdAsync(row.TrackId);
        string? localPath = cached?.FilePath != null && File.Exists(cached.FilePath)
            ? cached.FilePath
            : null;

        return new QueueItem
        {
            Id             = row.Id.ToString(),
            RequestedBy    = row.RequestedBy,
            Platform       = row.Platform,
            AddedAt        = row.AddedAt,
            IsPlaylistItem = row.IsPlaylistItem,
            PreparedForSpotify = false,
            Song           = new Song
            {
                SpotifyUri    = row.TrackId,
                Title         = row.Title,
                Artist        = row.Artist,
                CoverUrl      = row.CoverUrl,
                DurationMs    = row.DurationMs,
                LocalFilePath = localPath
            }
        };
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    private async Task SaveAsync(UserServices services)
    {
        await _saveLock.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            // Use GetUpcoming() (real items only) — NOT GetState().Upcoming which
            // includes synthesised playlist-preview items that must not be persisted.
            var nowPlaying = services.Queue.GetCurrentItem();
            var upcoming   = services.Queue.GetUpcoming();

            // Replace all rows for this user
            var existing = await db.PersistedQueueItems
                .Where(p => p.UserId == LocalUser.Id)
                .ToListAsync();
            db.PersistedQueueItems.RemoveRange(existing);

            if (nowPlaying != null)
                db.PersistedQueueItems.Add(ToRow(nowPlaying, 0));

            for (int i = 0; i < upcoming.Count; i++)
                db.PersistedQueueItems.Add(ToRow(upcoming[i], i + 1));

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kuyruk kaydedilirken hata oluştu");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static PersistedQueueItem ToRow(QueueItem item, int position) => new()
    {
        Id             = Guid.TryParse(item.Id, out var g) ? g : Guid.NewGuid(),
        UserId         = LocalUser.Id,
        Position       = position,
        TrackId        = item.Song.SpotifyUri,
        Title          = item.Song.Title,
        Artist         = item.Song.Artist,
        CoverUrl       = item.Song.CoverUrl,
        DurationMs     = item.Song.DurationMs,
        RequestedBy    = item.RequestedBy,
        Platform       = item.Platform,
        AddedAt        = item.AddedAt,
        IsPlaylistItem = item.IsPlaylistItem,
        PreparedForSpotify = item.PreparedForSpotify
    };
}
