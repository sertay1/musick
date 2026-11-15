using System.IO;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;

namespace MusicBot.Core.Services;

public class QueueService : IQueueService
{
    private readonly List<QueueItem> _upcoming = new();
    private readonly object _lock = new();
    private QueueItem? _currentItem;
    private int _progressMs;
    private bool _isPlaying;
    private Song? _spotifyTrack;
    private readonly Func<string?, bool> _isBlocked;
    private int _maxSize;
    private int _maxPerUser;
    private bool _lastAdvanceFromBackground;
    private int _lastAdvancePlaylistIndex;

    // Background playlist (cyclic, plays when _upcoming is empty)
    private List<Song> _backgroundPlaylist = new();
    private int _playlistIndex = 0;
    private string? _backgroundPlaylistName;

    // Compatibility with saved queues from the previous early-preparation transport.
    public void ReleasePreparedTrack(string id) { lock (_lock) { var item = _upcoming.Find(i => i.Id == id); if (item != null) item.PreparedForSpotify = false; } }
    public bool TryReserveNextTrack(string id) => false;

    public QueueItem? AdvanceIfIdle() { lock (_lock) return _currentItem == null ? Advance() : null; }

    public QueueItem? TryStartFallback(Song song)
    {
        lock (_lock)
        {
            if (_currentItem != null || _upcoming.Count > 0 || _backgroundPlaylist.Count > 0 || _isBlocked(song.SpotifyUri)) return null;
            return PlayNow(song, "Otomatik Kuyruk", "autoqueue", isPlaylistItem: true);
        }
    }

    public event Action<QueueState>? OnQueueUpdated;
    public event Action<NowPlayingState>? OnNowPlayingUpdated;
    public event Action<QueueItem>? OnSongAdded;
    public event Action<QueueItem>? OnSongRemoved;

    public int QueueLength
    {
        get { lock (_lock) return _upcoming.Count; }
    }

    public QueueService(int maxSize = 50, int maxPerUser = 3, Func<string?, bool>? isBlocked = null)
    {
        _isBlocked = isBlocked ?? (_ => false);
        _maxSize = maxSize;
        _maxPerUser = maxPerUser;
    }

    public void UpdateLimits(int maxQueueSize, int maxSongsPerUser)
    {
        lock (_lock) { _maxSize = maxQueueSize; _maxPerUser = maxSongsPerUser; }
    }

    public bool MoveUp(string spotifyUri)
    {
        lock (_lock)
        {
            var idx = _upcoming.FindIndex(i => i.Song.SpotifyUri == spotifyUri);
            if (idx <= 0) return false;

            (_upcoming[idx], _upcoming[idx - 1]) = (_upcoming[idx - 1], _upcoming[idx]);
        }
        OnQueueUpdated?.Invoke(GetState());
        return true;
    }

    public bool MoveDown(string spotifyUri)
    {
        lock (_lock)
        {
            var idx = _upcoming.FindIndex(i => i.Song.SpotifyUri == spotifyUri);
            if (idx < 0 || idx >= _upcoming.Count - 1) return false;

            (_upcoming[idx], _upcoming[idx + 1]) = (_upcoming[idx + 1], _upcoming[idx]);
        }
        OnQueueUpdated?.Invoke(GetState());
        return true;
    }

    public void Seed(QueueItem? current, IEnumerable<QueueItem> upcoming)
    {
        lock (_lock)
        {
            _currentItem = current;
            _upcoming.Clear();
            _upcoming.AddRange(upcoming.Where(i => i.Platform != "autoqueue" && i.Song.SpotifyUri != current?.Song.SpotifyUri).DistinctBy(i => i.Song.SpotifyUri));
            foreach (var item in _upcoming) item.PreparedForSpotify = false;
            _progressMs  = 0;
            _isPlaying   = false;
        }
        // Notify UI of restored state
        OnQueueUpdated?.Invoke(GetState());
    }

    public QueueItem AddSong(Song song, string requestedBy, string platform, bool bypassUserLimit = false, bool isPlaylistItem = false, int? maxPerUserOverride = null)
    {
        lock (_lock)
        {
            if (_isBlocked(song.SpotifyUri)) throw new InvalidOperationException("Bu kayıt kara listede. Önce engelini kaldırın.");
            // Duplicate check: already the current track
            if (_currentItem?.Song.SpotifyUri == song.SpotifyUri || _spotifyTrack?.SpotifyUri == song.SpotifyUri)
                throw new InvalidOperationException("Bu şarkı şu anda zaten çalıyor");

            // Duplicate check: already in upcoming queue
            var existingIndex = _upcoming.FindIndex(i => i.Song.SpotifyUri == song.SpotifyUri);
            if (existingIndex >= 0)
            {
                var existing = _upcoming[existingIndex];
                throw new InvalidOperationException(
                    $"Bu şarkı zaten kuyrukta (sıra: {existingIndex + 1}, isteyen: {existing.RequestedBy})");
            }

            if (!bypassUserLimit && _maxSize > 0 && _upcoming.Count >= _maxSize)
                throw new InvalidOperationException($"Kuyruk dolu (maks. {_maxSize} şarkı)");

            var perUserLimit = maxPerUserOverride ?? _maxPerUser;
            if (!bypassUserLimit && perUserLimit > 0)
            {
                var userSongs = _upcoming.Count(i =>
                    i.RequestedBy.Equals(requestedBy, StringComparison.OrdinalIgnoreCase));
                if (userSongs >= perUserLimit)
                    throw new InvalidOperationException($"Kuyrukta zaten {perUserLimit} şarkınız bulunuyor");
            }

            var item = new QueueItem
            {
                Song = song,
                RequestedBy = requestedBy,
                Platform = platform,
                IsPlaylistItem = isPlaylistItem,
            };

            if (!isPlaylistItem)
            {
                // User requests jump ahead of any playlist-origin items already queued
                var firstPlaylistIdx = _upcoming.FindIndex(i => i.IsPlaylistItem);
                if (firstPlaylistIdx >= 0)
                    _upcoming.Insert(firstPlaylistIdx, item);
                else
                    _upcoming.Add(item);
            }
            else
            {
                _upcoming.Add(item);
            }

            OnSongAdded?.Invoke(item);
            EmitUpdate();
            return item;
        }
    }

    public bool BumpToFront(string requestedBy)
    {
        lock (_lock)
        {
            var index = _upcoming.FindIndex(i =>
                i.RequestedBy.Equals(requestedBy, StringComparison.OrdinalIgnoreCase));

            if (index == -1) return false;
            if (index == 0)  return true; // already at front


            var item = _upcoming[index];
            _upcoming.RemoveAt(index);
            _upcoming.Insert(0, item);
            EmitUpdate();
            return true;
        }
    }

    /// <summary>
    /// Makes the donor's queued song the current track, pushing the interrupted song
    /// back to position 0 in the upcoming queue so it plays next.
    /// </summary>
    public bool InterruptForUser(string requestedBy)
    {
        lock (_lock)
        {
            var index = _upcoming.FindIndex(i =>
                i.RequestedBy.Equals(requestedBy, StringComparison.OrdinalIgnoreCase));

            if (index == -1) return false;

            var userItem = _upcoming[index];
            if (_isBlocked(userItem.Song.SpotifyUri)) return false;
            userItem.PreparedForSpotify = false;
            _upcoming.RemoveAt(index);

            if (_currentItem != null)
                _upcoming.Insert(0, _currentItem);

            _currentItem = userItem;
            _progressMs  = 0;
            _isPlaying   = true;
            EmitUpdate();
            return true;
        }
    }

    public bool RemoveAt(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _upcoming.Count) return false;

            var removed = _upcoming[index];
            _upcoming.RemoveAt(index);
            OnSongRemoved?.Invoke(removed);
            EmitUpdate();
            return true;
        }
    }

    public bool RemoveByUri(string spotifyUri)
    {
        if (string.IsNullOrWhiteSpace(spotifyUri)) return false;
        var cleanTarget = spotifyUri.Trim();
        var bareTarget = cleanTarget.Replace("spotify:track:", "", StringComparison.OrdinalIgnoreCase);

        lock (_lock)
        {
            bool removedAny = false;
            QueueItem? removedItem = null;

            var index = _upcoming.FindIndex(i =>
                string.Equals(i.Song.SpotifyUri, cleanTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.Song.SpotifyUri?.Replace("spotify:track:", "", StringComparison.OrdinalIgnoreCase), bareTarget, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {

                removedItem = _upcoming[index];
                _upcoming.RemoveAt(index);
                removedAny = true;
            }

            // Also check and remove all occurrences from _backgroundPlaylist
            if (_backgroundPlaylist.Count > 0)
            {
                for (int i = _backgroundPlaylist.Count - 1; i >= 0; i--)
                {
                    var bgUri = _backgroundPlaylist[i].SpotifyUri;
                    if (string.Equals(bgUri, cleanTarget, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(bgUri?.Replace("spotify:track:", "", StringComparison.OrdinalIgnoreCase), bareTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        var bgSong = _backgroundPlaylist[i];
                        _backgroundPlaylist.RemoveAt(i);
                        removedAny = true;

                        if (removedItem == null)
                        {
                            removedItem = new QueueItem
                            {
                                Song = bgSong,
                                RequestedBy = "Playlist",
                                Platform = "web",
                                IsPlaylistItem = true
                            };
                        }

                        // Fix _playlistIndex
                        if (_backgroundPlaylist.Count == 0)
                        {
                            _playlistIndex = 0;
                        }
                        else if (i < _playlistIndex)
                        {
                            _playlistIndex--;
                        }
                        else if (_playlistIndex >= _backgroundPlaylist.Count)
                        {
                            _playlistIndex = 0;
                        }
                    }
                }
            }

            if (!removedAny) return false;

            if (removedItem != null)
            {
                OnSongRemoved?.Invoke(removedItem);
            }
            EmitUpdate();
            return true;
        }
    }

    public QueueItem? Skip()
    {
        lock (_lock)
        {
            var skipped = _currentItem;
            Advance();
            return skipped;
        }
    }

    public QueueItem? Revoke(string requestedBy)
    {
        lock (_lock)
        {
            var index = _upcoming.FindIndex(i =>
                i.RequestedBy.Equals(requestedBy, StringComparison.OrdinalIgnoreCase));

            if (index == -1) return null;

            var removed = _upcoming[index];
            _upcoming.RemoveAt(index);
            OnSongRemoved?.Invoke(removed);
            EmitUpdate();
            return removed;
        }
    }

    public bool Bump(string requestedBy)
    {
        lock (_lock)
        {
            var index = _upcoming.FindIndex(i =>
                i.RequestedBy.Equals(requestedBy, StringComparison.OrdinalIgnoreCase));

            if (index <= 0) return false;


            var item = _upcoming[index];
            _upcoming.RemoveAt(index);
            _upcoming.Insert(index - 1, item);
            EmitUpdate();
            return true;
        }
    }

    public QueueItem PlayNow(Song song, string requestedBy, string platform, bool isPlaylistItem = false)
    {
        lock (_lock)
        {
            if (_isBlocked(song.SpotifyUri)) throw new InvalidOperationException("Bu kayıt kara listede. Önce engelini kaldırın.");

            // AutoQueue replaces an exhausted pool item; it must not put it back on top.
            // Explicit user interruptions retain the interrupted song.
            if (_currentItem != null && platform != "autoqueue" && _currentItem.Song.SpotifyUri != song.SpotifyUri)
                _upcoming.Insert(0, _currentItem);
            _upcoming.RemoveAll(i => i.Song.SpotifyUri == song.SpotifyUri);
            _spotifyTrack = null;

            var item = new QueueItem { Song = song, RequestedBy = requestedBy, Platform = platform, IsPlaylistItem = isPlaylistItem };
            _currentItem = item;
            _progressMs  = 0;
            _isPlaying   = true;
            EmitUpdate();
            return item;
        }
    }

    public bool Reorder(string spotifyUri, int toIndex)
    {
        lock (_lock)
        {
            var fromIndex = _upcoming.FindIndex(i => i.Song.SpotifyUri == spotifyUri);
            if (fromIndex >= 0)
            {
                toIndex = Math.Clamp(toIndex, 0, _upcoming.Count - 1);
                if (fromIndex == toIndex) return true;


                var item = _upcoming[fromIndex];
                _upcoming.RemoveAt(fromIndex);
                _upcoming.Insert(toIndex, item);
                EmitUpdate();
                return true;
            }

            if (_backgroundPlaylist.Count == 0) return false;

            var hiddenUris = new HashSet<string>(_upcoming.Select(i => i.Song.SpotifyUri));
            if (_currentItem != null) hiddenUris.Add(_currentItem.Song.SpotifyUri);

            var visiblePlaylist = new List<Song>();
            var hiddenPlaylist = new List<Song>();
            for (var i = 0; i < _backgroundPlaylist.Count; i++)
            {
                var song = _backgroundPlaylist[(_playlistIndex + i) % _backgroundPlaylist.Count];
                if (hiddenUris.Contains(song.SpotifyUri))
                    hiddenPlaylist.Add(song);
                else
                    visiblePlaylist.Add(song);
            }

            fromIndex = visiblePlaylist.FindIndex(s => s.SpotifyUri == spotifyUri);
            if (fromIndex < 0) return false;

            var playlistToIndex = Math.Clamp(toIndex - _upcoming.Count, 0, visiblePlaylist.Count - 1);
            if (fromIndex == playlistToIndex) return true;

            var moved = visiblePlaylist[fromIndex];
            visiblePlaylist.RemoveAt(fromIndex);
            visiblePlaylist.Insert(playlistToIndex, moved);

            _backgroundPlaylist = visiblePlaylist.Concat(hiddenPlaylist).ToList();
            _playlistIndex = 0;
            EmitUpdate();
            return true;
        }
    }

    public QueueItem? Advance()
    {
        lock (_lock)
        {
            _lastAdvanceFromBackground = false;
            _lastAdvancePlaylistIndex = _playlistIndex;
            _spotifyTrack = null;
            while (_upcoming.Count > 0 && _isBlocked(_upcoming[0].Song.SpotifyUri)) _upcoming.RemoveAt(0);
            // User requests take priority
            if (_upcoming.Count > 0)
            {
                _currentItem = _upcoming[0];
                _currentItem.PreparedForSpotify = false;
                _upcoming.RemoveAt(0);
                _progressMs = 0;
                _isPlaying = true;
                EmitUpdate();
                return _currentItem;
            }

            // Fall back to background playlist (cyclic)
            Song? background = null;
            for (var checkedSongs = 0; checkedSongs < _backgroundPlaylist.Count; checkedSongs++)
            {
                var candidate = _backgroundPlaylist[_playlistIndex];
                _playlistIndex = (_playlistIndex + 1) % _backgroundPlaylist.Count;
                if (!_isBlocked(candidate.SpotifyUri)) { background = candidate; break; }
            }
            if (background != null)
            {
                _lastAdvanceFromBackground = true;
                var src = background;

                _currentItem = new QueueItem
                {
                    Song = new Song
                    {
                        SpotifyUri    = src.SpotifyUri,
                        Title         = src.Title,
                        Artist        = src.Artist,
                        CoverUrl      = src.CoverUrl,
                        DurationMs    = src.DurationMs,
                        LocalFilePath = src.LocalFilePath,
                    },
                    RequestedBy    = "Playlist",
                    Platform       = "web",
                    IsPlaylistItem = true,
                };
                _progressMs = 0;
                _isPlaying  = true;
                EmitUpdate();
                return _currentItem;
            }

            _currentItem = null;
            _progressMs = 0;
            _isPlaying = false;
            EmitUpdate();
            return null;
        }
    }

    // Clear only the failed item; never promote (and accidentally skip) a waiting request.
    public bool DiscardCurrent(string expectedId)
    {
        lock (_lock)
        {
            if (_currentItem?.Id != expectedId) return false;
            _currentItem = null;
            _progressMs = 0;
            _isPlaying = false;
            _spotifyTrack = null;
        }
        EmitUpdate();
        return true;
    }

    public bool RestoreFailedAdvance(QueueItem? previous, QueueItem failed, NowPlayingState previousState)
    {
        lock (_lock)
        {
            if (_currentItem?.Id != failed.Id) return false;
            if (_lastAdvanceFromBackground)
                _playlistIndex = _lastAdvancePlaylistIndex;
            else if (!_upcoming.Any(i => i.Id == failed.Id))
                _upcoming.Insert(0, failed);
            _currentItem = previous;
            _progressMs = previousState.ProgressMs;
            _isPlaying = previousState.IsPlaying;
            _spotifyTrack = previousState.SpotifyTrack;
        }
        EmitUpdate();
        return true;
    }

    public void Shuffle()
    {
        lock (_lock)
        {
            var rng = new Random();
            var first = 0;
            for (int i = _upcoming.Count - 1; i > first; i--)
            {
                int j = rng.Next(first, i + 1);
                (_upcoming[i], _upcoming[j]) = (_upcoming[j], _upcoming[i]);
            }
        }
        OnQueueUpdated?.Invoke(GetState());
    }

    public void ShuffleBackgroundPlaylist()
    {
        lock (_lock)
        {
            if (_backgroundPlaylist.Count <= 1) return;
            var rng = new Random();
            for (int i = _backgroundPlaylist.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (_backgroundPlaylist[i], _backgroundPlaylist[j]) = (_backgroundPlaylist[j], _backgroundPlaylist[i]);
            }
            _playlistIndex = 0;
        }
        OnQueueUpdated?.Invoke(GetState());
    }

    public bool PromoteFromBackground(string spotifyUri, int? toIndex = null)
    {
        lock (_lock)
        {
            var bgIdx = _backgroundPlaylist.FindIndex(s => s.SpotifyUri == spotifyUri);
            if (bgIdx < 0) return false;

            if (_currentItem?.Song.SpotifyUri == spotifyUri) return false;
            if (_upcoming.Any(i => i.Song.SpotifyUri == spotifyUri)) return false;

            var src = _backgroundPlaylist[bgIdx];
            var item = new QueueItem
            {
                Song = new Song
                {
                    SpotifyUri    = src.SpotifyUri,
                    Title         = src.Title,
                    Artist        = src.Artist,
                    CoverUrl      = src.CoverUrl,
                    DurationMs    = src.DurationMs,
                    LocalFilePath = src.LocalFilePath,
                },
                RequestedBy    = "LocalUser",
                Platform       = "web",
                IsPlaylistItem = false,
            };

            // Insert at toIndex (clamped to user-item range) or before first playlist item
            var firstPlaylistIdx = _upcoming.FindIndex(i => i.IsPlaylistItem);
            var userCount = firstPlaylistIdx >= 0 ? firstPlaylistIdx : _upcoming.Count;

            if (toIndex.HasValue)
            {
                var insertAt = Math.Clamp(toIndex.Value, 0, userCount);

                _upcoming.Insert(insertAt, item);
            }
            else if (firstPlaylistIdx >= 0)
            {
                _upcoming.Insert(firstPlaylistIdx, item);
            }
            else
            {
                _upcoming.Add(item);
            }

            // Remove from background playlist and fix index
            _backgroundPlaylist.RemoveAt(bgIdx);
            if (_backgroundPlaylist.Count == 0)
                _playlistIndex = 0;
            else if (bgIdx < _playlistIndex)
                _playlistIndex--;
            else if (_playlistIndex >= _backgroundPlaylist.Count)
                _playlistIndex = 0;

            OnSongAdded?.Invoke(item);
            EmitUpdate();
            return true;
        }
    }

    public List<Song> GetNextDownloadCandidates(int count)
    {
        lock (_lock)
        {
            var candidates = new List<Song>();
            var seen = new HashSet<string>();
            if (_currentItem != null) seen.Add(_currentItem.Song.SpotifyUri);

            // First: user items
            foreach (var item in _upcoming)
            {
                if (seen.Contains(item.Song.SpotifyUri)) continue;
                seen.Add(item.Song.SpotifyUri);
                if (!IsDownloaded(item.Song))
                    candidates.Add(item.Song);
                if (candidates.Count >= count) return candidates;
            }

            // Then: next bg playlist songs
            int bgCount = _backgroundPlaylist.Count;
            for (int i = 0; i < bgCount && candidates.Count < count; i++)
            {
                var song = _backgroundPlaylist[(_playlistIndex + i) % bgCount];
                if (seen.Contains(song.SpotifyUri)) continue;
                seen.Add(song.SpotifyUri);
                if (!IsDownloaded(song))
                    candidates.Add(song);
            }

            return candidates;
        }
    }

    private static bool IsDownloaded(Song song) =>
        song.LocalFilePath != null &&
        File.Exists(song.LocalFilePath) &&
        new FileInfo(song.LocalFilePath).Length > 100_000;

    public void SetBackgroundPlaylist(IEnumerable<Song> songs, string? playlistName = null)
    {
        lock (_lock)
        {
            _backgroundPlaylist = new List<Song>(songs);
            _backgroundPlaylistName = playlistName;
            _playlistIndex = 0;
            // Remove any stale playlist-origin items from the explicit queue so the
            // new playlist's songs take effect immediately without old ones blocking.
            _upcoming.RemoveAll(i => i.IsPlaylistItem);
        }
        OnQueueUpdated?.Invoke(GetState());
    }

    public void ClearUserQueue(bool clearAll = false)
    {
        lock (_lock)
        {

            if (clearAll)
            {
                _upcoming.Clear();
                _backgroundPlaylist.Clear();
                _backgroundPlaylistName = null;
                _playlistIndex = 0;
            }
            else
                _upcoming.RemoveAll(i => !i.IsPlaylistItem);
        }
        EmitUpdate();
    }

    public void ClearBackgroundPlaylist()
    {
        lock (_lock)
        {
            _backgroundPlaylist.Clear();
            _backgroundPlaylistName = null;
            _playlistIndex = 0;
        }
        OnQueueUpdated?.Invoke(GetState());
    }

    public (List<Song> Songs, int Index) GetBackgroundPlaylist()
    {
        lock (_lock) return (new List<Song>(_backgroundPlaylist), _playlistIndex);
    }

    public void UpdateProgress(int progressMs, bool isPlaying, Song? spotifyTrack = null)
    {
        lock (_lock)
        {
            _progressMs = progressMs;
            _isPlaying = isPlaying;
            if (spotifyTrack != null)
            {
                _spotifyTrack = spotifyTrack;
            }
            else if (_currentItem != null)
            {
                _spotifyTrack = null;
            }
            // If _currentItem is null and spotifyTrack is null, preserve existing _spotifyTrack
            // so background player ticks or pause/resume calls do not erase active Spotify track data.
        }
        OnNowPlayingUpdated?.Invoke(GetNowPlaying());
    }

    public void ClearSpotifyTrack()
    {
        lock (_lock)
        {
            _spotifyTrack = null;
        }
        OnNowPlayingUpdated?.Invoke(GetNowPlaying());
    }

    public NowPlayingState GetNowPlaying()
    {
        lock (_lock)
        {
            return new NowPlayingState
            {
                Item = _currentItem,
                ProgressMs = _progressMs,
                IsPlaying = _isPlaying,
                SpotifyTrack = _spotifyTrack
            };
        }
    }

    public QueueItem? GetCurrentItem()
    {
        lock (_lock) return _currentItem;
    }

    public List<QueueItem> GetUpcoming()
    {
        lock (_lock) return new List<QueueItem>(_upcoming);
    }

    public QueueState GetState()
    {
        lock (_lock)
        {
            // Start with the real upcoming queue (user + any playlist items already in queue)
            var upcoming = new List<QueueItem>(_upcoming);

            // Synthesise visibility of remaining background-playlist songs so the UI
            // can show the full upcoming flow even before songs are downloaded.
            // Include the complete playlist; the client controls scrolling.
            if (_backgroundPlaylist.Count > 0)
            {
                var existingUris = new HashSet<string>(upcoming.Select(i => i.Song.SpotifyUri));
                if (_currentItem != null) existingUris.Add(_currentItem.Song.SpotifyUri);
                if (_spotifyTrack != null) existingUris.Add(_spotifyTrack.SpotifyUri);

                int count = _backgroundPlaylist.Count;
                for (int i = 0; i < count; i++)
                {
                    int idx = (_playlistIndex + i) % count;
                    var src = _backgroundPlaylist[idx];
                    if (existingUris.Contains(src.SpotifyUri)) continue;

                    upcoming.Add(new QueueItem
                    {
                        Song = new Song
                        {
                            SpotifyUri    = src.SpotifyUri,
                            Title         = src.Title,
                            Artist        = src.Artist,
                            CoverUrl      = src.CoverUrl,
                            DurationMs    = src.DurationMs,
                            LocalFilePath = src.LocalFilePath,
                        },
                        RequestedBy    = "Playlist",
                        Platform       = "web",
                        IsPlaylistItem = true,
                    });
                    existingUris.Add(src.SpotifyUri);
                }
            }

            return new QueueState
            {
                NowPlaying           = GetNowPlaying(),
                Upcoming             = upcoming,
                BackgroundPlaylist   = new List<Song>(_backgroundPlaylist),
                PlaylistIndex        = _playlistIndex,
                ActivePlaylistName   = _backgroundPlaylistName,
            };
        }
    }

    public bool MarkDownloadError(string spotifyUri, string? error)
    {
        bool found = false;
        lock (_lock)
        {
            var item = _upcoming.Find(i => i.Song.SpotifyUri == spotifyUri);
            if (item != null) { item.DownloadError = error; found = true; }
            else if (_currentItem?.Song.SpotifyUri == spotifyUri) { _currentItem.DownloadError = error; found = true; }
        }
        if (found) EmitUpdate();
        return found;
    }

    public bool UpdateSongForAlternative(string oldUri, Song newSong)
    {
        bool found = false;
        lock (_lock)
        {
            var item = _upcoming.Find(i => i.Song.SpotifyUri == oldUri);
            if (item != null) { item.Song = newSong; item.DownloadError = null; found = true; }
            else if (_currentItem?.Song.SpotifyUri == oldUri) { _currentItem.Song = newSong; _currentItem.DownloadError = null; found = true; }
        }
        if (found) EmitUpdate();
        return found;
    }

    private void EmitUpdate()
    {
        OnQueueUpdated?.Invoke(GetState());
    }
}
