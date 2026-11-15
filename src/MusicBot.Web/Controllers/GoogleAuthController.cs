using Microsoft.AspNetCore.Mvc;
using MusicBot.Services.Platforms;
namespace MusicBot.Controllers;
[ApiController, Route("api/auth/google")]
public sealed class GoogleAuthController(YouTubeOAuthService youtube) : ControllerBase
{
    [HttpPost("start")]
    public IActionResult Start() => BadRequest(new { error = "Eski Google giriş akışı kaldırıldı. Kendi istemcinizi Ayarlar → Hesaplar bölümünde yapılandırın." });
    [HttpGet("callback")]
    public async Task<ContentResult> Callback(string? code, string? state, string? error)
    {
        try
        {
            if (state?.StartsWith("yt-", StringComparison.Ordinal) != true) throw new InvalidOperationException("Geçersiz yetkilendirme yanıtı.");
            await youtube.CompleteAsync(state, code, error);
            return Page("YouTube bağlandı. Bu sekmeyi kapatıp Musick’e dönebilirsiniz.", 200);
        }
        catch (Exception ex) { return Page(ex.Message, 400); }
    }
    private static ContentResult Page(string message, int status) => new() { StatusCode = status, ContentType = "text/html; charset=utf-8",
        Content = "<html lang='tr'><meta charset='utf-8'><title>Musick</title><body style='background:#191a18;color:#eeeae2;font-family:Segoe UI;text-align:center;padding:60px'>" + System.Net.WebUtility.HtmlEncode(message) + "</body></html>" };
}
