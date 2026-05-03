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
    QueueItem PlayNow(Song song, string requestedBy, string platform, bool isPlaylistItem = false);
    /// <summary>Moves a queued song to a specific 0-based index in the upcoming list.</summary>
    bool Reorder(string spotifyUri, int toIndex);

    /// <summary>Sets the cyclic background playlist with an optional display name.</summary>
    void SetBackgroundPlaylist(IEnumerable<Song> songs, string? playlistName = null);
    void ClearBackgroundPlaylist();
    (List<Song> Songs, int Index) GetBackgroundPlaylist();
    void Shuffle();
    /// <summary>Randomizes the in-memory background playlist without touching the persisted library.</summary>
    void ShuffleBackgroundPlaylist();
    /// <summary>Moves a song from the background playlist into the user-requested upcoming queue. Returns false if not found, already present, or currently playing.</summary>
    bool PromoteFromBackground(string spotifyUri, int? toIndex = null);
    /// <summary>Returns the next songs that would be played and are not yet downloaded, for pre-warming.</summary>
    List<Song> GetNextDownloadCandidates(int count);
    /// <summary>Removes all user-requested (non-playlist) items from the upcoming queue.</summary>
    void ClearUserQueue();
    /// <summary>Marks or clears a download error on a queued item by URI. Returns false if not found.</summary>
    bool MarkDownloadError(string spotifyUri, string? error);
    /// <summary>Replaces the song on a queued item (by old URI) for alternative-version fallback. Clears DownloadError. Returns false if not found.</summary>
    bool UpdateSongForAlternative(string oldUri, Song newSong);

    event Action<QueueState>? OnQueueUpdated;
    event Action<NowPlayingState>? OnNowPlayingUpdated;
    event Action<QueueItem>? OnSongAdded;
    event Action<QueueItem>? OnSongRemoved;
}
