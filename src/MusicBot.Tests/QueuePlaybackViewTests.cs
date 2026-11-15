using System.Reflection;
using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Services;
using Xunit;

namespace MusicBot.Tests;

public class QueuePlaybackViewTests
{
    [Fact]
    public void NativeSpotifyPlayback_IsCurrentWithoutAnExplicitQueueEntry()
    {
        var queue = new QueueService();
        queue.UpdateProgress(30000, true, new Song { SpotifyUri = "spotify:track:native", Title = "Native", DurationMs = 180000 });
        queue.AddSong(new Song { SpotifyUri = "requested" }, "viewer", "kick");
        Assert.Null(queue.GetCurrentItem());
        Assert.Equal("Native", queue.GetPlayingItem()!.Song.Title);
        Assert.Equal("spotify", queue.GetPlayingItem()!.Platform);
        Assert.Equal(2, queue.GetUpcomingPosition(0));
        Assert.Equal(3, queue.GetUpcomingPosition(1));
    }

    [Fact]
    public void NoPlayback_FirstUpcomingSongIsOne()
    {
        Assert.Equal(1, new QueueService().GetUpcomingPosition(0));
    }

    [Fact]
    public void MatchingSpotifyState_PreservesRequester()
    {
        var queue = new QueueService();
        queue.AddSong(new Song { SpotifyUri = "requested" }, "viewer", "kick");
        queue.Advance();
        queue.UpdateProgress(1000, true, new Song { SpotifyUri = "requested" });
        Assert.Equal("viewer", queue.GetPlayingItem()!.RequestedBy);
        queue.UpdateProgress(1000, true, new Song { SpotifyUri = "native" });
        Assert.Equal("", queue.GetPlayingItem()!.RequestedBy);
    }

    [Fact]
    public void PositionCommand_CountsNativeSpotifySongAndItsRemainingTime()
    {
        var queue = new QueueService();
        queue.UpdateProgress(60000, true, new Song { SpotifyUri = "native", DurationMs = 180000 });
        queue.AddSong(new Song { SpotifyUri = "requested", Title = "Requested" }, "viewer", "kick");
        var services = new UserServices(Guid.NewGuid(), queue, null!, null!);
        var method = typeof(CommandRouterService).GetMethod("HandlePos", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = (CommandResult)method.Invoke(null, new object[] {
            new BotCommand { RequestedBy = "viewer" }, services,
            new CommandDefinition { Templates = new() { ["success"] = "{position}|{wait_time}" } }
        })!;
        Assert.True(result.Success);
        Assert.Equal("2|2 dk", result.Message);
    }
}
