using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Services;
using MusicBot.Services.Platforms;
using MusicBot.Tests.TestHelpers;
using Xunit;
namespace MusicBot.Tests;
public class PlaybackInteractionTests
{
    [Theory]
    [InlineData("spotify:track:1234567890123456789012", false)]
    [InlineData("youtube:aaaaaaaaaaa", false)]
    [InlineData("spotify:track:1234567890123456789012", true)]
    public async Task ConfirmedTracksStartVoting_ForBothSources_ButOnlyViewerRequestsStartAfk(string uri, bool automatic)
    {
        var path = Path.Combine(Path.GetTempPath(), "musick-interaction-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var builder = WebHost.CreateBuilder([]); builder.Configuration["DataDirectory"] = path;
            builder.Configuration["ConnectionStrings:DefaultConnection"] = "Data Source=" + Path.Combine(path, "test.db");
            builder.Configuration["Queue:VotingEnabled"] = "true"; builder.Configuration["Queue:PresenceCheckEnabled"] = "true";
            await using var app = WebHost.Configure(builder);
            var user = app.Services.GetRequiredService<UserContextManager>().GetOrCreate(LocalUser.Id);
            var item = user.Queue.PlayNow(new Song { SpotifyUri = uri, Title = "Song" }, automatic ? "Otomatik Kuyruk" : "Viewer", automatic ? "autoqueue" : "kick", automatic);
            user.Queue.UpdateProgress(1000, true);
            var sync = app.Services.GetRequiredService<PlaybackSyncService>();
            await (Task)typeof(PlaybackSyncService).GetMethod("ObserveHistoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sync, [item, true])!;
            var vote = app.Services.GetRequiredService<KickVoteService>(); var presence = app.Services.GetRequiredService<PresenceCheckService>();
            Assert.True(vote.IsActive);
            Assert.Equal(!automatic, presence.ConfirmPresence(automatic ? "Otomatik Kuyruk" : "Viewer"));
            presence.CancelCheck(); vote.CancelVote();
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }
    [Fact]
    public async Task OldVoteResultCannotSkipTheNextTrack_AndTheCurrentVoteCanSkip()
    {
        var settings = new QueueSettingsService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Queue:VotingEnabled"] = "true" }).Build(), FakeHub.Instance);
        var vote = new KickVoteService(FakeHub.Instance, NullLogger<KickVoteService>.Instance, settings);
        var skipped = 0; vote.SkipCurrentSong = () => { skipped++; return Task.CompletedTask; };
        await vote.StartVoteAsync(new Song { Title = "Old" }); await vote.VoteAsync("one", true);
        var generation = typeof(KickVoteService).GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var end = typeof(KickVoteService).GetMethod("EndVoteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = (Task)end.Invoke(vote, [generation.GetValue(vote)!])!;
        await vote.StartVoteAsync(new Song { Title = "New" });
        await pending; Assert.Equal(0, skipped); Assert.True(vote.IsActive);
        await vote.VoteAsync("two", true); await (Task)end.Invoke(vote, [generation.GetValue(vote)!])!;
        Assert.Equal(1, skipped); vote.CancelVote();
    }
    [Fact]
    public async Task AfkHonorsWarningConfirmation_AndSkipsUnconfirmedViewer()
    {
        var settings = new QueueSettingsService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Queue:PresenceCheckEnabled"] = "true", ["Queue:PresenceCheckConfirmSeconds"] = "0" }).Build(), FakeHub.Instance);
        var presence = new PresenceCheckService(NullLogger<PresenceCheckService>.Instance, new ChatResponseService(NullLogger<ChatResponseService>.Instance), settings);
        var skipped = 0; presence.SkipCurrentSong = () => { skipped++; return Task.CompletedTask; };
        presence.IssueWarningForNext("Viewer"); Assert.True(presence.ConfirmPresence("Viewer"));
        presence.PrepareForTrack("Viewer"); await presence.StartSongCheckAsync("Viewer"); Assert.Equal(0, skipped);
        presence.PrepareForTrack("Other"); await presence.StartSongCheckAsync("Other"); Assert.Equal(1, skipped);
        presence.CancelCheck();
    }
    [Fact]
    public async Task UpcomingViewerConfirmationCannotConfirmTheCurrentViewer()
    {
        var settings = new QueueSettingsService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Queue:PresenceCheckEnabled"] = "true", ["Queue:PresenceCheckConfirmSeconds"] = "1" }).Build(), FakeHub.Instance);
        var presence = new PresenceCheckService(NullLogger<PresenceCheckService>.Instance, new ChatResponseService(NullLogger<ChatResponseService>.Instance), settings);
        var skipped = 0; presence.SkipCurrentSong = () => { skipped++; return Task.CompletedTask; };
        var current = presence.StartSongCheckAsync("Current");
        presence.IssueWarningForNext("Upcoming"); Assert.True(presence.ConfirmPresence("Upcoming"));
        await current; Assert.Equal(1, skipped);
        presence.PrepareForTrack("Upcoming"); await presence.StartSongCheckAsync("Upcoming"); Assert.Equal(1, skipped);
        presence.CancelCheck();
    }
}
