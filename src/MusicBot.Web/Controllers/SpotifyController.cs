using Microsoft.AspNetCore.Mvc;
using MusicBot.Services;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/spotify")]
[Tags("Spotify")]
public class SpotifyController : ControllerBase
{
    private readonly UserContextManager _userContext;

    public SpotifyController(UserContextManager userContext)
    {
        _userContext = userContext;
    }

    /// <summary>Returns the Spotify access token (for users with Spotify connected).</summary>
    [HttpGet("token")]
    [ProducesResponseType(typeof(SpotifyTokenResponse), 200)]
    public async Task<IActionResult> GetToken()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        if (!services.Spotify.IsAuthenticated)
            return Unauthorized(new { error = "Spotify not connected" });

        var token = await services.Spotify.GetAccessTokenAsync();
        return Ok(new SpotifyTokenResponse(token));
    }
}

public record SpotifyTokenResponse(string AccessToken);
