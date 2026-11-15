using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Services.Commands;
using Xunit;

namespace MusicBot.Tests;

public class RoleMatrixRegressionTests
{
    [Fact]
    public void FailedAdvance_RestoresRequestAndPreservesNewArrivals()
    {
        var queue = new QueueService();
        queue.AddSong(new Song { SpotifyUri = "request" }, "viewer", "kick");
        var old = queue.GetNowPlaying();
        var requested = queue.Advance()!;
        queue.AddSong(new Song { SpotifyUri = "new-arrival" }, "other", "kick");
        Assert.True(queue.RestoreFailedAdvance(null, requested, old));
        Assert.Null(queue.GetCurrentItem());
        Assert.Equal(new[] { "request", "new-arrival" }, queue.GetUpcoming().Select(i => i.Song.SpotifyUri));
    }

    [Fact]
    public void Advance_ClearsPreviousSpotifyTrackFromDisplay()
    {
        var queue = new QueueService();
        queue.UpdateProgress(1000, true, new Song { SpotifyUri = "old" });
        queue.AddSong(new Song { SpotifyUri = "request" }, "viewer", "kick");
        queue.Advance();
        Assert.Null(queue.GetNowPlaying().SpotifyTrack);
        Assert.Equal("request", queue.GetNowPlaying().Item!.Song.SpotifyUri);
    }
    [Fact]
    public void ExplicitPermissions_DoNotGrantHigherRolesImplicitly()
    {
        var roles = new List<string> { "subscriber" };
        Assert.True(CommandConfigService.IsCommandAllowed("everyone", roles, new[] { "subscriber" }, "viewer", "owner"));
        Assert.False(CommandConfigService.IsCommandAllowed("everyone", roles, new[] { "moderator" }, "mod", "owner"));
        Assert.False(CommandConfigService.IsCommandAllowed("everyone", roles, new[] { "broadcaster" }, "owner", "owner"));
        Assert.False(CommandConfigService.IsCommandAllowed("everyone", [], [], "viewer", "owner"));
    }

    [Fact]
    public void MissingExplicitPermissions_PreservesLegacyPermissions()
    {
        Assert.True(CommandConfigService.IsCommandAllowed("subscriber", null, new[] { "moderator" }, "mod", "owner"));
        Assert.False(CommandConfigService.IsCommandAllowed("moderator", null, [], "viewer", "owner"));
    }

    [Fact]
    public void BackgroundPlaylist_ShowsAllOneHundredSongs()
    {
        var queue = new QueueService();
        queue.SetBackgroundPlaylist(Enumerable.Range(0, 100).Select(i => new Song { SpotifyUri = $"youtube:{i}" }));
        Assert.Equal(100, queue.GetState().Upcoming.Count);
        Assert.Empty(queue.GetUpcoming()); // preview entries must not become explicit requests
    }

    [Fact]
    public void RoleLimit_OverridesDefaultUserLimitWithoutBypassingTotalLimit()
    {
        var queue = new QueueService(maxSize: 3, maxPerUser: 1);
        queue.AddSong(new Song { SpotifyUri = "a" }, "viewer", "kick", maxPerUserOverride: 2);
        queue.AddSong(new Song { SpotifyUri = "b" }, "viewer", "kick", maxPerUserOverride: 2);
        Assert.Throws<InvalidOperationException>(() => queue.AddSong(new Song { SpotifyUri = "c" }, "viewer", "kick", maxPerUserOverride: 2));
        queue.AddSong(new Song { SpotifyUri = "d" }, "other", "kick", maxPerUserOverride: 2);
        Assert.Throws<InvalidOperationException>(() => queue.AddSong(new Song { SpotifyUri = "e" }, "other", "kick", maxPerUserOverride: 2));
    }
}
