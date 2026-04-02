using MusicBot.Core.Models;

namespace MusicBot.Core.Interfaces;

public interface IQueueService
{
    QueueItem AddSong(Song song, string requestedBy, string platform, bool bypassUserLimit = false, bool isPlaylistItem = false);
    /// <summary>Restores saved state directly, bypassing validation. Call once on startup.</summary>
    void Seed(QueueItem? current, IEnumerable<QueueItem> upcoming);
    QueueItem? Skip();
    QueueItem? Revoke(string requestedBy);
    bool Bump(string requestedBy);
    bool BumpToFront(string requestedBy);
    bool InterruptForUser(string requestedBy);
    bool RemoveByUri(string spotifyUri);
    QueueItem? Advance();
    void UpdateProgress(int progressMs, bool isPlaying, Song? spotifyTrack = null);
    NowPlayingState GetNowPlaying();
    QueueItem? GetCurrentItem();
    List<QueueItem> GetUpcoming();
    QueueState GetState();
    int QueueLength { get; }

    void UpdateLimits(int maxQueueSize, int maxSongsPerUser);
    bool MoveUp(string spotifyUri);
    bool MoveDown(string spotifyUri);
    /// <summary>Inserts song as the current track, pushing the current song back to position 0 of upcoming.</summary>
    QueueItem PlayNow(Song song, string requestedBy, string platform);
    /// <summary>Moves a queued song to a specific 0-based index in the upcoming list.</summary>
    bool Reorder(string spotifyUri, int toIndex);

    /// <summary>Sets the cyclic background playlist. Plays when Upcoming is empty; loops forever.</summary>
    void SetBackgroundPlaylist(IEnumerable<Song> songs);
    void ClearBackgroundPlaylist();
    (List<Song> Songs, int Index) GetBackgroundPlaylist();

    event Action<QueueState>? OnQueueUpdated;
    event Action<NowPlayingState>? OnNowPlayingUpdated;
    event Action<QueueItem>? OnSongAdded;
    event Action<QueueItem>? OnSongRemoved;
}
