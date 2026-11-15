using System.Text.Json;
namespace MusicBot.Services.Downloader;
internal static class YouTubeVideoMetadata
{
    public static object? Parse(JsonElement root, string expectedId)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("videoDetails", out var video) || video.ValueKind != JsonValueKind.Object
            || !video.TryGetProperty("videoId", out var id) || id.GetString() != expectedId) return null;
        string Text(string key) => video.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        if (!root.TryGetProperty("microformat", out var format) || format.ValueKind != JsonValueKind.Object
            || !format.TryGetProperty("playerMicroformatRenderer", out var details) || details.ValueKind != JsonValueKind.Object
            || !details.TryGetProperty("category", out var category) || category.ValueKind != JsonValueKind.String) return null;
        var live = video.TryGetProperty("isLive", out var isLive) && isLive.ValueKind == JsonValueKind.True;
        // Upcoming and currently live broadcasts have a live marker without an end timestamp.
        if (details.TryGetProperty("liveBroadcastDetails", out var broadcast) && broadcast.ValueKind == JsonValueKind.Object
            && !broadcast.TryGetProperty("endTimestamp", out _)) live = true;
        return new { title = Text("title"), artist = Text("author"), duration = int.TryParse(Text("lengthSeconds"), out var seconds) ? seconds : 0,
            categories = new[] { category.GetString() }, is_live = live };
    }
}
