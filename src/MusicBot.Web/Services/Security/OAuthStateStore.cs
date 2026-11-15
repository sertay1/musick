using System.Collections.Concurrent;
using System.Security.Cryptography;
namespace MusicBot.Services.Security;
public sealed class OAuthStateStore
{
    public static OAuthStateStore Shared { get; } = new();
    private readonly ConcurrentDictionary<string, (string Provider, DateTimeOffset Until)> _pending = new();
    public string Begin(string provider)
    {
        foreach (var entry in _pending.Where(p => p.Value.Provider == provider || p.Value.Until < DateTimeOffset.UtcNow)) _pending.TryRemove(entry.Key, out _);
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _pending[value] = (provider, DateTimeOffset.UtcNow.AddMinutes(10)); return value;
    }
    public bool Consume(string provider, string? state) => state != null && _pending.TryRemove(state, out var value)
        && value.Provider == provider && value.Until > DateTimeOffset.UtcNow;
}
