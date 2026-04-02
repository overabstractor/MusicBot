using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MusicBot.Services;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/relay")]
[Tags("Relay")]
public class RelayController : ControllerBase
{
    private readonly RelaySettings _relay;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<RelayController> _logger;

    public RelayController(IOptions<RelaySettings> relay, IHttpClientFactory httpFactory, ILogger<RelayController> logger)
    {
        _relay      = relay.Value;
        _httpFactory = httpFactory;
        _logger     = logger;
    }

    /// <summary>Check relay configuration and connectivity</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        if (string.IsNullOrWhiteSpace(_relay.Url) || string.IsNullOrWhiteSpace(_relay.ApiKey))
            return Ok(new { configured = false, reachable = false, error = (string?)null });

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{_relay.Url.TrimEnd('/')}/ping");
            req.Headers.Add("X-Relay-Key", _relay.ApiKey);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var res = await _httpFactory.CreateClient().SendAsync(req, cts.Token);

            if (res.IsSuccessStatusCode)
                return Ok(new { configured = true, reachable = true, error = (string?)null });

            var body = await res.Content.ReadAsStringAsync();
            _logger.LogWarning("Relay ping returned {Status}: {Body}", (int)res.StatusCode, body);
            return Ok(new { configured = true, reachable = false, error = $"HTTP {(int)res.StatusCode}" });
        }
        catch (OperationCanceledException)
        {
            return Ok(new { configured = true, reachable = false, error = "Timeout (5s)" });
        }
        catch (Exception ex)
        {
            return Ok(new { configured = true, reachable = false, error = ex.Message });
        }
    }
}
