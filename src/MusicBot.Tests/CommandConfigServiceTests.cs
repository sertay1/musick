using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Core.Models;
using MusicBot.Services.Commands;
using Xunit;

namespace MusicBot.Tests;

public class CommandConfigServiceTests : IDisposable
{
    [Fact]
    public void TurkishHistoryCommand_ResolvesWithAccentedSpelling()
    {
        using var service = new CommandConfigService(NullLogger<CommandConfigService>.Instance, _configuration);
        Assert.True(service.TryResolveCommand("!geçmiş", out var key, out var definition));
        Assert.Equal("gecmis", key);
        Assert.NotNull(definition);
    }
    [Fact]
    public void ExplicitRoleCooldown_OverridesExemption_AndZeroMeansNoWait()
    {
        using var service = new CommandConfigService(NullLogger<CommandConfigService>.Instance, _configuration);
        var cooldowns = new Dictionary<string, int> { ["moderator"] = 30 };
        Assert.True(service.CheckCooldownWithRole("play", "mod", 5, cooldowns, ["moderator"], ["moderator"], "owner", out _));
        service.RecordSuccessfulCommand("play", "mod");
        Assert.False(service.CheckCooldownWithRole("play", "mod", 5, cooldowns, ["moderator"], ["moderator"], "owner", out _));
        cooldowns["moderator"] = 0;
        Assert.True(service.CheckCooldownWithRole("play", "mod", 5, cooldowns, ["moderator"], ["moderator"], "owner", out _));
    }

    [Fact]
    public async Task SavingExplicitRoles_PreservesIndependentPermissionsAfterReload()
    {
        using var service = new CommandConfigService(NullLogger<CommandConfigService>.Instance, _configuration);
        var config = CommandsConfiguration.CreateDefault();
        config.Commands["play"].AllowedRoles = ["subscriber"];
        Assert.True(await service.SaveConfigurationAsync(config));
        Assert.True(service.LoadConfiguration(false));
        Assert.Equal(new[] { "subscriber" }, service.Current.Commands["play"].AllowedRoles);
    }
    private readonly string _tempDir;
    private readonly IConfiguration _configuration;

    public CommandConfigServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MusicBot_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = _tempDir
            })
            .Build();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void Initializes_CreatesDefaultCommandsJson_WhenFileMissing()
    {
        using var svc = new CommandConfigService(NullLogger<CommandConfigService>.Instance, _configuration);
        var configPath = Path.Combine(_tempDir, "commands.json");

        Assert.True(File.Exists(configPath));
        Assert.NotNull(svc.Current);
        Assert.True(svc.Current.Commands.ContainsKey("play"));
        Assert.True(svc.Current.Commands.ContainsKey("skip"));
        Assert.True(svc.Current.Commands.ContainsKey("sira"));
        Assert.True(svc.Current.Commands.ContainsKey("simdi"));
        Assert.True(svc.Current.Commands.ContainsKey("sil"));
    }

    [Theory]
    [InlineData("sr", "play")]
    [InlineData("!sr", "play")]
    [InlineData("istek", "play")]
    [InlineData("!istek", "play")]
    [InlineData("skip", "skip")]
    [InlineData("!atla", "skip")]
    [InlineData("gec", "skip")]
    [InlineData("sira", "sira")]
    [InlineData("!queue", "sira")]
    [InlineData("liste", "sira")]
    [InlineData("simdi", "simdi")]
    [InlineData("!calan", "simdi")]
    [InlineData("sil", "sil")]
    [InlineData("!iptal", "sil")]
    [InlineData("revoke", "sil")]
    [InlineData("ses", "ses")]
    [InlineData("!ses", "ses")]
    [InlineData("volume", "ses")]
    [InlineData("!volume", "ses")]
    [InlineData("vol", "ses")]
    public void TryResolveCommand_ResolvesAliasesCorrectly(string alias, string expectedKey)
    {
        using var svc = new CommandConfigService(NullLogger<CommandConfigService>.Instance, _configuration);

        var resolved = svc.TryResolveCommand(alias, out var commandKey, out var def);

        Assert.True(resolved);
        Assert.Equal(expectedKey, commandKey);
        Assert.NotNull(def);
    }

    [Fact]
    public void LoadConfiguration_MergesMissingBuiltInCommands_WhenOldJsonLoaded()
    {
        var configPath = Path.Combine(_tempDir, "commands.json");
        // Write an old commands.json that is missing "ses"
        var partialJson = @"{
          ""Commands"": {
            ""play"": { ""Name"": ""ekle"", ""Aliases"": [], ""Enabled"": true }
          }
        }";
        File.WriteAllText(configPath, partialJson);

        using var svc = new CommandConfigService(NullLogger<CommandConfigService>.Instance, _configuration);

        Assert.True(svc.Current.Commands.ContainsKey("play"));
        Assert.True(svc.Current.Commands.ContainsKey("ses")); // Successfully auto-merged!
        Assert.True(svc.TryResolveCommand("ses", out _, out _));
        Assert.True(svc.TryResolveCommand("volume", out _, out _));
    }

    [Fact]
    public void CheckCooldown_EnforcesCooldownDuration()
    {
        using var svc = new CommandConfigService(NullLogger<CommandConfigService>.Instance, _configuration);

        // First attempt: should succeed
        var first = svc.CheckCooldown("sr", "izleyici1", 10, out var remaining1);
        Assert.True(first);
        Assert.Equal(0, remaining1);

        svc.RecordSuccessfulCommand("sr", "izleyici1");
        // Immediate second attempt after success: should be blocked by cooldown
        var second = svc.CheckCooldown("sr", "izleyici1", 10, out var remaining2);
        Assert.False(second);
        Assert.True(remaining2 > 0 && remaining2 <= 10);

        // Different user: should succeed independently
        var otherUser = svc.CheckCooldown("sr", "izleyici2", 10, out var remaining3);
        Assert.True(otherUser);
        Assert.Equal(0, remaining3);
    }

    [Fact]
    public void IsRoleAllowed_Everyone_AllowsAll()
    {
        Assert.True(CommandConfigService.IsRoleAllowed("everyone", [], "izleyici", "yayinci"));
        Assert.True(CommandConfigService.IsRoleAllowed("everyone", ["subscriber"], "abone", "yayinci"));
        Assert.True(CommandConfigService.IsRoleAllowed("everyone", ["moderator"], "mod", "yayinci"));
        Assert.True(CommandConfigService.IsRoleAllowed("everyone", ["broadcaster"], "yayinci", "yayinci"));
    }

    [Fact]
    public void IsRoleAllowed_Subscriber_EnforcedCorrectly()
    {
        Assert.False(CommandConfigService.IsRoleAllowed("subscriber", [], "izleyici", "yayinci"));
        Assert.True(CommandConfigService.IsRoleAllowed("subscriber", ["subscriber"], "abone", "yayinci"));
        Assert.True(CommandConfigService.IsRoleAllowed("subscriber", ["moderator"], "mod", "yayinci"));
        Assert.True(CommandConfigService.IsRoleAllowed("subscriber", [], "yayinci", "yayinci")); // Broadcaster always allowed
    }

    [Fact]
    public void IsRoleAllowed_Moderator_EnforcedCorrectly()
    {
        Assert.False(CommandConfigService.IsRoleAllowed("moderator", [], "izleyici", "yayinci"));
        Assert.False(CommandConfigService.IsRoleAllowed("moderator", ["subscriber"], "abone", "yayinci"));
        Assert.True(CommandConfigService.IsRoleAllowed("moderator", ["moderator"], "mod", "yayinci"));
        Assert.True(CommandConfigService.IsRoleAllowed("moderator", [], "yayinci", "yayinci")); // Broadcaster always allowed
    }

    [Fact]
    public void IsRoleAllowed_Broadcaster_EnforcedCorrectly()
    {
        Assert.False(CommandConfigService.IsRoleAllowed("broadcaster", [], "izleyici", "yayinci"));
        Assert.False(CommandConfigService.IsRoleAllowed("broadcaster", ["subscriber"], "abone", "yayinci"));
        Assert.False(CommandConfigService.IsRoleAllowed("broadcaster", ["moderator"], "mod", "yayinci"));
        Assert.True(CommandConfigService.IsRoleAllowed("broadcaster", ["broadcaster"], "yayinci", "yayinci"));
        Assert.True(CommandConfigService.IsRoleAllowed("broadcaster", [], "yayinci", "yayinci")); // Match by channel name
    }

    [Fact]
    public void LoadConfiguration_InvalidJson_PreservesPreviousConfiguration()
    {
        using var svc = new CommandConfigService(NullLogger<CommandConfigService>.Instance, _configuration);
        var configPath = Path.Combine(_tempDir, "commands.json");

        var initialCommandsCount = svc.Current.Commands.Count;
        Assert.True(initialCommandsCount > 0);

        // Corrupt file with invalid JSON
        File.WriteAllText(configPath, "{ this is not valid json !!! }");

        var success = svc.LoadConfiguration(logSuccess: false);

        Assert.False(success);
        Assert.Equal(initialCommandsCount, svc.Current.Commands.Count);
    }

    [Fact]
    public async Task ReplyRemovedByOlderEditorStaysSilentAfterReload()
    {
        using var service = new CommandConfigService(NullLogger<CommandConfigService>.Instance, _configuration);
        var config = CommandsConfiguration.CreateDefault(); config.Commands["skip"].Templates.Remove("success");
        Assert.True(await service.SaveConfigurationAsync(config)); Assert.True(service.LoadConfiguration(false));
        Assert.Equal("", service.Current.Commands["skip"].GetTemplate("success", "must not resurrect"));
    }
}
