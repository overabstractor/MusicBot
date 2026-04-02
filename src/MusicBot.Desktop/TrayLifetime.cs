using Microsoft.Extensions.Hosting;

namespace MusicBot.Desktop;

/// <summary>
/// Replaces ConsoleLifetime so the host never allocates a console window.
/// Application lifetime is controlled by the WPF message loop instead.
/// </summary>
internal sealed class TrayLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken)         => Task.CompletedTask;
}
