using System.Collections.ObjectModel;
using Hatifect.ChestsAnywhereOverlay.Models;
using Hatifect.ChestsAnywhereOverlay.UI.Semantic;

namespace Hatifect.ChestsAnywhereOverlay.UI.Semantic;

/// <summary>
/// Integration-owned boundary from the CA controller into immutable semantic values. Reflection,
/// selector ownership, persistence, and remote handoff never cross this adapter.
/// </summary>
internal sealed class ChestsAnywhereNavigatorPortAdapter : IChestsAnywhereNavigatorPort
{
    private readonly ChestsAnywhereOverlayController _controller;

    internal ChestsAnywhereNavigatorPortAdapter(ChestsAnywhereOverlayController controller)
        => _controller = controller ?? throw new ArgumentNullException(nameof(controller));

    public ChestsAnywhereNavigatorSnapshot Capture() => Project(_controller.CaptureApplicationSnapshot());

    public ChestsAnywhereNavigatorSnapshot Refresh() => Capture();

    public ChestsAnywhereNavigatorSnapshot ChangeView(
        ChestsAnywhereNavigatorMode mode,
        string selectedCategoryKey)
    {
        switch (mode)
        {
            case ChestsAnywhereNavigatorMode.Category:
                _controller.SelectCategory(selectedCategoryKey);
                break;
            case ChestsAnywhereNavigatorMode.Favorites:
                _controller.SetMode(NavigatorMode.Favorites);
                break;
            case ChestsAnywhereNavigatorMode.Recent:
                _controller.SetMode(NavigatorMode.Recent);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return Capture();
    }

    public ChestsAnywhereNavigatorSnapshot ToggleFavorite(string storageKey)
    {
        RequireStorage(storageKey);
        _controller.ToggleFavorite(storageKey);
        return Capture();
    }

    public ChestsAnywhereNavigatorMutationResult RequestOpenStorage(string storageKey)
    {
        bool succeeded = _controller.OpenStorage(RequireStorage(storageKey));
        return new ChestsAnywhereNavigatorMutationResult(succeeded, Capture());
    }

    private StorageEntry RequireStorage(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A storage key is required.", nameof(key));
        return _controller.FindStorage(key)
            ?? throw new InvalidOperationException($"Storage '{key}' is no longer available.");
    }

    private ChestsAnywhereNavigatorSnapshot Project(ChestsAnywhereOverlayApplicationSnapshot source)
    {
        var categories = Array.AsReadOnly(source.Categories
            .Select(category => new ChestsAnywhereCategorySnapshot(category, category))
            .ToArray());
        var storages = Array.AsReadOnly(source.Storages
            .Select(storage => new ChestsAnywhereStorageSnapshot(
                storage.Key,
                storage.Name,
                storage.Category,
                storage.Location,
                storage.Order))
            .ToArray());
        var favorites = Array.AsReadOnly(source.FavoriteKeys.ToArray());
        var recent = Array.AsReadOnly(source.RecentKeys.ToArray());
        var last = new ReadOnlyDictionary<string, string>(
            source.LastByCategory.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        return new ChestsAnywhereNavigatorSnapshot(
            _controller.T("navigator.title"),
            ToSemantic(source.Mode),
            source.SelectedCategory,
            source.StatusText,
            categories,
            storages,
            favorites,
            recent,
            last,
            source.CurrentStorageKey,
            source.RememberLastStoragePerCategory,
            source.Revision);
    }

    private static ChestsAnywhereNavigatorMode ToSemantic(NavigatorMode mode)
        => mode switch
        {
            NavigatorMode.Category => ChestsAnywhereNavigatorMode.Category,
            NavigatorMode.Favorites => ChestsAnywhereNavigatorMode.Favorites,
            NavigatorMode.Recent => ChestsAnywhereNavigatorMode.Recent,
            _ => throw new InvalidOperationException($"Unknown Navigator mode '{mode}'.")
        };

}
