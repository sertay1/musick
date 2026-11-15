using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Services;
using Xunit;
namespace MusicBot.Tests;
public class YouTubeFallbackTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EmbedErrorNeverDownloadsOrReplaysOldCache_AndLateFailureCannotReplaceANewerTrack(bool staleReport, bool changedDuringStop)
    {
        var path = Path.Combine(Path.GetTempPath(), "musick-fallback-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var builder = WebHost.CreateBuilder([]); builder.Configuration["DataDirectory"] = path;
            builder.Configuration["ConnectionStrings:DefaultConnection"] = "Data Source=" + Path.Combine(path, "test.db");
            await using var app = WebHost.Configure(builder);
            var validator = app.Services.GetRequiredService<MusicBot.Services.Downloader.YtDlpDownloaderService>();
            var verified = (System.Collections.Concurrent.ConcurrentDictionary<string, Song>)typeof(MusicBot.Services.Downloader.YtDlpDownloaderService)
                .GetField("_musicVideos", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(validator)!;
            verified["aaaaaaaaaaa"] = new Song { SpotifyUri="youtube:aaaaaaaaaaa", Title="Verified song", Artist="Artist" };
            var file = Path.Combine(path, "cached-audio.bin"); await File.WriteAllBytesAsync(file, new byte[100001]);
            var queue = new QueueService(); var item = queue.PlayNow(new Song { SpotifyUri = "youtube:aaaaaaaaaaa", Title = "Song", LocalFilePath = file }, "Viewer", "kick");
            var player = DispatchProxy.Create<ILocalPlayerService, PlayerProbe>(); var probe = (PlayerProbe)(object)player;
            if (changedDuringStop) probe.OnStop = () => queue.PlayNow(new Song { SpotifyUri = "youtube:bbbbbbbbbbb", Title = "New" }, "Viewer2", "kick");
            var spotify = DispatchProxy.Create<ISpotifyService, SpotifyProbe>();
            var user = new UserServices(LocalUser.Id, queue, spotify, player);
            await app.Services.GetRequiredService<PlaybackSyncService>().RecoverYouTubePlaybackAsync(user, staleReport ? 6 : 7, 150);
            if (staleReport || changedDuringStop) Assert.Null(probe.Played);
            else { Assert.Null(probe.Played); Assert.NotNull(item.DownloadError); Assert.False(queue.GetNowPlaying().IsPlaying); }
            Assert.Equal(changedDuringStop ? "youtube:bbbbbbbbbbb" : "youtube:aaaaaaaaaaa", queue.GetCurrentItem()!.Song.SpotifyUri);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }
    [Fact]
    public async Task MissingSavedAudioDeviceFallsBackToDefaultWithoutPreventingStartup()
    {
        var path = Path.Combine(Path.GetTempPath(), "musick-audio-restore-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var builder = WebHost.CreateBuilder([]); builder.Configuration["DataDirectory"] = path;
            builder.Configuration["ConnectionStrings:DefaultConnection"] = "Data Source=" + Path.Combine(path, "test.db");
            await using var app = WebHost.Configure(builder);
            using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MusicBot.Data.MusicBotDbContext>();
            var profile = (await db.Users.FindAsync(LocalUser.Id))!; profile.AudioDeviceId = "missing-device"; profile.AudioVolume = 0.42f; await db.SaveChangesAsync();
            var manager = app.Services.GetRequiredService<UserContextManager>();
            var player = DispatchProxy.Create<ILocalPlayerService, AudioProbe>(); var probe = (AudioProbe)(object)player;
            var users = (System.Collections.Concurrent.ConcurrentDictionary<Guid, UserServices>)typeof(UserContextManager).GetField("_users", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
            users[LocalUser.Id] = new UserServices(LocalUser.Id, new QueueService(), DispatchProxy.Create<ISpotifyService, SpotifyProbe>(), player);
            await ActivatorUtilities.CreateInstance<MusicBot.Services.Player.AudioDeviceRestoreService>(app.Services).StartAsync(default);
            Assert.Equal(new string?[] { "missing-device", null }, probe.Devices); Assert.Equal(0.42f, probe.Volume);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }
    [Fact]
    public async Task RejectedSpotifyPausePreventsYouTubeFromStartingOverIt()
    {
        var path=Path.Combine(Path.GetTempPath(),"musick-handoff-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        try
        {
            var builder=WebHost.CreateBuilder([]);builder.Configuration["DataDirectory"]=path;builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            await using var app=WebHost.Configure(builder);var queue=new QueueService();var file=Path.Combine(path,"cached.mp3");await File.WriteAllBytesAsync(file,new byte[100001]);
            var item=queue.PlayNow(new Song{SpotifyUri="youtube:aaaaaaaaaaa",LocalFilePath=file,Title="Requested YouTube"},"Viewer","kick");
            var player=DispatchProxy.Create<ILocalPlayerService,PlayerProbe>();var user=new UserServices(LocalUser.Id,queue,DispatchProxy.Create<ISpotifyService,HandoffSpotifyProbe>(),player);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>app.Services.GetRequiredService<PlaybackSyncService>().StartCurrentTrackAsync(user));
            Assert.Null(((PlayerProbe)(object)player).Played);Assert.Equal(item.Id,queue.GetCurrentItem()!.Id);Assert.True(user.SpotifyNeedsUserAction);Assert.False(queue.GetNowPlaying().IsPlaying);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(path,true);}
    }
    public class HandoffSpotifyProbe:DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name switch
        {"get_IsAuthenticated"=>true,"PauseIfCurrentAsync"=>Task.FromException(new InvalidOperationException("Spotify rejected pause")),_=>throw new InvalidOperationException("Unexpected call: "+method.Name)};
    }

    public class AudioProbe : DispatchProxy
    {
        public List<string?> Devices = []; public float Volume;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "SetDeviceAsync") { var id = (string?)args![0]; Devices.Add(id); return id == null ? Task.CompletedTask : Task.FromException(new InvalidOperationException("Missing audio device")); }
            if (method.Name == "SetVolume") { Volume = (float)args![0]!; return null; }
            throw new InvalidOperationException("Unexpected audio call " + method.Name);
        }
    }

    public class PlayerProbe : DispatchProxy
    {
        public string? Played; public Action? OnStop; private long version = 7;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_PlaybackVersion" => version, "get_IsPlaying" => Played != null, "get_PositionMs" => 0,
            "StopAsync" => Stop(), "PlayAsync" => Play((string)args![0]!), _ => throw new InvalidOperationException("Unexpected player call: " + method.Name)
        };
        private Task Stop() { version++; OnStop?.Invoke(); return Task.CompletedTask; }
        private Task Play(string file) { Played = file; version++; return Task.CompletedTask; }
    }
    public class SpotifyProbe : DispatchProxy
    { protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "get_IsAuthenticated" ? false : throw new InvalidOperationException("Unexpected Spotify call: " + method.Name); }
}
