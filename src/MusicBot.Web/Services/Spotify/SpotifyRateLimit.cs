using System.Net.Http.Headers;
namespace MusicBot.Services.Spotify;

public sealed class SpotifyRateLimitException(int retryAfterSeconds) : InvalidOperationException(
    $"Spotify istek sınırına ulaşıldı (429). {Math.Max(1, retryAfterSeconds)} saniye sonra tekrar deneyin; şarkınız korundu.")
{
    public int RetryAfterSeconds { get; } = Math.Max(1, retryAfterSeconds);
}

public sealed class SpotifyRateLimit
{
    private readonly TimeProvider _clock;
    private readonly string? _persistencePath;
    public SpotifyRateLimit(TimeProvider? timeProvider = null, string? persistencePath = null)
    {
        _clock = timeProvider ?? TimeProvider.System; _persistencePath = persistencePath;
        if (persistencePath == null) return;
        try
        {
            if (File.Exists(persistencePath) && DateTimeOffset.TryParse(File.ReadAllText(persistencePath),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var until)) _until = until;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private readonly object _lock = new();
    private DateTimeOffset _until;
    public void ThrowIfLimited()
    {
        lock (_lock)
        {
            var remaining = _until - _clock.GetUtcNow();
            if (remaining > TimeSpan.Zero) throw new SpotifyRateLimitException((int)Math.Ceiling(remaining.TotalSeconds));
        }
    }
    public SpotifyRateLimitException Block(RetryConditionHeaderValue? retry)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            var delay = retry?.Delta ?? (retry?.Date is {} date ? date - now : TimeSpan.FromSeconds(30));
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            var until = now + delay;
            if (until > _until)
            {
                _until = until;
                if (_persistencePath != null)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_persistencePath))!);
                        var temp = _persistencePath + ".tmp";
                        File.WriteAllText(temp, _until.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                        File.Move(temp, _persistencePath, overwrite: true);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return new SpotifyRateLimitException((int)Math.Min(int.MaxValue, Math.Ceiling((_until - now).TotalSeconds)));
        }
    }
}
