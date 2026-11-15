using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Services.Player;
using MusicBot.Tests.TestHelpers;
using Xunit;

namespace MusicBot.Tests;

public class HybridPlayerTests
{
    [Fact]
    public async Task LateYouTubePlayingOrEndedReportsCannotUndoPauseOrAdvanceQueue()
    {
        using var player=new HybridPlayerService(NullLogger<LocalPlayerService>.Instance,FakeHub.Instance);var ended=0;
        player.OnTrackEnded+=(_,_)=>ended++;
        await player.PlayAsync("youtube:aaaaaaaaaaa");var beforePause=player.Snapshot();await player.PauseAsync();
        Assert.False(player.Report(beforePause with{IsPlaying=true},false));
        var paused=player.Snapshot();Assert.True(player.Report(paused with{IsPlaying=true},true));
        Assert.False(player.IsPlaying);Assert.Equal(0,ended);
        await player.ResumeAsync();Assert.False(player.Report(paused,true));
        Assert.True(player.Report(player.Snapshot() with{IsPlaying=true},false));Assert.True(player.IsPlaying);
    }

    [Fact]
    public void FailedAnnouncementCanRetry_AndRepeatedSongsHaveDistinctSessions()
    {
        var gate = new MusicBot.Services.TrackAnnouncementGate();
        Assert.True(gate.TryAccept("song|session1", true));
        Assert.False(gate.TryAccept("song|session1", true));
        gate.Release("song|session1");
        Assert.True(gate.TryAccept("song|session1", true));
        Assert.True(gate.TryAccept("song|session2", true));
        gate.Release("song|session1");
        Assert.False(gate.TryAccept("song|session2", true));
    }
    [Fact]
    public void FastYouTubeSearchParsesMetadataAndSkipsLiveEntries()
    {
        var html = "<script>var ytInitialData = {\"contents\":[{\"videoRenderer\":{\"videoId\":\"aaaaaaaaaaa\",\"title\":{\"runs\":[{\"text\":\"Music\"}]},\"ownerText\":{\"simpleText\":\"Artist\"},\"lengthText\":{\"simpleText\":\"3:20\"}}},{\"videoRenderer\":{\"videoId\":\"bbbbbbbbbbb\",\"title\":{\"simpleText\":\"Live\"}}}]};</script>";
        var track = Assert.Single(MusicBot.Services.Downloader.YouTubeSearchPage.Parse(html, 5));
        Assert.Equal("youtube:aaaaaaaaaaa", track.SpotifyUri);
        Assert.Equal(200000, track.DurationMs);
        Assert.Contains("i.ytimg.com", track.CoverUrl);
        Assert.Empty(MusicBot.Services.Downloader.YouTubeSearchPage.Parse("captcha page", 5));
    }
    [Fact]
    public async Task LateYouTubeReportsCannotChangeTheNextTrack_AndCompletionIsIdempotent()
    {
        using var player = new HybridPlayerService(NullLogger<LocalPlayerService>.Instance, FakeHub.Instance);
        await player.PlayAsync("youtube:aaaaaaaaaaa");
        var first = player.Snapshot();
        Assert.True(player.Report(first with { IsPlaying = true, PositionMs = 1000, DurationMs = 90000 }, false));
        await player.PlayAsync("youtube:bbbbbbbbbbb");
        Assert.False(player.Report(first with { PositionMs = 80000 }, true));
        Assert.Equal(0, player.PositionMs);
        var ended = 0; player.OnTrackEnded += (_, _) => ended++;
        var second = player.Snapshot();
        Assert.True(player.Report(second with { PositionMs = 90000 }, true));
        Assert.False(player.Report(second, true));
        Assert.Equal(1, ended);
    }
    [Fact]
    public async Task PausedYouTubeStateSurvivesRestore_AndErrorsDoNotLoopThePool()
    {
        using var player = new HybridPlayerService(NullLogger<LocalPlayerService>.Instance, FakeHub.Instance);
        await player.PlayAsync("youtube:aaaaaaaaaaa");
        await player.PauseAsync();
        Assert.False(player.Snapshot().RequestedPlaying);
        var ended = 0; player.OnTrackEnded += (_, _) => ended++;
        player.Report(player.Snapshot(), true, 150);
        Assert.Equal(0, ended);
        await player.StopAsync();
        Assert.Null(player.Snapshot().Uri);
    }
    [Theory]
    [InlineData("v1.2.1", "1.2.0", true)]
    [InlineData("v1.2.0", "1.2.0", false)]
    [InlineData("v1.1.9", "1.2.0", false)]
    [InlineData("invalid", "1.2.0", false)]
    public void UpdateComparisonRejectsOldAndInvalidReleases(string next, string current, bool expected)
        => Assert.Equal(expected, MusicBot.Controllers.UpdatesController.IsNewer(next, current));
}
