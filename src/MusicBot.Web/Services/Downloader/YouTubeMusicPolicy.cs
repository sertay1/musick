using System.Text.Json;
namespace MusicBot.Services.Downloader;
internal static class YouTubeMusicPolicy
{
    public static void Validate(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("YouTube şarkı bilgisi doğrulanamadı.");
        string Text(string key) => metadata.TryGetProperty(key,out var value) && value.ValueKind==JsonValueKind.String ? value.GetString() ?? "" : "";
        if (metadata.TryGetProperty("is_live",out var live) && live.ValueKind==JsonValueKind.True || Text("live_status") is "is_live" or "is_upcoming")
            throw new InvalidOperationException("Canlı YouTube yayınları şarkı olarak eklenemez.");
        if (!metadata.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array
            || !categories.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() is "Music" or "Müzik"))
            throw new InvalidOperationException("Yalnızca YouTube Müzik kategorisindeki videolar kabul edilir.");
    }
}
