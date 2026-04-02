using Microsoft.AspNetCore.Mvc;
using MusicBot.Services;
using NAudio.CoreAudioApi;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/player")]
[Tags("Player")]
public class PlayerController : ControllerBase
{
    private readonly UserContextManager _userContext;

    public PlayerController(UserContextManager userContext) => _userContext = userContext;

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

    /// <summary>List active WASAPI audio output devices</summary>
    [HttpGet("devices")]
    public IActionResult GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new { id = d.ID, name = d.FriendlyName, isDefault = d.ID == defaultId })
            .ToList();
        return Ok(devices);
    }

    /// <summary>Set audio output device (null = system default)</summary>
    [HttpPost("device")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> SetDevice([FromBody] SetDeviceRequest request)
    {
        await _userContext.GetOrCreate(LocalUser.Id).Player.SetDeviceAsync(request.DeviceId);
        return NoContent();
    }
}

public class VolumeRequest    { public float   Volume     { get; set; } }
public class SeekRequest      { public int     PositionMs { get; set; } }
public class SetDeviceRequest { public string? DeviceId   { get; set; } }
