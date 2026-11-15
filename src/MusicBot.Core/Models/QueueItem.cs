namespace MusicBot.Core.Models;

public class QueueItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public Song Song { get; set; } = new();
    public string RequestedBy { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public long AddedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// <summary>True when this item was auto-promoted from the background playlist (not user-requested).</summary>
    public bool IsPlaylistItem { get; set; }
    /// <summary>The next viewer request is committed to the native Spotify queue.</summary>
    public bool PreparedForSpotify { get; set; }
    /// <summary>Set when download or validation failed.</summary>
    public string? DownloadError { get; set; }
}
