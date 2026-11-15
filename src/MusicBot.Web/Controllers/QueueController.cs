using System.IO;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Services;
using MusicBot.Services.Downloader;
using Microsoft.EntityFrameworkCore;
using MusicBot.Data;
using MusicBot.Services.Platforms;
using System.Text.Json;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/queue")]
[Tags("Queue")]
public class QueueController : ControllerBase
{
    private readonly UserContextManager      _userContext;
    private readonly ILocalLibraryService    _library;
    private readonly PlaybackSyncService     _sync;
    private readonly PresenceCheckService    _presence;
    private readonly YtDlpDownloaderService  _downloader;
    private readonly BannedSongService       _banned;

    public QueueController(
        UserContextManager userContext,
        ILocalLibraryService library,
        PlaybackSyncService sync,
        PresenceCheckService presence,
        YtDlpDownloaderService downloader,
        BannedSongService banned)
    {
        _userContext = userContext;
        _library     = library;
        _sync        = sync;
        _presence    = presence;
        _downloader  = downloader;
        _banned      = banned;
    }

    /// <summary>Get local queue state</summary>
    [HttpGet]
    [ProducesResponseType(typeof(SpotifyQueueState), 200)]
    public IActionResult GetQueue()
    {
        return Ok(BuildQueueState(_userContext.GetOrCreate(LocalUser.Id)));
    }

    /// <summary>Get now playing</summary>
    [HttpGet("now-playing")]
    [ProducesResponseType(typeof(NowPlayingState), 200)]
    public IActionResult GetNowPlaying()
    {
        return Ok(_userContext.GetOrCreate(LocalUser.Id).Queue.GetNowPlaying());
    }

    /// <summary>Get now playing (public overlay endpoint — token ignored)</summary>
    [HttpGet("public/{overlayToken}/now-playing")]
    [ProducesResponseType(typeof(NowPlayingState), 200)]
    public IActionResult GetNowPlayingByToken(string overlayToken)
    {
        return Ok(_userContext.GetOrCreate(LocalUser.Id).Queue.GetNowPlaying());
    }

    /// <summary>Get queue (public overlay endpoint — token ignored)</summary>
    [HttpGet("public/{overlayToken}/spotify-queue")]
    [ProducesResponseType(typeof(SpotifyQueueState), 200)]
    public IActionResult GetQueueByToken(string overlayToken)
    {
        return Ok(BuildQueueState(_userContext.GetOrCreate(LocalUser.Id)));
    }

    /// <summary>Clear all user-requested songs from the queue (leaves the background playlist intact)</summary>
    [HttpDelete("user")]
    [ProducesResponseType(204)]
    public IActionResult ClearUserQueue()
    {
        _userContext.GetOrCreate(LocalUser.Id).Queue.ClearUserQueue();
        return NoContent();
    }

    /// <summary>Clear all pending requests and the cyclic list, preserving the playing track and library.</summary>
    [HttpDelete("pending")]
    public async Task<IActionResult> ClearPendingQueue([FromServices] PlaylistLibraryService playlists)
    {
        await playlists.SetActiveAsync(null);
        var queue = _userContext.GetOrCreate(LocalUser.Id).Queue;
        queue.ClearUserQueue(clearAll: true);
        return NoContent();
    }

    /// <summary>Remove a specific song from the queue by its URI (admin)</summary>
    [HttpDelete("item")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public IActionResult RemoveItem([FromBody] RemoveItemRequest req)
    {
        var ok = _userContext.GetOrCreate(LocalUser.Id).Queue.RemoveByUri(req.Uri);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Remove a queue item by its 0-based index</summary>
    [HttpDelete("item/{index:int}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public IActionResult RemoveItemByIndex(int index)
    {
        var ok = _userContext.GetOrCreate(LocalUser.Id).Queue.RemoveAt(index);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Move a queue item up or down</summary>
    [HttpPost("move")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public IActionResult MoveItem([FromBody] MoveItemRequest req)
    {
        var queue = _userContext.GetOrCreate(LocalUser.Id).Queue;
        var ok = req.Direction == "up" ? queue.MoveUp(req.Uri) : queue.MoveDown(req.Uri);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Start auto-queue immediately even when nothing is playing</summary>
    [HttpPost("start-auto")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    public async Task<IActionResult> StartAuto()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        if (services.Queue.GetCurrentItem() != null)
            return BadRequest(CommandResult.Fail("Zaten bir şarkı çalıyor. Otomatik kuyruk, kuyruk boşaldığında devreye girer."));

        var started = await _sync.TryStartAutoQueueAsync(services);
        return started
            ? Ok(CommandResult.Ok("Otomatik kuyruk başlatıldı."))
            : BadRequest(CommandResult.Fail("Otomatik kuyruk devre dışı veya şarkı havuzu boş."));
    }

    /// <summary>Play a song immediately, pushing the current song back to position 0 of the queue</summary>
    [HttpPost("play-now")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    public async Task<IActionResult> PlayNow([FromBody] DirectSongRequest req)
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var song     = req.ToSong();
        await ValidateSourceAsync(song);
        if (song.SpotifyUri.StartsWith("spotify:") && services.Spotify is MusicBot.Services.Spotify.SpotifyService spotify)
            spotify.EnsurePlaybackAvailable();

        var cached = await _library.FindByTrackIdAsync(song.SpotifyUri);
        if (cached != null && System.IO.File.Exists(cached.FilePath))
            song.LocalFilePath = cached.FilePath;

        _presence.CancelCheck();
        services.Queue.PlayNow(song, req.RequestedBy ?? "Admin", req.Platform ?? "web");
        await _sync.StartCurrentTrackAsync(services, userInitiated: true);

        return Ok(CommandResult.Ok($"\"{song.Title}\" - {song.Artist} çalınıyor."));
    }

    /// <summary>Add a song directly to the queue by metadata (from history / library)</summary>
    [HttpPost("enqueue")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    public async Task<IActionResult> Enqueue([FromBody] DirectSongRequest req)
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var song     = req.ToSong();
        await ValidateSourceAsync(song);

        var cached = await _library.FindByTrackIdAsync(song.SpotifyUri);
        if (cached != null && System.IO.File.Exists(cached.FilePath))
            song.LocalFilePath = cached.FilePath;

        try
        {
            services.Queue.AddSong(song, req.RequestedBy ?? "Admin", req.Platform ?? "web");

            if (services.Queue.GetCurrentItem() == null)
            {
                services.Queue.Advance();
                await _sync.StartCurrentTrackAsync(services, userInitiated: true);
            }

            return Ok(CommandResult.Ok($"\"{song.Title}\" - {song.Artist} kuyruğa eklendi."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(CommandResult.Fail(ex.Message));
        }
    }

    private async Task ValidateSourceAsync(Song song)
    {
        var settings = HttpContext.RequestServices.GetRequiredService<QueueSettingsService>();
        if (YouTubeLinks.IsYouTube(song.SpotifyUri))
        {
            if (!settings.YouTubeEnabled) throw new InvalidOperationException("YouTube istekleri ayarlardan kapatılmış.");
            if (!YouTubeLinks.TryVideoId(song.SpotifyUri, out var id)) throw new InvalidOperationException("Geçerli bir YouTube şarkı bağlantısı girin.");
            song.SpotifyUri = "youtube:" + id;
        }
        if (song.SpotifyUri.StartsWith("youtube:")) await _downloader.ValidateMusicVideoAsync(song.SpotifyUri);
        else if (song.SpotifyUri.StartsWith("spotify:") && !settings.SpotifyEnabled) throw new InvalidOperationException("Spotify istekleri ayarlardan kapatılmış.");
    }
    /// <summary>Move a queue item to a specific 0-based index</summary>
    [HttpPost("reorder")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public IActionResult ReorderItem([FromBody] ReorderRequest req)
    {
        var ok = _userContext.GetOrCreate(LocalUser.Id).Queue.Reorder(req.Uri, req.ToIndex);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Import all tracks from a YouTube or Spotify playlist URL into the queue</summary>
    [HttpPost("import-playlist")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> ImportPlaylist([FromBody] ImportPlaylistRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "Geçerli bir liste veya albüm linki girin." });

        var services = _userContext.GetOrCreate(LocalUser.Id);
        var requestedBy = string.IsNullOrWhiteSpace(req.RequestedBy) ? await GetImportRequesterAsync() : req.RequestedBy.Trim().TrimStart('@');

        List<Song> tracks;

        if (TryExtractYouTubePlaylistId(req.Url, out _))
        {
            if (!HttpContext.RequestServices.GetRequiredService<QueueSettingsService>().YouTubeEnabled)
                return BadRequest(new { error = "YouTube istekleri ayarlardan kapatılmış." });
            (tracks, _) = await _downloader.ImportPlaylistAsync(req.Url, int.MaxValue);
        }
        else if (TryExtractSpotifyPlaylistId(req.Url, out var playlistId) && services.Spotify.IsAuthenticated)
        {
            try { tracks = await services.Spotify.GetPlaylistTracksAsync(playlistId, int.MaxValue); }
            catch (Exception ex) { return BadRequest(new { error = $"Spotify listesi alınamadı: {ex.Message}" }); }
        }
        else if (TryExtractSpotifyAlbumId(req.Url, out var albumId) && services.Spotify.IsAuthenticated)
        {
            try { tracks = await services.Spotify.GetAlbumTracksAsync(albumId, int.MaxValue); }
            catch (Exception ex) { return BadRequest(new { error = $"Spotify albümü alınamadı: {ex.Message}" }); }
        }
        else if ((TryExtractSpotifyPlaylistId(req.Url, out _) || TryExtractSpotifyAlbumId(req.Url, out _)) && !services.Spotify.IsAuthenticated)
        {
            return BadRequest(new { error = "Spotify listelerini veya albümlerini içe aktarmak için önce Ayarlar'dan Spotify hesabınızı bağlayın." });
        }
        else
        {
            return BadRequest(new { error = "Tanınmayan liste veya albüm bağlantısı. Lütfen geçerli bir YouTube veya Spotify linki girin." });
        }

        int added = 0, skipped = 0;
        foreach (var song in tracks)
        {
            if (_banned.IsBanned(song.SpotifyUri)) { skipped++; continue; }

            var cached = await _library.FindByTrackIdAsync(song.SpotifyUri);
            if (cached != null && System.IO.File.Exists(cached.FilePath))
                song.LocalFilePath = cached.FilePath;

            try
            {
                services.Queue.AddSong(song, requestedBy, "web", bypassUserLimit: true, isPlaylistItem: true);
                if (song.LocalFilePath == null) _downloader.StartDownload(song);
                added++;
            }
            catch { skipped++; } // queue full or duplicate
        }

        if (services.Queue.GetCurrentItem() == null && services.Queue.GetUpcoming().Count > 0)
        {
            services.Queue.Advance();
            await _sync.StartCurrentTrackAsync(services, userInitiated: true);
        }

        return Ok(new { added, skipped, total = tracks.Count });
    }

    private async Task<string> GetImportRequesterAsync()
    {
        var db = HttpContext.RequestServices.GetRequiredService<MusicBotDbContext>();
        var config = await db.PlatformConfigs.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "kick");
        if (config != null)
        {
            try
            {
                using var json = JsonDocument.Parse(config.ConfigJson);
                if (json.RootElement.ValueKind == JsonValueKind.Object && (json.RootElement.TryGetProperty("channel", out var channel) || json.RootElement.TryGetProperty("Channel", out channel))
                    && channel.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(channel.GetString()))
                    return channel.GetString()!.Trim().TrimStart('@');
            }
            catch (JsonException) { }
        }
        return HttpContext.RequestServices.GetRequiredService<KickAuthService>().ChannelName?.Trim().TrimStart('@') is { Length: > 0 } name ? name : "Yayıncı";
    }

    /// <summary>Set a YouTube playlist as the cyclic background (plays when no user requests pending)</summary>
    [HttpPost("set-playlist")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> SetPlaylist([FromBody] SetPlaylistRequest req)
    {
        if (!HttpContext.RequestServices.GetRequiredService<QueueSettingsService>().YouTubeEnabled)
            return BadRequest(new { error = "YouTube istekleri ayarlardan kapatılmış." });
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "URL adresi gereklidir." });

        if (!TryExtractYouTubePlaylistId(req.Url, out _))
            return BadRequest(new { error = "Arka plan kuyruğu için yalnızca geçerli listeler desteklenir." });

        var (tracks, _) = await _downloader.ImportPlaylistAsync(req.Url, 500);
        if (tracks.Count == 0)
            return BadRequest(new { error = "Çalma listesi boş veya içe aktarılamadı." });

        var services = _userContext.GetOrCreate(LocalUser.Id);
        services.Queue.SetBackgroundPlaylist(tracks);

        // Resolve already-cached files
        foreach (var song in tracks)
        {
            var cached = await _library.FindByTrackIdAsync(song.SpotifyUri);
            if (cached != null && System.IO.File.Exists(cached.FilePath))
                song.LocalFilePath = cached.FilePath;
        }

        // Download all uncached songs one at a time in the background.
        // On-demand downloads (EnsureLocalFileAsync) will skip ahead in the queue when needed.
        var toDownload = tracks.Where(s => s.LocalFilePath == null && !s.SpotifyUri.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase)).ToList();
        if (toDownload.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                foreach (var song in toDownload)
                {
                    try   { await _downloader.GetOrStartDownloadAsync(song); }
                    catch { /* already logged inside the downloader */ }
                }
            });
        }

        // Start playback if nothing is currently playing
        if (services.Queue.GetCurrentItem() == null)
        {
            services.Queue.Advance();
            await _sync.StartCurrentTrackAsync(services, userInitiated: true);
        }

        return Ok(new { count = tracks.Count, message = $"Arka plan çalma listesi {tracks.Count} şarkı ile ayarlandı." });
    }

    /// <summary>Clear the background playlist</summary>
    [HttpDelete("playlist")]
    [ProducesResponseType(204)]
    public IActionResult ClearPlaylist()
    {
        _userContext.GetOrCreate(LocalUser.Id).Queue.ClearBackgroundPlaylist();
        return NoContent();
    }

    /// <summary>Get current background playlist state</summary>
    [HttpGet("playlist")]
    [ProducesResponseType(200)]
    public IActionResult GetPlaylist()
    {
        var (songs, index) = _userContext.GetOrCreate(LocalUser.Id).Queue.GetBackgroundPlaylist();
        return Ok(new { songs, index, total = songs.Count });
    }

    private static bool TryExtractYouTubePlaylistId(string url, out string playlistId)
        => YouTubeLinks.TryPlaylistId(url, out playlistId);

    private static bool TryExtractSpotifyPlaylistId(string url, out string playlistId)
    {
        playlistId = string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Host.ToLowerInvariant() != "open.spotify.com") return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length >= 2 && parts[0] == "playlist") { playlistId = parts[1]; return true; }
        return false;
    }

    private static bool TryExtractSpotifyAlbumId(string url, out string albumId)
    {
        albumId = string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Host.ToLowerInvariant() != "open.spotify.com") return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length >= 2 && parts[0] == "album") { albumId = parts[1]; return true; }
        return false;
    }

    private static SpotifyQueueState BuildQueueState(UserServices services)
    {
        var state   = services.Queue.GetState();
        var current = state.NowPlaying.Item;

        return new SpotifyQueueState
        {
            CurrentlyPlaying = current == null ? null : new Song
            {
                SpotifyUri  = current.Song.SpotifyUri,
                Title       = current.Song.Title,
                Artist      = current.Song.Artist,
                CoverUrl    = current.Song.CoverUrl,
                DurationMs  = current.Song.DurationMs,
                RequestedBy = current.RequestedBy,
                Platform    = current.Platform,
            },
            Queue = state.Upcoming.Select(i => new Song
            {
                SpotifyUri  = i.Song.SpotifyUri,
                Title       = i.Song.Title,
                Artist      = i.Song.Artist,
                CoverUrl    = i.Song.CoverUrl,
                DurationMs  = i.Song.DurationMs,
                RequestedBy = i.RequestedBy,
                Platform    = i.Platform,
            }).ToList()
        };
    }

    [HttpPost("shuffle")]
    public IActionResult Shuffle()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        services.Queue.Shuffle();
        services.Queue.ShuffleBackgroundPlaylist();
        return Ok(new { message = "Kuyruk karıştırıldı" });
    }

    /// <summary>Randomize the in-memory background playlist without touching the persisted library</summary>
    [HttpPost("shuffle-background")]
    [ProducesResponseType(204)]
    public IActionResult ShuffleBackground()
    {
        _userContext.GetOrCreate(LocalUser.Id).Queue.ShuffleBackgroundPlaylist();
        return NoContent();
    }

    /// <summary>Promote a song from the background playlist into the user-requested queue</summary>
    [HttpPost("promote-from-background")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    public IActionResult PromoteFromBackground([FromBody] PromoteFromBackgroundRequest req)
    {
        var ok = _userContext.GetOrCreate(LocalUser.Id).Queue.PromoteFromBackground(req.Uri, req.ToIndex);
        return ok ? NoContent() : BadRequest(new { error = "Şarkı arka plan listesinde bulunamadı, zaten kuyrukta veya şu an çalıyor." });
    }

    /// <summary>Pre-warm downloads for the next N songs that are not yet cached</summary>
    [HttpPost("prewarm-next")]
    [ProducesResponseType(202)]
    public IActionResult PrewarmNext([FromQuery] int count = 2)
    {
        count = Math.Clamp(count, 1, 5);
        var services   = _userContext.GetOrCreate(LocalUser.Id);
        var candidates = services.Queue.GetNextDownloadCandidates(count);
        foreach (var song in candidates.Where(s => !s.SpotifyUri.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase)))
        {
            var captured = song;
            _ = Task.Run(async () =>
            {
                try   { await _downloader.GetOrStartDownloadAsync(captured); }
                catch { /* already logged inside the downloader */ }
            });
        }
        return Accepted();
    }
}

public class RemoveItemRequest { public string Uri { get; set; } = string.Empty; }
public class MoveItemRequest   { public string Uri { get; set; } = string.Empty; public string Direction { get; set; } = "up"; }

public class DirectSongRequest
{
    public string  SpotifyUri  { get; set; } = string.Empty;
    public string  Title       { get; set; } = string.Empty;
    public string  Artist      { get; set; } = string.Empty;
    public string? CoverUrl    { get; set; }
    public int     DurationMs  { get; set; }
    public string? RequestedBy { get; set; }
    public string? Platform    { get; set; }

    public Song ToSong() => new()
    {
        SpotifyUri = SpotifyUri,
        Title      = Title,
        Artist     = Artist,
        CoverUrl   = CoverUrl ?? "",
        DurationMs = DurationMs,
    };
}

public class ReorderRequest
{
    public string Uri     { get; set; } = string.Empty;
    public int    ToIndex { get; set; }
}

public class ImportPlaylistRequest
{
    public string  Url         { get; set; } = string.Empty;
    public string? RequestedBy { get; set; }
}

public class SetPlaylistRequest
{
    public string Url { get; set; } = string.Empty;
}

public class PromoteFromBackgroundRequest
{
    public string  Uri     { get; set; } = string.Empty;
    public int?    ToIndex { get; set; }
}
