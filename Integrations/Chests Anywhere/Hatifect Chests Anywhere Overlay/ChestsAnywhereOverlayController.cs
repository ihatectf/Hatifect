using Hatifect.ChestsAnywhereOverlay.Integration;
using Hatifect.ChestsAnywhereOverlay.Models;
using Hatifect.ChestsAnywhereOverlay.Presentation;
using StardewModdingAPI;
using StardewValley;

namespace Hatifect.ChestsAnywhereOverlay;

internal enum NavigatorMode
{
    Category,
    Favorites,
    Recent
}

internal sealed class ChestsAnywhereOverlayController
{
    private const string PlayerStateKey = "Hatifect.ChestsAnywhereOverlay/State";
    private const string PreviousPlayerStateKey = "Hatifect.ChestsAnywhereOverlay/State";
    private const string LegacyPlayerStateKey = "Hatifect.Flow.ChestsAnywhere/State";
    private readonly IChestsAnywhereOverlayAdapter _adapter;
    private readonly Func<ChestsAnywhereOverlayConfig> _getConfig;
    private readonly Func<string, object?, string> _translate;
    private readonly Action _resetSession;
    private readonly Action _saveStateOnShutdown;
    private readonly ChestsAnywhereOverlayLifecycle _lifecycle = new();

    private NavigatorState _state = new();
    private StorageSnapshot? _snapshot;
    private IChestsAnywhereOverlayFrontend? _frontend;
    private string _snapshotRevision = string.Empty;
    private int _closeAfterRenderedFrames;
    private int _selectionWaitFrames;
    private string? _pendingSelectedKey;
    private bool _nativeSelectorResynchronizationPending;
    private long _semanticRevision;

    public NavigatorMode Mode { get; private set; } = NavigatorMode.Category;
    public string SelectedCategory { get; private set; } = string.Empty;
    public string StatusText { get; private set; } = string.Empty;
    public bool IsVisible => _frontend?.Visible == true;
    public int FavoriteCount => _snapshot?.Storages.Count(p => _state.Favorites.Contains(p.Key)) ?? 0;
    public int RecentCount => _snapshot?.Storages.Count(p => _state.Recent.Contains(p.Key)) ?? 0;
    public IReadOnlyList<string> CategoryNames => _snapshot?.Categories ?? Array.Empty<string>();
    public int VisibleStorageCount
    {
        get
        {
            if (_snapshot == null)
                return 0;
            return Mode switch
            {
                NavigatorMode.Favorites => _snapshot.Storages.Count(entry => _state.Favorites.Contains(entry.Key)),
                NavigatorMode.Recent => _state.Recent.Count(key => _snapshot.Storages.Any(entry => entry.Key == key)),
                _ => _snapshot.Storages.Count(entry => string.Equals(entry.Category, SelectedCategory, StringComparison.Ordinal))
            };
        }
    }
    public string CurrentViewTitle => Mode switch
    {
        NavigatorMode.Favorites => T("navigator.favorites"),
        NavigatorMode.Recent => T("navigator.recent"),
        _ => string.IsNullOrWhiteSpace(SelectedCategory) ? T("navigator.storages") : SelectedCategory
    };

    public IReadOnlyList<StorageEntry> VisibleStorages
    {
        get
        {
            if (_snapshot == null)
                return Array.Empty<StorageEntry>();

            IEnumerable<StorageEntry> query = _snapshot.Storages;
            if (Mode == NavigatorMode.Favorites)
                query = query.Where(p => _state.Favorites.Contains(p.Key));
            else if (Mode == NavigatorMode.Recent)
            {
                var byKey = _snapshot.Storages.ToDictionary(p => p.Key, StringComparer.Ordinal);
                return _state.Recent.Where(byKey.ContainsKey).Select(key => byKey[key]).ToArray();
            }
            else if (Mode == NavigatorMode.Category)
                query = query.Where(p => string.Equals(p.Category, SelectedCategory, StringComparison.Ordinal));

            var result = query.ToList();
            if (Mode == NavigatorMode.Category && _getConfig().RememberLastStoragePerCategory
                && _state.LastByCategory.TryGetValue(SelectedCategory, out string? lastKey))
            {
                int index = result.FindIndex(p => p.Key == lastKey);
                if (index > 0)
                {
                    StorageEntry recent = result[index];
                    result.RemoveAt(index);
                    result.Insert(0, recent);
                }
            }
            return result;
        }
    }

    public ChestsAnywhereOverlayController(
        IModHelper helper,
        IChestsAnywhereOverlayAdapter adapter,
        Func<ChestsAnywhereOverlayConfig> getConfig)
        : this(adapter, getConfig, CreateTranslator(helper))
    {
    }

    /// <summary>
    /// Minimal host-free composition seam for controller lifecycle tests. Production translation
    /// and shutdown persistence remain supplied by SMAPI through the public constructor.
    /// </summary>
    internal static ChestsAnywhereOverlayController CreateForLifecycleTest(
        IChestsAnywhereOverlayAdapter adapter,
        Func<ChestsAnywhereOverlayConfig> getConfig,
        Func<string, object?, string> translate,
        Action? saveStateOnShutdown = null)
        => new(adapter, getConfig, translate, saveStateOnShutdown ?? (() => { }));

    private ChestsAnywhereOverlayController(
        IChestsAnywhereOverlayAdapter adapter,
        Func<ChestsAnywhereOverlayConfig> getConfig,
        Func<string, object?, string> translate,
        Action? saveStateOnShutdown = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _getConfig = getConfig ?? throw new ArgumentNullException(nameof(getConfig));
        _translate = translate ?? throw new ArgumentNullException(nameof(translate));
        _resetSession = ResetSession;
        _saveStateOnShutdown = saveStateOnShutdown ?? SaveState;
        StatusText = T("navigator.status.ready");
    }

    public void AttachFrontend(IChestsAnywhereOverlayFrontend frontend)
    {
        ArgumentNullException.ThrowIfNull(frontend);
        if (_frontend != null) throw new InvalidOperationException("Chests Anywhere Overlay frontend is already attached.");
        _frontend = frontend;
        _frontend.Rendered += OnFrontendRendered;
        _frontend.Closed += OnFrontendClosed;
    }

    public void LoadState()
    {
        ChestsAnywhereOverlayConfig config = _getConfig();
        string? raw = null;
        if (Context.IsWorldReady)
        {
            if (!Game1.player.modData.TryGetValue(PlayerStateKey, out raw)
                && !Game1.player.modData.TryGetValue(PreviousPlayerStateKey, out raw))
            {
                Game1.player.modData.TryGetValue(LegacyPlayerStateKey, out raw);
            }
        }
        _state = NavigatorState.Deserialize(raw, config.RecentLimit);
        _semanticRevision++;
    }

    public void SaveState()
    {
        if (!Context.IsWorldReady)
            return;
        _state.Normalize(_getConfig().RecentLimit);
        Game1.player.modData[PlayerStateKey] = _state.Serialize();
    }

    public void Update()
    {
        if (!Context.IsWorldReady || !_adapter.IsOverlayActive)
        {
            if (_lifecycle.ObserveInactive() == ChestsAnywhereOverlayTransition.NativeSessionEnded)
                ResetSession();
            return;
        }

        UpdateActiveOverlay();
    }

    /// <summary>Processes one already-active native overlay tick.</summary>
    internal void UpdateActiveOverlay()
    {
        if (!ChestsAnywhereOverlayCapturePolicy.TryCaptureOrRetire(
                _adapter,
                _resetSession,
                out StorageSnapshot snapshot))
            return;

        string revision = snapshot.RevisionKey;
        bool changed = !ReferenceEquals(_snapshot?.Overlay, snapshot.Overlay) || !string.Equals(_snapshotRevision, revision, StringComparison.Ordinal);
        _snapshot = snapshot;
        _snapshotRevision = revision;
        if (changed) _semanticRevision++;

        // Direct world RMB stays entirely Chests Anywhere-owned. Native selectors are suppressed
        // only while the Hatifect overlay is actually visible, and restored immediately when it hides.
        if (IsVisible)
        {
            if (!_adapter.MuteNativeToggle(snapshot.Overlay))
            {
                Hide();
                return;
            }
            bool hideNativeSelectors = _getConfig().HideNativeSelectors;
            bool synchronizeSelectors = ChestsAnywhereSelectorSynchronizationPolicy.ShouldSynchronize(
                    changed || _nativeSelectorResynchronizationPending,
                    hideNativeSelectors,
                    _adapter.HasSuppressedNativeSelectors);
            if (synchronizeSelectors
                && !_adapter.SuppressNativeSelectors(snapshot.Overlay, hideNativeSelectors))
            {
                Hide();
                return;
            }
            if (synchronizeSelectors || !hideNativeSelectors)
                _nativeSelectorResynchronizationPending = false;
        }
        else if (!_adapter.RestoreNativeSelectors() || !_adapter.RestoreNativeToggle())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedCategory) || !snapshot.Categories.Contains(SelectedCategory, StringComparer.Ordinal))
            SelectedCategory = snapshot.Current?.Category ?? snapshot.Categories.FirstOrDefault() ?? string.Empty;

        if (_pendingSelectedKey != null && snapshot.CurrentKey == _pendingSelectedKey)
        {
            _pendingSelectedKey = null;
            _selectionWaitFrames = 0;
            _closeAfterRenderedFrames = Math.Max(_closeAfterRenderedFrames, 2);
        }

        if (changed)
            _frontend?.Refresh(preserveViewState: true);

        ChestsAnywhereOverlayTransition transition = _lifecycle.ObserveActive(
            snapshot.Overlay,
            IsVisible,
            _adapter.IsOverlayModal,
            _adapter.IsNativeToggleJustPressed(snapshot.Overlay));
        if (transition == ChestsAnywhereOverlayTransition.HideHatifect)
            Hide();
        else if (transition == ChestsAnywhereOverlayTransition.ShowHatifect)
        {
            Show();
        }

        _frontend?.UpdateOptions(_getConfig());
    }

    public void Show()
    {
        if (_snapshot == null)
            return;
        if (_frontend == null) return;
        Mode = NavigatorMode.Category;
        SelectedCategory = _snapshot.Current?.Category ?? SelectedCategory;
        StatusText = T("navigator.status.ready");
        _frontend.Refresh(preserveViewState: false);
        if (!_adapter.MuteNativeToggle(_snapshot.Overlay)
            || !_adapter.SuppressNativeSelectors(_snapshot.Overlay, _getConfig().HideNativeSelectors))
        {
            _adapter.RestoreNativeSelectors();
            _adapter.RestoreNativeToggle();
            return;
        }
        _frontend.UpdateOptions(_getConfig());
        if (!_frontend.Show())
        {
            _adapter.RestoreNativeSelectors();
            _adapter.RestoreNativeToggle();
        }
    }

    public void Hide()
    {
        _frontend?.Hide();
        _nativeSelectorResynchronizationPending = false;
        _adapter.RestoreNativeSelectors();
        _adapter.RestoreNativeToggle();
    }

    public void SetMode(NavigatorMode mode)
    {
        if (!Enum.IsDefined(typeof(NavigatorMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Mode = mode;
        _semanticRevision++;
        _frontend?.Refresh(preserveViewState: false);
    }

    public void SelectCategory(string category)
    {
        SelectedCategory = category;
        Mode = NavigatorMode.Category;
        _semanticRevision++;
        _frontend?.Refresh(preserveViewState: false);
    }

    public bool IsFavorite(string key) => _state.Favorites.Contains(key);

    public void ToggleFavorite(string key)
    {
        if (!_state.Favorites.Add(key))
            _state.Favorites.Remove(key);
        _semanticRevision++;
        SaveState();
        _frontend?.Refresh(preserveViewState: true);
    }

    public bool OpenStorage(StorageEntry entry)
    {
        if (_snapshot == null)
            return false;
        entry = ResolveLatestEntry(entry);
        if (!_adapter.SelectStorage(_snapshot, entry))
        {
            StatusText = T("navigator.status.switch_failed");
            _semanticRevision++;
            return false;
        }
        _nativeSelectorResynchronizationPending = true;

        _state.LastByCategory[entry.Category] = entry.Key;
        _state.Recent.RemoveAll(p => p == entry.Key);
        _state.Recent.Insert(0, entry.Key);
        _state.Normalize(_getConfig().RecentLimit);
        SaveState();
        StatusText = T("navigator.status.opening", new { category = entry.Category, storage = entry.Name });
        _semanticRevision++;
        _pendingSelectedKey = entry.Key;
        _selectionWaitFrames = 0;
        _closeAfterRenderedFrames = 0; // close only after CA confirms the replacement overlay is current and suppressed.
        return true;
    }

    public StorageEntry ResolveLatestEntry(StorageEntry fallback)
        => _snapshot?.Storages.FirstOrDefault(entry => string.Equals(entry.Key, fallback.Key, StringComparison.Ordinal)) ?? fallback;

    internal StorageEntry? FindStorage(string key)
        => _snapshot?.Storages.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));

    internal ChestsAnywhereOverlayApplicationSnapshot CaptureApplicationSnapshot()
    {
        StorageSnapshot snapshot = _snapshot
            ?? throw new InvalidOperationException("Chests Anywhere has no active storage snapshot.");
        ChestsAnywhereOverlayConfig config = _getConfig();
        return new ChestsAnywhereOverlayApplicationSnapshot(
            Mode,
            SelectedCategory,
            StatusText,
            ChestsAnywhereOverlayApplicationSnapshot.Freeze(snapshot.Categories),
            ChestsAnywhereOverlayApplicationSnapshot.Freeze(snapshot.Storages.Select(storage =>
                new ChestsAnywhereOverlayApplicationEntry(
                    storage.Key,
                    storage.Name,
                    storage.Category,
                    storage.Location,
                    storage.Order))),
            ChestsAnywhereOverlayApplicationSnapshot.Freeze(
                _state.Favorites.OrderBy(key => key, StringComparer.Ordinal)),
            ChestsAnywhereOverlayApplicationSnapshot.Freeze(_state.Recent),
            ChestsAnywhereOverlayApplicationSnapshot.Freeze(
                _state.LastByCategory.OrderBy(pair => pair.Key, StringComparer.Ordinal)),
            snapshot.CurrentKey,
            config.RememberLastStoragePerCategory,
            _semanticRevision);
    }

    public string T(string key) => SafeTranslation(_translate(key, null), key);
    public string T(string key, object tokens) => SafeTranslation(_translate(key, tokens), key);

    private static Func<string, object?, string> CreateTranslator(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        return (key, tokens) => tokens == null
            ? helper.Translation.Get(key).ToString()
            : helper.Translation.Get(key, tokens).ToString();
    }

    private static string SafeTranslation(string value, string key)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && !string.Equals(value, key, StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("no.translation", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("no translation", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("missing translation", StringComparison.OrdinalIgnoreCase))
            return value;
        string tail = key;
        int dot = tail.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < tail.Length) tail = tail[(dot + 1)..];
        tail = tail.Replace('_', ' ').Replace('-', ' ').Trim();
        return tail.Length == 0 ? "Text" : char.ToUpperInvariant(tail[0]) + tail[1..];
    }

    private void OnFrontendClosed()
    {
        _pendingSelectedKey = null;
        _selectionWaitFrames = 0;
        _closeAfterRenderedFrames = 0;
        _nativeSelectorResynchronizationPending = false;
        _adapter.RestoreNativeSelectors();
        _adapter.RestoreNativeToggle();
    }

    private void OnFrontendRendered()
    {
        if (_pendingSelectedKey != null)
        {
            _selectionWaitFrames++;
            if (_selectionWaitFrames >= 120)
            {
                _pendingSelectedKey = null;
                _selectionWaitFrames = 0;
                StatusText = T("navigator.status.switch_failed");
                _semanticRevision++;
                _frontend?.Refresh(preserveViewState: true);
            }
        }
        else if (_closeAfterRenderedFrames > 0)
        {
            _closeAfterRenderedFrames--;
            if (_closeAfterRenderedFrames == 0)
                Hide();
        }
    }

    private void ResetSession()
    {
        Hide();
        _snapshot = null;
        _snapshotRevision = string.Empty;
        _lifecycle.Reset();
        _pendingSelectedKey = null;
        _selectionWaitFrames = 0;
        _closeAfterRenderedFrames = 0;
        _nativeSelectorResynchronizationPending = false;
        _semanticRevision++;
        _adapter.EndSession();
    }

    public void Shutdown()
    {
        _saveStateOnShutdown();
        ResetSession();
    }

    internal bool HasActiveSessionForAcceptance
        => _snapshot != null || _lifecycle.HasActiveNativeOverlay;

}
