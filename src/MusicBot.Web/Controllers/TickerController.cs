using Microsoft.AspNetCore.Mvc;
using MusicBot.Services;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/ticker")]
[Tags("Ticker")]
public class TickerController : ControllerBase
{
    private readonly TickerMessageService _ticker;
    public TickerController(TickerMessageService ticker) => _ticker = ticker;

    [HttpGet]
    public IActionResult GetAll() => Ok(_ticker.GetAll());

    [HttpPost]
    [ProducesResponseType(typeof(TickerMessage), 201)]
    public IActionResult Create([FromBody] TickerMessage msg)
    {
        var created = _ticker.Add(msg);
        return CreatedAtAction(nameof(GetAll), created);
    }

    [HttpPut("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public IActionResult Update(string id, [FromBody] TickerMessage msg)
    {
        return _ticker.Update(id, msg) ? NoContent() : NotFound();
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public IActionResult Delete(string id)
    {
        return _ticker.Delete(id) ? NoContent() : NotFound();
    }
}
