using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Data;
using MusicBot.Hubs;
using MusicBot.Services.Security;

namespace MusicBot.Services.Spotify;

public class SpotifyService : ISpotifyService
{
    private readonly SpotifySettings _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SpotifyService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly Guid _userId;

    private readonly SpotifyRateLimit _rateLimit;
    private readonly SemaphoreSlim _repeatLock = new(1, 1);
    private readonly string? _repeatStatePath;
    private string? _repeatRestore;
    private bool _managedRepeatActive;

    private readonly SemaphoreSlim _apiRequestLock = new(1, 1);
    private DateTimeOffset _lastApiRequestAt;
    private readonly SpotifySnapshot<PlaybackState?> _playbackSnapshot = new();
    private readonly SpotifySnapshot<List<SpotifyDevice>> _deviceSnapshot = new();
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _volumeLock = new(1, 1);
    private int _volumeVersion;
    private int? _lastVolumeSent;
    private DateTimeOffset _lastVolumeSentAt;
    private async Task<HttpResponseMessage> SendSpotifyAsync(HttpRequestMessage request, CancellationToken cancellationToken = default, Func<bool>? isCurrent = null)
    {
        if (isCurrent?.Invoke() == false) return new HttpResponseMessage(System.Net.HttpStatusCode.ResetContent);
        var spotifyApi = request.RequestUri?.Host == "api.spotify.com";
        var changesPlayer = spotifyApi && request.Method != HttpMethod.Get && request.RequestUri!.AbsolutePath.StartsWith("/v1/me/player", StringComparison.Ordinal);
        if (spotifyApi)
        {
            _rateLimit.ThrowIfLimited();
            await _apiRequestLock.WaitAsync(cancellationToken);
        }
        try
        {
            if (spotifyApi)
            {
                // Shared by playback, artwork, search and bulk imports: no concurrent bursts.
                _rateLimit.ThrowIfLimited();
                var delay = TimeSpan.FromMilliseconds(500) - (DateTimeOffset.UtcNow - _lastApiRequestAt);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                _rateLimit.ThrowIfLimited();
                _lastApiRequestAt = DateTimeOffset.UtcNow;
            }
            if (isCurrent?.Invoke() == false) return new HttpResponseMessage(System.Net.HttpStatusCode.ResetContent);
            if (changesPlayer) { _playbackSnapshot.Invalidate(); _deviceSnapshot.Invalidate(); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/me/player" ? 3 : 6));
            var response = await _httpFactory.CreateClient().SendAsync(request, timeout.Token);
            if (spotifyApi && (int)response.StatusCode == 429)
            {
                var error = _rateLimit.Block(response.Headers.RetryAfter);
                response.Dispose();
                throw error;
            }
            return response;
        }
        finally
        {
            if (changesPlayer) { _playbackSnapshot.Invalidate(); _deviceSnapshot.Invalidate(); }
            if (spotifyApi) _apiRequestLock.Release();
        }
    }

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;
    private string? _lastActiveDeviceId;

    private static readonly string[] Scopes = {
        "user-read-playback-state",
        "user-modify-playback-state",
        "user-read-currently-playing",
        "streaming",
        "user-read-email",
        "user-read-private",
        "playlist-read-private",
        "playlist-read-collaborative",
        "user-library-read",
        "user-library-modify"
    };

    public void EnsurePlaybackAvailable() => _rateLimit.ThrowIfLimited();

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);
    private DateTimeOffset _libraryCheckedAt;
    private bool? _libraryAccess;
    private sealed class LibraryScopeException() : InvalidOperationException("Spotify oturumu kitaplık izni taşımıyor. Ayarlar'dan Spotify izinlerini yenileyin.") { }
    public async Task<bool?> HasLibraryAccessAsync()
    {
        if (DateTimeOffset.UtcNow - _libraryCheckedAt < TimeSpan.FromMinutes(1)) return _libraryAccess;
        try { await IsTrackSavedAsync("4iV5W9uYEdYUVa79Axb7Rh"); _libraryAccess = true; }
        catch (LibraryScopeException) { _libraryAccess = false; }
        catch { _libraryAccess = null; }
        _libraryCheckedAt = DateTimeOffset.UtcNow;
        return _libraryAccess;
    }
    public Guid UserId => _userId;
    public SpotifySettings Settings => _settings;

    public void UpdateCredentials(string clientId, string clientSecret)
    {
        _settings.ClientId = clientId;
        _settings.ClientSecret = clientSecret;
    }

    public SpotifyService(
        IOptions<SpotifySettings> settings,
        IHttpClientFactory httpFactory,
        ILogger<SpotifyService> logger,
        IServiceScopeFactory scopeFactory,
        IHubContext<OverlayHub> hub,
        Guid userId,
        string? rateLimitStatePath = null)
    {
        _settings = settings.Value;
        _settings.ClientSecret = TokenProtection.Unprotect(_settings.ClientSecret);
        _rateLimit = new SpotifyRateLimit(persistencePath: rateLimitStatePath);
        _repeatStatePath = rateLimitStatePath == null ? null : rateLimitStatePath + ".repeat";
        try { if (_repeatStatePath != null && File.Exists(_repeatStatePath)) { var saved = File.ReadAllText(_repeatStatePath).Trim(); if (saved is "off" or "context" or "track") _repeatRestore = saved; } } catch { }

        _httpFactory = httpFactory;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _hub = hub;
        _userId = userId;
        LoadTokenFromDb();
    }

    private string? _codeVerifier;
    public string GetAuthUrl(string? state = null)
    {
        if (string.IsNullOrWhiteSpace(_settings.ClientId)) throw new InvalidOperationException("Spotify istemci kimliği yapılandırılmamış.");
        _codeVerifier = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(_codeVerifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var scope = string.Join(" ", Scopes);
        var url = $"https://accounts.spotify.com/authorize?response_type=code&client_id={_settings.ClientId}&scope={Uri.EscapeDataString(scope)}&redirect_uri={Uri.EscapeDataString(_settings.RedirectUri)}&show_dialog=true&code_challenge_method=S256&code_challenge={challenge}";
        if (!string.IsNullOrEmpty(state))
            url += $"&state={Uri.EscapeDataString(state)}";
        return url;
    }

    public string GetAuthUrl() => GetAuthUrl(null);

    public async Task DisconnectAsync()
    {
        try { if (IsAuthenticated) await SetManagedQueueAsync(false); } catch (Exception ex) { _logger.LogWarning(ex, "Could not restore Spotify repeat mode before disconnect"); }
        _accessToken  = null;
        _refreshToken = null;
        _accountId = null;
        _expiresAt    = default;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            var token = await db.SpotifyTokens.FindAsync(_userId);
            if (token != null)
            {
                db.SpotifyTokens.Remove(token);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove Spotify token from DB for user {UserId}", _userId);
        }

        _logger.LogInformation("Spotify disconnected for user {UserId}", _userId);
    }

    public async Task HandleCallbackAsync(string code)
    {
        _accountId = null;
        _libraryCheckedAt = default;
        var response = await SendTokenRequest(new Dictionary<string, string>
        {
            ["grant_type"]   = "authorization_code",
            ["code"]         = code,
            ["redirect_uri"] = _settings.RedirectUri,
            ["code_verifier"] = _codeVerifier ?? throw new InvalidOperationException("Spotify giriş isteği bulunamadı."),
        });
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        _accessToken = json.RootElement.GetProperty("access_token").GetString();
        _refreshToken = json.RootElement.GetProperty("refresh_token").GetString();
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());

        await SaveTokenToDb();
        _logger.LogInformation("Spotify authenticated for user {UserId}", _userId);
    }

    public async Task<List<Song>> SearchAsync(string query, int limit = 5)
    {
        var token = await GetAccessTokenAsync();
        var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit={Math.Clamp(limit, 1, 10)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await SendSpotifyAsync(request);
        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var tracks = json.RootElement.GetProperty("tracks").GetProperty("items");

        var songs = new List<Song>();
        foreach (var track in tracks.EnumerateArray())
        {
            var song = ParseTrack(track);
            songs.Add(song);
            if (_currentEditions.Count >= 256) _currentEditions.Clear();
            _currentEditions[song.SpotifyUri] = song.SpotifyUri;
        }

        return songs;
    }

    private readonly SemaphoreSlim _playbackLock = new(1, 1);

    public async Task PlayAsync(string spotifyUri, string? deviceId = null)
    {
        await _playbackLock.WaitAsync();
        try { await PlayNativeTrackAsync(spotifyUri, deviceId); }
        finally { _playbackLock.Release(); }
    }

    public async Task<bool> PlayIfCurrentAsync(string spotifyUri, Func<bool> isCurrent)
    {
        await _playbackLock.WaitAsync();
        try
        {
            if (!isCurrent()) return false;
            await PlayNativeTrackAsync(spotifyUri, null, isCurrent);
            return isCurrent();
        }
        finally { _playbackLock.Release(); }
    }

    public async Task PauseIfCurrentAsync(Func<bool> isCurrent)
    {
        await PauseCoreAsync(isCurrent);
        for (var attempt = 0; attempt < 3 && isCurrent(); attempt++)
        {
            _playbackSnapshot.Invalidate();
            var actual = await GetPlaybackStateAsync();
            if (!isCurrent() || actual != null && !actual.IsPlaying) return;
            await Task.Delay(250);
        }
        if (isCurrent()) throw new SpotifyPlayerCommandException(409, "Spotify duraklatması doğrulanmadı; iki kaynağın aynı anda çalmaması için geçiş bekletildi.");
    }

    // Disable repeat-track while Musick owns advancement; a late handoff must not restart the old song.
    // The boundary timer pauses before selecting the next source. Restore the user's mode
    // on release; the tiny journal also survives an unexpected application exit.
    public async Task SetManagedQueueAsync(bool enabled, Func<bool>? isCurrent = null)
    {
        if (enabled && _managedRepeatActive || !enabled && _repeatRestore == null) return;
        await _repeatLock.WaitAsync();
        try
        {
            if (isCurrent?.Invoke() == false || enabled && _managedRepeatActive || !enabled && _repeatRestore == null) return;
            var state = await GetPlaybackStateAsync();
            if (isCurrent?.Invoke() == false || state?.RepeatState is not ("off" or "context" or "track")) return;
            if (enabled)
            {
                _repeatRestore ??= state.RepeatState;
                if (_repeatStatePath != null) await File.WriteAllTextAsync(_repeatStatePath, _repeatRestore);
            }
            // Respect a user's own repeat change when returning control.
            var target = enabled ? "off" : _repeatRestore!;
            if (state.RepeatState != target && (enabled || state.RepeatState == "off"))
            {
                var token = await GetAccessTokenAsync();
                var url = "https://api.spotify.com/v1/me/player/repeat?state=" + target;
                if (!string.IsNullOrEmpty(_lastActiveDeviceId)) url += "&device_id=" + Uri.EscapeDataString(_lastActiveDeviceId);
                using var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await SendSpotifyAsync(request, isCurrent: isCurrent);
                if (!response.IsSuccessStatusCode) throw await PlayerCommandErrorAsync(response, "tekrar ayarı");
                if (isCurrent?.Invoke() == false) return;
            }
            _managedRepeatActive = enabled;
            if (!enabled)
            {
                _repeatRestore = null;
                if (_repeatStatePath != null && File.Exists(_repeatStatePath)) File.Delete(_repeatStatePath);
            }
        }
        finally { _repeatLock.Release(); }
    }

    private async Task<bool> ConfirmPlaybackAsync(Func<bool>? isCurrent, params string[] expectedUris)
    {
        for(var check=0;check<4;check++)
        {
            if(isCurrent?.Invoke()==false)return false;
            await Task.Delay(new[]{500,1000,2000,3000}[check]);
            var actual=await GetPlaybackStateAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if(isCurrent?.Invoke()==false)return false;
            if(check is 0 or 3)_logger.LogInformation("Spotify confirmation: requested {Requested}; reported {Reported}; linked {Linked}; playing {Playing}",
                expectedUris.Length==0?"resume":string.Join(",",expectedUris),actual?.TrackUri,actual?.LinkedFromUri,actual?.IsPlaying);
            if(actual?.IsPlaying==true&&!string.IsNullOrEmpty(actual.TrackUri)&&(expectedUris.Length==0||expectedUris.Any(actual.MatchesTrack)))return true;
        }
        return false;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SpotifyTrackIdentity> _trackIdentities = new();
    private async Task<SpotifyTrackIdentity?> ReadTrackIdentityAsync(string uri)
    {
        if (_trackIdentities.TryGetValue(uri, out var cached)) return cached;
        var id = ExtractCleanTrackId(uri);
        if (string.IsNullOrEmpty(id) || id.Length != 22) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/tracks/" + id);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync());
        using var response = await SendSpotifyAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var identity = new SpotifyTrackIdentity(
            root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            root.TryGetProperty("duration_ms", out var duration) && duration.TryGetInt32(out var ms) ? ms : 0,
            root.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                ? artists.EnumerateArray().Where(a => a.TryGetProperty("id", out _)).Select(a => a.GetProperty("id").GetString() ?? "").ToArray() : [],
            root.TryGetProperty("external_ids", out var ids) && ids.ValueKind == JsonValueKind.Object && ids.TryGetProperty("isrc", out var isrc) ? isrc.GetString() : null,
            root.TryGetProperty("artists", out var namedArtists) && namedArtists.ValueKind == JsonValueKind.Array
                ? string.Join(" ", namedArtists.EnumerateArray().Where(a => a.TryGetProperty("name", out _)).Select(a => a.GetProperty("name").GetString())) : "");
        if (_trackIdentities.Count >= 256) _trackIdentities.Clear();
        _trackIdentities[uri] = identity;
        return identity;
    }
    private async Task<bool> IsEquivalentEditionAsync(string requested, string observed, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var target = await ReadTrackIdentityAsync(requested);
        if (target == null || !string.Equals(target.Title, title, StringComparison.OrdinalIgnoreCase)) return false;
        var actual = await ReadTrackIdentityAsync(observed);
        var matches = actual != null && target.IsEquivalentTo(actual);
        if (matches && _editionAliases.GetValueOrDefault(observed) != requested) { if (_editionAliases.Count >= 256) _editionAliases.Clear(); _editionAliases[observed] = requested; _playbackSnapshot.Invalidate(); }
        if (matches) _logger.LogInformation("Spotify alternate edition matched: requested {Requested}; observed {Observed}", requested, observed);
        return matches;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _editionAliases = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _currentEditions = new();
    private async Task<string> ResolveCurrentEditionAsync(string requested, Func<bool>? current)
    {
        if (_currentEditions.TryGetValue(requested, out var known)) return known;
        var target = await ReadTrackIdentityAsync(requested);
        if (current?.Invoke() == false || target == null || string.IsNullOrWhiteSpace(target.Title)
            || string.IsNullOrWhiteSpace(target.ArtistNames)) return requested;
        // Old playlist IDs may remain readable/playable=true yet be ignored by desktop.
        // Search supplies the current catalog edition; only tightly matched recordings qualify.
        var query = "track:" + target.Title + " artist:" + target.ArtistNames;
        var candidates = await SearchAsync(query, 5);
        if (current?.Invoke() == false) return requested;
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.Title.Trim(), target.Title.Trim(), StringComparison.OrdinalIgnoreCase)
                || Math.Abs((long)candidate.DurationMs - target.DurationMs) > 2000) continue;
            var identity = await ReadTrackIdentityAsync(candidate.SpotifyUri);
            if (current?.Invoke() == false) return requested;
            if (identity == null || !target.IsEquivalentTo(identity)) continue;
            if (_currentEditions.Count >= 256) _currentEditions.Clear();
            _currentEditions[requested] = candidate.SpotifyUri;
            if (candidate.SpotifyUri != requested)
            {
                if (_editionAliases.Count >= 256) _editionAliases.Clear();
                _editionAliases[candidate.SpotifyUri] = requested;
                _playbackSnapshot.Invalidate();
                _logger.LogInformation("Spotify current catalog edition resolved: {Requested} -> {Resolved}", requested, candidate.SpotifyUri);
            }
            return candidate.SpotifyUri;
        }
        return requested;
    }

    private readonly SpotifyPlaybackEngine _engine = new();
    public async Task WarmTrackAsync(string uri, Func<bool> current)
    {
        if (current()) await ResolveCurrentEditionAsync(uri, current);
    }

    public async Task<bool> PrepareNextTrackAsync(string uri, Func<bool> current, Action? rejected = null)
    {
        await _playbackLock.WaitAsync();
        var resolved = uri;
        var ready = false;
        try
        {
            if (!current()) return false;
            resolved = await ResolveCurrentEditionAsync(uri, current);
            if (!current()) return false;
            ready = await _engine.PrepareAsync(resolved, new NativePort(this, resolved, null, current), current);
            return ready;
        }
        catch (SpotifyCommandCancelledException) { return false; }
        finally
        {
            try { if (!ready && !_engine.MayHaveQueued(resolved)) rejected?.Invoke(); }
            finally { _playbackLock.Release(); }
        }
    }

    private async Task PlayNativeTrackAsync(string uri, string? deviceId, Func<bool>? isCurrent = null)
    {
        var id = ExtractCleanTrackId(uri);
        if (string.IsNullOrEmpty(id) || id.Length != 22) throw new InvalidOperationException("Geçerli bir Spotify şarkısı bulunamadı.");
        try
        {
            var resolved = await ResolveCurrentEditionAsync("spotify:track:" + id, isCurrent);
            if (isCurrent?.Invoke() == false) return;
            await _engine.SelectAsync(resolved, new NativePort(this, resolved, deviceId, isCurrent), isCurrent ?? (() => true));
        }
        finally
        {
            _logger.LogInformation("Spotify transition {Phase}: requested {Requested}; observed {Observed}; playing {Playing}",
                _engine.Phase, _engine.RequestedUri, _engine.Observed?.TrackUri, _engine.Observed?.IsPlaying);
        }
    }
    private sealed class NativePort(SpotifyService owner, string requestedUri, string? deviceId, Func<bool>? current) : ISpotifyPlaybackPort
    {
        private string? targetDevice = deviceId;
        public async Task<PlaybackState?> ReadAsync()
        {
            if (readCount++ > 0) owner._playbackSnapshot.Invalidate();
            var state = await owner.GetPlaybackStateAsync();
            if (state != null) targetDevice ??= owner._lastActiveDeviceId;
            if (state != null && !state.MatchesTrack(requestedUri)
                && await owner.IsEquivalentEditionAsync(requestedUri, state.TrackUri, state.Title))
                state = new PlaybackState { TrackUri=state.TrackUri, LinkedFromUri=requestedUri,
                    SampledAt=state.SampledAt, IsPlaying=state.IsPlaying, ProgressMs=state.ProgressMs, DurationMs=state.DurationMs,
                    Title=state.Title, Artist=state.Artist, CoverUrl=state.CoverUrl,
                    RepeatState=state.RepeatState, VolumePercent=state.VolumePercent };
            return state;
        }
        public async Task<SpotifyQueueState> QueueAsync()
        {
            var queue = await owner.GetQueueAsync();
            foreach (var song in queue.Queue.Take(1).Concat(queue.CurrentlyPlaying == null ? Array.Empty<Song>() : new[] { queue.CurrentlyPlaying }))
                if (song.SpotifyUri != requestedUri && await owner.IsEquivalentEditionAsync(requestedUri, song.SpotifyUri, song.Title))
                    song.SpotifyUri = requestedUri;
            return queue;
        }
        private int waitIndex;
        private int readCount;
        public Task WaitAsync() => Task.Delay(new[] { 250, 500, 1000, 1500, 2000 }[Math.Min(waitIndex++, 4)]);
        public Task EnqueueAsync(string uri) => Send(HttpMethod.Post, "queue", "uri=" + Uri.EscapeDataString(uri));
        public Task NextAsync() => Send(HttpMethod.Post, "next", null);
        public Task ResumeAsync() => Send(HttpMethod.Put, "play", null);
        public Task PauseAsync() => Send(HttpMethod.Put, "pause", null);
        public Task<IAsyncDisposable> SilenceAsync()
        {
            if (current?.Invoke() == false) throw new SpotifyCommandCancelledException();
            try
            {
                var mute = MusicBot.Services.Player.SpotifyAudioRouting.MuteForTransition();
                return Task.FromResult<IAsyncDisposable>(new RestoreAudio(owner, targetDevice, mute));
            }
            catch (Exception ex) { throw new SpotifyPlayerCommandException(409, "Spotify sırası sessizce düzeltilemedi: " + ex.Message); }
        }
        private sealed class RestoreAudio(SpotifyService owner, string? device, IDisposable mute) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                try
                {
                    // The playback lock is still held, so even a cancelled selection must stop its own intermediate audio.
                    owner._playbackSnapshot.Invalidate();
                    var state = await owner.GetPlaybackStateAsync();
                    if (state?.IsPlaying == true)
                    {
                        var cleanup = new NativePort(owner, state.TrackUri, device, null);
                        await cleanup.PauseAsync();
                        for (var check=0;check<4;check++)
                        {
                            owner._playbackSnapshot.Invalidate();
                            if ((await owner.GetPlaybackStateAsync())?.IsPlaying == false) break;
                            await Task.Delay(250);
                        }
                    }
                }
                finally { mute.Dispose(); }
            }
        }

        public async Task<bool> EnsureSessionAsync()
        {
            if (current?.Invoke() == false) return false;
            var device = await owner.PrepareIdleDeviceAsync();
            if (device == null)
            {
                AppEvents.RequestOpenSpotify();
                for (var check = 0; check < 4 && device == null; check++)
                {
                    await Task.Delay(750);
                    if (current?.Invoke() == false) return false;
                    owner._deviceSnapshot.Invalidate(); device = await owner.PrepareIdleDeviceAsync();
                }
            }
            if (device == null || current?.Invoke() == false) return false;
            await owner.TransferPlaybackCoreAsync(device, false, current);
            targetDevice = device;
            await Task.Delay(750);
            return current?.Invoke() != false;
        }

        private async Task Send(HttpMethod method, string action, string? query)
        {
            if (current?.Invoke() == false) throw new SpotifyCommandCancelledException();
            var token = await owner.GetAccessTokenAsync();
            var target = targetDevice;
            var args = new List<string>();
            if (query != null) args.Add(query);
            if (!string.IsNullOrEmpty(target)) args.Add("device_id=" + Uri.EscapeDataString(target));
            using var request = new HttpRequestMessage(method, "https://api.spotify.com/v1/me/player/" + action + (args.Count == 0 ? "" : "?" + string.Join("&",args)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await owner.SendSpotifyAsync(request, isCurrent: current);
            if (response.StatusCode == System.Net.HttpStatusCode.ResetContent) throw new SpotifyCommandCancelledException();
            if (!response.IsSuccessStatusCode) throw await owner.PlayerCommandErrorAsync(response, action);
        }
    }

    // POST /me/player/queue?uri={uri} — requires user-modify-playback-state, returns 204
    public async Task AddToQueueAsync(string spotifyUri)
    {
        var token = await GetAccessTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.spotify.com/v1/me/player/queue?uri={Uri.EscapeDataString(spotifyUri)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await SendSpotifyAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Spotify add-to-queue failed: {Status} {Error}", response.StatusCode, error);
            throw new InvalidOperationException($"Spotify rejected the request ({(int)response.StatusCode}): {error}");
        }
    }

    // POST /me/player/next — requires user-modify-playback-state, returns 204
    public async Task SkipAsync()
    {
        await RunPlayerCommandAsync(HttpMethod.Post, "next", null);
    }

    public Task PauseAsync() => PauseCoreAsync(null);
    private async Task PauseCoreAsync(Func<bool>? isCurrent)
    {
        await _playbackLock.WaitAsync();
        try
        {
            if (isCurrent?.Invoke() == false) return;
            var state = await GetPlaybackStateAsync();
            if (isCurrent?.Invoke() == false || state?.IsPlaying == false) return;
            var token = await GetAccessTokenAsync();
            using var request = new HttpRequestMessage(HttpMethod.Put,"https://api.spotify.com/v1/me/player/pause");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",token);
            using var response = await SendSpotifyAsync(request, isCurrent: isCurrent);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound) return;
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _playbackSnapshot.Invalidate();
                if (isCurrent?.Invoke() == false) return;
                var actual = await GetPlaybackStateAsync();
                if (actual?.IsPlaying == false) return;
            }
            throw await PlayerCommandErrorAsync(response,"duraklatma");
        }
        finally { _playbackLock.Release(); }
    }
    public Task ResumeAsync(string? deviceId = null) => ResumeCoreAsync(deviceId,null);
    public Task ResumeIfCurrentAsync(Func<bool> isCurrent) => ResumeCoreAsync(null,isCurrent);
    private async Task ResumeCoreAsync(string? deviceId,Func<bool>? isCurrent)
    {
        await RunPlayerCommandAsync(HttpMethod.Put,"play",deviceId,isCurrent);
    }
    private async Task<SpotifyPlayerCommandException> PlayerCommandErrorAsync(HttpResponseMessage response,string action)
    {
        var status=(int)response.StatusCode; var detail="";
        try
        {
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if(json.RootElement.TryGetProperty("error",out var error) && error.ValueKind==JsonValueKind.Object)
            {
                if(error.TryGetProperty("reason",out var reason)) detail=reason.GetString()??"";
                if(error.TryGetProperty("message",out var message)) detail+=" "+message.GetString();
            }
        }
        catch(JsonException) { }
        _logger.LogWarning("Spotify {Action} rejected with HTTP {Status}",action,status);
        var text=status switch
        {
            401=>"Spotify oturumu geçersiz. Hesabınızı yeniden bağlayın.",
            404=>"Spotify'da etkin oynatma bulunamadı. Spotify'ı açıp bir şarkı seçin.",
            403 when detail.Contains("premium",StringComparison.OrdinalIgnoreCase)=>"Spotify bu kontrol için Premium hesap istiyor.",
            403 when detail.Contains("scope",StringComparison.OrdinalIgnoreCase)=>"Spotify oynatma kontrol izni eksik. Hesabınızı yeniden bağlayın.",
            403=>$"Spotify mevcut oynatma durumunda {action} komutuna izin vermedi (403).",
            _=>$"Spotify {action} işlemini tamamlayamadı (HTTP {status})."
        };
        return new SpotifyPlayerCommandException(status,text);
    }

    private async Task<string> ActivatePlaybackDeviceAsync(string? preferred, bool play, Func<bool>? isCurrent = null)
    {
        var devices = (await GetDevicesAsync()).Where(d => !d.IsRestricted && !string.IsNullOrEmpty(d.Id)).ToList();
        var target = devices.FirstOrDefault(d => d.Id == preferred)
            ?? devices.FirstOrDefault(d => d.IsActive)
            ?? devices.FirstOrDefault(d => d.Type.Equals("computer", StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault();
        if (target == null) throw new InvalidOperationException("Spotify cihazı henüz hazır değil. Spotify açık kalsın; bağlantı kurulduktan sonra tekrar deneyin.");
        if (isCurrent?.Invoke()==false)return target.Id;
        await TransferPlaybackCoreAsync(target.Id,play,isCurrent);
        await Task.Delay(750); // Spotify does not guarantee ordering across Player endpoints.
        return target.Id;
    }

    private async Task<string?> PrepareIdleDeviceAsync()
    {
        var devices = (await GetDevicesAsync()).Where(d => !d.IsRestricted && !string.IsNullOrEmpty(d.Id)).ToList();
        var active = devices.FirstOrDefault(d => d.IsActive);
        if (active != null) return active.Id;
        var desktop = devices.FirstOrDefault(d => d.Type.Equals("computer", StringComparison.OrdinalIgnoreCase));
        if (desktop == null) return null;
        return desktop.Id;
    }

    private async Task RunPlayerCommandAsync(HttpMethod method, string action, string? deviceId, Func<bool>? isCurrent = null)
    {
        await _playbackLock.WaitAsync();
        try
        {
            if (isCurrent?.Invoke() == false) return;
            if (action == "play")
            {
                var state=await GetPlaybackStateAsync();
                if (isCurrent?.Invoke() == false || state?.IsPlaying == true && !string.IsNullOrEmpty(state.TrackUri)) return;
            }
            var token = await GetAccessTokenAsync();
            var target = deviceId ?? _lastActiveDeviceId;
            if (target == null) target = await PrepareIdleDeviceAsync();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var url = $"https://api.spotify.com/v1/me/player/{action}";
                if (!string.IsNullOrEmpty(target)) url += "?device_id=" + Uri.EscapeDataString(target);
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await SendSpotifyAsync(request,isCurrent:isCurrent);
                if (isCurrent?.Invoke() == false) return;
                if (response.IsSuccessStatusCode)
                {
                    if(action!="play" || await ConfirmPlaybackAsync(isCurrent) || isCurrent?.Invoke()==false)return;
                    throw new SpotifyPlayerCommandException(409,"Spotify isteği kabul etti ancak etkin bir şarkı çalmaya başlamadı. Musick'ten bir şarkı seçin.");
                }
                if (action == "play" && response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _playbackSnapshot.Invalidate();
                    var actual=await GetPlaybackStateAsync();
                    if (actual?.IsPlaying == true && !string.IsNullOrEmpty(actual.TrackUri)) return;
                }
                if (attempt == 0 && response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    target = await ActivatePlaybackDeviceAsync(target, true,isCurrent);
                    continue;
                }
                throw await PlayerCommandErrorAsync(response,action == "next" ? "sonraki şarkı" : action == "previous" ? "önceki şarkı" : "çalma");
            }
        }
        finally { _playbackLock.Release(); }
    }

    // GET /me/player — requires user-read-playback-state
    public Task<PlaybackState?> GetPlaybackStateAsync()
    {
        _rateLimit.ThrowIfLimited();
        return _playbackSnapshot.GetAsync(ReadPlaybackStateAsync, TimeSpan.FromSeconds(2));
    }
    private async Task<PlaybackState?> ReadPlaybackStateAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            var token = await GetAccessTokenAsync();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var sampledAt = DateTimeOffset.UtcNow;
            using var response = await SendSpotifyAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound || response.StatusCode == System.Net.HttpStatusCode.NoContent ||
                response.StatusCode == System.Net.HttpStatusCode.Accepted)
                return null;

            if (!response.IsSuccessStatusCode) throw await PlayerCommandErrorAsync(response,"oynatma durumunu okuma");

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = json.RootElement;

            if (root.TryGetProperty("device", out var currentDevice) && currentDevice.TryGetProperty("id", out var deviceId) && deviceId.GetString() is string activeId && !string.IsNullOrEmpty(activeId))
                _lastActiveDeviceId = activeId;

            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                return null;

            var song = ParseTrack(item);

            // Starting a new context/device can reset repeat mode. Let the next lease
            // check reapply it once, rather than assuming the previous write still holds.
            if (_managedRepeatActive && root.TryGetProperty("repeat_state", out var actualRepeat)
                && actualRepeat.ValueKind == JsonValueKind.String && actualRepeat.GetString() != "off") _managedRepeatActive = false;
            return new PlaybackState
            {
                SampledAt = sampledAt,
                RepeatState = root.TryGetProperty("repeat_state", out var repeat) && repeat.ValueKind == JsonValueKind.String ? repeat.GetString() : null,
                ProgressMs = root.TryGetProperty("progress_ms", out var p) ? p.GetInt32() : 0,
                IsPlaying = root.TryGetProperty("is_playing", out var ip) && ip.GetBoolean(),
                TrackUri = song.SpotifyUri,
                LinkedFromUri = item.TryGetProperty("linked_from",out var linked) && linked.ValueKind == JsonValueKind.Object && linked.TryGetProperty("uri",out var original) ? original.GetString() ?? _editionAliases.GetValueOrDefault(song.SpotifyUri) : _editionAliases.GetValueOrDefault(song.SpotifyUri),
                DurationMs = song.DurationMs,
                Title = song.Title,
                Artist = song.Artist,
                CoverUrl = song.CoverUrl,
                VolumePercent = root.TryGetProperty("device", out var device) && device.TryGetProperty("volume_percent", out var volume) && volume.ValueKind == JsonValueKind.Number ? volume.GetInt32() : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Spotify GetPlaybackStateAsync failed");
            throw;
        }
        finally { _stateLock.Release(); }
    }

    // GET /me/player/queue — requires user-read-playback-state
    public async Task<SpotifyQueueState> GetQueueAsync()
    {
        var token = await GetAccessTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/queue");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await SendSpotifyAsync(request);
        if (!response.IsSuccessStatusCode)
            throw await PlayerCommandErrorAsync(response, "kuyruk okuma");

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return new SpotifyQueueState();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var result = new SpotifyQueueState();

        if (json.RootElement.TryGetProperty("currently_playing", out var cp) &&
            cp.ValueKind != JsonValueKind.Null)
            result.CurrentlyPlaying = ParseTrack(cp);

        if (json.RootElement.TryGetProperty("queue", out var queue))
            foreach (var item in queue.EnumerateArray())
                result.Queue.Add(ParseTrack(item));

        return result;
    }

    public async Task<Song?> GetTrackAsync(string trackId)
    {
        try
        {
            var token = await GetAccessTokenAsync();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/tracks/{trackId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await SendSpotifyAsync(request);
            if (response.IsSuccessStatusCode)
            {
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                return ParseTrack(json.RootElement);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spotify GetTrackAsync API call failed for {TrackId}. Trying embed scraper...", trackId);
        }

        return await ScrapeEmbedTrackAsync(trackId);
    }

    public async Task<List<Song>> GetPlaylistTracksAsync(string playlistId, int maxTracks = int.MaxValue)
    {
        // Always use user token for playlist access. Spotify no longer allows
        // Client Credentials tokens to access playlist content.
        var token = await GetAccessTokenAsync();

        var songs  = new List<Song>();
        var offset = 0;
        const int pageSize = 50;
        var legacyItems = false;

        while (songs.Count < maxTracks)
        {
            var limit   = Math.Min(pageSize, maxTracks - songs.Count);
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.spotify.com/v1/playlists/{playlistId}/{(legacyItems ? "tracks" : "items")}?limit={limit}&offset={offset}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await SendSpotifyAsync(request);

            if (!legacyItems && response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
            {
                using var compatibility = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/playlists/{playlistId}/tracks?limit={limit}&offset={offset}");
                compatibility.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var previousApi = await SendSpotifyAsync(compatibility);
                if (previousApi.IsSuccessStatusCode) { response.Dispose(); response = previousApi; legacyItems = true; }
                else previousApi.Dispose();
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Spotify playlist {Id}: {Status}: {Body}. Trying embed scraper fallback...", playlistId, (int)response.StatusCode, body);
                if (offset > 0)
                    throw new InvalidOperationException($"Spotify listesinin devamı alınamadı (HTTP {(int)response.StatusCode}). Yarım liste kaydedilmedi; tekrar deneyin.");
                var scraped = await ScrapeEmbedPlaylistTracksAsync(playlistId, maxTracks);
                if (scraped.Count > 0)
                {
                    _logger.LogInformation("Recovered {Count} tracks via Spotify embed scraper for playlist {Id}", scraped.Count, playlistId);
                    return scraped;
                }

                string msg = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Forbidden =>
                        "Spotify liste içeriğine erişimi reddetti (403). Yeni geliştirme uygulamalarında yalnızca sahibi olduğunuz veya ortak düzenlediğiniz listelerin içeriği okunabiliyor. Başkasının listesini kitaplığa eklemek bu izni sağlamıyor; Spotify’da kendi listenize kopyalayıp aktarabilirsiniz.",
                    System.Net.HttpStatusCode.NotFound =>
                        "Spotify çalma listesi bulunamadı (404 Not Found). Lütfen bağlantıyı kontrol edin.",
                    System.Net.HttpStatusCode.Unauthorized =>
                        "Spotify oturum süresi doldu. Lütfen Ayarlar sayfasından Spotify hesabınızı yeniden bağlayın.",
                    _ => $"Spotify API isteği başarısız oldu (HTTP {(int)response.StatusCode})."
                };
                throw new InvalidOperationException(msg);
            }

            var json  = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var items = json.RootElement.GetProperty("items");
            int added = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("item", out var track) && !item.TryGetProperty("track", out track)) continue;
                if (track.ValueKind != JsonValueKind.Object || (track.TryGetProperty("type", out var kind) && kind.GetString() != "track")) continue;
                songs.Add(ParseTrack(track));
                added++;
            }

            offset += items.GetArrayLength();
            if (items.GetArrayLength() == 0 || !json.RootElement.TryGetProperty("next", out var nextPage) || nextPage.ValueKind == JsonValueKind.Null) break;
        }

        return songs;
    }

    public async Task<List<Song>> GetAlbumTracksAsync(string albumId, int maxTracks = int.MaxValue)
    {
        var token = await GetAccessTokenAsync();
        var songs = new List<Song>();
        var offset = 0;
        const int pageSize = 50;

        while (songs.Count < maxTracks)
        {
            var limit = Math.Min(pageSize, maxTracks - songs.Count);
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.spotify.com/v1/albums/{albumId}/tracks?limit={limit}&offset={offset}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await SendSpotifyAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Spotify album {Id}: {Status}: {Body}. Trying embed scraper fallback...", albumId, (int)response.StatusCode, body);
                if (offset > 0)
                    throw new InvalidOperationException($"Spotify albümünün devamı alınamadı (HTTP {(int)response.StatusCode}). Yarım liste kaydedilmedi.");
                var scraped = await ScrapeEmbedAlbumTracksAsync(albumId, maxTracks);
                if (scraped.Count > 0)
                {
                    _logger.LogInformation("Recovered {Count} tracks via Spotify embed scraper for album {Id}", scraped.Count, albumId);
                    return scraped;
                }

                string hint = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Forbidden =>
                        " (Bu Spotify albümüne erişilemedi. Lütfen Ayarlar'dan Spotify hesabınızı yeniden bağlayın)",
                    System.Net.HttpStatusCode.NotFound =>
                        " (Spotify albümü bulunamadı. Lütfen bağlantıyı kontrol edin)",
                    _ => ""
                };
                throw new InvalidOperationException($"Spotify API hatası ({(int)response.StatusCode}){hint}: {body}");
            }

            var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var items = json.RootElement.GetProperty("items");
            int added = 0;
            foreach (var item in items.EnumerateArray())
            {
                songs.Add(ParseTrack(item));
                added++;
            }

            offset += items.GetArrayLength();
            if (items.GetArrayLength() == 0 || !json.RootElement.TryGetProperty("next", out var nextPage) || nextPage.ValueKind == JsonValueKind.Null) break;
        }

        return songs;
    }

    public async Task<List<Song>> GetSavedTracksAsync(int maxTracks = int.MaxValue)
    {
        var token = await GetAccessTokenAsync();
        var songs  = new List<Song>();
        var offset = 0;
        const int pageSize = 50;

        while (songs.Count < maxTracks)
        {
            var limit = Math.Min(pageSize, maxTracks - songs.Count);
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.spotify.com/v1/me/tracks?limit={limit}&offset={offset}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await SendSpotifyAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Spotify beğenilen şarkılar alınamadı (HTTP {(int)response.StatusCode}). Yarım liste kaydedilmedi.");

            var json  = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var items = json.RootElement.GetProperty("items");
            int added = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("item", out var track) && !item.TryGetProperty("track", out track)) continue;
                if (track.ValueKind != JsonValueKind.Object || (track.TryGetProperty("type", out var kind) && kind.GetString() != "track")) continue;
                songs.Add(ParseTrack(track));
                added++;
            }

            offset += items.GetArrayLength();
            if (items.GetArrayLength() == 0 || !json.RootElement.TryGetProperty("next", out var nextPage) || nextPage.ValueKind == JsonValueKind.Null) break;
        }

        return songs;
    }

    private string? _accountId;
    public async Task<List<SpotifyPlaylistSummary>> GetUserPlaylistsAsync()
    {
        var token = await GetAccessTokenAsync();
        if (_accountId == null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await SendSpotifyAsync(request); response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (!json.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                throw new InvalidOperationException("Spotify hesap kimliği alınamadı; liste sahipliği doğrulanamadı.");
            _accountId = id.GetString();
        }
        var result = new List<SpotifyPlaylistSummary>(); var offset = 0;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/me/playlists?limit=50&offset={offset}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await SendSpotifyAsync(request); response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = json.RootElement;
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) break;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.Object
                    || !owner.TryGetProperty("id", out var ownerId) || ownerId.GetString() != _accountId) continue;
                if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) continue;
                var count = 0;
                if ((item.TryGetProperty("items", out var details) || item.TryGetProperty("tracks", out details)) && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("total", out var total) && total.TryGetInt32(out var n)) count = n;
                result.Add(new SpotifyPlaylistSummary { Id = id.GetString()!, Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    CoverUrl = ReadEmbedCover(item, ""), TrackCount = count });
            }
            offset += items.GetArrayLength();
            if (items.GetArrayLength() == 0 || !root.TryGetProperty("next", out var next) || next.ValueKind == JsonValueKind.Null) break;
        }
        return result;
    }

    public async Task<List<Song>> ScrapeEmbedPlaylistTracksAsync(string playlistId, int maxTracks = int.MaxValue)
    {
        return await ScrapeEmbedEntityTracksAsync($"https://open.spotify.com/embed/playlist/{playlistId}", maxTracks);
    }

    public async Task<List<Song>> ScrapeEmbedAlbumTracksAsync(string albumId, int maxTracks = int.MaxValue)
    {
        return await ScrapeEmbedEntityTracksAsync($"https://open.spotify.com/embed/album/{albumId}", maxTracks);
    }

    public async Task<Song?> ScrapeEmbedTrackAsync(string trackId)
    {
        try
        {
            var client = _httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://open.spotify.com/embed/track/{trackId}");
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            var res = await SendSpotifyAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Embed track scrape failed: {Status} for track {Id}", res.StatusCode, trackId);
                return null;
            }

            var html = await res.Content.ReadAsStringAsync();
            var match = System.Text.RegularExpressions.Regex.Match(html, @"<script id=""__NEXT_DATA__""[^>]*>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!match.Success) return null;

            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var root = doc.RootElement;
            if (!root.TryGetProperty("props", out var props) ||
                !props.TryGetProperty("pageProps", out var pageProps) ||
                !pageProps.TryGetProperty("state", out var state) ||
                !state.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("entity", out var entity))
            {
                return null;
            }

            string coverUrl = "";
            if (entity.TryGetProperty("coverArt", out var coverArt) &&
                coverArt.TryGetProperty("sources", out var sources) &&
                sources.ValueKind == JsonValueKind.Array &&
                sources.GetArrayLength() > 0 &&
                sources[0].TryGetProperty("url", out var urlProp))
            {
                coverUrl = urlProp.GetString() ?? "";
            }

            var uri = entity.TryGetProperty("uri", out var u) ? u.GetString() ?? $"spotify:track:{trackId}" : $"spotify:track:{trackId}";
            var title = entity.TryGetProperty("title", out var tit) ? tit.GetString() ?? "" : "";
            var duration = entity.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetInt32() : 0;

            string artist = "";
            if (entity.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0)
            {
                var aList = new List<string>();
                foreach (var a in artists.EnumerateArray())
                {
                    if (a.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                        aList.Add(n.GetString()!);
                }
                artist = string.Join(", ", aList);
            }
            if (string.IsNullOrWhiteSpace(artist) && entity.TryGetProperty("subtitle", out var sub))
            {
                artist = sub.GetString() ?? "";
            }

            if (string.IsNullOrWhiteSpace(title)) return null;

            return new Song
            {
                SpotifyUri = uri,
                Title = title,
                Artist = artist,
                DurationMs = duration,
                CoverUrl = coverUrl,
                Platform = "spotify"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scrape Spotify embed for track {Id}", trackId);
            return null;
        }
    }

    private static string ReadEmbedCover(JsonElement item, string fallback)
    {
        foreach (var field in new[] { "imageUrl", "imageUri" })
            if (item.TryGetProperty(field, out var direct) && direct.ValueKind == JsonValueKind.String && direct.GetString()?.StartsWith("https://") == true)
                return direct.GetString()!;
        foreach (var field in new[] { "coverArt", "albumOfTrack", "album" })
            if (item.TryGetProperty(field, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                var found = ReadEmbedCover(nested, "");
                if (!string.IsNullOrEmpty(found)) return found;
            }
        foreach (var field in new[] { "sources", "images" })
            if (item.TryGetProperty(field, out var images) && images.ValueKind == JsonValueKind.Array)
                foreach (var image in images.EnumerateArray())
                    if (image.TryGetProperty("url", out var url) && url.GetString()?.StartsWith("https://") == true) return url.GetString()!;
        return fallback;
    }

    private async Task<List<Song>> ScrapeEmbedEntityTracksAsync(string embedUrl, int maxTracks)
    {
        var songs = new List<Song>();
        var playlist = new Uri(embedUrl).AbsolutePath.StartsWith("/embed/playlist/", StringComparison.Ordinal);
        int? totalCount = null;
        var previewCount = 0;
        try
        {
            var client = _httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            var res = await SendSpotifyAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Embed scrape failed: {Status} for {Url}", res.StatusCode, embedUrl);
                return songs;
            }

            var html = await res.Content.ReadAsStringAsync();
            if (playlist)
            {
                using var countRequest = new HttpRequestMessage(HttpMethod.Get, embedUrl.Replace("/embed/playlist/", "/playlist/", StringComparison.Ordinal));
                countRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                using var countResponse = await SendSpotifyAsync(countRequest);
                if (countResponse.IsSuccessStatusCode)
                {
                    var page = await countResponse.Content.ReadAsStringAsync();
                    var count = System.Text.RegularExpressions.Regex.Match(page, @"<meta\b(?=[^>]*\bname\s*=\s*[""']music:song_count[""'])(?=[^>]*\bcontent\s*=\s*[""'](\d+)[""'])[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (count.Success && int.TryParse(count.Groups[1].Value, out var value)) totalCount = value;
                }
            }
            var match = System.Text.RegularExpressions.Regex.Match(html, @"<script id=""__NEXT_DATA__""[^>]*>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!match.Success) return songs;

            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var root = doc.RootElement;
            if (!root.TryGetProperty("props", out var props) ||
                !props.TryGetProperty("pageProps", out var pageProps) ||
                !pageProps.TryGetProperty("state", out var state) ||
                !state.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("entity", out var entity))
            {
                return songs;
            }

            string coverUrl = "";
            if (entity.TryGetProperty("coverArt", out var coverArt) &&
                coverArt.TryGetProperty("sources", out var sources) &&
                sources.GetArrayLength() > 0 &&
                sources[0].TryGetProperty("url", out var urlProp))
            {
                coverUrl = urlProp.GetString() ?? "";
            }

            if (entity.TryGetProperty("trackList", out var trackList))
            {
                previewCount = trackList.GetArrayLength();
                foreach (var t in trackList.EnumerateArray())
                {
                    if (songs.Count >= maxTracks) break;
                    var uri = t.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
                    var title = t.TryGetProperty("title", out var tit) ? tit.GetString() ?? "" : "";
                    var artist = t.TryGetProperty("subtitle", out var sub) ? sub.GetString() ?? "" : "";
                    var duration = t.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetInt32() : 0;

                    if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(title)) continue;

                    songs.Add(new Song
                    {
                        SpotifyUri = uri,
                        Title = title,
                        Artist = artist,
                        DurationMs = duration,
                        CoverUrl = ReadEmbedCover(t, embedUrl.Contains("/album/", StringComparison.Ordinal) ? coverUrl : ""),
                        Platform = "spotify"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scrape Spotify embed for {Url}", embedUrl);
        }
        if (playlist && songs.Count > 0 && maxTracks > songs.Count && (totalCount == null || totalCount > previewCount || songs.Count < previewCount))
            throw new InvalidOperationException(totalCount is int total
                ? $"Bu Spotify listesinde {total} kayıt var; herkese açık önizlemeden yalnızca {songs.Count} şarkı alınabildi. Spotify API'si de liste içeriğine erişim vermediği için eksik aktarım yapılmadı. Tam aktarım için Spotify'da listeyi kendi hesabına kopyalayın veya liste sahibinden ortak düzenleme izni alın."
                : "Spotify herkese açık listenin toplam şarkı sayısını vermedi; aktarımın eksiksiz olduğu doğrulanamadığı için kayıt yapılmadı. Listeyi kendi Spotify hesabınıza kopyalayarak aktarabilirsiniz.");
        if (!playlist && maxTracks > 50 && songs.Count >= 50)
            throw new InvalidOperationException("Spotify albüm önizlemesinin eksiksiz olduğu doğrulanamadı; tam aktarım için hesabınızı bağlayın.");
        return songs;
    }

    public async Task SetVolumeAsync(int volumePercent)
    {
        _rateLimit.ThrowIfLimited();
        var clamped = Math.Clamp(volumePercent, 0, 100);
        var revision = Interlocked.Increment(ref _volumeVersion);
        await Task.Delay(250);
        await _volumeLock.WaitAsync();
        try
        {
            if (revision != Volatile.Read(ref _volumeVersion)) return;
            if (_lastVolumeSent == clamped && DateTimeOffset.UtcNow - _lastVolumeSentAt < TimeSpan.FromSeconds(1)) return;
            var remaining = TimeSpan.FromMilliseconds(500) - (DateTimeOffset.UtcNow - _lastVolumeSentAt);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
            if (revision != Volatile.Read(ref _volumeVersion)) return;
            var token = await GetAccessTokenAsync();
            using var request = new HttpRequestMessage(HttpMethod.Put,
                $"https://api.spotify.com/v1/me/player/volume?volume_percent={clamped}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _lastVolumeSentAt = DateTimeOffset.UtcNow;
            using var response = await SendSpotifyAsync(request);
            if (!response.IsSuccessStatusCode) throw await PlayerCommandErrorAsync(response,"ses seviyesi");
            _lastVolumeSent = clamped;
            _lastVolumeSentAt = DateTimeOffset.UtcNow;
        }
        finally { _volumeLock.Release(); }
    }

    public Task SeekAsync(int positionMs) => SeekIfCurrentAsync(positionMs,null);
    public async Task SeekIfCurrentAsync(int positionMs,Func<bool>? isCurrent)
    {
        await _playbackLock.WaitAsync();
        try
        {
            if (isCurrent?.Invoke() == false) return;
            var token=await GetAccessTokenAsync();
            var url=$"https://api.spotify.com/v1/me/player/seek?position_ms={Math.Max(0,positionMs)}";
            if(_lastActiveDeviceId != null) url+="&device_id="+Uri.EscapeDataString(_lastActiveDeviceId);
            using var request=new HttpRequestMessage(HttpMethod.Put,url);
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            using var response=await SendSpotifyAsync(request,isCurrent:isCurrent);
            if(!response.IsSuccessStatusCode && isCurrent?.Invoke()!=false)throw await PlayerCommandErrorAsync(response,"ilerletme");
        }
        finally{_playbackLock.Release();}
    }
    public Task PreviousAsync() => RunPlayerCommandAsync(HttpMethod.Post,"previous",null);

    public Task<List<SpotifyDevice>> GetDevicesAsync()
    {
        _rateLimit.ThrowIfLimited();
        return _deviceSnapshot.GetAsync(ReadDevicesAsync, TimeSpan.FromSeconds(15));
    }
    private async Task<List<SpotifyDevice>> ReadDevicesAsync()
    {
        try
        {
            var token = await GetAccessTokenAsync();
            var client = _httpFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/devices");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await SendSpotifyAsync(request);
            if (!response.IsSuccessStatusCode) return new List<SpotifyDevice>();

            var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var list = new List<SpotifyDevice>();
            if (json.RootElement.TryGetProperty("devices", out var devices))
            {
                foreach (var d in devices.EnumerateArray())
                {
                    list.Add(new SpotifyDevice
                    {
                        Id = d.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "",
                        Name = d.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "",
                        Type = d.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? "" : "",
                        IsActive = d.TryGetProperty("is_active", out var aProp) && aProp.GetBoolean(),
                        IsRestricted = d.TryGetProperty("is_restricted", out var rProp) && rProp.GetBoolean(),
                        VolumePercent = d.TryGetProperty("volume_percent", out var vProp) && vProp.ValueKind == JsonValueKind.Number ? vProp.GetInt32() : null,
                    });
                }
            }
            return list;
        }
        catch (SpotifyRateLimitException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get Spotify devices");
            return new List<SpotifyDevice>();
        }
    }

    public Task TransferPlaybackAsync(string deviceId, bool play = false) => TransferPlaybackCoreAsync(deviceId,play,null);
    private async Task TransferPlaybackCoreAsync(string deviceId,bool play,Func<bool>? isCurrent)
    {
        var token = await GetAccessTokenAsync();
        var client = _httpFactory.CreateClient();
        var payload = play ? (object)new { device_ids = new[] { deviceId }, play = true } : new { device_ids = new[] { deviceId }, play = false };
        var request = new HttpRequestMessage(HttpMethod.Put, "https://api.spotify.com/v1/me/player")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await SendSpotifyAsync(request,isCurrent:isCurrent);
        if(isCurrent?.Invoke()==false)return;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Spotify TransferPlayback failed: {Status} {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Spotify cihaz değişikliği başarısız (HTTP {(int)response.StatusCode}).");
        }
        _lastActiveDeviceId = deviceId;
    }

    public static string ExtractCleanTrackId(string trackUriOrUrl)
    {
        if (string.IsNullOrWhiteSpace(trackUriOrUrl)) return "";
        var match = System.Text.RegularExpressions.Regex.Match(trackUriOrUrl, @"(?:spotify:track:|track/)([a-zA-Z0-9]{22})");
        if (match.Success) return match.Groups[1].Value;

        var cleaned = trackUriOrUrl.Trim().Replace("spotify:track:", "");
        var qIdx = cleaned.IndexOf('?');
        if (qIdx >= 0) cleaned = cleaned[..qIdx];
        return cleaned.Trim();
    }

    public Task SaveTrackAsync(string trackId) => UpdateSavedTrackAsync(trackId, HttpMethod.Put);
    public Task RemoveSavedTrackAsync(string trackId) => UpdateSavedTrackAsync(trackId, HttpMethod.Delete);
    private async Task UpdateSavedTrackAsync(string trackId, HttpMethod method)
    {
        var id = ExtractCleanTrackId(trackId);
        if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("Geçerli Spotify şarkısı bulunamadı.");
        var token = await GetAccessTokenAsync();
        using var request = new HttpRequestMessage(method, "https://api.spotify.com/v1/me/library?uris=" + Uri.EscapeDataString("spotify:track:" + id));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendSpotifyAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && (await response.Content.ReadAsStringAsync()).Contains("Insufficient client scope", StringComparison.OrdinalIgnoreCase)) throw new LibraryScopeException();
            throw new InvalidOperationException($"Spotify beğenisi eşitlenemedi (HTTP {(int)response.StatusCode}). Yerel beğeniniz korunuyor.");
        }
    }
    public async Task<bool> IsTrackSavedAsync(string trackId)
    {
        var id = ExtractCleanTrackId(trackId);
        if (string.IsNullOrEmpty(id)) return false;
        var token = await GetAccessTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/library/contains?uris=" + Uri.EscapeDataString("spotify:track:" + id));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendSpotifyAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && (await response.Content.ReadAsStringAsync()).Contains("Insufficient client scope", StringComparison.OrdinalIgnoreCase)) throw new LibraryScopeException();
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 && doc.RootElement[0].ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Obtains a short-lived app-level token using the Client Credentials flow.
    /// Works for all public Spotify content without requiring user authorization.
    /// </summary>
    private async Task<string> GetClientCredentialsTokenAsync()
    {
        var response = await SendTokenRequest(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
        });
        var json     = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return json.RootElement.GetProperty("access_token").GetString()
               ?? throw new InvalidOperationException("Spotify CC token vacío");
    }

    private static Song ParseTrack(JsonElement track)
    {
        var artists = track.TryGetProperty("artists", out var arr)
            ? string.Join(", ", arr.EnumerateArray().Select(a => a.GetProperty("name").GetString() ?? ""))
            : "";

        var coverUrl = "";
        if (track.TryGetProperty("album", out var album) &&
            album.TryGetProperty("images", out var images))
        {
            var list = images.EnumerateArray().ToList();
            if (list.Count > 0) coverUrl = list[0].GetProperty("url").GetString() ?? "";
        }

        return new Song
        {
            SpotifyUri = track.TryGetProperty("uri", out var uri) ? uri.GetString() ?? "" : "",
            Title = track.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            Artist = artists,
            CoverUrl = coverUrl,
            DurationMs = track.TryGetProperty("duration_ms", out var dur) ? dur.GetInt32() : 0
        };
    }

    public async Task<string> GetAccessTokenAsync()
    {
        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("Not authenticated with Spotify");

        if (DateTimeOffset.UtcNow >= _expiresAt.AddMinutes(-1))
        {
            try
            {
                await RefreshTokenAsync();
            }
            catch (Exception ex) when (ex is TaskCanceledException || ex is HttpRequestException http && (http.StatusCode == null || (int)http.StatusCode >= 500 || (int)http.StatusCode == 429))
            {
                _logger.LogWarning(ex, "Spotify token refresh temporarily unavailable; saved session retained");
                throw new InvalidOperationException("Spotify bağlantısı geçici olarak yanıt vermiyor. Oturumunuz korundu.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spotify token refresh failed — user must re-authenticate");
                _accessToken  = null;
                _refreshToken = null;
                await _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("auth:expired", new
                {
                    platform = "spotify",
                    message  = "Spotify oturum süreniz doldu. Lütfen Ayarlar'dan Spotify hesabınızı yeniden bağlayın."
                });
                throw new InvalidOperationException("Spotify token refresh failed — re-authentication required", ex);
            }
        }

        return _accessToken;
    }

    private async Task RefreshTokenAsync()
    {
        if (_refreshToken == null) throw new InvalidOperationException("No refresh token");

        var response = await SendTokenRequest(new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = _refreshToken,
        });
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        _accessToken = json.RootElement.GetProperty("access_token").GetString();
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());

        if (json.RootElement.TryGetProperty("refresh_token", out var rt))
            _refreshToken = rt.GetString();

        await SaveTokenToDb();
        _logger.LogInformation("Spotify token refreshed for user {UserId}", _userId);
    }

    private async Task<HttpResponseMessage> SendTokenRequest(Dictionary<string, string> fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        if (!string.IsNullOrEmpty(_settings.ClientId) && !string.IsNullOrEmpty(_settings.ClientSecret))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.ClientId}:{_settings.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        fields["client_id"] = _settings.ClientId;
        request.Content = new FormUrlEncodedContent(fields);

        var response = await SendSpotifyAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Spotify yetkilendirmesi başarısız (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
        }
        return response;
    }

    private async Task SaveTokenToDb()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            var protectedAccess = TokenProtection.Protect(_accessToken);
            var protectedRefresh = TokenProtection.Protect(_refreshToken);

            var existing = await db.SpotifyTokens.FindAsync(_userId);
            if (existing != null)
            {
                existing.AccessToken = protectedAccess;
                existing.RefreshToken = protectedRefresh;
                existing.ExpiresAt = _expiresAt;
            }
            else
            {
                db.SpotifyTokens.Add(new SpotifyToken
                {
                    UserId = _userId,
                    AccessToken = protectedAccess,
                    RefreshToken = protectedRefresh,
                    ExpiresAt = _expiresAt
                });
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save Spotify token to database");
        }
    }

    private void LoadTokenFromDb()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            var preference = db.PlatformConfigs.FirstOrDefault(p => p.UserId == _userId && p.Platform == "spotify_device");
            if (preference != null) _lastActiveDeviceId = JsonSerializer.Deserialize<string>(preference.ConfigJson);
            var token = db.SpotifyTokens.Find(_userId);
            if (token == null) return;

            _accessToken = TokenProtection.Unprotect(token.AccessToken);
            _refreshToken = TokenProtection.Unprotect(token.RefreshToken);
            _expiresAt = token.ExpiresAt;
            _logger.LogInformation("Loaded Spotify token from DB for user {UserId}", _userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load Spotify token from DB");
        }
    }
}
