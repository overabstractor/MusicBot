namespace MusicBot.Services.Platforms;

public class KickSettings
{
    /// <summary>Kick channel name to listen to (e.g. "mychannel")</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>MusicBot user slug that owns this Kick channel</summary>
    public string UserSlug { get; set; } = string.Empty;

    // OAuth2 app credentials (from Kick Developer Portal)
    public string ClientId     { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri  { get; set; } = "http://localhost:3050/api/auth/kick/callback";

    public bool OAuthConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(Channel) &&
        !string.IsNullOrWhiteSpace(UserSlug);
}
