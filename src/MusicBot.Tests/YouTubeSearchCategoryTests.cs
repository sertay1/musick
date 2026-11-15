using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicBot.Core.Interfaces;
using MusicBot.Services;
using MusicBot.Services.Downloader;
using MusicBot.Services.Platforms;
using Xunit;
namespace MusicBot.Tests;
public class YouTubeSearchCategoryTests
{
    private sealed class Http : HttpMessageHandler,IHttpClientFactory
    {
        public int Metadata;
        public HttpClient CreateClient(string name)=>new(this,false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string body;
            if(request.Method==HttpMethod.Get)
                body="var ytInitialData = "+JsonSerializer.Serialize(new{contents=new[]{"aaaaaaaaaaa","bbbbbbbbbbb","ccccccccccc"}.Select(id=>new{videoRenderer=new{videoId=id,title=new{simpleText=id},ownerText=new{simpleText="Artist"},lengthText=new{simpleText="3:00"}}})})+";";
            else
            {
                Metadata++;using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var id=json.RootElement.GetProperty("videoId").GetString();
                body=JsonSerializer.Serialize(new{videoDetails=new{videoId=id,title=id,author="Artist",lengthSeconds="180"},microformat=new{playerMicroformatRenderer=new{category=id=="bbbbbbbbbbb"?"People & Blogs":"Music"}}});
            }
            return new(HttpStatusCode.OK){Content=new StringContent(body)};
        }
    }
    [Fact]
    public async Task SearchFiltersEveryResultUsingCategoryAndRetainsTrackArtwork()
    {
        var path=Path.Combine(Path.GetTempPath(),"musick-category-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        try
        {
            var builder=WebHost.CreateBuilder([]);builder.Configuration["DataDirectory"]=path;builder.Configuration["ConnectionStrings:DefaultConnection"]="Data Source="+Path.Combine(path,"test.db");
            await using var app=WebHost.Configure(builder);using var http=new Http();
            var service=new YtDlpDownloaderService(app.Services.GetRequiredService<ILocalLibraryService>(),Options.Create(new MusicLibrarySettings{LibraryPath=Path.Combine(path,"audio")}),app.Services.GetRequiredService<QueueSettingsService>(),app.Services.GetRequiredService<YouTubeAuthService>(),NullLogger<YtDlpDownloaderService>.Instance,httpFactory:http);
            var result=await service.SearchAsync("test",5);
            Assert.Equal(new[]{"youtube:aaaaaaaaaaa","youtube:ccccccccccc"},result.Select(s=>s.SpotifyUri));Assert.All(result,s=>Assert.Contains(s.SpotifyUri[8..],s.CoverUrl));Assert.Equal(3,http.Metadata);
            result=await service.SearchAsync("test",5);Assert.Equal(2,result.Count);Assert.Equal(4,http.Metadata); // Accepted metadata is cached; rejected videos are rechecked.
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();Assert.Empty(await service.SearchAsync("test",5,cancelled.Token));
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(path,true);}
    }
}
