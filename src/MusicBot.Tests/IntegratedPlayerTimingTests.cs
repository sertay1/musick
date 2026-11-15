using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MusicBot;
using MusicBot.Controllers;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Services;
using MusicBot.Services.Downloader;
using Xunit;
namespace MusicBot.Tests;
public class IntegratedPlayerTimingTests
{
    [Fact] public void CachedSampleUsesCaptureTimeAndPausedProgressDoesNotAdvance()
    {
        var now=DateTimeOffset.UtcNow;
        var sample=new PlaybackState{SampledAt=now.AddSeconds(-2),ProgressMs=7000,DurationMs=10000,IsPlaying=true};
        Assert.Equal(9000,sample.EstimatedProgressMs(now));Assert.Equal(10000,sample.EstimatedProgressMs(now.AddSeconds(5)));
        sample.IsPlaying=false;Assert.Equal(7000,sample.EstimatedProgressMs(now));Assert.Equal(7000,sample.EstimatedProgressMs(now.AddHours(-1)));
    }
    [Fact] public async Task NotificationSettingPersistsImmediatelyAndSurvivesOldBackupShape()
    {
        await using var profile=new Profile();using var scope=profile.App.Services.CreateScope();var sp=scope.ServiceProvider;
        var settings=ActivatorUtilities.CreateInstance<SettingsController>(sp);settings.ControllerContext=new(){HttpContext=new DefaultHttpContext{RequestServices=sp}};
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request=JsonSerializer.Deserialize<UpdateSettingsRequest>(JsonSerializer.Serialize(((OkObjectResult)settings.GetSettings()).Value,options),options)!;
        Assert.True(JsonSerializer.SerializeToElement(((OkObjectResult)settings.GetSettings()).Value,options).GetProperty("notificationsEnabled").GetBoolean());
        request.NotificationsEnabled=false;await settings.UpdateSettings(request);
        Assert.False(JsonSerializer.SerializeToElement(((OkObjectResult)settings.GetSettings()).Value,options).GetProperty("notificationsEnabled").GetBoolean());
        using(var file=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(profile.Path,"appsettings.user.json"))))Assert.False(file.RootElement.GetProperty("Desktop").GetProperty("NotificationsEnabled").GetBoolean());
        var backup=ActivatorUtilities.CreateInstance<SettingsBackupController>(sp);backup.ControllerContext=settings.ControllerContext;
        var data=Assert.IsType<SettingsBackup>(Assert.IsType<OkObjectResult>(await backup.Export()).Value);Assert.False(data.Queue.NotificationsEnabled);
        request.NotificationsEnabled=null;await settings.UpdateSettings(request);
        Assert.False(JsonSerializer.SerializeToElement(((OkObjectResult)settings.GetSettings()).Value,options).GetProperty("notificationsEnabled").GetBoolean());
    }
    [Fact] public async Task ScheduledPauseAndAdvanceHaveOneOwnerAndDoNotReadAStaleSnapshotAtTheBoundary()
    {
        await using var profile=new Profile();var q=new QueueService();q.PlayNow(new(){SpotifyUri="spotify:track:aaaaaaaaaaaaaaaaaaaaaa"},"host","web");
        q.AddSong(new(){SpotifyUri="youtube:bbbbbbbbbbb"},"viewer","kick");
        var downloader=profile.App.Services.GetRequiredService<YtDlpDownloaderService>();
        ((System.Collections.Concurrent.ConcurrentDictionary<string,Song>)typeof(YtDlpDownloaderService).GetField("_musicVideos",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(downloader)!)["bbbbbbbbbbb"]=new(){SpotifyUri="youtube:bbbbbbbbbbb"};
        var spotify=DispatchProxy.Create<ISpotifyService,BoundarySpotify>();var probe=(BoundarySpotify)(object)spotify;
        var player=DispatchProxy.Create<ILocalPlayerService,YouTubeFallbackTests.PlayerProbe>();var user=new UserServices(LocalUser.Id,q,spotify,player);
        var sync=profile.App.Services.GetRequiredService<PlaybackSyncService>();
        var sample=new PlaybackState{TrackUri=q.GetCurrentItem()!.Song.SpotifyUri,IsPlaying=true,ProgressMs=7000,DurationMs=10000,SampledAt=DateTimeOffset.UtcNow.AddSeconds(-2)};
        await Prepare(sync,user,sample);await probe.PauseStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1,user.SkipPending);await sync.SkipCurrentAsync(user);Assert.StartsWith("spotify:",q.GetCurrentItem()!.Song.SpotifyUri);
        probe.Release.TrySetResult();
        for(var i=0;i<100&&user.SkipPending!=0;i++)await Task.Delay(20);
        Assert.Equal(0,user.SkipPending);Assert.Equal("youtube:bbbbbbbbbbb",q.GetCurrentItem()!.Song.SpotifyUri);Assert.Equal("youtube:bbbbbbbbbbb",((YouTubeFallbackTests.PlayerProbe)(object)player).Played);
    }
    [Fact] public async Task ExternalSeekReschedulesAndCancelsTheOldBoundaryTimer()
    {
        await using var profile=new Profile();var q=new QueueService();q.PlayNow(new(){SpotifyUri="spotify:track:aaaaaaaaaaaaaaaaaaaaaa"},"host","web");q.AddSong(new(){SpotifyUri="youtube:bbbbbbbbbbb"},"viewer","kick");
        var spotify=DispatchProxy.Create<ISpotifyService,BoundarySpotify>();var probe=(BoundarySpotify)(object)spotify;
        var user=new UserServices(LocalUser.Id,q,spotify,DispatchProxy.Create<ILocalPlayerService,YouTubeFallbackTests.PlayerProbe>());
        var sync=profile.App.Services.GetRequiredService<PlaybackSyncService>();
        await Prepare(sync,user,new(){TrackUri=q.GetCurrentItem()!.Song.SpotifyUri,IsPlaying=true,ProgressMs=9000,DurationMs=10000});
        await Task.Delay(50);
        await Prepare(sync,user,new(){TrackUri=q.GetCurrentItem()!.Song.SpotifyUri,IsPlaying=true,ProgressMs=1000,DurationMs=10000});
        await Task.Delay(1000);Assert.False(probe.PauseStarted.Task.IsCompleted);Assert.StartsWith("spotify:",q.GetCurrentItem()!.Song.SpotifyUri);
        await sync.StopAsync(default);
    }
    private static async Task Prepare(PlaybackSyncService sync,UserServices user,PlaybackState state)=>await(Task<bool>)typeof(PlaybackSyncService).GetMethod("PrepareSuccessorAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(sync,[user,state,user.PlaybackGeneration,CancellationToken.None])!;
    public class BoundarySpotify:DispatchProxy
    {
        public TaskCompletionSource PauseStarted=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name switch
        {
            "get_IsAuthenticated"=>true,"PauseIfCurrentAsync"=>Pause(),"SetManagedQueueAsync"=>Task.CompletedTask,"WarmTrackAsync"=>Task.CompletedTask,
            _=>throw new Exception("Unexpected call; no stale boundary read allowed: "+method.Name)
        };
        private async Task Pause(){PauseStarted.TrySetResult();await Release.Task;}
    }
    private sealed class Profile:IAsyncDisposable
    {
        public string Path{get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"musick-integrated-"+Guid.NewGuid().ToString("N"));
        public Microsoft.AspNetCore.Builder.WebApplication App{get;}
        public Profile(){Directory.CreateDirectory(Path);var builder=WebHost.CreateBuilder([]);builder.Configuration["DataDirectory"]=Path;builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+System.IO.Path.Combine(Path,"test.db");App=WebHost.Configure(builder);}
        public async ValueTask DisposeAsync(){await App.DisposeAsync();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(!System.IO.Path.GetFullPath(Path).StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new Exception("Invalid temp profile");Directory.Delete(Path,true);}
    }
}
