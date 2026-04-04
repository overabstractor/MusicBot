using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using MusicBot.Core.Models;
using MusicBot.Hubs;
using MusicBot.Services;
using MusicBot.Data;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/liked")]
[Tags("Liked Songs")]
public class LikedController : ControllerBase
{
    private readonly PlaylistLibraryService  _playlists;
    private readonly IHubContext<OverlayHub> _hub;

    public LikedController(PlaylistLibraryService playlists, IHubContext<OverlayHub> hub)
    {
        _playlists = playlists;
        _hub       = hub;
    }

    private Task BroadcastAsync() =>
        _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("playlist:status", new { });

    /// <summary>Returns all Spotify URIs the user has liked.</summary>
    [HttpGet("uris")]
    public async Task<IActionResult> GetLikedUris()
        => Ok(await _playlists.GetLikedUrisAsync());

    /// <summary>Toggles a song in/out of Liked Songs. Returns the new liked state.</summary>
    [HttpPost("toggle")]
    public async Task<IActionResult> Toggle([FromBody] LikedToggleRequest req)
    {
        var likedId = await _playlists.GetLikedPlaylistIdAsync();
        if (likedId == null) return BadRequest(new { error = "Liked Songs playlist not found" });

        var song = new Song
        {
            SpotifyUri = req.SpotifyUri,
            Title      = req.Title,
            Artist     = req.Artist,
            CoverUrl   = req.CoverUrl ?? "",
            DurationMs = req.DurationMs,
        };

        var isIn = await _playlists.IsSongInPlaylistAsync(likedId.Value, req.SpotifyUri);
        if (isIn)
        {
            await _playlists.RemoveSongAsync(likedId.Value, req.SpotifyUri);
            await BroadcastAsync();
            return Ok(new { isLiked = false });
        }
        await _playlists.AddSongAsync(likedId.Value, song);
        await BroadcastAsync();
        return Ok(new { isLiked = true });
    }

    /// <summary>Returns all playlists with a flag indicating whether this song is saved in each one.</summary>
    [HttpGet("memberships")]
    public async Task<IActionResult> GetMemberships([FromQuery] string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return BadRequest(new { error = "uri requerido" });
        var memberships = await _playlists.GetSongMembershipsAsync(uri);
        return Ok(memberships);
    }

    /// <summary>Toggles a song in/out of the given playlist. Returns the new membership state.</summary>
    [HttpPost("memberships/{playlistId:int}")]
    public async Task<IActionResult> ToggleMembership(int playlistId, [FromBody] LikedToggleRequest req)
    {
        var playlist = await _playlists.GetByIdAsync(playlistId);
        if (playlist == null) return NotFound();

        var song = new Song
        {
            SpotifyUri = req.SpotifyUri,
            Title      = req.Title,
            Artist     = req.Artist,
            CoverUrl   = req.CoverUrl ?? "",
            DurationMs = req.DurationMs,
        };

        var isIn = await _playlists.IsSongInPlaylistAsync(playlistId, req.SpotifyUri);
        if (isIn)
        {
            await _playlists.RemoveSongAsync(playlistId, req.SpotifyUri);
            await BroadcastAsync();
            return Ok(new { isInPlaylist = false });
        }
        await _playlists.AddSongAsync(playlistId, song);
        await BroadcastAsync();
        return Ok(new { isInPlaylist = true });
    }
}

public record LikedToggleRequest(string SpotifyUri, string Title, string Artist, string? CoverUrl, int DurationMs);
