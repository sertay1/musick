using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MusicBot.Controllers;
using MusicBot.Data;
using MusicBot.Services;
using Xunit;
namespace MusicBot.Tests;
public class SettingsBackupTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"apiKey\":\"no\"}")]
    [InlineData("{\"format\":\"musick.settings\",\"version\":99}")]
    public void IncompleteOrCredentialDocumentsAreRejected(string text) => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SettingsBackup>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    [Fact]
    public async Task ExportAndImport_KeepCredentialsAndAccountTokensUntouched()
    {
        var path = Path.Combine(Path.GetTempPath(), "musick-backup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        var theme = AppEvents.CurrentTheme; var language = AppEvents.Language;
        try
        {
            var builder = WebHost.CreateBuilder([]); builder.Configuration["DataDirectory"] = path;
            builder.Configuration["ConnectionStrings:DefaultConnection"] = "Data Source=" + Path.Combine(path, "test.db");
            builder.Configuration["Spotify:ClientSecret"] = "private-api-secret";
            await File.WriteAllTextAsync(Path.Combine(path, "appsettings.user.json"), "{\"Spotify\":{\"ClientSecret\":\"preserve-file-secret\"},\"Relay\":{\"ApiKey\":\"preserve-relay-key\"}}");
            await using var app = WebHost.Configure(builder);
            using var scope = app.Services.CreateScope(); var sp = scope.ServiceProvider; var db = sp.GetRequiredService<MusicBotDbContext>();
            // A future auth row must remain outside the explicit preference allowlist.
            db.PlatformConfigs.Add(new() { UserId = LocalUser.Id, Platform = "future_auth", ConfigJson = "{\"token\":\"private-account-token\"}" }); await db.SaveChangesAsync();
            var c = ActivatorUtilities.CreateInstance<SettingsBackupController>(sp);
            c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } };
            var result = Assert.IsType<OkObjectResult>(await c.Export());
            var backup = Assert.IsType<SettingsBackup>(result.Value);
            var json = JsonSerializer.Serialize(backup);
            Assert.DoesNotContain("private-api-secret", json); Assert.DoesNotContain("private-account-token", json); Assert.DoesNotContain("ClientSecret", json);
            backup.Appearance.Theme = "light"; backup.Appearance.Language = "en"; backup.Audio.Volume = 0.35f;
            backup.Commands.Commands["play"].AllowedRoles = ["vip"];
            Assert.IsType<OkObjectResult>(await c.Import(backup));
            Assert.Equal(0.35f, (await db.Users.FindAsync(LocalUser.Id))!.AudioVolume);
            Assert.Equal("private-api-secret", builder.Configuration["Spotify:ClientSecret"]);
            var savedFile = await File.ReadAllTextAsync(Path.Combine(path, "appsettings.user.json"));
            Assert.Contains("preserve-file-secret", savedFile); Assert.Contains("preserve-relay-key", savedFile);
            Assert.Equal("{\"token\":\"private-account-token\"}", (await db.PlatformConfigs.SingleAsync(p => p.Platform == "future_auth")).ConfigJson);
            var roundTrip = Assert.IsType<SettingsBackup>(Assert.IsType<OkObjectResult>(await c.Export()).Value);
            Assert.Equal("light", roundTrip.Appearance.Theme); Assert.Equal("en", roundTrip.Appearance.Language); Assert.Equal(0.35f, roundTrip.Audio.Volume);
            Assert.Equal(new[] { "vip" }, roundTrip.Commands.Commands["play"].AllowedRoles);
            backup.Queue.PresenceCheckConfirmSeconds = -1;
            Assert.IsType<BadRequestObjectResult>(await c.Import(backup));
        }
        finally { AppEvents.SetTheme(theme); AppEvents.Language = language; Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }
}
