using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Controllers;
using MusicBot.Services;
using MusicBot.Services.Commands;
using MusicBot.Services.Downloader;
using Xunit;
namespace MusicBot.Tests;
public class OnlinePlaybackRegressionTests
{
    [Fact] public void MetadataReadsMusicCategoryWithoutRequiringStructuredArtistOrTrack()
    {
        using var raw=JsonDocument.Parse("""{"videoDetails":{"videoId":"9bZkp7q19f0","title":"Gangnam Style","author":"officialpsy","lengthSeconds":"253"},"microformat":{"playerMicroformatRenderer":{"category":"Music"}}}""");
        var value=YouTubeVideoMetadata.Parse(raw.RootElement,"9bZkp7q19f0"); Assert.NotNull(value);
        using var metadata=JsonDocument.Parse(JsonSerializer.Serialize(value));
        YouTubeMusicPolicy.Validate(metadata.RootElement);
        Assert.Equal("Gangnam Style",metadata.RootElement.GetProperty("title").GetString());
        Assert.Null(YouTubeVideoMetadata.Parse(raw.RootElement,"aaaaaaaaaaa"));
    }
    [Fact] public void MusicCategoryDoesNotAuthorizeUpcomingLiveBroadcasts()
    {
        using var raw=JsonDocument.Parse("""{"videoDetails":{"videoId":"9bZkp7q19f0"},"microformat":{"playerMicroformatRenderer":{"category":"Music","liveBroadcastDetails":{"startTimestamp":"tomorrow"}}}}""");
        using var metadata=JsonDocument.Parse(JsonSerializer.Serialize(YouTubeVideoMetadata.Parse(raw.RootElement,"9bZkp7q19f0")));
        Assert.Throws<InvalidOperationException>(()=>YouTubeMusicPolicy.Validate(metadata.RootElement));
    }
    [Fact] public void CooldownChecksDoNotPenalizeFailureAndSuccessStartsTheClock()
    {
        var path=Path.Combine(Path.GetTempPath(),"musick-cooldown-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var config=new CommandConfigService(NullLogger<CommandConfigService>.Instance,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"DataDirectory",path}}).Build());
            Assert.True(config.CheckCooldown("play","viewer",30,out _));
            Assert.True(config.CheckCooldown("play","viewer",30,out _));
            config.RecordSuccessfulCommand("play","viewer");
            Assert.False(config.CheckCooldown("play","viewer",30,out var remaining)); Assert.InRange(remaining,1,30);
            Assert.True(config.CheckCooldown("skip","viewer",30,out _));
            Assert.True(config.CheckCooldown("play","other",30,out _));
        }
        finally { if(Directory.Exists(path)) Directory.Delete(path,true); }
    }
    [Fact] public void LatePoolCandidateCannotOvertakeRequestsOrBackgroundPlaylistOrActivePlayback()
    {
        var song=new Song{SpotifyUri="pool"}; var q=new QueueService(0,0);
        q.AddSong(new(){SpotifyUri="viewer"},"viewer","kick");
        Assert.Null(q.TryStartFallback(song)); Assert.Single(q.GetUpcoming()); Assert.Null(q.GetCurrentItem());
        q.ClearUserQueue(); q.SetBackgroundPlaylist([new(){SpotifyUri="background"}]);
        Assert.Null(q.TryStartFallback(song)); Assert.Equal("background",q.Advance()!.Song.SpotifyUri);
        Assert.Null(q.TryStartFallback(song));
        q.ClearBackgroundPlaylist(); q.DiscardCurrent(q.GetCurrentItem()!.Id);
        var item=q.TryStartFallback(song); Assert.NotNull(item); Assert.Equal("pool",item.Song.SpotifyUri); Assert.Empty(q.GetUpcoming());
    }
    [Fact] public void OldSpeculativeCandidatesAreRemovedAndIdleAdvanceCannotReplaceANewerCurrentTrack()
    {
        var q=new QueueService(0,0);
        q.Seed(null,[new(){Song=new(){SpotifyUri="pool"},Platform="autoqueue",IsPlaylistItem=true},new(){Song=new(){SpotifyUri="viewer"},Platform="kick",PreparedForSpotify=true}]);
        Assert.Single(q.GetUpcoming());Assert.False(q.GetUpcoming()[0].PreparedForSpotify);
        Assert.Equal("viewer",q.AdvanceIfIdle()!.Song.SpotifyUri);
        q.AddSong(new(){SpotifyUri="next"},"viewer2","kick");
        Assert.Null(q.AdvanceIfIdle());Assert.Equal("viewer",q.GetCurrentItem()!.Song.SpotifyUri);Assert.Single(q.GetUpcoming());
    }
    [Fact] public void ErrorTemplatesCanBeEditedAndSilenced()
    {
        var definition=CommandsConfiguration.CreateDefault().Commands["play"];
        Assert.True(definition.Templates.ContainsKey("service_error"));
        Assert.Equal("@viewer, unavailable",CommandTemplateEngine.Render(definition.GetTemplate("service_error"),new(){User="viewer",Error="unavailable"}));
        definition.Templates["service_error"]="";
        Assert.Equal("",CommandTemplateEngine.Render(definition.GetTemplate("service_error"),new(){Error="unavailable"}));
    }
    [Fact] public async Task CachedYouTubeSongAlwaysStreamsAndUrlSearchUsesExactSong()
    {
        var path=Path.Combine(Path.GetTempPath(),"musick-online-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var builder=WebHost.CreateBuilder([]);builder.Configuration["DataDirectory"]=path;builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            await using var app=WebHost.Configure(builder);
            var downloader=app.Services.GetRequiredService<YtDlpDownloaderService>();
            var verified=(System.Collections.Concurrent.ConcurrentDictionary<string,Song>)typeof(YtDlpDownloaderService).GetField("_musicVideos",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(downloader)!;
            verified["9bZkp7q19f0"]=new(){SpotifyUri="youtube:9bZkp7q19f0",Title="Gangnam Style",Artist="PSY",Platform="youtube"};
            var file=Path.Combine(path,"old-cache.mp3");await File.WriteAllBytesAsync(file,new byte[100001]);
            var q=new QueueService();var item=q.PlayNow(new(){SpotifyUri="youtube:9bZkp7q19f0",LocalFilePath=file},"viewer","kick");
            var player=DispatchProxy.Create<ILocalPlayerService,YouTubeFallbackTests.PlayerProbe>();
            var user=new UserServices(LocalUser.Id,q,DispatchProxy.Create<ISpotifyService,YouTubeFallbackTests.SpotifyProbe>(),player);
            await app.Services.GetRequiredService<PlaybackSyncService>().StartCurrentTrackAsync(user);
            Assert.Equal("youtube:9bZkp7q19f0",((YouTubeFallbackTests.PlayerProbe)(object)player).Played);Assert.Null(item.Song.LocalFilePath);Assert.True(File.Exists(file));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>downloader.GetOrStartDownloadAsync(item.Song));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>downloader.GetOrStartDownloadAsync(new(){SpotifyUri="spotify:track:test"}));
            var controller=ActivatorUtilities.CreateInstance<CommandsController>(app.Services);
            controller.ControllerContext=new(){HttpContext=new DefaultHttpContext{RequestServices=app.Services}};
            var result=Assert.IsType<OkObjectResult>(await controller.Search("https://music.youtube.com/watch?v=9bZkp7q19f0&si=test",10,"youtube"));
            var songs=Assert.IsType<List<Song>>(result.Value);Assert.Single(songs);Assert.Equal(item.Song.SpotifyUri,songs[0].SpotifyUri);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path,true); }
    }
}
