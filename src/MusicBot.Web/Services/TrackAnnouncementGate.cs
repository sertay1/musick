namespace MusicBot.Services;

/// <summary>Ignores transient observations and reserves each announcement atomically.</summary>
public sealed class TrackAnnouncementGate
{
    private readonly object _sync = new();
    private string? _candidate;
    private int _observations;
    private string? _announced;

    public bool NeedsConfirmation { get { lock (_sync) return _candidate != null && _candidate != _announced && _observations == 1; } }

    public bool TryAccept(string key, bool confirmed)
    {
        lock (_sync)
        {
            if (!string.Equals(_candidate, key, StringComparison.Ordinal))
            {
                _candidate = key;
                _observations = 0;
            }
            _observations++;
            if (!confirmed && _observations < 2) return false;
            if (string.Equals(_announced, key, StringComparison.Ordinal)) return false;
            _announced = key;
            return true;
        }
    }

    public void ResetCandidate()
    {
        lock (_sync) { _candidate = null; _observations = 0; }
    }
    public void Release(string key) { lock (_sync) if (_announced == key) { _announced = null; _candidate = null; _observations = 0; } }
}
