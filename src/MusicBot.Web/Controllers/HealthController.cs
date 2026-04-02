using Microsoft.AspNetCore.Mvc;
using MusicBot.Services;

namespace MusicBot.Controllers;

[ApiController]
[Route("health")]
[Tags("Health")]
public class HealthController : ControllerBase
{
    private readonly UserContextManager _userContext;

    public HealthController(UserContextManager userContext)
    {
        _userContext = userContext;
    }

    /// <summary>Basic health check</summary>
    /// <remarks>Returns OK if the server is running. No authentication required.</remarks>
    [HttpGet]
    [ProducesResponseType(typeof(HealthResponse), 200)]
    public IActionResult Get() => Ok(new HealthResponse { Status = "ok" });

    /// <summary>Health check with queue and Spotify status</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserHealthResponse), 200)]
    public IActionResult GetUserHealth()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        return Ok(new UserHealthResponse
        {
            Status      = "ok",
            Spotify     = services.Spotify.IsAuthenticated,
            QueueLength = services.Queue.QueueLength
        });
    }
}

public class HealthResponse
{
    public string Status { get; set; } = "ok";
}

public class UserHealthResponse
{
    public string Status { get; set; } = "ok";
    public bool Spotify { get; set; }
    public int QueueLength { get; set; }
}
