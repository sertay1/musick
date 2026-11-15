using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Models;
using MusicBot.Data;
using MusicBot.Services;
using NAudio.CoreAudioApi;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/player")]
[Tags("Player")]
public class PlayerController : ControllerBase
{
    private readonly UserContextManager   _userContext;
    private readonly MusicBotDbContext    _db;
    private readonly QueueSettingsService _queueSettings;
    private readonly PlaybackSyncService  _sync;

    public PlayerController(
        UserContextManager userContext,
        MusicBotDbContext db,
        QueueSettingsService queueSettings,
        PlaybackSyncService sync)
    {
        _userContext   = userContext;
        _db            = db;
        _queueSettings = queueSettings;
        _sync          = sync;
    }

    /// <summary>Get current playback volume (0.0 – 1.0).</summary>
    [HttpGet("volume")]
    public IActionResult GetVolume()
    {
        var volume = _userContext.GetOrCreate(LocalUser.Id).Player.Volume;
        return Ok(new { volume });
    }

    /// <summary>Set playback volume (0.0 – 1.0) and persist the choice.</summary>
    [HttpPost("volume")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> SetVolume([FromBody] VolumeRequest request)
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        if (!float.IsFinite(request.Volume) || request.Volume < 0 || request.Volume > 1) return BadRequest(new { error="Ses seviyesi 0 ile 1 arasında olmalı." });

        if (_sync.UsesSpotify(services))
        {
            await services.Spotify.SetVolumeAsync((int)Math.Round(request.Volume * 100));
        }

        services.Player.SetVolume(request.Volume);
        services.VolumeSync.LocalChange(DateTimeOffset.UtcNow);
        var user = await _db.Users.FindAsync(LocalUser.Id);
        if (user != null)
        {
            user.AudioVolume = request.Volume;
            await _db.SaveChangesAsync();
        }
        return NoContent();
    }

    /// <summary>Seek to a position in the current track</summary>
    [HttpPost("seek")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Seek([FromBody] SeekRequest request)
    {
        var services=_userContext.GetOrCreate(LocalUser.Id);
        var state=services.Queue.GetNowPlaying(); var song=state.SpotifyTrack??state.Item?.Song;
        if(request.ExpectedTrackUri != null && request.ExpectedTrackUri != song?.SpotifyUri)
            return Conflict(new { error="Şarkı değişti; ilerleme çubuğunu yeniden ayarlayın." });
        var generation=Interlocked.Read(ref services.PlaybackGeneration);
        var position=Math.Max(0,request.PositionMs);
        if(song?.DurationMs is > 0)position=Math.Min(position,song.DurationMs);
        bool Current()=>generation==Interlocked.Read(ref services.PlaybackGeneration)
            && (request.ExpectedTrackUri==null || request.ExpectedTrackUri==(services.Queue.GetNowPlaying().SpotifyTrack??services.Queue.GetCurrentItem()?.Song)?.SpotifyUri);
        if(!_sync.UsesSpotify(services)&&services.Player.CurrentFilePath!=null) services.Player.SeekTo(position);
        else if(services.Spotify.IsAuthenticated)
        {
            if(services.Spotify is MusicBot.Services.Spotify.SpotifyService spotify) await spotify.SeekIfCurrentAsync(position,Current);
            else await services.Spotify.SeekAsync(position);
        }
        if(Current())services.Queue.UpdateProgress(position,state.IsPlaying,state.SpotifyTrack);
        return NoContent();
    }

    /// <summary>Restart current track or navigate to previous played song</summary>
    [HttpPost("previous")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Previous()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        if (!_sync.UsesSpotify(services) && services.Player.CurrentFilePath != null)
        {
            if (services.Player.PositionMs > 3000)
            {
                services.Player.SeekTo(0);
            }
            else
            {
                var last = await _db.PlayedSongs.OrderByDescending(p => p.PlayedAt).Skip(1).FirstOrDefaultAsync();
                if (last != null)
                {
                    services.Queue.PlayNow(new Song
                    {
                        SpotifyUri  = last.TrackId,
                        Title       = last.Title,
                        Artist      = last.Artist,
                        DurationMs  = last.DurationMs,
                        CoverUrl    = last.CoverUrl ?? ""
                    }, last.RequestedBy ?? "User", last.Platform ?? "local");
                    await _sync.StartCurrentTrackAsync(services, userInitiated: true);
                }
                else
                {
                    services.Player.SeekTo(0);
                }
            }
            return NoContent();
        }

        if (services.Spotify.IsAuthenticated)
        {
            var state=await services.Spotify.GetPlaybackStateAsync();
            if(state?.ProgressMs>3000)await services.Spotify.SeekAsync(0);
            else await services.Spotify.PreviousAsync();
        }
        ActivityJournal.Record("musick", "Önceki şarkı", "", "Önceki şarkıya geçildi.");
        return NoContent();
    }

    /// <summary>List active audio output devices (WASAPI endpoints and Spotify Connect devices).</summary>
    [HttpGet("devices")]
    public async Task<IActionResult> GetDevices()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var player   = services.Player;
        var activeId = player.DeviceId;

        using var enumerator = new MMDeviceEnumerator();
        var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        var localDevices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new
            {
                id        = d.ID,
                name      = d.FriendlyName,
                isDefault = d.ID == defaultId,
                isActive  = d.ID == (activeId ?? defaultId),
                type      = "local"
            })
            .ToList();

        string? activeSpotifyDeviceId = null;
        var spotifyDeviceList = new List<object>();
        if (services.Spotify.IsAuthenticated)
        {
            try
            {
                var sDevices = await services.Spotify.GetDevicesAsync();
                foreach (var sd in sDevices)
                {
                    if (sd.IsActive) activeSpotifyDeviceId = sd.Id;
                    spotifyDeviceList.Add(new
                    {
                        id        = sd.Id,
                        name      = $"{sd.Name} ({sd.Type})",
                        isDefault = false,
                        isActive  = sd.IsActive,
                        type      = "spotify"
                    });
                }
            }
            catch { }
        }

        var allDevices = localDevices.Cast<object>().Concat(spotifyDeviceList).ToList();
        return Ok(new
        {
            activeDeviceId        = activeId,
            activeSpotifyDeviceId = activeSpotifyDeviceId,
            devices               = allDevices
        });
    }

    // A lazy YouTube iframe can start a new WebView audio process after the device was chosen.
    [HttpPost("refresh-output")]
    public async Task<IActionResult> RefreshOutput()
    {
        var profile = await _db.Users.FindAsync(LocalUser.Id);
        if (profile?.AudioDeviceId == null) return NoContent();
        try { MusicBot.Services.Player.SpotifyAudioRouting.Apply(profile.AudioDeviceId, MusicBot.Services.Player.SpotifyAudioRouting.AppAudioProcesses()); }
        catch (Exception ex) { ActivityJournal.Record("musick", "Ses aygıtı", "", ex.Message, false); }
        return NoContent();
    }

    /// <summary>Set audio output device (WASAPI or Spotify Connect) and persist local choice.</summary>
    [HttpPost("device")]
    public async Task<IActionResult> SetDevice([FromBody] SetDeviceRequest request)
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);

        if (request.Type == "spotify" && !string.IsNullOrEmpty(request.DeviceId) && services.Spotify.IsAuthenticated)
        {
            await services.Spotify.TransferPlaybackAsync(request.DeviceId);
            var preference = await _db.PlatformConfigs.FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "spotify_device");
            if (preference == null) { preference = new PlatformConfig { UserId = LocalUser.Id, Platform = "spotify_device" }; _db.PlatformConfigs.Add(preference); }
            preference.ConfigJson = System.Text.Json.JsonSerializer.Serialize(request.DeviceId);
            await _db.SaveChangesAsync();
            return Ok(new { success = true, deviceId = request.DeviceId, type = "spotify" });
        }

        await services.Player.SetDeviceAsync(request.DeviceId);
        string? warning = null;
        try
        {
            MusicBot.Services.Player.SpotifyAudioRouting.Apply(request.DeviceId,
                MusicBot.Services.Player.SpotifyAudioRouting.SpotifyProcesses().Concat(MusicBot.Services.Player.SpotifyAudioRouting.AppAudioProcesses()));
        }
        catch (Exception ex)
        {
            warning = "Musick ses çıkışı kaydedildi. Spotify yönlendirmesi henüz uygulanamadı; otomatik tekrar denenecek.";
            ActivityJournal.Record("musick", "Ses çıkışı", "", warning + " " + ex.Message, false);
        }
        var route = await _db.PlatformConfigs.FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "spotify_output");
        if (route == null) { route = new PlatformConfig { UserId = LocalUser.Id, Platform = "spotify_output" }; _db.PlatformConfigs.Add(route); }
        route.ConfigJson = System.Text.Json.JsonSerializer.Serialize(request.DeviceId);
        var user = await _db.Users.FindAsync(LocalUser.Id);
        if (user != null)
        {
            user.AudioDeviceId = request.DeviceId;
        }
        await _db.SaveChangesAsync();
        if (warning == null) ActivityJournal.Record("musick", "Ses çıkışı", "", "Spotify ve Musick için ses çıkışı kaydedildi.");
        return Ok(new { success = true, deviceId = request.DeviceId, type = "local", warning });
    }
}

public class VolumeRequest    { public float   Volume     { get; set; } }
public class SeekRequest      { public int PositionMs { get; set; } public string? ExpectedTrackUri { get; set; } }
public class SetDeviceRequest { public string? DeviceId   { get; set; } public string? Type { get; set; } }
