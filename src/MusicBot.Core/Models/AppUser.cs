namespace MusicBot.Core.Models;

public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string OverlayToken { get; set; } = Guid.NewGuid().ToString("N");
    public string? AudioDeviceId { get; set; }
    public float?  AudioVolume   { get; set; }

    public List<UserApiKey> ApiKeys { get; set; } = new();
    public SpotifyToken? SpotifyToken { get; set; }
}
