using System.Text.Json;

namespace Hatifect.ChestsAnywhereOverlay.Models;

internal sealed class NavigatorState
{
    public HashSet<string> Favorites { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LastByCategory { get; set; } = new(StringComparer.Ordinal);
    public List<string> Recent { get; set; } = new();

    public void Normalize(int recentLimit)
    {
        Favorites = new HashSet<string>(Favorites ?? new(), StringComparer.Ordinal);
        LastByCategory = new Dictionary<string, string>(LastByCategory ?? new(), StringComparer.Ordinal);
        Recent = (Recent ?? new()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).Take(recentLimit).ToList();
    }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static NavigatorState Deserialize(string? raw, int recentLimit)
    {
        NavigatorState state;
        try { state = string.IsNullOrWhiteSpace(raw) ? new NavigatorState() : JsonSerializer.Deserialize<NavigatorState>(raw) ?? new NavigatorState(); }
        catch { state = new NavigatorState(); }
        state.Normalize(recentLimit);
        return state;
    }
}
