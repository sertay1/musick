namespace MusicBot.Services;

/// <summary>
/// Holds the last-known connection status for platform services
/// so the OverlayHub can send it to newly-connected clients.
/// </summary>
public class IntegrationStatusTracker
{
    public string KickStatus { get; set; } = "disconnected";
}
