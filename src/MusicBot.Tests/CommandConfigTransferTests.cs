using System.Text.Json;
using MusicBot.Core.Models;
using MusicBot.Services;
using Xunit;
namespace MusicBot.Tests;
public class CommandConfigTransferTests
{
    [Fact]
    public void ExportRoundTripPreservesIndependentPermissionsAndCustomCommands()
    {
        var config = CommandsConfiguration.CreateDefault();
        config.Commands["play"].AllowedRoles = ["vip", "everyone"];
        config.CustomCommands["hello"] = new() { Name = "hello", Aliases = ["selam"], ResponseText = "Merhaba {user}", AllowedRoles = [] };
        var json = JsonSerializer.SerializeToElement(new { format = "musick.commands", version = 1, configuration = config });
        var imported = CommandConfigTransfer.Read(json);
        Assert.Equal(config.Commands["play"].AllowedRoles, imported.Commands["play"].AllowedRoles);
        Assert.Empty(imported.CustomCommands["hello"].AllowedRoles!);
        Assert.Equal("Merhaba {user}", imported.CustomCommands["hello"].ResponseText);
        var old = new CommandsConfiguration { Commands = new() { ["play"] = config.Commands["play"] } };
        var upgraded = CommandConfigTransfer.Read(JsonSerializer.SerializeToElement(old));
        Assert.True(upgraded.Commands.Count > 1);
        Assert.Equal(5, upgraded.RoleProfiles.Count);
        CommandConfigTransfer.Read(JsonSerializer.SerializeToElement(config)); // Existing raw commands.json supported.
    }
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"other\",\"version\":1}")]
    [InlineData("{\"format\":\"musick.commands\",\"version\":2}")]
    [InlineData("{\"commands\":{\"play\":null}}")]
    public void MalformedImportIsRejected(string json) => Assert.ThrowsAny<ArgumentException>(() => CommandConfigTransfer.Read(JsonSerializer.Deserialize<JsonElement>(json)));
    [Fact]
    public void InvalidRoleAndNegativeLimitsAreRejected()
    {
        var config = CommandsConfiguration.CreateDefault();
        config.Commands["play"].AllowedRoles = ["typo-role"];
        Assert.Throws<ArgumentException>(() => CommandConfigTransfer.Validate(config));
        config.Commands["play"].AllowedRoles = [];
        config.Commands["play"].MaxPerUser = -1;
        Assert.Throws<ArgumentException>(() => CommandConfigTransfer.Validate(config));
    }
}
