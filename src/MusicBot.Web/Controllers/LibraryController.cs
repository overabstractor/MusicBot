using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicBot.Data;
using MusicBot.Services;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/library")]
[Tags("Library")]
public class LibraryController : ControllerBase
{
    private readonly MusicBotDbContext _db;
    private readonly string _libraryPath;

    public LibraryController(MusicBotDbContext db, IOptions<MusicLibrarySettings> settings)
    {
        _db = db;
        _libraryPath = settings.Value.LibraryPath;
    }

    /// <summary>Get all cached tracks with play statistics</summary>
    [HttpGet]
    public async Task<IActionResult> GetLibrary()
    {
        var tracks = await _db.CachedTracks
            .OrderByDescending(t => t.DownloadedAt)
            .ToListAsync();

        // Group play history by TrackId
        var history = await _db.PlayedSongs
            .GroupBy(p => p.TrackId)
            .Select(g => new { TrackId = g.Key, Count = g.Count(), TotalMs = (long)g.Sum(p => p.DurationMs) })
            .ToListAsync();

        var statsMap = history.ToDictionary(h => h.TrackId);

        var result = tracks.Select(t =>
        {
            statsMap.TryGetValue(t.TrackId, out var stats);
            long fileSizeBytes = 0;
            try { if (System.IO.File.Exists(t.FilePath)) fileSizeBytes = new FileInfo(t.FilePath).Length; }
            catch { }

            return new
            {
                t.Id,
                t.TrackId,
                t.Title,
                t.Artist,
                t.CoverUrl,
                t.DurationMs,
                t.DownloadedAt,
                FileSizeBytes = fileSizeBytes,
                FileExists    = fileSizeBytes > 0 || System.IO.File.Exists(t.FilePath),
                PlayCount     = stats?.Count ?? 0,
                TotalPlayedMs = stats?.TotalMs ?? 0L,
            };
        });

        return Ok(result);
    }

    /// <summary>Delete a single cached track (DB row + file on disk)</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteTrack(int id)
    {
        var track = await _db.CachedTracks.FindAsync(id);
        if (track == null) return NotFound();

        TryDeleteFile(track.FilePath);
        _db.CachedTracks.Remove(track);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Delete all cached tracks (DB rows + files on disk)</summary>
    [HttpDelete]
    public async Task<IActionResult> ClearLibrary()
    {
        var all = await _db.CachedTracks.ToListAsync();
        foreach (var t in all) TryDeleteFile(t.FilePath);
        _db.CachedTracks.RemoveRange(all);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Open the music library folder in Windows Explorer</summary>
    [HttpPost("open-folder")]
    public IActionResult OpenFolder()
    {
        var path = Path.GetFullPath(_libraryPath);
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "explorer.exe",
            Arguments       = $"\"{path}\"",
            UseShellExecute = true,
        });
        return Ok(new { path });
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch { /* ignore — file may be in use */ }
    }
}
