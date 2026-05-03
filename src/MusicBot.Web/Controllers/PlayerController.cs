using Microsoft.AspNetCore.Mvc;
using MusicBot.Data;
using MusicBot.Services;
using NAudio.CoreAudioApi;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/player")]
[Tags("Player")]
public class PlayerController : ControllerBase
{
    private readonly UserContextManager _userContext;
    private readonly MusicBotDbContext  _db;

    public PlayerController(UserContextManager userContext, MusicBotDbContext db)
    {
        _userContext = userContext;
        _db          = db;
    }

    /// <summary>Set playback volume (0.0 – 1.0)</summary>
    [HttpPost("volume")]
    [ProducesResponseType(204)]
    public IActionResult SetVolume([FromBody] VolumeRequest request)
    {
        _userContext.GetOrCreate(LocalUser.Id).Player.SetVolume(request.Volume);
        return NoContent();
    }

    /// <summary>Seek to a position in the current track</summary>
    [HttpPost("seek")]
    [ProducesResponseType(204)]
    public IActionResult Seek([FromBody] SeekRequest request)
    {
        _userContext.GetOrCreate(LocalUser.Id).Player.SeekTo(request.PositionMs);
        return NoContent();
    }

    /// <summary>List active WASAPI audio output devices. activeDeviceId is the device currently in use (null = system default).</summary>
    [HttpGet("devices")]
    public IActionResult GetDevices()
    {
        var player    = _userContext.GetOrCreate(LocalUser.Id).Player;
        var activeId  = player.DeviceId;
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new { id = d.ID, name = d.FriendlyName, isDefault = d.ID == defaultId })
            .ToList();
        return Ok(new { activeDeviceId = activeId, devices });
    }

    /// <summary>Set audio output device (null = system default) and persist the choice.</summary>
    [HttpPost("device")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> SetDevice([FromBody] SetDeviceRequest request)
    {
        await _userContext.GetOrCreate(LocalUser.Id).Player.SetDeviceAsync(request.DeviceId);
        var user = await _db.Users.FindAsync(LocalUser.Id);
        if (user != null)
        {
            user.AudioDeviceId = request.DeviceId;
            await _db.SaveChangesAsync();
        }
        return NoContent();
    }
}

public class VolumeRequest    { public float   Volume     { get; set; } }
public class SeekRequest      { public int     PositionMs { get; set; } }
public class SetDeviceRequest { public string? DeviceId   { get; set; } }
