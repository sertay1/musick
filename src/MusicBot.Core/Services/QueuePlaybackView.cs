using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;

namespace MusicBot.Core.Services;

public static class QueuePlaybackView
{
    public static QueueItem? GetPlayingItem(this IQueueService queue)
    {
        var state = queue.GetNowPlaying();
        if (state.SpotifyTrack is { } spotify)
        {
            if (state.Item?.Song.SpotifyUri == spotify.SpotifyUri) return state.Item;
            return new QueueItem { Song = spotify, Platform = "spotify", RequestedBy = "", IsPlaylistItem = true };
        }
        return state.Item;
    }

    public static int GetUpcomingPosition(this IQueueService queue, int index) =>
        index + 1 + (queue.GetPlayingItem() != null ? 1 : 0);
}
