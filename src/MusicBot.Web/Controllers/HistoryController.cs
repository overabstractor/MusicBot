using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicBot.Data;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/history")]
[Tags("History")]
public class HistoryController : ControllerBase
{
    private readonly MusicBotDbContext _db;

    public HistoryController(MusicBotDbContext db) => _db = db;

    /// <summary>Get the last N played songs (newest first)</summary>
    [HttpGet]
    public async Task<IActionResult> GetHistory([FromQuery] int limit = 50)
    {
        var items = await _db.PlayedSongs
            .OrderByDescending(p => p.PlayedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(p => new
            {
                p.Id,
                p.TrackId,
                p.Title,
                p.Artist,
                p.CoverUrl,
                p.DurationMs,
                p.RequestedBy,
                p.Platform,
                p.PlayedAt,
            })
            .ToListAsync();

        return Ok(items);
    }

    /// <summary>Clear all play history</summary>
    [HttpDelete]
    public async Task<IActionResult> ClearHistory()
    {
        await _db.PlayedSongs.ExecuteDeleteAsync();
        return NoContent();
    }
}
