using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MusicBot.Core.Models;

namespace MusicBot.Services;

/// <summary>Ranks text requests by the requested title/artist, rather than trusting provider order.</summary>
internal static class SongSearchMatcher
{
    private static readonly HashSet<string> Noise = ["official", "audio", "video", "officialaudio", "officialvideo", "lyrics", "lyric", "resmi", "klip", "topic"];
    private static readonly HashSet<string> Special = ["cover", "karaoke", "instrumental", "remix", "sped", "spedup", "slowed", "reverb", "nightcore"];
    private static readonly HashSet<string> Versions = ["live", "akustik", "acoustic", "remaster", "remastered", "edit", "radio"];

    internal static bool RequestsSpecialVersion(string query) => Words(query).Any(Special.Contains);

    public static Song? Select(string query, IEnumerable<Song> candidates)
    {
        var requested = Words(query);
        if (requested.Length == 0) return null;
        var normalizedQuery = string.Join(' ', requested);
        var split = Regex.Split(query.Trim(), @"\s+[-–—]\s+", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return candidates.Where(s => !s.IsPlaylist && !string.IsNullOrWhiteSpace(s.SpotifyUri)).Select((song, index) =>
        {
            var title = Words(song.Title);
            var artist = Words(song.Artist);
            if (song.SpotifyUri.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase))
            {
                // Video publishers often put the artist in the title; the channel can be a label.
                var parts = Regex.Split(song.Title, @"\s+[-–—]\s+", RegexOptions.None, TimeSpan.FromMilliseconds(100));
                if (parts.Length == 2)
                {
                    var left = Words(parts[0]); var right = Words(parts[1]);
                    var reversed = artist.Length > 0 && artist.All(w => right.Contains(w));
                    title = reversed ? left : right;
                    artist = artist.Concat(reversed ? right : left).Distinct().ToArray();
                }
            }
            var all = title.Concat(artist).ToArray();
            if (title.Length == 0 || !requested.All(q => all.Any(w => Matches(q, w)))) return (Song: song, Score: double.NegativeInfinity, Index: index);
            // A matching artist alone is not evidence that this is the requested song.
            var titleCoverage = title.Count(w => requested.Any(q => Matches(q, w))) / (double)title.Length;
            if (titleCoverage < .4 || all.Any(w => Special.Contains(w) && !requested.Contains(w))) return (Song: song, Score: double.NegativeInfinity, Index: index);
            var exactTitle = string.Join(' ', title) == normalizedQuery;
            if (!exactTitle && split.Length == 2 && !(ContainsWords(split[0], artist) && ContainsWords(split[1], title))
                && !(ContainsWords(split[1], artist) && ContainsWords(split[0], title))) return (Song: song, Score: double.NegativeInfinity, Index: index);
            var score = (exactTitle ? 100 : 0) + titleCoverage * 40
                + requested.Count(q => artist.Contains(q)) * 4
                - all.Count(w => Versions.Contains(w) && !requested.Contains(w)) * 12;
            return (Song: song, Score: score, Index: index);
        }).Where(x => double.IsFinite(x.Score)).OrderByDescending(x => x.Score).ThenBy(x => x.Index).Select(x => x.Song).FirstOrDefault();
    }

    private static bool ContainsWords(string query, string[] target)
    {
        var words = Words(query);
        return words.Length > 0 && words.All(q => target.Any(w => Matches(q, w)));
    }

    private static string[] Words(string text)
    {
        var normalized = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant().Replace('ı', 'i').Replace("'", "").Replace("’", "").Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                normalized.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        return normalized.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Noise.Contains(w)).Distinct().ToArray();
    }

    private static bool Matches(string a, string b)
    {
        if (a == b) return true;
        // Only one typo in a substantial word; short words must match exactly.
        if (a.Length < 5 || b.Length < 5 || Math.Abs(a.Length - b.Length) > 1) return false;
        var i = 0; var j = 0; var edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length >= b.Length) i++;
            if (b.Length >= a.Length) j++;
        }
        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }
}
