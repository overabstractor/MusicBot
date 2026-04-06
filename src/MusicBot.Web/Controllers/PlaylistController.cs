using SysFile = System.IO.File;
using SysFileInfo = System.IO.FileInfo;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Hubs;
using MusicBot.Services;
using MusicBot.Services.Downloader;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/playlists")]
[Tags("Playlists")]
public class PlaylistController : ControllerBase
{
    private readonly PlaylistLibraryService  _playlists;
    private readonly YtDlpDownloaderService  _downloader;
    private readonly UserContextManager      _userContext;
    private readonly PlaybackSyncService     _sync;
    private readonly ILocalLibraryService    _library;
    private readonly IHubContext<OverlayHub> _hub;

    public PlaylistController(
        PlaylistLibraryService playlists,
        YtDlpDownloaderService downloader,
        UserContextManager userContext,
        PlaybackSyncService sync,
        ILocalLibraryService library,
        IHubContext<OverlayHub> hub)
    {
        _playlists   = playlists;
        _downloader  = downloader;
        _userContext = userContext;
        _sync        = sync;
        _library     = library;
        _hub         = hub;
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var all = await _playlists.GetAllAsync();
        var result = new List<object>();
        foreach (var p in all)
        {
            var count     = await _playlists.GetSongCountAsync(p.Id);
            var coverUrls = await _playlists.GetCoverUrlsAsync(p.Id, 4);
            result.Add(new { p.Id, p.Name, p.IsActive, p.IsSystem, p.IsPinned, p.PinOrder, p.CreatedAt, SongCount = count, CoverUrls = coverUrls });
        }
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PlaylistCreateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "El nombre no puede estar vacío" });

        try
        {
            var playlist = await _playlists.CreateAsync(req.Name);
            return Ok(new { playlist.Id, playlist.Name, playlist.IsActive, playlist.CreatedAt, SongCount = 0 });
        }
        catch
        {
            return BadRequest(new { error = "Ya existe una lista con ese nombre" });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();
        if (playlist.IsSystem) return BadRequest(new { error = "No se puede eliminar una lista del sistema" });

        // If this was the active playlist, deactivate the background playlist
        if (playlist.IsActive)
        {
            var services = _userContext.GetOrCreate(LocalUser.Id);
            services.Queue.ClearBackgroundPlaylist();
        }

        await _playlists.DeleteAsync(id);
        await BroadcastStatusAsync();
        return NoContent();
    }

    [HttpPut("{id:int}/rename")]
    public async Task<IActionResult> Rename(int id, [FromBody] PlaylistCreateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "El nombre no puede estar vacío" });

        var existing = await _playlists.GetByIdAsync(id);
        if (existing?.IsSystem == true) return BadRequest(new { error = "No se puede renombrar una lista del sistema" });
        var ok = await _playlists.RenameAsync(id, req.Name);
        if (ok) await BroadcastStatusAsync();
        return ok ? NoContent() : NotFound();
    }

    // ── Pin ──────────────────────────────────────────────────────────────────

    [HttpPost("{id:int}/pin")]
    public async Task<IActionResult> TogglePin(int id)
    {
        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        await _playlists.SetPinnedAsync(id, !playlist.IsPinned);
        await BroadcastStatusAsync();
        return Ok(new { isPinned = !playlist.IsPinned });
    }

    [HttpPut("pins/reorder")]
    public async Task<IActionResult> ReorderPins([FromBody] PinReorderRequest req)
    {
        if (req.Ids == null || req.Ids.Count == 0)
            return BadRequest(new { error = "ids requeridos" });

        await _playlists.ReorderPinsAsync(req.Ids);
        await BroadcastStatusAsync();
        return NoContent();
    }

    // ── Songs ─────────────────────────────────────────────────────────────────

    [HttpGet("{id:int}/songs")]
    public async Task<IActionResult> GetSongs(int id)
    {
        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        var songs = await _playlists.GetSongsAsync(id);
        return Ok(songs);
    }

    [HttpPost("{id:int}/songs")]
    public async Task<IActionResult> AddSong(int id, [FromBody] PlaylistSongRequest req)
    {
        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        var song = new Song
        {
            SpotifyUri = req.SpotifyUri,
            Title      = req.Title,
            Artist     = req.Artist,
            CoverUrl   = req.CoverUrl ?? "",
            DurationMs = req.DurationMs,
        };

        var added = await _playlists.AddSongAsync(id, song);
        if (!added)
            return Conflict(new { error = "La canción ya está en la lista" });

        // If this playlist is active, append to background playlist in memory
        if (playlist.IsActive)
        {
            var services = _userContext.GetOrCreate(LocalUser.Id);
            var (songs, index) = services.Queue.GetBackgroundPlaylist();
            songs.Add(song);
            services.Queue.SetBackgroundPlaylist(songs);
        }

        await BroadcastStatusAsync();
        return NoContent();
    }

    [HttpPut("{id:int}/songs/reorder")]
    public async Task<IActionResult> ReorderSong(int id, [FromBody] PlaylistSongReorderRequest req)
    {
        var ok = await _playlists.ReorderSongAsync(id, req.SpotifyUri, req.ToIndex);
        if (!ok) return NotFound();

        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist?.IsActive == true)
            await ReloadBackgroundPlaylistAsync(id);

        await BroadcastStatusAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}/songs/{uri}")]
    public async Task<IActionResult> RemoveSong(int id, string uri)
    {
        var spotifyUri = HttpUtility.UrlDecode(uri);
        var ok = await _playlists.RemoveSongAsync(id, spotifyUri);
        if (!ok) return NotFound();

        // If active, reload background playlist from DB
        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist?.IsActive == true)
            await ReloadBackgroundPlaylistAsync(id);

        await BroadcastStatusAsync();
        return NoContent();
    }

    // ── Import ────────────────────────────────────────────────────────────────

    [HttpPost("{id:int}/import")]
    public async Task<IActionResult> Import(int id, [FromBody] PlaylistImportRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "URL requerida" });

        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        List<Song> tracks;
        try
        {
            tracks = await _downloader.ImportPlaylistAsync(req.Url, 500);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        if (tracks.Count == 0)
            return BadRequest(new { error = "No se encontraron canciones en esa URL" });

        var added = await _playlists.BulkAddAsync(id, tracks);

        // If this playlist is active, reload the background playlist
        if (playlist.IsActive)
            await ReloadBackgroundPlaylistAsync(id);

        await BroadcastStatusAsync();
        return Ok(new { added, skipped = tracks.Count - added, total = tracks.Count });
    }

    // ── Playback ──────────────────────────────────────────────────────────────

    [HttpPost("{id:int}/play")]
    public async Task<IActionResult> Play(int id, [FromBody] PlaylistPlayRequest? req)
    {
        var playlist = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        var dbSongs = await _playlists.GetSongsAsync(id);
        if (dbSongs.Count == 0)
            return BadRequest(new { error = "La lista está vacía" });

        var songs = await MapToSongsAsync(dbSongs);

        if (req?.Shuffle == true)
            Shuffle(songs);

        var services = _userContext.GetOrCreate(LocalUser.Id);
        services.Queue.SetBackgroundPlaylist(songs, playlist.Name);
        await _playlists.SetActiveAsync(id);

        // If the queue is idle start playback from the background playlist
        if (services.Queue.GetCurrentItem() == null)
        {
            services.Queue.Advance();
            _ = Task.Run(() => _sync.StartCurrentTrackAsync(services));
        }

        await BroadcastStatusAsync();
        return Ok(new { message = $"Reproduciendo \"{playlist.Name}\"" });
    }

    /// <summary>
    /// Play a specific song from the playlist and load the remaining songs
    /// (from that position onwards, cyclically) into the background queue.
    /// </summary>
    [HttpPost("{id:int}/songs/{uri}/play")]
    public async Task<IActionResult> PlaySong(int id, string uri, [FromBody] PlaySongRequest? req)
    {
        var spotifyUri = HttpUtility.UrlDecode(uri);
        var playlist   = await _playlists.GetByIdAsync(id);
        if (playlist == null) return NotFound();

        var dbSongs = await _playlists.GetSongsAsync(id);
        if (dbSongs.Count == 0) return BadRequest(new { error = "La lista está vacía" });

        var songs      = await MapToSongsAsync(dbSongs);
        var clickedIdx = songs.FindIndex(s => s.SpotifyUri == spotifyUri);
        if (clickedIdx < 0) return NotFound(new { error = "Canción no encontrada en la lista" });

        var clickedSong = songs[clickedIdx];

        // Build the remaining songs: from (clickedIdx+1) cycling to clickedIdx-1
        var remaining = new List<Song>(songs.Count - 1);
        for (int i = 1; i < songs.Count; i++)
            remaining.Add(songs[(clickedIdx + i) % songs.Count]);

        if (req?.Shuffle == true)
            Shuffle(remaining);

        var services = _userContext.GetOrCreate(LocalUser.Id);
        services.Queue.SetBackgroundPlaylist(remaining, playlist.Name);
        services.Queue.PlayNow(clickedSong, "Playlist", "web", isPlaylistItem: true);
        _ = Task.Run(() => _sync.StartCurrentTrackAsync(services));

        await _playlists.SetActiveAsync(id);
        await BroadcastStatusAsync();

        return Ok(new { message = $"Reproduciendo \"{clickedSong.Title}\"" });
    }

    [HttpDelete("active")]
    public async Task<IActionResult> Deactivate()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        services.Queue.ClearBackgroundPlaylist();
        await _playlists.SetActiveAsync(null);
        await BroadcastStatusAsync();
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<List<Song>> MapToSongsAsync(List<PlaylistLibrarySong> dbSongs)
    {
        var songs = new List<Song>(dbSongs.Count);
        foreach (var s in dbSongs)
        {
            var song = new Song
            {
                SpotifyUri = s.SpotifyUri,
                Title      = s.Title,
                Artist     = s.Artist,
                CoverUrl   = s.CoverUrl,
                DurationMs = s.DurationMs,
            };
            // Check local cache so pre-warm skips already-downloaded files
            var cached = await _library.FindByTrackIdAsync(s.SpotifyUri);
            if (cached != null && SysFile.Exists(cached.FilePath)
                && new SysFileInfo(cached.FilePath).Length > 100_000)
            {
                song.LocalFilePath = cached.FilePath;
            }
            songs.Add(song);
        }
        return songs;
    }

    private async Task ReloadBackgroundPlaylistAsync(int playlistId)
    {
        var playlist = await _playlists.GetByIdAsync(playlistId);
        var dbSongs  = await _playlists.GetSongsAsync(playlistId);
        var songs    = await MapToSongsAsync(dbSongs);
        var services = _userContext.GetOrCreate(LocalUser.Id);
        services.Queue.SetBackgroundPlaylist(songs, playlist?.Name);
    }

    private Task BroadcastStatusAsync() =>
        _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("playlist:status", new { });

    private static void Shuffle<T>(List<T> list)
    {
        var rng = new Random();
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

// ── Request models ────────────────────────────────────────────────────────────

public record PlaylistCreateRequest(string Name);
public record PlaylistSongRequest(string SpotifyUri, string Title, string Artist, string? CoverUrl, int DurationMs);
public record PlaylistImportRequest(string Url);
public record PlaylistPlayRequest(bool Shuffle = false);
public record PlaySongRequest(bool Shuffle = false);
public record PinReorderRequest(List<int> Ids);
public record PlaylistSongReorderRequest(string SpotifyUri, int ToIndex);
