using MusicBot.Core.Models;

namespace MusicBot.Core.Interfaces;

/// <summary>Searches for song metadata from an external source (iTunes, Spotify, etc.).</summary>
public interface IMetadataService
{
    Task<List<Song>> SearchAsync(string query, int limit = 5);
}
