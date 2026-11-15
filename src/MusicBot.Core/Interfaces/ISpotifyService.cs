using MusicBot.Core.Models;

namespace MusicBot.Core.Interfaces;

public interface ISpotifyService
{
    bool IsAuthenticated { get; }
    string GetAuthUrl();
    Task HandleCallbackAsync(string code);
    Task DisconnectAsync();
    Task<string> GetAccessTokenAsync();
    Task<List<Song>> SearchAsync(string query, int limit = 5);
    Task PlayAsync(string spotifyUri, string? deviceId = null);
    async Task<bool> PlayIfCurrentAsync(string spotifyUri, Func<bool> isCurrent)
    {
        if (!isCurrent()) return false;
        await PlayAsync(spotifyUri);
        return true;
    }
    async Task PauseIfCurrentAsync(Func<bool> isCurrent) { if (isCurrent()) await PauseAsync(); }
    Task SetManagedQueueAsync(bool enabled, Func<bool>? isCurrent = null) => Task.CompletedTask;
    Task WarmTrackAsync(string uri, Func<bool> current) => Task.CompletedTask;
    Task<bool> PrepareNextTrackAsync(string uri, Func<bool> current, Action? rejected = null) { rejected?.Invoke(); return Task.FromResult(false); }
    Task AddToQueueAsync(string spotifyUri);
    Task SkipAsync();
    Task PauseAsync();
    Task ResumeAsync(string? deviceId = null);
    Task<PlaybackState?> GetPlaybackStateAsync();
    Task<SpotifyQueueState> GetQueueAsync();
    Task<Song?> GetTrackAsync(string trackId);
    Task<List<Song>> GetPlaylistTracksAsync(string playlistId, int maxTracks = int.MaxValue);
    Task<List<Song>> GetAlbumTracksAsync(string albumId, int maxTracks = int.MaxValue);
    Task<List<Song>> ScrapeEmbedPlaylistTracksAsync(string playlistId, int maxTracks = int.MaxValue);
    Task<List<Song>> ScrapeEmbedAlbumTracksAsync(string albumId, int maxTracks = int.MaxValue);
    Task<Song?> ScrapeEmbedTrackAsync(string trackId);
    Task<List<SpotifyPlaylistSummary>> GetUserPlaylistsAsync();
    Task<List<Song>> GetSavedTracksAsync(int maxTracks = int.MaxValue);
    Task SetVolumeAsync(int volumePercent);
    Task SeekAsync(int positionMs);
    Task PreviousAsync();
    Task<List<SpotifyDevice>> GetDevicesAsync();
    Task TransferPlaybackAsync(string deviceId, bool play = false);
    Task SaveTrackAsync(string trackId);
    Task RemoveSavedTrackAsync(string trackId);
    Task<bool> IsTrackSavedAsync(string trackId);
}

public class SpotifyDevice
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsRestricted { get; set; }
    public int? VolumePercent { get; set; }
}

public class SpotifyPlaylistSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public int TrackCount { get; set; }
}

public class PlaybackState
{
    public DateTimeOffset SampledAt { get; set; } = DateTimeOffset.UtcNow;
    public int EstimatedProgressMs(DateTimeOffset now) => (int)Math.Clamp((long)ProgressMs + (IsPlaying ? Math.Max(0, (now - SampledAt).TotalMilliseconds) : 0), 0, Math.Max(0, DurationMs));
    public string? RepeatState { get; set; }
    public int? VolumePercent { get; set; }
    public int ProgressMs { get; set; }
    public bool IsPlaying { get; set; }
    public string TrackUri { get; set; } = string.Empty;
    public string? LinkedFromUri { get; set; }
    public bool MatchesTrack(string? uri) => !string.IsNullOrEmpty(uri) && (TrackUri == uri || LinkedFromUri == uri);
    public int DurationMs { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
}

public class SpotifyQueueState
{
    public Song? CurrentlyPlaying { get; set; }
    public List<Song> Queue { get; set; } = new();
}
