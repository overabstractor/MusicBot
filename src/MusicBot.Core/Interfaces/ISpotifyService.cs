using MusicBot.Core.Models;

namespace MusicBot.Core.Interfaces;

public interface ISpotifyService
{
    bool IsAuthenticated { get; }
    string GetAuthUrl();
    Task HandleCallbackAsync(string code);
    Task DisconnectAsync();
    Task<string> GetAccessTokenAsync();
    Task<List<Song>> SearchAsync(string query, int limit = 5);
    Task PlayAsync(string spotifyUri, string? deviceId = null);
    Task AddToQueueAsync(string spotifyUri);
    Task SkipAsync();
    Task PauseAsync();
    Task ResumeAsync();
    Task<PlaybackState?> GetPlaybackStateAsync();
    Task<SpotifyQueueState> GetQueueAsync();
    Task<Song?> GetTrackAsync(string trackId);
    Task<List<Song>> GetPlaylistTracksAsync(string playlistId, int maxTracks = 50);
}

public class PlaybackState
{
    public int ProgressMs { get; set; }
    public bool IsPlaying { get; set; }
    public string TrackUri { get; set; } = string.Empty;
    public int DurationMs { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
}

public class SpotifyQueueState
{
    public Song? CurrentlyPlaying { get; set; }
    public List<Song> Queue { get; set; } = new();
}
