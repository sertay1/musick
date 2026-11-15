using System.Web;

namespace MusicBot.Services;

internal static class YouTubeLinks
{
    public static bool IsYouTube(string value) => TryParse(value, out _);

    private static bool TryParse(string value, out Uri uri)
    {
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            && parsed.Scheme is "https" or "http"
            && parsed.Host.ToLowerInvariant() is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or "www.music.youtube.com" or "youtu.be" or "www.youtu.be")
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    public static bool TryVideoId(string value, out string id)
    {
        id = "";
        if (!TryParse(value, out var uri)) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase)) id = parts[0];
        else if (parts.Length >= 2 && parts[0] is "shorts" or "embed" or "live") id = parts[1];
        else if (parts[0].Equals("watch", StringComparison.OrdinalIgnoreCase)) id = HttpUtility.ParseQueryString(uri.Query)["v"] ?? "";
        return id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    }

    public static bool TryPlaylistId(string value, out string id)
    {
        id = "";
        if (!TryParse(value, out var uri)) return false;
        id = HttpUtility.ParseQueryString(uri.Query)["list"] ?? "";
        return id.Length > 0;
    }
}
