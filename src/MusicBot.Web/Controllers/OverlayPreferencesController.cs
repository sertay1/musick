using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicBot.Data;
using MusicBot.Core.Models;
using MusicBot.Hubs;
using MusicBot.Services;

namespace MusicBot.Controllers;

[ApiController, Route("api/overlay-preferences")]
public class OverlayPreferencesController(MusicBotDbContext db, IHubContext<OverlayHub> hub) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var row = await db.PlatformConfigs.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "overlay_preferences");
        return row == null ? Ok(new { }) : Content(row.ConfigJson, "application/json");
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] OverlayPreferences preferences)
    {
        if (!preferences.IsValid()) return BadRequest(new { error = "Overlay ayarları geçersiz." });
        var row = await db.PlatformConfigs.FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "overlay_preferences");
        if (row == null) { row = new PlatformConfig { UserId = LocalUser.Id, Platform = "overlay_preferences" }; db.PlatformConfigs.Add(row); }
        row.ConfigJson = System.Text.Json.JsonSerializer.Serialize(preferences, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await db.SaveChangesAsync();
        await hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("overlay:preferences", preferences);
        return NoContent();
    }
}

public sealed class OverlayPreferences
{
    public string Theme { get; set; } = "glass";
    public string Design { get; set; } = "studio";
    public string Mode { get; set; } = "static";
    public string Accent { get; set; } = "";
    public string Text { get; set; } = "";
    public string Background { get; set; } = "";
    public double Opacity { get; set; } = 1;
    public double Scale { get; set; } = 1;
    public double BackgroundOpacity { get; set; } = 1;
    public bool ShowShadow { get; set; } = true;
    public double ShadowStrength { get; set; } = 0.5;
    public bool? NeonEnabled { get; set; }
    public string NeonColor { get; set; } = "";
    public double GlowStrength { get; set; } = 0.65;
    public double NeonSpeed { get; set; } = 1;
    public int Radius { get; set; } = 16;
    public int TitleSize { get; set; } = 0;
    public bool ShowArtwork { get; set; } = true;
    public bool ShowProgress { get; set; } = true;
    public bool ShowRequester { get; set; } = true;
    public bool BackgroundArt { get; set; }
    public bool Animate { get; set; }
    public bool KeepLast { get; set; } = true;
    public int QueueLimit { get; set; } = 8;
    public bool IsValid() => Theme is "glass" or "minimal" or "colorful" or "dark" or "light" or "midnight" or "warm"
        && Design is "studio" or "vinyl" or "broadcast" or "cassette" or "type" or "compact" or "poster" or "ribbon" or "classic"
        && Mode is "static" or "pulse" or "chroma" or "breathe" or "sweep" or "flicker" && Opacity is >= 0.1 and <= 1 && Scale is >= 0.5 and <= 2
        && BackgroundOpacity is >= 0 and <= 1 && ShadowStrength is >= 0 and <= 1 && GlowStrength is >= 0 and <= 1 && NeonSpeed is >= 0.25 and <= 3
        && Radius is >= 0 and <= 60 && TitleSize is >= 0 and <= 60 && QueueLimit is >= 1 and <= 30
        && new[] { Accent, Text, Background, NeonColor }.All(c => c != null && (c == "" || System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$")));
}
