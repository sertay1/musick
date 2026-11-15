using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MusicBot.Data;
using MusicBot.Services;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/artwork")]
public class ArtworkController(UserContextManager users, MusicBotDbContext db, IMemoryCache cache) : ControllerBase
{
    private static readonly SemaphoreSlim FetchSlots = new(2, 2);

    [HttpGet("image")]
    public async Task<IActionResult> Image([FromQuery] string trackId)
    {
        if (Regex.IsMatch(trackId, @"^youtube:[a-zA-Z0-9_-]{11}$"))
            return Redirect($"https://i.ytimg.com/vi/{trackId[8..]}/mqdefault.jpg");
        if (!Regex.IsMatch(trackId, @"^spotify:track:[a-zA-Z0-9]{22}$")) return NotFound();

        var url = await cache.GetOrCreateAsync("cover:" + trackId, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
            await FetchSlots.WaitAsync(HttpContext.RequestAborted);
            try
            {
                var song = await users.GetOrCreate(LocalUser.Id).Spotify.GetTrackAsync(trackId[14..]);
                return song?.CoverUrl ?? "";
            }
            catch { return ""; }
            finally { FetchSlots.Release(); }
        });
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return NotFound();
        // Repair old imports so subsequent views can load the cover directly.
        await db.PlaylistLibrarySongs.Where(s => s.SpotifyUri == trackId && (s.CoverUrl == null || s.CoverUrl == ""))
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.CoverUrl, url));
        Response.Headers.CacheControl = "public,max-age=900";
        return Redirect(url);
    }
}
