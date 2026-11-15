namespace MusicBot.Services.Platforms;

/// <summary>
/// Domain model for an incoming Kick chat message.
/// </summary>
public record KickChatMessage(
    string SenderUsername,
    string Content,
    IReadOnlyList<string> Badges
);

/// <summary>
/// Abstraction for reading Kick chat messages.
/// Isolates the chat reading transport (unofficial WebSocket vs official Webhook/Eventsub)
/// from the application logic.
/// </summary>
public interface IKickChatReader : IAsyncDisposable
{
    event Action<KickChatMessage>? OnMessageReceived;
    Task ConnectAsync(string channel, CancellationToken ct = default);
    Task DisconnectAsync();
    bool IsConnected { get; }
}
