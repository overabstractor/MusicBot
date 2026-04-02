namespace MusicBot.Services;

public class TickerMessage
{
    public string Id          { get; set; } = Guid.NewGuid().ToString("N");
    public string Text        { get; set; } = "";
    public string? ImageUrl   { get; set; }
    public int    DurationSec { get; set; } = 8;
    public bool   Enabled     { get; set; } = true;
    public int    Order       { get; set; } = 0;
}
