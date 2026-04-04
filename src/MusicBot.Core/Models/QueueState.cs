namespace MusicBot.Core.Models;

public class NowPlayingState
{
    public QueueItem? Item { get; set; }
    public int ProgressMs { get; set; }
    public bool IsPlaying { get; set; }
    /// <summary>
    /// The track actually playing on Spotify, even if it's not from the queue.
    /// </summary>
    public Song? SpotifyTrack { get; set; }
}

public class QueueState
{
    public NowPlayingState NowPlaying { get; set; } = new();
    /// <summary>User-requested songs (play before the background playlist).</summary>
    public List<QueueItem> Upcoming { get; set; } = new();
    /// <summary>Background playlist songs (cycle when Upcoming is empty).</summary>
    public List<Song> BackgroundPlaylist { get; set; } = new();
    /// <summary>Current position within BackgroundPlaylist (next song to play from it).</summary>
    public int PlaylistIndex { get; set; }
    /// <summary>Display name of the active background playlist, if any.</summary>
    public string? ActivePlaylistName { get; set; }
}
