using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Services;
using MusicBot.Services.Player;
using MusicBot.Tests.TestHelpers;
using System.Reflection;
using Xunit;
namespace MusicBot.Tests;
public class YouTubeCompletionTests
{
    [Fact]
    public async Task CachedFileArrivingDuringIframePlaybackCannotBlockCompletion()
    {
        var path=Path.Combine(Path.GetTempPath(),"musick-end-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var builder=WebHost.CreateBuilder([]); builder.Configuration["DataDirectory"]=path;
            builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            await using var app=WebHost.Configure(builder);
            var user=app.Services.GetRequiredService<UserContextManager>().GetOrCreate(LocalUser.Id);
            var sync=app.Services.GetRequiredService<PlaybackSyncService>();
            typeof(PlaybackSyncService).GetMethod("SubscribeToUser",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(sync,new object[]{LocalUser.Id,user});
            var item=user.Queue.PlayNow(new(){SpotifyUri="youtube:aaaaaaaaaaa",LocalFilePath="youtube:aaaaaaaaaaa"},"viewer","kick");
            user.LocalPlaybackItemId=item.Id;
            await user.Player.PlayAsync("youtube:aaaaaaaaaaa");
            var player=(HybridPlayerService)user.Player; var report=player.Snapshot();
            item.Song.LocalFilePath="download-completed.mp3";
            Assert.True(player.Report(report with{IsPlaying=true,PositionMs=2000,DurationMs=10000},false));
            Assert.Equal(2000,user.Queue.GetNowPlaying().ProgressMs);
            Assert.True(player.Report(report with{PositionMs=10000,DurationMs=10000},true));
            Assert.False(player.Report(report,true));
            for(var i=0;i<100 && (user.Queue.GetCurrentItem()!=null || player.CurrentFilePath!=null);i++) await Task.Delay(20);
            Assert.Null(user.Queue.GetCurrentItem());
            Assert.Null(player.CurrentFilePath);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path,true); }
    }
    public class RepeatDeniedProbe : DispatchProxy
    {
        public int Calls, Prepared;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if(method!.Name=="get_IsAuthenticated") return true;
            if(method.Name=="PrepareNextTrackAsync") { Prepared++; return Task.FromResult(((Func<bool>)args![1]!)()); }
            if(method.Name=="SetManagedQueueAsync") { Calls++; return Task.FromException(new MusicBot.Services.Spotify.SpotifyPlayerCommandException(403,"denied")); }
            throw new InvalidOperationException("Unexpected call: "+method.Name);
        }
    }
    [Fact]
    public async Task RepeatPermissionDenialDoesNotAbortSyncOrSpamFurtherRepeatCommands()
    {
        var path=Path.Combine(Path.GetTempPath(),"musick-repeat-denied-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var builder=WebHost.CreateBuilder([]); builder.Configuration["DataDirectory"]=path;
            builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            await using var app=WebHost.Configure(builder);
            var spotify=DispatchProxy.Create<MusicBot.Core.Interfaces.ISpotifyService,RepeatDeniedProbe>();
            using var player=new HybridPlayerService(NullLogger<LocalPlayerService>.Instance,FakeHub.Instance);
            var user=new UserServices(LocalUser.Id,new MusicBot.Core.Services.QueueService(),spotify,player);
            var sync=app.Services.GetRequiredService<PlaybackSyncService>();
            var manage=typeof(PlaybackSyncService).GetMethod("ManageRepeatAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
            await (Task)manage.Invoke(sync,new object?[]{user,true,null})!;
            await (Task)manage.Invoke(sync,new object?[]{user,false,null})!;
            Assert.Equal(1,((RepeatDeniedProbe)(object)spotify).Calls);
            Assert.False(user.SpotifyNeedsUserAction);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path,true); }
    }
    [Fact]
    public async Task PreparingSpotifyDuringYouTubeDoesNotStartOrSkipEitherPlayer()
    {
        var path=Path.Combine(Path.GetTempPath(),"musick-local-prepare-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var builder=WebHost.CreateBuilder([]); builder.Configuration["DataDirectory"]=path;
            builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            await using var app=WebHost.Configure(builder);
            var spotify=DispatchProxy.Create<MusicBot.Core.Interfaces.ISpotifyService,RepeatDeniedProbe>();
            using var player=new HybridPlayerService(NullLogger<LocalPlayerService>.Instance,FakeHub.Instance);
            var queue=new MusicBot.Core.Services.QueueService();
            var item=queue.PlayNow(new(){SpotifyUri="youtube:aaaaaaaaaaa"},"viewer","kick");
            queue.AddSong(new(){SpotifyUri="spotify:track:bbbbbbbbbbbbbbbbbbbbbb"},"next","kick");
            var user=new UserServices(LocalUser.Id,queue,spotify,player);
            var sync=app.Services.GetRequiredService<PlaybackSyncService>();
            Assert.Null(typeof(PlaybackSyncService).GetMethod("PrepareLocalSuccessorAsync",BindingFlags.NonPublic|BindingFlags.Instance));
            Assert.Equal(0,((RepeatDeniedProbe)(object)spotify).Prepared);
            Assert.Equal(item.Id,queue.GetCurrentItem()!.Id);
            Assert.Null(player.CurrentFilePath);
            Assert.Single(queue.GetUpcoming());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path,true); }
    }
}
