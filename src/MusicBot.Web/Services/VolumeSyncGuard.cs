namespace MusicBot.Services;

public sealed class VolumeSyncGuard
{
    private readonly object _sync = new();
    private DateTimeOffset _ignoreUntil;
    public void LocalChange(DateTimeOffset now) { lock (_sync) _ignoreUntil = now.AddSeconds(4); }
    public bool AcceptRemote(DateTimeOffset observedAt) { lock (_sync) return observedAt >= _ignoreUntil; }
}
