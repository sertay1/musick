using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Mvc;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Services;
using MusicBot.Services.Downloader;

namespace MusicBot.Controllers;

[ApiController]
[Route("api")]
[Tags("Commands")]
public class CommandsController : ControllerBase
{
    private readonly CommandRouterService  _router;
    private readonly UserContextManager    _userContext;
    private readonly PlaybackSyncService   _sync;
    private readonly YtDlpDownloaderService _downloader;
    private readonly IMetadataService       _metadata;
    private readonly MusicBot.Services.Commands.CommandConfigService _cmdConfig;
    private readonly QueueSettingsService _queueSettings;

    public CommandsController(
        CommandRouterService router,
        UserContextManager userContext,
        PlaybackSyncService sync,
        YtDlpDownloaderService downloader,
        IMetadataService metadata,
        MusicBot.Services.Commands.CommandConfigService cmdConfig,
        QueueSettingsService queueSettings)
    {
        _router        = router;
        _userContext   = userContext;
        _sync          = sync;
        _downloader    = downloader;
        _metadata      = metadata;
        _cmdConfig     = cmdConfig;
        _queueSettings = queueSettings;
    }

    /// <summary>
    /// Search for songs. YouTube is the primary source (finds the actual video to download).
    /// iTunes and Spotify run in parallel and enrich YouTube results with clean metadata.
    /// If YouTube search is slow or returns nothing, iTunes/Spotify results fill the list.
    /// </summary>
    private static readonly Microsoft.Extensions.Caching.Memory.MemoryCache SearchCache = new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 256 });
    [HttpGet("search")]
    [ProducesResponseType(typeof(List<Song>), 200)]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 10, [FromQuery] string source = "all")
    {
        if (string.IsNullOrWhiteSpace(q)) return Ok(Array.Empty<Song>());
        limit = Math.Clamp(limit, 1, 20); q = q.Trim();
        if (source is not ("all" or "spotify" or "youtube")) return BadRequest(new { error = "Arama kaynağı tanınmadı." });
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var key = (LocalUser.Id, q.ToLowerInvariant(), limit, source, services.Spotify.IsAuthenticated, _queueSettings.SpotifyEnabled, _queueSettings.YouTubeEnabled);
        if (SearchCache.TryGetValue(key, out List<Song>? cached)) return Ok(cached);
        async Task<List<Song>> Find(string platform)
        {
            try
            {
                if (platform == "spotify" && !services.Spotify.IsAuthenticated) return [];
                if (platform == "spotify" && !_queueSettings.SpotifyEnabled || platform == "youtube" && !_queueSettings.YouTubeEnabled) return [];
                if (YouTubeLinks.IsYouTube(q))
                {
                    if (platform != "youtube") return [];
                    if (!YouTubeLinks.TryVideoId(q, out var videoId)) return [];
                    return [await _downloader.ValidateMusicVideoAsync("youtube:" + videoId)];
                }
                var tracks = await (platform == "spotify" ? services.Spotify.SearchAsync(q, Math.Min(limit, 10)) : _downloader.SearchAsync(q, limit, HttpContext?.RequestAborted ?? default)).WaitAsync(TimeSpan.FromSeconds(platform == "youtube" ? 10 : 6));
                foreach (var song in tracks) song.Platform = platform;
                return tracks.Where(t => !t.IsPlaylist).ToList();
            }
            catch { return []; }
        }
        List<Song> songs;
        if (source == "all")
        {
            var both = await Task.WhenAll(Find("spotify"), Find("youtube"));
            songs = Enumerable.Range(0, Math.Max(both[0].Count, both[1].Count)).SelectMany(i => both.Where(list => i < list.Count).Select(list => list[i])).Take(limit).ToList();
        }
        else songs = await Find(source);
        if (songs.Count > 0) SearchCache.Set(key, songs, new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });
        return Ok(songs);
    }

    private static double MetaMatchScore(Song yt, Song meta)
    {
        var ytTitle  = Norm(yt.Title);
        var ytArtist = Norm(yt.Artist);
        var mtTitle  = Norm(meta.Title);
        var mtArtist = Norm(meta.Artist);
        double score = 0;
        if (ytTitle.Length > 1 && mtTitle.Length > 1 && (ytTitle.Contains(mtTitle) || mtTitle.Contains(ytTitle))) score += 2;
        if (ytArtist.Length > 1 && mtArtist.Length > 1 && (ytArtist.Contains(mtArtist) || mtArtist.Contains(ytArtist))) score += 2;
        if (yt.DurationMs > 0 && meta.DurationMs > 0 && Math.Abs(yt.DurationMs - meta.DurationMs) < 6000) score += 1;
        return score;
    }

    private static string Norm(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^\w\s]", "").Trim();

    /// <summary>Preview tracks from a YouTube playlist URL without saving anything.</summary>
    [HttpGet("search/playlist-tracks")]
    [ProducesResponseType(typeof(List<Song>), 200)]
    public async Task<IActionResult> GetPlaylistTracks([FromQuery] string url, [FromQuery] int limit = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(url))
            return BadRequest(new { error = "Bağlantı adresi gerekli." });
        try
        {
            var spotify = _userContext.GetOrCreate(LocalUser.Id).Spotify;
            if (CommandRouterService.TryExtractSpotifyPlaylistId(url, out var playlist))
            {
                if (!spotify.IsAuthenticated) return BadRequest(new { error = "Tam Spotify liste aktarımı için Spotify hesabını bağla." });
                return Ok(await spotify.GetPlaylistTracksAsync(playlist, limit));
            }
            if (CommandRouterService.TryExtractSpotifyAlbumId(url, out var album))
            {
                if (!spotify.IsAuthenticated) return BadRequest(new { error = "Tam Spotify albüm aktarımı için Spotify hesabını bağla." });
                return Ok(await spotify.GetAlbumTracksAsync(album, limit));
            }
            var (tracks, _) = await _downloader.ImportPlaylistAsync(url, limit);
            return Ok(tracks);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Search and add a song to the queue</summary>
    [HttpPost("play")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    public async Task<IActionResult> Play([FromBody] PlayRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(CommandResult.Fail("Şarkı sorgusu boş bırakılamaz."));

        var result = await _router.HandleAsync(new BotCommand
        {
            Type        = "play",
            Query       = request.Query,
            RequestedBy = request.RequestedBy ?? "Anonymous",
            Platform    = request.Platform    ?? "web"
        }, _userContext.GetOrCreate(LocalUser.Id));

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Skip to the next song</summary>
    [HttpPost("skip")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    public async Task<IActionResult> Skip([FromBody] BaseRequest request)
    {
        var result = await _router.HandleAsync(new BotCommand
        {
            Type        = "skip",
            RequestedBy = request.RequestedBy ?? "Anonymous",
            Platform    = "http"
        }, _userContext.GetOrCreate(LocalUser.Id));

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Bump a user's song up one position in the queue</summary>
    [HttpPost("bump")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    public async Task<IActionResult> Bump([FromBody] BaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestedBy))
            return BadRequest(CommandResult.Fail("İsteyen kullanıcı gerekli."));

        var result = await _router.HandleAsync(new BotCommand
        {
            Type        = "bump",
            RequestedBy = request.RequestedBy,
            Platform    = "http"
        }, _userContext.GetOrCreate(LocalUser.Id));

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Pause playback (both Local Player and Spotify)</summary>
    [HttpPost("pause")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Pause()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        await _sync.PauseCurrentAsync(services);
        return NoContent();
    }

    /// <summary>Resume playback (both Local Player and Spotify, or start current track)</summary>
    [HttpPost("resume")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Resume()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        _sync.EnsureSpotifyAvailable(services);
        services.SpotifyNeedsUserAction = false;
        services.PlaybackPausedByUser = false;
        var generation = Interlocked.Increment(ref services.PlaybackGeneration);
        var selected = services.Queue.GetCurrentItem()?.Song;
        if (selected?.SpotifyUri.StartsWith("youtube:") == true && !_queueSettings.YouTubeEnabled || selected?.SpotifyUri.StartsWith("spotify:") == true && !_queueSettings.SpotifyEnabled)
            return BadRequest(new { error = "Bu müzik kaynağı ayarlardan kapatılmış." });
        if (selected == null && !_queueSettings.SpotifyEnabled && services.Player.CurrentFilePath == null) return BadRequest(new { error = "Oynatılacak etkin müzik kaynağı yok." });
        if (_sync.UsesSpotify(services))
        {
            var pb = await services.Spotify.GetPlaybackStateAsync();
            var curItem = services.Queue.GetCurrentItem();

            if (generation != Interlocked.Read(ref services.PlaybackGeneration) || services.PlaybackPausedByUser) return NoContent();
            if (pb != null && !string.IsNullOrEmpty(pb.TrackUri))
            {
                // Play/Pause controls the actual transport, not a failed pending selection.
                if (!pb.IsPlaying) await ResumeSpotifyAsync(services, generation);
                if (generation != Interlocked.Read(ref services.PlaybackGeneration) || services.PlaybackPausedByUser) return NoContent();
                services.SpotifyNeedsUserAction = curItem != null && !pb.MatchesTrack(curItem.Song.SpotifyUri);
                services.Queue.UpdateProgress(pb.ProgressMs, true, new Song { SpotifyUri = pb.TrackUri, Title = pb.Title, Artist = pb.Artist, CoverUrl = pb.CoverUrl, DurationMs = pb.DurationMs });
                return NoContent();
            }
            if (pb == null || string.IsNullOrEmpty(pb.TrackUri))
            {
                if (curItem != null)
                {
                    await _sync.StartCurrentTrackAsync(services, userInitiated: true);
                }
                else if (services.Queue.GetUpcoming().Count > 0)
                {
                    var next = services.Queue.Advance();
                    if (next != null)
                    {
                        await _sync.StartCurrentTrackAsync(services, userInitiated: true);
                    }
                }
                else
                {
                    await ResumeSpotifyAsync(services,generation);
                }
            }
            else
            {
                await ResumeSpotifyAsync(services,generation);
            }
        }
        else if (services.Player.CurrentFilePath != null)
        {
            await services.Player.ResumeAsync();
        }
        else if (services.Queue.GetCurrentItem() != null)
        {
            await _sync.StartCurrentTrackAsync(services, userInitiated: true);
        }
        else if (services.Spotify.IsAuthenticated)
        {
            await ResumeSpotifyAsync(services,generation);
        }

        if (generation != Interlocked.Read(ref services.PlaybackGeneration) || services.PlaybackPausedByUser || services.SpotifyNeedsUserAction) return NoContent();
        var np = services.Queue.GetNowPlaying();
        services.Queue.UpdateProgress(np.ProgressMs, true, np.SpotifyTrack);
        return NoContent();
    }

    private static async Task ResumeSpotifyAsync(UserServices services,long generation)
    {
        bool Current() => generation == Interlocked.Read(ref services.PlaybackGeneration) && !services.PlaybackPausedByUser
            && services.Queue.GetCurrentItem()?.Song.SpotifyUri.StartsWith("youtube:") != true;
        try
        {
            if (services.Spotify is MusicBot.Services.Spotify.SpotifyService spotify) await spotify.ResumeIfCurrentAsync(Current);
            else if (Current()) await services.Spotify.ResumeAsync();
        }
        catch
        {
            if (!Current()) return;
            services.SpotifyNeedsUserAction = true;
            var state = services.Queue.GetNowPlaying();
            services.Queue.UpdateProgress(state.ProgressMs, false, state.SpotifyTrack);
            throw;
        }
    }

    /// <summary>Register a skip vote (!si = skip, !no = keep)</summary>
    [HttpPost("vote")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    public async Task<IActionResult> Vote([FromBody] VoteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username))
            return BadRequest(CommandResult.Fail("Kullanıcı adı gerekli."));

        var result = await _router.HandleAsync(new BotCommand
        {
            Type        = req.Skip ? "si" : "no",
            RequestedBy = req.Username,
            Platform    = req.Platform ?? "web"
        }, _userContext.GetOrCreate(LocalUser.Id));

        return Ok(result);
    }

    /// <summary>Get current commands configuration (from commands.json)</summary>
    [HttpGet("commands/config")]
    [ProducesResponseType(typeof(CommandsConfiguration), 200)]
    public IActionResult GetConfig()
    {
        return Ok(_cmdConfig.Current);
    }

    /// <summary>Update commands configuration and save to commands.json</summary>
    [HttpPut("commands/config")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> UpdateConfig([FromBody] CommandsConfiguration config)
    {
        try { CommandConfigTransfer.Validate(config); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        var saved = await _cmdConfig.SaveConfigurationAsync(config);
        return saved ? Ok(config) : StatusCode(500, "commands.json kaydedilirken hata oluştu.");
    }

    [HttpPost("commands/config/import")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> ImportConfig([FromBody] System.Text.Json.JsonElement document)
    {
        CommandsConfiguration config;
        try { config = CommandConfigTransfer.Read(document); }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        { return BadRequest(new { error = ex.Message }); }
        return await _cmdConfig.SaveConfigurationAsync(config) ? Ok(config)
            : StatusCode(500, new { error = "Komutlar içe aktarılırken kaydedilemedi." });
    }

    /// <summary>Force reload commands.json from disk</summary>
    [HttpPost("commands/config/reload")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    public IActionResult ReloadConfig()
    {
        var reloaded = _cmdConfig.LoadConfiguration(logSuccess: true);
        return reloaded ? Ok(_cmdConfig.Current) : BadRequest("commands.json okunamadı veya sözdizim hatası var.");
    }
}

public class PlayRequest
{
    public string  Query       { get; set; } = string.Empty;
    public string? RequestedBy { get; set; }
    public string? Platform    { get; set; }
}

public class BaseRequest
{
    public string? RequestedBy { get; set; }
}

public class VoteRequest
{
    public string  Username { get; set; } = string.Empty;
    public bool    Skip     { get; set; }
    public string? Platform { get; set; }
}
