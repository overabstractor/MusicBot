namespace MusicBot.Services.Platforms;

public class TwitchSettings
{
    /// <summary>Twitch application Client ID (from dev.twitch.tv/console)</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Twitch application Client Secret</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>OAuth redirect URI (must match the one registered in Twitch dev console)</summary>
    public string RedirectUri { get; set; } = "http://127.0.0.1:3050/api/auth/twitch/callback";

    /// <summary>Twitch channel name to listen to (e.g. "mychannel")</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>MusicBot user slug that owns this Twitch channel</summary>
    public string UserSlug { get; set; } = string.Empty;

    /// <summary>OAuth is configured if ClientId and ClientSecret are set</summary>
    public bool OAuthConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(Channel) &&
        !string.IsNullOrWhiteSpace(UserSlug);
}
