using MusicBot;
using Serilog;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile(
        $"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json",
        optional: true)
    .AddEnvironmentVariables()
    .Build();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(config)
    .Enrich.FromLogContext()
    .CreateLogger();

try
{
    Log.Information("Iniciando MusicBot Web");
    var builder = WebHost.CreateBuilder(args);
    var app = WebHost.Configure(builder);
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "La aplicación terminó de forma inesperada");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
