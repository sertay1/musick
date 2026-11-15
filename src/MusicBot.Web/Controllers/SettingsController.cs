using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MusicBot.Services;
using MusicBot.Services.Platforms;
using MusicBot.Services.Spotify;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/settings")]
[Tags("Settings")]
public class SettingsController : ControllerBase
{
    private readonly QueueSettingsService _settings;
    private readonly UserContextManager   _userContext;
    private readonly IConfiguration       _config;
    private readonly KickAuthService      _kickAuth;

    public SettingsController(
        QueueSettingsService settings,
        UserContextManager userContext,
        IConfiguration config,
        KickAuthService kickAuth)
    {
        _settings    = settings;
        _userContext = userContext;
        _config      = config;
        _kickAuth    = kickAuth;
    }

    /// <summary>Get current developer credentials status</summary>
    [HttpGet("credentials")]
    public IActionResult GetCredentials()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var spotify = (SpotifyService)services.Spotify;

        return Ok(new
        {
            kickClientId = _kickAuth.Settings.ClientId ?? "",
            kickClientSecret = "",
            kickClientSecretConfigured = !string.IsNullOrEmpty(_kickAuth.Settings.ClientSecret),
            kickRedirectUri = _kickAuth.Settings.RedirectUri ?? "http://localhost:3050/api/auth/kick/callback",
            spotifyClientId = spotify.Settings.ClientId ?? "",
            spotifyClientSecret = "",
            spotifyClientSecretConfigured = !string.IsNullOrEmpty(spotify.Settings.ClientSecret),
            spotifyRedirectUri = spotify.Settings.RedirectUri ?? "http://127.0.0.1:3050/api/auth/spotify/callback",
        });
    }

    // Explicit user action only. Ordinary settings reads never return saved secrets.
    [HttpPost("credentials/{platform}/reveal")]
    public IActionResult RevealCredential(string platform)
    {
        Response.Headers.CacheControl = "no-store";
        if (platform == "kick") return Ok(new { secret = _kickAuth.Settings.ClientSecret ?? "" });
        if (platform == "spotify")
        {
            var spotify = (SpotifyService)_userContext.GetOrCreate(LocalUser.Id).Spotify;
            return Ok(new { secret = spotify.Settings.ClientSecret ?? "" });
        }
        return BadRequest(new { error = "Geçersiz platform." });
    }

    /// <summary>Update Kick and Spotify developer credentials and persist to appsettings.user.json</summary>
    [HttpPut("credentials")]
    [ProducesResponseType(204)]
    public IActionResult UpdateCredentials([FromBody] UpdateCredentialsRequest req)
    {
        var userConfigPath = ResolveUserConfigPath();

        // 1. Kick credentials
        if (req.KickClientId != null || req.KickClientSecret != null)
        {
            var clientId = req.KickClientId?.Trim() ?? _kickAuth.Settings.ClientId;
            var clientSecret = req.KickClientSecret?.Trim() ?? _kickAuth.Settings.ClientSecret;
            _kickAuth.UpdateCredentials(clientId, clientSecret);

            PersistUserConfigSection(userConfigPath, "Kick", new Dictionary<string, object?>
            {
                ["ClientId"] = clientId,
                ["ClientSecret"] = MusicBot.Services.Security.TokenProtection.Protect(clientSecret),
                ["RedirectUri"] = "http://localhost:3050/api/auth/kick/callback"
            });
        }

        // 2. Spotify credentials
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var spotify = (SpotifyService)services.Spotify;

        if (req.SpotifyClientId != null || req.SpotifyClientSecret != null)
        {
            var clientId = req.SpotifyClientId?.Trim() ?? spotify.Settings.ClientId;
            var clientSecret = req.SpotifyClientSecret?.Trim() ?? spotify.Settings.ClientSecret;
            spotify.UpdateCredentials(clientId, clientSecret);

            PersistUserConfigSection(userConfigPath, "Spotify", new Dictionary<string, object?>
            {
                ["ClientId"] = clientId,
                ["ClientSecret"] = MusicBot.Services.Security.TokenProtection.Protect(clientSecret),
                ["RedirectUri"] = "http://127.0.0.1:3050/api/auth/spotify/callback"
            });
        }

        return NoContent();
    }

    /// <summary>Get current queue and voting settings</summary>
    [HttpGet]
    public IActionResult GetSettings() => Ok(new
    {
        maxQueueSize                    = _settings.MaxQueueSize,
        maxQueueSizeEnabled             = _settings.MaxQueueSizeEnabled,
        maxSongsPerUser                 = _settings.MaxSongsPerUser,
        maxSongsPerUserEnabled          = _settings.MaxSongsPerUserEnabled,
        votingEnabled                   = _settings.VotingEnabled,
        presenceCheckEnabled            = _settings.PresenceCheckEnabled,
        presenceCheckWarningSeconds     = _settings.PresenceCheckWarningSeconds,
        presenceCheckConfirmSeconds     = _settings.PresenceCheckConfirmSeconds,
        saveDownloads                   = _settings.SaveDownloads,
        autoQueueEnabled                = _settings.AutoQueueEnabled,
        loudnessNormalizationEnabled    = _settings.LoudnessNormalizationEnabled,
        directSpotifyMode               = _settings.DirectSpotifyMode,
        chatPlaylistRequestsEnabled = _settings.ChatPlaylistRequestsEnabled,
        spotifyEnabled = _settings.SpotifyEnabled,
        youTubeEnabled = _settings.YouTubeEnabled,
        youTubeVolumePercent = _settings.YouTubeVolumePercent,
        notificationsEnabled = _config.GetValue("Desktop:NotificationsEnabled", true),
        openLogOnStart                  = _config.GetValue<bool>("Desktop:OpenLogOnStart", false),
    });

    /// <summary>Update queue and voting settings</summary>
    [HttpPut]
    [ProducesResponseType(204)]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsRequest req)
    {
        if (req.YouTubeVolumePercent is < 0 or > 100) return BadRequest(new { error = "YouTube ses dengesi 0 ile 100 arasında olmalı." });
        PersistPreferences(req);
        await ApplyRuntimePreferencesAsync(req);
        return NoContent();
    }

    internal void PersistPreferences(UpdateSettingsRequest req)
    {
        // Persist BOTH Queue and Desktop sections to %AppData%\MusicBot\appsettings.user.json.
        // That file is loaded as a higher-priority config source (see WebHost.cs), so values
        // here override the bundled appsettings.json on next startup. It survives both dotnet
        // rebuilds (which overwrite bin/appsettings.json) and Velopack updates.
        // Keys must match exactly what QueueSettingsService reads in its ctor.
        var userConfigPath = ResolveUserConfigPath();
        PersistUserConfigSection(userConfigPath, "Queue", new Dictionary<string, object?>
        {
            ["MaxSize"]                      = req.MaxQueueSize,
            ["MaxQueueSizeEnabled"]          = req.MaxQueueSizeEnabled,
            ["MaxSongsPerUser"]              = req.MaxSongsPerUser,
            ["MaxSongsPerUserEnabled"]       = req.MaxSongsPerUserEnabled,
            ["VotingEnabled"]                = req.VotingEnabled,
            ["PresenceCheckEnabled"]         = req.PresenceCheckEnabled,
            ["PresenceCheckWarningSeconds"]  = req.PresenceCheckWarningSeconds,
            ["PresenceCheckConfirmSeconds"]  = req.PresenceCheckConfirmSeconds,
            ["SaveDownloads"]                = req.SaveDownloads,
            ["AutoQueueEnabled"]             = req.AutoQueueEnabled,
            ["LoudnessNormalizationEnabled"] = req.LoudnessNormalizationEnabled,
            ["DirectSpotifyMode"]            = req.DirectSpotifyMode,
            ["ChatPlaylistRequestsEnabled"] = req.ChatPlaylistRequestsEnabled,
            ["SpotifyEnabled"] = req.SpotifyEnabled,
            ["YouTubeEnabled"] = req.YouTubeEnabled,
            ["YouTubeVolumePercent"] = req.YouTubeVolumePercent ?? _settings.YouTubeVolumePercent,
        });
        PersistUserConfigSection(userConfigPath, "Desktop", new Dictionary<string, object?>
        {
            ["OpenLogOnStart"] = req.OpenLogOnStart,
            ["NotificationsEnabled"] = req.NotificationsEnabled ?? _config.GetValue("Desktop:NotificationsEnabled", true),
        });

    }

    internal async Task ApplyRuntimePreferencesAsync(UpdateSettingsRequest req)
    {
        if (req.NotificationsEnabled is bool enabled) _config["Desktop:NotificationsEnabled"] = enabled.ToString();
        _settings.SetChatPlaylistRequests(req.ChatPlaylistRequestsEnabled);
        _settings.SetPlatforms(req.SpotifyEnabled, req.YouTubeEnabled);
        _settings.SetYouTubeVolume(req.YouTubeVolumePercent ?? _settings.YouTubeVolumePercent);
        foreach (var (_, active) in _userContext.GetAllActive()) active.Player.SetVolume(active.Player.Volume);
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var current = services.Queue.GetCurrentItem()?.Song;
        if (current?.SpotifyUri.StartsWith("youtube:") == true && !req.YouTubeEnabled
            || current?.SpotifyUri.StartsWith("spotify:") == true && !req.SpotifyEnabled)
        {
            // A metadata/play request already in flight must not start after the source is disabled.
            Interlocked.Increment(ref services.PlaybackGeneration);
            services.LocalPlaybackItemId = null;
        }
        if (current?.SpotifyUri.StartsWith("youtube:") == true && !req.YouTubeEnabled) await services.Player.StopAsync();
        if (!req.SpotifyEnabled && services.Spotify.IsAuthenticated)
        {
            try { await services.Spotify.PauseAsync(); }
            catch (Exception ex) { ActivityJournal.Record("spotify", "Kaynak ayarı", "", ex.Message, false); }
        }
        if (current?.SpotifyUri.StartsWith("youtube:") == true && !req.YouTubeEnabled || !req.SpotifyEnabled && current?.SpotifyUri.StartsWith("spotify:") == true)
            services.Queue.UpdateProgress(services.Queue.GetNowPlaying().ProgressMs, false);
        int effectiveMaxSize = 0;
        int effectiveMaxPerUser = 0;
        _userContext.GetOrCreate(LocalUser.Id).Queue.UpdateLimits(effectiveMaxSize, effectiveMaxPerUser);
        await _settings.UpdateAsync(
            req.MaxQueueSize,
            req.MaxSongsPerUser,
            req.VotingEnabled,
            req.PresenceCheckEnabled,
            req.PresenceCheckWarningSeconds,
            req.PresenceCheckConfirmSeconds,
            req.SaveDownloads,
            req.AutoQueueEnabled,
            req.LoudnessNormalizationEnabled,
            req.DirectSpotifyMode,
            req.MaxQueueSizeEnabled,
            req.MaxSongsPerUserEnabled);

        if (!req.VotingEnabled) HttpContext?.RequestServices.GetService<KickVoteService>()?.CancelVote();
        if (!req.PresenceCheckEnabled) HttpContext?.RequestServices.GetService<PresenceCheckService>()?.CancelCheck();

    }

    [HttpPut("autoqueue")]
    public async Task<IActionResult> SetAutoQueue([FromBody] AutoQueueToggle request)
    {
        PersistUserConfigSection(ResolveUserConfigPath(), "Queue", new Dictionary<string, object?> { ["AutoQueueEnabled"] = request.Enabled });
        await _settings.UpdateAsync(_settings.MaxQueueSize, _settings.MaxSongsPerUser, _settings.VotingEnabled,
            _settings.PresenceCheckEnabled, _settings.PresenceCheckWarningSeconds, _settings.PresenceCheckConfirmSeconds,
            _settings.SaveDownloads, request.Enabled, _settings.LoudnessNormalizationEnabled, true,
            _settings.MaxQueueSizeEnabled, _settings.MaxSongsPerUserEnabled);
        return NoContent();
    }

    public sealed record AutoQueueToggle(bool Enabled);

    private string ResolveUserConfigPath()
    {
        // Set by WebHost.cs at startup. Fall back to %AppData%\MusicBot\ if missing
        // (covers test scenarios where the config key isn't injected).
        var dataDir = _config["DataDirectory"];
        if (string.IsNullOrWhiteSpace(dataDir))
            dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MusicBot");
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, "appsettings.user.json");
    }

    /// <summary>
    /// Writes the given key/value pairs into the named top-level section of the user
    /// config file, preserving every other section as-is. Creates the file if missing.
    /// Best effort — failures (read-only file, parse error) are swallowed so a UI save
    /// never errors out; in-memory state is already updated and broadcast.
    /// </summary>
    private static readonly object ConfigWriteLock = new();

    private static void PersistUserConfigSection(string path, string sectionName, IDictionary<string, object?> updates)
    {
        lock (ConfigWriteLock)
        {
            var dict = new Dictionary<string, object?>();
            bool sectionFound = false;

            if (System.IO.File.Exists(path))
            {
                var json = System.IO.File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name == sectionName)
                        {
                            sectionFound = true;
                            var section = new Dictionary<string, object?>();
                            foreach (var dp in prop.Value.EnumerateObject())
                                section[dp.Name] = JsonElementToObject(dp.Value);
                            foreach (var kv in updates) section[kv.Key] = kv.Value;
                            dict[prop.Name] = section;
                        }
                        else
                        {
                            dict[prop.Name] = JsonElementToObject(prop.Value);
                        }
                    }
                }
            }

            if (!sectionFound)
                dict[sectionName] = new Dictionary<string, object?>(updates);

            var output = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            // Write to a temp file then move, so a partial write never corrupts the user config.
            var tempPath = path + ".tmp";
            System.IO.File.WriteAllText(tempPath, output);
            System.IO.File.Move(tempPath, path, overwrite: true);
        }
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => el.GetBoolean(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Null   => null,
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        JsonValueKind.Array  => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        _ => el.Clone(),
    };
}

public class UpdateSettingsRequest
{
    public bool ChatPlaylistRequestsEnabled { get; set; }
    public bool SpotifyEnabled { get; set; } = true;
    public bool YouTubeEnabled { get; set; } = true;
    public int    MaxQueueSize                     { get; set; } = 50;
    public bool   MaxQueueSizeEnabled              { get; set; } = true;
    public int    MaxSongsPerUser                  { get; set; } = 10;
    public bool   MaxSongsPerUserEnabled           { get; set; } = true;
    public bool   VotingEnabled                    { get; set; } = false;
    public bool   PresenceCheckEnabled             { get; set; } = false;
    public int    PresenceCheckWarningSeconds      { get; set; } = 30;
    public int    PresenceCheckConfirmSeconds      { get; set; } = 30;
    public bool   SaveDownloads                    { get; set; } = false;
    public bool   AutoQueueEnabled                 { get; set; } = false;
    public int? YouTubeVolumePercent { get; set; }
    public bool   LoudnessNormalizationEnabled     { get; set; } = true;
    public bool   DirectSpotifyMode                { get; set; } = true;
    public bool   OpenLogOnStart                   { get; set; } = false;
    public bool? NotificationsEnabled { get; set; }
}

public class UpdateCredentialsRequest
{
    public string? KickClientId { get; set; }
    public string? KickClientSecret { get; set; }
    public string? SpotifyClientId { get; set; }
    public string? SpotifyClientSecret { get; set; }
}
