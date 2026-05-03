using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicBot.Data;

namespace MusicBot.Controllers;

public record CreateSupportTicketBody(string Title, string Description, string Category);

[ApiController]
[Route("api/support")]
[Tags("Support")]
public class SupportController : ControllerBase
{
    private readonly MusicBotDbContext _db;
    public SupportController(MusicBotDbContext db) => _db = db;

    [HttpGet("tickets")]
    public async Task<IActionResult> GetAll()
    {
        var tickets = await _db.SupportTickets
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
        return Ok(tickets);
    }

    [HttpPost("tickets")]
    public async Task<IActionResult> Create([FromBody] CreateSupportTicketBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Title))
            return BadRequest(new { error = "El título es obligatorio" });
        if (string.IsNullOrWhiteSpace(body.Description))
            return BadRequest(new { error = "La descripción es obligatoria" });

        var ticket = new SupportTicket
        {
            Title       = body.Title.Trim(),
            Description = body.Description.Trim(),
            Category    = body.Category?.Trim() is { Length: > 0 } c ? c : "general",
            Status      = "open",
            CreatedAt   = DateTime.UtcNow,
        };
        _db.SupportTickets.Add(ticket);
        await _db.SaveChangesAsync();
        return Created($"/api/support/tickets/{ticket.Id}", ticket);
    }

    [HttpDelete("tickets/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ticket = await _db.SupportTickets.FindAsync(id);
        if (ticket is null) return NotFound();
        _db.SupportTickets.Remove(ticket);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
