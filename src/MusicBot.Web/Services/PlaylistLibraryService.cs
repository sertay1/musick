using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Models;
using MusicBot.Data;

namespace MusicBot.Services;

public class PlaylistMembershipDto
{
    public int      Id           { get; set; }
    public string   Name         { get; set; } = "";
    public int      SongCount    { get; set; }
    public bool     IsInPlaylist { get; set; }
    public bool     IsSystem     { get; set; }
    public DateTime UpdatedAt    { get; set; }
}

public class PlaylistLibraryService
{
    public async Task SetSyncStatusAsync(int id, string sourceUrl, string? error)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var playlist = await db.PlaylistLibraries.FindAsync(id);
        if (playlist == null) return;
        playlist.SourceUrl = sourceUrl;
        playlist.SyncError = error;
        await db.SaveChangesAsync();
    }
    private readonly IServiceScopeFactory _scopeFactory;

    public PlaylistLibraryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<List<PlaylistLibrary>> GetAllAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibraries
            .OrderBy(p => p.IsSystem ? 0 : 1)
            .ThenBy(p => p.SortOrder.HasValue ? p.SortOrder!.Value : int.MaxValue)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync();
    }

    public sealed record Summary(PlaylistLibrary Playlist, int SongCount, long TotalDurationMs, List<string> CoverUrls);
    private sealed class CoverRow
    {
        public int PlaylistId { get; set; }
        public string? CoverUrl { get; set; }
        public string SpotifyUri { get; set; } = "";
    }
    public async Task<List<Summary>> GetSummariesAsync()
    {
        var playlists = await GetAllAsync();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var totals = await db.PlaylistLibrarySongs.GroupBy(s => s.PlaylistId)
            .Select(g => new { Id = g.Key, Count = g.Count(), Duration = g.Sum(s => (long)s.DurationMs) }).ToDictionaryAsync(s => s.Id);
        // Bound the result to four distinct covers per list before loading it into memory.
        var covers = await db.Database.SqlQueryRaw<CoverRow>("""
            SELECT PlaylistId, CoverUrl, SpotifyUri FROM (
                SELECT PlaylistId, CoverUrl, SpotifyUri,
                    ROW_NUMBER() OVER (PARTITION BY PlaylistId ORDER BY Position, SpotifyUri) AS RowNumber
                FROM (
                    SELECT PlaylistId, MAX(CoverUrl) AS CoverUrl, MIN(SpotifyUri) AS SpotifyUri, MIN(Position) AS Position
                    FROM PlaylistLibrarySongs
                    GROUP BY PlaylistId, CASE WHEN CoverUrl IS NULL OR CoverUrl = '' THEN SpotifyUri ELSE CoverUrl END
                )
            ) WHERE RowNumber <= 4
            """).ToListAsync();
        var byPlaylist = covers.GroupBy(c => c.PlaylistId).ToDictionary(g => g.Key, g => g.Select(c =>
            !string.IsNullOrWhiteSpace(c.CoverUrl) ? c.CoverUrl : "/api/artwork/image?trackId=" + Uri.EscapeDataString(c.SpotifyUri)).ToList());
        return playlists.Select(p => new Summary(p, totals.GetValueOrDefault(p.Id)?.Count ?? 0,
            totals.GetValueOrDefault(p.Id)?.Duration ?? 0, byPlaylist.GetValueOrDefault(p.Id) ?? [])).ToList();
    }

    public async Task<PlaylistLibrary?> GetByIdAsync(int id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibraries.FindAsync(id);
    }

    public async Task<PlaylistLibrary> CreateAsync(string name)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var playlist = new PlaylistLibrary
        {
            Name      = name.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.PlaylistLibraries.Add(playlist);
        await db.SaveChangesAsync();
        return playlist;
    }

    public async Task<bool> RenameAsync(int id, string newName)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var row = await db.PlaylistLibraries.FindAsync(id);
        if (row == null) return false;
        row.Name      = newName.Trim();
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var row = await db.PlaylistLibraries.FindAsync(id);
        if (row == null) return false;
        db.PlaylistLibraries.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<PlaylistLibrarySong>> GetSongsAsync(int playlistId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibrarySongs
            .Where(s => s.PlaylistId == playlistId)
            .OrderBy(s => s.Position)
            .ToListAsync();
    }

    public async Task<int> GetSongCountAsync(int playlistId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibrarySongs.CountAsync(s => s.PlaylistId == playlistId);
    }

    public async Task<long> GetTotalDurationMsAsync(int playlistId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibrarySongs
            .Where(s => s.PlaylistId == playlistId)
            .SumAsync(s => (long)s.DurationMs);
    }

    /// <summary>Assigns sequential SortOrder to non-system playlists in the given id order.</summary>
    public async Task ReorderPlaylistsAsync(List<int> ids)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var playlists = await db.PlaylistLibraries.Where(p => ids.Contains(p.Id) && !p.IsSystem).ToListAsync();
        for (int i = 0; i < ids.Count; i++)
        {
            var pl = playlists.FirstOrDefault(p => p.Id == ids[i]);
            if (pl != null) pl.SortOrder = i;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Returns the first <paramref name="max"/> distinct non-empty cover URLs for a playlist.</summary>
    public async Task<List<string>> GetCoverUrlsAsync(int playlistId, int max = 4)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var songs = await db.PlaylistLibrarySongs.Where(s => s.PlaylistId == playlistId)
            .OrderBy(s => s.Position).Select(s => new { s.CoverUrl, s.SpotifyUri }).ToListAsync();
        return songs.Select(s => !string.IsNullOrWhiteSpace(s.CoverUrl) ? s.CoverUrl
                : "/api/artwork/image?trackId=" + Uri.EscapeDataString(s.SpotifyUri))
            .Distinct().Take(max).ToList();
    }

    public async Task<bool> AddSongAsync(int playlistId, Song song)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

        if (await db.PlaylistLibrarySongs.AnyAsync(s => s.PlaylistId == playlistId && s.SpotifyUri == song.SpotifyUri))
            return false;

        var position = await db.PlaylistLibrarySongs.CountAsync(s => s.PlaylistId == playlistId);
        db.PlaylistLibrarySongs.Add(new PlaylistLibrarySong
        {
            PlaylistId = playlistId,
            SpotifyUri = song.SpotifyUri,
            Title      = song.Title,
            Artist     = song.Artist,
            CoverUrl   = song.CoverUrl,
            DurationMs = song.DurationMs,
            Position   = position,
        });

        var playlist = await db.PlaylistLibraries.FindAsync(playlistId);
        if (playlist != null) playlist.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ReorderSongAsync(int playlistId, string spotifyUri, int toIndex)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

        var songs = await db.PlaylistLibrarySongs
            .Where(s => s.PlaylistId == playlistId)
            .OrderBy(s => s.Position)
            .ToListAsync();

        var row = songs.FirstOrDefault(s => s.SpotifyUri == spotifyUri);
        if (row == null) return false;

        toIndex = Math.Clamp(toIndex, 0, songs.Count - 1);
        songs.Remove(row);
        songs.Insert(toIndex, row);

        for (int i = 0; i < songs.Count; i++)
            songs[i].Position = i;

        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveSongAsync(int playlistId, string spotifyUri)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

        var row = await db.PlaylistLibrarySongs
            .FirstOrDefaultAsync(s => s.PlaylistId == playlistId && s.SpotifyUri == spotifyUri);
        if (row == null) return false;

        db.PlaylistLibrarySongs.Remove(row);

        // Re-sequence positions
        var remaining = await db.PlaylistLibrarySongs
            .Where(s => s.PlaylistId == playlistId && s.Position > row.Position)
            .ToListAsync();
        foreach (var r in remaining) r.Position--;

        var playlist = await db.PlaylistLibraries.FindAsync(playlistId);
        if (playlist != null) playlist.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return true;
    }

    private readonly SemaphoreSlim _importLock = new(1, 1);
    public async Task<int> BulkAddAsync(int playlistId, IEnumerable<Song> songs)
    {
        await _importLock.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            var existing = await db.PlaylistLibrarySongs.Where(s => s.PlaylistId == playlistId).ToListAsync();
            var known = existing.Select(s => s.SpotifyUri).ToHashSet();
            var position = existing.Count == 0 ? 0 : existing.Max(s => s.Position) + 1;
            int added = 0;
            foreach (var song in songs)
            {
                if (string.IsNullOrWhiteSpace(song.SpotifyUri) || !known.Add(song.SpotifyUri)) continue;
                db.PlaylistLibrarySongs.Add(new PlaylistLibrarySong { PlaylistId = playlistId, SpotifyUri = song.SpotifyUri,
                    Title = song.Title, Artist = song.Artist, CoverUrl = song.CoverUrl, DurationMs = song.DurationMs, Position = position++ });
                added++;
            }
            var playlist = await db.PlaylistLibraries.FindAsync(playlistId);
            if (playlist != null) playlist.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return added;
        }
        finally { _importLock.Release(); }
    }

    // ── System playlists (Liked Songs) ────────────────────────────────────────

    /// <summary>Creates the "Beğendiklerim" system playlist if it doesn't exist yet, migrates legacy names, and ensures it is pinned.</summary>
    public async Task EnsureSystemPlaylistsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var existing = await db.PlaylistLibraries.FirstOrDefaultAsync(p => p.IsSystem);
        if (existing == null)
        {
            db.PlaylistLibraries.Add(new PlaylistLibrary
            {
                Name      = "Beğendiklerim",
                IsSystem  = true,
                IsPinned  = true,
                PinOrder  = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        else
        {
            bool modified = false;
            if (existing.Name is "Música que te gustó" or "Liked Songs")
            {
                existing.Name = "Beğendiklerim";
                existing.UpdatedAt = DateTime.UtcNow;
                modified = true;
            }
            if (!existing.IsPinned)
            {
                existing.IsPinned = true;
                existing.PinOrder = 0;
                modified = true;
            }
            if (modified)
                await db.SaveChangesAsync();
        }
    }

    /// <summary>Toggles the pinned state of a playlist. Returns false if not found.</summary>
    public async Task<bool> SetPinnedAsync(int id, bool isPinned)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var row = await db.PlaylistLibraries.FindAsync(id);
        if (row == null) return false;

        if (isPinned && !row.IsPinned)
        {
            var maxOrder = await db.PlaylistLibraries
                .Where(p => p.IsPinned)
                .Select(p => (int?)p.PinOrder)
                .MaxAsync() ?? -1;
            row.PinOrder = maxOrder + 1;
        }
        else if (!isPinned && row.IsPinned)
        {
            var oldOrder = row.PinOrder;
            var others = await db.PlaylistLibraries
                .Where(p => p.IsPinned && p.Id != id && p.PinOrder > oldOrder)
                .ToListAsync();
            foreach (var o in others) o.PinOrder--;
            row.PinOrder = 0;
        }

        row.IsPinned = isPinned;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Reorders pinned playlists. <paramref name="ids"/> is the desired order (index = new PinOrder).</summary>
    public async Task ReorderPinsAsync(List<int> ids)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var playlists = await db.PlaylistLibraries.Where(p => ids.Contains(p.Id)).ToListAsync();
        for (int i = 0; i < ids.Count; i++)
        {
            var pl = playlists.FirstOrDefault(p => p.Id == ids[i]);
            if (pl != null) pl.PinOrder = i;
        }
        await db.SaveChangesAsync();
    }

    public async Task<int?> GetLikedPlaylistIdAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var pl = await db.PlaylistLibraries.FirstOrDefaultAsync(p => p.IsSystem);
        return pl?.Id;
    }

    public async Task<List<string>> GetLikedUrisAsync()
    {
        var id = await GetLikedPlaylistIdAsync();
        if (id == null) return [];
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibrarySongs
            .Where(s => s.PlaylistId == id.Value)
            .Select(s => s.SpotifyUri)
            .ToListAsync();
    }

    public async Task<bool> IsSongInPlaylistAsync(int playlistId, string spotifyUri)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibrarySongs
            .AnyAsync(s => s.PlaylistId == playlistId && s.SpotifyUri == spotifyUri);
    }

    public async Task<List<PlaylistMembershipDto>> GetSongMembershipsAsync(string spotifyUri)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

        var playlists = await db.PlaylistLibraries.OrderBy(p => p.CreatedAt).ToListAsync();

        var containingIds = (await db.PlaylistLibrarySongs
            .Where(s => s.SpotifyUri == spotifyUri)
            .Select(s => s.PlaylistId)
            .ToListAsync())
            .ToHashSet();

        var counts = await db.PlaylistLibrarySongs
            .GroupBy(s => s.PlaylistId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        return playlists
            .Select(p => new PlaylistMembershipDto
            {
                Id           = p.Id,
                Name         = p.Name,
                SongCount    = counts.GetValueOrDefault(p.Id, 0),
                IsInPlaylist = containingIds.Contains(p.Id),
                IsSystem     = p.IsSystem,
                UpdatedAt    = p.UpdatedAt,
            })
            .ToList();
    }

    /// <summary>
    /// Marks the given playlist as active and all others as inactive.
    /// Pass null to deactivate all.
    /// </summary>
    public async Task SetActiveAsync(int? id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

        var all = await db.PlaylistLibraries.ToListAsync();
        foreach (var p in all)
            p.IsActive = id.HasValue && p.Id == id.Value;

        await db.SaveChangesAsync();
    }
}
