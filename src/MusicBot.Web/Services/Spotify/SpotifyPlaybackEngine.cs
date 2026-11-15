using MusicBot.Core.Interfaces;
namespace MusicBot.Services.Spotify;

public interface ISpotifyPlaybackPort
{
    Task<PlaybackState?> ReadAsync();
    Task<SpotifyQueueState> QueueAsync();
    Task EnqueueAsync(string uri);
    Task PauseAsync() => throw new NotSupportedException();
    Task<IAsyncDisposable> SilenceAsync() => throw new SpotifyPlayerCommandException(409, "Bu Spotify cihazında sessiz sıra düzeltmesi desteklenmiyor.");
    Task<bool> EnsureSessionAsync() => Task.FromResult(false);
    Task NextAsync();
    Task ResumeAsync();
    Task WaitAsync();
}
public enum SpotifyTransitionPhase { Idle, Preparing, Queued, Switching, Confirmed, Uncertain, Cancelled }
public sealed class SpotifyPlaybackEngine
{
    public SpotifyTransitionPhase Phase { get; private set; }
    public string? RequestedUri { get; private set; }
    public PlaybackState? Observed { get; private set; }
    // A timed-out next command may still be executing at Spotify. Never send it again
    // for the same transition while the old track remains the observed track.
    private readonly HashSet<string> _unconfirmedEnqueues = new(StringComparer.Ordinal);
    private string? _advanceAwaitingUri;
    private string? _advanceFromUri;
    // Called by SpotifyService while holding its playback command lock.
    internal bool MayHaveQueued(string uri) => _unconfirmedEnqueues.Contains(uri);
    private readonly SemaphoreSlim _commands = new(1, 1);
    public async Task<bool> PrepareAsync(string uri, ISpotifyPlaybackPort port, Func<bool> current)
    {
        await _commands.WaitAsync();
        try
        {
            if (!current()) return false;
            var queue = await port.QueueAsync();
            if (!current()) return false;
            if (queue.CurrentlyPlaying?.SpotifyUri == uri) return false;
            if (!queue.Queue.Any(song => song.SpotifyUri == uri) && _unconfirmedEnqueues.Add(uri))
            {
                try { await port.EnqueueAsync(uri); }
                catch (Exception ex) when (ex is SpotifyPlayerCommandException or SpotifyRateLimitException or SpotifyCommandCancelledException) { _unconfirmedEnqueues.Remove(uri); throw; }
                if (!current()) return false;
                queue = await port.QueueAsync();
            }
            return current() && queue.Queue.FirstOrDefault()?.SpotifyUri == uri;
        }
        finally { _commands.Release(); }
    }

    public async Task SelectAsync(string uri, ISpotifyPlaybackPort port, Func<bool> current)
    {
        await _commands.WaitAsync();
        try
        {
            if (!current()) { Phase = SpotifyTransitionPhase.Cancelled; return; }
            var resumed = false;
            RequestedUri = uri; Phase = SpotifyTransitionPhase.Preparing;
            Observed = await port.ReadAsync();
            if (!current()) { Phase = SpotifyTransitionPhase.Cancelled; return; }
            if (_advanceAwaitingUri != null && Observed?.MatchesTrack(_advanceAwaitingUri) == true)
                _advanceAwaitingUri = null;
            if (_advanceAwaitingUri != null && (Observed == null || Observed.TrackUri == _advanceFromUri))
                throw new SpotifyPlayerCommandException(409, "Önceki Spotify geçişi hâlâ doğrulanmadı. Çift atlama komutu gönderilmedi; istek korundu.");
            if (Observed?.MatchesTrack(uri) == true)
            {
                _unconfirmedEnqueues.Remove(uri);
                if (Observed.IsPlaying) { Phase = SpotifyTransitionPhase.Confirmed; return; }
                await port.ResumeAsync();
                resumed = true;
            }
            else
            {
                // Wake an available desktop device without replacing its playback context.
                if (Observed == null || string.IsNullOrEmpty(Observed.TrackUri))
                {
                    if (!await port.EnsureSessionAsync()) throw new SpotifyPlayerCommandException(409, "Spotify cihazı henüz hazır değil. Spotify'ı aç düğmesini kullanın ve Musick'ten bir şarkı seçin.");
                    if (!current()) { Phase = SpotifyTransitionPhase.Cancelled; return; }
                    Observed = await port.ReadAsync();
                }
                var queue = await port.QueueAsync();
                if (!current()) { Phase = SpotifyTransitionPhase.Cancelled; return; }
                if (queue.CurrentlyPlaying?.SpotifyUri != uri && !queue.Queue.Any(s => s.SpotifyUri == uri)
                    && _unconfirmedEnqueues.Add(uri))
                {
                    try { await port.EnqueueAsync(uri); }
                    catch (Exception ex) when (ex is SpotifyPlayerCommandException or SpotifyRateLimitException or SpotifyCommandCancelledException) { _unconfirmedEnqueues.Remove(uri); throw; }
                }
                Phase = SpotifyTransitionPhase.Queued;
                var ready = false;
                for (var check = 0; check < 6; check++)
                {
                    if (check > 0)
                    {
                        await port.WaitAsync();
                        if (!current()) { Phase = SpotifyTransitionPhase.Cancelled; return; }
                        queue = await port.QueueAsync();
                    }
                    if (!current()) { Phase = SpotifyTransitionPhase.Cancelled; return; }
                    if (queue.CurrentlyPlaying?.SpotifyUri == uri) { ready = true; break; }
                    var targetIndex = queue.Queue.FindIndex(song => song.SpotifyUri == uri);
                    if (targetIndex > 0)
                    {
                        await SelectQueuedTargetSilentlyAsync(uri, queue, targetIndex, port, current);
                        if (!current()) { Phase = SpotifyTransitionPhase.Cancelled; return; }
                        ready = true; break;
                    }
                    if (queue.Queue.FirstOrDefault()?.SpotifyUri == uri)
                    {
                        if (!current()) { Phase = SpotifyTransitionPhase.Cancelled; return; }
                        Phase = SpotifyTransitionPhase.Switching;
                        _advanceAwaitingUri = uri;
                        _advanceFromUri = Observed?.TrackUri;
                        try { await port.NextAsync(); }
                        catch (Exception ex) when (ex is SpotifyPlayerCommandException or SpotifyRateLimitException or SpotifyCommandCancelledException) { _advanceAwaitingUri = null; throw; }
                        ready = true;
                        break;
                    }
                }
                if (!ready) throw new SpotifyPlayerCommandException(409, "Şarkı Spotify sırasına eklendi ancak ilk sırada doğrulanamadı. Başka şarkılar atlanmadı; istek korundu.");
            }
            for (var check = 0; check < 6; check++)
            {
                if (check > 0) await port.WaitAsync();
                if (!current()) { Phase = SpotifyTransitionPhase.Cancelled; return; }
                Observed = await port.ReadAsync();
                if (!current()) { Phase = SpotifyTransitionPhase.Cancelled; return; }
                if (!resumed && Observed?.MatchesTrack(uri) == true && !Observed.IsPlaying)
                {
                    await port.ResumeAsync();
                    resumed = true;
                    continue;
                }
                if (Observed?.MatchesTrack(uri) == true && Observed.IsPlaying) { _unconfirmedEnqueues.Remove(uri); _advanceAwaitingUri = null; Phase = SpotifyTransitionPhase.Confirmed; return; }
            }
            throw new SpotifyPlayerCommandException(409, "Spotify geçişinin sonucu henüz doğrulanmadı. Yeniden çalma komutu gönderilmedi; istek korundu.");
        }
        catch (SpotifyCommandCancelledException) { Phase = SpotifyTransitionPhase.Cancelled; }
        catch { Phase = SpotifyTransitionPhase.Uncertain; throw; }
        finally { _commands.Release(); }
    }
    private async Task SelectQueuedTargetSilentlyAsync(string uri, SpotifyQueueState queue, int targetIndex, ISpotifyPlaybackPort port, Func<bool> current)
    {
        // shortcut: recover at most four older native entries; larger queues need manual cleanup to avoid API bursts.
        if (targetIndex > 4) throw new SpotifyPlayerCommandException(409, "Spotify'ın kendi sırasında seçilen parçadan önce çok fazla kayıt var. Spotify kuyruğunu temizleyip tekrar deneyin; başka şarkılar çalınmadı.");
        await using var silence = await port.SilenceAsync();
        var prefix = queue.Queue.Take(targetIndex + 1).Select(song => song.SpotifyUri).ToArray();
        if (!current()) return;
        Observed = await port.ReadAsync();
        if (!current()) return;
        if (Observed == null) throw new SpotifyPlayerCommandException(409, "Spotify sırası sessizce düzenlenemedi; oynatma durumu henüz bilinmiyor.");
        if (Observed.MatchesTrack(uri)) return;
        if (Observed.IsPlaying) await port.PauseAsync();
        var paused = false;
        for (var check = 0; check < 4; check++)
        {
            if (!current()) return;
            Observed = await port.ReadAsync();
            if (!current()) return;
            if (Observed != null && !Observed.IsPlaying) { paused = true; break; }
            await port.WaitAsync();
        }
        if (!paused) throw new SpotifyPlayerCommandException(409, "Spotify duraklaması doğrulanmadı. Yanlış şarkı çalmamak için sıra değişimi durduruldu.");
        foreach (var expected in prefix)
        {
            if (!current()) return;
            var actualQueue = await port.QueueAsync();
            if (!current()) return;
            if (actualQueue.CurrentlyPlaying?.SpotifyUri == uri) return;
            if (actualQueue.Queue.FirstOrDefault()?.SpotifyUri != expected)
                throw new SpotifyPlayerCommandException(409, "Spotify sırası işlem sırasında değişti; ek atlama gönderilmedi.");
            _advanceAwaitingUri = expected; _advanceFromUri = Observed?.TrackUri;
            Phase = SpotifyTransitionPhase.Switching;
            try { await port.NextAsync(); }
            catch (Exception ex) when (ex is SpotifyPlayerCommandException or SpotifyRateLimitException or SpotifyCommandCancelledException) { _advanceAwaitingUri = null; throw; }
            var arrived = false;
            for (var check = 0; check < 6; check++)
            {
                if (check > 0) await port.WaitAsync();
                if (!current()) return;
                Observed = await port.ReadAsync();
                if (!current()) return;
                if (Observed?.IsPlaying == true)
                {
                    await port.PauseAsync();
                    continue;
                }
                if (Observed?.MatchesTrack(expected) == true) { arrived = true; _advanceAwaitingUri = null; break; }
            }
            if (!arrived) throw new SpotifyPlayerCommandException(409, "Spotify'daki sessiz geçiş henüz doğrulanmadı; çift atlama yapılmadı.");
        }
    }

}
