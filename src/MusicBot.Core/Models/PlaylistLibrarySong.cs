namespace MusicBot.Core.Models;

public class PlaylistLibrarySong
{
    public int    Id         { get; set; }
    public int    PlaylistId { get; set; }
    public string SpotifyUri { get; set; } = "";
    public string Title      { get; set; } = "";
    public string Artist     { get; set; } = "";
    public string CoverUrl   { get; set; } = "";
    public int    DurationMs { get; set; }
    public int    Position   { get; set; }

    public PlaylistLibrary? Playlist { get; set; }
}
