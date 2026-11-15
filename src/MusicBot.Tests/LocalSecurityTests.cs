using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MusicBot.Services.Security;
using MusicBot.Services.Platforms;
using MusicBot.Tests.TestHelpers;
using Xunit;
namespace MusicBot.Tests;
public class LocalSecurityTests
{
    [Theory]
    [InlineData("http://127.0.0.1:3050", true)]
    [InlineData("http://localhost:3050", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://localhost:9999", false)]
    [InlineData("http://localhost.evil.example:3050", false)]
    [InlineData("null", false)]
    public void LocalOriginsAreExact(string origin, bool expected) => Assert.Equal(expected, LocalRequestSecurity.OriginAllowed(origin, 3050));
    [Fact]
    public async Task DashboardAllowsOwnOverlayFramesAndYouTubeWithoutAllowingArbitraryFramesOrInlineScripts()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("127.0.0.1", 3050);
        context.Request.Path = "/";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        await LocalRequestSecurity.ProtectAsync(context, _ => Task.CompletedTask, false);
        var directives = context.Response.Headers["Content-Security-Policy"].ToString().Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToDictionary(d => d[0], d => d.Skip(1).ToArray());
        Assert.Equal(new[] { "'self'", "https://www.youtube.com", "https://www.youtube-nocookie.com" }, directives["frame-src"]);
        Assert.DoesNotContain("'unsafe-inline'", directives["script-src"]);
        Assert.Equal("SAMEORIGIN", context.Response.Headers["X-Frame-Options"].ToString());
        Assert.Equal(new[] { "'self'" }, directives["frame-ancestors"]);
    }
    [Theory]
    [InlineData("localhost", "http://localhost:3050", true, 204)]
    [InlineData("localhost", "https://evil.example", true, 403)]
    [InlineData("evil.example", "", true, 403)]
    [InlineData("localhost", "", false, 403)]
    public async Task LocalMutationsRejectCsrfAndDnsRebinding(string host, string origin, bool marker, int status)
    {
        var context = new DefaultHttpContext(); context.Request.Host = new HostString(host, 3050); context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Method = "POST"; context.Request.Path = "/api/pause"; if (origin.Length > 0) context.Request.Headers.Origin = origin;
        if (marker) context.Request.Headers[LocalRequestSecurity.Header] = "1";
        await LocalRequestSecurity.ProtectAsync(context, c => { c.Response.StatusCode = 204; return Task.CompletedTask; }, false);
        Assert.Equal(status, context.Response.StatusCode);
    }
    [Fact]
    public void OAuthStateIsUnpredictableProviderBoundAndSingleUse()
    {
        var states = new OAuthStateStore(); var first = states.Begin("spotify"); var second = states.Begin("spotify");
        Assert.NotEqual(first, second); Assert.False(states.Consume("spotify", first)); Assert.True(states.Consume("spotify", second)); Assert.False(states.Consume("spotify", second));
        Assert.False(states.Consume("google", states.Begin("kick"))); Assert.False(states.Consume("kick", null));
    }
    [Fact]
    public void CredentialsAreEncryptedAtRest_AndUnreadableCipherFailsClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), "musick-private-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"Spotify\":{\"ClientSecret\":\"test-secret\"},\"Queue\":{\"Enabled\":true}}");
            CredentialProtection.MigrateFile(path); var contents = File.ReadAllText(path); Assert.DoesNotContain("test-secret", contents);
            using var json = JsonDocument.Parse(contents); Assert.Equal("test-secret", TokenProtection.Unprotect(json.RootElement.GetProperty("Spotify").GetProperty("ClientSecret").GetString()));
            Assert.Equal(string.Empty, TokenProtection.Unprotect(TokenProtection.EncryptedPrefix + "not-valid-data"));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void MissingSharedGoogleClientCannotInitiateAccountAccess()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var oauth = new YouTubeOAuthService(services.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(), new RejectHttp(), FakeHub.Instance);
        Assert.Throws<InvalidOperationException>(() => oauth.Begin()); Assert.False(oauth.IsConnected);
    }
    [Fact]
    public void LegacyPerUserGoogleClientCannotEnableSharedLogin()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Google:ClientId"] = "100000000000-legacy.apps.googleusercontent.com", ["Google:UseOwnClient"] = "true" }).Build();
        var oauth = new YouTubeOAuthService(services.GetRequiredService<IServiceScopeFactory>(), config, new RejectHttp(), FakeHub.Instance);
        Assert.False(oauth.IsConfigured); Assert.Throws<InvalidOperationException>(() => oauth.Begin());
    }
    private sealed class RejectHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network may be used"); }
}
