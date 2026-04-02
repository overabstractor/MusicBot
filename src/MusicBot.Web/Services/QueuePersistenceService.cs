using System.IO;
using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Data;

namespace MusicBot.Services;

/// <summary>
/// Persists the queue to SQLite so it survives app restarts.
/// – On startup: loads saved items and seeds the local user's queue.
/// – While running: saves the queue to DB whenever it changes.
/// </summary>
public class QueuePersistenceService : IHostedService
{
    private readonly UserContextManager  _userContext;
    private readonly ILocalLibraryService _library;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QueuePersistenceService> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public QueuePersistenceService(
        UserContextManager userContext,
        ILocalLibraryService library,
        IServiceScopeFactory scopeFactory,
        ILogger<QueuePersistenceService> logger)
    {
        _userContext  = userContext;
        _library      = library;
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

            var rows = await db.PersistedQueueItems
                .Where(p => p.UserId == LocalUser.Id)
                .OrderBy(p => p.Position)
                .ToListAsync();

            if (rows.Count == 0) return;

            var currentRow  = rows.FirstOrDefault(r => r.Position == 0);
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

    private async Task<QueueItem> ToQueueItemAsync(PersistedQueueItem row)
    {
        var cached = await _library.FindByTrackIdAsync(row.TrackId);
        string? localPath = cached?.FilePath != null && File.Exists(cached.FilePath)
            ? cached.FilePath
            : null;

        return new QueueItem
        {
            Id          = row.Id.ToString(),
            RequestedBy = row.RequestedBy,
            Platform    = row.Platform,
            AddedAt     = row.AddedAt,
            Song        = new Song
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

            var state = services.Queue.GetState();

            // Replace all rows for this user
            var existing = await db.PersistedQueueItems
                .Where(p => p.UserId == LocalUser.Id)
                .ToListAsync();
            db.PersistedQueueItems.RemoveRange(existing);

            if (state.NowPlaying.Item != null)
                db.PersistedQueueItems.Add(ToRow(state.NowPlaying.Item, 0));

            for (int i = 0; i < state.Upcoming.Count; i++)
                db.PersistedQueueItems.Add(ToRow(state.Upcoming[i], i + 1));

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
        Id          = Guid.TryParse(item.Id, out var g) ? g : Guid.NewGuid(),
        UserId      = LocalUser.Id,
        Position    = position,
        TrackId     = item.Song.SpotifyUri,
        Title       = item.Song.Title,
        Artist      = item.Song.Artist,
        CoverUrl    = item.Song.CoverUrl,
        DurationMs  = item.Song.DurationMs,
        RequestedBy = item.RequestedBy,
        Platform    = item.Platform,
        AddedAt     = item.AddedAt
    };
}
