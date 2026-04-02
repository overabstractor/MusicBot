namespace MusicBot.Services;

/// <summary>
/// Holds the last-known connection status for platform services
/// so the OverlayHub can send it to newly-connected clients.
/// </summary>
public class IntegrationStatusTracker
{
    public string TikTokStatus  { get; set; } = "disconnected";
    public string TwitchStatus  { get; set; } = "disconnected";
    public string KickStatus    { get; set; } = "disconnected";
}
