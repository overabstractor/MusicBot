namespace MusicBot.Core.Models;

public class Song
{
    /// <summary>Unique track identifier. Format: "itunes:{trackId}" or "spotify:{trackUri}".</summary>
    public string SpotifyUri { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public int DurationMs { get; set; }
    public string? RequestedBy { get; set; }
    public string? Platform { get; set; }
    /// <summary>Absolute path to the locally cached audio file. Null until downloaded.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LocalFilePath { get; set; }
    public bool IsDownloaded => LocalFilePath != null;

    // ── Playlist search result metadata (only populated for IsPlaylist=true results) ──
    public bool IsPlaylist { get; set; }
    public string? PlaylistUrl { get; set; }
    public int PlaylistVideoCount { get; set; }
}
