namespace MusicBot.Core.Models;

/// <summary>Represents a song that has been downloaded and stored locally.</summary>
public class CachedTrack
{
    public int Id { get; set; }
    /// <summary>Unique track key, e.g. "itunes:1234567890".</summary>
    public string TrackId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public int DurationMs { get; set; }
    /// <summary>Absolute path to the .mp3 file on disk.</summary>
    public string FilePath { get; set; } = "";
    public DateTime DownloadedAt { get; set; } = DateTime.UtcNow;
}
