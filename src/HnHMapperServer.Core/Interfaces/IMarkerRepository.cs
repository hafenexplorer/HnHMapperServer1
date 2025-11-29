using HnHMapperServer.Core.Models;

namespace HnHMapperServer.Core.Interfaces;

public interface IMarkerRepository
{
    Task<Marker?> GetMarkerAsync(int markerId);
    Task<Marker?> GetMarkerByKeyAsync(string key);
    Task<List<Marker>> GetAllMarkersAsync();
    Task SaveMarkerAsync(Marker marker, string key);
    Task DeleteMarkerAsync(string key);
    Task<int> GetNextMarkerIdAsync();

    /// <summary>
    /// Efficiently saves multiple markers in a single transaction.
    /// Only inserts markers that don't already exist (by key).
    /// </summary>
    /// <returns>Number of markers actually inserted</returns>
    Task<int> SaveMarkersBatchAsync(List<(Marker marker, string key)> markers);
}
