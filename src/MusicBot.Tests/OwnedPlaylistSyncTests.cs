using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MusicBot.Controllers;
using MusicBot.Core.Models;
using MusicBot.Services;
using MusicBot.Services.Spotify;
using Xunit;
namespace MusicBot.Tests;
public class OwnedPlaylistSyncTests
{
    private sealed class Http : HttpMessageHandler,IHttpClientFactory
    {
        public HttpClient CreateClient(string name)=>new(this,false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;object body;
            if(path=="/v1/me")body=new{id="me"};
            else if(path=="/v1/me/playlists")body=new{items=new[]{new{id="owned",name="Owned",owner=new{id="me"},tracks=new{total=90}},new{id="denied",name="Denied",owner=new{id="me"},tracks=new{total=90}},new{id="foreign",name="Followed",owner=new{id="other"},tracks=new{total=90}}},next=(string?)null};
            else if(path.Contains("/denied/"))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            else if(path.Contains("/owned/"))
            {
                var offset=int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["offset"]!);
                body=new{items=Enumerable.Range(offset,Math.Min(50,90-offset)).Select(i=>new{item=new{type="track",uri=$"spotify:track:{i:D22}",name=$"Track {i}",album=new{images=new[]{new{url=$"https://images.test/{i}.jpg"}}}}}),next=offset==0?"next":null};
            }
            else throw new Exception("Unexpected remote request: "+path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(body))});
        }
    }
    [Fact]
    public async Task SyncImportsAllOwnedTracksWithoutCreatingFailedPlaceholdersOrDeletingSavedContent()
    {
        var path=Path.Combine(Path.GetTempPath(),"musick-owned-sync-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        try
        {
            using var http=new Http();var builder=WebHost.CreateBuilder([]);builder.Configuration["DataDirectory"]=path;builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");builder.Services.AddSingleton<IHttpClientFactory>(http);
            await using var app=WebHost.Configure(builder);var library=app.Services.GetRequiredService<PlaylistLibraryService>();
            var empty=await library.CreateAsync("Failed foreign");await library.SetSyncStatusAsync(empty.Id,"https://open.spotify.com/playlist/foreign-empty","old error");
            var saved=await library.CreateAsync("Saved foreign");await library.SetSyncStatusAsync(saved.Id,"https://open.spotify.com/playlist/foreign-saved","old error");await library.BulkAddAsync(saved.Id,new[]{new Song{SpotifyUri="youtube:aaaaaaaaaaa",Title="Saved"}});
            var spotify=app.Services.GetRequiredService<UserContextManager>().GetOrCreate(LocalUser.Id).Spotify;var flags=BindingFlags.Instance|BindingFlags.NonPublic;typeof(SpotifyService).GetField("_accessToken",flags)!.SetValue(spotify,"test");typeof(SpotifyService).GetField("_expiresAt",flags)!.SetValue(spotify,DateTimeOffset.UtcNow.AddHours(1));
            var controller=ActivatorUtilities.CreateInstance<PlaylistController>(app.Services);controller.ControllerContext=new(){HttpContext=new DefaultHttpContext{RequestServices=app.Services}};
            Assert.IsType<OkObjectResult>(await controller.SyncSpotify());
            var lists=await library.GetAllAsync();var owned=Assert.Single(lists,p=>p.SourceUrl=="https://open.spotify.com/playlist/owned");
            var songs=await library.GetSongsAsync(owned.Id);Assert.Equal(90,songs.Count);Assert.Equal(90,songs.Select(s=>s.CoverUrl).Distinct().Count());
            Assert.DoesNotContain(lists,p=>p.Name=="Denied" || p.Name=="Followed" || p.Id==empty.Id);Assert.Single(await library.GetSongsAsync(saved.Id));
            var user=app.Services.GetRequiredService<UserContextManager>().GetOrCreate(LocalUser.Id);
            user.Queue.PlayNow(new Song{SpotifyUri="spotify:track:current",Title="Playing"},"host","web");
            var playingId=user.Queue.GetCurrentItem()!.Id;
            user.Queue.AddSong(new Song{SpotifyUri="spotify:track:viewer"},"viewer","kick");
            user.Queue.AddSong(new Song{SpotifyUri="spotify:track:import"},"host","web",isPlaylistItem:true);
            user.Queue.SetBackgroundPlaylist(new[]{new Song{SpotifyUri="spotify:track:background"}},"Background");
            await library.SetActiveAsync(owned.Id);
            var queueController=ActivatorUtilities.CreateInstance<QueueController>(app.Services);
            Assert.IsType<NoContentResult>(await queueController.ClearPendingQueue(library));
            Assert.Empty(user.Queue.GetUpcoming());Assert.Empty(user.Queue.GetBackgroundPlaylist().Songs);
            Assert.Equal(playingId,user.Queue.GetCurrentItem()!.Id);Assert.True(user.Queue.GetNowPlaying().IsPlaying);
            Assert.Equal(90,(await library.GetSongsAsync(owned.Id)).Count);Assert.DoesNotContain(await library.GetAllAsync(),p=>p.IsActive);
            using (var scope=app.Services.CreateScope())
            {
                var db=scope.ServiceProvider.GetRequiredService<MusicBot.Data.MusicBotDbContext>();
                db.PlatformConfigs.Add(new MusicBot.Data.PlatformConfig{UserId=LocalUser.Id,Platform="kick",ConfigJson="{\"channel\":\"@SertAy\"}"});await db.SaveChangesAsync();
            }
            queueController.ControllerContext=new(){HttpContext=new DefaultHttpContext{RequestServices=app.Services}};
            Assert.IsType<OkObjectResult>(await queueController.ImportPlaylist(new(){Url="https://open.spotify.com/playlist/owned"}));
            Assert.Equal(90,user.Queue.GetUpcoming().Count);Assert.All(user.Queue.GetUpcoming(),item=>Assert.Equal("SertAy",item.RequestedBy));
            Assert.Equal(playingId,user.Queue.GetCurrentItem()!.Id);
            // The desktop event is replaceable here; no application is launched.
            var launches=0;Action launch=()=>launches++;AppEvents.OnOpenSpotifyRequested+=launch;
            try {Assert.IsType<NoContentResult>(ActivatorUtilities.CreateInstance<AppController>(app.Services).OpenSpotify());Assert.Equal(1,launches);}
            finally {AppEvents.OnOpenSpotifyRequested-=launch;}
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(path,true);}
    }
}
