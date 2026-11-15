using System.Text;
using System.Text.Json;
using MusicBot.Core.Models;

namespace MusicBot.Services.Downloader;

internal static class YouTubeSearchPage
{
    public static List<Song> Parse(string html, int limit)
    {
        var start = html.IndexOf("ytInitialData", StringComparison.Ordinal);
        if (start < 0) return [];
        start = html.IndexOf('{', start);
        if (start < 0) return [];
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(html[start..]));
            using var document = JsonDocument.ParseValue(ref reader);
            var stack = new Stack<JsonElement>(); stack.Push(document.RootElement);
            var result = new List<Song>(); var ids = new HashSet<string>();
            while (stack.Count > 0 && result.Count < limit)
            {
                var node = stack.Pop();
                if (node.ValueKind == JsonValueKind.Object)
                {
                    if (node.TryGetProperty("videoRenderer", out var video) && video.ValueKind == JsonValueKind.Object)
                    {
                        var id = video.TryGetProperty("videoId", out var v) ? v.GetString() : null;
                        if (id?.Length == 11 && ids.Add(id))
                        {
                            var title = Text(video, "title");
                            var artist = Text(video, "ownerText");
                            var duration = Text(video, "lengthText");
                            if (string.IsNullOrEmpty(duration)) continue; // Exclude live streams and upcoming videos.
                            var seconds = 0; foreach (var part in duration.Split(':')) if (int.TryParse(part, out var n)) seconds = seconds * 60 + n;
                            result.Add(new Song { SpotifyUri = "youtube:" + id, Title = title, Artist = artist, DurationMs = seconds * 1000, CoverUrl = "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg", Platform = "youtube" });
                        }
                    }
                    else foreach (var property in node.EnumerateObject().Reverse()) stack.Push(property.Value);
                }
                else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray().Reverse()) stack.Push(child);
            }
            return result;
        }
        catch (JsonException) { return []; }
    }
    private static string Text(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object) return "";
        if (value.TryGetProperty("simpleText", out var simple) && simple.ValueKind == JsonValueKind.String) return simple.GetString() ?? "";
        if (!value.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(runs.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("text", out _)).Select(r => r.GetProperty("text").GetString()));
    }
}
