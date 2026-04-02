namespace MusicBot.Core.Models;

public class BotCommand
{
    public string Type { get; set; } = string.Empty; // play, skip, revoke, bump
    public string Query { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
}
