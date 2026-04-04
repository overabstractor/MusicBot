using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Models;
using MusicBot.Data;

namespace MusicBot.Services;

public class PlaylistMembershipDto
{
    public int      Id           { get; set; }
    public string   Name         { get; set; } = "";
    public int      SongCount    { get; set; }
    public bool     IsInPlaylist { get; set; }
    public bool     IsSystem     { get; set; }
    public DateTime UpdatedAt    { get; set; }
}

public class PlaylistLibraryService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PlaylistLibraryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<List<PlaylistLibrary>> GetAllAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibraries
            .OrderBy(p => p.IsPinned ? 0 : 1)
            .ThenBy(p => p.IsPinned ? p.PinOrder : 0)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync();
    }

    public async Task<PlaylistLibrary?> GetByIdAsync(int id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibraries.FindAsync(id);
    }

    public async Task<PlaylistLibrary> CreateAsync(string name)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var playlist = new PlaylistLibrary
        {
            Name      = name.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.PlaylistLibraries.Add(playlist);
        await db.SaveChangesAsync();
        return playlist;
    }

    public async Task<bool> RenameAsync(int id, string newName)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var row = await db.PlaylistLibraries.FindAsync(id);
        if (row == null) return false;
        row.Name      = newName.Trim();
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var row = await db.PlaylistLibraries.FindAsync(id);
        if (row == null) return false;
        db.PlaylistLibraries.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<PlaylistLibrarySong>> GetSongsAsync(int playlistId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibrarySongs
            .Where(s => s.PlaylistId == playlistId)
            .OrderBy(s => s.Position)
            .ToListAsync();
    }

    public async Task<int> GetSongCountAsync(int playlistId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibrarySongs.CountAsync(s => s.PlaylistId == playlistId);
    }

    /// <summary>Returns the first <paramref name="max"/> distinct non-empty cover URLs for a playlist.</summary>
    public async Task<List<string>> GetCoverUrlsAsync(int playlistId, int max = 4)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibrarySongs
            .Where(s => s.PlaylistId == playlistId && s.CoverUrl != null && s.CoverUrl != "")
            .OrderBy(s => s.Position)
            .Select(s => s.CoverUrl!)
            .Distinct()
            .Take(max)
            .ToListAsync();
    }

    public async Task<bool> AddSongAsync(int playlistId, Song song)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

        if (await db.PlaylistLibrarySongs.AnyAsync(s => s.PlaylistId == playlistId && s.SpotifyUri == song.SpotifyUri))
            return false;

        var position = await db.PlaylistLibrarySongs.CountAsync(s => s.PlaylistId == playlistId);
        db.PlaylistLibrarySongs.Add(new PlaylistLibrarySong
        {
            PlaylistId = playlistId,
            SpotifyUri = song.SpotifyUri,
            Title      = song.Title,
            Artist     = song.Artist,
            CoverUrl   = song.CoverUrl,
            DurationMs = song.DurationMs,
            Position   = position,
        });

        var playlist = await db.PlaylistLibraries.FindAsync(playlistId);
        if (playlist != null) playlist.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveSongAsync(int playlistId, string spotifyUri)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

        var row = await db.PlaylistLibrarySongs
            .FirstOrDefaultAsync(s => s.PlaylistId == playlistId && s.SpotifyUri == spotifyUri);
        if (row == null) return false;

        db.PlaylistLibrarySongs.Remove(row);

        // Re-sequence positions
        var remaining = await db.PlaylistLibrarySongs
            .Where(s => s.PlaylistId == playlistId && s.Position > row.Position)
            .ToListAsync();
        foreach (var r in remaining) r.Position--;

        var playlist = await db.PlaylistLibraries.FindAsync(playlistId);
        if (playlist != null) playlist.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return true;
    }

    public async Task<int> BulkAddAsync(int playlistId, IEnumerable<Song> songs)
    {
        int added = 0;
        foreach (var song in songs)
            if (await AddSongAsync(playlistId, song)) added++;
        return added;
    }

    // ── System playlists (Liked Songs) ────────────────────────────────────────

    /// <summary>Creates the "Música que te gustó" system playlist if it doesn't exist yet, and ensures it is pinned.</summary>
    public async Task EnsureSystemPlaylistsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var existing = await db.PlaylistLibraries.FirstOrDefaultAsync(p => p.IsSystem);
        if (existing == null)
        {
            db.PlaylistLibraries.Add(new PlaylistLibrary
            {
                Name      = "Música que te gustó",
                IsSystem  = true,
                IsPinned  = true,
                PinOrder  = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        else if (!existing.IsPinned)
        {
            existing.IsPinned = true;
            existing.PinOrder = 0;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Toggles the pinned state of a playlist. Returns false if not found.</summary>
    public async Task<bool> SetPinnedAsync(int id, bool isPinned)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var row = await db.PlaylistLibraries.FindAsync(id);
        if (row == null) return false;

        if (isPinned && !row.IsPinned)
        {
            var maxOrder = await db.PlaylistLibraries
                .Where(p => p.IsPinned)
                .Select(p => (int?)p.PinOrder)
                .MaxAsync() ?? -1;
            row.PinOrder = maxOrder + 1;
        }
        else if (!isPinned && row.IsPinned)
        {
            var oldOrder = row.PinOrder;
            var others = await db.PlaylistLibraries
                .Where(p => p.IsPinned && p.Id != id && p.PinOrder > oldOrder)
                .ToListAsync();
            foreach (var o in others) o.PinOrder--;
            row.PinOrder = 0;
        }

        row.IsPinned = isPinned;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Reorders pinned playlists. <paramref name="ids"/> is the desired order (index = new PinOrder).</summary>
    public async Task ReorderPinsAsync(List<int> ids)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var playlists = await db.PlaylistLibraries.Where(p => ids.Contains(p.Id)).ToListAsync();
        for (int i = 0; i < ids.Count; i++)
        {
            var pl = playlists.FirstOrDefault(p => p.Id == ids[i]);
            if (pl != null) pl.PinOrder = i;
        }
        await db.SaveChangesAsync();
    }

    public async Task<int?> GetLikedPlaylistIdAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var pl = await db.PlaylistLibraries.FirstOrDefaultAsync(p => p.IsSystem);
        return pl?.Id;
    }

    public async Task<List<string>> GetLikedUrisAsync()
    {
        var id = await GetLikedPlaylistIdAsync();
        if (id == null) return [];
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibrarySongs
            .Where(s => s.PlaylistId == id.Value)
            .Select(s => s.SpotifyUri)
            .ToListAsync();
    }

    public async Task<bool> IsSongInPlaylistAsync(int playlistId, string spotifyUri)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        return await db.PlaylistLibrarySongs
            .AnyAsync(s => s.PlaylistId == playlistId && s.SpotifyUri == spotifyUri);
    }

    public async Task<List<PlaylistMembershipDto>> GetSongMembershipsAsync(string spotifyUri)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

        var playlists = await db.PlaylistLibraries.OrderBy(p => p.CreatedAt).ToListAsync();

        var containingIds = (await db.PlaylistLibrarySongs
            .Where(s => s.SpotifyUri == spotifyUri)
            .Select(s => s.PlaylistId)
            .ToListAsync())
            .ToHashSet();

        var counts = await db.PlaylistLibrarySongs
            .GroupBy(s => s.PlaylistId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        return playlists
            .Select(p => new PlaylistMembershipDto
            {
                Id           = p.Id,
                Name         = p.Name,
                SongCount    = counts.GetValueOrDefault(p.Id, 0),
                IsInPlaylist = containingIds.Contains(p.Id),
                IsSystem     = p.IsSystem,
                UpdatedAt    = p.UpdatedAt,
            })
            .ToList();
    }

    /// <summary>
    /// Marks the given playlist as active and all others as inactive.
    /// Pass null to deactivate all.
    /// </summary>
    public async Task SetActiveAsync(int? id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

        var all = await db.PlaylistLibraries.ToListAsync();
        foreach (var p in all)
            p.IsActive = id.HasValue && p.Id == id.Value;

        await db.SaveChangesAsync();
    }
}
