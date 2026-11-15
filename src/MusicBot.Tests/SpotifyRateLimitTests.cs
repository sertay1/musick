using System.Net.Http.Headers;
using MusicBot.Services.Spotify;
using Xunit;
namespace MusicBot.Tests;
public class SpotifyRateLimitTests
{
    [Fact]
    public void DeltaCooldownBlocksUntilExpiry_AndThenAllowsRecovery()
    {
        var clock = new Clock(); var gate = new SpotifyRateLimit(clock);
        Assert.Equal(12, gate.Block(new RetryConditionHeaderValue(TimeSpan.FromSeconds(12))).RetryAfterSeconds);
        Assert.Throws<SpotifyRateLimitException>(() => gate.ThrowIfLimited());
        clock.Now = clock.Now.AddSeconds(11);
        Assert.Equal(1, Assert.Throws<SpotifyRateLimitException>(() => gate.ThrowIfLimited()).RetryAfterSeconds);
        clock.Now = clock.Now.AddSeconds(1); gate.ThrowIfLimited();
    }
    [Fact]
    public void HeaderDateAndMissingHeaderAreSupported_AndCooldownNeverShortens()
    {
        var clock = new Clock(); var gate = new SpotifyRateLimit(clock);
        Assert.Equal(45, gate.Block(new RetryConditionHeaderValue(clock.Now.AddSeconds(45))).RetryAfterSeconds);
        clock.Now = clock.Now.AddSeconds(5);
        Assert.Equal(40, gate.Block(null).RetryAfterSeconds);
        clock.Now = clock.Now.AddSeconds(40); gate.ThrowIfLimited();
        Assert.Equal(30, gate.Block(null).RetryAfterSeconds);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
