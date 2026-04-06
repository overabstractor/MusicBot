using Microsoft.AspNetCore.Mvc;
using MusicBot.Services;
using MusicBot.Services.Downloader;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/app")]
[Tags("App")]
public class AppController : ControllerBase
{
    private readonly UserContextManager      _userContext;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly YtDlpDownloaderService  _downloader;

    public AppController(
        UserContextManager userContext,
        IHostApplicationLifetime lifetime,
        YtDlpDownloaderService downloader)
    {
        _userContext = userContext;
        _lifetime    = lifetime;
        _downloader  = downloader;
    }

    /// <summary>Shutdown the application — stops playback first, then signals the Desktop layer to exit</summary>
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

        // Signal the WPF layer to perform a full exit (not minimize to tray)
        AppEvents.RequestShutdown();
        return Ok(new { status = "shutting_down" });
    }

    /// <summary>Open the log viewer window (Desktop only — fires a static event)</summary>
    [HttpPost("open-log")]
    public IActionResult OpenLog()
    {
        AppEvents.RequestOpenLog();
        return Ok(new { status = "ok" });
    }

    /// <summary>Open the logs directory in Explorer (Desktop only — fires a static event)</summary>
    [HttpPost("open-log-dir")]
    public IActionResult OpenLogDir()
    {
        AppEvents.RequestOpenLogDir();
        return Ok(new { status = "ok" });
    }

    /// <summary>Returns the current installed version of the application</summary>
    [HttpGet("version")]
    public IActionResult GetVersion() => Ok(new { version = AppInfo.Version });

    /// <summary>Downloads the latest yt-dlp release from GitHub, replacing the current binary</summary>
    [HttpPost("yt-dlp/update")]
    public async Task<IActionResult> UpdateYtDlp()
    {
        try
        {
            var version = await _downloader.UpdateYtDlpAsync();
            return Ok(new { version, message = $"yt-dlp actualizado a {version}" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

}

