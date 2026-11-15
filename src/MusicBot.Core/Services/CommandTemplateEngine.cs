using System.Text.RegularExpressions;
using MusicBot.Core.Models;

namespace MusicBot.Core.Services;

public class TemplateContext
{
    public string Error { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string Song { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public string QueueLength { get; set; } = string.Empty;
    public string WaitTime { get; set; } = string.Empty;
    public string Requester { get; set; } = string.Empty;
    public string Volume { get; set; } = string.Empty;
}

public static class CommandTemplateEngine
{
    public const int KickMaxMessageLength = 500;

    private static readonly HashSet<string> KnownPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "{error}",
        "{hata}",
        "{user}",
        "{kullanici}",
        "{song}",
        "{sarki}",
        "{artist}",
        "{sanatci}",
        "{position}",
        "{sira}",
        "{queue_length}",
        "{sayi}",
        "{wait_time}",
        "{sure}",
        "{requester}",
        "{isteyen}",
        "{volume}",
        "{ses}"
    };

    private static readonly Regex PlaceholderRegex = new(@"\{[a-zA-Z0-9_]+\}", RegexOptions.Compiled);

    /// <summary>
    /// Renders a template string by replacing placeholders with values from the given context,
    /// and safely truncating the result to the Kick message character limit (500 chars).
    /// </summary>
    public static string Render(string? template, TemplateContext context, int maxLength = KickMaxMessageLength)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        var rendered = template
            .Replace("{error}", context.Error ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{hata}", context.Error ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{user}", context.User ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{kullanici}", context.User ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{song}", context.Song ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{sarki}", context.Song ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{artist}", context.Artist ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{sanatci}", context.Artist ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{position}", context.Position ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{sira}", context.Position ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{queue_length}", context.QueueLength ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{sayi}", context.QueueLength ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{wait_time}", context.WaitTime ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{sure}", context.WaitTime ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{requester}", context.Requester ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{isteyen}", context.Requester ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{volume}", context.Volume ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{ses}", context.Volume ?? "", StringComparison.OrdinalIgnoreCase);

        return TruncateSafely(rendered, maxLength);
    }

    /// <summary>
    /// Safely truncates a message to the maximum length, breaking at a word boundary if possible.
    /// </summary>
    public static string TruncateSafely(string message, int maxLength = KickMaxMessageLength)
    {
        if (string.IsNullOrEmpty(message) || message.Length <= maxLength)
            return message;

        const string ellipsis = "...";
        var maxContentLen = maxLength - ellipsis.Length;
        if (maxContentLen <= 0)
            return message[..maxLength];

        // Look for the last space within maxContentLen to avoid cutting off words
        var lastSpace = message.LastIndexOf(' ', maxContentLen);
        if (lastSpace > maxContentLen * 0.6)
        {
            return string.Concat(message.AsSpan(0, lastSpace), ellipsis);
        }

        return string.Concat(message.AsSpan(0, maxContentLen), ellipsis);
    }

    /// <summary>
    /// Scans all command templates in a configuration and returns any unknown placeholders found.
    /// </summary>
    public static List<(string Command, string TemplateKey, string UnknownPlaceholder)> FindUnknownPlaceholders(CommandsConfiguration config)
    {
        var result = new List<(string, string, string)>();
        if (config?.Commands == null) return result;

        foreach (var (cmdName, cmdDef) in config.Commands)
        {
            if (cmdDef?.Templates == null) continue;

            foreach (var (tmplKey, tmplVal) in cmdDef.Templates)
            {
                if (string.IsNullOrEmpty(tmplVal)) continue;

                var matches = PlaceholderRegex.Matches(tmplVal);
                foreach (Match m in matches)
                {
                    if (!KnownPlaceholders.Contains(m.Value))
                    {
                        result.Add((cmdName, tmplKey, m.Value));
                    }
                }
            }
        }

        return result;
    }
}
