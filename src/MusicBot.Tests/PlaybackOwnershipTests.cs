using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Services;
using MusicBot.Services.Downloader;
using System.Text.Json;
using Xunit;

namespace MusicBot.Tests;

public class PlaybackOwnershipTests
{
    [Fact]
    public async Task DisposedPlayer_LateAudioErrorDoesNotAdvanceTheQueue()
    {
        using var player = new MusicBot.Services.Player.LocalPlayerService(Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicBot.Services.Player.LocalPlayerService>.Instance);
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.OnTrackEnded += (_, _) => ended.TrySetResult(true);
        var previous = player.PlaybackVersion;
        await player.StopAsync();
        Assert.True(player.PlaybackVersion > previous);
        typeof(MusicBot.Services.Player.LocalPlayerService).GetMethod("HandlePlaybackStopped", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(player, new object[] { new object(), new NAudio.Wave.StoppedEventArgs(new InvalidOperationException("Late audio error")) });
        var wait = Task.Delay(100);
        Assert.Same(wait, await Task.WhenAny(ended.Task, wait));
    }

    [Fact]
    public void LateLocalPlayerEvents_CannotOverwriteNewSourceOrQueueItem()
    {
        var item = new QueueItem { Song = new Song { LocalFilePath = "current.mp3" } };
        var state = new LocalPlayerState { FilePath = "current.mp3", PositionMs = 24000, DurationMs = 90000, IsPlaying = true };
        Assert.True(PlaybackOwnership.AcceptLocalState(item, item.Id, state, false));
        Assert.False(PlaybackOwnership.AcceptLocalState(item, item.Id, state, true));
        Assert.False(PlaybackOwnership.AcceptLocalState(item, "previous-item", state, false));
        state.FilePath = "previous.mp3";
        Assert.False(PlaybackOwnership.AcceptLocalState(item, item.Id, state, false));
        Assert.False(PlaybackOwnership.AcceptLocalState(null, null, state, false));
    }

    [Fact]
    public void VolumeEcho_IsIgnoredWhileLocalChangeSettles_AndForInflightOldObservations()
    {
        var guard = new VolumeSyncGuard();
        var time = DateTimeOffset.UtcNow;
        Assert.True(guard.AcceptRemote(time));
        guard.LocalChange(time);
        Assert.False(guard.AcceptRemote(time.AddSeconds(-1)));
        Assert.False(guard.AcceptRemote(time.AddSeconds(3)));
        Assert.True(guard.AcceptRemote(time.AddSeconds(5)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"entries\":null}")]
    [InlineData("{\"entries\":[null,{},42]}")]
    public void YouTubeLibrary_NullOrUnavailableEntriesAreSafe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(YtDlpDownloaderService.ParseLibraryPlaylists(doc.RootElement));
    }

    [Fact]
    public void YouTubeLibrary_ReadsValidPlaylistsAlongsideNullEntries()
    {
        using var doc = JsonDocument.Parse("{\"entries\":[null,{\"id\":\"PLabcdefghijklmnop\",\"title\":\"Music\"},null]}");
        var list = YtDlpDownloaderService.ParseLibraryPlaylists(doc.RootElement);
        Assert.Equal("Music", Assert.Single(list).Title);
    }
}
