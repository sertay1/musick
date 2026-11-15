using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicBot.Data;
using MusicBot.Services.Platforms;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/platforms")]
[Tags("Platforms")]
public class PlatformsController : ControllerBase
{
    private readonly MusicBotDbContext         _db;
    private readonly PlatformConnectionManager _manager;
    private readonly KickAuthService           _kickAuth;

    public PlatformsController(
        MusicBotDbContext db,
        PlatformConnectionManager manager,
        KickAuthService kickAuth)
    {
        _db       = db;
        _manager  = manager;
        _kickAuth = kickAuth;
    }

    /// <summary>Get Kick platform config and connection status</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var configs = await _db.PlatformConfigs
            .Where(p => p.UserId == LocalUser.Id && p.Platform == "kick")
            .ToListAsync();

        var cfg   = configs.FirstOrDefault();
        var state = _manager.GetState(LocalUser.Id, "kick");

        var result = new[]
        {
            new PlatformStateDto
            {
                Platform     = "kick",
                Status       = state.Status.ToString().ToLower(),
                ErrorMessage = state.ErrorMessage,
                AutoConnect  = cfg?.AutoConnect ?? false,
                Config       = cfg?.ConfigJson != null
                    ? JsonSerializer.Deserialize<JsonElement>(cfg.ConfigJson)
                    : null
            }
        };

        return Ok(result);
    }

    /// <summary>Save Kick configuration</summary>
    [HttpPut("kick")]
    public async Task<IActionResult> SaveKick([FromBody] SaveKickRequest req)
    {
        await UpsertConfig("kick",
            JsonSerializer.Serialize(new { channel = req.Channel, commandRoles = req.CommandRoles, allowedUsers = req.AllowedUsers }),
            true);

        _manager.SetUserSlug(LocalUser.Id, LocalUser.Slug);
        _manager.UpdateKickSettings(LocalUser.Id, req.CommandRoles, req.AllowedUsers);

        if (!string.IsNullOrWhiteSpace(req.Channel) && _kickAuth.IsAuthenticated)
        {
            _manager.ConnectKick(LocalUser.Id, new PlatformConnectionManager.KickPlatformConfig(req.Channel, req.CommandRoles, req.AllowedUsers));
        }

        return NoContent();
    }

    /// <summary>Connect Kick</summary>
    [HttpPost("{platform}/connect")]
    public async Task<IActionResult> Connect(string platform)
    {
        if (platform.ToLower() != "kick")
            return BadRequest(new { error = "Yalnızca Kick platformu desteklenmektedir." });

        var cfg = await _db.PlatformConfigs
            .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "kick");

        if (cfg == null)
            return BadRequest(new { error = "Kick için kayıtlı yapılandırma bulunamadı." });

        _manager.SetUserSlug(LocalUser.Id, LocalUser.Slug);

        if (!_kickAuth.IsAuthenticated)
            return BadRequest(new { error = "Kick hesabı bağlı değil — lütfen önce Kick hesabınızı bağlayın." });

        var c = JsonSerializer.Deserialize<KickJson>(cfg.ConfigJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var channel = !string.IsNullOrWhiteSpace(c?.Channel) ? c.Channel : (_kickAuth.ChannelName ?? "");
        if (string.IsNullOrWhiteSpace(channel))
            return BadRequest(new { error = "Kick kanalı belirtilmemiş — lütfen ayarlar bölümünden kanalınızı girin." });

        _manager.ConnectKick(LocalUser.Id, new PlatformConnectionManager.KickPlatformConfig(channel, c?.CommandRoles, c?.AllowedUsers));

        return NoContent();
    }

    /// <summary>Disconnect Kick</summary>
    [HttpPost("{platform}/disconnect")]
    public IActionResult Disconnect(string platform)
    {
        _manager.Disconnect(LocalUser.Id, "kick");
        return NoContent();
    }

    /// <summary>Forget Kick account</summary>
    [HttpPost("{platform}/forget")]
    public async Task<IActionResult> Forget(string platform)
    {
        _manager.Disconnect(LocalUser.Id, "kick");

        var cfg = await _db.PlatformConfigs
            .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "kick");
        if (cfg != null)
        {
            cfg.AutoConnect = false;
            await _db.SaveChangesAsync();
        }

        await _kickAuth.DisconnectAsync();
        return NoContent();
    }

    private async Task UpsertConfig(string platform, string json, bool autoConnect)
    {
        var existing = await _db.PlatformConfigs
            .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == platform);

        if (existing != null)
        {
            existing.ConfigJson  = json;
            existing.AutoConnect = autoConnect;
        }
        else
        {
            _db.PlatformConfigs.Add(new PlatformConfig
            {
                UserId      = LocalUser.Id,
                Platform    = platform,
                ConfigJson  = json,
                AutoConnect = autoConnect
            });
        }

        await _db.SaveChangesAsync();
    }

    private sealed class KickJson { public string? Channel { get; set; } public string[]? CommandRoles { get; set; } public string[]? AllowedUsers { get; set; } }
}

public class PlatformStateDto
{
    public string       Platform     { get; set; } = string.Empty;
    public string       Status       { get; set; } = "disconnected";
    public string?      ErrorMessage { get; set; }
    public bool         AutoConnect  { get; set; }
    public JsonElement? Config       { get; set; }
}

public class SaveKickRequest
{
    public string   Channel      { get; set; } = string.Empty;
    public bool     AutoConnect  { get; set; }
    public string[] CommandRoles { get; set; } = ["all"];
    public string[] AllowedUsers { get; set; } = [];
}
