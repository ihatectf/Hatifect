using System.Collections.ObjectModel;
using System.Text;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;

namespace Hatifect.ChestsAnywhereOverlay.UI.Semantic;

/// <summary>Semantic navigator modes; presentation decides how each mode is materialized.</summary>
internal enum ChestsAnywhereNavigatorMode
{
    Category,
    Favorites,
    Recent
}

/// <summary>
/// Provider-neutral, immutable input for one visible Chests Anywhere storage. The integration
/// adapter owns reflection and handles; this semantic boundary carries only stable facts.
/// </summary>
internal sealed record ChestsAnywhereStorageSnapshot(
    string Key,
    string Name,
    string CategoryKey,
    string Location,
    int? SortOrder);

internal sealed record ChestsAnywhereCategorySnapshot(string Key, string Label);

/// <summary>
/// Immutable copied capture supplied by the Chests Anywhere integration owner. All collections are
/// validated and copied before they become observable Experience state.
/// </summary>
internal sealed record ChestsAnywhereNavigatorSnapshot(
    string Title,
    ChestsAnywhereNavigatorMode Mode,
    string SelectedCategoryKey,
    string StatusText,
    IReadOnlyList<ChestsAnywhereCategorySnapshot> Categories,
    IReadOnlyList<ChestsAnywhereStorageSnapshot> Storages,
    IReadOnlyCollection<string> FavoriteStorageKeys,
    IReadOnlyList<string> RecentStorageKeys,
    IReadOnlyDictionary<string, string> LastStorageByCategory,
    string CurrentStorageKey,
    bool RememberLastStoragePerCategory,
    long Revision);

internal sealed record ChestsAnywhereNavigatorMutationResult(
    bool Succeeded,
    ChestsAnywhereNavigatorSnapshot Snapshot);

/// <summary>Stable semantic category identity and display metadata.</summary>
internal sealed record ChestsAnywhereNavigatorCategory(UiSymbolId Id, string Key, string Label);

/// <summary>Stable semantic storage identity and display metadata.</summary>
internal sealed record ChestsAnywhereNavigatorStorage(
    UiSymbolId Id,
    string Key,
    string Name,
    string CategoryKey,
    string Location,
    int? SortOrder,
    bool IsFavorite,
    bool IsCurrent);

/// <summary>Host-visible, geometry-free request to let the integration perform CA's menu handoff.</summary>
internal sealed record ChestsAnywhereStorageHandoff(UiSymbolId StorageId, string StorageKey);

/// <summary>
/// The integration owner is the only authority that performs remote/menu mutations. Every method
/// either returns a complete immutable replacement capture or throws; sessions never publish a
/// partially refreshed local projection after a failed operation.
/// </summary>
internal interface IChestsAnywhereNavigatorPort
{
    ChestsAnywhereNavigatorSnapshot Capture();
    ChestsAnywhereNavigatorSnapshot Refresh();
    ChestsAnywhereNavigatorSnapshot ChangeView(
        ChestsAnywhereNavigatorMode mode,
        string selectedCategoryKey);
    ChestsAnywhereNavigatorSnapshot ToggleFavorite(string storageKey);
    ChestsAnywhereNavigatorMutationResult RequestOpenStorage(string storageKey);
}

/// <summary>Canonical IDs derive from provider/domain keys, never labels, localization, or index.</summary>
internal static class ChestsAnywhereNavigatorIdentity
{
    internal static UiSymbolId Category(string key) => Item("category", key);
    internal static UiSymbolId Storage(string key) => Item("storage", key);

    private static UiSymbolId Item(string kind, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A stable domain key is required.", nameof(key));
        return new UiSymbolId(
            "Hatifect.ChestsAnywhereOverlay",
            $"navigator/{kind}/{Convert.ToHexString(Encoding.UTF8.GetBytes(key))}");
    }
}

/// <summary>
/// Host-free semantic Chests Anywhere Navigator. It preserves navigator state and explicit domain
/// intents while deliberately excluding selector suppression, native-toggle interception, menu
/// handoff confirmation, geometry, focus, and portal ownership from the Experience.
/// </summary>
internal sealed partial class ChestsAnywhereNavigatorExperienceSession : IDisposable
{
    private readonly IChestsAnywhereNavigatorPort _port;
    private readonly Action? _onClose;
    private readonly Func<string, string, string> _translate;
    private readonly UiPublication _publication;
    private readonly UiPublishedState<ChestsAnywhereNavigatorMode> _mode;
    private readonly UiPublishedState<string> _selectedCategory;
    private readonly UiPublishedState<UiStatus> _status;
    private readonly UiPublishedState<ChestsAnywhereStorageHandoff?> _handoff;
    private readonly UiPublishedSelectableCollection<ChestsAnywhereNavigatorCategory> _categories;
    private readonly UiPublishedSelectableCollection<ChestsAnywhereNavigatorStorage> _storages;
    private readonly UiPublishedState<NavigatorProjection> _projection;
    private bool _requesting;
    private bool _completed;
    private bool _disposed;

    public ChestsAnywhereNavigatorExperienceSession(
        UiSymbolId id,
        IChestsAnywhereNavigatorPort port,
        Action? onClose = null,
        Func<string, string, string>? translate = null)
    {
        if (!id.IsValid) throw new ArgumentException("A stable Chests Anywhere Navigator Experience ID is required.", nameof(id));
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _onClose = onClose;
        _translate = translate ?? ((_, fallback) => fallback);
        NavigatorProjection projection = Project(port.Capture());
        _publication = new UiPublication(id);
        var categoryType = UiSourceTypes.Scalar<ChestsAnywhereNavigatorCategory>(new("Hatifect.ChestsAnywhereOverlay", "data/category"), false);
        var storageType = UiSourceTypes.Scalar<ChestsAnywhereNavigatorStorage>(new("Hatifect.ChestsAnywhereOverlay", "data/storage"), false);
        var modeType = UiSourceTypes.Scalar<ChestsAnywhereNavigatorMode>(new("Hatifect.ChestsAnywhereOverlay", "data/mode"), false);
        var handoffType = UiSourceTypes.Scalar<ChestsAnywhereStorageHandoff?>(new("Hatifect.ChestsAnywhereOverlay", "data/handoff"), true);
        _projection = _publication.State(id.Child("state/projection"), projection,
            UiSourceTypes.Scalar<NavigatorProjection>(new("Hatifect.ChestsAnywhereOverlay", "data/projection"), false));
        _mode = _publication.State(id.Child("source/mode"), projection.Mode, modeType);
        _selectedCategory = _publication.State(id.Child("state/category-key"), projection.SelectedCategoryKey, UiSourceTypes.String);
        _status = _publication.State(id.Child("element/Status"),
            new UiStatus(UiStatusKind.Status, projection.StatusText), UiSourceTypes.Status);
        _handoff = _publication.State<ChestsAnywhereStorageHandoff?>(id.Child("element/Handoff"), null, handoffType);
        _categories = _publication.SelectableCollection(id.Child("element/Categories"),
            projection.Categories, categoryType,
            category => category.Id,
            category => category.Label,
            CategoryId(projection.SelectedCategoryKey));
        IReadOnlyList<ChestsAnywhereNavigatorStorage> visibleStorages =
            VisibleStorages(projection, projection.Mode, projection.SelectedCategoryKey);
        _storages = _publication.SelectableCollection(id.Child("element/Storages"),
            visibleStorages, storageType,
            storage => storage.Id,
            storage => storage.Name,
            SelectInitialStorageId(visibleStorages),
            StorageSupportingText);
        Categories = new(_categories, RequestCategory);
        Storages = new(_storages, RequestStorage);
        UiSymbolId categories = id.Child("element/Categories"), storages = id.Child("element/Storages");
        UiSymbolId mode = id.Child("source/mode"), category = id.Child("source/selected-category"), storage = id.Child("source/selected-storage");
        UiActionDefinition[] actions = CreateActions(id);
        var builder = new UiExperienceBuilder(id, Text("navigator.title", projection.Title))
            // Retain the existing passive mode display until the authoring/catalog migration.
            .Select("Mode", _mode)
            .Source(mode, "ModeInput", "Mode input", _mode, modeType, UiCapabilities.Filter)
            .Element(categories, "Categories", Text("navigator.categories", "Categories"), Categories,
                UiSourceTypes.Collection(categoryType), UiCapabilities.Navigate)
            .Source(category, "SelectedCategory", "Selected category", new UiSelectionSource(Categories),
                UiSourceTypes.Selection(categoryType), UiCapabilities.Select, UiCapabilities.Filter)
            .Element(storages, "Storages", Text("navigator.storages", "Storages"), Storages,
                UiSourceTypes.Collection(storageType), UiCapabilities.Browse, UiCapabilities.Select)
            .Source(storage, "SelectedStorage", "Selected storage", new UiSelectionSource(Storages),
                UiSourceTypes.Selection(storageType), UiCapabilities.Select)
            .Status(id.Child("element/Status"), "Status", "Status", _status)
            .Element(id.Child("element/Handoff"), "Handoff", "Handoff", _handoff,
                handoffType, UiCapabilities.Monitor)
            .Actions("Actions", actions)
            .Input(storages, new(storages.Child("input/mode"), "Mode", modeType.Descriptor, true))
            .Input(storages, new(storages.Child("input/category"), "Category", UiSourceTypes.Selection(categoryType).Descriptor, true))
            .Relation(new(id.Child("relation/category-selection"), UiRelationKind.Selection, categories, category))
            .Relation(new(id.Child("relation/storage-selection"), UiRelationKind.Selection, storages, storage))
            .Relation(new(id.Child("relation/mode-filter"), UiRelationKind.Filter, mode, storages, storages.Child("input/mode")))
            .Relation(new(id.Child("relation/category-filter"), UiRelationKind.Filter, category, storages, storages.Child("input/category")))
            .VisualRole("StorageCategory")
            .VisualRole("Storage")
            .VisualRole("Storage.Current")
            .VisualRole("Status")
            .VisualRole("Action.Primary");
        foreach (UiActionDefinition action in actions.Where(action => action.Id == id.Child("action/open") || action.Id == id.Child("action/toggle-favorite")))
        {
            builder.Action(action, action.Id == id.Child("action/open") ? "OpenStorage" : "FavoriteStorage",
                    UiDataTypes.Action(action.Id.Child("contract"), RequestType.Descriptor, ReceiptType.Descriptor,
                        UiSourceTypes.Selection(storageType).Descriptor, UiCapabilities.Select.Id))
                .Relation(new(action.Id.Child("target"), UiRelationKind.ActionTarget, action.Id, storage));
        }
        Experience = builder.Build();
    }

    public UiExperienceDefinition Experience { get; }
    public UiPublication Publication => _publication;
    public IUiSemanticSource<ChestsAnywhereNavigatorMode> Mode => _mode;
    public IUiSemanticSource<string> SelectedCategory => _selectedCategory;
    public IUiSemanticSource<UiStatus> Status => _status;
    public ChestsAnywhereSelectionSource<ChestsAnywhereNavigatorCategory> Categories { get; }
    public ChestsAnywhereSelectionSource<ChestsAnywhereNavigatorStorage> Storages { get; }
    public IUiSemanticSource<ChestsAnywhereStorageHandoff?> Handoff => _handoff;
    public bool IsCompleted => _completed;

    public void SelectMode(ChestsAnywhereNavigatorMode mode)
        => Request(() => ExecuteRequest(CaptureRequest(NavigatorCommand.ChangeView, mode: mode)));

    public void SelectCategory(string categoryKey)
        => Request(() =>
        {
            if (string.IsNullOrWhiteSpace(categoryKey)) throw new ArgumentException("A category key is required.", nameof(categoryKey));
            ExecuteRequest(CaptureRequest(NavigatorCommand.SelectCategory, categoryKey: categoryKey));
        });

    public void Refresh(bool preserveView = true)
        => Request(() => ExecuteRequest(CaptureRequest(NavigatorCommand.Refresh, preserveView: preserveView)));

    public void OpenSelectedStorage()
        => Request(() => ExecuteRequest(CaptureRequest(NavigatorCommand.Open)));

    public void ToggleSelectedFavorite()
        => Request(() => ExecuteRequest(CaptureRequest(NavigatorCommand.Favorite)));

    public void Close()
        => Request(() => ExecuteRequest(CaptureRequest(NavigatorCommand.Close)));

    public void Dispose()
    {
        if (_disposed) return;
        _publication.Dispose();
        _disposed = true;
    }

    private bool Available => !_disposed && !_completed && !_publication.IsDisposed;
    private bool HasSelectedStorage() => Available && _storages.SelectedItemId != null;
    private bool CanSelectCategory() => Available && _categories.SelectedItemId != null;

    private bool RequestCategory(UiSymbolId id)
    {
        if (!CanRequestSelection(_categories, id, out int index)) return false;
        if (_categories.SelectedItemId == id) return true;
        SelectCategory(_categories.Value[index].Key);
        return _categories.SelectedItemId == id;
    }

    private bool RequestStorage(UiSymbolId id)
    {
        if (!CanRequestSelection(_storages, id, out _)) return false;
        bool succeeded = false;
        Request(() => succeeded = _publication.BeginUpdate().Select(_storages, id).Commit().Succeeded);
        return succeeded;
    }

    private bool CanRequestSelection<T>(UiPublishedSelectableCollection<T> source, UiSymbolId id, out int index)
    {
        if (!id.IsValid) throw new ArgumentException("A stable collection item ID is required.", nameof(id));
        index = -1;
        if (!Available || _requesting || _publication.IsPublishing) return false;
        _publication.Capture(); // Validate owning thread before any request or provider callback.
        return source.TryGetIndex(id, out index);
    }

    private void Request(Action request)
    {
        ThrowIfUnavailable();
        _publication.Capture();
        if (_requesting || _publication.IsPublishing)
            throw new InvalidOperationException("UIP003: A navigator request cannot run inside another request or publication observer.");
        _requesting = true;
        try { request(); }
        finally { _requesting = false; }
    }

    private void ApplyProjection(NavigatorProjection next, NavigatorRequestContext context, bool preserveView,
        ChestsAnywhereStorageHandoff? handoff = null, bool replaceHandoff = false,
        UiStatusKind statusKind = UiStatusKind.Status)
    {
        RequireCurrent(context);
        ChestsAnywhereNavigatorMode nextMode = preserveView ? context.Mode : next.Mode;
        string nextCategory = NormalizeCategory(next, preserveView ? context.CategoryKey : next.SelectedCategoryKey);
        IReadOnlyList<ChestsAnywhereNavigatorStorage> visible = VisibleStorages(next, nextMode, nextCategory);
        UiSymbolId? prior = context.StorageId;
        UiSymbolId? selection = prior is { } selected && visible.Any(storage => storage.Id == selected)
            ? selected : visible.Count == 0 ? null : visible[0].Id;
        UiPublicationResult result = _publication.BeginUpdate()
            .Set(_projection, next)
            .Set(_mode, nextMode)
            .Set(_selectedCategory, nextCategory)
            .Set(_status, new UiStatus(statusKind, next.StatusText))
            .Set(_handoff, replaceHandoff ? handoff : context.Handoff)
            .Replace(_categories, next.Categories)
            .Select(_categories, CategoryId(nextCategory))
            .Replace(_storages, visible)
            .Select(_storages, selection)
            .Commit();
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    private static IReadOnlyList<ChestsAnywhereNavigatorStorage> VisibleStorages(
        NavigatorProjection projection,
        ChestsAnywhereNavigatorMode mode,
        string categoryKey)
    {
        IEnumerable<ChestsAnywhereNavigatorStorage> values = mode switch
        {
            ChestsAnywhereNavigatorMode.Favorites => projection.Storages.Where(storage => storage.IsFavorite),
            ChestsAnywhereNavigatorMode.Recent => projection.RecentStorageKeys
                .Select(key => projection.StoragesByKey.TryGetValue(key, out ChestsAnywhereNavigatorStorage? storage) ? storage : null)
                .OfType<ChestsAnywhereNavigatorStorage>(),
            _ => projection.Storages.Where(storage => string.Equals(storage.CategoryKey, categoryKey, StringComparison.Ordinal))
        };
        ChestsAnywhereNavigatorStorage[] result = values.ToArray();
        if (mode == ChestsAnywhereNavigatorMode.Category && projection.RememberLastStoragePerCategory &&
            projection.LastStorageByCategory.TryGetValue(categoryKey, out string? lastKey))
        {
            int index = Array.FindIndex(result, storage => string.Equals(storage.Key, lastKey, StringComparison.Ordinal));
            if (index > 0)
                (result[0], result[index]) = (result[index], result[0]);
        }
        return Array.AsReadOnly(result);
    }

    private static UiSymbolId? SelectInitialStorageId(
        IReadOnlyList<ChestsAnywhereNavigatorStorage> visibleStorages)
    {
        ChestsAnywhereNavigatorStorage? current = visibleStorages.FirstOrDefault(storage => storage.IsCurrent);
        return current?.Id ?? (visibleStorages.Count == 0 ? null : visibleStorages[0].Id);
    }

    private static NavigatorProjection Project(ChestsAnywhereNavigatorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(typeof(ChestsAnywhereNavigatorMode), snapshot.Mode))
            throw new InvalidOperationException("The navigator snapshot contains an invalid mode.");
        ArgumentNullException.ThrowIfNull(snapshot.Categories);
        ArgumentNullException.ThrowIfNull(snapshot.Storages);
        ArgumentNullException.ThrowIfNull(snapshot.FavoriteStorageKeys);
        ArgumentNullException.ThrowIfNull(snapshot.RecentStorageKeys);
        ArgumentNullException.ThrowIfNull(snapshot.LastStorageByCategory);
        if (string.IsNullOrWhiteSpace(snapshot.StatusText))
            throw new InvalidOperationException("The navigator snapshot contains no status message.");

        ChestsAnywhereNavigatorCategory[] categories = snapshot.Categories
            .Select(category => category ?? throw new InvalidOperationException("The navigator snapshot contains a null category."))
            .Select(category =>
            {
                if (string.IsNullOrWhiteSpace(category.Key)) throw new InvalidOperationException("A navigator category has no stable key.");
                string label = string.IsNullOrWhiteSpace(category.Label) ? category.Key : category.Label;
                return new ChestsAnywhereNavigatorCategory(ChestsAnywhereNavigatorIdentity.Category(category.Key), category.Key, label);
            })
            .OrderBy(category => category.Key, StringComparer.Ordinal)
            .ToArray();
        EnsureUnique(categories.Select(category => category.Key), "category key");

        var categoryKeys = new HashSet<string>(categories.Select(category => category.Key), StringComparer.Ordinal);
        ChestsAnywhereStorageSnapshot[] rawStorages = snapshot.Storages
            .Select(storage => storage ?? throw new InvalidOperationException("The navigator snapshot contains a null storage."))
            .ToArray();
        EnsureUnique(rawStorages.Select(storage => storage.Key), "storage key");
        foreach (ChestsAnywhereStorageSnapshot storage in rawStorages)
        {
            if (string.IsNullOrWhiteSpace(storage.Key)) throw new InvalidOperationException("A navigator storage has no stable key.");
            if (string.IsNullOrWhiteSpace(storage.CategoryKey) || !categoryKeys.Contains(storage.CategoryKey))
                throw new InvalidOperationException($"Storage '{storage.Key}' names an unknown category '{storage.CategoryKey}'.");
            if (string.IsNullOrWhiteSpace(storage.Name)) throw new InvalidOperationException($"Storage '{storage.Key}' has no display name.");
        }

        var favorites = new HashSet<string>(snapshot.FavoriteStorageKeys.Where(key => !string.IsNullOrWhiteSpace(key)), StringComparer.Ordinal);
        ChestsAnywhereNavigatorStorage[] storages = rawStorages
            .OrderBy(storage => storage.SortOrder ?? int.MaxValue)
            .ThenBy(storage => storage.Key, StringComparer.Ordinal)
            .Select(storage => new ChestsAnywhereNavigatorStorage(
                ChestsAnywhereNavigatorIdentity.Storage(storage.Key),
                storage.Key,
                storage.Name,
                storage.CategoryKey,
                storage.Location ?? string.Empty,
                storage.SortOrder,
                favorites.Contains(storage.Key),
                string.Equals(storage.Key, snapshot.CurrentStorageKey, StringComparison.Ordinal)))
            .ToArray();
        var byKey = new ReadOnlyDictionary<string, ChestsAnywhereNavigatorStorage>(
            storages.ToDictionary(storage => storage.Key, StringComparer.Ordinal));
        string[] recent = snapshot.RecentStorageKeys
            .Where(key => !string.IsNullOrWhiteSpace(key) && byKey.ContainsKey(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var lastByCategory = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string category, string storage) in snapshot.LastStorageByCategory.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(category) && !string.IsNullOrWhiteSpace(storage) &&
                categoryKeys.Contains(category) && byKey.TryGetValue(storage, out ChestsAnywhereNavigatorStorage? entry) &&
                string.Equals(entry.CategoryKey, category, StringComparison.Ordinal))
                lastByCategory.Add(category, storage);
        }

        string selectedCategory = NormalizeCategory(categories, snapshot.SelectedCategoryKey, snapshot.CurrentStorageKey, byKey);
        string title = string.IsNullOrWhiteSpace(snapshot.Title) ? "Storage navigator" : snapshot.Title;
        return new NavigatorProjection(
            title,
            snapshot.Mode,
            selectedCategory,
            snapshot.StatusText,
            Array.AsReadOnly(categories),
            Array.AsReadOnly(storages),
            byKey,
            Array.AsReadOnly(recent),
            new ReadOnlyDictionary<string, string>(lastByCategory),
            snapshot.CurrentStorageKey ?? string.Empty,
            snapshot.RememberLastStoragePerCategory,
            snapshot.Revision);
    }

    private static string NormalizeCategory(NavigatorProjection projection, string candidate)
        => NormalizeCategory(projection.Categories, candidate, projection.CurrentStorageKey, projection.StoragesByKey);

    private static string NormalizeCategory(
        IReadOnlyList<ChestsAnywhereNavigatorCategory> categories,
        string? candidate,
        string? currentStorageKey,
        IReadOnlyDictionary<string, ChestsAnywhereNavigatorStorage> storages)
    {
        if (!string.IsNullOrWhiteSpace(candidate) && categories.Any(category => string.Equals(category.Key, candidate, StringComparison.Ordinal)))
            return candidate;
        if (!string.IsNullOrWhiteSpace(currentStorageKey) && storages.TryGetValue(currentStorageKey, out ChestsAnywhereNavigatorStorage? current))
            return current.CategoryKey;
        return categories.Count == 0 ? string.Empty : categories[0].Key;
    }

    private static UiSymbolId? CategoryId(string categoryKey)
        => string.IsNullOrWhiteSpace(categoryKey) ? null : ChestsAnywhereNavigatorIdentity.Category(categoryKey);

    private static string StorageSupportingText(ChestsAnywhereNavigatorStorage storage)
        => string.IsNullOrWhiteSpace(storage.Location) ? storage.CategoryKey : storage.Location;

    private string Text(string key, string fallback)
    {
        string translated = _translate(key, fallback);
        return string.IsNullOrWhiteSpace(translated) ? fallback : translated;
    }

    private static void EnsureUnique(IEnumerable<string> keys, string description)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException($"A navigator {description} is empty.");
            if (!known.Add(key)) throw new InvalidOperationException($"The navigator snapshot contains duplicate {description} '{key}'.");
        }
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ChestsAnywhereNavigatorExperienceSession));
        if (_completed) throw new InvalidOperationException("The Chests Anywhere Navigator session is complete.");
        if (_publication.IsDisposed) throw new ObjectDisposedException(nameof(UiPublication));
    }

    private sealed record NavigatorProjection(
        string Title,
        ChestsAnywhereNavigatorMode Mode,
        string SelectedCategoryKey,
        string StatusText,
        IReadOnlyList<ChestsAnywhereNavigatorCategory> Categories,
        IReadOnlyList<ChestsAnywhereNavigatorStorage> Storages,
        IReadOnlyDictionary<string, ChestsAnywhereNavigatorStorage> StoragesByKey,
        IReadOnlyList<string> RecentStorageKeys,
        IReadOnlyDictionary<string, string> LastStorageByCategory,
        string CurrentStorageKey,
        bool RememberLastStoragePerCategory,
        long Revision);
}
