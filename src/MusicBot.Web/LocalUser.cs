namespace MusicBot;

/// <summary>
/// Fixed identity for the single local user.
/// All controllers use this ID instead of reading from JWT claims.
/// </summary>
public static class LocalUser
{
    public static readonly Guid Id           = new("00000000-0000-0000-0000-000000000001");
    public const           string Username   = "local";
    public const           string Slug       = "local";
    public const           string OverlayToken = "local";
}
