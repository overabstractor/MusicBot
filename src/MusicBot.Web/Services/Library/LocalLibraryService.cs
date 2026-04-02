using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Data;

namespace MusicBot.Services.Library;

public class LocalLibraryService : ILocalLibraryService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public LocalLibraryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<CachedTrack?> FindByTrackIdAsync(string trackId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.CachedTracks.FirstOrDefaultAsync(t => t.TrackId == trackId);
    }

    public async Task SaveAsync(CachedTrack track)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

        // Avoid duplicates in case of a race condition
        var exists = await db.CachedTracks.AnyAsync(t => t.TrackId == track.TrackId);
        if (!exists)
        {
            db.CachedTracks.Add(track);
            await db.SaveChangesAsync();
        }
    }

    public async Task UpdateDurationAsync(string trackId, int durationMs)
    {
        using var scope = _scopeFactory.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var row = await db.CachedTracks.FirstOrDefaultAsync(t => t.TrackId == trackId);
        if (row == null) return;
        row.DurationMs = durationMs;
        await db.SaveChangesAsync();
    }

    public async Task DeleteByTrackIdAsync(string trackId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var row = await db.CachedTracks.FirstOrDefaultAsync(t => t.TrackId == trackId);
        if (row == null) return;
        // Delete physical file
        try { if (File.Exists(row.FilePath)) File.Delete(row.FilePath); } catch { /* ignore */ }
        db.CachedTracks.Remove(row);
        await db.SaveChangesAsync();
    }
}
