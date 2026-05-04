using MusicBot.Data;

namespace MusicBot.Services.Player;

/// <summary>
/// Restores the previously selected audio output device after startup.
/// </summary>
public class AudioDeviceRestoreService : IHostedService
{
    private readonly UserContextManager _userContext;
    private readonly IServiceScopeFactory _scopeFactory;

    public AudioDeviceRestoreService(UserContextManager userContext, IServiceScopeFactory scopeFactory)
    {
        _userContext  = userContext;
        _scopeFactory = scopeFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db   = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var user = await db.Users.FindAsync([LocalUser.Id], cancellationToken);
        var player = _userContext.GetOrCreate(LocalUser.Id).Player;
        if (user?.AudioDeviceId != null)
            await player.SetDeviceAsync(user.AudioDeviceId);
        if (user?.AudioVolume is float vol)
            player.SetVolume(vol);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
