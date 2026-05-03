namespace MusicBot.Data;

public class FeatureRequest
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int Votes { get; set; }
    public string Status { get; set; } = "open";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class FeatureVote
{
    public int Id { get; set; }
    public int FeatureRequestId { get; set; }
    public Guid UserId { get; set; }
    public DateTime VotedAt { get; set; } = DateTime.UtcNow;
}
