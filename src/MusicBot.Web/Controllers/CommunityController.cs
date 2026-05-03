using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicBot.Data;

namespace MusicBot.Controllers;

public record FeatureRequestDto(int Id, string Title, string Description, int Votes, string Status, DateTime CreatedAt, bool HasVoted);
public record CreateFeatureRequestBody(string Title, string Description);

[ApiController]
[Route("api/community")]
[Tags("Community")]
public class CommunityController : ControllerBase
{
    private readonly MusicBotDbContext _db;
    public CommunityController(MusicBotDbContext db) => _db = db;

    [HttpGet("features")]
    public async Task<IActionResult> GetFeatures()
    {
        var userId  = LocalUser.Id;
        var votedIds = await _db.FeatureVotes
            .Where(v => v.UserId == userId)
            .Select(v => v.FeatureRequestId)
            .ToHashSetAsync();

        var features = await _db.FeatureRequests
            .OrderByDescending(f => f.Votes)
            .ThenByDescending(f => f.CreatedAt)
            .Select(f => new FeatureRequestDto(f.Id, f.Title, f.Description, f.Votes, f.Status, f.CreatedAt, votedIds.Contains(f.Id)))
            .ToListAsync();

        return Ok(features);
    }

    [HttpPost("features")]
    public async Task<IActionResult> CreateFeature([FromBody] CreateFeatureRequestBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Title))
            return BadRequest(new { error = "El título es obligatorio" });

        var feature = new FeatureRequest
        {
            Title       = body.Title.Trim(),
            Description = body.Description?.Trim() ?? "",
            CreatedAt   = DateTime.UtcNow,
        };
        _db.FeatureRequests.Add(feature);
        await _db.SaveChangesAsync();

        return Created($"/api/community/features/{feature.Id}",
            new FeatureRequestDto(feature.Id, feature.Title, feature.Description, 0, feature.Status, feature.CreatedAt, false));
    }

    [HttpPost("features/{id:int}/vote")]
    public async Task<IActionResult> Vote(int id)
    {
        var userId  = LocalUser.Id;
        var feature = await _db.FeatureRequests.FindAsync(id);
        if (feature is null) return NotFound();

        var existing = await _db.FeatureVotes
            .FirstOrDefaultAsync(v => v.FeatureRequestId == id && v.UserId == userId);

        if (existing is not null)
        {
            _db.FeatureVotes.Remove(existing);
            feature.Votes = Math.Max(0, feature.Votes - 1);
            await _db.SaveChangesAsync();
            return Ok(new { votes = feature.Votes, hasVoted = false });
        }

        _db.FeatureVotes.Add(new FeatureVote { FeatureRequestId = id, UserId = userId, VotedAt = DateTime.UtcNow });
        feature.Votes++;
        await _db.SaveChangesAsync();
        return Ok(new { votes = feature.Votes, hasVoted = true });
    }

    [HttpDelete("features/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var feature = await _db.FeatureRequests.FindAsync(id);
        if (feature is null) return NotFound();

        await _db.FeatureVotes.Where(v => v.FeatureRequestId == id).ExecuteDeleteAsync();
        _db.FeatureRequests.Remove(feature);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
