namespace MusicBot.Core.Models;

public class AutoQueueSong
{
    public int      Id          { get; set; }
    public string   SpotifyUri  { get; set; } = "";
    public string   Title       { get; set; } = "";
    public string   Artist      { get; set; } = "";
    public string   CoverUrl    { get; set; } = "";
    public int      DurationMs  { get; set; }
    public DateTime AddedAt     { get; set; } = DateTime.UtcNow;
}
