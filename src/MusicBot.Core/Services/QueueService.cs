using System.IO;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;

namespace MusicBot.Core.Services;

public class QueueService : IQueueService
{
    private readonly List<QueueItem> _upcoming = new();
    private readonly object _lock = new();
    private QueueItem? _currentItem;
    private int _progressMs;
    private bool _isPlaying;
    private Song? _spotifyTrack;
    private int _maxSize;
    private int _maxPerUser;

    // Background playlist (cyclic, plays when _upcoming is empty)
    private List<Song> _backgroundPlaylist = new();
    private int _playlistIndex = 0;
    private string? _backgroundPlaylistName;

    public event Action<QueueState>? OnQueueUpdated;
    public event Action<NowPlayingState>? OnNowPlayingUpdated;
    public event Action<QueueItem>? OnSongAdded;
    public event Action<QueueItem>? OnSongRemoved;

    public int QueueLength
    {
        get { lock (_lock) return _upcoming.Count; }
    }

    public QueueService(int maxSize = 50, int maxPerUser = 3)
    {
        _maxSize = maxSize;
        _maxPerUser = maxPerUser;
    }

    public void UpdateLimits(int maxQueueSize, int maxSongsPerUser)
    {
        lock (_lock) { _maxSize = maxQueueSize; _maxPerUser = maxSongsPerUser; }
    }

    public bool MoveUp(string spotifyUri)
    {
        lock (_lock)
        {
            var idx = _upcoming.FindIndex(i => i.Song.SpotifyUri == spotifyUri);
            if (idx <= 0) return false;
            (_upcoming[idx], _upcoming[idx - 1]) = (_upcoming[idx - 1], _upcoming[idx]);
        }
        OnQueueUpdated?.Invoke(GetState());
        return true;
    }

    public bool MoveDown(string spotifyUri)
    {
        lock (_lock)
        {
            var idx = _upcoming.FindIndex(i => i.Song.SpotifyUri == spotifyUri);
            if (idx < 0 || idx >= _upcoming.Count - 1) return false;
            (_upcoming[idx], _upcoming[idx + 1]) = (_upcoming[idx + 1], _upcoming[idx]);
        }
        OnQueueUpdated?.Invoke(GetState());
        return true;
    }

    public void Seed(QueueItem? current, IEnumerable<QueueItem> upcoming)
    {
        lock (_lock)
        {
            _currentItem = current;
            _upcoming.Clear();
            _upcoming.AddRange(upcoming);
            _progressMs  = 0;
            _isPlaying   = false;
        }
        // Notify UI of restored state
        OnQueueUpdated?.Invoke(GetState());
    }

    public QueueItem AddSong(Song song, string requestedBy, string platform, bool bypassUserLimit = false, bool isPlaylistItem = false)
    {
        lock (_lock)
        {
            // Duplicate check: already the current track
            if (_currentItem?.Song.SpotifyUri == song.SpotifyUri)
                throw new InvalidOperationException("Esta canción ya está sonando actualmente");

            // Duplicate check: already in upcoming queue
            var existingIndex = _upcoming.FindIndex(i => i.Song.SpotifyUri == song.SpotifyUri);
            if (existingIndex >= 0)
            {
                var existing = _upcoming[existingIndex];
                throw new InvalidOperationException(
                    $"Esta canción ya está en la cola (posición {existingIndex + 1}, solicitada por {existing.RequestedBy})");
            }

            if (_upcoming.Count >= _maxSize)
                throw new InvalidOperationException($"La cola está llena (máx. {_maxSize} canciones)");

            if (!bypassUserLimit)
            {
                var userSongs = _upcoming.Count(i =>
                    i.RequestedBy.Equals(requestedBy, StringComparison.OrdinalIgnoreCase));
                if (userSongs >= _maxPerUser)
                    throw new InvalidOperationException($"Ya tienes {_maxPerUser} canciones en la cola");
            }

            var item = new QueueItem
            {
                Song = song,
                RequestedBy = requestedBy,
                Platform = platform,
                IsPlaylistItem = isPlaylistItem,
            };

            if (!isPlaylistItem)
            {
                // User requests jump ahead of any playlist-origin items already queued
                var firstPlaylistIdx = _upcoming.FindIndex(i => i.IsPlaylistItem);
                if (firstPlaylistIdx >= 0)
                    _upcoming.Insert(firstPlaylistIdx, item);
                else
                    _upcoming.Add(item);
            }
            else
            {
                _upcoming.Add(item);
            }

            OnSongAdded?.Invoke(item);
            EmitUpdate();
            return item;
        }
    }

    public bool BumpToFront(string requestedBy)
    {
        lock (_lock)
        {
            var index = _upcoming.FindIndex(i =>
                i.RequestedBy.Equals(requestedBy, StringComparison.OrdinalIgnoreCase));

            if (index == -1) return false;
            if (index == 0)  return true; // already at front

            var item = _upcoming[index];
            _upcoming.RemoveAt(index);
            _upcoming.Insert(0, item);
            EmitUpdate();
            return true;
        }
    }

    /// <summary>
    /// Makes the donor's queued song the current track, pushing the interrupted song
    /// back to position 0 in the upcoming queue so it plays next.
    /// </summary>
    public bool InterruptForUser(string requestedBy)
    {
        lock (_lock)
        {
            var index = _upcoming.FindIndex(i =>
                i.RequestedBy.Equals(requestedBy, StringComparison.OrdinalIgnoreCase));

            if (index == -1) return false;

            var userItem = _upcoming[index];
            _upcoming.RemoveAt(index);

            if (_currentItem != null)
                _upcoming.Insert(0, _currentItem);

            _currentItem = userItem;
            _progressMs  = 0;
            _isPlaying   = true;
            EmitUpdate();
            return true;
        }
    }

    public bool RemoveByUri(string spotifyUri)
    {
        lock (_lock)
        {
            var index = _upcoming.FindIndex(i => i.Song.SpotifyUri == spotifyUri);
            if (index == -1) return false;

            var removed = _upcoming[index];
            _upcoming.RemoveAt(index);
            OnSongRemoved?.Invoke(removed);
            EmitUpdate();
            return true;
        }
    }

    public QueueItem? Skip()
    {
        lock (_lock)
        {
            var skipped = _currentItem;
            Advance();
            return skipped;
        }
    }

    public QueueItem? Revoke(string requestedBy)
    {
        lock (_lock)
        {
            var index = _upcoming.FindIndex(i =>
                i.RequestedBy.Equals(requestedBy, StringComparison.OrdinalIgnoreCase));

            if (index == -1) return null;

            var removed = _upcoming[index];
            _upcoming.RemoveAt(index);
            OnSongRemoved?.Invoke(removed);
            EmitUpdate();
            return removed;
        }
    }

    public bool Bump(string requestedBy)
    {
        lock (_lock)
        {
            var index = _upcoming.FindIndex(i =>
                i.RequestedBy.Equals(requestedBy, StringComparison.OrdinalIgnoreCase));

            if (index <= 0) return false;

            var item = _upcoming[index];
            _upcoming.RemoveAt(index);
            _upcoming.Insert(index - 1, item);
            EmitUpdate();
            return true;
        }
    }

    public QueueItem PlayNow(Song song, string requestedBy, string platform, bool isPlaylistItem = false)
    {
        lock (_lock)
        {
            // Push current track back to front so it plays next
            if (_currentItem != null)
                _upcoming.Insert(0, _currentItem);

            var item = new QueueItem { Song = song, RequestedBy = requestedBy, Platform = platform, IsPlaylistItem = isPlaylistItem };
            _currentItem = item;
            _progressMs  = 0;
            _isPlaying   = true;
            EmitUpdate();
            return item;
        }
    }

    public bool Reorder(string spotifyUri, int toIndex)
    {
        lock (_lock)
        {
            var fromIndex = _upcoming.FindIndex(i => i.Song.SpotifyUri == spotifyUri);
            if (fromIndex >= 0)
            {
                toIndex = Math.Clamp(toIndex, 0, _upcoming.Count - 1);
                if (fromIndex == toIndex) return true;

                var item = _upcoming[fromIndex];
                _upcoming.RemoveAt(fromIndex);
                _upcoming.Insert(toIndex, item);
                EmitUpdate();
                return true;
            }

            if (_backgroundPlaylist.Count == 0) return false;

            var hiddenUris = new HashSet<string>(_upcoming.Select(i => i.Song.SpotifyUri));
            if (_currentItem != null) hiddenUris.Add(_currentItem.Song.SpotifyUri);

            var visiblePlaylist = new List<Song>();
            var hiddenPlaylist = new List<Song>();
            for (var i = 0; i < _backgroundPlaylist.Count; i++)
            {
                var song = _backgroundPlaylist[(_playlistIndex + i) % _backgroundPlaylist.Count];
                if (hiddenUris.Contains(song.SpotifyUri))
                    hiddenPlaylist.Add(song);
                else
                    visiblePlaylist.Add(song);
            }

            fromIndex = visiblePlaylist.FindIndex(s => s.SpotifyUri == spotifyUri);
            if (fromIndex < 0) return false;

            var playlistToIndex = Math.Clamp(toIndex - _upcoming.Count, 0, visiblePlaylist.Count - 1);
            if (fromIndex == playlistToIndex) return true;

            var moved = visiblePlaylist[fromIndex];
            visiblePlaylist.RemoveAt(fromIndex);
            visiblePlaylist.Insert(playlistToIndex, moved);

            _backgroundPlaylist = visiblePlaylist.Concat(hiddenPlaylist).ToList();
            _playlistIndex = 0;
            EmitUpdate();
            return true;
        }
    }

    public QueueItem? Advance()
    {
        lock (_lock)
        {
            // User requests take priority
            if (_upcoming.Count > 0)
            {
                _currentItem = _upcoming[0];
                _upcoming.RemoveAt(0);
                _progressMs = 0;
                _isPlaying = true;
                EmitUpdate();
                return _currentItem;
            }

            // Fall back to background playlist (cyclic)
            if (_backgroundPlaylist.Count > 0)
            {
                var src = _backgroundPlaylist[_playlistIndex];
                _playlistIndex = (_playlistIndex + 1) % _backgroundPlaylist.Count;

                _currentItem = new QueueItem
                {
                    Song = new Song
                    {
                        SpotifyUri    = src.SpotifyUri,
                        Title         = src.Title,
                        Artist        = src.Artist,
                        CoverUrl      = src.CoverUrl,
                        DurationMs    = src.DurationMs,
                        LocalFilePath = src.LocalFilePath,
                    },
                    RequestedBy    = "Playlist",
                    Platform       = "web",
                    IsPlaylistItem = true,
                };
                _progressMs = 0;
                _isPlaying  = true;
                EmitUpdate();
                return _currentItem;
            }

            _currentItem = null;
            _progressMs = 0;
            _isPlaying = false;
            EmitUpdate();
            return null;
        }
    }

    public void Shuffle()
    {
        lock (_lock)
        {
            var rng = new Random();
            for (int i = _upcoming.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (_upcoming[i], _upcoming[j]) = (_upcoming[j], _upcoming[i]);
            }
        }
        OnQueueUpdated?.Invoke(GetState());
    }

    public void ShuffleBackgroundPlaylist()
    {
        lock (_lock)
        {
            if (_backgroundPlaylist.Count <= 1) return;
            var rng = new Random();
            for (int i = _backgroundPlaylist.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (_backgroundPlaylist[i], _backgroundPlaylist[j]) = (_backgroundPlaylist[j], _backgroundPlaylist[i]);
            }
            _playlistIndex = 0;
        }
        OnQueueUpdated?.Invoke(GetState());
    }

    public bool PromoteFromBackground(string spotifyUri, int? toIndex = null)
    {
        lock (_lock)
        {
            var bgIdx = _backgroundPlaylist.FindIndex(s => s.SpotifyUri == spotifyUri);
            if (bgIdx < 0) return false;

            if (_currentItem?.Song.SpotifyUri == spotifyUri) return false;
            if (_upcoming.Any(i => i.Song.SpotifyUri == spotifyUri)) return false;

            var src = _backgroundPlaylist[bgIdx];
            var item = new QueueItem
            {
                Song = new Song
                {
                    SpotifyUri    = src.SpotifyUri,
                    Title         = src.Title,
                    Artist        = src.Artist,
                    CoverUrl      = src.CoverUrl,
                    DurationMs    = src.DurationMs,
                    LocalFilePath = src.LocalFilePath,
                },
                RequestedBy    = "LocalUser",
                Platform       = "web",
                IsPlaylistItem = false,
            };

            // Insert at toIndex (clamped to user-item range) or before first playlist item
            var firstPlaylistIdx = _upcoming.FindIndex(i => i.IsPlaylistItem);
            var userCount = firstPlaylistIdx >= 0 ? firstPlaylistIdx : _upcoming.Count;

            if (toIndex.HasValue)
            {
                var insertAt = Math.Clamp(toIndex.Value, 0, userCount);
                _upcoming.Insert(insertAt, item);
            }
            else if (firstPlaylistIdx >= 0)
            {
                _upcoming.Insert(firstPlaylistIdx, item);
            }
            else
            {
                _upcoming.Add(item);
            }

            // Remove from background playlist and fix index
            _backgroundPlaylist.RemoveAt(bgIdx);
            if (_backgroundPlaylist.Count == 0)
                _playlistIndex = 0;
            else if (bgIdx < _playlistIndex)
                _playlistIndex--;
            else if (_playlistIndex >= _backgroundPlaylist.Count)
                _playlistIndex = 0;

            OnSongAdded?.Invoke(item);
            EmitUpdate();
            return true;
        }
    }

    public List<Song> GetNextDownloadCandidates(int count)
    {
        lock (_lock)
        {
            var candidates = new List<Song>();
            var seen = new HashSet<string>();
            if (_currentItem != null) seen.Add(_currentItem.Song.SpotifyUri);

            // First: user items
            foreach (var item in _upcoming)
            {
                if (seen.Contains(item.Song.SpotifyUri)) continue;
                seen.Add(item.Song.SpotifyUri);
                if (!IsDownloaded(item.Song))
                    candidates.Add(item.Song);
                if (candidates.Count >= count) return candidates;
            }

            // Then: next bg playlist songs
            int bgCount = _backgroundPlaylist.Count;
            for (int i = 0; i < bgCount && candidates.Count < count; i++)
            {
                var song = _backgroundPlaylist[(_playlistIndex + i) % bgCount];
                if (seen.Contains(song.SpotifyUri)) continue;
                seen.Add(song.SpotifyUri);
                if (!IsDownloaded(song))
                    candidates.Add(song);
            }

            return candidates;
        }
    }

    private static bool IsDownloaded(Song song) =>
        song.LocalFilePath != null &&
        File.Exists(song.LocalFilePath) &&
        new FileInfo(song.LocalFilePath).Length > 100_000;

    public void SetBackgroundPlaylist(IEnumerable<Song> songs, string? playlistName = null)
    {
        lock (_lock)
        {
            _backgroundPlaylist = new List<Song>(songs);
            _backgroundPlaylistName = playlistName;
            _playlistIndex = 0;
            // Remove any stale playlist-origin items from the explicit queue so the
            // new playlist's songs take effect immediately without old ones blocking.
            _upcoming.RemoveAll(i => i.IsPlaylistItem);
        }
        OnQueueUpdated?.Invoke(GetState());
    }

    public void ClearUserQueue()
    {
        lock (_lock)
        {
            _upcoming.RemoveAll(i => !i.IsPlaylistItem);
        }
        EmitUpdate();
    }

    public void ClearBackgroundPlaylist()
    {
        lock (_lock)
        {
            _backgroundPlaylist.Clear();
            _backgroundPlaylistName = null;
            _playlistIndex = 0;
        }
        OnQueueUpdated?.Invoke(GetState());
    }

    public (List<Song> Songs, int Index) GetBackgroundPlaylist()
    {
        lock (_lock) return (new List<Song>(_backgroundPlaylist), _playlistIndex);
    }

    public void UpdateProgress(int progressMs, bool isPlaying, Song? spotifyTrack = null)
    {
        lock (_lock)
        {
            _progressMs = progressMs;
            _isPlaying = isPlaying;
            _spotifyTrack = spotifyTrack;
        }
        OnNowPlayingUpdated?.Invoke(GetNowPlaying());
    }

    public NowPlayingState GetNowPlaying()
    {
        lock (_lock)
        {
            return new NowPlayingState
            {
                Item = _currentItem,
                ProgressMs = _progressMs,
                IsPlaying = _isPlaying,
                SpotifyTrack = _spotifyTrack
            };
        }
    }

    public QueueItem? GetCurrentItem()
    {
        lock (_lock) return _currentItem;
    }

    public List<QueueItem> GetUpcoming()
    {
        lock (_lock) return new List<QueueItem>(_upcoming);
    }

    public QueueState GetState()
    {
        lock (_lock)
        {
            // Start with the real upcoming queue (user + any playlist items already in queue)
            var upcoming = new List<QueueItem>(_upcoming);

            // Synthesise visibility of remaining background-playlist songs so the UI
            // can show the full upcoming flow even before songs are downloaded.
            // Cap the total visible list at 50 items.
            if (_backgroundPlaylist.Count > 0 && upcoming.Count < 50)
            {
                var existingUris = new HashSet<string>(upcoming.Select(i => i.Song.SpotifyUri));
                if (_currentItem != null) existingUris.Add(_currentItem.Song.SpotifyUri);

                int count = _backgroundPlaylist.Count;
                for (int i = 0; i < count && upcoming.Count < 50; i++)
                {
                    int idx = (_playlistIndex + i) % count;
                    var src = _backgroundPlaylist[idx];
                    if (existingUris.Contains(src.SpotifyUri)) continue;

                    upcoming.Add(new QueueItem
                    {
                        Song = new Song
                        {
                            SpotifyUri    = src.SpotifyUri,
                            Title         = src.Title,
                            Artist        = src.Artist,
                            CoverUrl      = src.CoverUrl,
                            DurationMs    = src.DurationMs,
                            LocalFilePath = src.LocalFilePath,
                        },
                        RequestedBy    = "Playlist",
                        Platform       = "web",
                        IsPlaylistItem = true,
                    });
                    existingUris.Add(src.SpotifyUri);
                }
            }

            return new QueueState
            {
                NowPlaying           = GetNowPlaying(),
                Upcoming             = upcoming,
                BackgroundPlaylist   = new List<Song>(_backgroundPlaylist),
                PlaylistIndex        = _playlistIndex,
                ActivePlaylistName   = _backgroundPlaylistName,
            };
        }
    }

    public bool MarkDownloadError(string spotifyUri, string? error)
    {
        bool found = false;
        lock (_lock)
        {
            var item = _upcoming.Find(i => i.Song.SpotifyUri == spotifyUri);
            if (item != null) { item.DownloadError = error; found = true; }
            else if (_currentItem?.Song.SpotifyUri == spotifyUri) { _currentItem.DownloadError = error; found = true; }
        }
        if (found) EmitUpdate();
        return found;
    }

    public bool UpdateSongForAlternative(string oldUri, Song newSong)
    {
        bool found = false;
        lock (_lock)
        {
            var item = _upcoming.Find(i => i.Song.SpotifyUri == oldUri);
            if (item != null) { item.Song = newSong; item.DownloadError = null; found = true; }
            else if (_currentItem?.Song.SpotifyUri == oldUri) { _currentItem.Song = newSong; _currentItem.DownloadError = null; found = true; }
        }
        if (found) EmitUpdate();
        return found;
    }

    private void EmitUpdate()
    {
        OnQueueUpdated?.Invoke(GetState());
    }
}
