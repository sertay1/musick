using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicBot.Core.Interfaces;
using MusicBot.Services;
using MusicBot.Services.Spotify;
using MusicBot.Tests.TestHelpers;
using Xunit;

namespace MusicBot.Tests;

public class SpotifyRegressionTests
{
    [Fact]
    public async Task OlderPlaylistApiRemainsSupportedAndReadsAllPages()
    {
        var modern = 0;
        using var handler = new Handler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/items")) { modern++; return new(HttpStatusCode.Forbidden); }
            Assert.EndsWith("/tracks", req.RequestUri.AbsolutePath);
            var offset = int.Parse(System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query)["offset"]!);
            return JsonResponse(new { items = Enumerable.Range(offset, Math.Min(50, 75 - offset)).Select(i => new { track = new { uri = $"spotify:track:{i:D22}", name = "Song" } }), next = offset == 0 ? "next" : null });
        });
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(75, (await Create(handler, provider).GetPlaylistTracksAsync("test")).Count);
        Assert.Equal(1, modern);
    }
    [Fact]
    public async Task ActualScopeErrorIsDistinguishedFromOtherForbiddenResponses()
    {
        using var missing = new Handler(req => new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"message\":\"Insufficient client scope\"}}") });
        using var forbidden = new Handler(req => new(HttpStatusCode.Forbidden));
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.False(await Create(missing, provider).HasLibraryAccessAsync());
        Assert.Null(await Create(forbidden, provider).HasLibraryAccessAsync());
    }
    [Fact]
    public async Task SupersededSourceCommands_DoNotTouchSpotify()
    {
        using var handler = new Handler(req => throw new Exception("Stale command reached Spotify"));
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = Create(handler, provider);
        Assert.False(await service.PlayIfCurrentAsync("1234567890123456789012", () => false));
        await service.PauseIfCurrentAsync(() => false);
    }
    [Fact]
    public async Task Likes_UseCurrentLibraryApi_AndPreserveTrackUri()
    {
        var methods = new List<HttpMethod>();
        using var handler = new Handler(req =>
        {
            Assert.StartsWith("/v1/me/library", req.RequestUri!.AbsolutePath);
            Assert.Contains("uris=spotify%3Atrack%3A1234567890123456789012", req.RequestUri.Query);
            methods.Add(req.Method);
            return req.Method == HttpMethod.Get ? JsonResponse(new[] { true }) : new(HttpStatusCode.OK);
        });
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = Create(handler, provider);
        await service.SaveTrackAsync("spotify:track:1234567890123456789012");
        Assert.True(await service.IsTrackSavedAsync("1234567890123456789012"));
        await service.RemoveSavedTrackAsync("1234567890123456789012");
        Assert.Equal(new[] { HttpMethod.Put, HttpMethod.Get, HttpMethod.Delete }, methods);
    }

    [Fact]
    public async Task LibraryApiFailure_IsReportedWithoutClaimingPermissionIsMissing()
    {
        using var handler = new Handler(req => new(HttpStatusCode.TooManyRequests));
        using var provider = new ServiceCollection().BuildServiceProvider();
        var error = await Assert.ThrowsAsync<SpotifyRateLimitException>(() => Create(handler, provider).SaveTrackAsync("1234567890123456789012"));
        Assert.Contains("429", error.Message);
        Assert.DoesNotContain("izin", error.Message);
    }
    [Fact]
    public async Task IdlePlayback404_IsEmptyState_AndPauseIsIdempotent()
    {
        using var handler = new Handler(req => new(HttpStatusCode.NotFound));
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = Create(handler, provider);
        Assert.Null(await service.GetPlaybackStateAsync());
        await service.PauseAsync();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdlePlayer_RecoversMissingDeviceBeforeRetryingCommand(bool skip)
    {
        var commands = 0;
        var transfers = 0;
        using var handler = new Handler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/devices")) return JsonResponse(new { devices = new[] { new { id = "desktop", type = "Computer", is_active = false, is_restricted = false } } });
            if (path == "/v1/me/player" && req.Method == HttpMethod.Get) return commands<2?new(HttpStatusCode.NoContent):JsonResponse(new{is_playing=true,item=new{uri="spotify:track:1234567890123456789012"}});
            if (path == "/v1/me/player") { transfers++; return new(HttpStatusCode.NoContent); }
            commands++;
            if (commands == 1) return new(HttpStatusCode.NotFound);
            Assert.Contains("device_id=desktop", req.RequestUri.Query);
            return new(HttpStatusCode.NoContent);
        });
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = Create(handler, provider);
        if (skip) await service.SkipAsync(); else await service.ResumeAsync();
        Assert.Equal(2, commands);
        Assert.Equal(1, transfers); // Transfer only after an actual missing-device 404, never on idle preflight.
    }



    [Fact]
    public async Task PlaybackState_IncludesVolumeFromSpotifyDevice()
    {
        using var handler = new Handler(req => JsonResponse(new { device = new { volume_percent = 37 }, item = new { uri = "spotify:track:1234567890123456789012" } }));
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(37, (await Create(handler, provider).GetPlaybackStateAsync())!.VolumePercent);
    }

    [Fact]
    public async Task PlaylistImport_FailedLaterPageRejectsPartialList()
    {
        using var handler = new Handler(req => req.RequestUri!.Query.Contains("offset=0")
            ? JsonResponse(new { items = new[] { new { item = new { type = "track", uri = "spotify:track:1234567890123456789012", name = "Song" } } }, next = "next" })
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var provider = new ServiceCollection().BuildServiceProvider();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(handler, provider).GetPlaylistTracksAsync("large"));
        Assert.Contains("Yarım liste kaydedilmedi", error.Message);
    }

    [Fact]
    public async Task DefaultPlaylistImport_ReadsEveryPagePastFiveHundred()
    {
        using var handler = new Handler(req =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query);
            var offset = int.Parse(query["offset"]!);
            var items = Enumerable.Range(offset, Math.Min(50, 650 - offset)).Select(i => new { item = new { type = "track", uri = $"spotify:track:{i:D22}", name = $"Song {i}" } });
            return JsonResponse(new { items, next = offset + 50 < 650 ? "next" : null });
        });
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(650, (await Create(handler, provider).GetPlaylistTracksAsync("large")).Count);
    }

    [Fact]
    public async Task UserPlaylists_ReadsAllPages()
    {
        using var handler = new Handler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/v1/me") return JsonResponse(new { id = "me" });
            var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query);
            var offset = int.Parse(query["offset"]!);
            var items = Enumerable.Range(offset, Math.Min(50, 120 - offset)).Select(i => new { id = i.ToString(), name = $"List {i}", owner = new { id = "me" } });
            return JsonResponse(new { items, next = offset + 50 < 120 ? "next" : null });
        });
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(120, (await Create(handler, provider).GetUserPlaylistsAsync()).Count);
    }

    [Fact]
    public async Task FollowedForeignPlaylistsAreExcludedAndOwnerProfileIsCached()
    {
        var profiles=0;
        using var handler=new Handler(req=>{
            if(req.RequestUri!.AbsolutePath=="/v1/me"){profiles++;return JsonResponse(new{id="me"});}
            return new(HttpStatusCode.OK){Content=new StringContent("{\"items\":[null,{\"id\":\"foreign\",\"owner\":{\"id\":\"other\"}},{\"id\":\"unknown\",\"owner\":null},{\"id\":\"mine\",\"owner\":{\"id\":\"me\"},\"items\":{\"total\":90}}],\"next\":null}")};
        });
        using var provider=new ServiceCollection().BuildServiceProvider();var service=Create(handler,provider);
        var own=Assert.Single(await service.GetUserPlaylistsAsync());Assert.Equal("mine",own.Id);Assert.Equal(90,own.TrackCount);
        Assert.Single(await service.GetUserPlaylistsAsync());Assert.Equal(1,profiles);
    }
    [Fact]
    public async Task ForbiddenPrivatePlaylistRetainsProviderRestrictionWithoutImportingAnything()
    {
        var reads=0;using var handler=new Handler(req=>{reads++;return new(HttpStatusCode.Forbidden);});
        using var provider=new ServiceCollection().BuildServiceProvider();
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>Create(handler,provider).GetPlaylistTracksAsync("foreign"));
        Assert.Contains("erişimi reddetti",error.Message);Assert.Equal(3,reads);
    }
    [Theory]
    [InlineData(25,25,true)]
    [InlineData(50,50,true)]
    [InlineData(90,90,true)]
    [InlineData(197,100,false)]
    public async Task PublicPlaylistFallbackRequiresVerifiedCompleteContent(int total,int available,bool complete)
    {
        using var handler=new Handler(req=>
        {
            if(req.RequestUri!.Host=="api.spotify.com")return new(HttpStatusCode.Forbidden);
            Assert.Null(req.Headers.Authorization);
            if(!req.RequestUri.AbsolutePath.Contains("/embed/"))return new(HttpStatusCode.OK){Content=new StringContent($"<meta content='{total}' name='music:song_count'/>")};
            var entity=new{trackList=Enumerable.Range(0,available).Select(i=>new{uri=$"spotify:track:{i:D22}",title=$"Track {i}",subtitle="Artist",duration=180000,imageUrl=$"https://images.test/{i}.jpg"})};
            var json=JsonSerializer.Serialize(new{props=new{pageProps=new{state=new{data=new{entity}}}}});
            return new(HttpStatusCode.OK){Content=new StringContent("<script id=\"__NEXT_DATA__\">"+json+"</script>")};
        });
        using var provider=new ServiceCollection().BuildServiceProvider();var service=Create(handler,provider);
        if(complete){var songs=await service.GetPlaylistTracksAsync("public");Assert.Equal(total,songs.Count);Assert.Equal(total,songs.Select(song=>song.CoverUrl).Distinct().Count());}
        else {var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>service.GetPlaylistTracksAsync("public"));Assert.Contains(total.ToString(),error.Message);Assert.Contains(available.ToString(),error.Message);Assert.Contains("eksik aktarım yapılmadı",error.Message);}
    }
    [Fact]
    public async Task UnknownPublicPlaylistTotalNeverPassesOffAPreviewAsComplete()
    {
        using var handler=new Handler(req=>
        {
            if(req.RequestUri!.Host=="api.spotify.com")return new(HttpStatusCode.Forbidden);
            var html=req.RequestUri.AbsolutePath.Contains("/embed/")?"<script id=\"__NEXT_DATA__\">{\"props\":{\"pageProps\":{\"state\":{\"data\":{\"entity\":{\"trackList\":[{\"uri\":\"spotify:track:aaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\"}]}}}}}}</script>":"<html>Unavailable count</html>";
            return new(HttpStatusCode.OK){Content=new StringContent(html)};
        });
        using var provider=new ServiceCollection().BuildServiceProvider();var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>Create(handler,provider).GetPlaylistTracksAsync("public"));Assert.Contains("toplam şarkı sayısını vermedi",error.Message);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };




    [Fact]
    public async Task Spotify429_BlocksOtherEndpointsAndVolume_WithoutSendingMoreRequests()
    {
        var calls = 0;
        using var handler = new Handler(req => {
            calls++; var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60)); return response;
        });
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = Create(handler, provider);
        var error = await Assert.ThrowsAsync<SpotifyRateLimitException>(() => service.GetPlaybackStateAsync());
        Assert.Equal(60, error.RetryAfterSeconds);
        await Assert.ThrowsAsync<SpotifyRateLimitException>(() => service.GetPlaybackStateAsync());
        await Assert.ThrowsAsync<SpotifyRateLimitException>(() => service.SetVolumeAsync(50));
        await Assert.ThrowsAsync<SpotifyRateLimitException>(() => service.SeekAsync(1000));
        Assert.Equal(1, calls); Assert.True(service.IsAuthenticated);
    }

    [Fact]
    public async Task VolumeBurst_SendsOnlyLatestValue_AndDoesNotResendIdenticalValue()
    {
        var calls = 0; string? query = null;
        using var handler = new Handler(req => { calls++; query = req.RequestUri!.Query; return new(HttpStatusCode.NoContent); });
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = Create(handler, provider);
        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => service.SetVolumeAsync(i * 4)));
        Assert.Equal(1, calls); Assert.Contains("volume_percent=80", query!);
        await service.SetVolumeAsync(80); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SyncChatAndUiPlaybackReadsShareOneHttpCall_AndResumeInvalidatesIt()
    {
        var reads = 0; var playing=false;
        using var handler = new Handler(req => {
            if (req.Method == HttpMethod.Get) { reads++; return playing?JsonResponse(new{is_playing=true,item=new{uri="spotify:track:1234567890123456789012"}}):new(HttpStatusCode.NoContent); }
            playing=true;
            return new(HttpStatusCode.NoContent);
        });
        using var provider = new ServiceCollection().BuildServiceProvider(); var service = Create(handler,provider);
        await Task.WhenAll(Enumerable.Range(0,40).Select(_ => service.GetPlaybackStateAsync()));
        Assert.Equal(1,reads);
        await service.ResumeAsync("known-device"); await service.GetPlaybackStateAsync(); Assert.Equal(2,reads);
    }


    [Fact]
    public async Task SkipDuringCooldownDoesNotAdvanceOrDiscardQueue()
    {
        var path = Path.Combine(Path.GetTempPath(),"musick-blocked-skip-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var builder = WebHost.CreateBuilder([]); builder.Configuration["DataDirectory"] = path;
            builder.Configuration["ConnectionStrings:DefaultConnection"] = "Data Source="+Path.Combine(path,"test.db");
            await using var app = WebHost.Configure(builder);
            using var provider = new ServiceCollection().BuildServiceProvider();
            using var handler = new Handler(_ => new(HttpStatusCode.TooManyRequests)); var spotify = Create(handler,provider);
            await Assert.ThrowsAsync<SpotifyRateLimitException>(() => spotify.GetPlaybackStateAsync());
            var queue = new MusicBot.Core.Services.QueueService();
            var first = queue.PlayNow(new() { SpotifyUri="spotify:track:"+new string('a',22) },"Viewer","kick");
            queue.AddSong(new() { SpotifyUri="spotify:track:"+new string('b',22) },"Second","kick");
            var user = new UserServices(LocalUser.Id,queue,spotify,DispatchProxy.Create<ILocalPlayerService,ManualPlaybackIntentTests.PlayerProbe>());
            var sync = app.Services.GetRequiredService<PlaybackSyncService>();
            await Assert.ThrowsAsync<SpotifyRateLimitException>(() => sync.SkipCurrentAsync(user));
            Assert.Equal(first.Id,queue.GetCurrentItem()!.Id); Assert.Single(queue.GetUpcoming()); Assert.Equal(0,user.SkipPending);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path,true); }
    }

    [Fact]
    public async Task PauseAlreadyPausedDoesNotSendForbiddenDuplicateCommand()
    {
        var pauses=0;
        using var handler=new Handler(req=>{
            if(req.Method==HttpMethod.Get)return JsonResponse(new{is_playing=false,item=new{uri="spotify:track:1234567890123456789012"}});
            pauses++;return new(HttpStatusCode.Forbidden);
        });
        using var provider=new ServiceCollection().BuildServiceProvider();await Create(handler,provider).PauseAsync();Assert.Equal(0,pauses);
    }
    [Fact]
    public async Task Pause403WithConfirmedPausedStateIsSuccessful_WithoutRetryingPause()
    {
        var reads=0;var pauses=0;
        using var handler=new Handler(req=>{
            if(req.Method==HttpMethod.Get)return JsonResponse(new{is_playing=++reads==1,item=new{uri="spotify:track:1234567890123456789012"}});
            pauses++;return new(HttpStatusCode.Forbidden);
        });
        using var provider=new ServiceCollection().BuildServiceProvider();await Create(handler,provider).PauseAsync();Assert.Equal(1,pauses);Assert.Equal(2,reads);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedSeekOrPreviousIsReportedInsteadOfSilentlySucceeding(bool previous)
    {
        using var handler=new Handler(req=>req.Method==HttpMethod.Get?JsonResponse(new{devices=Array.Empty<object>()}):new(HttpStatusCode.Forbidden));
        using var provider=new ServiceCollection().BuildServiceProvider();var service=Create(handler,provider);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>previous?service.PreviousAsync():service.SeekAsync(12000));
    }

    [Fact]
    public async Task ResumeAlreadyPlayingDoesNotSendDuplicatePlay()
    {
        var writes=0;using var handler=new Handler(req=>{
            if(req.Method==HttpMethod.Get)return JsonResponse(new{is_playing=true,item=new{uri="spotify:track:1234567890123456789012"}});
            writes++;return new(HttpStatusCode.Forbidden);
        });
        using var provider=new ServiceCollection().BuildServiceProvider();await Create(handler,provider).ResumeAsync();Assert.Equal(0,writes);
    }
    [Fact]
    public async Task ActualPauseDenialRetains403AndScopeMessage_WithoutRepeatedPause()
    {
        var pauses=0;using var handler=new Handler(req=>{
            if(req.Method==HttpMethod.Get)return JsonResponse(new{is_playing=true,item=new{uri="spotify:track:1234567890123456789012"}});
            pauses++;return new(HttpStatusCode.Forbidden){Content=new StringContent("{\"error\":{\"message\":\"Insufficient client scope\"}}")};
        });
        using var provider=new ServiceCollection().BuildServiceProvider();
        var error=await Assert.ThrowsAsync<SpotifyPlayerCommandException>(()=>Create(handler,provider).PauseAsync());
        Assert.Equal(403,error.StatusCode);Assert.Contains("kontrol izni",error.Message);Assert.Equal(1,pauses);
    }

    [Fact]
    public async Task VolumeScopeDenialIsNotMisreportedAsPremiumOrSuccess()
    {
        using var handler=new Handler(_=>new(HttpStatusCode.Forbidden){Content=new StringContent("{\"error\":{\"message\":\"Insufficient client scope\"}}")});
        using var provider=new ServiceCollection().BuildServiceProvider();
        var error=await Assert.ThrowsAsync<SpotifyPlayerCommandException>(()=>Create(handler,provider).SetVolumeAsync(60));
        Assert.Equal(403,error.StatusCode);Assert.Contains("kontrol izni",error.Message);Assert.DoesNotContain("Premium",error.Message);
    }



    [Fact]
    public async Task ResumeAcceptedWithNoTrackDoesNotClaimSuccessOrReplay()
    {
        var plays=0;var transfers=0;
        using var handler=new Handler(req=>{if(req.Method==HttpMethod.Put&&req.RequestUri!.AbsolutePath.EndsWith("/play"))plays++;else if(req.Method==HttpMethod.Put)transfers++;return new(HttpStatusCode.NoContent);});
        using var provider=new ServiceCollection().BuildServiceProvider();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>Create(handler,provider).ResumeAsync("desktop"));
        Assert.Equal(1,plays);Assert.Equal(0,transfers);
    }



    private static SpotifyService Create(HttpMessageHandler handler, ServiceProvider provider)
    {
        var service = new SpotifyService(Options.Create(new SpotifySettings()), new Factory(handler),
            NullLogger<SpotifyService>.Instance, provider.GetRequiredService<IServiceScopeFactory>(), FakeHub.Instance, Guid.NewGuid());
        typeof(SpotifyService).GetField("_accessToken", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, "test");
        typeof(SpotifyService).GetField("_expiresAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, DateTimeOffset.UtcNow.AddHours(1));
        return service;
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(401)]
    public async Task RejectedPlayback_ThrowsWithoutRepeatedPlayRequests(int status)
    {
        var calls = 0;
        using var handler = new Handler(req =>
        {
            if (req.RequestUri!.AbsolutePath.StartsWith("/v1/tracks/"))
                return JsonResponse(new { album = new { uri = "spotify:album:1234567890123456789012" } });
            if (req.RequestUri.AbsolutePath.EndsWith("/devices")) return JsonResponse(new { devices = Array.Empty<object>() });
            calls++;
            return new((HttpStatusCode)status);
        });
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = Create(handler, provider);
        if (status == 429) await Assert.ThrowsAsync<SpotifyRateLimitException>(() => service.PlayAsync("spotify:track:1234567890123456789012"));
        else { var error = await Assert.ThrowsAsync<SpotifyPlayerCommandException>(() => service.PlayAsync("spotify:track:1234567890123456789012")); Assert.Equal(status, error.StatusCode); }
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PlaylistPagination_DoesNotStopAtUnavailableItemOrFirstFifty()
    {
        var offsets = new List<string>();
        using var handler = new Handler(req =>
        {
            Assert.Contains("/items?", req.RequestUri!.AbsoluteUri);
            offsets.Add(req.RequestUri.Query);
            var second = req.RequestUri.Query.Contains("offset=50");
            var items = Enumerable.Range(second ? 50 : 0, 50).Select(i => new
            {
                item = i == 12 ? null : new { type = "track", uri = $"spotify:track:{i:D22}", name = $"Song {i}", duration_ms = 1000,
                    album = new { images = new[] { new { url = "https://i.scdn.co/image/test" } } } }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { items, next = second ? null : "next" }), Encoding.UTF8, "application/json") };
        });
        using var provider = new ServiceCollection().BuildServiceProvider();
        var songs = await Create(handler, provider).GetPlaylistTracksAsync("list", 500);
        Assert.Equal(99, songs.Count);
        Assert.Equal(2, offsets.Count);
        Assert.Contains("offset=50", offsets[1]);
        Assert.All(songs, song => Assert.NotEmpty(song.CoverUrl));
    }

    [Fact]
    public void PauseAndMissingPlayback_DoNotAdvanceQueue()
    {
        var observer = new SpotifyPlaybackObserver();
        var now = DateTimeOffset.UtcNow;
        Assert.False(observer.ShouldAdvance(new PlaybackState { TrackUri = "a", IsPlaying = true, DurationMs = 10000, ProgressMs = 7000 }, now));
        Assert.False(observer.ShouldAdvance(null, now.AddSeconds(1)));
        Assert.False(observer.ShouldAdvance(new PlaybackState { TrackUri = "a", IsPlaying = false, DurationMs = 10000, ProgressMs = 9800 }, now.AddSeconds(2)));
    }

    [Fact]
    public void CompletedTrack_AdvancesOnlyOnce()
    {
        var observer = new SpotifyPlaybackObserver();
        var now = DateTimeOffset.UtcNow;
        var state = new PlaybackState { TrackUri = "a", IsPlaying = true, DurationMs = 10000, ProgressMs = 9900 };
        Assert.True(observer.ShouldAdvance(state, now));
        Assert.False(observer.ShouldAdvance(state, now.AddSeconds(1)));
        Assert.False(observer.ShouldAdvance(new PlaybackState { TrackUri = "b", IsPlaying = true, DurationMs = 10000, ProgressMs = 0 }, now.AddSeconds(2)));
    }

    [Fact]
    public void SpotifyNaturalTransition_IsRecognizedWithoutEarlySkip()
    {
        var observer = new SpotifyPlaybackObserver();
        var now = DateTimeOffset.UtcNow;
        Assert.False(observer.ShouldAdvance(new PlaybackState { TrackUri = "a", IsPlaying = true, DurationMs = 10000, ProgressMs = 9000 }, now));
        Assert.True(observer.ShouldAdvance(new PlaybackState { TrackUri = "b", IsPlaying = true, DurationMs = 10000, ProgressMs = 100 }, now.AddSeconds(1)));
    }

    [Fact]
    public async Task ManagedQueueRepeatLease_IsAppliedOnceAndRestoresPreviousMode()
    {
        var repeat = "context"; var writes = new List<string>();
        using var handler = new Handler(req => {
            if (req.RequestUri!.AbsolutePath.EndsWith("/repeat")) {
                repeat = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query)["state"]!;
                writes.Add(repeat); return new(HttpStatusCode.NoContent);
            }
            return JsonResponse(new { repeat_state = repeat, is_playing = true, item = new { uri = "spotify:track:1234567890123456789012" } });
        });
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = Create(handler, provider);
        await service.SetManagedQueueAsync(true); await service.SetManagedQueueAsync(true);
        await service.SetManagedQueueAsync(false); await service.SetManagedQueueAsync(false);
        Assert.Equal(new[] { "off", "context" }, writes);
    }

    [Fact]
    public void NativeRepeatWrapAdvancesManagedQueueOnce_WithoutAnotherSongPlayingFirst()
    {
        var observer = new SpotifyPlaybackObserver(); var now = DateTimeOffset.UtcNow;
        Assert.False(observer.ShouldAdvance(new PlaybackState { TrackUri = "a", RepeatState = "track", IsPlaying = true, DurationMs = 10000, ProgressMs = 8000 }, now));
        Assert.True(observer.ShouldAdvance(new PlaybackState { TrackUri = "a", RepeatState = "track", IsPlaying = true, DurationMs = 10000, ProgressMs = 1500 }, now.AddSeconds(4)));
        Assert.False(observer.ShouldAdvance(new PlaybackState { TrackUri = "a", RepeatState = "track", IsPlaying = true, DurationMs = 10000, ProgressMs = 2500 }, now.AddSeconds(5)));
    }

    [Fact]
    public async Task RepeatModeDenialPreservesProviderStatusInsteadOfGeneric500()
    {
        using var handler = new Handler(req => req.RequestUri!.AbsolutePath.EndsWith("/repeat")
            ? new(HttpStatusCode.Forbidden)
            : JsonResponse(new { repeat_state = "track", is_playing = true, item = new { uri = "spotify:track:1234567890123456789012" } }));
        using var provider = new ServiceCollection().BuildServiceProvider();
        var error = await Assert.ThrowsAsync<SpotifyPlayerCommandException>(() => Create(handler, provider).SetManagedQueueAsync(true));
        Assert.Equal(403, error.StatusCode);
    }


}
