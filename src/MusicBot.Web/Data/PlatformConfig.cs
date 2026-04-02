namespace MusicBot.Data;

public class PlatformConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Platform { get; set; } = string.Empty; // "tiktok" | "twitch" | "kick"
    public string ConfigJson { get; set; } = "{}";
    public bool AutoConnect { get; set; } = false;
}
