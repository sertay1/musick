using MusicBot.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;
using MusicBot.Hubs;

namespace MusicBot.Services.Player;

public sealed record BrowserPlayback(long Version, string? Uri, bool IsPlaying, int PositionMs, int DurationMs, float Volume, bool RequestedPlaying = true);

/// <summary>Owns exactly one transport: Windows audio or the visible YouTube iframe.</summary>
public sealed class HybridPlayerService : ILocalPlayerService
{
    private readonly LocalPlayerService _local;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly object _sync = new();
    private long _version;
    private string? _browserUri;
    private bool _playing, _ended, _requestedPlaying;
    private int _position, _duration;
    private float _volume = 1f;
    private readonly QueueSettingsService? _settings;
    private float OutputVolume => Math.Clamp(_volume * (_settings?.YouTubeVolumePercent ?? 100) / 100f, 0f, 1f);
    public HybridPlayerService(ILogger<LocalPlayerService> logger, IHubContext<OverlayHub> hub, QueueSettingsService? settings = null)
    {
        _settings = settings;
        _local = new LocalPlayerService(logger); _hub = hub;
        _local.OnStateChanged += s => { if (!IsBrowserPlayback) OnStateChanged?.Invoke(s); };
        _local.OnTrackEnded += (file, version) => { var current = PlaybackVersion; if (!IsBrowserPlayback && version == _local.PlaybackVersion) OnTrackEnded?.Invoke(file, current); };
    }
    public bool IsBrowserPlayback { get { lock (_sync) return _browserUri != null; } }
    public bool IsPlaying => IsBrowserPlayback ? _playing : _local.IsPlaying;
    public int PositionMs => IsBrowserPlayback ? _position : _local.PositionMs;
    public int DurationMs => IsBrowserPlayback ? _duration : _local.DurationMs;
    public string? CurrentFilePath => IsBrowserPlayback ? _browserUri : _local.CurrentFilePath;
    public float Volume => _volume;
    public string? DeviceId => _local.DeviceId;
    public long PlaybackVersion => Interlocked.Read(ref _version);
    public event Action<LocalPlayerState>? OnStateChanged;
    public event Action<string?, long>? OnTrackEnded;
    public BrowserPlayback Snapshot() { lock (_sync) return new(PlaybackVersion, _browserUri, _playing, _position, _duration, OutputVolume, _requestedPlaying); }
    private Task Send(string action) => _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("youtube:command", new { action, state = Snapshot() });
    public async Task PlayAsync(string filePath)
    {
        await StopAsync();
        if (!filePath.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase)) { _local.SetVolume(OutputVolume); await _local.PlayAsync(filePath); return; }
        lock (_sync) { Interlocked.Increment(ref _version); _browserUri = filePath; _playing = false; _requestedPlaying = true; _position = 0; _duration = 0; _ended = false; }
        await Send("load");
    }
    public Task PauseAsync() { if (!IsBrowserPlayback) return _local.PauseAsync(); lock (_sync) { _playing = false; _requestedPlaying = false; } return Send("pause"); }
    public Task ResumeAsync() { if (!IsBrowserPlayback) return _local.ResumeAsync(); bool restart; lock (_sync) { restart = _ended; _ended = false; _requestedPlaying = true; } return Send(restart ? "load" : "resume"); }
    public async Task StopAsync()
    {
        var browser = IsBrowserPlayback;
        Interlocked.Increment(ref _version);
        lock (_sync) { _browserUri = null; _playing = _requestedPlaying = false; _position = _duration = 0; _ended = true; }
        await _local.StopAsync();
        if (browser) await Send("stop");
    }
    public void SetVolume(float volume) { _volume = Math.Clamp(volume, 0f, 1f); _local.SetVolume(OutputVolume); if (IsBrowserPlayback) _ = Send("volume"); }
    public void SeekTo(int positionMs) { if (!IsBrowserPlayback) { _local.SeekTo(positionMs); return; } _position = Math.Max(0, positionMs); _ = Send("seek"); }
    public Task SetDeviceAsync(string? deviceId) => _local.SetDeviceAsync(deviceId);
    public bool Report(BrowserPlayback report, bool ended, int? error = null)
    {
        string? uri;
        var completed = false;
        LocalPlayerState state;
        lock (_sync)
        {
            if (report.Version != _version || report.Uri != _browserUri || _browserUri == null || _ended || report.RequestedPlaying != _requestedPlaying) return false;
            uri = _browserUri; _playing = _requestedPlaying && report.IsPlaying; _position = Math.Max(0, report.PositionMs); _duration = Math.Max(0, report.DurationMs);
            completed = ended && _requestedPlaying;
            if (completed) { _ended = true; _playing = _requestedPlaying = false; }
            state = new LocalPlayerState { FilePath = uri, IsPlaying = _playing, PositionMs = _position, DurationMs = _duration };
        }
        OnStateChanged?.Invoke(state);
        if (completed && error is not > 0) OnTrackEnded?.Invoke(uri, report.Version);
        return true;
    }
    public void Dispose() { Interlocked.Increment(ref _version); _local.Dispose(); }
}
