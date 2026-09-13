namespace Hatifect.ChestsAnywhereOverlay.Models;

internal sealed record StorageSnapshot(
    object Overlay,
    IReadOnlyList<string> Categories,
    IReadOnlyList<StorageEntry> Storages,
    string CurrentKey)
{
    public StorageEntry? Current => Storages.FirstOrDefault(p => p.Key == CurrentKey);

    // Only fields that affect Navigator layout belong in the structural revision. Current chest
    // selection and automation state are read from the latest snapshot when the user acts, so
    // transient Chests Anywhere state does not rebuild the retained button grid every frame.
    public string RevisionKey => string.Join("\n", Categories)
        + "\n"
        + string.Join("\n", Storages.Select(p => $"{p.Key}|{p.Name}|{p.Category}|{p.Order}"));
}
