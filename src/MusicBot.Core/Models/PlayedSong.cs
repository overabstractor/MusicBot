namespace MusicBot.Core.Models;

public class PlayedSong
{
    public Guid     Id          { get; set; } = Guid.NewGuid();
    public string   TrackId     { get; set; } = string.Empty;
    public string   Title       { get; set; } = string.Empty;
    public string   Artist      { get; set; } = string.Empty;
    public string?  CoverUrl    { get; set; }
    public int      DurationMs  { get; set; }
    public string?  RequestedBy { get; set; }
    public string?  Platform    { get; set; }
    public DateTime PlayedAt    { get; set; } = DateTime.UtcNow;
}
