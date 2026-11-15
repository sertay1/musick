namespace MusicBot.Services.Spotify;
public sealed class SpotifyPlayerCommandException(int statusCode, string message) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
}
