using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Services.Platforms;
using MusicBot.Tests.TestHelpers;
using Xunit;
namespace MusicBot.Tests;
public class YouTubeMusicLikeTests
{
    [Fact]
    public async Task CookieCaptureCannotAuthenticate_AndMissingOwnedOAuthFailsClosed()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var auth = new YouTubeAuthService(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<YouTubeAuthService>.Instance, FakeHub.Instance);
        await auth.EnsureLoadedAsync();
        Assert.False(auth.IsConnected); Assert.Null(auth.CookiesFilePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.RateMusicAsync("aaaaaaaaaaa", true));
    }
}
