using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MusicBot.Data;
using MusicBot.Services.Platforms;
using MusicBot.Tests.TestHelpers;
using Xunit;
namespace MusicBot.Tests;
public class YouTubeBrowserOAuthTests
{
    [Fact]
    public async Task BrowserOAuth_UsesPkce_Refreshes_Paginates_Rates_AndNeverExportsCookies()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var provider = new ServiceCollection().AddDbContext<MusicBotDbContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
        provider.GetRequiredService<MusicBotDbContext>().Database.EnsureCreated();
        var refreshes = 0; var ratings = 0;
        using var handler = new Handler(async request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "oauth2.googleapis.com")
            {
                Assert.False(request.Headers.Contains("X-Relay-Key"));
                var body = QueryHelpers.ParseQuery("?" + await request.Content!.ReadAsStringAsync());
                Assert.Equal("100000000000-testowned.apps.googleusercontent.com", body["client_id"].ToString());
                Assert.Equal("native-test-secret", body["client_secret"].ToString());
                if (body["grant_type"] == "refresh_token") { refreshes++; return Json(new { access_token = "fresh-access", expires_in = 3600 }); }
                Assert.True(body["code_verifier"].ToString().Length >= 43);
                return Json(new { access_token = "initial-access", refresh_token = "private-refresh", expires_in = 0, scope = "https://www.googleapis.com/auth/youtube.force-ssl" });
            }
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fresh-access", request.Headers.Authorization.Parameter);
            if (uri.AbsolutePath.EndsWith("/channels")) return Json(new { items = new[] { new { snippet = new { title = "Test account" } } } });
            if (uri.AbsolutePath.EndsWith("/playlists"))
            {
                var second = uri.Query.Contains("pageToken=");
                return Json(new { items = Enumerable.Range(second ? 50 : 0, second ? 2 : 50).Select(i => new { id = "PL" + i, snippet = new { title = "List " + i } }), nextPageToken = second ? null : "second" });
            }
            if (uri.AbsolutePath.EndsWith("/playlistItems")) return Json(new { items = new[] { new { snippet = new { title = "Song", videoOwnerChannelTitle = "Artist", resourceId = new { videoId = "aaaaaaaaaaa" }, thumbnails = new { medium = new { url = "https://example.invalid/art.jpg" } } } } } });
            if (uri.AbsolutePath.EndsWith("/videos/rate")) { ratings++; Assert.Equal(HttpMethod.Post, request.Method); return new(HttpStatusCode.NoContent); }
            throw new InvalidOperationException("Unexpected HTTP: " + uri);
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleOAuth:ClientId"] = "100000000000-testowned.apps.googleusercontent.com", ["GoogleOAuth:ClientSecret"] = "native-test-secret", ["Google:ClientId"] = "ignored-legacy.apps.googleusercontent.com", ["Google:UseOwnClient"] = "false" }).Build();
        var oauth = new YouTubeOAuthService(provider.GetRequiredService<IServiceScopeFactory>(), config, new Factory(handler), FakeHub.Instance);
        await oauth.EnsureLoadedAsync(); Assert.True(oauth.IsConfigured);
        var url = new Uri(oauth.Begin()); var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("accounts.google.com", url.Host); Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(43, query["code_challenge"].ToString().Length);
        Assert.DoesNotContain("client_secret", url.Query);
        var state = query["state"].ToString();
        await oauth.CompleteAsync(state, "code", null);
        Assert.True(oauth.IsConnected); Assert.Equal("Test account", oauth.Account); Assert.Equal(1, refreshes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.CompleteAsync(state, "code", null));
        Assert.Equal(52, (await oauth.PlaylistsAsync()).Count);
        Assert.Equal("youtube:aaaaaaaaaaa", Assert.Single(await oauth.PlaylistTracksAsync("PL1")).SpotifyUri);
        await oauth.RateAsync("aaaaaaaaaaa", true); await oauth.RateAsync("aaaaaaaaaaa", false); Assert.Equal(2, ratings);
        var stored = (await provider.GetRequiredService<MusicBotDbContext>().PlatformConfigs.AsNoTracking().SingleAsync()).ConfigJson;
        Assert.DoesNotContain("private-refresh", stored); Assert.DoesNotContain("fresh-access", stored);
        await oauth.DisconnectAsync(); Assert.False(oauth.IsConnected);
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
}
