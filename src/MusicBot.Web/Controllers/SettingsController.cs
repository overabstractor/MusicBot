using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MusicBot.Services;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/settings")]
[Tags("Settings")]
public class SettingsController : ControllerBase
{
    private readonly QueueSettingsService _settings;
    private readonly UserContextManager   _userContext;
    private readonly IConfiguration       _config;

    public SettingsController(QueueSettingsService settings, UserContextManager userContext, IConfiguration config)
    {
        _settings    = settings;
        _userContext = userContext;
        _config      = config;
    }

    /// <summary>Get current queue and voting settings</summary>
    [HttpGet]
    public IActionResult GetSettings() => Ok(new
    {
        maxQueueSize                    = _settings.MaxQueueSize,
        maxSongsPerUser                 = _settings.MaxSongsPerUser,
        votingEnabled                   = _settings.VotingEnabled,
        presenceCheckEnabled            = _settings.PresenceCheckEnabled,
        presenceCheckWarningSeconds     = _settings.PresenceCheckWarningSeconds,
        presenceCheckConfirmSeconds     = _settings.PresenceCheckConfirmSeconds,
        saveDownloads                   = _settings.SaveDownloads,
        autoQueueEnabled                = _settings.AutoQueueEnabled,
        openLogOnStart                  = _config.GetValue<bool>("Desktop:OpenLogOnStart", false),
    });

    /// <summary>Update queue and voting settings</summary>
    [HttpPut]
    [ProducesResponseType(204)]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsRequest req)
    {
        _userContext.GetOrCreate(LocalUser.Id).Queue.UpdateLimits(req.MaxQueueSize, req.MaxSongsPerUser);
        await _settings.UpdateAsync(
            req.MaxQueueSize,
            req.MaxSongsPerUser,
            req.VotingEnabled,
            req.PresenceCheckEnabled,
            req.PresenceCheckWarningSeconds,
            req.PresenceCheckConfirmSeconds,
            req.SaveDownloads,
            req.AutoQueueEnabled);

        // Persist Desktop:OpenLogOnStart to appsettings.json
        PersistDesktopSetting("OpenLogOnStart", req.OpenLogOnStart);

        return NoContent();
    }

    private static void PersistDesktopSetting(string key, bool value)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!System.IO.File.Exists(path)) return;

            var json = System.IO.File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var dict = new Dictionary<string, object?>();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "Desktop")
                {
                    var desktop = new Dictionary<string, object>();
                    foreach (var dp in prop.Value.EnumerateObject())
                        desktop[dp.Name] = dp.Value.ValueKind == JsonValueKind.True || dp.Value.ValueKind == JsonValueKind.False
                            ? dp.Value.GetBoolean() : dp.Value.Clone();
                    desktop[key] = value;
                    dict[prop.Name] = desktop;
                }
                else
                {
                    dict[prop.Name] = prop.Value.Clone();
                }
            }

            if (!dict.ContainsKey("Desktop"))
                dict["Desktop"] = new Dictionary<string, object> { [key] = value };

            var output = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(path, output);
        }
        catch { /* best effort */ }
    }
}

public class UpdateSettingsRequest
{
    public int    MaxQueueSize                     { get; set; } = 50;
    public int    MaxSongsPerUser                  { get; set; } = 10;
    public bool   VotingEnabled                    { get; set; } = false;
    public bool   PresenceCheckEnabled             { get; set; } = false;
    public int    PresenceCheckWarningSeconds      { get; set; } = 30;
    public int    PresenceCheckConfirmSeconds      { get; set; } = 30;
    public bool   SaveDownloads                    { get; set; } = false;
    public bool   AutoQueueEnabled                 { get; set; } = false;
    public bool   OpenLogOnStart                   { get; set; } = false;
}
