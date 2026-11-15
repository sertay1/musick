using Microsoft.AspNetCore.SignalR;
using MusicBot.Hubs;
namespace MusicBot.Services.Platforms;
public class YouTubeAuthService
{
    private readonly YouTubeOAuthService? _oauth;
    private readonly IHubContext<OverlayHub> _hub;
    public YouTubeAuthService(IServiceScopeFactory scopes, ILogger<YouTubeAuthService> logger, IHubContext<OverlayHub> hub, YouTubeOAuthService? oauth = null) { _oauth = oauth; _hub = hub; }
    public bool IsEnabled => IsConnected;
    public bool IsConnected => _oauth?.IsConnected == true;
    public string? CookiesFilePath => null;
    public string? AccountLabel => _oauth?.Account;
    public DateTimeOffset SavedAt => default;
    public bool LoginCancelled => false;
    public Task EnsureLoadedAsync() => _oauth?.EnsureLoadedAsync() ?? Task.CompletedTask;
    public Task InitAsync() => EnsureLoadedAsync();
    public void ResetCancelledFlag() { }
    public Task EnableAsync() => Task.CompletedTask;
    public Task DisableAsync() => Task.CompletedTask;
    public async Task RateMusicAsync(string videoId, bool like)
    {
        await EnsureLoadedAsync();
        if (!IsConnected) throw new InvalidOperationException("YouTube beğenilerini eşitlemek için size ait OAuth istemcisiyle hesabınızı bağlayın. Yerel beğeni korundu.");
        await _oauth!.RateAsync(videoId, like);
    }
    public async Task DisconnectAsync()
    {
        if (_oauth != null) await _oauth.DisconnectAsync();
        await _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("auth:updated", new { platform = "youtube", authenticated = false });
    }
}
