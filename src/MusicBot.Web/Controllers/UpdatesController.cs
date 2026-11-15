using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace MusicBot.Controllers;

[ApiController, Route("api/updates")]
public sealed class UpdatesController(IHttpClientFactory factory, IHostApplicationLifetime lifetime) : ControllerBase
{
    private const string Repository = "https://github.com/sertay1/musick";
    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Musick-Desktop/" + AppInfo.Version);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
    [HttpGet]
    public async Task<IActionResult> Check()
    {
        using var response = await Client().GetAsync("https://api.github.com/repos/sertay1/musick/releases/latest", HttpContext.RequestAborted);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return Ok(new { currentVersion = AppInfo.Version, available = false, message = "Henüz yayımlanmış bir sürüm yok veya depo herkese açık değil.", releaseUrl = Repository + "/releases" });
        if (!response.IsSuccessStatusCode) return BadRequest(new { error = "GitHub sürüm bilgisi alınamadı. Daha sonra tekrar deneyin." });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var installer = FindInstaller(root);
        return Ok(new { currentVersion = AppInfo.Version, version = tag, available = IsNewer(tag, AppInfo.Version), canInstall = installer.HasValue, releaseUrl = root.GetProperty("html_url").GetString(), message = "GitHub sürüm kontrolü tamamlandı." });
    }
    internal static bool IsNewer(string version, string current) => Version.TryParse(version.TrimStart('v').Split('-')[0], out var next) && (!Version.TryParse(current.TrimStart('v').Split('-')[0], out var installed) || next > installed);
    private static JsonElement? FindInstaller(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)) return asset;
        }
        return null;
    }
    [HttpPost("install")]
    public async Task<IActionResult> Install()
    {
        using var response = await Client().GetAsync("https://api.github.com/repos/sertay1/musick/releases/latest");
        if (!response.IsSuccessStatusCode) return BadRequest(new { error = "Yüklenebilir bir GitHub sürümü bulunamadı." });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var release = json.RootElement;
        if (!IsNewer(release.GetProperty("tag_name").GetString() ?? "", AppInfo.Version)) return BadRequest(new { error = "Uygulama zaten güncel." });
        var asset = FindInstaller(release);
        if (!asset.HasValue) return BadRequest(new { error = "Release içine Musick-Setup.exe kurulum dosyasını ekleyin." });
        var url = asset.Value.GetProperty("browser_download_url").GetString() ?? "";
        if (!url.StartsWith(Repository + "/releases/download/", StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return BadRequest(new { error = "Güncelleme adresi doğrulanamadı." });
        var digest = asset.Value.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
        if (digest == null || !digest.StartsWith("sha256:") || digest.Length != 71) return BadRequest(new { error = "GitHub dosya özeti (SHA-256) bulunamadı. Güncelleme kurulmadı." });
        var directory = Path.Combine(Path.GetTempPath(), "MusickUpdates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "Musick-Setup.exe");
        using (var download = await Client().GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            download.EnsureSuccessStatusCode();
            await using var file = System.IO.File.Create(target);
            await download.Content.CopyToAsync(file);
        }
        await using (var file = System.IO.File.OpenRead(target))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
            if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) { file.Close(); System.IO.File.Delete(target); return BadRequest(new { error = "Güncelleme dosyası doğrulanamadı." }); }
        }
        MusicBot.Services.UpdateInstallerLauncher.StartAfterExit(target, Environment.ProcessId);
        _ = Task.Run(async () => { await Task.Delay(750); lifetime.StopApplication(); AppEvents.RequestShutdown(); });
        return Ok(new { message = "Kurulum açılıyor. Musick güncelleme için kapanacak." });
    }
}
