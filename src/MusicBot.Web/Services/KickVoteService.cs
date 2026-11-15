using Microsoft.AspNetCore.SignalR;
using MusicBot.Core.Models;
using MusicBot.Hubs;

namespace MusicBot.Services;

/// <summary>
/// Manages a 30-second "kick-vote" when a song starts (extra time for stream delay).
/// Chat can vote !si (skip) or !no (keep). Majority wins.
/// </summary>
public class KickVoteService
{
    private readonly IHubContext<OverlayHub>  _hub;
    private readonly ILogger<KickVoteService> _logger;
    private readonly QueueSettingsService     _settings;

    /// <summary>
    /// Set by PlaybackSyncService.ExecuteAsync to avoid circular dependency.
    /// Called when the vote result is "skip".
    /// </summary>
    public Func<Task>? SkipCurrentSong { get; set; }

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private int _yes, _no;
    private readonly HashSet<string> _voted = new(StringComparer.OrdinalIgnoreCase);
    private Song? _song;
    private bool _active;
    private long _generation;
    private Func<Task>? _skipAction;
    private DateTime _endsAt;

    public bool IsActive => _active;

    public bool VotingEnabled => _settings.VotingEnabled;

    /// <summary>Returns the current in-progress vote state, or null if no vote is active.</summary>
    public object? GetCurrentVotePayload()
    {
        lock (_lock)
        {
            if (!_active || _song == null) return null;
            var secondsLeft = Math.Max(0, (int)(_endsAt - DateTime.UtcNow).TotalSeconds);
            return new
            {
                songTitle   = _song.Title,
                artist      = _song.Artist,
                coverUrl    = _song.CoverUrl ?? "",
                yesVotes    = _yes,
                noVotes     = _no,
                secondsLeft,
            };
        }
    }

    public KickVoteService(IHubContext<OverlayHub> hub, ILogger<KickVoteService> logger, QueueSettingsService settings)
    {
        _hub      = hub;
        _logger   = logger;
        _settings = settings;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task StartVoteAsync(Song song)
    {
        if (!_settings.VotingEnabled) return;

        CancellationTokenSource cts;
        CancellationToken token;
        long generation;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            generation = ++_generation;
            _skipAction = SkipCurrentSong;
            cts     = _cts = new CancellationTokenSource();
            token = cts.Token;
            _yes    = 0;
            _no     = 0;
            _voted.Clear();
            _song   = song;
            _active = true;
            _endsAt = DateTime.UtcNow.AddSeconds(30);
        }

        _logger.LogInformation("KickVote started for \"{Title}\"", song.Title);

        await BroadcastAsync("vote:started", new
        {
            songTitle   = song.Title,
            artist      = song.Artist,
            coverUrl    = song.CoverUrl,
            yesVotes    = 0,
            noVotes     = 0,
            secondsLeft = 30,
        });

        // Server-side countdown: broadcast update every second
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 29; i >= 0; i--)
                {
                    await Task.Delay(1000, token);
                    int y, n;
                    lock (_lock) { if (generation != _generation) return; y = _yes; n = _no; }
                    await BroadcastAsync("vote:updated", new { yesVotes = y, noVotes = n, secondsLeft = i });
                }
                await EndVoteAsync(generation);
            }
            catch (OperationCanceledException) { /* vote cancelled — new song started */ }
        });
    }

    /// <summary>Returns "ok", "already_voted", or "no_active".</summary>
    public async Task<string> VoteAsync(string username, bool skip)
    {
        int y, n;
        lock (_lock)
        {
            if (!_active || !_settings.VotingEnabled) return "no_active";
            if (!_voted.Add(username)) return "already_voted";
            if (skip) _yes++; else _no++;
            y = _yes; n = _no;
        }

        int secondsLeft = Math.Max(0, (int)(_endsAt - DateTime.UtcNow).TotalSeconds);
        await BroadcastAsync("vote:updated", new { yesVotes = y, noVotes = n, secondsLeft });
        return "ok";
    }

    public void CancelVote()
    {
        bool wasActive;
        lock (_lock) { wasActive = _active; ++_generation; _active = false; _song = null; _cts?.Cancel(); _cts?.Dispose(); _cts = null; }
        if (wasActive) _ = BroadcastAsync("vote:ended", new { result = "cancelled", yesVotes = 0, noVotes = 0 });
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task EndVoteAsync(long generation)
    {
        bool doSkip;
        Func<Task>? skipAction;
        int y, n;
        lock (_lock)
        {
            if (!_active || generation != _generation) return;
            _active = false;
            y = _yes; n = _no;
            doSkip = y > n;
            skipAction = _skipAction;
        }

        _logger.LogInformation("KickVote ended: {Result} (yes={Yes}, no={No})",
            doSkip ? "SKIP" : "KEEP", y, n);

        await BroadcastAsync("vote:ended", new
        {
            result   = doSkip ? "skip" : "keep",
            yesVotes = y,
            noVotes  = n,
        });

        if (doSkip && skipAction != null)
        {
            await Task.Delay(2500); // let overlays show the result before skipping
            lock (_lock) { if (generation != _generation || !_settings.VotingEnabled) return; }
            await skipAction();
        }
    }

    private Task BroadcastAsync(string eventName, object data)
        => _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync(eventName, data);
}
