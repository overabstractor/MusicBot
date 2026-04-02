namespace MusicBot.Core.Models;

public class SpotifyToken
{
    public Guid UserId { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }

    public AppUser User { get; set; } = null!;
}
