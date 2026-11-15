using SysFile = System.IO.File;
using SysFileInfo = System.IO.FileInfo;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Hubs;
using MusicBot.Services;
using MusicBot.Services.Downloader;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/playlists")]
[Tags("Playlists")]
public class PlaylistController : ControllerBase
{
    private readonly PlaylistLibraryService  _playlists;
    private readonly YtDlpDownloaderService  _downloader;
    private readonly UserContextManager      _userContext;
    private readonly PlaybackSyncService     _sync;
    private readonly ILocalLibraryService    _library;
    private readonly IHubContext<OverlayHub> _hub;

    public PlaylistController(
        PlaylistLibraryService playlists,
        YtDlpDownloaderService downloader,
        UserContextManager userContext,
        PlaybackSyncService sync,
        ILocalLibraryService library,
        IHubContext<OverlayHub> hub)
    {
        _playlists   = playlists;
        _downloader  = downloader;
        _userContext = userContext;
        _sync        = sync;
        _library     = library;
        _hub         = hub;
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var all = await _playlists.GetSummariesAsync();
        var result = all.Select(summary =>
        {
            var p = summary.Playlist;
            return new { p.Id, p.Name, p.SourceUrl, p.SyncError, p.IsActive, p.IsSystem, p.IsPinned, p.PinOrder, p.SortOrder,
                p.CreatedAt, SongCount = summary.SongCount, CoverUrls = summary.CoverUrls, TotalDurationMs = summary.TotalDurationMs };
        }).ToList();
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PlaylistCreateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Liste adı boş bırakılamaz." });

        try
        {
            var playlist = await _playlists.CreateAsync(req.Name);
            return Ok(new { playlist.Id, playlist.Name, playlist.IsActive, playlist.CreatedAt, SongCount = 0 });
        }
        catch
        {
            return BadRequest(new { error = "Bu isimde bir çalma listesi zaten mevcut" });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();
        if (playlist.IsSystem) return BadRequest(new { error = "Sistem listeleri (Beğendiklerim) silinemez" });

        // If this was the active playlist, deactivate the background playlist
        if (playlist.IsActive)
        {
            var services = _userContext.GetOrCreate(LocalUser.Id);
            services.Queue.ClearBackgroundPlaylist();
        }

        await _playlists.DeleteAsync(id);
        await BroadcastStatusAsync();
        return NoContent();
    }

    [HttpPut("{id:int}/rename")]
    public async Task<IActionResult> Rename(int id, [FromBody] PlaylistCreateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Liste adı boş olamaz" });

        var existing = await _playlists.GetByIdAsync(id);
        if (existing?.IsSystem == true) return BadRequest(new { error = "Sistem listeleri yeniden adlandırılamaz" });
        var ok = await _playlists.RenameAsync(id, req.Name);
        if (ok) await BroadcastStatusAsync();
        return ok ? NoContent() : NotFound();
    }

    // ── Pin ──────────────────────────────────────────────────────────────────

    [HttpPost("{id:int}/pin")]
    public async Task<IActionResult> TogglePin(int id)
    {
        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        await _playlists.SetPinnedAsync(id, !playlist.IsPinned);
        await BroadcastStatusAsync();
        return Ok(new { isPinned = !playlist.IsPinned });
    }

    [HttpPut("reorder")]
    public async Task<IActionResult> ReorderPlaylists([FromBody] PinReorderRequest req)
    {
        if (req.Ids == null || req.Ids.Count == 0)
            return BadRequest(new { error = "Liste kimlikleri gerekli." });

        await _playlists.ReorderPlaylistsAsync(req.Ids);
        await BroadcastStatusAsync();
        return NoContent();
    }

    [HttpPut("pins/reorder")]
    public async Task<IActionResult> ReorderPins([FromBody] PinReorderRequest req)
    {
        if (req.Ids == null || req.Ids.Count == 0)
            return BadRequest(new { error = "Liste kimlikleri gerekli." });

        await _playlists.ReorderPinsAsync(req.Ids);
        await BroadcastStatusAsync();
        return NoContent();
    }

    // ── Songs ─────────────────────────────────────────────────────────────────

    [HttpGet("{id:int}/songs")]
    public async Task<IActionResult> GetSongs(int id)
    {
        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        var songs = await _playlists.GetSongsAsync(id);
        return Ok(songs);
    }

    [HttpPost("{id:int}/songs")]
    public async Task<IActionResult> AddSong(int id, [FromBody] PlaylistSongRequest req)
    {
        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        var song = new Song
        {
            SpotifyUri = req.SpotifyUri,
            Title      = req.Title,
            Artist     = req.Artist,
            CoverUrl   = req.CoverUrl ?? "",
            DurationMs = req.DurationMs,
        };

        var added = await _playlists.AddSongAsync(id, song);
        if (!added)
            return Conflict(new { error = "Şarkı zaten bu listede." });

        await BroadcastStatusAsync();
        return NoContent();
    }

    [HttpPut("{id:int}/songs/reorder")]
    public async Task<IActionResult> ReorderSong(int id, [FromBody] PlaylistSongReorderRequest req)
    {
        var ok = await _playlists.ReorderSongAsync(id, req.SpotifyUri, req.ToIndex);
        if (!ok) return NotFound();

        await BroadcastStatusAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}/songs/{uri}")]
    public async Task<IActionResult> RemoveSong(int id, string uri)
    {
        var spotifyUri = HttpUtility.UrlDecode(uri);
        var ok = await _playlists.RemoveSongAsync(id, spotifyUri);
        if (!ok) return NotFound();

        await BroadcastStatusAsync();
        return NoContent();
    }

    // ── Import ────────────────────────────────────────────────────────────────

    [HttpPost("{id:int}/import")]
    public async Task<IActionResult> Import(int id, [FromBody] PlaylistImportRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "Bağlantı adresi (URL) gereklidir" });

        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        var services = _userContext.GetOrCreate(LocalUser.Id);
        List<Song> tracks;
        string? importedName = null;

        if (TryExtractSpotifyPlaylistId(req.Url, out var spPlaylistId))
        {
            try
            {
                tracks = services.Spotify.IsAuthenticated
                    ? await services.Spotify.GetPlaylistTracksAsync(spPlaylistId, int.MaxValue)
                    : await services.Spotify.ScrapeEmbedPlaylistTracksAsync(spPlaylistId, int.MaxValue);
                importedName = "Spotify çalma listesi";
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }
        else if (TryExtractSpotifyAlbumId(req.Url, out var spAlbumId))
        {
            try
            {
                tracks = services.Spotify.IsAuthenticated
                    ? await services.Spotify.GetAlbumTracksAsync(spAlbumId, int.MaxValue)
                    : await services.Spotify.ScrapeEmbedAlbumTracksAsync(spAlbumId, int.MaxValue);
                importedName = "Spotify albüm";
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }
        else if (TryExtractSpotifyTrackId(req.Url, out var spTrackId))
        {
            try
            {
                var singleSong = services.Spotify.IsAuthenticated
                    ? await services.Spotify.GetTrackAsync(spTrackId)
                    : await services.Spotify.ScrapeEmbedTrackAsync(spTrackId);

                singleSong ??= await services.Spotify.ScrapeEmbedTrackAsync(spTrackId);

                if (singleSong != null)
                {
                    tracks = new List<Song> { singleSong };
                    importedName = singleSong.Title;
                }
                else
                {
                    tracks = new List<Song>();
                }
            }
            catch (Exception ex)
            {
                var fallbackSong = await services.Spotify.ScrapeEmbedTrackAsync(spTrackId);
                if (fallbackSong != null)
                {
                    tracks = new List<Song> { fallbackSong };
                    importedName = fallbackSong.Title;
                }
                else
                {
                    return BadRequest(new { error = $"Spotify şarkısı içeri aktarılamadı: {ex.Message}" });
                }
            }
        }
        else if (req.Url.Contains("collection/tracks", StringComparison.OrdinalIgnoreCase) ||
                 req.Url.Contains("spotify:collection", StringComparison.OrdinalIgnoreCase))
        {
            if (!services.Spotify.IsAuthenticated)
                return BadRequest(new { error = "Spotify Beğenilen Şarkılar'ı içe aktarmak için lütfen Ayarlar'dan Spotify hesabınızı bağlayın." });

            try
            {
                tracks = await services.Spotify.GetSavedTracksAsync(int.MaxValue);
                importedName = "Spotify Beğenilen Şarkılar";
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = $"Spotify beğenilen şarkılar içeri aktarılamadı: {ex.Message}" });
            }
        }
        else if (req.Url.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Geçersiz veya tanınmayan Spotify bağlantısı. Lütfen geçerli bir Spotify şarkı, albüm veya çalma listesi bağlantısı girin." });
        }
        else
        {
            try
            {
                (tracks, importedName) = await _downloader.ImportPlaylistAsync(req.Url, int.MaxValue);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        if (tracks.Count == 0)
            return BadRequest(new { error = "Bu bağlantıda şarkı bulunamadı." });

        // Auto-rename to the playlist title only when the user did not
        // supply their own name (frontend passes userProvidedName when it did).
        string? finalName = null;
        bool userChoseName = !string.IsNullOrWhiteSpace(req.UserProvidedName);
        if (!userChoseName && !string.IsNullOrEmpty(importedName) && !playlist.IsSystem)
        {
            await _playlists.RenameAsync(id, importedName);
            finalName = importedName;
        }

        var added = await _playlists.BulkAddAsync(id, tracks);

        await BroadcastStatusAsync();
        return Ok(new { added, skipped = tracks.Count - added, total = tracks.Count, name = finalName });
    }

    // ── Playback ──────────────────────────────────────────────────────────────

    [HttpPost("{id:int}/play")]
    public async Task<IActionResult> Play(int id, [FromBody] PlaylistPlayRequest? req)
    {
        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        var dbSongs = await _playlists.GetSongsAsync(id);
        if (dbSongs.Count == 0)
            return BadRequest(new { error = "Çalma listesi boş" });

        var songs = await MapToSongsAsync(dbSongs);

        if (req?.Shuffle == true)
            Shuffle(songs);

        var services = _userContext.GetOrCreate(LocalUser.Id);
        services.Queue.SetBackgroundPlaylist(songs, playlist.Name);
        await _playlists.SetActiveAsync(id);

        // If the queue is idle start playback from the background playlist
        if (services.Queue.GetCurrentItem() == null)
        {
            services.Queue.Advance();
            _ = Task.Run(() => _sync.StartCurrentTrackAsync(services));
        }

        await BroadcastStatusAsync();
        return Ok(new { message = $"\"{playlist.Name}\" çalınıyor" });
    }

    /// <summary>
    /// Play a specific song from the playlist and load the remaining songs
    /// (from that position onwards, cyclically) into the background queue.
    /// </summary>
    [HttpPost("{id:int}/songs/{uri}/play")]
    public async Task<IActionResult> PlaySong(int id, string uri, [FromBody] PlaySongRequest? req)
    {
        var spotifyUri = HttpUtility.UrlDecode(uri);
        var playlist   = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        var dbSongs = await _playlists.GetSongsAsync(id);
        if (dbSongs.Count == 0) return BadRequest(new { error = "Çalma listesi boş" });

        var songs      = await MapToSongsAsync(dbSongs);
        var clickedIdx = songs.FindIndex(s => s.SpotifyUri == spotifyUri);
        if (clickedIdx < 0) return NotFound(new { error = "Şarkı çalma listesinde bulunamadı" });

        var clickedSong = songs[clickedIdx];

        // Build the remaining songs: from (clickedIdx+1) cycling to clickedIdx-1
        var remaining = new List<Song>(songs.Count - 1);
        for (int i = 1; i < songs.Count; i++)
            remaining.Add(songs[(clickedIdx + i) % songs.Count]);

        if (req?.Shuffle == true)
            Shuffle(remaining);

        var services = _userContext.GetOrCreate(LocalUser.Id);
        services.Queue.SetBackgroundPlaylist(remaining, playlist.Name);
        services.Queue.PlayNow(clickedSong, "Playlist", "web", isPlaylistItem: true);
        _ = Task.Run(() => _sync.StartCurrentTrackAsync(services));

        await _playlists.SetActiveAsync(id);
        await BroadcastStatusAsync();

        return Ok(new { message = $"\"{clickedSong.Title}\" çalınıyor." });
    }

    [HttpDelete("active")]
    public async Task<IActionResult> Deactivate()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        services.Queue.ClearBackgroundPlaylist();
        await _playlists.SetActiveAsync(null);
        await BroadcastStatusAsync();
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
            // Check local cache so pre-warm skips already-downloaded files
            var cached = await _library.FindByTrackIdAsync(s.SpotifyUri);
            if (cached != null && SysFile.Exists(cached.FilePath)
                && new SysFileInfo(cached.FilePath).Length > 100_000)
            {
                song.LocalFilePath = cached.FilePath;
            }
            songs.Add(song);
        }
        return songs;
    }

    /// <summary>Auto-import all playlists from authenticated Spotify account into local library</summary>
    [HttpPost("sync-spotify")]
    public async Task<IActionResult> SyncSpotify()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        if (!services.Spotify.IsAuthenticated)
            return BadRequest(new { error = "Spotify bağlı değil. Lütfen önce Spotify hesabınızı bağlayın." });

        try
        {
            var userPlaylists = await services.Spotify.GetUserPlaylistsAsync();
            int importedCount = 0;
            int failedCount = 0;
            var failures = new List<string>();
            var ownedIds = userPlaylists.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var summary in await _playlists.GetSummariesAsync())
                if (summary.SongCount == 0 && !summary.Playlist.IsSystem && !summary.Playlist.IsActive && summary.Playlist.SyncError != null
                    && summary.Playlist.SourceUrl is string source && CommandRouterService.TryExtractSpotifyPlaylistId(source, out var oldId) && !ownedIds.Contains(oldId))
                    await _playlists.DeleteAsync(summary.Playlist.Id);
            var localLists = await _playlists.GetAllAsync();
            foreach (var p in userPlaylists)
            {
                var sourceUrl = "https://open.spotify.com/playlist/" + p.Id;
                var catalog = localLists.FirstOrDefault(x => x.SourceUrl == sourceUrl)
                    ?? localLists.FirstOrDefault(x => !x.IsSystem && x.SourceUrl == null && x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                try
                {
                    var tracks = await services.Spotify.GetPlaylistTracksAsync(p.Id, int.MaxValue);
                    if (tracks.Count == 0 && p.TrackCount > 0)
                    {
                        if (catalog != null) await _playlists.SetSyncStatusAsync(catalog.Id, sourceUrl, "Spotify bu liste için şarkı döndürmedi. Başkasına ait listelerde API yalnızca liste bilgilerini paylaşabilir.");
                        failedCount++; failures.Add(p.Name); continue;
                    }

                catalog ??= await _playlists.CreateAsync(localLists.Any(x => x.Name == p.Name) ? p.Name + " · " + p.Id[..Math.Min(6, p.Id.Length)] : p.Name);
                catalog.SourceUrl = sourceUrl;
                if (!localLists.Any(x => x.Id == catalog.Id)) localLists.Add(catalog);
                    var existing = catalog;
                    int playlistId;
                    if (existing == null)
                    {
                        var created = await _playlists.CreateAsync(p.Name);
                        playlistId = created.Id;
                    }
                    else
                    {
                        playlistId = existing.Id;
                    }

                    await _playlists.BulkAddAsync(playlistId, tracks);
                    await _playlists.SetSyncStatusAsync(playlistId, sourceUrl, null);
                    importedCount++;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    failures.Add(p.Name);
                    if (catalog != null) await _playlists.SetSyncStatusAsync(catalog.Id, sourceUrl, "Spotify bu listenin içeriğini uygulamaya vermedi. Liste kitaplıkta gösteriliyor; mevcut şarkılar korundu. " + ex.Message);
                    ActivityJournal.Record("spotify", "Liste aktarımı", "", $"{p.Name}: {ex.Message}", false);
                    // If an individual playlist is private, collaborative or returns 403, skip it gracefully
                    continue;
                }
            }

            await BroadcastStatusAsync();
            return Ok(new { message = $"Kitaplığınızdaki {userPlaylists.Count} size ait liste kontrol edildi. {importedCount} liste eşitlendi." + (failedCount > 0 ? $" {failedCount} listenin içeriği alınamadı: {string.Join(", ", failures)}. Ayrıntılar Etkinlik Günlüğü'nde." : ""), count = importedCount });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Spotify listeleri eşitlenemedi: {ex.Message}" });
        }
    }

    [HttpPost("sync-youtube")]
    public async Task<IActionResult> SyncYouTube([FromBody] YouTubeSyncRequest request)
    {
        try
        {
            var lists = await _downloader.GetYouTubePlaylistsAsync(request.ChannelUrl);
            int imported = 0, failed = 0;
            foreach (var source in lists)
            {
                HttpContext.RequestAborted.ThrowIfCancellationRequested();
                try
                {
                    ActivityJournal.Record("musick", "Liste aktarımı", "", $"YouTube: {source.Title} alınıyor…");
                    var (tracks, _) = await _downloader.ImportPlaylistAsync(source.Url);
                    if (tracks.Count == 0) { failed++; continue; }
                    var existing = (await _playlists.GetAllAsync()).FirstOrDefault(p => !p.IsSystem && p.Name == source.Title);
                    var id = existing?.Id ?? (await _playlists.CreateAsync(source.Title)).Id;
                    await _playlists.BulkAddAsync(id, tracks);
                    imported++;
                }
                catch (Exception ex) { failed++; ActivityJournal.Record("musick", "Liste aktarımı", "", ex.Message, false); }
            }
            await BroadcastStatusAsync();
            return Ok(new { count = imported, message = $"{imported} YouTube listesi aktarıldı. {failed} liste alınamadı." });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    private static bool TryExtractSpotifyPlaylistId(string url, out string playlistId)
    {
        playlistId = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var m = System.Text.RegularExpressions.Regex.Match(url, @"(?:spotify:playlist:|playlist/)([a-zA-Z0-9]+)");
        if (m.Success)
        {
            playlistId = m.Groups[1].Value;
            return true;
        }
        return false;
    }

    private static bool TryExtractSpotifyAlbumId(string url, out string albumId)
    {
        albumId = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var m = System.Text.RegularExpressions.Regex.Match(url, @"(?:spotify:album:|album/)([a-zA-Z0-9]+)");
        if (m.Success)
        {
            albumId = m.Groups[1].Value;
            return true;
        }
        return false;
    }

    private static bool TryExtractSpotifyTrackId(string url, out string trackId)
    {
        trackId = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var m = System.Text.RegularExpressions.Regex.Match(url, @"(?:spotify:track:|track/)([a-zA-Z0-9]+)");
        if (m.Success)
        {
            trackId = m.Groups[1].Value;
            return true;
        }
        return false;
    }

    /// <summary>Bulk delete songs from a playlist</summary>
    [HttpPost("{id:int}/songs/bulk-delete")]
    public async Task<IActionResult> BulkDeleteSongs(int id, [FromBody] BulkUrisRequest req)
    {
        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        int removed = 0;
        foreach (var uri in req.Uris)
        {
            if (await _playlists.RemoveSongAsync(id, uri))
                removed++;
        }

        await BroadcastStatusAsync();
        return Ok(new { removed });
    }

    /// <summary>Bulk enqueue songs to upcoming queue</summary>
    [HttpPost("{id:int}/songs/bulk-enqueue")]
    public async Task<IActionResult> BulkEnqueueSongs(int id, [FromBody] BulkUrisRequest req)
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var dbSongs = await _playlists.GetSongsAsync(id);
        var songs = await MapToSongsAsync(dbSongs);
        var toAdd = songs.Where(s => req.Uris.Contains(s.SpotifyUri)).ToList();

        int enqueued = 0;
        foreach (var song in toAdd)
        {
            try
            {
                services.Queue.AddSong(song, "Kütüphane", "web", bypassUserLimit: true);
                if (song.LocalFilePath == null) _downloader.StartDownload(song);
                enqueued++;
            }
            catch { }
        }

        if (services.Queue.GetCurrentItem() == null && services.Queue.GetUpcoming().Count > 0)
        {
            services.Queue.Advance();
            await _sync.StartCurrentTrackAsync(services);
        }

        return Ok(new { enqueued });
    }

    private Task BroadcastStatusAsync() =>
        _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("playlist:status", new { });

    private static void Shuffle<T>(List<T> list)
    {
        var rng = new Random();
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

// ── Request models ────────────────────────────────────────────────────────────

public record PlaylistCreateRequest(string Name);
public record PlaylistSongRequest(string SpotifyUri, string Title, string Artist, string? CoverUrl, int DurationMs);
public record PlaylistImportRequest(string Url, string? UserProvidedName = null);
public record PlaylistPlayRequest(bool Shuffle = false);
public record PlaySongRequest(bool Shuffle = false);
public record PinReorderRequest(List<int> Ids);
public record PlaylistSongReorderRequest(string SpotifyUri, int ToIndex);
public class BulkUrisRequest { public List<string> Uris { get; set; } = new(); }

public record YouTubeSyncRequest(string? ChannelUrl);
