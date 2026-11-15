using Microsoft.AspNetCore.Mvc;
using MusicBot.Services;
using MusicBot.Services.Player;

namespace MusicBot.Controllers;

[ApiController, Route("api/youtube-player")]
public sealed class YouTubePlayerController(UserContextManager users, PlaybackSyncService playback) : ControllerBase
{
    [HttpGet] public IActionResult Get() => Ok(((HybridPlayerService)users.GetOrCreate(LocalUser.Id).Player).Snapshot());
    [HttpPost("status")]
    public IActionResult Status([FromBody] Report request)
    {
        var player = (HybridPlayerService)users.GetOrCreate(LocalUser.Id).Player;
        if (request.Error is > 0 && request.State.Version == player.PlaybackVersion)
            ActivityJournal.Record("youtube", "Oynatma", "", $"YouTube oynatma hatası: {request.Error}. Video gömülü oynatmaya kapalı olabilir.", false);
        if (!player.Report(request.State, request.Ended || request.Error is > 0, request.Error))
            return Conflict(new { action = "restore", state = player.Snapshot() });
        if (request.Error is > 0)
            _ = playback.RecoverYouTubePlaybackAsync(users.GetOrCreate(LocalUser.Id), request.State.Version, request.Error.Value);
        return NoContent();
    }
    public record Report(BrowserPlayback State, bool Ended, int? Error);
}
