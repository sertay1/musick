using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicBot.Hubs;
using MusicBot.Services.Platforms;
using MusicBot.Services.Spotify;
using MusicBot.Tests.TestHelpers;
using Xunit;

namespace MusicBot.Tests;

public class CredentialUpdateTests
{
    private sealed class DummyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class DummyScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => null;
        public void Dispose() { }
    }

    [Fact]
    public void KickAuthService_UpdateCredentials_UpdatesSettingsAndAuthUrl()
    {
        var settings = new KickSettings
        {
            ClientId = "",
            ClientSecret = "",
            RedirectUri = "http://localhost:3050/api/auth/kick/callback"
        };
        var options = Options.Create(settings);
        var httpFactory = new DummyHttpClientFactory();
        var scopeFactory = new DummyScopeFactory();
        var logger = NullLogger<KickAuthService>.Instance;

        var authService = new KickAuthService(options, httpFactory, scopeFactory, FakeHub.Instance, logger);

        // Before update: GetAuthUrl should throw because ClientId is empty
        Assert.Throws<InvalidOperationException>(() => authService.GetAuthUrl());

        // Update credentials live
        authService.UpdateCredentials("test-kick-client-id", "test-kick-secret");

        Assert.Equal("test-kick-client-id", authService.Settings.ClientId);
        Assert.Equal("test-kick-secret", authService.Settings.ClientSecret);

        // Now GetAuthUrl succeeds and includes updated client_id
        var url = authService.GetAuthUrl();
        Assert.Contains("client_id=test-kick-client-id", url);
    }

    [Fact]
    public void SpotifyService_UpdateCredentials_UpdatesSettingsAndAuthUrl()
    {
        var settings = new SpotifySettings
        {
            ClientId = "initial-id",
            ClientSecret = "initial-secret",
            RedirectUri = "http://localhost:3050/api/auth/spotify/callback"
        };
        var options = Options.Create(settings);
        var httpFactory = new DummyHttpClientFactory();
        var scopeFactory = new DummyScopeFactory();
        var logger = NullLogger<SpotifyService>.Instance;

        var spotifyService = new SpotifyService(options, httpFactory, logger, scopeFactory, FakeHub.Instance, Guid.NewGuid());

        var initialUrl = spotifyService.GetAuthUrl();
        Assert.Contains("client_id=initial-id", initialUrl);

        // Update live
        spotifyService.UpdateCredentials("new-spotify-client-id", "new-spotify-secret");

        Assert.Equal("new-spotify-client-id", spotifyService.Settings.ClientId);
        Assert.Equal("new-spotify-secret", spotifyService.Settings.ClientSecret);

        var updatedUrl = spotifyService.GetAuthUrl();
        Assert.Contains("client_id=new-spotify-client-id", updatedUrl);
    }
}
