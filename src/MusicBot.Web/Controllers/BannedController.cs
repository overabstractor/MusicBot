using Microsoft.AspNetCore.Mvc;
using MusicBot.Services;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/banned")]
[Tags("Banned Songs")]
public class BannedController : ControllerBase
{
    private readonly BannedSongService _banned;

    public BannedController(BannedSongService banned) => _banned = banned;

    /// <summary>List all banned songs</summary>
    [HttpGet]
    public IActionResult GetAll()
        => Ok(_banned.GetAll().Select(s => new { s.Uri, s.Title, s.Artist, s.BannedAt }));

    /// <summary>Ban a song</summary>
    [HttpPost]
    [ProducesResponseType(204)]
    public IActionResult Ban([FromBody] BanRequest req)
    {
        _banned.Ban(req.Uri, req.Title, req.Artist);
        return NoContent();
    }

    /// <summary>Unban a song</summary>
    [HttpDelete("{uri}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public IActionResult Unban(string uri)
    {
        var ok = _banned.Unban(Uri.UnescapeDataString(uri));
        return ok ? NoContent() : NotFound();
    }
}

public class BanRequest
{
    public string Uri    { get; set; } = string.Empty;
    public string Title  { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
}
