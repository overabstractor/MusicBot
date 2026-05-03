namespace MusicBot.Core.Models;

public class QueueItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public Song Song { get; set; } = new();
    public string RequestedBy { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public long AddedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// <summary>True when this item was auto-promoted from the background playlist (not user-requested).</summary>
    public bool IsPlaylistItem { get; set; }
    /// <summary>Set when the song cannot be downloaded and no alternative was found. Null when healthy.</summary>
    public string? DownloadError { get; set; }
}
