using MusicBot.Services.Player;
using System.IO;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Core.Services;
using MusicBot.Data;
using MusicBot.Hubs;
using MusicBot.Services.Commands;
using MusicBot.Services.Downloader;

namespace MusicBot.Services;

/// <summary>
/// Bridges the local audio player and the queue:
/// – Subscribes to each user's ILocalPlayerService events.
/// – Updates queue progress every 500 ms from player state.
/// – Auto-advances the queue when a track ends.
/// – Exposes StartCurrentTrackAsync for CommandRouterService to trigger playback.
/// </summary>
public class PlaybackSyncService : BackgroundService
{
    private readonly UserContextManager _userContext;
    private readonly YtDlpDownloaderService _downloader;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KickVoteService _kickVote;
    private readonly PresenceCheckService _presence;
    private readonly ILogger<PlaybackSyncService> _logger;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly ILocalLibraryService _library;
    private readonly AutoQueueService _autoQueue;
    private readonly QueueSettingsService _queueSettings;
    private readonly Platforms.ChatResponseService _chatResponse;
    private readonly Commands.CommandConfigService _cmdConfig;
    private readonly TrackAnnouncementGate _announcements = new();
    private readonly TrackAnnouncementGate _history = new();

    // Tracks in-progress downloads so new SignalR clients can receive current state on connect
    public record ActiveDownload(string SpotifyUri, string Title, string Artist, int Pct);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ActiveDownload> _activeDownloads = new();
    public IReadOnlyCollection<ActiveDownload> GetActiveDownloads() => _activeDownloads.Values.ToList();

    public PlaybackSyncService(
        UserContextManager userContext,
        YtDlpDownloaderService downloader,
        IServiceScopeFactory scopeFactory,
        KickVoteService kickVote,
        PresenceCheckService presence,
        ILogger<PlaybackSyncService> logger,
        IHubContext<OverlayHub> hub,
        ILocalLibraryService library,
        AutoQueueService autoQueue,
        QueueSettingsService queueSettings,
        Platforms.ChatResponseService chatResponse,
        Commands.CommandConfigService cmdConfig)
    {
        _userContext   = userContext;
        _downloader    = downloader;
        _scopeFactory  = scopeFactory;
        _kickVote      = kickVote;
        _presence      = presence;
        _logger        = logger;
        _hub           = hub;
        _library       = library;
        _autoQueue     = autoQueue;
        _queueSettings = queueSettings;
        _chatResponse  = chatResponse;
        _cmdConfig     = cmdConfig;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wire skip delegates here to break circular dependencies
        _kickVote.SkipCurrentSong = async () =>
        {
            var services = _userContext.GetOrCreate(LocalUser.Id);
            await SkipCurrentAsync(services);
        };

        _presence.SkipCurrentSong = async () =>
        {
            var services = _userContext.GetOrCreate(LocalUser.Id);
            await SkipCurrentAsync(services);
        };

        // Broadcast download events to overlay clients and track active state
        _downloader.DownloadStarted += (trackId, title, artist) =>
        {
            _activeDownloads[trackId] = new ActiveDownload(trackId, title, artist, 0);
            _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                    .SendAsync("download:started", new { spotifyUri = trackId, title, artist });
        };

        _downloader.ProgressChanged += (trackId, pct) =>
        {
            _activeDownloads.AddOrUpdate(trackId,
                _ => new ActiveDownload(trackId, "", "", pct),
                (_, existing) => existing with { Pct = pct });
            _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                    .SendAsync("download:progress", new { spotifyUri = trackId, pct });
        };

        _downloader.DownloadCompleted += trackId =>
        {
            _activeDownloads.TryRemove(trackId, out _);
            _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                    .SendAsync("download:done", new { spotifyUri = trackId });
        };

        _downloader.DownloadFailed += (trackId, error) =>
        {
            _activeDownloads.TryRemove(trackId, out _);
            _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                    .SendAsync("download:error", new { spotifyUri = trackId, error });

            if (!IsUnrecoverableDownloadError(error)) return;

            var svc = _userContext.GetOrCreate(LocalUser.Id);

            // Current track failures are handled in StartCurrentTrackAsync; skip here
            if (svc.Queue.GetCurrentItem()?.Song.SpotifyUri == trackId) return;

            var item = svc.Queue.GetUpcoming().FirstOrDefault(i => i.Song.SpotifyUri == trackId);
            if (item == null) return;

            if (item.IsPlaylistItem)
            {
                // Background items are auto-managed — remove silently
                svc.Queue.RemoveByUri(trackId);
                return;
            }

            // User-requested: search for alternative asynchronously
            _ = Task.Run(async () =>
                await TryApplyAlternativeOrMarkFailedAsync(svc, trackId, item.Song.Title, item.Song.Artist));
        };

        _userContext.OnUserCreated += SubscribeToUser;

        foreach (var (_, services) in _userContext.GetAllActive())
            SubscribeToUser(services.UserId, services);

        return RunSpotifySyncAsync(stoppingToken);
    }

    public bool UsesSpotify(UserServices services) => services.Spotify.IsAuthenticated
        && _queueSettings.SpotifyEnabled
        && !IsYouTube(services.Queue.GetCurrentItem()?.Song)
        && (_queueSettings.DirectSpotifyMode || (services.Player.CurrentFilePath == null && services.Queue.GetCurrentItem() == null));

    public void EnsureSpotifyAvailable(UserServices services)
    {
        if (UsesSpotify(services) && services.Spotify is Spotify.SpotifyService spotify) spotify.EnsurePlaybackAvailable();
    }

    private static bool IsYouTube(Song? song) => song?.SpotifyUri.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) == true;
    private readonly SemaphoreSlim _transitionLock = new(1, 1);
    private SpotifyPlaybackObserver _spotifyObserver = new();
    private string? _requestedSpotifyUri;
    private DateTimeOffset _requestedSpotifyAt;
    private DateTimeOffset _autoRetryAfter;
    private DateTimeOffset _transitionRetryAfter;
    private readonly TrackAnnouncementGate _interactionGate = new();

    private async Task RunSpotifySyncAsync(CancellationToken ct)
    {
        var emptyResponses = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var svc = _userContext.GetOrCreate(LocalUser.Id);
                if (UsesSpotify(svc))
                {
                    await _transitionLock.WaitAsync(ct);
                    try
                    {
                        if (!UsesSpotify(svc)) continue;
                        if (svc.SpotifyTransitioning) { await Task.Delay(250, ct); continue; }
                        if (_autoRetryAfter != default && DateTimeOffset.UtcNow >= _autoRetryAfter
                            && svc.Queue.GetCurrentItem() == null && _queueSettings.AutoQueueEnabled)
                        {
                            await TryStartAutoQueueAsync(svc);
                            continue;
                        }
                        var managed = !svc.SpotifyNeedsUserAction && (svc.Queue.GetCurrentItem() != null || svc.Queue.GetState().Upcoming.Count > 0 || (_queueSettings.AutoQueueEnabled && await _autoQueue.CountAsync() > 0));
                        var observedGeneration = Interlocked.Read(ref svc.PlaybackGeneration);
                        var observedAt = DateTimeOffset.UtcNow;
                        var observedItem = svc.Queue.GetCurrentItem()?.Id;
                        var pb = await svc.Spotify.GetPlaybackStateAsync().WaitAsync(TimeSpan.FromSeconds(3));
                        if (!UsesSpotify(svc) || svc.SpotifyTransitioning || svc.Queue.GetCurrentItem()?.Id != observedItem || observedGeneration != Interlocked.Read(ref svc.PlaybackGeneration)) continue;
                        var readyForNativeNext = false;
                        try { readyForNativeNext = await PrepareSuccessorAsync(svc, pb, observedGeneration, ct); }
                        catch (Spotify.SpotifyRateLimitException) { throw; }
                        catch (Exception ex) { _logger.LogWarning(ex, "Successor preparation deferred; current playback synchronization continues"); }
                        await ManageRepeatAsync(svc, managed && !readyForNativeNext,
                            () => UsesSpotify(svc) && observedGeneration == Interlocked.Read(ref svc.PlaybackGeneration));
                        var requestedConfirmed = pb?.IsPlaying == true && pb.MatchesTrack(_requestedSpotifyUri)
                            && svc.Queue.GetCurrentItem()?.Song.SpotifyUri == _requestedSpotifyUri;
                        if (svc.SpotifyNeedsUserAction && !svc.PlaybackPausedByUser && requestedConfirmed)
                        {
                            svc.SpotifyNeedsUserAction = false;
                            _spotifyObserver = new SpotifyPlaybackObserver();
                            _transitionRetryAfter = default;
                        }
                        var freshState = pb?.MatchesTrack(_requestedSpotifyUri) == true
                            || DateTimeOffset.UtcNow - _requestedSpotifyAt > TimeSpan.FromSeconds(5);
                        if (pb != null && !string.IsNullOrEmpty(pb.TrackUri) && freshState)
                        {
                            if (pb.VolumePercent is int volume && svc.VolumeSync.AcceptRemote(observedAt) && Math.Abs(svc.Player.Volume - volume / 100f) > 0.005f)
                                svc.Player.SetVolume(volume / 100f);
                            emptyResponses = 0;
                            var observedUri = pb.MatchesTrack(_requestedSpotifyUri) ? _requestedSpotifyUri! : pb.TrackUri;
                            var song = new Song { SpotifyUri = observedUri, Title = pb.Title ?? "", Artist = pb.Artist ?? "", CoverUrl = pb.CoverUrl ?? "", DurationMs = pb.DurationMs };
                            svc.Queue.UpdateProgress(pb.ProgressMs, pb.IsPlaying && !svc.PlaybackPausedByUser, song);
                            var upcomingRequest = svc.Queue.GetUpcoming().FirstOrDefault();
                            if (pb.IsPlaying && pb.DurationMs > 0 && pb.DurationMs - pb.ProgressMs <= _queueSettings.PresenceCheckWarningSeconds * 1000
                                && upcomingRequest is { Platform: "kick", IsPlaylistItem: false })
                                _presence.IssueWarningForNext(upcomingRequest.RequestedBy);
                            var transitioned = false;
                            if (!svc.PlaybackPausedByUser && !svc.SpotifyNeedsUserAction && freshState && DateTimeOffset.UtcNow >= _transitionRetryAfter && _spotifyObserver.ShouldAdvance(pb, DateTimeOffset.UtcNow))
                            {
                                var previous = svc.Queue.GetCurrentItem();
                                var previousState = svc.Queue.GetNowPlaying();
                                var next = svc.Queue.Advance();
                                if (next == null && _queueSettings.AutoQueueEnabled)
                                {
                                    var autoSong = await _autoQueue.GetRandomSongAsync(new[] { previous?.Song.SpotifyUri ?? observedUri });
                                    if (autoSong != null) next = svc.Queue.TryStartFallback(autoSong);
                                    if (next == null) next = svc.Queue.AdvanceIfIdle();
                                }
                                if (next != null)
                                {
                                    transitioned = true;
                                    _announcements.ResetCandidate();
                                    await StartAdvancedTrackAsync(svc, previous, previousState, next);
                                }
                                else svc.Queue.UpdateProgress(pb.ProgressMs, pb.IsPlaying && !svc.PlaybackPausedByUser, song);
                            }
                            if (!transitioned && freshState && pb.IsPlaying && !svc.PlaybackPausedByUser)
                            {
                                var item = svc.Queue.GetCurrentItem();
                                var requester = item?.Song.SpotifyUri == observedUri ? item.RequestedBy : null;
                                await ObserveHistoryAsync(item?.Song.SpotifyUri == observedUri ? item
                                    : new QueueItem { Song = song, Platform = "spotify", RequestedBy = "" }, confirmed: false);
                                await TryAnnounceTrackAsync(song, requester, confirmed: false);
                            }
                            else
                            {
                                _announcements.ResetCandidate();
                                _history.ResetCandidate();
                            }
                        }
                        else if ((pb == null || string.IsNullOrEmpty(pb.TrackUri)) && ++emptyResponses >= 2)
                        {
                            _announcements.ResetCandidate();
                            _history.ResetCandidate();
                            var last = svc.Queue.GetNowPlaying();
                            svc.Queue.UpdateProgress(last.ProgressMs, false, last.SpotifyTrack);
                        }
                        // No playback response is not an instruction to skip a requested song.
                    }
                    finally { _transitionLock.Release(); }
                }
                else
                {
                    if (svc.Spotify.IsAuthenticated) await ManageRepeatAsync(svc, false);
                    _spotifyObserver = new SpotifyPlaybackObserver();
                    _announcements.ResetCandidate();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Spotify.SpotifyRateLimitException ex)
            {
                _logger.LogWarning("Spotify synchronization waiting {Seconds} seconds for rate limit", ex.RetryAfterSeconds);
                try { await Task.Delay(TimeSpan.FromSeconds(ex.RetryAfterSeconds), ct); } catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Spotify synchronization failed");
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            }
            try { await Task.Delay(_announcements.NeedsConfirmation ? 2000 : 4000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private bool _repeatControlDenied;
    private async Task ManageRepeatAsync(UserServices services, bool enabled, Func<bool>? current = null)
    {
        if (_repeatControlDenied || current?.Invoke() == false) return;
        try { await services.Spotify.SetManagedQueueAsync(enabled, current); }
        catch (Spotify.SpotifyPlayerCommandException ex) when (ex.StatusCode == 403)
        {
            _repeatControlDenied = true;
            _logger.LogWarning("Spotify repeat control denied; continuing with prepared queue and guarded source handoff");
        }
    }

    private readonly object _handoffSync = new();
    private string? _scheduledHandoff;
    private CancellationTokenSource? _handoffCancellation;
    private DateTimeOffset _handoffDeadline;
    private int _metadataWarmBusy;
    private string? _metadataWarmUri;
    private DateTimeOffset _metadataWarmAfter;
    private async Task WarmNextMetadataAsync(UserServices services)
    {
        var next = services.Queue.GetState().Upcoming.FirstOrDefault()?.Song;
        if (next == null || !next.SpotifyUri.StartsWith("spotify:") || !services.Spotify.IsAuthenticated
            || next.SpotifyUri == _metadataWarmUri || DateTimeOffset.UtcNow < _metadataWarmAfter
            || Interlocked.CompareExchange(ref _metadataWarmBusy, 1, 0) != 0) return;
        bool Current() => !services.PlaybackPausedByUser && !services.SpotifyNeedsUserAction && services.SkipPending == 0 && !services.SpotifyTransitioning
            && services.Queue.GetState().Upcoming.FirstOrDefault()?.Song.SpotifyUri == next.SpotifyUri;
        try
        {
            if (!Current()) return;
            await services.Spotify.WarmTrackAsync(next.SpotifyUri, Current);
            if (Current()) _metadataWarmUri = next.SpotifyUri;
        }
        catch (Exception ex)
        {
            _metadataWarmAfter = DateTimeOffset.UtcNow.AddSeconds(ex is Spotify.SpotifyRateLimitException rate ? rate.RetryAfterSeconds : 15);
            _logger.LogDebug(ex, "Next-track metadata warmup deferred");
        }
        finally { Interlocked.Exchange(ref _metadataWarmBusy, 0); }
    }

    private async Task<bool> PrepareSuccessorAsync(UserServices svc, PlaybackState? playback, long generation, CancellationToken ct)
    {
        if (playback?.IsPlaying != true || playback.DurationMs <= 0 || svc.PlaybackPausedByUser || svc.SpotifyNeedsUserAction) return false;
        var now = DateTimeOffset.UtcNow;
        var remaining = playback.DurationMs - playback.EstimatedProgressMs(now);
        if (remaining > 60000 || remaining <= 0) return false;
        var owner = svc.Queue.GetCurrentItem()?.Id;
        var next = svc.Queue.GetUpcoming().FirstOrDefault()?.Song ?? PeekNextBackgroundSong(svc);
        if (next == null && (!_queueSettings.AutoQueueEnabled || await _autoQueue.CountAsync() == 0)) return false;
        var deadline = playback.SampledAt.AddMilliseconds(playback.DurationMs - playback.ProgressMs);
        var key = generation + "|" + owner + "|" + playback.TrackUri;
        CancellationTokenSource timer;
        lock (_handoffSync)
        {
            if (_scheduledHandoff == key && Math.Abs((deadline - _handoffDeadline).TotalMilliseconds) < 750) return false;
            _handoffCancellation?.Cancel();
            timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _handoffCancellation = timer; _handoffDeadline = deadline; _scheduledHandoff = key;
        }
        bool Current() => !timer.IsCancellationRequested && !svc.PlaybackPausedByUser && !svc.SpotifyNeedsUserAction
            && UsesSpotify(svc) && generation == Interlocked.Read(ref svc.PlaybackGeneration) && svc.Queue.GetCurrentItem()?.Id == owner;
        _ = WarmNextMetadataAsync(svc);
        _ = Task.Run(async () =>
        {
            var locked = false; var claimed = false;
            try
            {
                var wait = deadline - DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(500);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, timer.Token);
                await _transitionLock.WaitAsync(timer.Token); locked = true;
                if (!Current() || Interlocked.CompareExchange(ref svc.SkipPending, 1, 0) != 0) return;
                claimed = true;
                if (svc.Queue.GetUpcoming().Count == 0 && svc.Queue.GetBackgroundPlaylist().Songs.Count == 0
                    && (!_queueSettings.AutoQueueEnabled || await _autoQueue.CountAsync() == 0)) return;
                // Pause and select under the same ownership lock; the poller cannot advance between them.
                await svc.Spotify.PauseIfCurrentAsync(Current);
                if (Current()) await SkipCurrentCoreAsync(svc);
            }
            catch (OperationCanceledException) when (timer.IsCancellationRequested) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Scheduled source handoff failed"); }
            finally
            {
                if (claimed) Interlocked.Exchange(ref svc.SkipPending, 0);
                if (locked) _transitionLock.Release();
                lock (_handoffSync)
                {
                    if (ReferenceEquals(_handoffCancellation, timer)) { _scheduledHandoff = null; _handoffCancellation = null; }
                    timer.Dispose();
                }
            }
        });
        return false;
    }

    private async Task EnsureSpotifyPlayAsync(UserServices svc, Song song)
    {
        var owner = svc.Queue.GetCurrentItem()?.Id;
        var ownerGeneration = Interlocked.Read(ref svc.PlaybackGeneration);
        var spotifyUri = song.SpotifyUri;
        var cleanId = Spotify.SpotifyService.ExtractCleanTrackId(spotifyUri);
        if (string.IsNullOrEmpty(cleanId) || cleanId.Length != 22)
        {
            try
            {
                var query = $"{song.Title} {song.Artist}".Trim();
                var results = await svc.Spotify.SearchAsync(query, 10);
                var match = SongSearchMatcher.Select(query, results);
                if (match != null)
                {
                    spotifyUri = match.SpotifyUri;
                    song.SpotifyUri = spotifyUri;
                    if (string.IsNullOrEmpty(song.CoverUrl)) song.CoverUrl = match.CoverUrl;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve Spotify track for \"{Title}\"", song.Title);
            }
        }

        _requestedSpotifyUri = song.SpotifyUri;
        _requestedSpotifyAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("Direct Spotify: Requesting playback of \"{Title}\" by {Artist} ({Uri})", song.Title, song.Artist, spotifyUri);
        // Keep the repeat lease during handoff: releasing it here lets Spotify autoplay race the selected request.
        if (!await svc.Spotify.PlayIfCurrentAsync(spotifyUri, () => !svc.PlaybackPausedByUser && ownerGeneration == Interlocked.Read(ref svc.PlaybackGeneration) && UsesSpotify(svc) && svc.Queue.GetCurrentItem()?.Id == owner)) return;
        if (svc.PlaybackPausedByUser || ownerGeneration != Interlocked.Read(ref svc.PlaybackGeneration) || !UsesSpotify(svc) || svc.Queue.GetCurrentItem()?.Song.SpotifyUri != song.SpotifyUri) return;
        await ManageRepeatAsync(svc, true, () => !svc.PlaybackPausedByUser && ownerGeneration == Interlocked.Read(ref svc.PlaybackGeneration) && UsesSpotify(svc));
        // A manual transition must not be mistaken for the old track ending naturally.
        _spotifyObserver = new SpotifyPlaybackObserver();
        _transitionRetryAfter = default;
        _autoRetryAfter = default;
        _requestedSpotifyUri = song.SpotifyUri;
        _requestedSpotifyAt = DateTimeOffset.UtcNow;
        svc.Queue.UpdateProgress(0, true, song);
        await ObserveHistoryAsync(svc.Queue.GetPlayingItem()!, confirmed: true);
        _ = TryAnnounceTrackAsync(song, svc.Queue.GetCurrentItem()?.RequestedBy, confirmed: true);
    }

    private async Task StartAdvancedTrackAsync(UserServices services, QueueItem? previous, NowPlayingState previousState, QueueItem next)
    {
        try { await StartCurrentTrackAsync(services); }
        catch (Exception ex)
        {
            if (services.PlaybackPausedByUser || services.Queue.GetCurrentItem()?.Id != next.Id) return;
            _spotifyObserver = new SpotifyPlaybackObserver();
            if (next.Platform == "autoqueue" && !next.Song.SpotifyUri.StartsWith("spotify:") && ex is not Spotify.SpotifyRateLimitException && ex is not PlaybackHandoffException)
            {
                _autoQueue.DeferUnavailable(next.Song.SpotifyUri);
                if (!services.Queue.DiscardCurrent(next.Id)) return;
                _logger.LogWarning(ex, "AutoQueue transition failed for {Title}; trying another pool track", next.Song.Title);
                await TryStartAutoQueueAsync(services);
                return;
            }
            _transitionRetryAfter = DateTimeOffset.UtcNow.AddSeconds(ex is Spotify.SpotifyRateLimitException limit ? limit.RetryAfterSeconds : 15);
            if (next.Song.SpotifyUri.StartsWith("spotify:") || ex is PlaybackHandoffException)
            {
                // The accepted command may still complete. Keep its target as one pending
                // item; restoring the old current + inserting this item creates ghost duplicates.
                services.Queue.ClearSpotifyTrack();
                services.Queue.UpdateProgress(0, false);
                services.SpotifyNeedsUserAction = true;
            }
            else { services.Queue.ClearSpotifyTrack(); services.Queue.UpdateProgress(0, false); }
            await _hub.Clients.Group($"user:{services.UserId}").SendAsync("queue:download-failed",
                new { title = next.Song.Title, artist = next.Song.Artist, reason = "Spotify geçişi doğrulanamadı; şarkı sırada korundu. " + ex.Message });
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        lock (_handoffSync) _handoffCancellation?.Cancel();
        foreach (var (_, services) in _userContext.GetAllActive())
        {
            try { if (services.Spotify.IsAuthenticated) await ManageRepeatAsync(services, false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Spotify repeat restoration deferred until reconnect"); }
            try { await services.Player.StopAsync(); }
            catch { /* ignore errors on shutdown */ }
        }
        await base.StopAsync(ct);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Skips the current track, deletes its file when in temp mode, then starts the next.
    /// Use this instead of calling Queue.Skip() + StartCurrentTrackAsync() directly.
    /// </summary>
    public bool RequestSkip(UserServices services, string requester, string platform, CommandDefinition? definition = null)
    {
        if (Interlocked.CompareExchange(ref services.SkipPending, 1, 0) != 0) return false;
        var operation = SkipClaimedAsync(services);
        _ = ReportSkipFailureAsync(operation, requester, platform, definition);
        return true;
    }
    private async Task ReportSkipFailureAsync(Task operation, string requester, string platform, CommandDefinition? definition)
    {
        try { await operation; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Requested skip could not finish");
            ActivityJournal.Record("musick", "Şarkı atlama", requester, ex.Message, false);
            var fallback = definition?.Templates.TryGetValue("success", out var successReply) == true && string.IsNullOrWhiteSpace(successReply)
                ? "" : "Şarkı geçişi tamamlanamadı. {error}";
            var text = (definition?.GetTemplate("failure", fallback) ?? fallback)
                .Replace("{user}", requester).Replace("{error}", ex.Message);
            if (platform == "kick" && !string.IsNullOrWhiteSpace(text)) await _chatResponse.SendChatMessageAsync(requester, text, platform);
        }
    }
    public async Task SkipCurrentAsync(UserServices services) => await TrySkipCurrentAsync(services);
    public async Task<bool> TrySkipCurrentAsync(UserServices services)
    {
        if (Interlocked.CompareExchange(ref services.SkipPending, 1, 0) != 0) return false;
        await SkipClaimedAsync(services);
        return true;
    }
    private async Task SkipClaimedAsync(UserServices services)
    {
        var expected = services.Queue.GetCurrentItem()?.Id;
        var external = services.Queue.GetNowPlaying().SpotifyTrack?.SpotifyUri;
        services.PlaybackPausedByUser = false;
        Interlocked.Increment(ref services.PlaybackGeneration);
        _autoRetryAfter = default;
        var acquired = false;
        try
        {
            await _transitionLock.WaitAsync(); acquired = true;
            // A natural end may already have fulfilled this skip while the lock was busy.
            if (services.Queue.GetCurrentItem()?.Id != expected) return;
            if (expected == null && external != services.Queue.GetNowPlaying().SpotifyTrack?.SpotifyUri) return;
            EnsureSpotifyAvailable(services);
            services.SpotifyNeedsUserAction = false;
            await SkipCurrentCoreAsync(services);
        }
        catch
        {
            if (UsesSpotify(services)) services.SpotifyNeedsUserAction = true;
            throw;
        }
        finally { if (acquired) _transitionLock.Release(); Interlocked.Exchange(ref services.SkipPending, 0); }
    }
    public async Task PauseCurrentAsync(UserServices services)
    {
        services.PlaybackPausedByUser = true;
        var generation = Interlocked.Increment(ref services.PlaybackGeneration);
        _autoRetryAfter = default;
        _kickVote.CancelVote(); _presence.CancelRunningCheck();
        var state = services.Queue.GetNowPlaying();
        services.Queue.UpdateProgress(state.ProgressMs, false, state.SpotifyTrack);
        if (services.Player.CurrentFilePath != null) await services.Player.PauseAsync();
        if (services.Spotify.IsAuthenticated)
        {
            if (services.Spotify is Spotify.SpotifyService spotify)
                await spotify.PauseIfCurrentAsync(() => services.PlaybackPausedByUser && generation == Interlocked.Read(ref services.PlaybackGeneration));
            else await services.Spotify.PauseAsync();
        }
    }

    private async Task SkipCurrentCoreAsync(UserServices services)
    {
        _kickVote.CancelVote();
        _presence.CancelRunningCheck();
        if (UsesSpotify(services))
        {
            var previous = services.Queue.GetCurrentItem();
            var previousState = services.Queue.GetNowPlaying();
            var nextTrack = services.Queue.Advance();
            if (nextTrack != null)
            {
                await StartAdvancedTrackAsync(services, previous, previousState, nextTrack);
            }
            else
            {
                if (!_queueSettings.AutoQueueEnabled || !await TryStartAutoQueueAsync(services))
                {
                    // Queue is empty: NEVER pause Spotify.
                    // Call SkipAsync() so Spotify advances to its own next song in the user's playlist/radio!
                    await ManageRepeatAsync(services, false);
                    await services.Spotify.SkipAsync();
                }
            }
            return;
        }

        var current = services.Queue.GetCurrentItem();
        _logger.LogInformation(
            "SkipCurrentAsync: title={Title} localPath={Path} isPlaylist={IsPlaylist} saveDownloads={Save}",
            current?.Song.Title ?? "(null)",
            current?.Song.LocalFilePath ?? "(null)",
            current?.IsPlaylistItem,
            _queueSettings.SaveDownloads);

        services.Queue.Skip();
        await services.Player.StopAsync();
        if (services.Queue.GetCurrentItem() == null)
        {
            if (_queueSettings.AutoQueueEnabled && await TryStartAutoQueueAsync(services)) return;
            if (_queueSettings.SpotifyEnabled && services.Spotify.IsAuthenticated) await services.Spotify.SkipAsync();
            return;
        }
        await StartCurrentTrackAsync(services);

        if (!_queueSettings.SaveDownloads && current?.Song.LocalFilePath != null)
        {
            _logger.LogInformation("Deleting skipped track \"{Title}\" ({Path})", current.Song.Title, current.Song.LocalFilePath);
            _downloader.InvalidateCachedDownload(current.Song.SpotifyUri);
            await _library.DeleteByTrackIdAsync(current.Song.SpotifyUri);
        }

        // Pre-warm the next two songs after skip to avoid gap
        var upcoming = services.Queue.GetUpcoming();
        var next = upcoming.FirstOrDefault();
        if (next != null)
            _ = Task.Run(() => PrewarmDownloadAsync(next.Song));
        var nextNext = upcoming.ElementAtOrDefault(1);
        if (nextNext != null)
            _ = Task.Run(() => PrewarmDownloadAsync(nextNext.Song));
    }

    /// <summary>
    /// Called by CommandRouterService after a play/skip command.
    /// Plays the current queue item, waiting for download if needed.
    /// </summary>
    public async Task StartCurrentTrackAsync(UserServices services, bool userInitiated = false)
    {
        if (userInitiated) { services.SpotifyNeedsUserAction = false; services.PlaybackPausedByUser = false; }
        if (services.SpotifyNeedsUserAction && services.Queue.GetCurrentItem() != null)
            throw new InvalidOperationException("Spotify geçişi durduruldu; seçilen şarkı korundu. Devam etmek için Çal düğmesine basın.");
        _kickVote.CancelVote();
        var selectedItem = services.Queue.GetCurrentItem();
        _presence.CancelRunningCheck();
        var selected = selectedItem?.Song;
        if (selected != null && IsYouTube(selected) && !_queueSettings.YouTubeEnabled) throw new InvalidOperationException("YouTube oynatma ayarlardan kapatılmış.");
        if (selected != null && selected.SpotifyUri.StartsWith("spotify:") && !_queueSettings.SpotifyEnabled) throw new InvalidOperationException("Spotify oynatma ayarlardan kapatılmış.");
        var generation = Interlocked.Increment(ref services.PlaybackGeneration);
        services.SpotifyTransitioning = false;
        services.LocalPlaybackItemId = null;
        if (UsesSpotify(services))
        {
            EnsureSpotifyAvailable(services);
            var cur = services.Queue.GetCurrentItem();
            if (cur != null)
            {
                services.SpotifyTransitioning = true;
                services.Queue.UpdateProgress(0, false);
                try
                {
                    await services.Player.StopAsync();
                    if (generation != Interlocked.Read(ref services.PlaybackGeneration) || services.Queue.GetCurrentItem()?.Id != cur.Id) return;
                    await EnsureSpotifyPlayAsync(services, cur.Song);
                }
                catch
                {
                    if (!services.PlaybackPausedByUser && generation == Interlocked.Read(ref services.PlaybackGeneration) && services.Queue.GetCurrentItem()?.Id == cur.Id)
                    {
                        services.SpotifyNeedsUserAction = true;
                        services.Queue.UpdateProgress(0, false);
                    }
                    throw;
                }
                finally { if (generation == Interlocked.Read(ref services.PlaybackGeneration)) services.SpotifyTransitioning = false; }
            }
            return;
        }

        var current = services.Queue.GetCurrentItem();
        if (current == null)
        {
            _presence.CancelCheck();
            await services.Player.StopAsync();
            return;
        }

        services.Queue.ClearSpotifyTrack();
        services.Queue.UpdateProgress(0, false);
        await services.Player.StopAsync();
        if (services.Spotify.IsAuthenticated && _queueSettings.DirectSpotifyMode)
        {
            try { await services.Spotify.PauseIfCurrentAsync(() => services.Queue.GetCurrentItem()?.Id == current.Id && generation == Interlocked.Read(ref services.PlaybackGeneration)); }
            catch (Exception ex)
            {
                if (services.Queue.GetCurrentItem()?.Id != current.Id || generation != Interlocked.Read(ref services.PlaybackGeneration)) return;
                services.SpotifyNeedsUserAction = true;
                _logger.LogWarning(ex,"Source handoff stopped because Spotify could not be paused");
                if (ex is Spotify.SpotifyRateLimitException) throw;
                throw new PlaybackHandoffException(ex);
            }
        }
        try
        {
            if (IsYouTube(current.Song))
            {
                await _downloader.ValidateMusicVideoAsync(current.Song.SpotifyUri);
                if (services.Queue.GetCurrentItem()?.Id != current.Id || generation != Interlocked.Read(ref services.PlaybackGeneration)) return;
                services.Queue.MarkDownloadError(current.Song.SpotifyUri, null);
                current.Song.LocalFilePath = null;
                services.LocalPlaybackItemId = current.Id;
                await services.Player.PlayAsync(current.Song.SpotifyUri);
                return;
            }
            await EnsureLocalFileAsync(current.Song);
            if (services.Queue.GetCurrentItem()?.Id != current.Id || generation != Interlocked.Read(ref services.PlaybackGeneration)) return;
            services.LocalPlaybackItemId = current.Id;
            await services.Player.PlayAsync(current.Song.LocalFilePath!);
            services.Queue.UpdateProgress(services.Player.PositionMs, services.Player.IsPlaying);
            await ObserveHistoryAsync(current, confirmed: true);
            await TryAnnounceTrackAsync(current.Song, current.RequestedBy);
        }
        catch (Exception ex) when (IsYouTube(current.Song))
        {
            if (services.Queue.GetCurrentItem()?.Id != current.Id || generation != Interlocked.Read(ref services.PlaybackGeneration)) return;
            services.Queue.MarkDownloadError(current.Song.SpotifyUri, ex.Message);
            services.Queue.UpdateProgress(0, false);
            await _hub.Clients.Group($"user:{services.UserId}").SendAsync("queue:download-failed", new { title = current.Song.Title, artist = current.Song.Artist, reason = ex.Message });
            throw;
        }
        catch (Exception ex) when (IsRateLimitError(ex))
        {
            if (services.Queue.GetCurrentItem()?.Id != current.Id || generation != Interlocked.Read(ref services.PlaybackGeneration)) return;
            // Rate-limited after exhausting retries — skip the song.
            // DownloadCoreAsync already waited up to 165s; don't add more delay here
            // because the queue may have already advanced during that time.
            _logger.LogWarning("Skipping \"{Title}\" after rate-limit retries exhausted", current.Song.Title);
            _downloader.InvalidateCachedDownload(current.Song.SpotifyUri);
            services.Queue.Skip();
            _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                    .SendAsync("queue:download-failed", new { title = current.Song.Title, artist = current.Song.Artist, reason = ex.Message });
            await StartCurrentTrackAsync(services);
            return;
        }
        catch (Exception ex) when (IsUnrecoverableDownloadError(ex))
        {
            if (services.Queue.GetCurrentItem()?.Id != current.Id || generation != Interlocked.Read(ref services.PlaybackGeneration)) return;
            var origUri    = current.Song.SpotifyUri;
            var origTitle  = current.Song.Title;
            var origArtist = current.Song.Artist;
            _logger.LogWarning("Song unavailable: \"{Title}\" — searching for alternative", origTitle);
            _downloader.InvalidateCachedDownload(origUri);

            bool altFound = false;
            try
            {
                var alt = await _downloader.SearchBestMatchAsync($"{origTitle} {origArtist}".Trim());
                if (services.Queue.GetCurrentItem()?.Id != current.Id || generation != Interlocked.Read(ref services.PlaybackGeneration)) return;
                if (alt != null && alt.SpotifyUri != origUri)
                {
                    _logger.LogInformation("Alternative found for \"{Title}\": {NewUri}", origTitle, alt.SpotifyUri);
                    services.Queue.UpdateSongForAlternative(origUri, alt);
                    // current.Song is now alt (same object reference as _currentItem.Song)
                    await EnsureLocalFileAsync(current.Song);
                    if (services.Queue.GetCurrentItem()?.Id != current.Id || generation != Interlocked.Read(ref services.PlaybackGeneration)) return;
                    services.LocalPlaybackItemId = current.Id;
                    await services.Player.PlayAsync(current.Song.LocalFilePath!);
                    services.Queue.UpdateProgress(services.Player.PositionMs, services.Player.IsPlaying);
                    await ObserveHistoryAsync(current, confirmed: true);
                    await TryAnnounceTrackAsync(current.Song, current.RequestedBy);

                    altFound = true;
                }
            }
            catch (Exception altEx)
            {
                _logger.LogWarning(altEx, "Alternative also failed for \"{Title}\"", origTitle);
                _downloader.InvalidateCachedDownload(current.Song.SpotifyUri);
            }

            if (!altFound)
            {
                if (services.Queue.GetCurrentItem()?.Id != current.Id || generation != Interlocked.Read(ref services.PlaybackGeneration)) return;
                services.Queue.Skip();
                _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                        .SendAsync("queue:download-failed", new { title = origTitle, artist = origArtist, reason = "No disponible" });
                await StartCurrentTrackAsync(services);
            }
            return;
        }
        catch (Exception ex)
        {
            if (services.Queue.GetCurrentItem()?.Id != current.Id || generation != Interlocked.Read(ref services.PlaybackGeneration)) return;
            _logger.LogError(ex, "Playback failed for \"{Title}\" — removing from queue", current.Song.Title);
            // Delete corrupt/unplayable file and evict the cached download task so it re-downloads next time
            _downloader.InvalidateCachedDownload(current.Song.SpotifyUri);
            if (current.Song.LocalFilePath != null && File.Exists(current.Song.LocalFilePath))
            {
                try { File.Delete(current.Song.LocalFilePath); } catch { }
                await _library.DeleteByTrackIdAsync(current.Song.SpotifyUri);
            }
            if (services.Queue.GetCurrentItem()?.Id != current.Id || generation != Interlocked.Read(ref services.PlaybackGeneration)) return;
            services.Queue.Skip();
            _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                    .SendAsync("queue:download-failed", new { title = current.Song.Title, artist = current.Song.Artist, reason = ex.Message });
            await StartCurrentTrackAsync(services);
            return;
        }

        _logger.LogInformation("Playing \"{Title}\" by {Artist} for {UserId}",
            current.Song.Title, current.Song.Artist, services.UserId);

        // Start kick-vote and presence check for this track
        // (skip presence check for songs added directly from the dashboard)

    }

    // ── Player event subscriptions ────────────────────────────────────────────

    private void SubscribeToUser(Guid userId, UserServices services)
    {
        services.Queue.OnQueueUpdated += state => { _ = WarmNextMetadataAsync(services); };
        services.Queue.OnSongRemoved += removed =>
        {
            _logger.LogInformation("OnSongRemoved: InvalidateCachedDownload for {Uri} (\"{Title}\")",
                removed.Song.SpotifyUri, removed.Song.Title);
            _downloader.InvalidateCachedDownload(removed.Song.SpotifyUri);

            if (!_queueSettings.SaveDownloads && removed.Song.LocalFilePath != null)
            {
                try
                {
                    if (File.Exists(removed.Song.LocalFilePath))
                        File.Delete(removed.Song.LocalFilePath);
                }
                catch { }
                _ = _library.DeleteByTrackIdAsync(removed.Song.SpotifyUri);
            }
        };

        services.Player.OnStateChanged += state =>
        {
            var owner = services.Queue.GetCurrentItem();
            if (!PlaybackOwnership.AcceptLocalState(owner, services.LocalPlaybackItemId, state, UsesSpotify(services), services.Player.CurrentFilePath)) return;
            if (services.Player.CurrentFilePath != null)
            {
                var current = services.Queue.GetCurrentItem();
                if (current != null && state.DurationMs > 0) current.Song.DurationMs = state.DurationMs;
                services.Queue.UpdateProgress(state.PositionMs, state.IsPlaying && !services.PlaybackPausedByUser);
                if (services.Player.IsBrowserPlayback && state.IsPlaying && current != null)
                {
                    _ = ConfirmBrowserTrackAsync(services, current, services.Player.PlaybackVersion);
                    _ = WarmNextMetadataAsync(services);
                }
            }

            // 60 s remaining: warn the next requester and pre-warm the next two songs' downloads
            if (state.IsPlaying && !services.PlaybackPausedByUser && state.DurationMs > 0)
            {
                var remaining = state.DurationMs - state.PositionMs;
                if (remaining <= 60_000)
                {
                    var upcoming = services.Queue.GetUpcoming();
                    var next = upcoming.FirstOrDefault();
                    if (next != null)
                    {
                        _presence.IssueWarningForNext(next.RequestedBy);
                        _ = Task.Run(() => PrewarmDownloadAsync(next.Song));

                        // Also pre-warm the song after next to build a buffer
                        var nextNext = upcoming.ElementAtOrDefault(1);
                        if (nextNext != null)
                            _ = Task.Run(() => PrewarmDownloadAsync(nextNext.Song));
                    }
                    else
                    {
                        // No user requests; pre-warm the next background playlist song
                        var bgNext = PeekNextBackgroundSong(services);
                        if (bgNext != null)
                            _ = Task.Run(() => PrewarmDownloadAsync(bgNext));
                    }
                }
            }
        };

        services.Player.OnTrackEnded += (file, version) =>
        {
            var ended = services.Queue.GetCurrentItem();
            if (services.PlaybackPausedByUser || ended?.Id != services.LocalPlaybackItemId || UsesSpotify(services) || services.Player.CurrentFilePath != file || version != services.Player.PlaybackVersion) return;
            _ = Task.Run(async () =>
            {
                await _transitionLock.WaitAsync();
                try
                {
                    if (services.PlaybackPausedByUser || ended == null || services.Queue.GetCurrentItem()?.Id != ended.Id || version != services.Player.PlaybackVersion) return;
                    var next = services.Queue.Advance();
                    if (next != null) await StartCurrentTrackAsync(services);
                    else if (!await TryStartAutoQueueAsync(services, excludeUris: new[] { ended.Song.SpotifyUri }))
                    {
                        await services.Player.StopAsync();
                        if (_queueSettings.SpotifyEnabled && services.Spotify.IsAuthenticated)
                        {
                            await ManageRepeatAsync(services, false);
                            await services.Spotify.SkipAsync();
                        }
                    }
                    if (!_queueSettings.SaveDownloads && ended.Song.LocalFilePath != null
                        && services.Queue.GetCurrentItem()?.Song.SpotifyUri != ended.Song.SpotifyUri)
                    {
                        _downloader.InvalidateCachedDownload(ended.Song.SpotifyUri);
                        await _library.DeleteByTrackIdAsync(ended.Song.SpotifyUri);
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Track transition failed"); }
                finally { _transitionLock.Release(); }
            });
        };
    }

    /// <summary>
    /// Picks a random song from the auto-queue pool and starts playback.
    /// Retries up to <paramref name="attemptsLeft"/> times on download failure.
    /// Failed pool songs are deferred temporarily; viewer requests are preserved.
    /// </summary>
    public async Task<bool> TryStartAutoQueueAsync(UserServices services, int attemptsLeft = 3, IReadOnlyCollection<string>? excludeUris = null)
    {
        if (!_queueSettings.AutoQueueEnabled || services.PlaybackPausedByUser || services.SpotifyNeedsUserAction) return false;
        for (var attempt = 0; attempt < attemptsLeft; attempt++)
        {
            // A late recovery must never replace playback that has already changed.
            if (services.Queue.GetCurrentItem() != null) return false;
            var previousState = services.Queue.GetNowPlaying();
            QueueItem? item;
            if (services.Queue.GetUpcoming().Count > 0 || services.Queue.GetBackgroundPlaylist().Songs.Count > 0) item = services.Queue.AdvanceIfIdle();
            else
            {
                var candidate = await _autoQueue.GetRandomSongAsync(excludeUris);
                item = candidate == null ? null : services.Queue.TryStartFallback(candidate);
                item ??= services.Queue.AdvanceIfIdle();
                if (item == null) return false;
            }
            if (item == null) continue;
            var song = item.Song;
            try
            {
                await StartCurrentTrackAsync(services);
                _autoRetryAfter = default;
                return services.Queue.GetCurrentItem() != null;
            }
            catch (Exception ex) when (song.SpotifyUri.StartsWith("spotify:") || ex is PlaybackHandoffException)
            {
                if (!services.PlaybackPausedByUser && services.Queue.GetCurrentItem()?.Id == item.Id)
                {
                    services.SpotifyNeedsUserAction = true;
                    services.Queue.UpdateProgress(0, false);
                }
                _autoRetryAfter = default;
                throw;
            }
            catch (Spotify.SpotifyRateLimitException)
            {
                if (!services.PlaybackPausedByUser && services.Queue.GetCurrentItem()?.Id == item.Id)
                    services.Queue.UpdateProgress(0, false);
                // The selected song is valid: rate limiting must not discard it or try the pool again.
                _autoRetryAfter = default;
                throw;
            }
            catch (Exception ex)
            {
                if (item.Platform != "autoqueue")
                {
                    services.Queue.RestoreFailedAdvance(null, item, previousState);
                    _spotifyObserver = new SpotifyPlaybackObserver();
                    _transitionRetryAfter = DateTimeOffset.UtcNow.AddSeconds(15);
                    throw;
                }
                if (services.PlaybackPausedByUser || services.Queue.GetCurrentItem()?.Id != item.Id) return false;
                _logger.LogWarning(ex, "AutoQueue could not play {Title}", song.Title);
                ActivityJournal.Record("musick", "Otomatik Kuyruk", "", $"{song.Title}: {ex.Message}", false);
                _autoQueue.DeferUnavailable(song.SpotifyUri);
                _spotifyObserver = new SpotifyPlaybackObserver();
                if (!services.Queue.DiscardCurrent(item.Id)) return false;
            }
        }
        // Temporary Spotify/device errors should recover without restarting the application.
        _autoRetryAfter = default;
        return false;
    }

    private static bool IsUnrecoverableDownloadError(string msg) =>
        msg.Contains("Private video")
        || msg.Contains("Video unavailable")
        || msg.Contains("This video is not available")
        || msg.Contains("has been removed")
        || msg.Contains("account associated with this video has been terminated");

    private static bool IsUnrecoverableDownloadError(Exception ex) => IsUnrecoverableDownloadError(ex.Message);

    private static bool IsRateLimitError(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("429") || msg.Contains("Too Many Requests");
    }

    /// <summary>
    /// For an upcoming user-requested item whose download failed permanently:
    /// searches for an alternative video and updates the queue item.
    /// If no alternative is found, marks the item as failed so the UI can show it.
    /// </summary>
    private async Task TryApplyAlternativeOrMarkFailedAsync(
        UserServices services, string failedUri, string title, string artist)
    {
        try
        {
            var alt = await _downloader.SearchBestMatchAsync($"{title} {artist}".Trim());
            if (alt != null && alt.SpotifyUri != failedUri && services.Queue.UpdateSongForAlternative(failedUri, alt))
            {
                _logger.LogInformation(
                    "Found alternative for unavailable \"{Title}\": {NewUri}", title, alt.SpotifyUri);
                _downloader.StartDownload(alt);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alternative search failed for \"{Title}\"", title);
        }

        // No alternative — mark the item so the frontend shows the error badge
        services.Queue.MarkDownloadError(failedUri, "No disponible");
        _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                .SendAsync("queue:download-failed", new { title, artist, reason = "No disponible" });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a background download so the file is ready before the current track ends.
    /// GetOrStartDownloadAsync deduplicates concurrent calls, so this is safe to call repeatedly.
    /// </summary>
    private async Task PrewarmDownloadAsync(Song song)
    {
        // The iframe streams ordinary YouTube songs; downloading those too wastes bandwidth and ffmpeg work.
        if (IsYouTube(song)) return;
        if (song.SpotifyUri.StartsWith("spotify:") && _userContext.GetOrCreate(LocalUser.Id).Spotify.IsAuthenticated) return;
        if (song.LocalFilePath != null && File.Exists(song.LocalFilePath)
            && new FileInfo(song.LocalFilePath).Length > 100_000)
            return;
        try
        {
            await _downloader.GetOrStartDownloadAsync(song);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pre-warm download failed for \"{Title}\" — will retry at track end", song.Title);
        }
    }

    /// <summary>
    /// Returns the next song from the background playlist without advancing the index.
    /// Returns null if there are user requests pending (they take priority) or no background playlist.
    /// </summary>
    private static Song? PeekNextBackgroundSong(UserServices services)
    {
        if (services.Queue.GetUpcoming().Count > 0) return null;
        var (songs, index) = services.Queue.GetBackgroundPlaylist();
        if (songs.Count == 0) return null;
        return songs[index % songs.Count];
    }

    private Task ObserveHistoryAsync(QueueItem item, bool confirmed)
    {
        StartConfirmedInteractions(item, confirmed);
        if (!_history.TryAccept(item.Song.SpotifyUri, confirmed)) return Task.CompletedTask;
        ActivityJournal.Record(item.Platform == "spotify" || item.Song.SpotifyUri.StartsWith("spotify:") ? "spotify" : "musick", "Çalıyor", item.RequestedBy, $"{item.Song.Title} — {item.Song.Artist}");
        return RecordHistoryAsync(item);
    }

    private void StartConfirmedInteractions(QueueItem item, bool confirmed)
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var state = services.Queue.GetNowPlaying();
        if (!state.IsPlaying || (state.SpotifyTrack ?? state.Item?.Song)?.SpotifyUri != item.Song.SpotifyUri) return;
        var key = item.Song.SpotifyUri + "|" + (services.Queue.GetCurrentItem()?.Id == item.Id ? item.Id : "external");
        if (!_interactionGate.TryAccept(key, confirmed)) return;
        _presence.PrepareForTrack(item is { Platform: "kick", IsPlaylistItem: false } ? item.RequestedBy : null);
        bool StillPlaying() => (services.Queue.GetNowPlaying().SpotifyTrack ?? services.Queue.GetCurrentItem()?.Song)?.SpotifyUri == item.Song.SpotifyUri
            && (key.EndsWith("|external") || services.Queue.GetCurrentItem()?.Id == item.Id);
        _kickVote.SkipCurrentSong = async () => { if (StillPlaying()) await SkipCurrentAsync(services); };
        _presence.SkipCurrentSong = async () => { if (StillPlaying()) await SkipCurrentAsync(services); };
        _ = _kickVote.StartVoteAsync(item.Song);
        if (item.Platform == "kick" && !item.IsPlaylistItem && !string.IsNullOrWhiteSpace(item.RequestedBy))
            _ = _presence.StartSongCheckAsync(item.RequestedBy);
    }

    public async Task RecoverYouTubePlaybackAsync(UserServices services, long expectedVersion, int error)
    {
        await _transitionLock.WaitAsync();
        try
        {
            var item = services.Queue.GetCurrentItem();
            if (item == null || !IsYouTube(item.Song) || services.Player.PlaybackVersion != expectedVersion || !_queueSettings.YouTubeEnabled) return;
            Interlocked.Increment(ref services.PlaybackGeneration);
            await services.Player.StopAsync();
            if (services.Queue.GetCurrentItem()?.Id != item.Id) return;
            var reason = $"YouTube çevrimiçi oynatma hatası ({error}). Bu video gömülü oynatmaya kapalı veya şu anda erişilemiyor. Yeniden deneyin ya da sıradakine geçin.";
            services.Queue.UpdateProgress(0, false);
            services.Queue.MarkDownloadError(item.Song.SpotifyUri, reason);
            ActivityJournal.Record("youtube", "Oynatma hatası", item.RequestedBy, reason, false);
            await _hub.Clients.Group($"user:{services.UserId}").SendAsync("queue:download-failed", new { title = item.Song.Title, artist = item.Song.Artist, reason });
        }
        finally { _transitionLock.Release(); }
    }

    private async Task RecordHistoryAsync(QueueItem item)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            db.PlayedSongs.Add(new PlayedSong
            {
                TrackId     = item.Song.SpotifyUri ?? "",
                Title       = item.Song.Title,
                Artist      = item.Song.Artist,
                CoverUrl    = item.Song.CoverUrl,
                DurationMs  = item.Song.DurationMs,
                RequestedBy = item.RequestedBy,
                Platform    = item.Platform,
                PlayedAt    = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording history");
        }
    }

    /// <summary>Ensures song.LocalFilePath is set, waiting for an in-progress download if necessary.</summary>
    private async Task EnsureLocalFileAsync(Song song)
    {
        if (song.LocalFilePath != null && File.Exists(song.LocalFilePath)
            && new FileInfo(song.LocalFilePath).Length > 100_000)
            return;

        _logger.LogInformation("Waiting for download of \"{Title}\"...", song.Title);
        song.LocalFilePath = await _downloader.GetOrStartDownloadAsync(song);
    }

    // ── Enriched queue for overlays ───────────────────────────────────────────
    private async Task ConfirmBrowserTrackAsync(UserServices services, QueueItem item, long version)
    {
        if (services.Player.PlaybackVersion != version || services.Queue.GetCurrentItem()?.Id != item.Id) return;
        await ObserveHistoryAsync(item, confirmed: true);
        if (services.Player.PlaybackVersion != version || services.Queue.GetCurrentItem()?.Id != item.Id) return;
        await TryAnnounceTrackAsync(item.Song, item.RequestedBy, confirmed: true);
    }

    public Task<SpotifyQueueState?> GetEnrichedQueueAsync(UserServices services)
    {
        var state    = services.Queue.GetState();
        var upcoming = state.Upcoming.Select(i => new Song
        {
            SpotifyUri  = i.Song.SpotifyUri,
            Title       = i.Song.Title,
            Artist      = i.Song.Artist,
            CoverUrl    = i.Song.CoverUrl,
            DurationMs  = i.Song.DurationMs,
            RequestedBy = i.RequestedBy,
            Platform    = i.Platform,
        }).ToList();

        var current = state.NowPlaying.Item;
        var result  = new SpotifyQueueState
        {
            CurrentlyPlaying = current == null ? null : new Song
            {
                SpotifyUri  = current.Song.SpotifyUri,
                Title       = current.Song.Title,
                Artist      = current.Song.Artist,
                CoverUrl    = current.Song.CoverUrl,
                DurationMs  = current.Song.DurationMs,
                RequestedBy = current.RequestedBy,
                Platform    = current.Platform,
            },
            Queue = upcoming,
        };
        return Task.FromResult<SpotifyQueueState?>(result);
    }

    private DateTimeOffset _announceRetryAfter;
    private async Task TryAnnounceTrackAsync(Song song, string? requester, bool confirmed = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(song.SpotifyUri) && string.IsNullOrWhiteSpace(song.Title)) return;
            var active = _userContext.GetOrCreate(LocalUser.Id).Queue.GetNowPlaying();
            var actual = active.SpotifyTrack ?? active.Item?.Song;
            if (!active.IsPlaying || actual?.SpotifyUri != song.SpotifyUri) return;
            if (!_chatResponse.HasSender("kick") || DateTimeOffset.UtcNow < _announceRetryAfter) return;
            // A Spotify radio track must not inherit the identity of an unrelated pending request.
            var owner = active.Item?.Song.SpotifyUri == song.SpotifyUri ? active.Item : null;
            if (owner == null || owner.IsPlaylistItem) requester = null;
            var uriKey = (!string.IsNullOrWhiteSpace(song.SpotifyUri) ? song.SpotifyUri : song.Title) + "|" + owner?.Id;
            var cfg = _cmdConfig.Current;
            if (!cfg.Commands.TryGetValue("announce", out var announceDef) || !announceDef.Enabled) return;
            if (!_announcements.TryAccept(uriKey, confirmed)) return;

            string template;
            if (!string.IsNullOrWhiteSpace(requester) && !string.Equals(requester, "Yayıncı", StringComparison.OrdinalIgnoreCase))
            {
                template = announceDef.GetTemplate("success", "🎵 Şimdi Çalıyor: \"{song}\" - {artist} (İsteyen: @{requester})");
            }
            else
            {
                template = announceDef.GetTemplate("success_no_requester", "🎵 Şimdi Çalıyor: \"{song}\" - {artist}");
            }

            var msg = CommandTemplateEngine.Render(template, new TemplateContext
            {
                Song = song.Title,
                Artist = song.Artist,
                Requester = requester ?? "",
                User = requester ?? ""
            });

            if (!string.IsNullOrWhiteSpace(msg))
            {
                _logger.LogInformation("Track change announcement: {Message}", msg);
                if (!await _chatResponse.SendConfirmedAsync("kick", msg, () => { var now = _userContext.GetOrCreate(LocalUser.Id).Queue.GetNowPlaying(); return now.IsPlaying && (now.SpotifyTrack ?? now.Item?.Song)?.SpotifyUri == song.SpotifyUri && (owner == null || now.Item?.Id == owner.Id); })) { _announcements.Release(uriKey); _announceRetryAfter = DateTimeOffset.UtcNow.AddSeconds(5); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send track change announcement to chat");
        }
    }
}
