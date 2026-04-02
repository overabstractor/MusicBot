using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Models;
using MusicBot.Data;

namespace MusicBot.Services;

public class AutoQueueService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutoQueueService> _logger;
    private readonly Random _rng = new();

    public AutoQueueService(IServiceScopeFactory scopeFactory, ILogger<AutoQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task<List<AutoQueueSong>> GetAllAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.AutoQueueSongs.OrderBy(s => s.AddedAt).ToListAsync();
    }

    public async Task<int> CountAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.AutoQueueSongs.CountAsync();
    }

    public async Task<Song?> GetRandomSongAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var count = await db.AutoQueueSongs.CountAsync();
        if (count == 0) return null;
        var skip = _rng.Next(count);
        var entry = await db.AutoQueueSongs.Skip(skip).FirstOrDefaultAsync();
        if (entry == null) return null;
        return new Song
        {
            SpotifyUri = entry.SpotifyUri,
            Title      = entry.Title,
            Artist     = entry.Artist,
            CoverUrl   = entry.CoverUrl,
            DurationMs = entry.DurationMs,
        };
    }

    public async Task<bool> AddAsync(Song song)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        if (await db.AutoQueueSongs.AnyAsync(s => s.SpotifyUri == song.SpotifyUri)) return false;
        if (await db.AutoQueueSongs.CountAsync() >= 100) return false;
        db.AutoQueueSongs.Add(new AutoQueueSong
        {
            SpotifyUri = song.SpotifyUri,
            Title      = song.Title,
            Artist     = song.Artist,
            CoverUrl   = song.CoverUrl,
            DurationMs = song.DurationMs,
            AddedAt    = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<int> BulkAddAsync(IEnumerable<Song> songs)
    {
        int added = 0;
        foreach (var song in songs)
            if (await AddAsync(song)) added++;
        return added;
    }

    public async Task<bool> RemoveAsync(string spotifyUri)
    {
        using var scope = _scopeFactory.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var row = await db.AutoQueueSongs.FirstOrDefaultAsync(s => s.SpotifyUri == spotifyUri);
        if (row == null) return false;
        db.AutoQueueSongs.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task ClearAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        db.AutoQueueSongs.RemoveRange(db.AutoQueueSongs);
        await db.SaveChangesAsync();
    }
}
