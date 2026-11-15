using MusicBot.Core.Models;
using MusicBot.Core.Services;
using Xunit;

namespace MusicBot.Tests;

public class CommandTemplateEngineTests
{
    [Fact]
    public void Render_ReplacesAllKnownPlaceholders()
    {
        var template = "@{user}, \"{song}\" - {artist} eklendi. (Sıra: #{position}/{queue_length}, Bekleme: ~{wait_time}, İsteyen: @{requester})";
        var context = new TemplateContext
        {
            User = "ahmet",
            Song = "Kırılmış Kalbini",
            Artist = "Duman",
            Position = "3",
            QueueLength = "7",
            WaitTime = "12 dk",
            Requester = "mehmet"
        };

        var result = CommandTemplateEngine.Render(template, context);

        Assert.Equal("@ahmet, \"Kırılmış Kalbini\" - Duman eklendi. (Sıra: #3/7, Bekleme: ~12 dk, İsteyen: @mehmet)", result);
    }

    [Fact]
    public void Render_NullContextProperties_DoesNotThrow()
    {
        var template = "@{user}: {song}";
        var context = new TemplateContext();

        var result = CommandTemplateEngine.Render(template, context);

        Assert.Equal("@: ", result);
    }

    [Fact]
    public void Render_ExceedsKickLimit_TruncatesSafelyTo500Chars()
    {
        // 600 character string
        var longSong = new string('A', 550);
        var template = "@{user}: {song}";
        var context = new TemplateContext
        {
            User = "testuser",
            Song = longSong
        };

        var result = CommandTemplateEngine.Render(template, context);

        Assert.True(result.Length <= 500);
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void TruncateSafely_ShortMessage_ReturnsUnchanged()
    {
        var message = "Kısa bir bot mesajı.";
        var result = CommandTemplateEngine.TruncateSafely(message, 500);

        Assert.Equal(message, result);
    }

    [Fact]
    public void TruncateSafely_LongMessageWithSpaces_BreaksAtWordBoundary()
    {
        var words = string.Join(" ", Enumerable.Repeat("kelime", 120));
        var result = CommandTemplateEngine.TruncateSafely(words, 100);

        Assert.True(result.Length <= 100);
        Assert.EndsWith("...", result);
        // The last character before "..." should not be in the middle of "kelime"
        var beforeEllipsis = result[..^3];
        Assert.Equal("kelime", beforeEllipsis.Split(' ').Last());
    }

    [Fact]
    public void FindUnknownPlaceholders_AllKnown_ReturnsEmpty()
    {
        var config = CommandsConfiguration.CreateDefault();
        var unknown = CommandTemplateEngine.FindUnknownPlaceholders(config);

        Assert.Empty(unknown);
    }

    [Fact]
    public void FindUnknownPlaceholders_UnknownPlaceholder_Detected()
    {
        var config = new CommandsConfiguration
        {
            Commands = new()
            {
                ["test"] = new CommandDefinition
                {
                    Name = "test",
                    Templates = new()
                    {
                        ["success"] = "@{user} {invalid_placeholder} {song}"
                    }
                }
            }
        };

        var unknown = CommandTemplateEngine.FindUnknownPlaceholders(config);

        Assert.Single(unknown);
        Assert.Equal("test", unknown[0].Command);
        Assert.Equal("success", unknown[0].TemplateKey);
        Assert.Equal("{invalid_placeholder}", unknown[0].UnknownPlaceholder);
    }
}
