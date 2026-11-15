using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using MusicBot.Core.Models;
using MusicBot.Hubs;
using MusicBot.Services;
using MusicBot.Data;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/liked")]
[Tags("Liked Songs")]
public class LikedController : ControllerBase
{
    private readonly PlaylistLibraryService  _playlists;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly UserContextManager      _userContext;

    public LikedController(
        PlaylistLibraryService playlists,
        IHubContext<OverlayHub> hub,
        UserContextManager userContext)
    {
        _playlists   = playlists;
        _hub         = hub;
        _userContext = userContext;
    }

    private Task BroadcastAsync() =>
        _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("playlist:status", new { });

    /// <summary>Returns all Spotify URIs the user has liked.</summary>
    [HttpGet("uris")]
    public async Task<IActionResult> GetLikedUris()
        => Ok(await _playlists.GetLikedUrisAsync());

    /// <summary>Toggles a song in/out of Liked Songs. Syncs with Spotify if track is from Spotify. Returns the new liked state.</summary>
    [HttpPost("toggle")]
    public async Task<IActionResult> Toggle([FromBody] LikedToggleRequest req)
    {
        var likedId = await _playlists.GetLikedPlaylistIdAsync();
        if (likedId == null) return BadRequest(new { error = "Beğendiklerim listesi bulunamadı" });

        var song = new Song
        {
            SpotifyUri = req.SpotifyUri,
            Title      = req.Title,
            Artist     = req.Artist,
            CoverUrl   = req.CoverUrl ?? "",
            DurationMs = req.DurationMs,
        };

        var isIn = await _playlists.IsSongInPlaylistAsync(likedId.Value, req.SpotifyUri);
        var services = _userContext.GetOrCreate(LocalUser.Id);
        string? spotifyWarning = null;

        if (isIn) await _playlists.RemoveSongAsync(likedId.Value, req.SpotifyUri);
        else await _playlists.AddSongAsync(likedId.Value, song);
        try
        {
            if (req.SpotifyUri.StartsWith("spotify:track:") && services.Spotify.IsAuthenticated)
            {
                if (isIn) await services.Spotify.RemoveSavedTrackAsync(req.SpotifyUri);
                else await services.Spotify.SaveTrackAsync(req.SpotifyUri);
                if (await services.Spotify.IsTrackSavedAsync(req.SpotifyUri) != !isIn)
                    spotifyWarning = "Spotify henüz beğeniyi doğrulamadı. Yerel beğeniniz korundu; Spotify bağlantısını kontrol edin.";
            }
            else if (req.SpotifyUri.StartsWith("youtube:"))
            {
                var youtube = HttpContext.RequestServices.GetRequiredService<MusicBot.Services.Platforms.YouTubeAuthService>();
                await youtube.RateMusicAsync(req.SpotifyUri[8..], !isIn);
            }
        }
        catch (Exception ex) { spotifyWarning = ex.Message; }
        if (spotifyWarning != null) ActivityJournal.Record(req.SpotifyUri.StartsWith("youtube:") ? "youtube" : "spotify", "Beğeni", "", spotifyWarning, false);
        await BroadcastAsync();
        return Ok(new { isLiked = !isIn, spotifyWarning });
    }

    /// <summary>Returns all playlists with a flag indicating whether this song is saved in each one.</summary>
    [HttpGet("memberships")]
    public async Task<IActionResult> GetMemberships([FromQuery] string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return BadRequest(new { error = "URI parametresi gereklidir" });
        var memberships = await _playlists.GetSongMembershipsAsync(uri);
        return Ok(memberships);
    }

    /// <summary>Toggles a song in/out of the given playlist. Returns the new membership state.</summary>
    [HttpPost("memberships/{playlistId:int}")]
    public async Task<IActionResult> ToggleMembership(int playlistId, [FromBody] LikedToggleRequest req)
    {
        var playlist = await _playlists.GetByIdAsync(playlistId);
        if (playlist == null) return NotFound();

        var song = new Song
        {
            SpotifyUri = req.SpotifyUri,
            Title      = req.Title,
            Artist     = req.Artist,
            CoverUrl   = req.CoverUrl ?? "",
            DurationMs = req.DurationMs,
        };

        var isIn = await _playlists.IsSongInPlaylistAsync(playlistId, req.SpotifyUri);
        if (isIn)
        {
            await _playlists.RemoveSongAsync(playlistId, req.SpotifyUri);
            await BroadcastAsync();
            return Ok(new { isInPlaylist = false });
        }
        await _playlists.AddSongAsync(playlistId, song);
        await BroadcastAsync();
        return Ok(new { isInPlaylist = true });
    }
}

public record LikedToggleRequest(string SpotifyUri, string Title, string Artist, string? CoverUrl, int DurationMs);
