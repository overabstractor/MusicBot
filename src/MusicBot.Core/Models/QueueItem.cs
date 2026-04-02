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
}
