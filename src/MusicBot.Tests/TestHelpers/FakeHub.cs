using Microsoft.AspNetCore.SignalR;
using MusicBot.Hubs;

namespace MusicBot.Tests.TestHelpers;

/// <summary>
/// Stub de IHubContext que descarta todos los mensajes SignalR.
/// Permite construir servicios que dependen del hub sin infraestructura real.
/// </summary>
internal sealed class FakeHub : IHubContext<OverlayHub>
{
    public static readonly FakeHub Instance = new();
    public IHubClients Clients { get; } = FakeClients.Instance;
    public IGroupManager Groups { get; } = FakeGroupManager.Instance;
}

internal sealed class FakeClients : IHubClients
{
    public static readonly FakeClients Instance = new();
    private static readonly FakeClientProxy _proxy = new();

    public IClientProxy All                                                                       => _proxy;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds)                   => _proxy;
    public IClientProxy Client(string connectionId)                                               => _proxy;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds)                             => _proxy;
    public IClientProxy Group(string groupName)                                                   => _proxy;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
    public IClientProxy Groups(IReadOnlyList<string> groupNames)                                 => _proxy;
    public IClientProxy User(string userId)                                                       => _proxy;
    public IClientProxy Users(IReadOnlyList<string> userIds)                                     => _proxy;
}

internal sealed class FakeClientProxy : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal sealed class FakeGroupManager : IGroupManager
{
    public static readonly FakeGroupManager Instance = new();
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
