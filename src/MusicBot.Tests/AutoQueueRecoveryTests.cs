using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Services;
using MusicBot.Services.Player;
using MusicBot.Tests.TestHelpers;
using Xunit;

namespace MusicBot.Tests;

public class AutoQueueRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UnconfirmedSpotifyPlayStopsAfterOnePoolSong_AndExplicitRetryCanRecover(bool viewerArrives, bool naturalAdvance)
    {
        var path = Path.Combine(Path.GetTempPath(),"musick-auto-stop-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var builder=WebHost.CreateBuilder([]); builder.Configuration["DataDirectory"]=path;
            builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            builder.Configuration["Queue:AutoQueueEnabled"]="true";
            await using var app=WebHost.Configure(builder);
            var pool=app.Services.GetRequiredService<AutoQueueService>();
            await pool.BulkAddAsync(Enumerable.Range(0,400).Select(i=>new Song{SpotifyUri=$"spotify:track:{i:D22}",Title=$"Pool {i}"}));
            var spotify=DispatchProxy.Create<ISpotifyService,SpotifyProbe>(); var probe=(SpotifyProbe)(object)spotify;
            probe.FailuresLeft=5;
            var queue=new QueueService(); using var player=new HybridPlayerService(NullLogger<LocalPlayerService>.Instance,FakeHub.Instance);
            var user=new UserServices(LocalUser.Id,queue,spotify,player); var sync=app.Services.GetRequiredService<PlaybackSyncService>();
            var previous=queue.PlayNow((await pool.GetRandomSongAsync())!,"Otomatik Kuyruk","autoqueue",true);
            if(viewerArrives)probe.DuringFailure=()=>queue.AddSong(new Song{SpotifyUri="spotify:track:"+new string('z',22),Title="Viewer song"},"Viewer","kick");
            if(naturalAdvance)
            {
                var state=queue.GetNowPlaying(); queue.Advance();
                var next=queue.PlayNow((await pool.GetRandomSongAsync())!,"Otomatik Kuyruk","autoqueue",true);
                var operation=(Task)typeof(PlaybackSyncService).GetMethod("StartAdvancedTrackAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(sync,[user,previous,state,next])!;
                await Assert.ThrowsAsync<InvalidOperationException>(()=>operation);
            }
            else await Assert.ThrowsAsync<InvalidOperationException>(()=>sync.SkipCurrentAsync(user));
            Assert.Single(probe.Attempts); Assert.False(user.SpotifyTransitioning);
            Assert.Equal(400,await pool.CountAsync()); Assert.NotNull(queue.GetCurrentItem());
            Assert.DoesNotContain(queue.GetUpcoming(), i => i.Song.SpotifyUri == queue.GetCurrentItem()!.Song.SpotifyUri);
            if(viewerArrives) Assert.Contains(queue.GetUpcoming(),i=>i.RequestedBy=="Viewer");
            Assert.False(await sync.TryStartAutoQueueAsync(user)); Assert.Single(probe.Attempts);
            probe.FailuresLeft=0;
            if(naturalAdvance) await sync.StartCurrentTrackAsync(user,userInitiated:true);
            else
            {
                // Explicit Play must also clear an earlier user pause. Otherwise only Next wakes playback.
                user.PlaybackPausedByUser=true;
                await sync.StartCurrentTrackAsync(user,userInitiated:true);
            }
            Assert.Equal(2,probe.Attempts.Count); Assert.Equal(probe.Attempts[0],probe.Attempts[1]);
            Assert.True(queue.GetNowPlaying().IsPlaying);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(path,true);}
    }

    public class SpotifyProbe : DispatchProxy
    {
        public int FailuresLeft;
        public Action? DuringFailure;
        public List<string> Attempts = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_IsAuthenticated") return true;
            if (method.Name == nameof(ISpotifyService.PlayIfCurrentAsync))
            {
                Attempts.Add((string)args![0]!);
                if (FailuresLeft-- > 0) { DuringFailure?.Invoke(); DuringFailure = null; return Task.FromException<bool>(new InvalidOperationException("Spotify playback not confirmed")); }
                return Task.FromResult(((Func<bool>)args[1]!)());
            }
            if (method.Name == nameof(ISpotifyService.SetManagedQueueAsync)) return Task.CompletedTask;
            if (method.Name == nameof(ISpotifyService.SkipAsync)) return Task.CompletedTask;
            throw new InvalidOperationException("Unexpected Spotify call: " + method.Name);
        }
    }
}
