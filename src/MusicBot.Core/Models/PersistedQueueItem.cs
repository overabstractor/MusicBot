namespace MusicBot.Core.Models;

/// <summary>DB entity that persists the queue across sessions.</summary>
public class PersistedQueueItem
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>0 = currently playing, 1+ = upcoming (in order).</summary>
    public int Position { get; set; }

    // Song metadata (denormalized so we don't need a join on load)
    public string TrackId    { get; set; } = "";
    public string Title      { get; set; } = "";
    public string Artist     { get; set; } = "";
    public string CoverUrl   { get; set; } = "";
    public int    DurationMs { get; set; }

    // Queue metadata
    public string RequestedBy    { get; set; } = "";
    public string Platform       { get; set; } = "";
    public long   AddedAt        { get; set; }
    public bool   IsPlaylistItem { get; set; }
}
