namespace MusicBot.Core.Models;

public class CommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }

    public static CommandResult Ok(string message, object? data = null) =>
        new() { Success = true, Message = message, Data = data };

    public static CommandResult Fail(string message) =>
        new() { Success = false, Message = message };
}
