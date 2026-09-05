using System.Collections.ObjectModel;

namespace Hatifect.ChestsAnywhereOverlay.Models;

/// <summary>
/// One main-thread-owned immutable copy of controller state for the semantic presentation adapter.
/// Chests Anywhere handles remain confined to the integration assembly.
/// </summary>
internal sealed record ChestsAnywhereOverlayApplicationSnapshot(
    NavigatorMode Mode,
    string SelectedCategory,
    string StatusText,
    IReadOnlyList<string> Categories,
    IReadOnlyList<ChestsAnywhereOverlayApplicationEntry> Storages,
    IReadOnlyCollection<string> FavoriteKeys,
    IReadOnlyList<string> RecentKeys,
    IReadOnlyDictionary<string, string> LastByCategory,
    string CurrentStorageKey,
    bool RememberLastStoragePerCategory,
    long Revision)
{
    internal static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values)
        => Array.AsReadOnly(values.ToArray());

    internal static IReadOnlyDictionary<string, string> Freeze(
        IEnumerable<KeyValuePair<string, string>> values)
        => new ReadOnlyDictionary<string, string>(
            values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}

internal sealed record ChestsAnywhereOverlayApplicationEntry(
    string Key,
    string Name,
    string Category,
    string Location,
    int? Order);
