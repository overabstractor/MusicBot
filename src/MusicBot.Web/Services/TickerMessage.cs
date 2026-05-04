namespace MusicBot.Services;

public class TickerMessage
{
    public string   Id               { get; set; } = Guid.NewGuid().ToString("N");
    public string   Text             { get; set; } = "";
    public int      IntervalMinutes  { get; set; } = 5;
    public int      MinChatMessages  { get; set; } = 0; // 0 = no minimum required
    public string[] Platforms        { get; set; } = [];
    public bool     Enabled          { get; set; } = true;
    public int      Order            { get; set; } = 0;
}
