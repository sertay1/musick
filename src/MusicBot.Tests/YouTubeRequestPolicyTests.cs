using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MusicBot.Core.Models;
using MusicBot.Services;
using Xunit;

namespace MusicBot.Tests;

public class YouTubeRequestPolicyTests
{
    [Fact]
    public async Task DisabledYouTubeRejectsMusicAndVideoLinksBeforeAnySearchOrQueueChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "musick-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var builder = WebHost.CreateBuilder([]);
            builder.Configuration["DataDirectory"] = path;
            builder.Configuration["ConnectionStrings:DefaultConnection"] = "Data Source=" + Path.Combine(path, "test.db");
            builder.Configuration["Queue:YouTubeEnabled"] = "false";
            // No host/background services are started; no real account or chat is connected.
            await using var app = WebHost.Configure(builder);
            var settings = app.Services.GetRequiredService<QueueSettingsService>();
            Assert.False(settings.YouTubeEnabled);
            var services = app.Services.GetRequiredService<UserContextManager>().GetOrCreate(LocalUser.Id);
            var router = app.Services.GetRequiredService<CommandRouterService>();
            foreach (var link in new[] {
                "https://music.youtube.com/watch?v=dQw4w9WgXcQ",
                "https://music.youtube.com/watch?v=dQw4w9WgXcQ&list=RDAMVMdQw4w9WgXcQ",
                "https://www.music.youtube.com/watch?v=dQw4w9WgXcQ",
                "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                "https://m.youtube.com/watch?v=dQw4w9WgXcQ",
                "https://youtu.be/dQw4w9WgXcQ?si=test",
                "https://music.youtube.com/playlist?list=PLtest",
                "https://www.youtube.com/shorts/dQw4w9WgXcQ" })
            {
                var result = await router.HandleAsync(new BotCommand { Type = "play", Query = link, RequestedBy = "test", Platform = "kick" }, services);
                Assert.False(result.Success);
                Assert.Contains("YouTube istekleri ayarlardan kapatılmış", result.Message);
                Assert.Null(services.Queue.GetCurrentItem());
                Assert.Empty(services.Queue.GetUpcoming());
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }
}
