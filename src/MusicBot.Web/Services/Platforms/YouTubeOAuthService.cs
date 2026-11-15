using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Models;
using MusicBot.Data;
using MusicBot.Hubs;
using MusicBot.Services.Security;

namespace MusicBot.Services.Platforms;

// Browser OAuth is separate from optional legacy downloader cookies. Browser cookies are never read.
public sealed class YouTubeOAuthService
{
    public const string CallbackPath = "/api/auth/google/callback";
    private readonly IServiceScopeFactory _scopes;
    private readonly string _clientId;
    private readonly string _clientSecret;
    public bool IsConfigured => System.Text.RegularExpressions.Regex.IsMatch(_clientId, @"^[0-9]+-[a-zA-Z0-9_-]+\.apps\.googleusercontent\.com$")
        && Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_clientId))) != "3B51171DCF61EE19F4142D8BE2CB56D849709728ABEADF6648719558B1F00AEB";
    private readonly IHttpClientFactory _http;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly SemaphoreSlim _tokens = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Verifier, DateTimeOffset Expires)> _pending = new();
    private string? _access, _refresh, _boundClient;
    private DateTimeOffset _expires;
    public string? Account { get; private set; }
    public string? LastError { get; private set; }
    public bool IsConnected => IsConfigured && _boundClient == _clientId && (!string.IsNullOrEmpty(_refresh) || !string.IsNullOrEmpty(_access) && _expires > DateTimeOffset.UtcNow);
    private readonly Task _loaded;
    public Task EnsureLoadedAsync() => _loaded;
    public YouTubeOAuthService(IServiceScopeFactory scopes, IConfiguration config, IHttpClientFactory http, IHubContext<OverlayHub> hub)
    { _scopes = scopes; _clientId = config["GoogleOAuth:ClientId"]?.Trim() ?? ""; _clientSecret = config["GoogleOAuth:ClientSecret"] ?? ""; _http = http; _hub = hub; _loaded = LoadAsync(); }

    public string Begin()
    {
        var client = _clientId;
        if (!IsConfigured) throw new InvalidOperationException("Bu Musick sürümünde YouTube hesap bağlantısı henüz hazır değil. Şarkı çalmaya devam edebilirsiniz.");
        foreach (var entry in _pending.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow)) _pending.TryRemove(entry.Key, out _);
        var state = "yt-" + Guid.NewGuid().ToString("N");
        var verifier = Base64(RandomNumberGenerator.GetBytes(32));
        _pending[state] = (verifier, DateTimeOffset.UtcNow.AddMinutes(10));
        LastError = null;
        var redirect = "http://127.0.0.1:3050" + CallbackPath;
        return "https://accounts.google.com/o/oauth2/v2/auth?client_id=" + Uri.EscapeDataString(client)
            + "&redirect_uri=" + Uri.EscapeDataString(redirect) + "&response_type=code&access_type=offline&prompt=consent"
            + "&scope=" + Uri.EscapeDataString("https://www.googleapis.com/auth/youtube.force-ssl")
            + "&state=" + state + "&code_challenge_method=S256&code_challenge=" + Base64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public async Task CompleteAsync(string state, string? code, string? error)
    {
        if (!_pending.TryRemove(state, out var pending) || pending.Expires <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("YouTube giriş bağlantısı geçersiz veya süresi dolmuş.");
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) { LastError = "YouTube yetkilendirmesi iptal edildi veya reddedildi."; throw new InvalidOperationException(LastError); }
        await _loaded;
        await _tokens.WaitAsync();
        try
        {
            using var json = await ExchangeAsync(new { grant_type = "authorization_code", code, redirect_uri = "http://127.0.0.1:3050" + CallbackPath, code_verifier = pending.Verifier });
            var root = json.RootElement;
            if (root.TryGetProperty("scope", out var scope) && !(scope.GetString() ?? "").Split(' ').Contains("https://www.googleapis.com/auth/youtube.force-ssl"))
                throw new InvalidOperationException("YouTube listeleri ve beğenileri için istenen izin verilmedi.");
            ApplyTokens(root);
            await SaveAsync();
        }
        catch (Exception ex) { LastError = ex.Message; throw; }
        finally { _tokens.Release(); }
        try
        {
            using var info = await GetAsync("channels?part=snippet&mine=true");
            var entries = info.RootElement.GetProperty("items");
            Account = entries.GetArrayLength() > 0 ? entries[0].GetProperty("snippet").GetProperty("title").GetString() : "YouTube";
            await SaveAsync();
        }
        catch { Account = "YouTube"; }
        LastError = null;
        await _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("auth:updated", new { platform = "youtube", authenticated = true, account = Account });
    }
    private void ApplyTokens(JsonElement root)
    {
        _boundClient = _clientId;
        _access = root.GetProperty("access_token").GetString();
        if (root.TryGetProperty("refresh_token", out var refresh)) _refresh = refresh.GetString();
        _expires = DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600);
    }
    private async Task<JsonDocument> ExchangeAsync(object body)
    {
        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var fields = JsonSerializer.SerializeToElement(body).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        fields["client_id"] = _clientId;
        var secret = _clientSecret;
        if (!string.IsNullOrEmpty(secret)) fields["client_secret"] = secret;
        using var response = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(fields));
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"YouTube yetkilendirmesi tamamlanamadı (HTTP {(int)response.StatusCode}). Google OAuth istemcisinin YouTube izinlerini kontrol edin.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    private async Task<string> AccessAsync()
    {
        await _loaded;
        await _tokens.WaitAsync();
        try
        {
            if (!IsConnected) throw new InvalidOperationException("YouTube hesabınızı Musick üzerinden yeniden bağlayın.");
            if (_access != null && _expires > DateTimeOffset.UtcNow.AddMinutes(1)) return _access;
            if (string.IsNullOrWhiteSpace(_refresh)) throw new InvalidOperationException("YouTube hesabını varsayılan tarayıcıdan yeniden bağlayın.");
            using var json = await ExchangeAsync(new { grant_type = "refresh_token", refresh_token = _refresh });
            ApplyTokens(json.RootElement); await SaveAsync(); return _access!;
        }
        finally { _tokens.Release(); }
    }
    private async Task<JsonDocument> GetAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessAsync());
        using var client = _http.CreateClient(); client.Timeout = TimeSpan.FromSeconds(30);
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"YouTube API isteği başarısız (HTTP {(int)response.StatusCode}). Hesabın izinlerini ve YouTube Data API erişimini kontrol edin.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    public async Task RateAsync(string id, bool like)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/youtube/v3/videos/rate?id=" + Uri.EscapeDataString(id) + "&rating=" + (like ? "like" : "none"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessAsync());
        using var client = _http.CreateClient(); client.Timeout = TimeSpan.FromSeconds(30);
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"YouTube beğenisi eşitlenemedi (HTTP {(int)response.StatusCode}). Yerel beğeni korundu.");
    }
    public async Task<List<(string Url, string Title)>> PlaylistsAsync()
    {
        var result = new List<(string, string)>(); string? page = null; var seen = new HashSet<string>();
        do
        {
            using var json = await GetAsync("playlists?part=snippet&mine=true&maxResults=50" + (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page)));
            foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
                result.Add(("https://www.youtube.com/playlist?list=" + item.GetProperty("id").GetString(), item.GetProperty("snippet").GetProperty("title").GetString() ?? "YouTube"));
            page = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (!string.IsNullOrEmpty(page) && seen.Add(page));
        return result;
    }
    public async Task<List<Song>> PlaylistTracksAsync(string id)
    {
        var result = new List<Song>(); string? page = null; var seen = new HashSet<string>();
        do
        {
            using var json = await GetAsync("playlistItems?part=snippet&playlistId=" + Uri.EscapeDataString(id) + "&maxResults=50" + (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page)));
            foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
            {
                var snippet = item.GetProperty("snippet");
                if (!snippet.TryGetProperty("resourceId", out var resource) || !resource.TryGetProperty("videoId", out var video)) continue;
                var title = snippet.GetProperty("title").GetString() ?? "";
                if (title is "Deleted video" or "Private video") continue;
                var cover = "";
                if (snippet.TryGetProperty("thumbnails", out var thumbs) && thumbs.TryGetProperty("medium", out var medium)) cover = medium.GetProperty("url").GetString() ?? "";
                result.Add(new Song { SpotifyUri = "youtube:" + video.GetString(), Title = title, Artist = snippet.TryGetProperty("videoOwnerChannelTitle", out var author) ? author.GetString() ?? "" : "", CoverUrl = cover });
            }
            page = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (!string.IsNullOrEmpty(page) && seen.Add(page));
        return result;
    }
    public async Task DisconnectAsync()
    {
        await _loaded; await _tokens.WaitAsync();
        try { _pending.Clear(); _access = _refresh = Account = null; _expires = default; LastError = null; await SaveAsync(); }
        finally { _tokens.Release(); }
    }
    private async Task LoadAsync()
    {
        try
        {
            using var scope = _scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            var row = await db.PlatformConfigs.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "youtube_oauth");
            if (row == null) return;
            var stored = JsonSerializer.Deserialize<Stored>(row.ConfigJson); if (stored == null) return;
            if (!IsConfigured || stored.ClientId != _clientId)
            { db.PlatformConfigs.Remove(row); await db.SaveChangesAsync(); return; }
            _boundClient = stored.ClientId;
            _access = TokenProtection.Unprotect(stored.Access); _refresh = TokenProtection.Unprotect(stored.Refresh); _expires = stored.Expires; Account = stored.Account;
        }
        catch { /* Missing/unreadable tokens require a fresh browser login. */ }
    }
    private async Task SaveAsync()
    {
        using var scope = _scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var row = await db.PlatformConfigs.FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "youtube_oauth");
        if (row == null) { row = new() { UserId = LocalUser.Id, Platform = "youtube_oauth" }; db.PlatformConfigs.Add(row); }
        row.ConfigJson = JsonSerializer.Serialize(new Stored(TokenProtection.Protect(_access), TokenProtection.Protect(_refresh), _expires, Account, _boundClient));
        await db.SaveChangesAsync();
    }
    private record Stored(string Access, string Refresh, DateTimeOffset Expires, string? Account, string? ClientId = null);
}
