namespace Hatifect.ChestsAnywhereOverlay.Models;

internal sealed record StorageEntry(
    string Key,
    string Name,
    string Category,
    string Location,
    int? Order,
    object Handle);
