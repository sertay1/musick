using MusicBot.Core.Interfaces;

namespace MusicBot.Services;

/// <summary>Recognizes a completed track without interpreting pause/API gaps as completion.</summary>
public sealed class SpotifyPlaybackObserver
{
    private string? _uri;
    private string? _completedUri;
    private int _progress;
    private int _duration;
    private bool _playing;
    private DateTimeOffset _observedAt;

    public bool ShouldAdvance(PlaybackState? state, DateTimeOffset now)
    {
        if (state == null || string.IsNullOrEmpty(state.TrackUri)) return false;
        var changed = _uri != null && _uri != state.TrackUri;
        var elapsed = Math.Clamp((now - _observedAt).TotalMilliseconds, 0, 5000);
        var repeated = !changed && state.RepeatState == "track" && state.IsPlaying
            && state.ProgressMs < _progress && state.ProgressMs <= elapsed + 500;
        var finishedPrevious = (changed || repeated) && _playing && _duration > 0
            && _progress + elapsed >= _duration - 1000 && _completedUri != _uri;
        if (finishedPrevious) _completedUri = _uri;
        var atEnd = state.IsPlaying && state.DurationMs > 0
            && state.ProgressMs >= state.DurationMs - 500 && _completedUri != state.TrackUri;
        if (atEnd) _completedUri = state.TrackUri;
        _uri = state.TrackUri;
        _progress = state.ProgressMs;
        _duration = state.DurationMs;
        _playing = state.IsPlaying;
        _observedAt = now;
        return finishedPrevious || atEnd;
    }
}
