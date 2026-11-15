using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Services.Platforms;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;

namespace MusicBot.Services.Downloader;

/// <summary>
/// Downloads audio from YouTube via yt-dlp (using YoutubeDLSharp).
/// Searches for the best matching video by title/artist/duration,
/// downloads as MP3, and persists to the local library.
/// In-progress downloads are tracked so duplicate requests await the same task.
/// </summary>
public class YtDlpDownloaderService
{
    private static readonly HttpClient SearchClient = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
    private readonly HttpClient _searchClient;
    private readonly ILocalLibraryService _library;
    private readonly MusicLibrarySettings _settings;
    private readonly QueueSettingsService _queueSettings;
    private readonly YouTubeAuthService _youtubeAuth;
    private readonly ILogger<YtDlpDownloaderService> _logger;
    private readonly YoutubeDL _ytdl;
    private readonly YouTubeOAuthService? _youtubeOAuth;

    // trackId → running download task
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inProgress = new();

    /// <summary>Fires with (spotifyUri, percentComplete 0–100) during downloads.</summary>
    public event Action<string, int>? ProgressChanged;

    /// <summary>Fires with (spotifyUri, title, artist) when a real download begins (not served from cache).</summary>
    public event Action<string, string, string>? DownloadStarted;

    /// <summary>Fires with (spotifyUri) when a download completes successfully.</summary>
    public event Action<string>? DownloadCompleted;

    /// <summary>Fires with (spotifyUri, errorMessage) when a download fails.</summary>
    public event Action<string, string>? DownloadFailed;

    public YtDlpDownloaderService(
        ILocalLibraryService library,
        IOptions<MusicLibrarySettings> settings,
        QueueSettingsService queueSettings,
        YouTubeAuthService youtubeAuth,
        ILogger<YtDlpDownloaderService> logger, YouTubeOAuthService? youtubeOAuth = null, IHttpClientFactory? httpFactory = null)
    {
        _searchClient = httpFactory?.CreateClient("YouTubeMetadata") ?? SearchClient;
        _youtubeOAuth = youtubeOAuth;
        _library       = library;
        _settings      = settings.Value;
        _queueSettings = queueSettings;
        _youtubeAuth   = youtubeAuth;
        _logger        = logger;

        Directory.CreateDirectory(_settings.LibraryPath);

        _ytdl = new YoutubeDL
        {
            YoutubeDLPath  = ResolveYtDlpPath(_settings.YtDlpPath),
            FFmpegPath     = ResolveFfmpegPath(_settings.FfmpegPath),
            OutputFolder   = _settings.LibraryPath,
            OverwriteFiles = false,
        };
    }

    /// <summary>
    /// Ensures ffmpeg is available next to the executable.
    /// Downloads it automatically if missing.
    /// Called by YtDlpSetupService at startup.
    /// </summary>
    /// <summary>
    /// Ensures Deno is available next to the executable.
    /// Deno is required by yt-dlp to resolve YouTube's JS challenges (bot detection / 429 errors).
    /// </summary>
    public async Task EnsureDenoAsync()
    {
        var path = Path.Combine(_settings.ToolsDirectory, "deno.exe");

        if (File.Exists(path))
        {
            _logger.LogInformation("Deno mevcut: '{Path}'", path);
            return;
        }

        _logger.LogInformation("Deno bulunamadı — '{Dir}' dizinine otomatik olarak indiriliyor...",
            _settings.ToolsDirectory);
        try
        {
            await YoutubeDLSharp.Utils.DownloadDeno(_settings.ToolsDirectory);
            _logger.LogInformation("Deno başarıyla indirildi: '{Path}'", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deno otomatik olarak indirilemedi.");
        }
    }

    public async Task EnsureFfmpegAsync()
    {
        var path = ResolveFfmpegPath(_settings.FfmpegPath);

        if (File.Exists(path))
        {
            _logger.LogInformation("ffmpeg mevcut: '{Path}'", path);
            _ytdl.FFmpegPath = path;
            return;
        }

        if (IsOnPath(_settings.FfmpegPath))
        {
            _logger.LogInformation("ffmpeg sistem PATH yolunda bulundu");
            return;
        }

        _logger.LogInformation("ffmpeg bulunamadı — '{Dir}' dizinine otomatik olarak indiriliyor...",
            _settings.ToolsDirectory);

        try
        {
            await YoutubeDLSharp.Utils.DownloadFFmpeg(_settings.ToolsDirectory);
            _ytdl.FFmpegPath = path;
            _logger.LogInformation("ffmpeg başarıyla indirildi: '{Path}'", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ffmpeg otomatik olarak indirilemedi.");
        }
    }

    /// <summary>
    /// Resolves the effective ffmpeg path:
    /// 1. Next to the executable (preferred — works after auto-download)
    /// 2. Configured path if absolute and exists
    /// 3. Configured name if on PATH
    /// 4. Falls back to BaseDirectory/ffmpeg.exe (download target)
    /// </summary>
    private string ResolveFfmpegPath(string configured)
    {
        var appDirExe = Path.Combine(_settings.ToolsDirectory, "ffmpeg.exe");
        if (File.Exists(appDirExe)) return appDirExe;
        if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;
        if (IsOnPath(configured)) return configured;
        return appDirExe; // download target
    }

    /// <summary>
    /// Ensures yt-dlp is available next to the executable.
    /// Downloads it if missing, or updates it if the binary is older than 7 days.
    /// Called by YtDlpSetupService at startup.
    /// </summary>
    public async Task EnsureYtDlpAsync()
    {
        var path = _ytdl.YoutubeDLPath;

        if (File.Exists(path))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age.TotalDays < 7)
            {
                _logger.LogInformation("yt-dlp disponible en '{Path}' (actualizado hace {Days:F0} días)",
                    path, age.TotalDays);
                return;
            }

            _logger.LogInformation(
                "yt-dlp tiene {Days:F0} días de antigüedad — actualizando automáticamente…",
                age.TotalDays);
            await UpdateYtDlpAsync();
            return;
        }

        if (IsOnPath(_settings.YtDlpPath))
        {
            _logger.LogInformation("yt-dlp encontrado en PATH");
            return;
        }

        _logger.LogInformation("yt-dlp bulunamadı — '{Dir}' dizinine otomatik olarak indiriliyor...",
            _settings.ToolsDirectory);

        try
        {
            await YoutubeDLSharp.Utils.DownloadYtDlp(_settings.ToolsDirectory);
            _ytdl.YoutubeDLPath = path;
            _logger.LogInformation("yt-dlp başarıyla indirildi: '{Path}'", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp otomatik olarak indirilemedi.");
        }
    }

    /// <summary>
    /// Downloads the latest yt-dlp release from GitHub, replacing the current binary.
    /// Safe to call while downloads are in progress — existing tasks continue with the old binary.
    /// </summary>
    public async Task<string> UpdateYtDlpAsync()
    {
        var path = Path.Combine(_settings.ToolsDirectory, "yt-dlp.exe");

        _logger.LogInformation("Actualizando yt-dlp…");
        try
        {
            await YoutubeDLSharp.Utils.DownloadYtDlp(_settings.ToolsDirectory);
            _ytdl.YoutubeDLPath = path;

            // Read the version from the new binary
            var version = await GetYtDlpVersionAsync();
            _logger.LogInformation("yt-dlp actualizado correctamente → {Version}", version);
            return version;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al actualizar yt-dlp");
            throw;
        }
    }

    private async Task<string> GetYtDlpVersionAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = _ytdl.YoutubeDLPath,
                Arguments              = "--version",
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "desconocida";
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output.Trim();
        }
        catch
        {
            return "desconocida";
        }
    }

    /// <summary>
    /// Resolves the effective yt-dlp path:
    /// 1. Tools directory (%AppData%\MusicBot\tools\ — survives Velopack updates)
    /// 2. Configured path if absolute and exists
    /// 3. Configured name if on PATH
    /// 4. Falls back to ToolsDirectory/yt-dlp.exe (download target)
    /// </summary>
    private string ResolveYtDlpPath(string configured)
    {
        var toolsDirExe = Path.Combine(_settings.ToolsDirectory, "yt-dlp.exe");
        if (File.Exists(toolsDirExe)) return toolsDirExe;
        if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;
        if (IsOnPath(configured)) return configured;
        return toolsDirExe; // download target
    }

    private static bool IsOnPath(string name)
    {
        var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        return envPath.Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Combine(dir, name)) ||
                        File.Exists(Path.Combine(dir, name + ".exe")));
    }

    /// <summary>Fire-and-forget: starts a background download if not already running.</summary>
    public void StartDownload(Song song) { if (!song.SpotifyUri.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) && !song.SpotifyUri.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)) _ = ObserveDownloadAsync(song); }

    private async Task ObserveDownloadAsync(Song song)
    {
        try { await GetOrStartDownloadAsync(song); }
        catch (Exception ex) { _logger.LogDebug(ex, "Background download failed for {Uri}", song.SpotifyUri); }
    }

    /// <summary>Returns the local file path, waiting for an in-progress download or starting one.</summary>
    public async Task<string> GetOrStartDownloadAsync(Song song)
    {
        if (song.SpotifyUri.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("YouTube şarkıları çevrimiçi oynatılır; ses dosyası indirilmez.");
        if (song.SpotifyUri.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Spotify şarkıları Spotify uygulamasında oynatılır; YouTube ses dosyası indirilmez.");
        var key = song.SpotifyUri;
        // GetOrAdd may run its factory more than once. Only the winning Lazy starts I/O.
        var download = _inProgress.GetOrAdd(key,
            _ => new Lazy<Task<string>>(() => DownloadCoreAsync(song)));
        try { return await download.Value; }
        catch
        {
            // An older failed attempt must not evict a newer replacement.
            ((ICollection<KeyValuePair<string, Lazy<Task<string>>>>)_inProgress)
                .Remove(new(key, download));
            throw;
        }
    }

    /// <summary>
    /// Removes a completed (possibly corrupt) download task from the cache so that the
    /// next call to GetOrStartDownloadAsync will re-download the file from scratch.
    /// </summary>
    public void InvalidateCachedDownload(string spotifyUri) =>
        _inProgress.TryRemove(spotifyUri, out _);

    // ── YouTube metadata search ──────────────────────────────────────────────
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Song> _musicVideos = new();
    public async Task<Song> ValidateMusicVideoAsync(string uri, CancellationToken cancellationToken = default)
    {
        if (!_queueSettings.YouTubeEnabled) throw new InvalidOperationException("YouTube istekleri ayarlardan kapatılmış.");
        var id = uri.StartsWith("youtube:") ? uri[8..] : uri;
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]{11}$")) throw new InvalidOperationException("Geçerli YouTube şarkı bağlantısı bulunamadı.");
        if (_musicVideos.TryGetValue(id, out var cached)) return cached;
        using var doc = await FetchVideoInfoAsync(id, cancellationToken);
        if (!_queueSettings.YouTubeEnabled) throw new InvalidOperationException("YouTube istekleri ayarlardan kapatılmış.");
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("YouTube müzik bilgisi doğrulanamadı. Tekrar deneyin.");
        var root = doc.RootElement;
        YouTubeMusicPolicy.Validate(root);
        if (!_queueSettings.YouTubeEnabled) throw new InvalidOperationException("YouTube istekleri ayarlardan kapatılmış.");
        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var artist = root.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(a.GetString()) ? a.GetString()! : root.TryGetProperty("channel", out var c) ? c.GetString() ?? "" : "";
        var song = new Song { SpotifyUri = "youtube:" + id, Title = title, Artist = artist, CoverUrl = "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg", DurationMs = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? (int)(d.GetDouble() * 1000) : 0, Platform = "youtube" };
        if (_musicVideos.Count > 512) _musicVideos.Clear();
        _musicVideos[id] = song;
        return song;
    }

    private async Task<JsonDocument?> FetchVideoInfoAsync(string id, CancellationToken cancellationToken)
    {
        // Read metadata only: extracting playable formats is unnecessary for the iframe.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            request.Content = JsonContent.Create(new { context = new { client = new { clientName = "WEB", clientVersion = "2.20261111.01.00", hl = "en", gl = "US" } }, videoId = id });
            using var response = await _searchClient.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var metadata = YouTubeVideoMetadata.Parse(body.RootElement, id);
            if (metadata != null) return JsonDocument.Parse(JsonSerializer.Serialize(metadata));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "YouTube metadata endpoint unavailable; using metadata extractor"); }
        return await RunYtDlpJsonAsync(["--skip-download", "--dump-single-json", "--no-playlist", "--ignore-no-formats-error", "--extractor-args", "youtube:lang=en;skip=hls,dash", "https://www.youtube.com/watch?v=" + id], TimeSpan.FromSeconds(12), cancellationToken);
    }

    /// <summary>Fetch metadata for a single YouTube video by its ID.</summary>
    public async Task<Song?> FetchVideoMetadataAsync(string videoId) => await ValidateMusicVideoAsync(videoId);

    /// <summary>
    /// Fetch all tracks from a YouTube playlist URL.
    /// Uses <c>--print</c> (tab-separated, one line per entry) — the most reliable method
    /// across yt-dlp versions.  Thumbnail is constructed from the video ID.
    /// </summary>
    /// <summary>
    /// Normalizes YouTube and YouTube Music URLs to the standard youtube.com form
    /// so yt-dlp always uses the well-tested YouTube extractor.
    /// e.g. music.youtube.com/playlist?list=X  →  youtube.com/playlist?list=X
    ///      music.youtube.com/watch?v=X&amp;list=Y →  youtube.com/playlist?list=Y
    /// </summary>
    internal static string NormalizeYouTubeUrl(string url)
    {
        if (!url.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase))
            return url;

        try
        {
            var uri   = new Uri(url);
            var query = uri.Query.TrimStart('?');
            var listId = query.Split('&')
                              .Select(p => p.Split('='))
                              .FirstOrDefault(p => p.Length == 2 && p[0].Equals("list", StringComparison.OrdinalIgnoreCase))?[1];

            if (!string.IsNullOrEmpty(listId))
                return $"https://www.youtube.com/playlist?list={Uri.UnescapeDataString(listId)}";
        }
        catch { }

        // Fallback: just swap the host
        return url.Replace("music.youtube.com", "www.youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<(string Url, string Title)>> GetYouTubePlaylistsAsync(string? channelUrl)
    {
        await _youtubeAuth.EnsureLoadedAsync();
        if (string.IsNullOrWhiteSpace(channelUrl) && _youtubeOAuth?.IsConnected == true)
            return await _youtubeOAuth.PlaylistsAsync();

        if (string.IsNullOrWhiteSpace(channelUrl) && !_youtubeAuth.IsConnected)
            throw new InvalidOperationException("YouTube / YouTube Music kitaplığınızı eşitlemek için Ayarlar → Bağlantılar bölümünden YouTube hesabınızı bağlayın.");
        var target = string.IsNullOrWhiteSpace(channelUrl) ? "https://www.youtube.com/feed/playlists" : channelUrl.Trim();
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com"))
            throw new InvalidOperationException("Geçerli bir YouTube kanal bağlantısı girin.");
        if (!target.Contains("/feed/") && !uri.AbsolutePath.EndsWith("/playlists")) target = target.TrimEnd('/') + "/playlists";
        var arguments = new List<string> { "--flat-playlist", "--dump-single-json", "--skip-download", "--ignore-errors" };
        if (_youtubeAuth.IsConnected && _youtubeAuth.CookiesFilePath != null) { arguments.Add("--cookies"); arguments.Add(_youtubeAuth.CookiesFilePath); }
        arguments.Add(target);
        using var doc = await RunYtDlpJsonAsync(arguments, TimeSpan.FromMinutes(5));
        if (doc == null)
            throw new InvalidOperationException("YouTube listeleri okunamadı. Hesap bağlantısını veya kanalın listeler sayfasını kontrol edin.");
        return ParseLibraryPlaylists(doc.RootElement);
    }

    internal static List<(string Url, string Title)> ParseLibraryPlaylists(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return [];
        var results = new List<(string Url, string Title)>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var id = entry.TryGetProperty("id", out var value) ? value.GetString() ?? "" : "";
            var title = entry.TryGetProperty("title", out var name) ? name.GetString() ?? "YouTube Listesi" : "YouTube Listesi";
            if (id.Length > 11 || id is "LL" or "WL") results.Add(("https://www.youtube.com/playlist?list=" + Uri.EscapeDataString(id), title));
        }
        return results.DistinctBy(p => p.Url).ToList();
    }

    public async Task<(List<Song> Songs, string? PlaylistName)> ImportPlaylistAsync(string playlistUrl, int maxTracks = int.MaxValue)
    {
        playlistUrl = NormalizeYouTubeUrl(playlistUrl);
        if (_youtubeOAuth != null)
        {
            await _youtubeOAuth.EnsureLoadedAsync();
            if (_youtubeOAuth.IsConnected && Uri.TryCreate(playlistUrl, UriKind.Absolute, out var playlistUri)
                && (playlistUri.Host == "www.youtube.com" || playlistUri.Host == "youtube.com" || playlistUri.Host == "music.youtube.com")
                && Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(playlistUri.Query).TryGetValue("list", out var list))
                return ((await _youtubeOAuth.PlaylistTracksAsync(list.ToString())).Take(maxTracks).ToList(), null);
        }

        _logger.LogInformation("Importing playlist: {Url} (max {Max})", playlistUrl, maxTracks);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));

        var psi = new ProcessStartInfo
        {
            FileName               = _ytdl.YoutubeDLPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        // --print outputs one line per entry:
        //   playlist_title<TAB>id<TAB>title<TAB>channel<TAB>duration<TAB>availability
        // playlist_title is the same for every line; we capture it from the first.
        psi.ArgumentList.Add("--flat-playlist");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(playlist_title)s\t%(id)s\t%(title)s\t%(channel)s\t%(duration)s\t%(availability)s");
        psi.ArgumentList.Add("--no-warnings");
        if (maxTracks != int.MaxValue) { psi.ArgumentList.Add("--playlist-items"); psi.ArgumentList.Add($"1-{maxTracks}"); }
        if (_youtubeAuth.IsConnected && _youtubeAuth.CookiesFilePath != null) { psi.ArgumentList.Add("--cookies"); psi.ArgumentList.Add(_youtubeAuth.CookiesFilePath); }
        psi.ArgumentList.Add(playlistUrl);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        var songs        = new List<Song>();
        int skipped      = 0;
        string? playlistName = null;
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cts.Token)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts        = line.Split('\t');
                var plTitle      = parts.Length > 0 ? parts[0].Trim() : null;
                var id           = parts.Length > 1 ? parts[1].Trim() : null;
                var title        = parts.Length > 2 ? parts[2].Trim() : "";
                var chan         = parts.Length > 3 ? parts[3].Trim() : "";
                var durStr       = parts.Length > 4 ? parts[4].Trim() : "0";
                var availability = parts.Length > 5 ? parts[5].Trim() : "NA";

                if (playlistName == null && !string.IsNullOrEmpty(plTitle) && plTitle != "NA")
                    playlistName = plTitle;

                if (string.IsNullOrEmpty(id) || id == "NA") continue;

                if (IsUnavailablePlaylistEntry(title, availability))
                {
                    skipped++;
                    _logger.LogDebug("Skipping unavailable playlist entry: id={Id} title={Title} availability={Av}",
                        id, title, availability);
                    continue;
                }

                double.TryParse(durStr is "NA" or "" ? "0" : durStr,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double dur);

                var (songTitle, artist) = SplitVideoTitle(title, chan == "NA" ? "" : chan);
                songs.Add(new Song
                {
                    SpotifyUri = $"youtube:{id}",
                    Title      = songTitle,
                    Artist     = artist,
                    // YouTube always serves thumbnails at this URL — no NA risk
                    CoverUrl   = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg",
                    DurationMs = (int)(dur * 1000),
                });

                if (songs.Count >= maxTracks) break;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Playlist import timed out after 30 min (got {N} tracks so far)", songs.Count);
        }

        try
        {
            await process.WaitForExitAsync(cts.Token);
            var error = await stderrTask;
            if (process.ExitCode != 0 && songs.Count == 0)
                throw new InvalidOperationException("YouTube listesi okunamadı: " + error.Split('\n')[0]);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await stderrTask; } catch (OperationCanceledException) { }
            throw new TimeoutException("YouTube liste aktarımı zaman aşımına uğradı. Lütfen tekrar deneyin.");
        }
        if (skipped > 0)
            _logger.LogInformation("Playlist import complete: {N} tracks added, {Skipped} unavailable skipped from {Url}",
                songs.Count, skipped, playlistUrl);
        else
            _logger.LogInformation("Playlist import complete: {N} tracks from {Url}", songs.Count, playlistUrl);
        return (songs, playlistName);
    }

    /// <summary>Search YouTube and return matching songs.</summary>
    public async Task<List<Song>> SearchAsync(string query, int limit = 5, CancellationToken cancellationToken = default)
    {
        if (!_queueSettings.YouTubeEnabled || limit <= 0) return [];
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        List<Song> candidates;
        try { candidates = await SearchCandidatesAsync(query, Math.Min(30, Math.Max(limit, limit * 3)), budget.Token); }
        catch (OperationCanceledException) { return []; }
        using var concurrency = new SemaphoreSlim(4, 4);
        var checkedSongs = await Task.WhenAll(candidates.Select(async song =>
        {
            var entered = false;
            try
            {
                await concurrency.WaitAsync(budget.Token); entered = true;
                return await ValidateMusicVideoAsync(song.SpotifyUri, budget.Token);
            }
            catch (OperationCanceledException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (HttpRequestException) { return null; }
            finally { if (entered) concurrency.Release(); }
        }));
        return checkedSongs.Where(song => song != null).Take(limit).Select(song => song!).ToList();
    }

    private async Task<List<Song>> SearchCandidatesAsync(string query, int limit, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query));
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 Musick/1.2");
            using var response = await _searchClient.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                var songs = YouTubeSearchPage.Parse(await response.Content.ReadAsStringAsync(timeout.Token), limit);
                if (songs.Count > 0) return songs;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return []; }
        catch (Exception ex) { _logger.LogDebug(ex, "YouTube page search unavailable; trying yt-dlp"); }
        using var doc = await RunYtDlpJsonAsync(
            ["--flat-playlist", "-J", "--no-warnings", $"ytsearch{limit}:{query}"],
            TimeSpan.FromSeconds(5), cancellationToken);

        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return [];

        return ParseEntries(entries, limit);
    }

    /// <summary>Search YouTube specifically for playlists and return them as Song entries with IsPlaylist=true.</summary>
    public async Task<List<Song>> SearchPlaylistsAsync(string query, int limit = 5)
    {
        // Use YouTube's playlist filter (sp=EgIQAw%3D%3D) via a direct URL search
        var searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}&sp=EgIQAw%3D%3D";
        using var doc = await RunYtDlpJsonAsync(
            ["--flat-playlist", "-J", "--no-warnings", searchUrl],
            TimeSpan.FromSeconds(12));

        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object) return [];

        if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return [];

        var results = new List<Song>();
        foreach (var entry in entries.EnumerateArray().Take(limit))
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var id = entry.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;

            var title   = entry.TryGetProperty("title",    out var tEl)  ? tEl.GetString()  ?? "" : "";
            var channel = entry.TryGetProperty("channel",  out var chEl) ? chEl.GetString() ?? "" :
                          entry.TryGetProperty("uploader", out var upEl) ? upEl.GetString() ?? "" : "";

            var thumb = "";
            if (entry.TryGetProperty("thumbnails", out var thumbsArr) && thumbsArr.ValueKind == JsonValueKind.Array)
            {
                var thumbsList = thumbsArr.EnumerateArray().ToList();
                if (thumbsList.Count > 0)
                    thumb = thumbsList.Last().TryGetProperty("url", out var tUrl) ? tUrl.GetString() ?? "" : "";
            }
            else if (entry.TryGetProperty("thumbnail", out var thumbEl))
            {
                thumb = thumbEl.GetString() ?? "";
            }

            var count = 0;
            if (entry.TryGetProperty("playlist_count", out var cEl) && cEl.ValueKind == JsonValueKind.Number)
                count = cEl.GetInt32();

            var url = (entry.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null)
                      ?? $"https://www.youtube.com/playlist?list={id}";

            results.Add(new Song
            {
                SpotifyUri         = $"ytplaylist:{id}",
                Title              = title,
                Artist             = channel,
                CoverUrl           = thumb,
                DurationMs         = 0,
                IsPlaylist         = true,
                PlaylistUrl        = url,
                PlaylistVideoCount = count,
            });
        }
        return results;
    }

    /// <summary>
    /// Finds the single best YouTube match for a free-text query.
    /// Appends "official audio" for non-remix queries.
    /// Filters out remixes/covers/karaoke unless the query explicitly asks for them.
    /// Returns null if nothing is found.
    /// </summary>
    public async Task<Song?> SearchBestMatchAsync(string query)
    {
        bool isSpecialQuery = SongSearchMatcher.RequestsSpecialVersion(query);
        var searchQuery = isSpecialQuery ? query : $"{query} official audio";
        var results = await SearchAsync(searchQuery, 10);
        return SongSearchMatcher.Select(query, results);
    }

    // ── Direct yt-dlp process helpers ────────────────────────────────────────

    /// <summary>
    /// Runs yt-dlp with the given arguments and returns the parsed JSON output.
    /// Returns null on timeout, error, or parse failure.
    /// </summary>
    private async Task<JsonDocument?> RunYtDlpJsonAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var psi = new ProcessStartInfo
        {
            FileName               = _ytdl.YoutubeDLPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        await _youtubeAuth.EnsureLoadedAsync();
        var arguments = args.ToList();
        if (!arguments.Contains("--cookies") && _youtubeAuth.IsConnected && _youtubeAuth.CookiesFilePath != null)
        {
            arguments.InsertRange(0, new[] { "--cookies", _youtubeAuth.CookiesFilePath });
        }
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read stdout and stderr concurrently to avoid deadlock on full pipe buffers
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
            var output = await stdoutTask;
            if (string.IsNullOrWhiteSpace(output))
            {
                var err = await stderrTask;
                _logger.LogWarning("yt-dlp produced no output. stderr: {Err}", err.Length > 500 ? err[..500] : err);
                return null;
            }
            return JsonDocument.Parse(output);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("yt-dlp process timed out after {Timeout}", timeout);
            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp JSON parse/run failed");
            return null;
        }
    }

    private List<Song> ParseEntries(JsonElement entries, int maxCount)
    {
        var songs = new List<Song>();
        if (entries.ValueKind != JsonValueKind.Array) return songs;
        foreach (var entry in entries.EnumerateArray())
        {
            if (songs.Count >= maxCount) break;
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var id = entry.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;

            // Detect playlist entries mixed into ytsearch results
            var entryType  = entry.TryGetProperty("_type",         out var typeEl) ? typeEl.GetString() : null;
            var hasPlCount = entry.TryGetProperty("playlist_count", out var pcEl)  && pcEl.ValueKind == JsonValueKind.Number;
            var isPlaylist = entryType == "playlist" || hasPlCount;

            var title   = entry.TryGetProperty("title",    out var tEl)  ? tEl.GetString()  ?? "" : "";
            var channel = entry.TryGetProperty("channel",  out var chEl) ? chEl.GetString() ?? "" :
                          entry.TryGetProperty("uploader", out var upEl) ? upEl.GetString() ?? "" : "";
            var thumb   = entry.TryGetProperty("thumbnail", out var thEl) ? thEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(thumb) && id.Length == 11) thumb = "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg";

            if (!isPlaylist)
            {
                var availability = entry.TryGetProperty("availability", out var avEl) ? avEl.GetString() ?? "NA" : "NA";
                if (IsUnavailablePlaylistEntry(title, availability)) continue;
            }

            if (isPlaylist)
            {
                var count  = hasPlCount ? pcEl.GetInt32() : 0;
                var url    = (entry.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null)
                             ?? $"https://www.youtube.com/playlist?list={id}";
                songs.Add(new Song
                {
                    SpotifyUri         = $"ytplaylist:{id}",
                    Title              = title,
                    Artist             = channel,
                    CoverUrl           = thumb,
                    DurationMs         = 0,
                    IsPlaylist         = true,
                    PlaylistUrl        = url,
                    PlaylistVideoCount = count,
                });
            }
            else
            {
                var dur = entry.TryGetProperty("duration", out var dEl) && dEl.ValueKind == JsonValueKind.Number
                          ? dEl.GetDouble() : 0;
                var (songTitle, artist) = SplitVideoTitle(title, channel);
                songs.Add(new Song
                {
                    SpotifyUri = $"youtube:{id}",
                    Title      = songTitle,
                    Artist     = artist,
                    CoverUrl   = thumb,
                    DurationMs = (int)(dur * 1000),
                });
            }
        }
        return songs;
    }

    /// <summary>
    /// Returns true for playlist entries that are known to be unavailable at import time,
    /// based on the availability field or YouTube's placeholder titles for private/deleted videos.
    /// </summary>
    internal static bool IsUnavailablePlaylistEntry(string title, string availability)
    {
        // yt-dlp availability values that indicate the video cannot be played
        if (availability is "private" or "premium_only" or "subscriber_only" or "needs_auth")
            return true;

        // YouTube substitutes these placeholder titles for deleted/private videos in playlists
        var t = title.Trim('[', ']').ToLowerInvariant();
        return t is "private video" or "deleted video";
    }

    internal static (string title, string artist) SplitVideoTitle(string videoTitle, string channelName)
    {
        var idx = videoTitle.IndexOf(" - ", StringComparison.Ordinal);
        if (idx > 0)
        {
            var artist = videoTitle[..idx].Trim();
            var title  = videoTitle[(idx + 3)..].Trim();
            title = System.Text.RegularExpressions.Regex.Replace(
                title, @"\s*[\(\[][^\)\]]*[\)\]]$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            return (title, artist);
        }
        return (videoTitle.Trim(), channelName.Trim());
    }

    // ── Core download logic ──────────────────────────────────────────────────

    private static bool IsRateLimitError(string[] errorOutput)
    {
        var combined = string.Join(" ", errorOutput);
        return (combined.Contains("429") || combined.Contains("Too Many Requests"))
            && !IsUnrecoverableOutputError(combined);
    }

    private static bool IsUnrecoverableOutputError(string combined) =>
        combined.Contains("Video unavailable")
        || combined.Contains("Private video")
        || combined.Contains("This video is not available")
        || combined.Contains("has been removed")
        || combined.Contains("account associated with this video has been terminated")
        || combined.Contains("Sign in to confirm");  // bot detection — requires cookies, retrying never helps

    private async Task<string> DownloadCoreAsync(Song song)
    {
        var safeId     = song.SpotifyUri.Replace(":", "_");
        bool normalize = _queueSettings.LoudnessNormalizationEnabled;
        var ext        = _settings.UseNativeAudioFormat ? "m4a" : "mp3";
        var outputPath = Path.GetFullPath(Path.Combine(_settings.LibraryPath, $"{safeId}.{ext}"));

        // Already on disk — validate it has reasonable content (> 100 KB),
        // otherwise treat it as a corrupt stub and delete it before re-downloading.
        if (File.Exists(outputPath))
        {
            if (new FileInfo(outputPath).Length > 100_000)
            {
                await EnsureSavedToLibraryAsync(song, outputPath);
                return outputPath;
            }
            _logger.LogWarning("Cached file for \"{Title}\" is too small (corrupt stub) — deleting and re-downloading", song.Title);
            try { File.Delete(outputPath); } catch { }
        }

        _logger.LogInformation("Downloading \"{Title}\" by {Artist}...", song.Title, song.Artist);
        DownloadStarted?.Invoke(song.SpotifyUri, song.Title, song.Artist);

        try
        {
            string? videoId;
            if (song.SpotifyUri.StartsWith("youtube:"))
                videoId = song.SpotifyUri["youtube:".Length..];
            else
                videoId = await FindBestYouTubeMatchAsync(song);

            if (videoId == null)
                throw new InvalidOperationException($"No YouTube match found for \"{song.Title}\" by {song.Artist}");

            var url = $"https://www.youtube.com/watch?v={videoId}";

            int lastPct = -1;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.State != DownloadState.Downloading) return;
                var pct = Math.Min(99, (int)(p.Progress * 100));
                if (pct == lastPct) return;
                lastPct = pct;
                _logger.LogInformation("Download progress [{TrackId}]: {Pct}%", song.SpotifyUri, pct);
                ProgressChanged?.Invoke(song.SpotifyUri, pct);
            });

            var overrideOptions = new OptionSet
            {
                Output     = outputPath,
                NoPlaylist = true,
            };
            overrideOptions.AddCustomOption<bool>("--ignore-config", true);
            overrideOptions.AddCustomOption<string>("--extractor-args", "youtube:player_client=default");
            var deno = Path.Combine(_settings.ToolsDirectory, "deno.exe");
            if (File.Exists(deno)) overrideOptions.AddCustomOption<string>("--js-runtimes", "deno:" + deno);

            // YouTube cookies for bot-detection bypass (opt-in via Settings).
            // When enabled and the cookies.txt exists, yt-dlp authenticates as the user.
            if (_youtubeAuth.IsEnabled
                && !string.IsNullOrWhiteSpace(_youtubeAuth.CookiesFilePath)
                && File.Exists(_youtubeAuth.CookiesFilePath))
            {
                overrideOptions.Cookies = _youtubeAuth.CookiesFilePath;
            }

            var audioFormat = _settings.UseNativeAudioFormat
                ? AudioConversionFormat.M4a   // no ffmpeg needed; Windows MF plays M4A natively
                : AudioConversionFormat.Mp3;  // requires ffmpeg

            // Retry up to 3 times on HTTP 429 with exponential backoff.
            // 429 is transient — YouTube rate-limits bursts after large playlist imports.
            RunResult<string>? result = null;
            int[] retryDelaysSec = [45, 120];
            for (int attempt = 0; attempt <= retryDelaysSec.Length; attempt++)
            {
                result = await _ytdl.RunAudioDownload(
                    url,
                    audioFormat,
                    progress: progress,
                    overrideOptions: overrideOptions);

                if (result.Success) break;
                if (attempt == retryDelaysSec.Length) break;
                if (!IsRateLimitError(result.ErrorOutput)) break;

                var delaySec = retryDelaysSec[attempt];
                _logger.LogWarning(
                    "YouTube rate-limited (429) for \"{Title}\" — waiting {Delay}s before retry {Attempt}/{Max}",
                    song.Title, delaySec, attempt + 1, retryDelaysSec.Length);
                await Task.Delay(TimeSpan.FromSeconds(delaySec));
            }

            if (!result!.Success)
            {
                // Content ID / availability blocks: retry with android_vr client, which uses a
                // different API path not covered by most "blocked on this website/application" rules.
                if (IsUnrecoverableOutputError(string.Join(" ", result.ErrorOutput)))
                {
                    _logger.LogWarning(
                        "Retrying \"{Title}\" with supported default clients after a transient extractor failure", song.Title);

                    // android_vr does NOT support cookies — yt-dlp logs
                    // "Skipping client 'android_vr' since it does not support cookies" and then
                    // fails to find any audio format. Always invoke this fallback cookieless.
                    var altOptions = new OptionSet { Output = outputPath, NoPlaylist = true };
                    altOptions.AddCustomOption<string>("--extractor-args", "youtube:player_client=default");

                    result = await _ytdl.RunAudioDownload(url, audioFormat, progress: progress, overrideOptions: altOptions);

                    if (result.Success)
                        _logger.LogInformation("android_vr bypass succeeded for \"{Title}\"", song.Title);
                }

                if (!result!.Success)
                    throw new InvalidOperationException(
                        $"yt-dlp failed for \"{song.Title}\": {string.Join("; ", result.ErrorOutput)}");
            }

            var filePath = File.Exists(outputPath) ? outputPath : result.Data ?? outputPath;

            if (!File.Exists(filePath))
                throw new InvalidOperationException($"yt-dlp finished but output file not found: {filePath}");

            // Loudness normalization: two-pass EBU R128 with linear=true.
            // First pass measures, second pass applies a single linear gain (NO dynamic compression),
            // preserving the original dynamics — equivalent to ReplayGain/Spotify-style normalization.
            if (normalize)
            {
                try
                {
                    var ok = await NormalizeAudioFileAsync(filePath, song.Title);
                    if (!ok)
                        _logger.LogWarning("Loudness normalization skipped/failed for \"{Title}\" — keeping original file", song.Title);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Loudness normalization threw for \"{Title}\" — keeping original file", song.Title);
                }
            }

            // Correct DurationMs from the actual downloaded file (metadata duration can differ)
            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                var actualMs = (int)tagFile.Properties.Duration.TotalMilliseconds;
                if (actualMs > 0 && Math.Abs(actualMs - song.DurationMs) > 1000)
                {
                    _logger.LogInformation(
                        "Correcting duration for \"{Title}\": {Old}ms → {New}ms",
                        song.Title, song.DurationMs, actualMs);
                    song.DurationMs = actualMs;
                }
            }
            catch { /* TagLib optional; keep original estimate */ }

            await EnsureSavedToLibraryAsync(song, filePath);
            if (_queueSettings.SaveDownloads)
                await WriteId3TagsAsync(song, filePath);
            _logger.LogInformation("Downloaded \"{Title}\" → {Path}", song.Title, filePath);
            DownloadCompleted?.Invoke(song.SpotifyUri);
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed for \"{Title}\" ({SpotifyUri})", song.Title, song.SpotifyUri);
            DownloadFailed?.Invoke(song.SpotifyUri, ex.Message);
            throw;
        }
    }

    private async Task EnsureSavedToLibraryAsync(Song song, string filePath)
    {
        var existing = await _library.FindByTrackIdAsync(song.SpotifyUri);
        if (existing != null)
        {
            // Persist corrected duration if it changed after reading the actual file
            if (song.DurationMs > 0 && existing.DurationMs != song.DurationMs)
                await _library.UpdateDurationAsync(song.SpotifyUri, song.DurationMs);
            // Persist new path if the file was re-downloaded to a different location
            // (e.g. after a Velopack update wiped the old current\ directory)
            if (!string.Equals(existing.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                await _library.UpdateFilePathAsync(song.SpotifyUri, filePath);
            return;
        }

        await _library.SaveAsync(new CachedTrack
        {
            TrackId      = song.SpotifyUri,
            Title        = song.Title,
            Artist       = song.Artist,
            CoverUrl     = song.CoverUrl,
            DurationMs   = song.DurationMs,
            FilePath     = filePath,
            DownloadedAt = DateTime.UtcNow
        });
    }

    // ── YouTube search ───────────────────────────────────────────────────────

    private static readonly string[] RemixCoverKeywords =
        ["remix", "cover", "karaoke", "tribute", "mashup", "nightcore", "bootleg"];

    private async Task<string?> FindBestYouTubeMatchAsync(Song song)
    {
        var expectedSec  = song.DurationMs / 1000.0;
        var toleranceSec = Math.Max(expectedSec * 0.20, 10);

        var metaLower   = $"{song.Title} {song.Artist}".ToLowerInvariant();
        var isRemixReq  = RemixCoverKeywords.Any(k => metaLower.Contains(k));
        var searchQuery = isRemixReq
            ? $"{song.Title} {song.Artist}"
            : $"{song.Title} {song.Artist} official audio";

        RunResult<VideoData> result;
        try
        {
            result = await _ytdl.RunVideoDataFetch(
                $"ytsearch10:{searchQuery}",
                flat: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("yt-dlp search failed: {Error}", ex.Message);
            return null;
        }

        if (!result.Success || result.Data?.Entries == null)
        {
            _logger.LogWarning("No YouTube results for \"{Title}\"", song.Title);
            return null;
        }

        var candidates = ScoreCandidates(result.Data.Entries, song, expectedSec, toleranceSec, skipFilter: false);

        if (candidates.Count == 0)
        {
            _logger.LogWarning("No filtered match for \"{Title}\", retrying without filter.", song.Title);
            candidates = ScoreCandidates(result.Data.Entries, song, expectedSec, toleranceSec, skipFilter: true);
        }

        if (candidates.Count == 0)
        {
            _logger.LogWarning("No YouTube match found for \"{Title}\"", song.Title);
            return null;
        }

        return candidates.OrderByDescending(c => c.Score).ThenBy(c => c.DurationDiff).First().Id;
    }

    private async Task WriteId3TagsAsync(Song song, string filePath)
    {
        try
        {
            var file = TagLib.File.Create(filePath);
            file.Tag.Title      = song.Title;
            file.Tag.Performers = new[] { song.Artist };
            file.Tag.Comment    = song.SpotifyUri;

            if (!string.IsNullOrEmpty(song.CoverUrl))
            {
                try
                {
                    using var http   = new HttpClient();
                    var imageBytes   = await http.GetByteArrayAsync(song.CoverUrl);
                    var pic          = new TagLib.Picture(new TagLib.ByteVector(imageBytes))
                    {
                        Type        = TagLib.PictureType.FrontCover,
                        MimeType    = "image/jpeg",
                    };
                    file.Tag.Pictures = new TagLib.IPicture[] { pic };
                }
                catch { /* skip cover if download fails */ }
            }

            file.Save();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not write ID3 tags to {Path}: {Error}", filePath, ex.Message);
        }
    }

    private record YtCandidate(string Id, double Score, double DurationDiff);

    private List<YtCandidate> ScoreCandidates(
        VideoData[] entries, Song song,
        double expectedSec, double toleranceSec,
        bool skipFilter)
    {
        var titleLower  = song.Title.ToLowerInvariant();
        var artistLower = song.Artist.ToLowerInvariant();
        var results     = new List<YtCandidate>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.ID)) continue;
            if (!entry.Duration.HasValue || entry.Duration.Value <= 0) continue;

            var diff = Math.Abs(entry.Duration.Value - expectedSec);
            if (diff > toleranceSec) continue;

            var titleL   = (entry.Title   ?? "").ToLowerInvariant();
            var channelL = (entry.Channel ?? entry.Uploader ?? "").ToLowerInvariant();

            if (!skipFilter && RemixCoverKeywords.Any(k => titleL.Contains(k)))
                continue;

            bool titleMatch  = titleL.Contains(titleLower)  || (titleLower.Length > 4 && titleLower.Contains(titleL));
            bool artistMatch = titleL.Contains(artistLower) || channelL.Contains(artistLower);

            // Require at least title or artist match unless we're in last-resort mode
            if (!skipFilter && !titleMatch && !artistMatch)
                continue;

            double score = 0;
            if (titleMatch && artistMatch) score += 4;
            else if (titleMatch)           score += 2;
            else if (artistMatch)          score += 1;

            if (channelL.Contains("vevo"))                                    score += 3;
            if (channelL.Contains("official") || titleL.Contains("official")) score += 2;
            if (titleL.Contains("audio"))                                     score += 1;

            results.Add(new YtCandidate(entry.ID, score, diff));
        }
        return results;
    }

    // ── Loudness normalization ───────────────────────────────────────────────

    /// <summary>
    /// Two-pass EBU R128 loudness normalization with linear=true.
    /// Pass 1 measures, pass 2 applies a single linear gain (no dynamic compression)
    /// so the original dynamics are preserved — equivalent to ReplayGain / Spotify's
    /// gain-only normalization mode. Target -14 LUFS with TP -1.5 dBTP ceiling.
    /// Returns true on success, false on any failure (caller keeps the original file).
    /// </summary>
    private async Task<bool> NormalizeAudioFileAsync(string filePath, string songTitle)
    {
        var ffmpeg = _ytdl.FFmpegPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogDebug("Skipping loudness normalization: ffmpeg path not configured");
            return false;
        }
        if (!File.Exists(ffmpeg) && !IsOnPath(ffmpeg))
        {
            _logger.LogDebug("Skipping loudness normalization: ffmpeg not found at {Path}", ffmpeg);
            return false;
        }

        _logger.LogInformation("Normalizing loudness for \"{Title}\"…", songTitle);

        // ── Pass 1: measure ────────────────────────────────────────────────
        const string LOUDNORM_TARGET = "I=-14:LRA=11:TP=-1.5";
        var pass1Stderr = await RunFfmpegCaptureStderrAsync(ffmpeg,
            ["-hide_banner", "-nostats", "-i", filePath,
             "-af", $"loudnorm={LOUDNORM_TARGET}:print_format=json",
             "-f", "null", "-"],
            TimeSpan.FromMinutes(5));

        if (pass1Stderr == null) return false;

        // ffmpeg prints the JSON block at the END of stderr — grab the last { ... } pair
        var jsonStart = pass1Stderr.LastIndexOf('{');
        var jsonEnd   = pass1Stderr.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            _logger.LogWarning("Could not locate loudnorm JSON in ffmpeg output for \"{Title}\"", songTitle);
            return false;
        }

        string input_i, input_lra, input_tp, input_thresh, target_offset;
        try
        {
            var json = pass1Stderr.Substring(jsonStart, jsonEnd - jsonStart + 1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            input_i       = root.GetProperty("input_i").GetString() ?? "0";
            input_lra     = root.GetProperty("input_lra").GetString() ?? "0";
            input_tp      = root.GetProperty("input_tp").GetString() ?? "0";
            input_thresh  = root.GetProperty("input_thresh").GetString() ?? "0";
            target_offset = root.GetProperty("target_offset").GetString() ?? "0";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse loudnorm JSON for \"{Title}\"", songTitle);
            return false;
        }

        // If input is effectively silent, ebur128 returns "-inf" / "nan" — skip.
        if (input_i.Contains("inf", StringComparison.OrdinalIgnoreCase)
         || input_i.Contains("nan", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Skipping normalization for \"{Title}\": measured loudness is {I}", songTitle, input_i);
            return false;
        }

        // ── Pass 2: apply linear gain ──────────────────────────────────────
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        // libmp3lame doesn't support 192kHz (loudnorm's internal rate), so resample back to 44.1kHz.
        var filter = $"loudnorm={LOUDNORM_TARGET}" +
                     $":measured_I={input_i}:measured_LRA={input_lra}:measured_TP={input_tp}" +
                     $":measured_thresh={input_thresh}:offset={target_offset}" +
                     $":linear=true:print_format=summary,aresample=44100";

        string[] codecArgs = ext switch
        {
            ".mp3" => ["-c:a", "libmp3lame", "-q:a", "2"],
            ".m4a" => ["-c:a", "aac", "-b:a", "192k"],
            _      => ["-c:a", "libmp3lame", "-q:a", "2"],
        };

        var tempPath = filePath + ".norm.tmp" + ext;
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

        var pass2Args = new List<string>
        {
            "-y", "-hide_banner", "-nostats",
            "-i", filePath,
            "-af", filter,
        };
        pass2Args.AddRange(codecArgs);
        pass2Args.Add(tempPath);

        var pass2Ok = await RunFfmpegSilentAsync(ffmpeg, pass2Args, TimeSpan.FromMinutes(10));
        if (!pass2Ok || !File.Exists(tempPath) || new FileInfo(tempPath).Length < 10_000)
        {
            _logger.LogWarning("Loudness normalization pass 2 failed for \"{Title}\"", songTitle);
            try { File.Delete(tempPath); } catch { }
            return false;
        }

        // Swap files atomically (delete + move; File.Replace requires both on same volume which they are).
        try
        {
            File.Delete(filePath);
            File.Move(tempPath, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to replace original file after normalization for \"{Title}\"", songTitle);
            try { File.Delete(tempPath); } catch { }
            return false;
        }

        _logger.LogInformation("Normalized \"{Title}\": {From} LUFS → -14 LUFS (linear gain)", songTitle, input_i);
        return true;
    }

    private async Task<string?> RunFfmpegCaptureStderrAsync(string ffmpegPath, IEnumerable<string> args, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var psi = new ProcessStartInfo
        {
            FileName               = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            await stdoutTask;
            return await stderrTask;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg process failed");
            return null;
        }
    }

    private async Task<bool> RunFfmpegSilentAsync(string ffmpegPath, IEnumerable<string> args, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var psi = new ProcessStartInfo
        {
            FileName               = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
            // Drain both streams so the process doesn't block on a full pipe buffer.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
            {
                var tail = stderr.Length > 500 ? stderr[^500..] : stderr;
                _logger.LogWarning("ffmpeg exited with code {Code}. Last stderr: {Err}", proc.ExitCode, tail);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            _logger.LogWarning("ffmpeg timed out after {Timeout}", timeout);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg process failed");
            return false;
        }
    }
}
