using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Services.Spotify;
using Xunit;
namespace MusicBot.Tests;
public class SpotifyPlaybackEngineTests
{
    private sealed class Port : ISpotifyPlaybackPort
    {
        public PlaybackState? State = new() { TrackUri="old", IsPlaying=true };
        public List<Song> Songs = new(); public int Adds,Nexts,Resumes,Waits,WaitsBeforeNext; public bool Confirm=true, CancelAfterAdd, HideEnqueued; public bool Current=true, CancelOnNext; public bool CanWake, IgnorePause, StartOnNext; public int Pauses, Wakes, SilenceRestores; public bool Silenced; public Action? BeforeQueue;
        public Task<PlaybackState?> ReadAsync()=>Task.FromResult(State);
        public Task<SpotifyQueueState> QueueAsync(){ BeforeQueue?.Invoke(); return Task.FromResult(new SpotifyQueueState { CurrentlyPlaying=State==null?null:new Song{SpotifyUri=State.TrackUri}, Queue=Songs.ToList() }); }
        public Task EnqueueAsync(string uri) { Adds++; if (!HideEnqueued) Songs.Add(new Song{SpotifyUri=uri});if(CancelAfterAdd)Current=false;return Task.CompletedTask; }
        public Task NextAsync(){WaitsBeforeNext=Waits;Nexts++;if(Confirm){State=new(){TrackUri=Songs[0].SpotifyUri,IsPlaying=StartOnNext || State?.IsPlaying==true};Songs.RemoveAt(0);}if(CancelOnNext)Current=false;return Task.CompletedTask;}
        public Task<IAsyncDisposable> SilenceAsync(){Silenced=true;return Task.FromResult<IAsyncDisposable>(new Restore(this));}
        private sealed class Restore(Port owner):IAsyncDisposable { public ValueTask DisposeAsync(){ if(owner.State!=null)owner.State.IsPlaying=false;owner.Silenced=false;owner.SilenceRestores++;return ValueTask.CompletedTask;} }
        public Task PauseAsync(){Pauses++;if(!IgnorePause && State!=null)State.IsPlaying=false;return Task.CompletedTask;}
        public Task<bool> EnsureSessionAsync(){Wakes++;return Task.FromResult(CanWake);}
        public Task ResumeAsync(){Resumes++;State!.IsPlaying=true;return Task.CompletedTask;}
        public Task WaitAsync(){Waits++;return Task.CompletedTask;}
    }
    [Fact] public async Task SelectedTrackIsQueuedThenAdvancedExactlyOnce(){var p=new Port();var e=new SpotifyPlaybackEngine();await e.SelectAsync("target",p,()=>true);await e.SelectAsync("target",p,()=>true);Assert.Equal(1,p.Adds);Assert.Equal(1,p.Nexts);Assert.Equal(0,p.Resumes);Assert.Equal(SpotifyTransitionPhase.Confirmed,e.Phase);}
    [Fact] public async Task ExistingNativeQueueEntryIsNotDuplicated(){var p=new Port();p.Songs.Add(new(){SpotifyUri="target"});await new SpotifyPlaybackEngine().SelectAsync("target",p,()=>true);Assert.Equal(0,p.Adds);Assert.Equal(1,p.Nexts);}
    [Fact] public async Task OlderNativeEntriesArePassedOnlyWhilePaused(){var p=new Port();p.Songs.Add(new(){SpotifyUri="older"});await new SpotifyPlaybackEngine().SelectAsync("target",p,()=>true);Assert.Equal(2,p.Nexts);Assert.Equal(1,p.Pauses);Assert.Equal(1,p.Resumes);Assert.Equal("target",p.State!.TrackUri);Assert.True(p.State.IsPlaying);}
    [Fact] public async Task FailedPauseNeverSendsNext(){var p=new Port{IgnorePause=true};p.Songs.Add(new(){SpotifyUri="older"});await Assert.ThrowsAsync<SpotifyPlayerCommandException>(()=>new SpotifyPlaybackEngine().SelectAsync("target",p,()=>true));Assert.Equal(0,p.Nexts);}
    [Fact] public async Task DeepNativeQueueNeverDrainsInAnUnboundedLoop(){var p=new Port();p.Songs.AddRange(Enumerable.Range(0,5).Select(i=>new Song{SpotifyUri="old"+i}));await Assert.ThrowsAsync<SpotifyPlayerCommandException>(()=>new SpotifyPlaybackEngine().SelectAsync("target",p,()=>true));Assert.Equal(0,p.Nexts+p.Pauses);}
    [Fact] public async Task EmptyAvailableDeviceCanSelectWithoutResettingContext(){var p=new Port{State=null,CanWake=true};await new SpotifyPlaybackEngine().SelectAsync("target",p,()=>true);Assert.Equal(1,p.Wakes);Assert.Equal(1,p.Adds);Assert.Equal(1,p.Nexts);Assert.Equal(1,p.Resumes);Assert.Equal("target",p.State!.TrackUri);}
    [Fact] public async Task TimedOutSilentNextIsNotRepeated(){var p=new Port{Confirm=false};p.Songs.Add(new(){SpotifyUri="older"});var engine=new SpotifyPlaybackEngine();await Assert.ThrowsAsync<SpotifyPlayerCommandException>(()=>engine.SelectAsync("target",p,()=>true));await Assert.ThrowsAsync<SpotifyPlayerCommandException>(()=>engine.SelectAsync("target",p,()=>true));Assert.Equal(1,p.Nexts);Assert.Equal(0,p.Resumes);}
    [Fact] public async Task ProviderStartingPlaybackOnNextIsMutedAndPausedBeforeRestore(){var p=new Port{StartOnNext=true};p.Songs.Add(new(){SpotifyUri="older"});await new SpotifyPlaybackEngine().SelectAsync("target",p,()=>true);Assert.Equal(2,p.Nexts);Assert.Equal(1,p.Resumes);Assert.Equal("target",p.State!.TrackUri);Assert.False(p.Silenced);Assert.Equal(1,p.SilenceRestores);}

    [Fact] public async Task CancelledSilentSelectionRestoresMuteWithoutResumingAnIntermediateTrack(){var p=new Port{StartOnNext=true,CancelOnNext=true};p.Songs.Add(new(){SpotifyUri="older"});var e=new SpotifyPlaybackEngine();await e.SelectAsync("target",p,()=>p.Current);Assert.Equal(1,p.Nexts);Assert.Equal(0,p.Resumes);Assert.False(p.Silenced);Assert.False(p.State!.IsPlaying);Assert.Equal(1,p.SilenceRestores);Assert.Equal(SpotifyTransitionPhase.Cancelled,e.Phase);}
    [Fact] public async Task ChangedNativeQueueStopsWithoutBlindSkipping(){var p=new Port();p.Songs.Add(new(){SpotifyUri="older"});var reads=0;p.BeforeQueue=()=>{if(++reads==3)p.Songs.Insert(0,new(){SpotifyUri="concurrent"});};await Assert.ThrowsAsync<SpotifyPlayerCommandException>(()=>new SpotifyPlaybackEngine().SelectAsync("target",p,()=>true));Assert.Equal(0,p.Nexts);}

    [Fact] public async Task CancelAfterEnqueueNeverSendsNext(){var p=new Port{CancelAfterAdd=true};var e=new SpotifyPlaybackEngine();await e.SelectAsync("target",p,()=>p.Current);Assert.Equal(0,p.Nexts);Assert.Equal(SpotifyTransitionPhase.Cancelled,e.Phase);}
    [Fact] public async Task IdleTransportIsNotResetOrReplayed(){var p=new Port{State=null};await Assert.ThrowsAsync<SpotifyPlayerCommandException>(()=>new SpotifyPlaybackEngine().SelectAsync("target",p,()=>true));Assert.Equal(0,p.Adds+p.Nexts+p.Resumes);}
    [Fact] public async Task UnconfirmedTransitionDoesNotLoopOrClaimSuccess(){var p=new Port{Confirm=false};var e=new SpotifyPlaybackEngine();await Assert.ThrowsAsync<SpotifyPlayerCommandException>(()=>e.SelectAsync("target",p,()=>true));Assert.Equal(1,p.Nexts);Assert.Equal(1,p.Adds);Assert.Equal(SpotifyTransitionPhase.Uncertain,e.Phase);}
    [Fact] public async Task PausedCurrentTrackResumesWithoutRestart(){var p=new Port{State=new(){TrackUri="target",IsPlaying=false,ProgressMs=41000}};await new SpotifyPlaybackEngine().SelectAsync("target",p,()=>true);Assert.Equal(1,p.Resumes);Assert.Equal(0,p.Adds+p.Nexts);Assert.Equal(41000,p.State.ProgressMs);}
    [Fact] public async Task RelinkedCurrentTrackDoesNotSendCommands(){var p=new Port{State=new(){TrackUri="playable",LinkedFromUri="target",IsPlaying=true}};await new SpotifyPlaybackEngine().SelectAsync("target",p,()=>true);Assert.Equal(0,p.Adds+p.Nexts+p.Resumes);}
    [Fact]
    public async Task RepeatedRequestWhileNextIsUnconfirmedDoesNotSendSecondNext()
    {
        var port = new Port { Confirm = false };
        var engine = new SpotifyPlaybackEngine();
        await Assert.ThrowsAsync<SpotifyPlayerCommandException>(() => engine.SelectAsync("target", port, () => true));
        await Assert.ThrowsAsync<SpotifyPlayerCommandException>(() => engine.SelectAsync("target", port, () => true));
        Assert.Equal(1, port.Nexts);
        Assert.Equal(1, port.Adds);
        port.State = new() { TrackUri = "target", IsPlaying = true };
        await engine.SelectAsync("target", port, () => true);
        Assert.Equal(SpotifyTransitionPhase.Confirmed, engine.Phase);
        Assert.Equal(1, port.Nexts);
    }
    [Fact]
    public async Task AcceptedButInvisibleEnqueueIsNotRepeated()
    {
        var port = new Port { HideEnqueued = true };
        var engine = new SpotifyPlaybackEngine();
        await Assert.ThrowsAsync<SpotifyPlayerCommandException>(() => engine.SelectAsync("target", port, () => true));
        await Assert.ThrowsAsync<SpotifyPlayerCommandException>(() => engine.SelectAsync("target", port, () => true));
        Assert.Equal(1, port.Adds);
        Assert.Equal(0, port.Nexts);
    }
    [Fact]
    public async Task PreparedSuccessorIsEnqueuedOnceWithoutSkippingCurrentTrack()
    {
        var port=new Port(); var engine=new SpotifyPlaybackEngine();
        Assert.True(await engine.PrepareAsync("target",port,()=>true));
        Assert.True(await engine.PrepareAsync("target",port,()=>true));
        Assert.Equal(1,port.Adds); Assert.Equal(0,port.Nexts);
        await engine.SelectAsync("target",port,()=>true);
        Assert.Equal(1,port.Adds); Assert.Equal(1,port.Nexts); Assert.Equal(0,port.WaitsBeforeNext);
    }
    [Fact]
    public async Task PreparedTrackAlreadyStartedNaturallyIsAdoptedWithoutSecondSkip()
    {
        var port=new Port(); var engine=new SpotifyPlaybackEngine();
        await engine.PrepareAsync("target",port,()=>true);
        port.State=new(){TrackUri="target",IsPlaying=true}; port.Songs.Clear();
        await engine.SelectAsync("target",port,()=>true);
        Assert.Equal(0,port.Nexts); Assert.Equal(1,port.Adds);
    }
    [Fact]
    public async Task CancelledPreparationDoesNotEnableNativeContinuation()
    {
        var port=new Port{CancelAfterAdd=true}; var engine=new SpotifyPlaybackEngine();
        Assert.False(await engine.PrepareAsync("target",port,()=>port.Current));
        Assert.Equal(0,port.Nexts);
    }
}
