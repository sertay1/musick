using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MusicBot;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Services;
using MusicBot.Services.Platforms;
using Xunit;
namespace MusicBot.Tests;
public class ExternalSpotifyAnnouncementTests
{
    private static Song External()=>new(){SpotifyUri="spotify:track:external",Title="Radio song",Artist="Radio artist"};
    private static Task Announce(Profile p,Song song,bool confirmed=false)=>(Task)typeof(PlaybackSyncService).GetMethod("TryAnnounceTrackAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(p.Sync,[song,"Old viewer",confirmed])!;
    [Fact] public async Task RetiringAnUnrelatedQueueOwnerDoesNotResetRadioConfirmationOrAttributeItsRequester()
    {
        await using var p=new Profile();var old=p.User.Queue.PlayNow(new(){SpotifyUri="spotify:track:old"},"Old viewer","kick");
        var radio=External();p.User.Queue.UpdateProgress(1000,true,radio);await Announce(p,radio);
        Assert.Empty(p.Messages);p.User.Queue.DiscardCurrent(old.Id);p.User.Queue.UpdateProgress(2000,true,radio);
        await Announce(p,radio);await Announce(p,radio);
        Assert.Single(p.Messages);Assert.Contains("Radio song",p.Messages[0]);Assert.DoesNotContain("Old viewer",p.Messages[0]);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task QueuedSendSurvivesUnrelatedOwnerRemovalButNeverAnnouncesAReplacedActualTrack(bool replaceActual)
    {
        await using var p=new Profile();var old=p.User.Queue.PlayNow(new(){SpotifyUri="spotify:track:old"},"Old viewer","kick");var radio=External();p.User.Queue.UpdateProgress(1000,true,radio);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Chat.GuardedKickSender=async(message,current)=>{started.TrySetResult();await release.Task;if(current?.Invoke()==false)return false;p.Messages.Add(message);return true;};
        var pending=Announce(p,radio,true);await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        p.User.Queue.DiscardCurrent(old.Id);p.User.Queue.UpdateProgress(1500,true,replaceActual?new(){SpotifyUri="spotify:track:other",Title="Other"}:radio);
        release.TrySetResult();await pending;
        Assert.Equal(replaceActual?0:1,p.Messages.Count);
    }
    [Fact] public async Task FailedManagedSelectionDoesNotSilenceStableExternalSpotifyPlayback()
    {
        await using var p=new Profile();p.User.Queue.PlayNow(new(){SpotifyUri="spotify:track:failed"},"Old viewer","autoqueue",true);p.User.SpotifyNeedsUserAction=true;
        var messages=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Chat.RegisterSender("kick",message=>{p.Messages.Add(message);messages.TrySetResult();return Task.FromResult(true);});
        await p.Sync.StartAsync(default);
        try { await messages.Task.WaitAsync(TimeSpan.FromSeconds(6));Assert.Single(p.Messages);Assert.Contains("Radio song",p.Messages[0]);Assert.True(p.User.SpotifyNeedsUserAction); }
        finally { await p.Sync.StopAsync(default); }
        Assert.Equal(0,p.Probe.PlaybackWrites);
    }
    [Fact] public async Task DisabledRequesterFreeTemplateStaysSilentForExternalSpotify()
    {
        await using var p=new Profile();p.App.Services.GetRequiredService<MusicBot.Services.Commands.CommandConfigService>().Current.Commands["announce"].Templates["success_no_requester"]="";
        var radio=External();p.User.Queue.UpdateProgress(1000,true,radio);await Announce(p,radio);await Announce(p,radio);Assert.Empty(p.Messages);
    }
    public class SpotifyProbe:DispatchProxy
    {
        public int PlaybackWrites;
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name switch
        {
            "get_IsAuthenticated"=>true,"GetPlaybackStateAsync"=>Task.FromResult<PlaybackState?>(new(){TrackUri="spotify:track:external",Title="Radio song",Artist="Radio artist",IsPlaying=true,DurationMs=180000,ProgressMs=50000}),
            "SetManagedQueueAsync"=>Task.CompletedTask,
            _=>Unexpected(method.Name)
        };
        private object Unexpected(string name){PlaybackWrites++;throw new Exception("Announcement must not control Spotify: "+name);}
    }
    private sealed class Profile:IAsyncDisposable
    {
        private readonly string path=Path.Combine(Path.GetTempPath(),"musick-radio-announcement-"+Guid.NewGuid().ToString("N"));
        public Microsoft.AspNetCore.Builder.WebApplication App{get;}
        public UserServices User{get;} public PlaybackSyncService Sync{get;} public ChatResponseService Chat{get;}
        public SpotifyProbe Probe{get;} public List<string> Messages{get;}=[];
        public Profile()
        {
            Directory.CreateDirectory(path);var builder=WebHost.CreateBuilder([]);builder.Configuration["DataDirectory"]=path;builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            builder.Configuration["Queue:AutoQueueEnabled"]="false";App=WebHost.Configure(builder);
            var users=App.Services.GetRequiredService<UserContextManager>();var original=users.GetOrCreate(LocalUser.Id);
            var spotify=DispatchProxy.Create<ISpotifyService,SpotifyProbe>();Probe=(SpotifyProbe)(object)spotify;
            User=new(LocalUser.Id,original.Queue,spotify,original.Player);
            ((ConcurrentDictionary<Guid,UserServices>)typeof(UserContextManager).GetField("_users",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(users)!)[LocalUser.Id]=User;
            Chat=App.Services.GetRequiredService<ChatResponseService>();Chat.RegisterSender("kick",message=>{Messages.Add(message);return Task.FromResult(true);});
            Sync=App.Services.GetRequiredService<PlaybackSyncService>();
        }
        public async ValueTask DisposeAsync(){await App.DisposeAsync();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(!Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new Exception("Invalid test profile");Directory.Delete(path,true);}
    }
}
