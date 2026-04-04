using System.IO;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Services;
using MusicBot.Services.Downloader;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/queue")]
[Tags("Queue")]
public class QueueController : ControllerBase
{
    private readonly UserContextManager      _userContext;
    private readonly ILocalLibraryService    _library;
    private readonly PlaybackSyncService     _sync;
    private readonly PresenceCheckService    _presence;
    private readonly YtDlpDownloaderService  _downloader;
    private readonly BannedSongService       _banned;

    public QueueController(
        UserContextManager userContext,
        ILocalLibraryService library,
        PlaybackSyncService sync,
        PresenceCheckService presence,
        YtDlpDownloaderService downloader,
        BannedSongService banned)
    {
        _userContext = userContext;
        _library     = library;
        _sync        = sync;
        _presence    = presence;
        _downloader  = downloader;
        _banned      = banned;
    }

    /// <summary>Get local queue state</summary>
    [HttpGet]
    [ProducesResponseType(typeof(SpotifyQueueState), 200)]
    public IActionResult GetQueue()
    {
        return Ok(BuildQueueState(_userContext.GetOrCreate(LocalUser.Id)));
    }

    /// <summary>Get now playing</summary>
    [HttpGet("now-playing")]
    [ProducesResponseType(typeof(NowPlayingState), 200)]
    public IActionResult GetNowPlaying()
    {
        return Ok(_userContext.GetOrCreate(LocalUser.Id).Queue.GetNowPlaying());
    }

    /// <summary>Get now playing (public overlay endpoint — token ignored)</summary>
    [HttpGet("public/{overlayToken}/now-playing")]
    [ProducesResponseType(typeof(NowPlayingState), 200)]
    public IActionResult GetNowPlayingByToken(string overlayToken)
    {
        return Ok(_userContext.GetOrCreate(LocalUser.Id).Queue.GetNowPlaying());
    }

    /// <summary>Get queue (public overlay endpoint — token ignored)</summary>
    [HttpGet("public/{overlayToken}/spotify-queue")]
    [ProducesResponseType(typeof(SpotifyQueueState), 200)]
    public IActionResult GetQueueByToken(string overlayToken)
    {
        return Ok(BuildQueueState(_userContext.GetOrCreate(LocalUser.Id)));
    }

    /// <summary>Clear all user-requested songs from the queue (leaves the background playlist intact)</summary>
    [HttpDelete("user")]
    [ProducesResponseType(204)]
    public IActionResult ClearUserQueue()
    {
        _userContext.GetOrCreate(LocalUser.Id).Queue.ClearUserQueue();
        return NoContent();
    }

    /// <summary>Remove a specific song from the queue by its URI (admin)</summary>
    [HttpDelete("item")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public IActionResult RemoveItem([FromBody] RemoveItemRequest req)
    {
        var ok = _userContext.GetOrCreate(LocalUser.Id).Queue.RemoveByUri(req.Uri);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Move a queue item up or down</summary>
    [HttpPost("move")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public IActionResult MoveItem([FromBody] MoveItemRequest req)
    {
        var queue = _userContext.GetOrCreate(LocalUser.Id).Queue;
        var ok = req.Direction == "up" ? queue.MoveUp(req.Uri) : queue.MoveDown(req.Uri);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Start auto-queue immediately even when nothing is playing</summary>
    [HttpPost("start-auto")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    public async Task<IActionResult> StartAuto()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        if (services.Queue.GetCurrentItem() != null)
            return BadRequest(CommandResult.Fail("Ya hay una canción en reproducción"));

        var started = await _sync.TryStartAutoQueueAsync(services);
        return started
            ? Ok(CommandResult.Ok("Cola automática iniciada"))
            : BadRequest(CommandResult.Fail("La cola automática está desactivada o el pool está vacío"));
    }

    /// <summary>Play a song immediately, pushing the current song back to position 0 of the queue</summary>
    [HttpPost("play-now")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    public async Task<IActionResult> PlayNow([FromBody] DirectSongRequest req)
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var song     = req.ToSong();

        var cached = await _library.FindByTrackIdAsync(song.SpotifyUri);
        if (cached != null && System.IO.File.Exists(cached.FilePath))
            song.LocalFilePath = cached.FilePath;

        _presence.CancelCheck();
        services.Queue.PlayNow(song, req.RequestedBy ?? "Admin", req.Platform ?? "web");
        await _sync.StartCurrentTrackAsync(services);

        return Ok(CommandResult.Ok($"Reproduciendo \"{song.Title}\" de {song.Artist}"));
    }

    /// <summary>Add a song directly to the queue by metadata (from history / library)</summary>
    [HttpPost("enqueue")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    public async Task<IActionResult> Enqueue([FromBody] DirectSongRequest req)
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var song     = req.ToSong();

        var cached = await _library.FindByTrackIdAsync(song.SpotifyUri);
        if (cached != null && System.IO.File.Exists(cached.FilePath))
            song.LocalFilePath = cached.FilePath;

        try
        {
            services.Queue.AddSong(song, req.RequestedBy ?? "Admin", req.Platform ?? "web");

            if (services.Queue.GetCurrentItem() == null)
            {
                services.Queue.Advance();
                await _sync.StartCurrentTrackAsync(services);
            }

            return Ok(CommandResult.Ok($"Agregada \"{song.Title}\" de {song.Artist} a la cola"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(CommandResult.Fail(ex.Message));
        }
    }

    /// <summary>Move a queue item to a specific 0-based index</summary>
    [HttpPost("reorder")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public IActionResult ReorderItem([FromBody] ReorderRequest req)
    {
        var ok = _userContext.GetOrCreate(LocalUser.Id).Queue.Reorder(req.Uri, req.ToIndex);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Import all tracks from a YouTube or Spotify playlist URL into the queue</summary>
    [HttpPost("import-playlist")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> ImportPlaylist([FromBody] ImportPlaylistRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "URL requerida" });

        var services = _userContext.GetOrCreate(LocalUser.Id);
        var requestedBy = req.RequestedBy ?? "Admin";

        List<Song> tracks;

        if (TryExtractYouTubePlaylistId(req.Url, out _))
        {
            tracks = await _downloader.ImportPlaylistAsync(req.Url, int.MaxValue);
        }
        else if (TryExtractSpotifyPlaylistId(req.Url, out var playlistId) && services.Spotify.IsAuthenticated)
        {
            try { tracks = await services.Spotify.GetPlaylistTracksAsync(playlistId, int.MaxValue); }
            catch (Exception ex) { return BadRequest(new { error = $"No se pudo obtener la playlist de Spotify: {ex.Message}" }); }
        }
        else if (TryExtractSpotifyPlaylistId(req.Url, out _) && !services.Spotify.IsAuthenticated)
        {
            return BadRequest(new { error = "Conecta Spotify primero para importar playlists de Spotify" });
        }
        else
        {
            return BadRequest(new { error = "URL de playlist no reconocida (YouTube o Spotify)" });
        }

        int added = 0, skipped = 0;
        foreach (var song in tracks)
        {
            if (_banned.IsBanned(song.SpotifyUri)) { skipped++; continue; }

            var cached = await _library.FindByTrackIdAsync(song.SpotifyUri);
            if (cached != null && System.IO.File.Exists(cached.FilePath))
                song.LocalFilePath = cached.FilePath;

            try
            {
                services.Queue.AddSong(song, requestedBy, "web", bypassUserLimit: true, isPlaylistItem: true);
                if (song.LocalFilePath == null) _downloader.StartDownload(song);
                added++;
            }
            catch { skipped++; } // queue full or duplicate
        }

        if (services.Queue.GetCurrentItem() == null && services.Queue.GetUpcoming().Count > 0)
        {
            services.Queue.Advance();
            await _sync.StartCurrentTrackAsync(services);
        }

        return Ok(new { added, skipped, total = tracks.Count });
    }

    /// <summary>Set a YouTube playlist as the cyclic background (plays when no user requests pending)</summary>
    [HttpPost("set-playlist")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> SetPlaylist([FromBody] SetPlaylistRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "URL requerida" });

        if (!TryExtractYouTubePlaylistId(req.Url, out _))
            return BadRequest(new { error = "Solo se admiten playlists de YouTube para la cola de fondo" });

        var tracks = await _downloader.ImportPlaylistAsync(req.Url, 500);
        if (tracks.Count == 0)
            return BadRequest(new { error = "La playlist está vacía o no se pudo importar" });

        var services = _userContext.GetOrCreate(LocalUser.Id);
        services.Queue.SetBackgroundPlaylist(tracks);

        // Resolve already-cached files
        foreach (var song in tracks)
        {
            var cached = await _library.FindByTrackIdAsync(song.SpotifyUri);
            if (cached != null && System.IO.File.Exists(cached.FilePath))
                song.LocalFilePath = cached.FilePath;
        }

        // Download all uncached songs one at a time in the background.
        // On-demand downloads (EnsureLocalFileAsync) will skip ahead in the queue when needed.
        var toDownload = tracks.Where(s => s.LocalFilePath == null).ToList();
        if (toDownload.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                foreach (var song in toDownload)
                {
                    try   { await _downloader.GetOrStartDownloadAsync(song); }
                    catch { /* already logged inside the downloader */ }
                }
            });
        }

        // Start playback if nothing is currently playing
        if (services.Queue.GetCurrentItem() == null)
        {
            services.Queue.Advance();
            await _sync.StartCurrentTrackAsync(services);
        }

        return Ok(new { count = tracks.Count, message = $"Playlist de fondo establecida con {tracks.Count} canciones" });
    }

    /// <summary>Clear the background playlist</summary>
    [HttpDelete("playlist")]
    [ProducesResponseType(204)]
    public IActionResult ClearPlaylist()
    {
        _userContext.GetOrCreate(LocalUser.Id).Queue.ClearBackgroundPlaylist();
        return NoContent();
    }

    /// <summary>Get current background playlist state</summary>
    [HttpGet("playlist")]
    [ProducesResponseType(200)]
    public IActionResult GetPlaylist()
    {
        var (songs, index) = _userContext.GetOrCreate(LocalUser.Id).Queue.GetBackgroundPlaylist();
        return Ok(new { songs, index, total = songs.Count });
    }

    private static bool TryExtractYouTubePlaylistId(string url, out string playlistId)
    {
        playlistId = string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("www.youtube.com" or "youtube.com" or "m.youtube.com")) return false;
        var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var list = qs["list"];
        if (string.IsNullOrEmpty(list)) return false;
        playlistId = list;
        return true;
    }

    private static bool TryExtractSpotifyPlaylistId(string url, out string playlistId)
    {
        playlistId = string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Host.ToLowerInvariant() != "open.spotify.com") return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length >= 2 && parts[0] == "playlist") { playlistId = parts[1]; return true; }
        return false;
    }

    private static SpotifyQueueState BuildQueueState(UserServices services)
    {
        var state   = services.Queue.GetState();
        var current = state.NowPlaying.Item;

        return new SpotifyQueueState
        {
            CurrentlyPlaying = current == null ? null : new Song
            {
                SpotifyUri  = current.Song.SpotifyUri,
                Title       = current.Song.Title,
                Artist      = current.Song.Artist,
                CoverUrl    = current.Song.CoverUrl,
                DurationMs  = current.Song.DurationMs,
                RequestedBy = current.RequestedBy,
                Platform    = current.Platform,
            },
            Queue = state.Upcoming.Select(i => new Song
            {
                SpotifyUri  = i.Song.SpotifyUri,
                Title       = i.Song.Title,
                Artist      = i.Song.Artist,
                CoverUrl    = i.Song.CoverUrl,
                DurationMs  = i.Song.DurationMs,
                RequestedBy = i.RequestedBy,
                Platform    = i.Platform,
            }).ToList()
        };
    }

    [HttpPost("shuffle")]
    public IActionResult Shuffle()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        services.Queue.Shuffle();
        return Ok(new { message = "Cola mezclada" });
    }
}

public class RemoveItemRequest { public string Uri { get; set; } = string.Empty; }
public class MoveItemRequest   { public string Uri { get; set; } = string.Empty; public string Direction { get; set; } = "up"; }

public class DirectSongRequest
{
    public string  SpotifyUri  { get; set; } = string.Empty;
    public string  Title       { get; set; } = string.Empty;
    public string  Artist      { get; set; } = string.Empty;
    public string? CoverUrl    { get; set; }
    public int     DurationMs  { get; set; }
    public string? RequestedBy { get; set; }
    public string? Platform    { get; set; }

    public Song ToSong() => new()
    {
        SpotifyUri = SpotifyUri,
        Title      = Title,
        Artist     = Artist,
        CoverUrl   = CoverUrl ?? "",
        DurationMs = DurationMs,
    };
}

public class ReorderRequest
{
    public string Uri     { get; set; } = string.Empty;
    public int    ToIndex { get; set; }
}

public class ImportPlaylistRequest
{
    public string  Url         { get; set; } = string.Empty;
    public string? RequestedBy { get; set; }
}

public class SetPlaylistRequest
{
    public string Url { get; set; } = string.Empty;
}
