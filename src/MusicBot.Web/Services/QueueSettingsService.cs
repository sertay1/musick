using Microsoft.AspNetCore.SignalR;
using MusicBot.Hubs;

namespace MusicBot.Services;

/// <summary>
/// Holds the live queue/voting configuration and broadcasts changes via SignalR.
/// </summary>
public class QueueSettingsService
{
    public int    MaxQueueSize                     { get; private set; }
    public bool   MaxQueueSizeEnabled              { get; private set; } = true;
    public int    MaxSongsPerUser                  { get; private set; }
    public bool   MaxSongsPerUserEnabled           { get; private set; } = true;
    public bool   VotingEnabled                    { get; private set; }
    public bool   PresenceCheckEnabled             { get; private set; } = false;
    public int    PresenceCheckWarningSeconds      { get; private set; } = 30;
    public int    PresenceCheckConfirmSeconds      { get; private set; } = 30;
    public bool   SaveDownloads                    { get; private set; } = false;
    public bool   AutoQueueEnabled                 { get; private set; } = false;
    public bool   LoudnessNormalizationEnabled     { get; private set; } = true;
    public int YouTubeVolumePercent { get; private set; } = 70;
    public void SetYouTubeVolume(int value) => YouTubeVolumePercent = Math.Clamp(value, 0, 100);
    public bool   DirectSpotifyMode => true;
    public bool ChatPlaylistRequestsEnabled { get; private set; }
    public void SetChatPlaylistRequests(bool enabled) => ChatPlaylistRequestsEnabled = enabled;
    public bool SpotifyEnabled { get; private set; } = true;
    public bool YouTubeEnabled { get; private set; } = true;
    public void SetPlatforms(bool spotify, bool youtube) { SpotifyEnabled = spotify; YouTubeEnabled = youtube; }

    private readonly IHubContext<OverlayHub> _hub;

    public QueueSettingsService(IConfiguration config, IHubContext<OverlayHub> hub)
    {
        _hub = hub;
        SetYouTubeVolume(config.GetValue("Queue:YouTubeVolumePercent", 70));
        var q       = config.GetSection("Queue");
        ChatPlaylistRequestsEnabled = q.GetValue("ChatPlaylistRequestsEnabled", false);
        SpotifyEnabled = q.GetValue("SpotifyEnabled", true);
        YouTubeEnabled = q.GetValue("YouTubeEnabled", true);
        MaxQueueSize           = q.GetValue("MaxSize",                50);
        MaxQueueSizeEnabled    = false;
        MaxSongsPerUser        = q.GetValue("MaxSongsPerUser",        10);
        MaxSongsPerUserEnabled = true;
        VotingEnabled          = q.GetValue("VotingEnabled",          false);
        PresenceCheckEnabled        = q.GetValue("PresenceCheckEnabled",        false);
        PresenceCheckWarningSeconds = q.GetValue("PresenceCheckWarningSeconds", 30);
        PresenceCheckConfirmSeconds = q.GetValue("PresenceCheckConfirmSeconds", 30);
        SaveDownloads    = q.GetValue("SaveDownloads",    false);
        AutoQueueEnabled = q.GetValue("AutoQueueEnabled", false);
        LoudnessNormalizationEnabled = q.GetValue("LoudnessNormalizationEnabled", true);

    }

    public async Task UpdateAsync(
        int    maxQueueSize,
        int    maxSongsPerUser,
        bool   votingEnabled,
        bool   presenceCheckEnabled,
        int    presenceCheckWarningSeconds,
        int    presenceCheckConfirmSeconds,
        bool   saveDownloads   = false,
        bool   autoQueueEnabled = false,
        bool   loudnessNormalizationEnabled = true,
        bool   directSpotifyMode = true,
        bool   maxQueueSizeEnabled = true,
        bool   maxSongsPerUserEnabled = true)
    {
        MaxQueueSize                    = maxQueueSize;
        MaxQueueSizeEnabled             = false;
        MaxSongsPerUser                 = maxSongsPerUser;
        MaxSongsPerUserEnabled          = true;
        VotingEnabled                   = votingEnabled;
        PresenceCheckEnabled            = presenceCheckEnabled;
        PresenceCheckWarningSeconds     = Math.Max(5, presenceCheckWarningSeconds);
        PresenceCheckConfirmSeconds     = Math.Max(5, presenceCheckConfirmSeconds);
        SaveDownloads                   = saveDownloads;
        AutoQueueEnabled                = autoQueueEnabled;
        LoudnessNormalizationEnabled    = loudnessNormalizationEnabled;
        directSpotifyMode = true;

        await _hub.Clients.Group($"user:{LocalUser.Id}")
                          .SendAsync("settings:updated", new
                          {
                              maxQueueSize,
                              maxQueueSizeEnabled,
                              maxSongsPerUser,
                              maxSongsPerUserEnabled,
                              votingEnabled,
                              presenceCheckEnabled,
                              presenceCheckWarningSeconds     = PresenceCheckWarningSeconds,
                              presenceCheckConfirmSeconds     = PresenceCheckConfirmSeconds,
                              saveDownloads,
                              autoQueueEnabled,
                              loudnessNormalizationEnabled,
                              directSpotifyMode,
                              chatPlaylistRequestsEnabled = ChatPlaylistRequestsEnabled,
                              spotifyEnabled = SpotifyEnabled,
                              youTubeEnabled = YouTubeEnabled,
                              youTubeVolumePercent = YouTubeVolumePercent,
                          });
    }
}
