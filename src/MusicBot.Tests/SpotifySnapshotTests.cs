using MusicBot.Services.Spotify;
using Xunit;
namespace MusicBot.Tests;
public class SpotifySnapshotTests
{
    [Fact]
    public async Task ConcurrentAndRepeatedReadsShareOneFetch_ThenExpire()
    {
        var clock = new Clock(); var cache = new SpotifySnapshot<string?>(clock); var calls = 0;
        async Task<string?> Load() { Interlocked.Increment(ref calls); await Task.Yield(); return null; }
        await Task.WhenAll(Enumerable.Range(0,100).Select(_ => cache.GetAsync(Load, TimeSpan.FromSeconds(2))));
        Assert.Equal(1,calls);
        clock.Now = clock.Now.AddSeconds(2); await cache.GetAsync(Load,TimeSpan.FromSeconds(2)); Assert.Equal(2,calls);
        cache.Invalidate(); await cache.GetAsync(Load,TimeSpan.FromSeconds(2)); Assert.Equal(3,calls);
    }
    [Fact]
    public async Task InvalidationDuringReadDoesNotCacheOldPlayback()
    {
        var cache = new SpotifySnapshot<string>(); var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = cache.GetAsync(() => pending.Task,TimeSpan.FromSeconds(2));
        cache.Invalidate(); pending.SetResult("old"); await read;
        Assert.Equal("new", await cache.GetAsync(() => Task.FromResult("new"),TimeSpan.FromSeconds(2)));
    }
    [Fact]
    public async Task FailedReadIsNotCached()
    {
        var cache = new SpotifySnapshot<int>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(() => Task.FromException<int>(new InvalidOperationException()),TimeSpan.FromSeconds(2)));
        Assert.Equal(7,await cache.GetAsync(() => Task.FromResult(7),TimeSpan.FromSeconds(2)));
    }
    [Fact]
    public void CooldownSurvivesReopening_WithoutTokensOrAccountData()
    {
        var path = Path.Combine(Path.GetTempPath(),"musick-cooldown-"+Guid.NewGuid().ToString("N")+".txt");
        try
        {
            var clock = new Clock(); new SpotifyRateLimit(clock,path).Block(new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7232)));
            clock.Now = clock.Now.AddSeconds(32);
            Assert.Equal(7200,Assert.Throws<SpotifyRateLimitException>(() => new SpotifyRateLimit(clock,path).ThrowIfLimited()).RetryAfterSeconds);
            clock.Now = clock.Now.AddHours(2); new SpotifyRateLimit(clock,path).ThrowIfLimited();
        }
        finally { File.Delete(path); }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026,11,13,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
