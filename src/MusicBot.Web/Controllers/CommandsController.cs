using Microsoft.AspNetCore.Mvc;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Services;
using MusicBot.Services.Downloader;

namespace MusicBot.Controllers;

[ApiController]
[Route("api")]
[Tags("Commands")]
public class CommandsController : ControllerBase
{
    private readonly CommandRouterService  _router;
    private readonly UserContextManager    _userContext;
    private readonly PlaybackSyncService   _sync;
    private readonly YtDlpDownloaderService _downloader;
    private readonly IMetadataService       _metadata;

    public CommandsController(
        CommandRouterService router,
        UserContextManager userContext,
        PlaybackSyncService sync,
        YtDlpDownloaderService downloader,
        IMetadataService metadata)
    {
        _router     = router;
        _userContext = userContext;
        _sync        = sync;
        _downloader  = downloader;
        _metadata    = metadata;
    }

    /// <summary>
    /// Search for songs. YouTube is the primary source (finds the actual video to download).
    /// iTunes and Spotify run in parallel and enrich YouTube results with clean metadata.
    /// If YouTube search is slow or returns nothing, iTunes/Spotify results fill the list.
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(List<Song>), 200)]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(q)) return Ok(Array.Empty<Song>());

        var services = _userContext.GetOrCreate(LocalUser.Id);

        // YouTube video search + playlist search in parallel (both capped by timeout)
        var ytRaw         = _downloader.SearchAsync(q, limit);
        var ytPlaylistRaw = _downloader.SearchPlaylistsAsync(q, 5);

        var ytDone         = await Task.WhenAny(ytRaw,         Task.Delay(7000));
        var ytPlaylistDone = await Task.WhenAny(ytPlaylistRaw, Task.Delay(12000));

        var ytResults       = ytDone == ytRaw && ytRaw.IsCompletedSuccessfully
                              ? ytRaw.Result : new List<Song>();
        var playlistResults = ytPlaylistDone == ytPlaylistRaw && ytPlaylistRaw.IsCompletedSuccessfully
                              ? ytPlaylistRaw.Result : new List<Song>();

        // iTunes + Spotify in parallel (fast HTTP calls)
        var itunesTask  = Task.Run(async () => { try { return (IEnumerable<Song>)await _metadata.SearchAsync(q, limit); } catch { return []; } });
        var spotifyTask = services.Spotify.IsAuthenticated
            ? Task.Run(async () => { try { return (IEnumerable<Song>)await services.Spotify.SearchAsync(q, limit); } catch { return []; } })
            : Task.FromResult<IEnumerable<Song>>([]);

        await Task.WhenAll(itunesTask, spotifyTask);
        var metaSongs = (await itunesTask).Concat(await spotifyTask).ToList();

        var results = new List<Song>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Enrich YouTube results with iTunes/Spotify metadata where a match is found
        foreach (var yt in ytResults)
        {
            var best = metaSongs
                .Select(m => (song: m, score: MetaMatchScore(yt, m)))
                .Where(x => x.score >= 3)
                .OrderByDescending(x => x.score)
                .FirstOrDefault();

            var song = best.song == null ? yt : new Song
            {
                SpotifyUri = yt.SpotifyUri,
                Title      = best.song.Title,
                Artist     = best.song.Artist,
                CoverUrl   = !string.IsNullOrEmpty(best.song.CoverUrl) ? best.song.CoverUrl : yt.CoverUrl,
                DurationMs = best.song.DurationMs > 0 ? best.song.DurationMs : yt.DurationMs,
            };

            if (seen.Add(song.SpotifyUri)) results.Add(song);
        }

        // 2. Fill remaining slots with metadata-only results (spotify:/itunes: URIs).
        //    These are played via the downloader which finds the YouTube match at download time.
        foreach (var meta in metaSongs)
        {
            if (results.Count >= limit) break;
            if (seen.Add(meta.SpotifyUri)) results.Add(meta);
        }

        // 3. Append playlist results at the end (separate section in the UI)
        results.AddRange(playlistResults);

        return Ok(results.Take(limit + playlistResults.Count));
    }

    private static double MetaMatchScore(Song yt, Song meta)
    {
        var ytTitle  = Norm(yt.Title);
        var ytArtist = Norm(yt.Artist);
        var mtTitle  = Norm(meta.Title);
        var mtArtist = Norm(meta.Artist);
        double score = 0;
        if (ytTitle.Length > 1 && mtTitle.Length > 1 && (ytTitle.Contains(mtTitle) || mtTitle.Contains(ytTitle))) score += 2;
        if (ytArtist.Length > 1 && mtArtist.Length > 1 && (ytArtist.Contains(mtArtist) || mtArtist.Contains(ytArtist))) score += 2;
        if (yt.DurationMs > 0 && meta.DurationMs > 0 && Math.Abs(yt.DurationMs - meta.DurationMs) < 6000) score += 1;
        return score;
    }

    private static string Norm(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^\w\s]", "").Trim();

    /// <summary>Preview tracks from a YouTube playlist URL without saving anything.</summary>
    [HttpGet("search/playlist-tracks")]
    [ProducesResponseType(typeof(List<Song>), 200)]
    public async Task<IActionResult> GetPlaylistTracks([FromQuery] string url, [FromQuery] int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(url))
            return BadRequest(new { error = "URL requerida" });
        try
        {
            var tracks = await _downloader.ImportPlaylistAsync(url, limit);
            return Ok(tracks);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Search and add a song to the queue</summary>
    [HttpPost("play")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    public async Task<IActionResult> Play([FromBody] PlayRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(CommandResult.Fail("Missing 'query' field"));

        var result = await _router.HandleAsync(new BotCommand
        {
            Type        = "play",
            Query       = request.Query,
            RequestedBy = request.RequestedBy ?? "Anonymous",
            Platform    = request.Platform    ?? "web"
        }, _userContext.GetOrCreate(LocalUser.Id));

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Skip to the next song</summary>
    [HttpPost("skip")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    public async Task<IActionResult> Skip([FromBody] BaseRequest request)
    {
        var result = await _router.HandleAsync(new BotCommand
        {
            Type        = "skip",
            RequestedBy = request.RequestedBy ?? "Anonymous",
            Platform    = "http"
        }, _userContext.GetOrCreate(LocalUser.Id));

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Bump a user's song up one position in the queue</summary>
    [HttpPost("bump")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    public async Task<IActionResult> Bump([FromBody] BaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestedBy))
            return BadRequest(CommandResult.Fail("Missing 'requestedBy' field"));

        var result = await _router.HandleAsync(new BotCommand
        {
            Type        = "bump",
            RequestedBy = request.RequestedBy,
            Platform    = "http"
        }, _userContext.GetOrCreate(LocalUser.Id));

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Pause playback</summary>
    [HttpPost("pause")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Pause()
    {
        await _userContext.GetOrCreate(LocalUser.Id).Player.PauseAsync();
        return NoContent();
    }

    /// <summary>Resume playback (or start current track if player is unloaded after restart)</summary>
    [HttpPost("resume")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Resume()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);

        // After a restart the player is unloaded (CurrentFilePath == null) but the queue
        // may already have a current item from persistence. In that case, start it fresh.
        if (services.Player.CurrentFilePath == null && services.Queue.GetCurrentItem() != null)
            await _sync.StartCurrentTrackAsync(services);
        else
            await services.Player.ResumeAsync();

        return NoContent();
    }

    /// <summary>Register a skip vote (!si = skip, !no = keep)</summary>
    [HttpPost("vote")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    public async Task<IActionResult> Vote([FromBody] VoteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username))
            return BadRequest(CommandResult.Fail("username es requerido"));

        var result = await _router.HandleAsync(new BotCommand
        {
            Type        = req.Skip ? "si" : "no",
            RequestedBy = req.Username,
            Platform    = req.Platform ?? "web"
        }, _userContext.GetOrCreate(LocalUser.Id));

        return Ok(result);
    }

    /// <summary>
    /// TikTok gift bump: each coin moves the donor's song one position up.
    /// 100+ coins at once interrupts the current song immediately.
    /// </summary>
    [HttpPost("queue/gift-bump")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    public async Task<IActionResult> GiftBump([FromBody] GiftBumpRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || req.Coins <= 0)
            return BadRequest(CommandResult.Fail("username y coins son requeridos"));

        var services = _userContext.GetOrCreate(LocalUser.Id);

        if (req.Coins >= 100)
        {
            var ok = services.Queue.InterruptForUser(req.Username);
            if (!ok)
                return Ok(CommandResult.Fail($"@{req.Username} no tiene canciones en la cola"));

            await _sync.StartCurrentTrackAsync(services);
            return Ok(CommandResult.Ok($"¡@{req.Username} interrumpió la cola con {req.Coins} monedas! 🚀"));
        }
        else
        {
            for (int i = 0; i < req.Coins; i++)
                if (!services.Queue.Bump(req.Username)) break;

            return Ok(CommandResult.Ok($"@{req.Username} subió su canción {req.Coins} posición(es)"));
        }
    }
}

public class PlayRequest
{
    public string  Query       { get; set; } = string.Empty;
    public string? RequestedBy { get; set; }
    public string? Platform    { get; set; }
}

public class BaseRequest
{
    public string? RequestedBy { get; set; }
}

public class VoteRequest
{
    public string  Username { get; set; } = string.Empty;
    public bool    Skip     { get; set; }
    public string? Platform { get; set; }
}

public class GiftBumpRequest
{
    public string Username { get; set; } = string.Empty;
    public int    Coins    { get; set; }
}
