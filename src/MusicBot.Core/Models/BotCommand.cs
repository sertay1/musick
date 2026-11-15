namespace MusicBot.Core.Models;

public class BotCommand
{
    public string Type { get; set; } = string.Empty; // play, skip, sira, simdi, sil, etc.
    public string Query { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public List<string> Badges { get; set; } = new();
    public CommandDefinition? Definition { get; set; }
}
