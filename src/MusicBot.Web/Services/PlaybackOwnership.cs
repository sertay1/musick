using MusicBot.Core.Models;
using MusicBot.Core.Interfaces;

namespace MusicBot.Services;

public static class PlaybackOwnership
{
    public static bool AcceptLocalState(QueueItem? current, string? ownerId, LocalPlayerState state, bool usesSpotify, string? activeFilePath = null) =>
        !usesSpotify && current != null && current.Id == ownerId && !string.IsNullOrEmpty(state.FilePath)
        && state.FilePath == (activeFilePath ?? current.Song.LocalFilePath);
}
