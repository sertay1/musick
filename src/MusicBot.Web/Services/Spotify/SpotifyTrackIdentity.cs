namespace MusicBot.Services.Spotify;

// Spotify desktop can play another release without populating linked_from.
// Treat tightly matching editions as the requested song, never just similar search results.
public sealed record SpotifyTrackIdentity(string Title, int DurationMs, string[] ArtistIds, string? Isrc, string ArtistNames = "")
{
    public bool IsEquivalentTo(SpotifyTrackIdentity other)
    {
        if (DurationMs <= 0 || other.DurationMs <= 0 || Math.Abs((long)DurationMs - other.DurationMs) > 2000)
            return false;
        if (ArtistIds.Length == 0 || ArtistIds.Any(string.IsNullOrWhiteSpace)
            || !ArtistIds.ToHashSet(StringComparer.Ordinal).SetEquals(other.ArtistIds)) return false;
        return !string.IsNullOrWhiteSpace(Title) && string.Equals(Title.Trim(), other.Title.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
