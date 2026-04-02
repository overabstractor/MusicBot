using MusicBot.Core.Models;

namespace MusicBot.Core.Interfaces;

/// <summary>Manages the local music library cache (SQLite + disk).</summary>
public interface ILocalLibraryService
{
    Task<CachedTrack?> FindByTrackIdAsync(string trackId);
    Task SaveAsync(CachedTrack track);
    Task UpdateDurationAsync(string trackId, int durationMs);
    Task DeleteByTrackIdAsync(string trackId);
}
