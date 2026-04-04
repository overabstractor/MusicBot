namespace MusicBot.Core.Models;

public class PlaylistLibrary
{
    public int      Id        { get; set; }
    public string   Name      { get; set; } = "";
    public bool     IsActive  { get; set; }
    /// <summary>True for auto-generated system playlists (e.g. Liked Songs). Cannot be deleted or renamed.</summary>
    public bool     IsSystem  { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Whether this playlist is pinned to the top of the sidebar.</summary>
    public bool     IsPinned  { get; set; }
    /// <summary>Display order among pinned playlists (lower = higher).</summary>
    public int      PinOrder  { get; set; }

    public List<PlaylistLibrarySong> Songs { get; set; } = new();
}
