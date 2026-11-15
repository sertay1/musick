using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicBot.Data;
using MusicBot.Hubs;
using MusicBot.Services;
using MusicBot.Services.Platforms;
using MusicBot.Services.Spotify;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/auth")]
[Tags("Auth")]
public class AuthController : ControllerBase
{
    private readonly MusicBotDbContext _db;
    private readonly UserContextManager _userContext;
    private readonly KickAuthService _kickAuth;
    private readonly YouTubeAuthService _youtubeAuth;
    private readonly IHubContext<OverlayHub> _hub;

    private readonly PlatformConnectionManager _manager;

    public AuthController(
        MusicBotDbContext db,
        UserContextManager userContext,
        KickAuthService kickAuth,
        YouTubeAuthService youtubeAuth,
        IHubContext<OverlayHub> hub,
        PlatformConnectionManager manager)
    {
        _db          = db;
        _userContext = userContext;
        _kickAuth    = kickAuth;
        _youtubeAuth = youtubeAuth;
        _hub         = hub;
        _manager     = manager;
    }

    /// <summary>Get local user info and Spotify connection status</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(MeResponse), 200)]
    public IActionResult Me()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        return Ok(new MeResponse
        {
            Id               = LocalUser.Id,
            Username         = LocalUser.Username,
            OverlayToken     = LocalUser.OverlayToken,
            SpotifyConnected = services.Spotify.IsAuthenticated
        });
    }

    // ── Spotify OAuth ────────────────────────────────────────────────────────

    /// <summary>Get Spotify OAuth URL</summary>
    [HttpGet("spotify")]
    [ProducesResponseType(typeof(SpotifyAuthUrlResponse), 200)]
    public IActionResult SpotifyLogin()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var url = ((SpotifyService)services.Spotify).GetAuthUrl(MusicBot.Services.Security.OAuthStateStore.Shared.Begin("spotify"));
        return Ok(new SpotifyAuthUrlResponse { Url = url });
    }

    /// <summary>Spotify OAuth callback — handles redirect from Spotify</summary>
    [HttpGet("spotify/callback")]
    public async Task<IActionResult> SpotifyCallback(
        [FromQuery] string? code,
        [FromQuery] string? error,
        [FromQuery] string? state)
    {
        if (!MusicBot.Services.Security.OAuthStateStore.Shared.Consume("spotify", state)) return BadRequest("Yetkilendirme bağlantısı geçersiz veya süresi dolmuş.");

        if (!string.IsNullOrEmpty(error))
            return BadRequest($"Yetkilendirme başarısız: {error}");

        if (string.IsNullOrEmpty(code))
            return BadRequest("Yetkilendirme kodu eksik.");

        var services = _userContext.GetOrCreate(LocalUser.Id);
        try
        {
            await services.Spotify.HandleCallbackAsync(code);
            return Content("""
                <html>
                <body style='background:#1a1a2e;color:#e0e0e0;font-family:sans-serif;display:flex;justify-content:center;align-items:center;height:100vh;margin:0;'>
                    <div style='text-align:center;'>
                        <h1 style='color:#1DB954;'>&#10003; Spotify Bağlandı!</h1>
                        <p>Bu pencereyi kapatıp Musick’e dönebilirsiniz.</p>
                    </div>
                </body>
                </html>
                """, "text/html");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Hesap bağlantısı hatası: {ex.Message}");
        }
    }

    /// <summary>Check Spotify connection status</summary>
    [HttpGet("spotify/status")]
    [ProducesResponseType(typeof(SpotifyStatusResponse), 200)]
    public async Task<IActionResult> SpotifyStatus()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var library = services.Spotify is MusicBot.Services.Spotify.SpotifyService spotify && spotify.IsAuthenticated ? await spotify.HasLibraryAccessAsync() : null;
        return Ok(new SpotifyStatusResponse { Authenticated = services.Spotify.IsAuthenticated, LibraryAccess = library });
    }

    /// <summary>Disconnect Spotify — removes stored token</summary>
    [HttpDelete("spotify")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> SpotifyDisconnect()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        await services.Spotify.DisconnectAsync();
        return NoContent();
    }
    // ── System browser helper ─────────────────────────────────────────────────

    /// <summary>
    /// Opens a URL in the user's default system browser.
    /// Used for OAuth flows (Kick) so the user can leverage their existing sessions.
    /// </summary>
    [HttpPost("open-in-browser")]
    [ProducesResponseType(200)]
    public IActionResult OpenInBrowser([FromQuery] string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps && uri.Host is ("accounts.spotify.com" or "open.spotify.com" or "id.kick.com" or "kick.com" or "accounts.google.com" or "www.youtube.com" or "music.youtube.com" or "console.cloud.google.com" or "myaccount.google.com" or "developer.spotify.com" or "developers.kick.com" or "github.com")
              || uri.Scheme == Uri.UriSchemeHttp && uri.Host is ("127.0.0.1" or "localhost") && uri.Port == 3050 && uri.AbsolutePath.StartsWith("/overlays/")))
            return BadRequest("Geçersiz URL adresi");

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
        return Ok(new { success = true });
    }

    // ── YouTube cookie login (yt-dlp bot detection bypass) ────────────────────

    /// <summary>
    /// Opens the in-app YouTube/Google login window (WebView2). After login the Desktop
    /// layer captures cookies and writes them to a Netscape cookies.txt that yt-dlp uses.
    /// </summary>
    [HttpPost("youtube/start")]
    [ProducesResponseType(200)]
    public IActionResult YouTubeStartLogin()
    {
        try
        {
            var oauth = HttpContext.RequestServices.GetRequiredService<YouTubeOAuthService>();
            var url = oauth.Begin();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return Ok(new { message = "YouTube yetkilendirmesi varsayılan tarayıcıda açıldı." });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Get YouTube auth state — toggle on/off + connection status.</summary>
    [HttpGet("youtube/status")]
    [ProducesResponseType(200)]
    public IActionResult YouTubeStatus() => Ok(new
    {
        configured = HttpContext.RequestServices.GetRequiredService<YouTubeOAuthService>().IsConfigured,
        enabled       = _youtubeAuth.IsEnabled,
        authenticated = _youtubeAuth.IsConnected,
        account       = _youtubeAuth.AccountLabel,
        savedAt       = _youtubeAuth.SavedAt == default ? (DateTimeOffset?)null : _youtubeAuth.SavedAt,
        cancelled     = _youtubeAuth.LoginCancelled,
        error = HttpContext.RequestServices.GetRequiredService<YouTubeOAuthService>().LastError,
    });

    /// <summary>Enable the use of YouTube cookies in yt-dlp.</summary>
    [HttpPost("youtube/enable")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> YouTubeEnable()
    {
        await _youtubeAuth.EnableAsync();
        return NoContent();
    }

    /// <summary>Disable the use of YouTube cookies (cookies file is preserved on disk).</summary>
    [HttpPost("youtube/disable")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> YouTubeDisable()
    {
        await _youtubeAuth.DisableAsync();
        return NoContent();
    }

    /// <summary>Disconnect YouTube — deletes the cookies file and clears the WebView2 session.</summary>
    [HttpDelete("youtube")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> YouTubeDisconnect()
    {
        await _youtubeAuth.DisconnectAsync();
        return NoContent();
    }

    // ── Kick OAuth ────────────────────────────────────────────────────────────

    /// <summary>Get Kick OAuth URL</summary>
    [HttpGet("kick")]
    public IActionResult KickLogin()
    {
        var url = _kickAuth.GetAuthUrl(MusicBot.Services.Security.OAuthStateStore.Shared.Begin("kick"));
        return Ok(new { url });
    }

    /// <summary>Kick OAuth callback — handles redirect from Kick</summary>
    [HttpGet("kick/callback")]
    public async Task<IActionResult> KickCallback(
        [FromQuery] string? code,
        [FromQuery] string? error,
        [FromQuery] string? state)
    {
        if (!MusicBot.Services.Security.OAuthStateStore.Shared.Consume("kick", state)) return BadRequest("Yetkilendirme bağlantısı geçersiz veya süresi dolmuş.");

        if (!string.IsNullOrEmpty(error))
            return BadRequest($"Yetkilendirme başarısız: {error}");

        if (string.IsNullOrEmpty(code))
            return BadRequest("Yetkilendirme kodu eksik.");

        try
        {
            await _kickAuth.HandleCallbackAsync(code);

            // Auto-connect Kick if channel is known
            try
            {
                var cfg = await _db.PlatformConfigs.FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "kick");
                if (cfg != null)
                {
                    cfg.AutoConnect = true;
                    await _db.SaveChangesAsync();

                    using var doc = System.Text.Json.JsonDocument.Parse(cfg.ConfigJson);
                    var ch = doc.RootElement.TryGetProperty("channel", out var cp) ? cp.GetString() : null;
                    var roles = doc.RootElement.TryGetProperty("commandRoles", out var rp) && rp.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? rp.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToArray()
                        : null;
                    var users = doc.RootElement.TryGetProperty("allowedUsers", out var up) && up.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? up.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToArray()
                        : null;

                    var targetChannel = !string.IsNullOrWhiteSpace(ch) ? ch : (_kickAuth.ChannelName ?? "");
                    if (!string.IsNullOrWhiteSpace(targetChannel))
                    {
                        _manager.SetUserSlug(LocalUser.Id, LocalUser.Slug);
                        _manager.ConnectKick(LocalUser.Id, new PlatformConnectionManager.KickPlatformConfig(targetChannel, roles, users));
                    }
                }
            }
            catch { /* non-fatal */ }

            await _hub.Clients.Group($"user:{LocalUser.Id}")
                .SendAsync("auth:updated", new { platform = "kick", authenticated = true, channel = _kickAuth.ChannelName });

            return Content("""
                <!DOCTYPE html>
                <html lang='tr'>
                <head><meta charset='utf-8'><title>Kick Bağlantısı Başarılı - Musick</title></head>
                <body style='background:#0f172a;color:#f8fafc;font-family:Segoe UI, sans-serif;display:flex;justify-content:center;align-items:center;height:100vh;margin:0;'>
                    <div style='text-align:center;padding:32px;background:#1e293b;border:1px solid #334155;border-radius:12px;box-shadow:0 8px 24px rgba(0,0,0,0.4);max-width:400px;'>
                        <div style='font-size:48px;margin-bottom:12px;'>🟢</div>
                        <h1 style='color:#53fc18;font-size:22px;margin:0 0 10px;'>Kick Bağlantısı Başarılı!</h1>
                        <p style='color:#94a3b8;font-size:14px;margin:0 0 18px;'>Hesabınız Musick ile başarıyla bağlandı ve sohbet dinleyici aktif edildi.</p>
                        <p style='color:#64748b;font-size:12px;margin:0;'>Bu pencereyi güvenle kapatıp uygulamaya dönebilirsiniz.</p>
                    </div>
                </body>
                </html>
                """, "text/html");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Hesap bağlantısı hatası: {ex.Message}");
        }
    }

    /// <summary>Check Kick OAuth status</summary>
    [HttpGet("kick/status")]
    public IActionResult KickStatus()
    {
        return Ok(new { authenticated = _kickAuth.IsAuthenticated, channel = _kickAuth.ChannelName });
    }

    /// <summary>Disconnect Kick — removes stored token</summary>
    [HttpDelete("kick")]
    public async Task<IActionResult> KickDisconnect()
    {
        await _kickAuth.DisconnectAsync();
        return NoContent();
    }

}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class MeResponse
{
    public Guid   Id               { get; set; }
    public string Username         { get; set; } = string.Empty;
    public string OverlayToken     { get; set; } = string.Empty;
    public bool   SpotifyConnected { get; set; }
}

public class SpotifyAuthUrlResponse { public string Url { get; set; } = string.Empty; }
public class SpotifyStatusResponse  { public bool Authenticated { get; set; } public bool? LibraryAccess { get; set; } }
public class ErrorResponse          { public string Error { get; set; } = string.Empty; }
