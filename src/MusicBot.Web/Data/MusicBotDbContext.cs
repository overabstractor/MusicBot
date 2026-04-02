using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Models;

namespace MusicBot.Data;

public class MusicBotDbContext : DbContext
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserApiKey> ApiKeys => Set<UserApiKey>();
    public DbSet<SpotifyToken> SpotifyTokens => Set<SpotifyToken>();
    public DbSet<PlatformConfig> PlatformConfigs => Set<PlatformConfig>();
    public DbSet<CachedTrack> CachedTracks => Set<CachedTrack>();
    public DbSet<PersistedQueueItem> PersistedQueueItems => Set<PersistedQueueItem>();
    public DbSet<PlayedSong>         PlayedSongs         => Set<PlayedSong>();
    public DbSet<AutoQueueSong>      AutoQueueSongs      => Set<AutoQueueSong>();

    public MusicBotDbContext(DbContextOptions<MusicBotDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.Slug).IsUnique();
            e.HasIndex(u => u.OverlayToken).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<UserApiKey>(e =>
        {
            e.HasKey(k => k.Id);
            e.HasIndex(k => k.KeyPrefix);
            e.HasOne(k => k.User).WithMany(u => u.ApiKeys).HasForeignKey(k => k.UserId);
        });

        modelBuilder.Entity<SpotifyToken>(e =>
        {
            e.HasKey(t => t.UserId);
            e.HasOne(t => t.User).WithOne(u => u.SpotifyToken).HasForeignKey<SpotifyToken>(t => t.UserId);
        });

        modelBuilder.Entity<PlatformConfig>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => new { p.UserId, p.Platform }).IsUnique();
        });

        modelBuilder.Entity<CachedTrack>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.TrackId).IsUnique();
        });

        modelBuilder.Entity<PersistedQueueItem>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => new { p.UserId, p.Position });
        });

        modelBuilder.Entity<PlayedSong>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.PlayedAt);
        });

        modelBuilder.Entity<AutoQueueSong>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.SpotifyUri).IsUnique();
        });
    }
}
