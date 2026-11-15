namespace MusicBot.Services.Player;
public static class YouTubeEmbedIdentity
{
    public const string Referrer = "https://musicbot/";
    public static bool IsEmbedDocument(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host is "www.youtube.com" or "youtube.com" or "www.youtube-nocookie.com" or "youtube-nocookie.com"
        && uri.AbsolutePath.StartsWith("/embed/", StringComparison.Ordinal);
}
