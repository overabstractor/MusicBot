using System.Web;
using Microsoft.AspNetCore.Mvc;
using MusicBot.Core.Models;
using MusicBot.Services;
using MusicBot.Services.Downloader;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/autoqueue")]
[Tags("AutoQueue")]
public class AutoQueueController : ControllerBase
{
    private readonly AutoQueueService      _autoQueue;
    private readonly YtDlpDownloaderService _downloader;
    private readonly UserContextManager    _userContext;

    public AutoQueueController(AutoQueueService autoQueue, YtDlpDownloaderService downloader, UserContextManager userContext)
    {
        _autoQueue   = autoQueue;
        _downloader  = downloader;
        _userContext = userContext;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _autoQueue.GetAllAsync());

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AutoQueueAddRequest req)
    {
        var song = new Song
        {
            SpotifyUri = req.SpotifyUri,
            Title      = req.Title,
            Artist     = req.Artist,
            CoverUrl   = req.CoverUrl ?? "",
            DurationMs = req.DurationMs,
        };
        var added = await _autoQueue.AddAsync(song);
        return added ? Ok(new { added = true }) : BadRequest(new { error = "Ya existe o la lista está llena (máx. 100)" });
    }

    [HttpDelete("{uri}")]
    public async Task<IActionResult> Remove(string uri)
    {
        var ok = await _autoQueue.RemoveAsync(Uri.UnescapeDataString(uri));
        return ok ? NoContent() : NotFound();
    }

    [HttpDelete]
    public async Task<IActionResult> Clear()
    {
        await _autoQueue.ClearAsync();
        return NoContent();
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] AutoQueueImportRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "URL requerida" });

        List<Song> tracks;
        var services = _userContext.GetOrCreate(LocalUser.Id);

        if (TryExtractYouTubePlaylistId(req.Url, out _))
        {
            tracks = await _downloader.ImportPlaylistAsync(req.Url, 100);
        }
        else if (TryExtractSpotifyPlaylistId(req.Url, out var pid) && services.Spotify.IsAuthenticated)
        {
            try { tracks = await services.Spotify.GetPlaylistTracksAsync(pid, 100); }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }
        else if (TryExtractSpotifyPlaylistId(req.Url, out _))
        {
            return BadRequest(new { error = "Conecta Spotify primero para importar playlists de Spotify" });
        }
        else
        {
            return BadRequest(new { error = "URL de playlist no reconocida" });
        }

        var added = await _autoQueue.BulkAddAsync(tracks);
        return Ok(new { added, total = tracks.Count });
    }

    private static bool TryExtractYouTubePlaylistId(string url, out string id)
    {
        id = string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("www.youtube.com" or "youtube.com" or "m.youtube.com")) return false;
        var list = HttpUtility.ParseQueryString(uri.Query)["list"];
        if (string.IsNullOrEmpty(list)) return false;
        id = list; return true;
    }

    private static bool TryExtractSpotifyPlaylistId(string url, out string id)
    {
        id = string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Host.ToLowerInvariant() != "open.spotify.com") return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length >= 2 && parts[0] == "playlist") { id = parts[1]; return true; }
        return false;
    }
}

public class AutoQueueAddRequest
{
    public string  SpotifyUri { get; set; } = "";
    public string  Title      { get; set; } = "";
    public string  Artist     { get; set; } = "";
    public string? CoverUrl   { get; set; }
    public int     DurationMs { get; set; }
}

public class AutoQueueImportRequest
{
    public string Url { get; set; } = "";
}
