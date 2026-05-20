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
        loudnessNormalizationEnabled    = _settings.LoudnessNormalizationEnabled,
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
            req.AutoQueueEnabled,
            req.LoudnessNormalizationEnabled);

        // Persist BOTH Queue and Desktop sections to %AppData%\MusicBot\appsettings.user.json.
        // That file is loaded as a higher-priority config source (see WebHost.cs), so values
        // here override the bundled appsettings.json on next startup. It survives both dotnet
        // rebuilds (which overwrite bin/appsettings.json) and Velopack updates.
        // Keys must match exactly what QueueSettingsService reads in its ctor.
        var userConfigPath = ResolveUserConfigPath();
        PersistUserConfigSection(userConfigPath, "Queue", new Dictionary<string, object?>
        {
            ["MaxSize"]                      = req.MaxQueueSize,
            ["MaxSongsPerUser"]              = req.MaxSongsPerUser,
            ["VotingEnabled"]                = req.VotingEnabled,
            ["PresenceCheckEnabled"]         = req.PresenceCheckEnabled,
            ["PresenceCheckWarningSeconds"]  = req.PresenceCheckWarningSeconds,
            ["PresenceCheckConfirmSeconds"]  = req.PresenceCheckConfirmSeconds,
            ["SaveDownloads"]                = req.SaveDownloads,
            ["AutoQueueEnabled"]             = req.AutoQueueEnabled,
            ["LoudnessNormalizationEnabled"] = req.LoudnessNormalizationEnabled,
        });
        PersistUserConfigSection(userConfigPath, "Desktop", new Dictionary<string, object?>
        {
            ["OpenLogOnStart"] = req.OpenLogOnStart,
        });

        return NoContent();
    }

    private string ResolveUserConfigPath()
    {
        // Set by WebHost.cs at startup. Fall back to %AppData%\MusicBot\ if missing
        // (covers test scenarios where the config key isn't injected).
        var dataDir = _config["DataDirectory"];
        if (string.IsNullOrWhiteSpace(dataDir))
            dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MusicBot");
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, "appsettings.user.json");
    }

    /// <summary>
    /// Writes the given key/value pairs into the named top-level section of the user
    /// config file, preserving every other section as-is. Creates the file if missing.
    /// Best effort — failures (read-only file, parse error) are swallowed so a UI save
    /// never errors out; in-memory state is already updated and broadcast.
    /// </summary>
    private static void PersistUserConfigSection(string path, string sectionName, IDictionary<string, object?> updates)
    {
        try
        {
            var dict = new Dictionary<string, object?>();
            bool sectionFound = false;

            if (System.IO.File.Exists(path))
            {
                var json = System.IO.File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name == sectionName)
                        {
                            sectionFound = true;
                            var section = new Dictionary<string, object?>();
                            foreach (var dp in prop.Value.EnumerateObject())
                                section[dp.Name] = JsonElementToObject(dp.Value);
                            foreach (var kv in updates) section[kv.Key] = kv.Value;
                            dict[prop.Name] = section;
                        }
                        else
                        {
                            dict[prop.Name] = JsonElementToObject(prop.Value);
                        }
                    }
                }
            }

            if (!sectionFound)
                dict[sectionName] = new Dictionary<string, object?>(updates);

            var output = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            // Write to a temp file then move, so a partial write never corrupts the user config.
            var tempPath = path + ".tmp";
            System.IO.File.WriteAllText(tempPath, output);
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.File.Move(tempPath, path);
        }
        catch { /* best effort — in-memory state is already updated */ }
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => el.GetBoolean(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Null   => null,
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        JsonValueKind.Array  => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        _ => el.Clone(),
    };
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
    public bool   LoudnessNormalizationEnabled     { get; set; } = true;
    public bool   OpenLogOnStart                   { get; set; } = false;
}
