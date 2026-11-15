using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Models;
using MusicBot.Data;
using MusicBot.Hubs;
using MusicBot.Services;
using MusicBot.Services.Commands;
using MusicBot.Services.Platforms;

namespace MusicBot.Controllers;

[ApiController, Route("api/settings/backup")]
public sealed class SettingsBackupController(MusicBotDbContext db, IServiceProvider services,
    UserContextManager users, CommandConfigService commands, TickerMessageService ticker, IHubContext<OverlayHub> hub) : ControllerBase
{
    private static readonly SemaphoreSlim Writes = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private SettingsController Settings() { var c = ActivatorUtilities.CreateInstance<SettingsController>(services); c.ControllerContext = ControllerContext; return c; }
    [HttpGet]
    public async Task<IActionResult> Export()
    {
        var settings = (OkObjectResult)Settings().GetSettings();
        var rows = await db.PlatformConfigs.AsNoTracking().Where(p => p.UserId == LocalUser.Id
            && (p.Platform == "kick" || p.Platform == "overlay_preferences" || p.Platform == "spotify_output" || p.Platform == "spotify_device")).ToListAsync();
        T? Read<T>(string platform) { var row = rows.FirstOrDefault(p => p.Platform == platform); return row == null ? default : JsonSerializer.Deserialize<T>(row.ConfigJson, Json); }
        var backup = new SettingsBackup
        {
            Queue = JsonSerializer.Deserialize<UpdateSettingsRequest>(JsonSerializer.Serialize(settings.Value, Json), Json)!,
            Commands = commands.Current,
            Kick = Read<KickPreferences>("kick") ?? new(),
            Overlays = Read<OverlayPreferences>("overlay_preferences") ?? new(),
            Audio = new() { OutputDeviceId = Read<string>("spotify_output"), SpotifyDeviceId = Read<string>("spotify_device"), Volume = users.GetOrCreate(LocalUser.Id).Player.Volume },
            Appearance = new() { Theme = AppEvents.CurrentTheme, Language = AppEvents.Language },
            BannedSongs = services.GetRequiredService<BannedSongService>().GetAll().ToList(),
            TickerMessages = ticker.GetAll()
        };
        return Ok(backup);
    }
    [HttpPost("import"), RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> Import([FromBody] SettingsBackup backup)
    {
        try { backup.Validate(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        await Writes.WaitAsync();
        try
        {
            // Validate the complete file first. Only explicitly listed preferences can be persisted.
            backup.Commands = CommandConfigTransfer.Read(JsonSerializer.SerializeToElement(backup.Commands, Json));
            var warnings = new List<string>();
            async Task WriteRow(string platform, object? value)
            {
                var row = await db.PlatformConfigs.FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == platform);
                if (row == null) { row = new() { UserId = LocalUser.Id, Platform = platform }; db.PlatformConfigs.Add(row); }
                row.ConfigJson = JsonSerializer.Serialize(value, Json); row.AutoConnect = platform == "kick";
            }
            var settings = Settings();
            var previousCommands = CommandConfigTransfer.Read(JsonSerializer.SerializeToElement(commands.Current, Json));
            var data = services.GetRequiredService<IConfiguration>()["DataDirectory"]
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicBot");
            var files = new[] { "commands.json", "appsettings.user.json", "banned-songs.json", "ticker-messages.json" }
                .Select(name => Path.Combine(data, name)).ToDictionary(path => path, path => System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null);
            if (files.Keys.Any(Directory.Exists)) return StatusCode(500, new { error = "Ayar dosyası yerine klasör bulundu; hiçbir tercih değiştirilmedi." });
            await using var transaction = await db.Database.BeginTransactionAsync();
            try
            {
                if (!await commands.SaveConfigurationAsync(backup.Commands)) throw new IOException("Komut ayarları yazılamadı.");
                settings.PersistPreferences(backup.Queue);
                backup.BannedSongs = backup.BannedSongs.DistinctBy(entry => entry.Uri).ToList();
                for (var index = 0; index < backup.TickerMessages.Count; index++) backup.TickerMessages[index].Order = index;
                WritePreferenceFile(Path.Combine(data, "banned-songs.json"), JsonSerializer.SerializeToUtf8Bytes(backup.BannedSongs));
                WritePreferenceFile(Path.Combine(data, "ticker-messages.json"), JsonSerializer.SerializeToUtf8Bytes(backup.TickerMessages));
            await WriteRow("kick", backup.Kick);
            await WriteRow("overlay_preferences", backup.Overlays);
            await WriteRow("spotify_output", backup.Audio.OutputDeviceId);
            await WriteRow("spotify_device", backup.Audio.SpotifyDeviceId);
            var profile = await db.Users.FindAsync(LocalUser.Id);
            if (profile != null) { profile.AudioVolume = backup.Audio.Volume; profile.AudioDeviceId = backup.Audio.OutputDeviceId; }
            await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception error)
            {
                await transaction.RollbackAsync();
                db.ChangeTracker.Clear();
                var restoreErrors = new List<string>();
                foreach (var (path, bytes) in files)
                {
                    try
                    {
                        if (bytes == null) { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
                        else if (!System.IO.File.Exists(path) || !System.IO.File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) WritePreferenceFile(path, bytes);
                    }
                    catch (Exception) { restoreErrors.Add(Path.GetFileName(path)); }
                }
                commands.RestoreInMemory(previousCommands);
                services.GetRequiredService<ILogger<SettingsBackupController>>().LogWarning(error, "Preference import rolled back");
                return StatusCode(500, new { error = restoreErrors.Count == 0 ? "Ayarlar içe aktarılamadı; önceki ayarlar geri yüklendi."
                    : "İçe aktarma başarısız; şu dosyalar geri yüklenemedi: " + string.Join(", ", restoreErrors) });
            }
            await transaction.DisposeAsync();
            try
            {
                services.GetRequiredService<BannedSongService>().ReplaceAll(backup.BannedSongs, persist: false);
                ticker.ReplaceAll(backup.TickerMessages, persist: false);
                try { await settings.ApplyRuntimePreferencesAsync(backup.Queue); }
                catch (Exception) { warnings.Add("Tercihler kaydedildi; oynatıcıya uygulanması için Musick'i yeniden başlatın."); }
                var user = users.GetOrCreate(LocalUser.Id);
                var playerSettings = ActivatorUtilities.CreateInstance<PlayerController>(services);
                playerSettings.ControllerContext = ControllerContext;
                user.Player.SetVolume(backup.Audio.Volume);
                user.VolumeSync.LocalChange(DateTimeOffset.UtcNow);
                try
                {
                    if (user.Spotify.IsAuthenticated || user.Player.CurrentFilePath != null)
                        await playerSettings.SetDevice(new SetDeviceRequest { DeviceId = backup.Audio.OutputDeviceId });
                    else await user.Player.SetDeviceAsync(backup.Audio.OutputDeviceId);
                }
                catch
                {
                    await user.Player.SetDeviceAsync(null);
                    var activeProfile = await db.Users.FindAsync(LocalUser.Id);
                    if (activeProfile != null) activeProfile.AudioDeviceId = null;
                    await WriteRow("spotify_output", null); await db.SaveChangesAsync();
                    warnings.Add("Bu bilgisayarda seçilen ses aygıtı kullanılamadı; varsayılan aygıt seçildi.");
                }
                var manager = services.GetRequiredService<PlatformConnectionManager>();
                manager.UpdateKickSettings(LocalUser.Id, backup.Kick.CommandRoles, backup.Kick.AllowedUsers);
                if (!string.IsNullOrWhiteSpace(backup.Kick.Channel) && services.GetRequiredService<KickAuthService>().IsAuthenticated)
                {
                    manager.SetUserSlug(LocalUser.Id, LocalUser.Slug);
                    manager.ConnectKick(LocalUser.Id, new PlatformConnectionManager.KickPlatformConfig(backup.Kick.Channel, backup.Kick.CommandRoles, backup.Kick.AllowedUsers));
                }
                AppEvents.SetTheme(backup.Appearance.Theme); AppEvents.Language = backup.Appearance.Language;
                await hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("overlay:preferences", backup.Overlays);
            }
            catch (Exception error)
            {
                services.GetRequiredService<ILogger<SettingsBackupController>>().LogWarning(error, "Saved preferences could not be fully applied at runtime");
                warnings.Add("Ayarlar kaydedildi; bazı değişikliklerin uygulanması için Musick'i yeniden başlatın.");
            }
            return Ok(new { appearance = backup.Appearance, warnings, message = "Ayarlar içe aktarıldı. Hesaplar ve API bilgileri değiştirilmedi." });
        }
        finally { Writes.Release(); }
    }
    private static void WritePreferenceFile(string path, byte[] bytes)
    {
        var temporary = path + ".import.tmp";
        try { System.IO.File.WriteAllBytes(temporary, bytes); System.IO.File.Move(temporary, path, true); }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }

}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SettingsBackup
{
    [JsonRequired]
    public string Format { get; set; } = "musick.settings";
    [JsonRequired]
    public int Version { get; set; } = 1;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonRequired]
    public UpdateSettingsRequest Queue { get; set; } = new();
    [JsonRequired]
    public CommandsConfiguration Commands { get; set; } = CommandsConfiguration.CreateDefault();
    [JsonRequired]
    public KickPreferences Kick { get; set; } = new();
    [JsonRequired]
    public OverlayPreferences Overlays { get; set; } = new();
    [JsonRequired]
    public AudioPreferences Audio { get; set; } = new();
    [JsonRequired]
    public AppearancePreferences Appearance { get; set; } = new();
    [JsonRequired]
    public List<TickerMessage> TickerMessages { get; set; } = [];
    [JsonRequired]
    public List<BannedSongEntry> BannedSongs { get; set; } = [];
    public void Validate()
    {
        if (Format != "musick.settings" || Version != 1 || Queue == null || Kick == null || Overlays == null || Audio == null || Appearance == null || Commands == null || TickerMessages == null || BannedSongs == null)
            throw new ArgumentException("Ayar dosyasının biçimi veya sürümü geçersiz.");
        CommandConfigTransfer.Validate(Commands);
        if (Queue.YouTubeVolumePercent is < 0 or > 100 || Queue.MaxQueueSize is < 1 or > 10000 || Queue.MaxSongsPerUser is < 0 or > 10000
            || Queue.PresenceCheckWarningSeconds is < 0 or > 3600 || Queue.PresenceCheckConfirmSeconds is < 1 or > 3600
            || !Overlays.IsValid() || Appearance.Theme is not ("dark" or "light") || Appearance.Language is not ("tr" or "en")
            || !float.IsFinite(Audio.Volume) || Audio.Volume is < 0 or > 1 || Audio.OutputDeviceId?.Length > 512 || Audio.SpotifyDeviceId?.Length > 512)
            throw new ArgumentException("Ayar dosyasında geçersiz değerler var.");
        if (BannedSongs.Count > 10000 || BannedSongs.Any(b => b == null || string.IsNullOrWhiteSpace(b.Uri) || b.Uri.Length > 512 || b.Title == null || b.Title.Length > 1000 || b.Artist == null || b.Artist.Length > 1000))
            throw new ArgumentException("Yasaklı şarkı ayarları geçersiz.");
        var roles = new[] { "all", "everyone", "broadcaster", "moderator", "vip", "og", "subscriber", "list" };
        if (Kick.Channel == null || Kick.Channel.Length > 128 || Kick.CommandRoles == null || Kick.CommandRoles.Length > 10
            || Kick.CommandRoles.Any(r => !roles.Contains(r)) || Kick.AllowedUsers == null || Kick.AllowedUsers.Length > 1000
            || Kick.AllowedUsers.Any(u => string.IsNullOrWhiteSpace(u) || u.Length > 100)
            || TickerMessages.Count > 1000 || TickerMessages.Any(m => m == null || m.Text == null || m.Text.Length > 10000
                || m.IntervalMinutes is < 1 or > 10080 || m.MinChatMessages < 0 || m.Platforms == null || m.Platforms.Any(p => p is not ("kick" or "twitch" or "tiktok" or "youtube"))))
            throw new ArgumentException("Kanal, rol veya otomatik mesaj ayarları geçersiz.");
    }
}
public sealed class KickPreferences
{
    public string Channel { get; set; } = "";
    public string[] CommandRoles { get; set; } = ["all"];
    public string[] AllowedUsers { get; set; } = [];
}
public sealed class AudioPreferences
{
    public float Volume { get; set; } = 1;
    public string? OutputDeviceId { get; set; }
    public string? SpotifyDeviceId { get; set; }
}
public sealed class AppearancePreferences
{
    public string Theme { get; set; } = "dark";
    public string Language { get; set; } = "tr";
}
