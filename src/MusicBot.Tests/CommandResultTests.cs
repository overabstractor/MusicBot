using MusicBot.Core.Models;
using Xunit;

namespace MusicBot.Tests;

public class CommandResultTests
{
    [Fact]
    public void Ok_SetsSuccessTrueAndMessage()
    {
        var result = CommandResult.Ok("Canción agregada");
        Assert.True(result.Success);
        Assert.Equal("Canción agregada", result.Message);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Ok_WithData_SetsDataProperty()
    {
        var data   = new { title = "test" };
        var result = CommandResult.Ok("ok", data);

        Assert.True(result.Success);
        Assert.Same(data, result.Data);
    }

    [Fact]
    public void Fail_SetsSuccessFalseAndMessage()
    {
        var result = CommandResult.Fail("Error al buscar canción");
        Assert.False(result.Success);
        Assert.Equal("Error al buscar canción", result.Message);
    }

    [Fact]
    public void Fail_DataIsAlwaysNull()
    {
        var result = CommandResult.Fail("error");
        Assert.Null(result.Data);
    }
}
