using MusicBot.Tests.TestHelpers;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicBot.Services.Spotify;
using Xunit;

namespace MusicBot.Tests;

public class SpotifyNativeTransportTests
{
    private const string Target = "spotify:track:1234567890123456789012";
    private const string Old = "spotify:track:abcdefghijklmnopqrstuv";
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public string? Actual = Old;
        public string? Linked;
        public bool AlternateEdition, ResolveEdition, AvailableDesktop;
        public int Searches;
        public string? LastEnqueued;
        public const string Fresh = "spotify:track:yyyyyyyyyyyyyyyyyyyyyy";
        public bool Playing = true, ConfirmNext = true, ConfirmResume = true, Current = true, CancelOnQueueRead;
        public int Denial;
        public readonly List<string> Queue = new();
        public readonly List<string> Writes = new();
        public readonly List<string> Devices = new();
        public int StateReads;
        public HttpClient CreateClient(string name) => new(this, false);
        private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data)) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage response;
            if (request.Method == HttpMethod.Get && path == "/v1/search")
            {
                Searches++;
                response=Json(new{tracks=new{items=new[]{new{uri=Fresh,name="Requested",duration_ms=180000,artists=new[]{new{name="Artist"}}}}}});
            }
            else if (request.Method == HttpMethod.Get && path.StartsWith("/v1/tracks/"))
            {
                response = Json(new { name="Requested", duration_ms=180000, artists=new[]{new{id="artist",name=ResolveEdition?"Artist":""}}, external_ids=new{isrc="recording"} });
            }
            else if (request.Method == HttpMethod.Get && path == "/v1/me/player/devices") response = Json(new { devices = AvailableDesktop ? new[]{new { id="desktop",type="Computer",is_active=false,is_restricted=false }} : Array.Empty<object>() });
            else if (request.Method == HttpMethod.Get && path == "/v1/me/player")
            {
                StateReads++;
                response = Actual == null ? new(HttpStatusCode.NoContent) : Json(new { is_playing = Playing, progress_ms = 41000,
                    device = new { id = "active-device", is_active = true }, item = new { uri = Actual, name = AlternateEdition && Actual != Old ? "Requested" : "", duration_ms=180000, linked_from = new { uri = Linked } } });
            }
            else if (request.Method == HttpMethod.Get && path == "/v1/me/player/queue")
            {
                if (CancelOnQueueRead) Current = false;
                response = Json(new { currently_playing = new { uri = Actual }, queue = Queue.Select(uri => new { uri }) });
            }
            else if (request.Method == HttpMethod.Put && path == "/v1/me/player")
            {
                var body=JsonDocument.Parse(request.Content!.ReadAsStringAsync(ct).Result);
                Assert.False(body.RootElement.GetProperty("play").GetBoolean());
                Assert.False(body.RootElement.TryGetProperty("uris",out _));
                Writes.Add(path);response=new(HttpStatusCode.NoContent);
            }
            else
            {
                Assert.NotEqual(HttpMethod.Get, request.Method); // No album/device lookups.
                Assert.Null(request.Content); // No context/offset/uris reset payload.
                Writes.Add(path);
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                Devices.Add(query["device_id"] ?? "");
                if (Denial != 0) response = new((HttpStatusCode)Denial);
                else
                {
                    if (path == "/v1/me/player/queue") { LastEnqueued=query["uri"]; Queue.Add(LastEnqueued!); }
                    else if (path == "/v1/me/player/next") { if (ConfirmNext) { Actual = AlternateEdition ? "spotify:track:zzzzzzzzzzzzzzzzzzzzzz" : Queue[0]; Queue.RemoveAt(0); } }
                    else if (path == "/v1/me/player/play") { if (ConfirmResume) Playing = true; }
                    else throw new Exception("Unexpected mutation: " + path);
                    response = new(HttpStatusCode.NoContent);
                }
            }
            return Task.FromResult(response);
        }
    }
    private static SpotifyService Create(Transport transport, ServiceProvider provider)
    {
        var service = new SpotifyService(Options.Create(new SpotifySettings()), transport,
            NullLogger<SpotifyService>.Instance, provider.GetRequiredService<IServiceScopeFactory>(), FakeHub.Instance, Guid.NewGuid());
        typeof(SpotifyService).GetField("_accessToken", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, "test");
        typeof(SpotifyService).GetField("_expiresAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, DateTimeOffset.UtcNow.AddHours(1));
        typeof(SpotifyService).GetField("_lastActiveDeviceId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, "obsolete-device");
        return service;
    }
    [Fact]
    public async Task SelectionUsesNativeQueueAndSingleNextOnObservedDevice()
    {
        using var t = new Transport(); using var p = new ServiceCollection().BuildServiceProvider(); var s = Create(t,p);
        await s.PlayAsync(Target); await s.PlayAsync(Target);
        Assert.Equal(new[]{"/v1/me/player/queue", "/v1/me/player/next"}, t.Writes);
        Assert.All(t.Devices, d => Assert.Equal("active-device", d));
    }
    [Fact]
    public async Task FailedConfirmationNeverReplaysTransfersOrDuplicatesOnRetry()
    {
        using var t = new Transport { ConfirmNext = false }; using var p = new ServiceCollection().BuildServiceProvider(); var s = Create(t,p);
        await Assert.ThrowsAsync<SpotifyPlayerCommandException>(() => s.PlayAsync(Target));
        Assert.Equal(new[]{"/v1/me/player/queue", "/v1/me/player/next"}, t.Writes);
        Assert.Equal(7,t.StateReads);
        await Assert.ThrowsAsync<SpotifyPlayerCommandException>(() => s.PlayAsync(Target));
        Assert.Equal(2,t.Writes.Count);
        // A late provider confirmation is adopted on the next request, not restarted.
        t.Actual = Target; await Task.Delay(2100); await s.PlayAsync(Target);
        Assert.Equal(2, t.Writes.Count);
    }
    [Fact]
    public async Task EmptyTransportDoesNotResetSpotify()
    {
        using var t = new Transport { Actual = null }; using var p = new ServiceCollection().BuildServiceProvider();
        var error = await Assert.ThrowsAsync<SpotifyPlayerCommandException>(() => Create(t,p).PlayAsync(Target));
        Assert.Equal(409, error.StatusCode); Assert.Empty(t.Writes);
    }
    [Fact]
    public async Task EmptyAvailableDesktopActivatesWithoutReplayingOldContext()
    {
        using var t=new Transport {Actual=null,AvailableDesktop=true};using var p=new ServiceCollection().BuildServiceProvider();
        await Create(t,p).PlayAsync(Target);
        Assert.Equal(Target,t.Actual);Assert.True(t.Playing);
        Assert.Equal(new[]{"/v1/me/player","/v1/me/player/queue","/v1/me/player/next"},t.Writes);
        Assert.All(t.Devices,d=>Assert.Equal("desktop",d));
    }
    [Fact]
    public async Task ExistingQueueTargetIsNotAddedAgain()
    {
        using var t = new Transport(); t.Queue.Add(Target); using var p = new ServiceCollection().BuildServiceProvider();
        await Create(t,p).PlayAsync(Target); Assert.Equal(new[]{"/v1/me/player/next"}, t.Writes);
    }
    [Fact]
    public async Task RelinkedActualTrackIsNotRestarted()
    {
        using var t = new Transport { Linked = Target }; using var p = new ServiceCollection().BuildServiceProvider();
        await Create(t,p).PlayAsync(Target); Assert.Empty(t.Writes);
    }
    [Fact]
    public async Task PausedOtherTrackResumesOnlyAfterTargetIsObserved()
    {
        using var t = new Transport { Playing = false }; using var p = new ServiceCollection().BuildServiceProvider();
        await Create(t,p).PlayAsync(Target);
        Assert.Equal(new[]{"/v1/me/player/queue", "/v1/me/player/next", "/v1/me/player/play"}, t.Writes);
    }
    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(429)]
    public async Task QueueDenialDoesNotIssueNextOrReplay(int status)
    {
        using var t = new Transport { Denial = status }; using var p = new ServiceCollection().BuildServiceProvider();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Create(t,p).PlayAsync(Target));
        Assert.Equal(new[]{"/v1/me/player/queue"}, t.Writes);
    }
    [Fact]
    public async Task SupersededQueueReadCannotIssuePlaybackCommands()
    {
        using var t = new Transport { CancelOnQueueRead = true }; using var p = new ServiceCollection().BuildServiceProvider();
        Assert.False(await Create(t,p).PlayIfCurrentAsync(Target, () => t.Current)); Assert.Empty(t.Writes);
    }
    [Fact]
    public async Task VerifiedAlternateEditionIsAlsoVisibleToBackgroundSync()
    {
        using var t = new Transport { AlternateEdition = true };
        using var p = new ServiceCollection().BuildServiceProvider(); var service = Create(t,p);
        await service.PlayAsync(Target);
        var state = await service.GetPlaybackStateAsync();
        Assert.True(state!.MatchesTrack(Target));
        Assert.NotEqual(Target, state.TrackUri);
        Assert.True(state.IsPlaying);
        Assert.Equal(new[]{"/v1/me/player/queue", "/v1/me/player/next"},t.Writes);
    }
    [Fact]
    public async Task OldPlaylistIdResolvesBeforeEnqueueAndRetainsRequestedIdentity()
    {
        using var t = new Transport { ResolveEdition = true };
        using var p = new ServiceCollection().BuildServiceProvider(); var service = Create(t,p);
        await service.PlayAsync(Target);
        Assert.Equal(Transport.Fresh,t.LastEnqueued);
        Assert.True((await service.GetPlaybackStateAsync())!.MatchesTrack(Target));
        await service.PlayAsync(Target);
        Assert.Equal(1,t.Searches);
        Assert.Equal(2,t.Writes.Count);
    }
    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(429)]
    public async Task RejectedPreparationReleasesReservationWithoutSkipping(int status)
    {
        using var t=new Transport {Denial=status}; using var p=new ServiceCollection().BuildServiceProvider();
        var released=false;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>Create(t,p).PrepareNextTrackAsync(Target,()=>true,()=>released=true));
        Assert.True(released); Assert.Equal(new[]{"/v1/me/player/queue"},t.Writes);
    }
    [Fact]
    public async Task SupersededPreparationReleasesReservationWithoutWriting()
    {
        using var t=new Transport {CancelOnQueueRead=true}; using var p=new ServiceCollection().BuildServiceProvider();
        var released=false;
        Assert.False(await Create(t,p).PrepareNextTrackAsync(Target,()=>t.Current,()=>released=true));
        Assert.True(released); Assert.Empty(t.Writes);
    }
}
