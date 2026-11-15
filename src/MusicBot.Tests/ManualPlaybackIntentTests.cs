using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Services;
using Xunit;
namespace MusicBot.Tests;
public class ManualPlaybackIntentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSkipsCoalesce_AndPausePreventsLatePlaybackOrRestoration(bool transportFails)
    {
        var path = Path.Combine(Path.GetTempPath(), "musick-intent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var builder = WebHost.CreateBuilder([]); builder.Configuration["DataDirectory"] = path; builder.Configuration["ConnectionStrings:DefaultConnection"] = "Data Source=" + Path.Combine(path,"test.db");
            await using var app = WebHost.Configure(builder);
            var queue = new QueueService(); var first = Song('a'); var next = Song('b'); var last = Song('c');
            queue.PlayNow(first,"Viewer","kick"); queue.AddSong(next,"Second","kick"); queue.AddSong(last,"Third","kick");
            var spotify = DispatchProxy.Create<ISpotifyService, SpotifyProbe>(); var probe = (SpotifyProbe)(object)spotify; probe.Fails = transportFails;
            var user = new UserServices(LocalUser.Id, queue, spotify, DispatchProxy.Create<ILocalPlayerService, PlayerProbe>());
            var sync = app.Services.GetRequiredService<PlaybackSyncService>();
            var pending = sync.SkipCurrentAsync(user); await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(sync.RequestSkip(user, "Owner", "http")); await sync.SkipCurrentAsync(user);
            Assert.Equal(next.SpotifyUri, queue.GetCurrentItem()!.Song.SpotifyUri); Assert.Single(queue.GetUpcoming());
            await sync.PauseCurrentAsync(user); probe.Release.TrySetResult(true); await pending;
            Assert.True(user.PlaybackPausedByUser); Assert.False(queue.GetNowPlaying().IsPlaying);
            Assert.Equal(next.SpotifyUri, queue.GetCurrentItem()!.Song.SpotifyUri); Assert.Equal(1, probe.Plays); Assert.Equal(0, user.SkipPending);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path,true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSkipReturnsSuccessOnlyAfterSpotifyVerification(bool silent)
    {
        var path = Path.Combine(Path.GetTempPath(), "musick-ack-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var builder=WebHost.CreateBuilder([]);builder.Configuration["DataDirectory"]=path;builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            await using var app=WebHost.Configure(builder);var queue=new QueueService();queue.PlayNow(Song('a'),"Viewer","kick");queue.AddSong(Song('b'),"Other","kick");
            var spotify=DispatchProxy.Create<ISpotifyService,SpotifyProbe>();var probe=(SpotifyProbe)(object)spotify;
            var user=new UserServices(LocalUser.Id,queue,spotify,DispatchProxy.Create<ILocalPlayerService,PlayerProbe>());
            var pending=app.Services.GetRequiredService<CommandRouterService>().HandleAsync(new BotCommand{Type="skip",RequestedBy="Owner",Platform="kick",Badges=["broadcaster"],Definition=silent?new CommandDefinition{Templates=new(){{"success",""}}}:null},user);
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(pending.IsCompleted);Assert.Equal(1,user.SkipPending);
            probe.Release.TrySetResult(true);
            var result=await pending.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(result.Success);if(silent)Assert.True(string.IsNullOrWhiteSpace(result.Message));Assert.Equal(0,user.SkipPending);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(path,true);}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateResumeCannotUndoPauseOrMarkNewYouTubeSongPlaying(bool switchSource)
    {
        var path=Path.Combine(Path.GetTempPath(),"musick-late-resume-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        try
        {
            var builder=WebHost.CreateBuilder([]);builder.Configuration["DataDirectory"]=path;builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            await using var app=WebHost.Configure(builder);
            var queue=new QueueService();queue.PlayNow(Song('a'),"Viewer","kick");
            var spotify=DispatchProxy.Create<ISpotifyService,SpotifyProbe>();var probe=(SpotifyProbe)(object)spotify;
            var user=new UserServices(LocalUser.Id,queue,spotify,DispatchProxy.Create<ILocalPlayerService,PlayerProbe>());
            var users=app.Services.GetRequiredService<UserContextManager>();
            var map=(System.Collections.Concurrent.ConcurrentDictionary<Guid,UserServices>)typeof(UserContextManager).GetField("_users",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(users)!;
            map[LocalUser.Id]=user;
            var controller=ActivatorUtilities.CreateInstance<MusicBot.Controllers.CommandsController>(app.Services);
            var pending=controller.Resume();await probe.ResumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if(switchSource){queue.PlayNow(new Song{SpotifyUri="youtube:aaaaaaaaaaa",Title="New source"},"Viewer","kick");queue.UpdateProgress(0,false);Interlocked.Increment(ref user.PlaybackGeneration);}
            else await app.Services.GetRequiredService<PlaybackSyncService>().PauseCurrentAsync(user);
            probe.ResumeRelease.SetResult(true);await pending;Assert.False(queue.GetNowPlaying().IsPlaying);
            if(switchSource)Assert.Equal("youtube:aaaaaaaaaaa",queue.GetCurrentItem()!.Song.SpotifyUri);
            else Assert.True(user.PlaybackPausedByUser);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(path,true);}
    }
    [Fact]
    public async Task SeekFromOldTrackIsRejectedBeforeTouchingNewPlayback()
    {
        var path=Path.Combine(Path.GetTempPath(),"musick-stale-seek-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        try
        {
            var builder=WebHost.CreateBuilder([]);builder.Configuration["DataDirectory"]=path;builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            await using var app=WebHost.Configure(builder);var queue=new QueueService();queue.PlayNow(Song('b'),"Viewer","kick");
            var users=app.Services.GetRequiredService<UserContextManager>();
            var map=(System.Collections.Concurrent.ConcurrentDictionary<Guid,UserServices>)typeof(UserContextManager).GetField("_users",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(users)!;
            map[LocalUser.Id]=new UserServices(LocalUser.Id,queue,DispatchProxy.Create<ISpotifyService,SpotifyProbe>(),DispatchProxy.Create<ILocalPlayerService,PlayerProbe>());
            var controller=ActivatorUtilities.CreateInstance<MusicBot.Controllers.PlayerController>(app.Services);
            var response=await controller.Seek(new MusicBot.Controllers.SeekRequest{PositionMs=12000,ExpectedTrackUri=Song('a').SpotifyUri});
            Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(response);Assert.Equal(0,queue.GetNowPlaying().ProgressMs);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(path,true);}
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSpotifySnapshotCannotReplaceJustConfirmedPlayerTrack_AndLateConfirmationRecovers(bool lateConfirmation)
    {
        var path=Path.Combine(Path.GetTempPath(),"musick-stale-state-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        try
        {
            var builder=WebHost.CreateBuilder([]);builder.Configuration["DataDirectory"]=path;builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            await using var app=WebHost.Configure(builder);var queue=new QueueService();var expected=Song('a');queue.PlayNow(expected,"Viewer","kick");queue.UpdateProgress(1000,true,expected);
            var users=app.Services.GetRequiredService<UserContextManager>();var map=(System.Collections.Concurrent.ConcurrentDictionary<Guid,UserServices>)typeof(UserContextManager).GetField("_users",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(users)!;
            var observedSpotify = DispatchProxy.Create<ISpotifyService,StaleStateProbe>();
            ((StaleStateProbe)(object)observedSpotify).MatchExpected = lateConfirmation;
            map[LocalUser.Id]=new UserServices(LocalUser.Id,queue,observedSpotify,DispatchProxy.Create<ILocalPlayerService,PlayerProbe>());
            if (lateConfirmation) { map[LocalUser.Id].SpotifyNeedsUserAction = true; queue.UpdateProgress(0, false); }
            var sync=app.Services.GetRequiredService<PlaybackSyncService>();
            typeof(PlaybackSyncService).GetField("_requestedSpotifyUri",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(sync,expected.SpotifyUri);
            typeof(PlaybackSyncService).GetField("_requestedSpotifyAt",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(sync,DateTimeOffset.UtcNow);
            using var stop=new CancellationTokenSource();
            var run=(Task)typeof(PlaybackSyncService).GetMethod("RunSpotifySyncAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(sync,[stop.Token])!;
            try{Assert.Equal(expected.SpotifyUri,queue.GetNowPlaying().SpotifyTrack!.SpotifyUri);Assert.True(queue.GetNowPlaying().IsPlaying);Assert.False(map[LocalUser.Id].SpotifyNeedsUserAction);}
            finally{stop.Cancel();await run;}
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(path,true);}
    }
    [Fact]
    public async Task ResumeDoesNotRestartPendingSongWhenSpotifyAlreadyPlaysAnotherTrack()
    {
        var path=Path.Combine(Path.GetTempPath(),"musick-actual-resume-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        try
        {
            var builder=WebHost.CreateBuilder([]);builder.Configuration["DataDirectory"]=path;builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            await using var app=WebHost.Configure(builder);var queue=new QueueService();queue.PlayNow(Song('a'),"Viewer","kick");queue.UpdateProgress(0,false);
            var user=new UserServices(LocalUser.Id,queue,DispatchProxy.Create<ISpotifyService,StaleStateProbe>(),DispatchProxy.Create<ILocalPlayerService,PlayerProbe>()){SpotifyNeedsUserAction=true};
            var users=app.Services.GetRequiredService<UserContextManager>();
            var map=(System.Collections.Concurrent.ConcurrentDictionary<Guid,UserServices>)typeof(UserContextManager).GetField("_users",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(users)!;map[LocalUser.Id]=user;
            var controller=ActivatorUtilities.CreateInstance<MusicBot.Controllers.CommandsController>(app.Services);
            await controller.Resume();await controller.Resume();
            Assert.True(queue.GetNowPlaying().IsPlaying);Assert.Equal(Song('b').SpotifyUri,queue.GetNowPlaying().SpotifyTrack!.SpotifyUri);
            Assert.True(user.SpotifyNeedsUserAction);Assert.Equal(Song('a').SpotifyUri,queue.GetCurrentItem()!.Song.SpotifyUri);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(path,true);}
    }
    public class StaleStateProbe:DispatchProxy
    {
        public bool MatchExpected;
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name switch
        {"SetManagedQueueAsync"=>Task.CompletedTask,"get_IsAuthenticated"=>true,"GetPlaybackStateAsync"=>Task.FromResult<PlaybackState?>(new PlaybackState{TrackUri="spotify:track:"+new string(MatchExpected?'a':'b',22),IsPlaying=true,ProgressMs=0,DurationMs=180000}),_=>throw new InvalidOperationException("Unexpected call: "+method.Name)};
    }

    private static Song Song(char id)=>new(){SpotifyUri="spotify:track:"+new string(id,22),Title="Song "+id};
    public class SpotifyProbe:DispatchProxy
    {
        public TaskCompletionSource<bool> ResumeStarted=new(TaskCreationOptions.RunContinuationsAsynchronously),ResumeRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Plays;public bool Fails;public TaskCompletionSource<bool> Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name switch{"SetManagedQueueAsync"=>Task.CompletedTask,"get_IsAuthenticated"=>true,"PlayIfCurrentAsync"=>Play((Func<bool>)args![1]!),"PauseAsync"=>Task.CompletedTask,"GetPlaybackStateAsync"=>Task.FromResult<PlaybackState?>(new PlaybackState{TrackUri="spotify:track:"+new string('a',22),IsPlaying=false}),"ResumeAsync"=>Resume(),_=>throw new InvalidOperationException("Unexpected Spotify call: "+method.Name)};
        private async Task Resume(){ResumeStarted.TrySetResult(true);await ResumeRelease.Task;}
        private async Task<bool> Play(Func<bool> current){Plays++;Started.TrySetResult(true);await Release.Task;if(Fails)throw new InvalidOperationException("Transport failed after pause");return current();}
    }
    public class PlayerProbe:DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name switch{"StopAsync"=>Task.CompletedTask,"get_CurrentFilePath"=>null,_=>throw new InvalidOperationException("Unexpected player call: "+method.Name)};
    }
}
