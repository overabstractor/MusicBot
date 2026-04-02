using MusicBot.Core.Models;

namespace MusicBot.Core.Interfaces;

public interface IChatAdapter
{
    string PlatformName { get; }
    Task ConnectAsync();
    Task DisconnectAsync();
    event Func<BotCommand, Task>? OnCommand;
}
