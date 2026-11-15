namespace MusicBot.Services.Spotify;

// Concurrent UI/chat/sync reads share a bounded-lived snapshot, including an empty player.
public sealed class SpotifySnapshot<T>(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _generation, _loadedGeneration = -1;
    private DateTimeOffset _expires;
    private T _value = default!;
    public void Invalidate() => Interlocked.Increment(ref _generation);
    public async Task<T> GetAsync(Func<Task<T>> load, TimeSpan lifetime)
    {
        await _lock.WaitAsync();
        try
        {
            var generation = Interlocked.Read(ref _generation);
            if (_loadedGeneration == generation && _expires > _clock.GetUtcNow()) return _value;
            var value = await load();
            // An overlapping command may have made this response obsolete.
            if (generation == Interlocked.Read(ref _generation))
            { _value = value; _loadedGeneration = generation; _expires = _clock.GetUtcNow() + lifetime; }
            return value;
        }
        finally { _lock.Release(); }
    }
}
