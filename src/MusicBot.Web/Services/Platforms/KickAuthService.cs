using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicBot.Data;
using MusicBot.Hubs;
using MusicBot.Services.Security;

namespace MusicBot.Services.Platforms;

/// <summary>
/// Manages Kick OAuth 2.1 Authorization Code + PKCE flow.
/// Used to identify the streamer's channel name and optionally send chat messages.
/// Stores tokens in PlatformConfig table under platform = "kick_auth".
/// </summary>
public class KickAuthService
{
    private readonly KickSettings _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly ILogger<KickAuthService> _logger;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _accessToken;
    private string? _refreshToken;
    private string? _channelName;
    private int     _userId;
    private DateTimeOffset _expiresAt;

    // PKCE verifier stored in memory between GetAuthUrl and HandleCallbackAsync
    private string? _pendingCodeVerifier;

    public bool    IsAuthenticated => !string.IsNullOrEmpty(_accessToken);
    public string? ChannelName     => _channelName;
    public int     UserId          => _userId;
    public int     TargetBroadcasterUserId { get; set; }
    public KickSettings Settings   => _settings;

    public async Task<int> ResolveBroadcasterUserIdAsync(string channelSlug)
    {
        if (string.IsNullOrWhiteSpace(channelSlug)) return 0;
        channelSlug = channelSlug.Trim().TrimStart('@');
        if (_userId > 0 && string.Equals(channelSlug, _channelName, StringComparison.OrdinalIgnoreCase))
            return TargetBroadcasterUserId = _userId;
        try
        {
            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var res = await client.GetStringAsync($"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channelSlug)}", timeout.Token);
            using var doc = JsonDocument.Parse(res);
            if (doc.RootElement.TryGetProperty("user_id", out var uidProp) && uidProp.GetInt32() > 0)
            {
                TargetBroadcasterUserId = uidProp.GetInt32();
                _logger.LogInformation("Resolved broadcaster user_id={Id} for Kick channel '{Channel}'", TargetBroadcasterUserId, channelSlug);
                return TargetBroadcasterUserId;
            }
            if (doc.RootElement.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                && user.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var userId) && userId > 0)
            {
                TargetBroadcasterUserId = idProp.GetInt32();
                _logger.LogInformation("Resolved broadcaster user_id={Id} from channel id for Kick channel '{Channel}'", TargetBroadcasterUserId, channelSlug);
                return TargetBroadcasterUserId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve broadcaster user_id for channel {Channel}", channelSlug);
        }
        return 0;
    }

    public void UpdateCredentials(string clientId, string clientSecret)
    {
        _settings.ClientId = clientId;
        _settings.ClientSecret = clientSecret;
    }

    public KickAuthService(
        IOptions<KickSettings> settings,
        IHttpClientFactory httpFactory,
        IServiceScopeFactory scopeFactory,
        IHubContext<OverlayHub> hub,
        ILogger<KickAuthService> logger)
    {
        _settings     = settings.Value;
        _httpFactory  = httpFactory;
        _scopeFactory = scopeFactory;
        _hub          = hub;
        _logger       = logger;
        LoadTokenFromDb();
    }

    /// <summary>Generates the Kick OAuth 2.1 + PKCE authorization URL.</summary>
    public string GetAuthUrl(string? state = null)
    {
        if (string.IsNullOrWhiteSpace(_settings.ClientId))
            throw new InvalidOperationException("Kick ClientId yapılandırılmamış.");

        var verifier  = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        _pendingCodeVerifier = verifier;

        var url = "https://id.kick.com/oauth/authorize" +
                  $"?response_type=code" +
                  $"&client_id={Uri.EscapeDataString(_settings.ClientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(_settings.RedirectUri)}" +
                  $"&scope={Uri.EscapeDataString("user:read chat:write")}" +
                  $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                  $"&code_challenge_method=S256";
        if (!string.IsNullOrEmpty(state))
            url += $"&state={Uri.EscapeDataString(state)}";
        return url;
    }

    /// <summary>Exchanges an authorization code for access + refresh tokens.</summary>
    public async Task HandleCallbackAsync(string code)
    {
        if (_pendingCodeVerifier == null)
            throw new InvalidOperationException("No PKCE code verifier — call GetAuthUrl first");

        var verifier = _pendingCodeVerifier;
        _pendingCodeVerifier = null;

        var response = await SendTokenRequest(new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["code"]          = code,
            ["redirect_uri"]  = _settings.RedirectUri,
            ["code_verifier"] = verifier,
        });
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        _accessToken  = json.RootElement.GetProperty("access_token").GetString();
        _refreshToken = json.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = json.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        _expiresAt    = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        (_channelName, _userId) = await FetchUserInfoAsync();

        await SaveTokenToDb();
        _logger.LogInformation("Kick authenticated as: {Channel} (userId={UserId})", _channelName, _userId);
    }

    /// <summary>Returns a valid access token, auto-refreshing if expired.</summary>
    public async Task<string> GetAccessTokenAsync()
    {
        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("Not authenticated with Kick");

        if (DateTimeOffset.UtcNow >= _expiresAt.AddMinutes(-5))
        {
            try
            {
                await RefreshTokenAsync();
            }
            catch (Exception ex) when (ex is TaskCanceledException || ex is HttpRequestException http && (http.StatusCode == null || (int)http.StatusCode >= 500 || (int)http.StatusCode == 429))
            {
                _logger.LogWarning(ex, "Kick token refresh temporarily unavailable; keeping saved session");
                throw new InvalidOperationException("Kick bağlantısı geçici olarak yanıt vermiyor. Oturumunuz korundu.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kick token refresh failed — user must re-authenticate");
                _accessToken  = null;
                _refreshToken = null;
                await _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("auth:expired", new
                {
                    platform = "kick",
                    message  = "Kick oturumu sona erdi. Ayarlar’dan Kick hesabını yeniden bağlayın."
                });
                throw new InvalidOperationException("Kick token refresh failed — re-authentication required", ex);
            }
        }

        return _accessToken;
    }

    /// <summary>Removes stored Kick token and revokes it with Kick's OAuth server.</summary>
    public async Task DisconnectAsync()
    {
        var tokenToRevoke = _accessToken;

        _accessToken  = null;
        _refreshToken = null;
        _channelName  = null;
        _userId       = 0;
        _expiresAt    = default;

        // Revoke the token with Kick so the session is invalidated server-side
        if (!string.IsNullOrWhiteSpace(tokenToRevoke))
        {
            try
            {
                var http = _httpFactory.CreateClient();
                var req = new HttpRequestMessage(HttpMethod.Post, "https://id.kick.com/oauth/revoke");
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"]           = tokenToRevoke,
                    ["client_id"]       = _settings.ClientId,
                    ["token_type_hint"] = "access_token",
                });
                var res = await http.SendAsync(req);
                _logger.LogInformation("Kick token revoked — status {Status}", (int)res.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kick token revocation failed (token cleared locally regardless)");
            }
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            var cfg = await db.PlatformConfigs
                .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "kick_auth");
            if (cfg != null) { db.PlatformConfigs.Remove(cfg); await db.SaveChangesAsync(); }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove Kick token from DB"); }

        // Tell the Desktop layer to clear Kick cookies from the main WebView2
        await AppEvents.NotifyPlatformAuthForgotten("kick");

        _logger.LogInformation("Kick disconnected");
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    private async Task RefreshTokenAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
        if (_accessToken != null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-5)) return;

        if (_refreshToken == null) throw new InvalidOperationException("No Kick refresh token");

        var response = await SendTokenRequest(new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = _refreshToken,
        });
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        _accessToken = json.RootElement.GetProperty("access_token").GetString();
        var expiresIn = json.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        _expiresAt   = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        if (json.RootElement.TryGetProperty("refresh_token", out var rt)) _refreshToken = rt.GetString();

        await SaveTokenToDb();
        _logger.LogInformation("Kick token refreshed");
        }
        finally { _refreshLock.Release(); }
    }


    private async Task<HttpResponseMessage> SendTokenRequest(Dictionary<string, string> fields)
    {
        var form = new Dictionary<string, string>(fields)
        {
            ["client_id"] = _settings.ClientId,
        };
        if (!string.IsNullOrWhiteSpace(_settings.ClientSecret))
            form["client_secret"] = _settings.ClientSecret;

        var request = new HttpRequestMessage(HttpMethod.Post, "https://id.kick.com/oauth/token")
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var response = await _httpFactory.CreateClient().SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Kick yetkilendirmesi başarısız (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
        }
        return response;
    }

    private async Task<(string? name, int userId)> FetchUserInfoAsync()
    {
        try
        {
            var client = _httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.kick.com/public/v1/users");
            req.Headers.Add("Authorization", $"Bearer {_accessToken}");
            req.Headers.Add("Accept", "application/json");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var res = await client.SendAsync(req, timeout.Token);
            res.EnsureSuccessStatusCode();
            var json = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());

            // Response: { "data": [{ "user_id": 123, "name": "username", ... }] }
            var root = json.RootElement;
            if (root.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                root = data[0];

            var userId = root.TryGetProperty("user_id", out var uid) ? uid.GetInt32() : 0;

            string? name = null;
            if (root.TryGetProperty("name", out var n))     name = n.GetString();
            else if (root.TryGetProperty("username", out var u)) name = u.GetString();
            else if (root.TryGetProperty("slug", out var s))    name = s.GetString();

            if (name == null)
                _logger.LogWarning("Kick user response: {Json}", json.RootElement.GetRawText()[..Math.Min(500, json.RootElement.GetRawText().Length)]);

            return (name, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Kick user info");
            return (null, 0);
        }
    }

    /// <summary>Sends a message to Kick chat (supports bot account replying to streamer channel).</summary>
    public async Task<bool> SendChatMessageAsync(string message, Func<bool>? isCurrent = null)
    {
        if (_accessToken == null || _userId == 0) return false;

        try
        {
            var token = await GetAccessTokenAsync();
            if (isCurrent?.Invoke() == false) return false;

            int targetBroadcaster = TargetBroadcasterUserId > 0 ? TargetBroadcasterUserId : _userId;

            var client = _httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.kick.com/public/v1/chat");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Content = JsonContent.Create(new
            {
                content              = message,
                type                 = "user",
                broadcaster_user_id  = targetBroadcaster,
            });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var res = await client.SendAsync(req, timeout.Token);
            var body = await res.Content.ReadAsStringAsync();

            if (res.IsSuccessStatusCode)
            {
                _logger.LogInformation("Kick chat sent OK: {Message}", message);
                return true;
            }

            _logger.LogWarning("Kick chat send failed {Status}: {Body}", (int)res.StatusCode, body[..Math.Min(300, body.Length)]);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kick chat send error");
            return false;
        }
    }

    private async Task SaveTokenToDb()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            var tokenJson = JsonSerializer.Serialize(new
            {
                accessToken  = TokenProtection.Protect(_accessToken),
                refreshToken = TokenProtection.Protect(_refreshToken),
                channelName  = _channelName,
                userId       = _userId,
                expiresAt    = _expiresAt.ToUnixTimeSeconds(),
            });

            var existing = await db.PlatformConfigs
                .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "kick_auth");

            if (existing != null)
                existing.ConfigJson = tokenJson;
            else
                db.PlatformConfigs.Add(new PlatformConfig
                {
                    UserId = LocalUser.Id, Platform = "kick_auth", ConfigJson = tokenJson,
                });

            await db.SaveChangesAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to save Kick token to DB"); }
    }

    private void LoadTokenFromDb()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            var cfg = db.PlatformConfigs
                .FirstOrDefault(p => p.UserId == LocalUser.Id && p.Platform == "kick_auth");
            if (cfg == null) return;

            var json = JsonDocument.Parse(cfg.ConfigJson).RootElement;
            _accessToken  = TokenProtection.Unprotect(json.GetProperty("accessToken").GetString());
            _refreshToken = TokenProtection.Unprotect(json.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null);
            _channelName  = json.TryGetProperty("channelName", out var cn) ? cn.GetString() : null;
            _userId       = json.TryGetProperty("userId", out var uid) ? uid.GetInt32() : 0;
            _expiresAt    = DateTimeOffset.FromUnixTimeSeconds(json.GetProperty("expiresAt").GetInt64());

            _logger.LogInformation("Loaded Kick token from DB (channel: {Channel}, userId: {UserId})", _channelName, _userId);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not load Kick token from DB"); }
    }

    // ── PKCE helpers ──────────────────────────────────────────────────────────

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
