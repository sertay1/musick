using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Services;
using MusicBot.Services.Player;
using MusicBot.Tests.TestHelpers;
using Xunit;

namespace MusicBot.Tests;
public class QueueOwnershipRegressionTests
{
    [Fact]
    public void EmptyAndWhitespaceRepliesStayDisabled_OnlyMissingKeysUseDefaults()
    {
        var cmd = new CommandDefinition(); cmd.Templates["success"] = ""; cmd.Templates["pending"] = "  ";
        Assert.Equal("", cmd.GetTemplate("success", "must not send"));
        Assert.Equal("  ", cmd.GetTemplate("pending", "must not send"));
        Assert.Equal("default", cmd.GetTemplate("missing", "default"));
    }
    [Fact]
    public void AutoPoolReplacementDoesNotRequeueOldOrDuplicateTarget()
    {
        var queue = new QueueService(); var first = new Song { SpotifyUri = "first" }; var second = new Song { SpotifyUri = "second" };
        queue.PlayNow(first, "Pool", "autoqueue", true); queue.AddSong(second, "viewer", "kick");
        queue.PlayNow(second, "Pool", "autoqueue", true);
        Assert.Empty(queue.GetUpcoming()); Assert.Equal("second", queue.GetCurrentItem()!.Song.SpotifyUri);
    }
    [Fact]
    public async Task YouTubeBalanceChangesOutputWithoutMovingMasterVolume_AndSurvivesTrackChanges()
    {
        var settings = new QueueSettingsService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Queue:YouTubeVolumePercent"] = "40" }).Build(), FakeHub.Instance);
        using var player = new HybridPlayerService(NullLogger<LocalPlayerService>.Instance, FakeHub.Instance, settings);
        player.SetVolume(0.5f); await player.PlayAsync("youtube:aaaaaaaaaaa");
        Assert.Equal(0.5f, player.Volume); Assert.Equal(0.2f, player.Snapshot().Volume);
        await player.PlayAsync("youtube:bbbbbbbbbbb"); Assert.Equal(0.2f, player.Snapshot().Volume);
        settings.SetYouTubeVolume(80); player.SetVolume(player.Volume);
        Assert.Equal(0.5f, player.Volume); Assert.Equal(0.4f, player.Snapshot().Volume);
    }

    [Fact]
    public void RestoredLegacyDuplicatesAreCollapsed_AndExternalCurrentCannotBeQueuedAgain()
    {
        var queue = new QueueService(); var song = new Song { SpotifyUri = "same" };
        queue.Seed(new QueueItem { Song = song }, new[] { new QueueItem { Song = song }, new QueueItem { Song = new Song { SpotifyUri = "other" } } });
        Assert.Single(queue.GetUpcoming()); Assert.Equal("other", queue.GetUpcoming()[0].Song.SpotifyUri);
        queue = new QueueService(); queue.UpdateProgress(1000, true, song);
        Assert.Throws<InvalidOperationException>(() => queue.AddSong(song, "viewer", "kick"));
    }
}
