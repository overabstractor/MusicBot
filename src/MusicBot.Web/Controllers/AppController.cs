using Microsoft.AspNetCore.Mvc;
using MusicBot.Services;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/app")]
[Tags("App")]
public class AppController : ControllerBase
{
    private readonly UserContextManager _userContext;
    private readonly IHostApplicationLifetime _lifetime;

    public AppController(UserContextManager userContext, IHostApplicationLifetime lifetime)
    {
        _userContext = userContext;
        _lifetime   = lifetime;
    }

    /// <summary>Shutdown the application — stops playback first</summary>
    [HttpPost("shutdown")]
    public async Task<IActionResult> Shutdown()
    {
        // Stop playback before shutting down
        try
        {
            var services = _userContext.GetOrCreate(LocalUser.Id);
            if (services.Player.IsPlaying)
                await services.Player.StopAsync();
        }
        catch { /* best effort */ }

        // Request graceful shutdown
        _lifetime.StopApplication();
        return Ok(new { status = "shutting_down" });
    }

    /// <summary>Open the log viewer window (Desktop only — fires a static event)</summary>
    [HttpPost("open-log")]
    public IActionResult OpenLog()
    {
        AppEvents.RequestOpenLog();
        return Ok(new { status = "ok" });
    }
}

