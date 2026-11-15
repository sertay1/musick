using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MusicBot;
using MusicBot.Controllers;
using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Core.Interfaces;
using MusicBot.Data;
using MusicBot.Services;
using MusicBot.Services.Commands;
using Xunit;
namespace MusicBot.Tests;

public class PonytailFixRegressionTests
{
    private sealed class Profile : IAsyncDisposable
    {
        public string Path { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"musick-audit-fix-"+Guid.NewGuid().ToString("N"));
        public Microsoft.AspNetCore.Builder.WebApplication App { get; }
        public Profile()
        {
            Directory.CreateDirectory(Path); var builder=WebHost.CreateBuilder([]);
            builder.Configuration["DataDirectory"]=Path; builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+System.IO.Path.Combine(Path,"test.db");
            App=WebHost.Configure(builder);
        }
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if(!System.IO.Path.GetFullPath(Path).StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new Exception("Invalid temp path");
            Directory.Delete(Path,true);
        }
    }
    [Fact] public void ViewerRequestsRemainEditableWithoutNativeQueueReservation()
    {
        var q=new QueueService(0,0); q.PlayNow(new(){SpotifyUri="A"},"host","web");
        var b=q.AddSong(new(){SpotifyUri="spotify:track:B"},"viewer","kick"); q.AddSong(new(){SpotifyUri="C"},"viewer2","kick");
        Assert.False(q.TryReserveNextTrack(b.Id));
        Assert.True(q.Reorder("C",0)); Assert.True(q.RemoveByUri(b.Song.SpotifyUri));
        Assert.Equal("C",q.GetUpcoming()[0].Song.SpotifyUri);
    }
    [Fact] public void AutomaticCandidateYieldsToViewerAndIsNeverReserved()
    {
        var q=new QueueService(0,0); q.PlayNow(new(){SpotifyUri="A"},"host","web");
        var auto=q.AddSong(new(){SpotifyUri="spotify:track:auto"},"auto","autoqueue",isPlaylistItem:true);
        Assert.False(q.TryReserveNextTrack(auto.Id));
        q.AddSong(new(){SpotifyUri="viewer"},"viewer","kick"); Assert.Equal("viewer",q.GetUpcoming()[0].Song.SpotifyUri);
    }
    [Fact] public async Task PlaylistBatchHonorsTheRoleLimit()
    {
        await using var p=new Profile(); var services=p.App.Services;
        services.GetRequiredService<QueueSettingsService>().SetChatPlaylistRequests(true);
        services.GetRequiredService<CommandConfigService>().Current.RoleProfiles["everyone"].MaxSongsInQueue=1;
        var spotify=DispatchProxy.Create<ISpotifyService,PlaylistProbe>();
        var player=DispatchProxy.Create<ILocalPlayerService,YouTubeFallbackTests.PlayerProbe>();
        var q=new QueueService(0,0); q.PlayNow(new(){SpotifyUri="spotify:track:current"},"host","web");
        var user=new UserServices(LocalUser.Id,q,spotify,player);
        var result=await services.GetRequiredService<CommandRouterService>().HandleAsync(new(){Type="play",Query="https://open.spotify.com/playlist/test",RequestedBy="viewer",Platform="kick"},user);
        Assert.True(result.Success); Assert.Single(q.GetUpcoming());
    }
    public class PlaylistProbe:DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name switch
        {
            "get_IsAuthenticated"=>true,
            "GetPlaylistTracksAsync"=>Task.FromResult(Enumerable.Range(1,3).Select(i=>new Song{SpotifyUri="spotify:track:"+i.ToString().PadLeft(22,'0')}).ToList()),
            "GetPlaybackStateAsync"=>Task.FromResult<PlaybackState?>(new(){TrackUri="spotify:track:current",IsPlaying=true}),
            _=>throw new Exception("Unexpected Spotify call: "+method.Name)
        };
    }
    [Fact] public async Task InvalidYouTubeCacheCannotStartDuringFallback()
    {
        await using var p=new Profile(); var file=System.IO.Path.Combine(p.Path,"cached.bin"); await File.WriteAllBytesAsync(file,new byte[100001]);
        var q=new QueueService(); var item=q.PlayNow(new(){SpotifyUri="youtube:bad",LocalFilePath=file},"viewer","kick");
        var player=DispatchProxy.Create<ILocalPlayerService,YouTubeFallbackTests.PlayerProbe>();
        var spotify=DispatchProxy.Create<ISpotifyService,YouTubeFallbackTests.SpotifyProbe>();
        var user=new UserServices(LocalUser.Id,q,spotify,player);
        await p.App.Services.GetRequiredService<PlaybackSyncService>().RecoverYouTubePlaybackAsync(user,7,150);
        Assert.Null(((YouTubeFallbackTests.PlayerProbe)(object)player).Played); Assert.NotNull(item.DownloadError);
    }
    [Fact] public async Task FailedImportRollsBackFilesDatabaseAndCommandMemory()
    {
        await using var p=new Profile(); using var scope=p.App.Services.CreateScope(); var sp=scope.ServiceProvider;
        var db=sp.GetRequiredService<MusicBotDbContext>(); var commands=sp.GetRequiredService<CommandConfigService>();
        var controller=ActivatorUtilities.CreateInstance<SettingsBackupController>(sp);
        controller.ControllerContext=new(){HttpContext=new DefaultHttpContext{RequestServices=sp}};
        var backup=Assert.IsType<SettingsBackup>(Assert.IsType<OkObjectResult>(await controller.Export()).Value);
        backup=JsonSerializer.Deserialize<SettingsBackup>(JsonSerializer.Serialize(backup))!;
        var config=System.IO.Path.Combine(p.Path,"appsettings.user.json"); await File.WriteAllTextAsync(config,"{\"Preserve\":\"original\"}");
        var previous=await File.ReadAllTextAsync(System.IO.Path.Combine(p.Path,"commands.json"));
        var oldRoles=commands.Current.Commands["play"].AllowedRoles?.ToArray();
        backup.Commands.Commands["play"].AllowedRoles=["vip"]; backup.Queue.YouTubeEnabled=false;
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailImport BEFORE INSERT ON PlatformConfigs BEGIN SELECT RAISE(ABORT, 'injected import failure'); END");
        var result=Assert.IsType<ObjectResult>(await controller.Import(backup)); Assert.Equal(500,result.StatusCode);
        Assert.Equal(previous,await File.ReadAllTextAsync(System.IO.Path.Combine(p.Path,"commands.json")));
        Assert.Equal("{\"Preserve\":\"original\"}",await File.ReadAllTextAsync(config));
        Assert.Equal(oldRoles,commands.Current.Commands["play"].AllowedRoles);
        Assert.True(sp.GetRequiredService<QueueSettingsService>().YouTubeEnabled);
        Assert.Empty(await db.PlatformConfigs.ToListAsync());
    }
    [Fact] public async Task RuntimeFailureAfterImportKeepsSavedPreferencesAndReturnsWarning()
    {
        await using var p=new Profile(); using var scope=p.App.Services.CreateScope(); var sp=scope.ServiceProvider;
        var controller=ActivatorUtilities.CreateInstance<SettingsBackupController>(sp);
        controller.ControllerContext=new(){HttpContext=new DefaultHttpContext{RequestServices=sp}};
        var backup=Assert.IsType<SettingsBackup>(Assert.IsType<OkObjectResult>(await controller.Export()).Value);
        backup=JsonSerializer.Deserialize<SettingsBackup>(JsonSerializer.Serialize(backup))!;
        backup.Queue.YouTubeEnabled=false;
        Action<string> failTheme=_=>throw new IOException("injected runtime failure");
        AppEvents.OnThemeChanged+=failTheme;
        try
        {
            var result=Assert.IsType<OkObjectResult>(await controller.Import(backup));
            var body=JsonSerializer.SerializeToElement(result.Value);
            Assert.True(body.GetProperty("warnings").GetArrayLength()>0);
            Assert.False(sp.GetRequiredService<QueueSettingsService>().YouTubeEnabled);
            Assert.Contains("false",await File.ReadAllTextAsync(System.IO.Path.Combine(p.Path,"appsettings.user.json")));
            Assert.NotEmpty(await sp.GetRequiredService<MusicBotDbContext>().PlatformConfigs.ToListAsync());
        }
        finally { AppEvents.OnThemeChanged-=failTheme; }
    }
    [Fact] public async Task LibrarySummaryPreservesCountsAndDistinctCoverOrder()
    {
        await using var p=new Profile(); var library=p.App.Services.GetRequiredService<PlaylistLibraryService>();
        var empty=await library.CreateAsync("Empty"); var full=await library.CreateAsync("Full");
        for(var i=0;i<7;i++) await library.AddSongAsync(full.Id,new(){SpotifyUri="spotify:track:"+i.ToString().PadLeft(22,'0'),DurationMs=10000,CoverUrl=i<2?"https://cover/same":i==2?"":"https://cover/"+i});
        var summaries=await library.GetSummariesAsync(); var row=summaries.Single(s=>s.Playlist.Id==full.Id);
        Assert.Equal(7,row.SongCount); Assert.Equal(70000,row.TotalDurationMs); Assert.Equal(4,row.CoverUrls.Count); Assert.Equal(4,row.CoverUrls.Distinct().Count());
        Assert.Equal("https://cover/same",row.CoverUrls[0]); Assert.Contains("/api/artwork/",row.CoverUrls[1]);
        var none=summaries.Single(s=>s.Playlist.Id==empty.Id); Assert.Equal(0,none.SongCount); Assert.Empty(none.CoverUrls);
    }
    [Fact] public void InstallerWaitsForExitAndQuotesPathsLiterally()
    {
        var script=UpdateInstallerLauncher.WaitScript("C:\\Temp\\Sert'Ay\\Setup.exe",42);
        Assert.True(script.IndexOf("Wait-Process",StringComparison.Ordinal)<script.IndexOf("Start-Process",StringComparison.Ordinal));
        Assert.Contains("Sert''Ay",script); Assert.DoesNotContain("Stop-Process",script);
        Assert.Throws<ArgumentException>(()=>UpdateInstallerLauncher.WaitScript("relative.exe",42));
    }
}
