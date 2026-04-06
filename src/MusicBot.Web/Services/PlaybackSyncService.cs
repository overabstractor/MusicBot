using System.IO;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Data;
using MusicBot.Hubs;
using MusicBot.Services.Downloader;

namespace MusicBot.Services;

/// <summary>
/// Bridges the local audio player and the queue:
/// – Subscribes to each user's ILocalPlayerService events.
/// – Updates queue progress every 500 ms from player state.
/// – Auto-advances the queue when a track ends.
/// – Exposes StartCurrentTrackAsync for CommandRouterService to trigger playback.
/// </summary>
public class PlaybackSyncService : BackgroundService
{
    private readonly UserContextManager _userContext;
    private readonly YtDlpDownloaderService _downloader;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KickVoteService _kickVote;
    private readonly PresenceCheckService _presence;
    private readonly ILogger<PlaybackSyncService> _logger;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly ILocalLibraryService _library;
    private readonly AutoQueueService _autoQueue;
    private readonly QueueSettingsService _queueSettings;

    // Tracks in-progress downloads so new SignalR clients can receive current state on connect
    public record ActiveDownload(string SpotifyUri, string Title, string Artist, int Pct);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ActiveDownload> _activeDownloads = new();
    public IReadOnlyCollection<ActiveDownload> GetActiveDownloads() => _activeDownloads.Values.ToList();

    public PlaybackSyncService(
        UserContextManager userContext,
        YtDlpDownloaderService downloader,
        IServiceScopeFactory scopeFactory,
        KickVoteService kickVote,
        PresenceCheckService presence,
        ILogger<PlaybackSyncService> logger,
        IHubContext<OverlayHub> hub,
        ILocalLibraryService library,
        AutoQueueService autoQueue,
        QueueSettingsService queueSettings)
    {
        _userContext   = userContext;
        _downloader    = downloader;
        _scopeFactory  = scopeFactory;
        _kickVote      = kickVote;
        _presence      = presence;
        _logger        = logger;
        _hub           = hub;
        _library       = library;
        _autoQueue     = autoQueue;
        _queueSettings = queueSettings;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wire skip delegates here to break circular dependencies
        _kickVote.SkipCurrentSong = async () =>
        {
            var services = _userContext.GetOrCreate(LocalUser.Id);
            services.Queue.Skip();
            await StartCurrentTrackAsync(services);
        };

        _presence.SkipCurrentSong = async () =>
        {
            var services = _userContext.GetOrCreate(LocalUser.Id);
            services.Queue.Skip();
            await StartCurrentTrackAsync(services);
        };

        // Broadcast download events to overlay clients and track active state
        _downloader.DownloadStarted += (trackId, title, artist) =>
        {
            _activeDownloads[trackId] = new ActiveDownload(trackId, title, artist, 0);
            _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                    .SendAsync("download:started", new { spotifyUri = trackId, title, artist });
        };

        _downloader.ProgressChanged += (trackId, pct) =>
        {
            _activeDownloads.AddOrUpdate(trackId,
                _ => new ActiveDownload(trackId, "", "", pct),
                (_, existing) => existing with { Pct = pct });
            _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                    .SendAsync("download:progress", new { spotifyUri = trackId, pct });
        };

        _downloader.DownloadCompleted += trackId =>
        {
            _activeDownloads.TryRemove(trackId, out _);
            _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                    .SendAsync("download:done", new { spotifyUri = trackId });
        };

        _downloader.DownloadFailed += (trackId, error) =>
        {
            _activeDownloads.TryRemove(trackId, out _);
            _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                    .SendAsync("download:error", new { spotifyUri = trackId, error });
        };

        _userContext.OnUserCreated += SubscribeToUser;

        foreach (var (_, services) in _userContext.GetAllActive())
            SubscribeToUser(services.UserId, services);

        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        foreach (var (_, services) in _userContext.GetAllActive())
        {
            try { await services.Player.StopAsync(); }
            catch { /* ignore errors on shutdown */ }
        }
        await base.StopAsync(ct);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by CommandRouterService after a play/skip command.
    /// Plays the current queue item, waiting for download if needed.
    /// </summary>
    public async Task StartCurrentTrackAsync(UserServices services)
    {
        var current = services.Queue.GetCurrentItem();
        if (current == null)
        {
            _presence.CancelCheck();
            await services.Player.StopAsync();
            return;
        }

        _presence.CancelCheck();
        try
        {
            await EnsureLocalFileAsync(current.Song);
            await services.Player.PlayAsync(current.Song.LocalFilePath!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playback failed for \"{Title}\" — removing from queue", current.Song.Title);
            // Delete corrupt/unplayable file and evict the cached download task so it re-downloads next time
            _downloader.InvalidateCachedDownload(current.Song.SpotifyUri);
            if (current.Song.LocalFilePath != null && File.Exists(current.Song.LocalFilePath))
            {
                try { File.Delete(current.Song.LocalFilePath); } catch { }
                await _library.DeleteByTrackIdAsync(current.Song.SpotifyUri);
            }
            services.Queue.Skip();
            _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                    .SendAsync("queue:download-failed", new { title = current.Song.Title, artist = current.Song.Artist, reason = ex.Message });
            await StartCurrentTrackAsync(services);
            return;
        }

        _logger.LogInformation("Playing \"{Title}\" by {Artist} for {UserId}",
            current.Song.Title, current.Song.Artist, services.UserId);

        // Start kick-vote and presence check for this track
        // (skip presence check for songs added directly from the dashboard)
        _ = Task.Run(() => _kickVote.StartVoteAsync(current.Song));
        if (current.Platform != "web")
            _ = Task.Run(() => _presence.StartSongCheckAsync(current.RequestedBy));
    }

    // ── Player event subscriptions ────────────────────────────────────────────

    private void SubscribeToUser(Guid userId, UserServices services)
    {
        services.Player.OnStateChanged += state =>
        {
            services.Queue.UpdateProgress(state.PositionMs, state.IsPlaying);

            // 30 s remaining: warn the next requester and pre-warm the next song's download
            if (state.IsPlaying && state.DurationMs > 0)
            {
                var remaining = state.DurationMs - state.PositionMs;
                if (remaining <= 30_000)
                {
                    var upcoming = services.Queue.GetUpcoming();
                    var next = upcoming.FirstOrDefault();
                    if (next != null)
                    {
                        _presence.IssueWarningForNext(next.RequestedBy);
                        _ = Task.Run(() => PrewarmDownloadAsync(next.Song));
                    }
                    else
                    {
                        // No user requests; pre-warm the next background playlist song
                        var bgNext = PeekNextBackgroundSong(services);
                        if (bgNext != null)
                            _ = Task.Run(() => PrewarmDownloadAsync(bgNext));
                    }
                }
            }
        };

        services.Player.OnTrackEnded += () =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var current = services.Queue.GetCurrentItem();
                    if (current != null)
                        await RecordHistoryAsync(current);

                    var next = services.Queue.Advance();
                    if (next != null)
                    {
                        _logger.LogInformation("Auto-advancing to \"{Title}\" for {UserId}",
                            next.Song.Title, userId);
                        _presence.CancelCheck();
                        try
                        {
                            await EnsureLocalFileAsync(next.Song);
                        }
                        catch (Exception dlEx)
                        {
                            _logger.LogError(dlEx, "Playback failed for \"{Title}\" during auto-advance — skipping", next.Song.Title);
                            _downloader.InvalidateCachedDownload(next.Song.SpotifyUri);
                            if (next.Song.LocalFilePath != null && File.Exists(next.Song.LocalFilePath))
                            {
                                try { File.Delete(next.Song.LocalFilePath); } catch { }
                                await _library.DeleteByTrackIdAsync(next.Song.SpotifyUri);
                            }
                            services.Queue.Skip();
                            _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                                    .SendAsync("queue:download-failed", new { title = next.Song.Title, artist = next.Song.Artist, reason = dlEx.Message });
                            await StartCurrentTrackAsync(services);
                            return;
                        }
                        // Delete the previous song's file if in temp mode.
                        // Background-playlist items are never deleted — they'll be needed on the next cycle.
                        if (!_queueSettings.SaveDownloads
                            && current?.Song.LocalFilePath != null
                            && current.IsPlaylistItem == false)
                        {
                            _downloader.InvalidateCachedDownload(current.Song.SpotifyUri);
                            await _library.DeleteByTrackIdAsync(current.Song.SpotifyUri);
                        }
                        await services.Player.PlayAsync(next.Song.LocalFilePath!);
                        _ = Task.Run(() => _kickVote.StartVoteAsync(next.Song));
                        if (next.Platform != "web")
                            _ = Task.Run(() => _presence.StartSongCheckAsync(next.RequestedBy));
                    }
                    else
                    {
                        // Queue is empty; background playlist handles itself via Advance().
                        // Only delete file for non-playlist items in temp mode.
                        if (!_queueSettings.SaveDownloads
                            && current?.Song.LocalFilePath != null
                            && current.IsPlaylistItem == false)
                        {
                            _downloader.InvalidateCachedDownload(current.Song.SpotifyUri);
                            await _library.DeleteByTrackIdAsync(current.Song.SpotifyUri);
                        }

                        await TryStartAutoQueueAsync(services);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during auto-advance for {UserId}", userId);
                }
            });
        };
    }

    // ── Auto-queue ────────────────────────────────────────────────────────────

    /// <summary>
    /// Picks a random song from the auto-queue pool and starts playback.
    /// Retries up to <paramref name="attemptsLeft"/> times on download failure.
    /// Songs that are permanently unavailable (private/removed) are deleted from the pool.
    /// </summary>
    public async Task<bool> TryStartAutoQueueAsync(UserServices services, int attemptsLeft = 3)
    {
        if (!_queueSettings.AutoQueueEnabled || attemptsLeft <= 0)
        {
            await services.Player.StopAsync();
            return false;
        }

        var autoSong = await _autoQueue.GetRandomSongAsync();
        if (autoSong == null)
        {
            await services.Player.StopAsync();
            return false;
        }

        _logger.LogInformation("Auto-queue: starting \"{Title}\"", autoSong.Title);

        var cached = await _library.FindByTrackIdAsync(autoSong.SpotifyUri);
        if (cached != null && File.Exists(cached.FilePath) && new FileInfo(cached.FilePath).Length > 100_000)
            autoSong.LocalFilePath = cached.FilePath;

        services.Queue.AddSong(autoSong, "Auto", "web");
        services.Queue.Advance();

        if (autoSong.LocalFilePath == null)
        {
            try
            {
                autoSong.LocalFilePath = await _downloader.GetOrStartDownloadAsync(autoSong);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-queue download failed for \"{Title}\"", autoSong.Title);

                // Remove permanently unavailable videos so they never block the pool again
                if (IsUnrecoverableDownloadError(ex))
                {
                    _logger.LogWarning(
                        "Removing permanently unavailable auto-queue song \"{Title}\" ({Uri})",
                        autoSong.Title, autoSong.SpotifyUri);
                    await _autoQueue.RemoveAsync(autoSong.SpotifyUri);
                }

                services.Queue.Skip();
                _ = _hub.Clients.Group($"user:{LocalUser.Id}")
                        .SendAsync("queue:download-failed", new { title = autoSong.Title, artist = autoSong.Artist, reason = ex.Message });

                // Try the next random song instead of stopping entirely
                return await TryStartAutoQueueAsync(services, attemptsLeft - 1);
            }
        }

        await services.Player.PlayAsync(autoSong.LocalFilePath);
        return true;
    }

    private static bool IsUnrecoverableDownloadError(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("Private video")
            || msg.Contains("Video unavailable")
            || msg.Contains("This video is not available")
            || msg.Contains("has been removed")
            || msg.Contains("account associated with this video has been terminated");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a background download so the file is ready before the current track ends.
    /// GetOrStartDownloadAsync deduplicates concurrent calls, so this is safe to call repeatedly.
    /// </summary>
    private async Task PrewarmDownloadAsync(Song song)
    {
        if (song.LocalFilePath != null && File.Exists(song.LocalFilePath)
            && new FileInfo(song.LocalFilePath).Length > 100_000)
            return;
        try
        {
            await _downloader.GetOrStartDownloadAsync(song);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-warm download failed for \"{Title}\" — will retry at track end", song.Title);
        }
    }

    /// <summary>
    /// Returns the next song from the background playlist without advancing the index.
    /// Returns null if there are user requests pending (they take priority) or no background playlist.
    /// </summary>
    private static Song? PeekNextBackgroundSong(UserServices services)
    {
        if (services.Queue.GetUpcoming().Count > 0) return null;
        var (songs, index) = services.Queue.GetBackgroundPlaylist();
        if (songs.Count == 0) return null;
        return songs[index % songs.Count];
    }

    private async Task RecordHistoryAsync(QueueItem item)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            db.PlayedSongs.Add(new PlayedSong
            {
                TrackId     = item.Song.SpotifyUri ?? "",
                Title       = item.Song.Title,
                Artist      = item.Song.Artist,
                CoverUrl    = item.Song.CoverUrl,
                DurationMs  = item.Song.DurationMs,
                RequestedBy = item.RequestedBy,
                Platform    = item.Platform,
                PlayedAt    = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording history");
        }
    }

    /// <summary>Ensures song.LocalFilePath is set, waiting for an in-progress download if necessary.</summary>
    private async Task EnsureLocalFileAsync(Song song)
    {
        if (song.LocalFilePath != null && File.Exists(song.LocalFilePath)
            && new FileInfo(song.LocalFilePath).Length > 100_000)
            return;

        _logger.LogInformation("Waiting for download of \"{Title}\"...", song.Title);
        song.LocalFilePath = await _downloader.GetOrStartDownloadAsync(song);
    }

    // ── Enriched queue for overlays ───────────────────────────────────────────

    public Task<SpotifyQueueState?> GetEnrichedQueueAsync(UserServices services)
    {
        var state    = services.Queue.GetState();
        var upcoming = state.Upcoming.Select(i => new Song
        {
            SpotifyUri  = i.Song.SpotifyUri,
            Title       = i.Song.Title,
            Artist      = i.Song.Artist,
            CoverUrl    = i.Song.CoverUrl,
            DurationMs  = i.Song.DurationMs,
            RequestedBy = i.RequestedBy,
            Platform    = i.Platform,
        }).ToList();

        var current = state.NowPlaying.Item;
        var result  = new SpotifyQueueState
        {
            CurrentlyPlaying = current == null ? null : new Song
            {
                SpotifyUri  = current.Song.SpotifyUri,
                Title       = current.Song.Title,
                Artist      = current.Song.Artist,
                CoverUrl    = current.Song.CoverUrl,
                DurationMs  = current.Song.DurationMs,
                RequestedBy = current.RequestedBy,
                Platform    = current.Platform,
            },
            Queue = upcoming,
        };
        return Task.FromResult<SpotifyQueueState?>(result);
    }
}
