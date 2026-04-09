using System.IO;
using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Data;

namespace MusicBot.Services;

/// <summary>
/// Persists the queue to SQLite so it survives app restarts.
/// – On startup: loads saved items and seeds the local user's queue,
///   and restores the active background playlist.
/// – While running: saves the queue to DB whenever it changes.
/// </summary>
public class QueuePersistenceService : IHostedService
{
    private readonly UserContextManager  _userContext;
    private readonly ILocalLibraryService _library;
    private readonly PlaylistLibraryService _playlists;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QueuePersistenceService> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public QueuePersistenceService(
        UserContextManager userContext,
        ILocalLibraryService library,
        PlaylistLibraryService playlists,
        IServiceScopeFactory scopeFactory,
        ILogger<QueuePersistenceService> logger)
    {
        _userContext  = userContext;
        _library      = library;
        _playlists    = playlists;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _userContext.OnUserCreated += SubscribeToUser;

        // Ensure the local user's services exist, then restore
        var services = _userContext.GetOrCreate(LocalUser.Id);
        _ = Task.Run(() => RestoreAsync(services), ct);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    // ── Subscribe ────────────────────────────────────────────────────────────

    private void SubscribeToUser(Guid userId, UserServices services)
    {
        services.Queue.OnQueueUpdated += _ => { Task.Run(() => SaveAsync(services)); };
    }

    // ── Restore ──────────────────────────────────────────────────────────────

    private async Task RestoreAsync(UserServices services)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            // ── 1. Read persisted queue items FIRST, before any OnQueueUpdated
            //       fires (SetBackgroundPlaylist below triggers SaveAsync which
            //       would wipe the DB rows before we get to read them).
            var rows = await db.PersistedQueueItems
                .Where(p => p.UserId == LocalUser.Id)
                .OrderBy(p => p.Position)
                .ToListAsync();

            // ── 2. Restore the active background playlist ────────────────────
            var activePlaylist = (await _playlists.GetAllAsync()).FirstOrDefault(p => p.IsActive);
            if (activePlaylist != null)
            {
                var dbSongs = await _playlists.GetSongsAsync(activePlaylist.Id);
                var songs   = await MapToSongsAsync(dbSongs);
                services.Queue.SetBackgroundPlaylist(songs, activePlaylist.Name);
                _logger.LogInformation(
                    "Playlist activa restaurada: \"{Name}\" ({Count} canciones)",
                    activePlaylist.Name, songs.Count);
            }

            // ── 3. Seed the explicit queue items ─────────────────────────────
            if (rows.Count == 0) return;

            var currentRow   = rows.FirstOrDefault(r => r.Position == 0);
            var upcomingRows = rows.Where(r => r.Position > 0).ToList();

            QueueItem? current = currentRow != null
                ? await ToQueueItemAsync(currentRow)
                : null;

            var upcoming = new List<QueueItem>();
            foreach (var row in upcomingRows)
                upcoming.Add(await ToQueueItemAsync(row));

            services.Queue.Seed(current, upcoming);

            _logger.LogInformation(
                "Cola restaurada: {Current} actual + {Count} en espera",
                current != null ? $"\"{current.Song.Title}\"" : "ninguna",
                upcoming.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restaurando la cola");
        }
    }

    private async Task<List<Song>> MapToSongsAsync(List<PlaylistLibrarySong> dbSongs)
    {
        var songs = new List<Song>(dbSongs.Count);
        foreach (var s in dbSongs)
        {
            var song = new Song
            {
                SpotifyUri = s.SpotifyUri,
                Title      = s.Title,
                Artist     = s.Artist,
                CoverUrl   = s.CoverUrl,
                DurationMs = s.DurationMs,
            };
            var cached = await _library.FindByTrackIdAsync(s.SpotifyUri);
            if (cached?.FilePath != null && File.Exists(cached.FilePath))
                song.LocalFilePath = cached.FilePath;
            songs.Add(song);
        }
        return songs;
    }

    private async Task<QueueItem> ToQueueItemAsync(PersistedQueueItem row)
    {
        var cached = await _library.FindByTrackIdAsync(row.TrackId);
        string? localPath = cached?.FilePath != null && File.Exists(cached.FilePath)
            ? cached.FilePath
            : null;

        return new QueueItem
        {
            Id             = row.Id.ToString(),
            RequestedBy    = row.RequestedBy,
            Platform       = row.Platform,
            AddedAt        = row.AddedAt,
            IsPlaylistItem = row.IsPlaylistItem,
            Song           = new Song
            {
                SpotifyUri    = row.TrackId,
                Title         = row.Title,
                Artist        = row.Artist,
                CoverUrl      = row.CoverUrl,
                DurationMs    = row.DurationMs,
                LocalFilePath = localPath
            }
        };
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    private async Task SaveAsync(UserServices services)
    {
        await _saveLock.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            // Use GetUpcoming() (real items only) — NOT GetState().Upcoming which
            // includes synthesised playlist-preview items that must not be persisted.
            var nowPlaying = services.Queue.GetCurrentItem();
            var upcoming   = services.Queue.GetUpcoming();

            // Replace all rows for this user
            var existing = await db.PersistedQueueItems
                .Where(p => p.UserId == LocalUser.Id)
                .ToListAsync();
            db.PersistedQueueItems.RemoveRange(existing);

            if (nowPlaying != null)
                db.PersistedQueueItems.Add(ToRow(nowPlaying, 0));

            for (int i = 0; i < upcoming.Count; i++)
                db.PersistedQueueItems.Add(ToRow(upcoming[i], i + 1));

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error guardando la cola");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static PersistedQueueItem ToRow(QueueItem item, int position) => new()
    {
        Id             = Guid.TryParse(item.Id, out var g) ? g : Guid.NewGuid(),
        UserId         = LocalUser.Id,
        Position       = position,
        TrackId        = item.Song.SpotifyUri,
        Title          = item.Song.Title,
        Artist         = item.Song.Artist,
        CoverUrl       = item.Song.CoverUrl,
        DurationMs     = item.Song.DurationMs,
        RequestedBy    = item.RequestedBy,
        Platform       = item.Platform,
        AddedAt        = item.AddedAt,
        IsPlaylistItem = item.IsPlaylistItem
    };
}
