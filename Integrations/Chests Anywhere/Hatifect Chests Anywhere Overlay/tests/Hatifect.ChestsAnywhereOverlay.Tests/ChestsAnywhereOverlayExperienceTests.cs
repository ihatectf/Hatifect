using Hatifect.ChestsAnywhereOverlay.Integration;
using Hatifect.ChestsAnywhereOverlay.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Hatifect.ChestsAnywhereOverlay.Tests;

public sealed class ChestsAnywhereOverlayExperienceTests
{
    [Fact]
    public void ExactPackageNavigatorGraphPreservesTwoFilterInputsSelectionAndActionTargets()
    {
        var id = Id("graph");
        using var session = new ChestsAnywhereNavigatorExperienceSession(id, new RecordingPort(Snapshot(
            categories: new[] { new ChestsAnywhereCategorySnapshot("farm", "Farm") },
            storages: new[] { Storage("chest", "Storage", "farm", 1) }, selectedCategory: "farm", current: "chest")));
        byte[] wire = UiBindingContextJson.Export(session.Experience.CreateBindingContext());
        UiBindingContext imported = UiBindingContextJson.Import(wire);
        Assert.Equal(wire, UiBindingContextJson.Export(imported));
        UiSemanticGraph graph = imported.Graph!;
        Assert.Empty(UiGraphBinder.Validate(graph));
        var slots = graph.Nodes.Single(node => node.Alias == "Storages").Inputs;
        Assert.Equal(2, slots.Count);
        Assert.NotEqual(slots[0].AcceptedType, slots[1].AcceptedType);
        Assert.Equal(2, graph.Relations.Count(relation => relation.Kind == UiRelationKind.Filter));
        Assert.Equal(2, graph.Relations.Count(relation => relation.Kind == UiRelationKind.Selection));
        Assert.Equal(2, graph.Relations.Count(relation => relation.Kind == UiRelationKind.ActionTarget && relation.Target == id.Child("source/selected-storage")));
        Assert.False(imported.TryGetElement("SelectedStorage", out _));
        Assert.True(new UiCompiler().Compile("presentation Navigator\nStorages -> Primary\nCategories -> Secondary\n", imported).IsValid);
        var selected = Assert.IsType<UiSelectionSource>(session.Experience.Sources.Single(source => source.Alias == "SelectedStorage").Source);
        Assert.Equal(session.Storages.SelectedItemId, selected.Value);

        JsonNode damaged = JsonNode.Parse(wire)!;
        JsonArray relations = damaged["graph"]!["relations"]!.AsArray();
        relations.Remove(relations.Single(relation => relation!["id"]!.GetValue<string>() == id.Child("relation/category-filter").ToString()));
        Assert.Contains("UIG018", Assert.Throws<InvalidDataException>(() => UiBindingContextJson.Import(Encoding.UTF8.GetBytes(damaged.ToJsonString()))).Message);
    }

    [Fact]
    public void ProjectionCopiesInputsAndNormalizesStableIdentityAndOrdering()
    {
        var categories = new List<ChestsAnywhereCategorySnapshot>
        {
            new("z", "Translated Z"), new("a", "Translated A")
        };
        var storages = new List<ChestsAnywhereStorageSnapshot>
        {
            Storage("z", "Name Z", "z", 2),
            Storage("a-second", "Name A2", "a", 1),
            Storage("a-first", "Name A1", "a", 1)
        };
        var favorites = new HashSet<string>(new[] { "a-first" }, StringComparer.Ordinal);
        var recent = new List<string> { "z", "a-first", "z", "stale" };
        var last = new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "a-second" };
        var port = new RecordingPort(Snapshot(
            categories: categories,
            storages: storages,
            favorites: favorites,
            recent: recent,
            last: last,
            selectedCategory: "a",
            current: "a-first"));

        using var session = new ChestsAnywhereNavigatorExperienceSession(Id("projection"), port);
        categories.Clear();
        storages.Clear();
        favorites.Clear();
        recent.Clear();
        last.Clear();

        Assert.Equal(new[] { "a", "z" }, session.Categories.Value.Select(category => category.Key));
        Assert.Equal(new[] { "a-second", "a-first" }, session.Storages.Value.Select(storage => storage.Key));
        Assert.Equal(ChestsAnywhereNavigatorIdentity.Category("a"), session.Categories.Value[0].Id);
        Assert.Equal(ChestsAnywhereNavigatorIdentity.Storage("a-first"), session.Storages.Value[1].Id);
        Assert.Equal("a-first", session.Storages.Value.Single(storage => storage.IsCurrent).Key);
        Assert.True(session.Storages.Value.Single(storage => storage.Key == "a-first").IsFavorite);
    }

    [Fact]
    public void ActionsExposeOnlyCategoryFavoritesRecentRefreshOpenFavoriteAndClose()
    {
        var port = new RecordingPort(Snapshot(
            categories: new[]
            {
                new ChestsAnywhereCategorySnapshot("farm", "Farm"),
                new ChestsAnywhereCategorySnapshot("mine", "Mine")
            },
            storages: new[]
            {
                Storage("farm-a", "Farm A", "farm", 1),
                Storage("mine-a", "Mine A", "mine", 2)
            },
            favorites: new[] { "mine-a" },
            recent: new[] { "mine-a", "farm-a" },
            selectedCategory: "farm",
            current: "farm-a"));
        using var session = new ChestsAnywhereNavigatorExperienceSession(Id("actions"), port);

        Assert.Equal(
            new[]
            {
                "mode-categories", "mode-favorites", "mode-recent", "select-category",
                "refresh", "open", "toggle-favorite", "close"
            },
            session.Experience.Actions.Select(ActionName));

        Assert.True(Action(session, "mode-favorites").TryExecute());
        Assert.Equal("mine-a", Assert.Single(session.Storages.Value).Key);
        Assert.True(Action(session, "mode-recent").TryExecute());
        Assert.Equal(new[] { "mine-a", "farm-a" }, session.Storages.Value.Select(storage => storage.Key));
        Assert.True(Action(session, "mode-categories").TryExecute());
        Assert.True(session.Categories.TrySelect(ChestsAnywhereNavigatorIdentity.Category("farm")));
        Assert.Equal("farm-a", Assert.Single(session.Storages.Value).Key);

        Assert.True(Action(session, "open").TryExecute());
        Assert.Equal("farm-a", Assert.Single(port.OpenRequests));
        Assert.Equal("farm-a", session.Handoff.Value!.StorageKey);
        Assert.True(Action(session, "toggle-favorite").TryExecute());
        Assert.True(Assert.Single(session.Storages.Value).IsFavorite);
        Assert.Equal("farm-a", Assert.Single(port.FavoriteRequests));
    }

    [Fact]
    public void FailedPortOperationsLeaveLastCompleteProjectionAndNoFalseHandoff()
    {
        ChestsAnywhereNavigatorSnapshot baseline = Snapshot(status: "Ready");
        var port = new RecordingPort(baseline)
        {
            ThrowOnRefresh = true,
            ThrowOnChangeView = true,
            ThrowOnToggleFavorite = true
        };
        using var session = new ChestsAnywhereNavigatorExperienceSession(Id("atomic"), port);
        UiSymbolId selected = Assert.Single(session.Storages.Value).Id;

        Assert.Throws<InvalidOperationException>(() => session.Refresh());
        Assert.Throws<InvalidOperationException>(() => session.SelectMode(ChestsAnywhereNavigatorMode.Favorites));
        Assert.Throws<InvalidOperationException>(() => session.ToggleSelectedFavorite());
        Assert.Equal("Ready", session.Status.Value);
        Assert.Equal(selected, session.Storages.SelectedItemId);
        Assert.False(Assert.Single(session.Storages.Value).IsFavorite);

        port.ThrowOnRefresh = false;
        port.ThrowOnChangeView = false;
        port.ThrowOnToggleFavorite = false;
        port.RejectOpen = true;
        session.OpenSelectedStorage();
        Assert.Null(session.Handoff.Value);
        Assert.Equal("Switch failed", session.Status.Value);
    }

    [Theory]
    [InlineData("duplicate-category")]
    [InlineData("duplicate-storage")]
    [InlineData("unknown-category")]
    public void InvalidProviderSnapshotsAreRejectedBeforePublication(string defect)
    {
        ChestsAnywhereNavigatorSnapshot snapshot = defect switch
        {
            "duplicate-category" => Snapshot(categories: new[]
            {
                new ChestsAnywhereCategorySnapshot("farm", "One"),
                new ChestsAnywhereCategorySnapshot("farm", "Two")
            }),
            "duplicate-storage" => Snapshot(storages: new[]
            {
                Storage("same", "One", "farm", 1),
                Storage("same", "Two", "farm", 2)
            }),
            _ => Snapshot(storages: new[] { Storage("mine", "Mine", "missing", 1) })
        };

        Assert.Throws<InvalidOperationException>(() =>
            new ChestsAnywhereNavigatorExperienceSession(Id(defect), new RecordingPort(snapshot)));
    }

    [Fact]
    public void ExactToggleHandoffPreservesNativeRmbAndRepeatedBSequence()
    {
        var lifecycle = new ChestsAnywhereOverlayLifecycle();
        var firstNativeOverlay = new object();

        Assert.Equal(
            ChestsAnywhereOverlayTransition.None,
            lifecycle.ObserveActive(firstNativeOverlay, hatifectVisible: false, nativeModal: false, nativeTogglePressed: false));
        Assert.Equal(
            ChestsAnywhereOverlayTransition.HideHatifect,
            lifecycle.ObserveActive(firstNativeOverlay, hatifectVisible: true, nativeModal: false, nativeTogglePressed: true));
        Assert.Equal(ChestsAnywhereOverlayTransition.NativeSessionEnded, lifecycle.ObserveInactive());

        var secondNativeOverlay = new object();
        Assert.Equal(
            ChestsAnywhereOverlayTransition.ShowHatifect,
            lifecycle.ObserveActive(secondNativeOverlay, hatifectVisible: false, nativeModal: false, nativeTogglePressed: true));
        Assert.Equal(
            ChestsAnywhereOverlayTransition.HideHatifect,
            lifecycle.ObserveActive(secondNativeOverlay, hatifectVisible: true, nativeModal: false, nativeTogglePressed: true));
        Assert.Equal(ChestsAnywhereOverlayTransition.NativeSessionEnded, lifecycle.ObserveInactive());

        var thirdNativeOverlay = new object();
        Assert.Equal(
            ChestsAnywhereOverlayTransition.ShowHatifect,
            lifecycle.ObserveActive(thirdNativeOverlay, hatifectVisible: false, nativeModal: false, nativeTogglePressed: true));
    }

    [Fact]
    public void PrivateApiShapeGuardRejectsAbsentAndIncompatibleBeforeMutation()
    {
        var incompatible = new IncompatibleApi();

        Assert.False(ChestsAnywherePrivateApiShape.IsSupported(null));
        Assert.False(ChestsAnywherePrivateApiShape.IsSupported(incompatible));
        Assert.Equal(0, incompatible.MutationCount);
        Assert.True(ChestsAnywherePrivateApiShape.IsSupported(new CompatibleApi()));
    }

    [Fact]
    public void CompletionAndDisposalRetireEveryAction()
    {
        var port = new RecordingPort(Snapshot());
        int closeRequests = 0;
        var session = new ChestsAnywhereNavigatorExperienceSession(Id("lifecycle"), port, () => closeRequests++);
        UiActionDefinition[] actions = session.Experience.Actions.ToArray();

        Assert.True(Action(session, "close").TryExecute());
        Assert.True(session.IsCompleted);
        Assert.Equal(1, closeRequests);
        Assert.All(actions, action => Assert.False(action.CanExecute));
        Assert.Throws<InvalidOperationException>(() => session.Refresh());
        session.Dispose();
        session.Dispose();

        var disposed = new ChestsAnywhereNavigatorExperienceSession(Id("disposed"), new RecordingPort(Snapshot()));
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.Refresh());
    }

    [Fact]
    public void SemanticAssemblyRemainsHostFree()
    {
        string[] references = typeof(ChestsAnywhereNavigatorExperienceSession).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToArray();

        Assert.Contains("Hatifect.UI.Experience", references);
        Assert.DoesNotContain("Hatifect.ChestsAnywhereOverlay", references);
        Assert.DoesNotContain("Hatifect.UI.Runtime", references);
        Assert.DoesNotContain(references, reference => reference.Contains("Stardew", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("SMAPI", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("MonoGame", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("XNA", StringComparison.OrdinalIgnoreCase));
    }

    private static string ActionName(UiActionDefinition action)
        => action.Id.LocalId[(action.Id.LocalId.LastIndexOf('/') + 1)..];

    private static UiActionDefinition Action(ChestsAnywhereNavigatorExperienceSession session, string name)
        => Assert.Single(
            session.Experience.Actions,
            action => action.Id.LocalId.EndsWith($"/action/{name}", StringComparison.Ordinal));

    private static ChestsAnywhereNavigatorSnapshot Snapshot(
        IReadOnlyList<ChestsAnywhereCategorySnapshot>? categories = null,
        IReadOnlyList<ChestsAnywhereStorageSnapshot>? storages = null,
        IReadOnlyCollection<string>? favorites = null,
        IReadOnlyList<string>? recent = null,
        IReadOnlyDictionary<string, string>? last = null,
        ChestsAnywhereNavigatorMode mode = ChestsAnywhereNavigatorMode.Category,
        string selectedCategory = "farm",
        string current = "",
        string status = "Ready")
        => new(
            "Chests Anywhere Overlay",
            mode,
            selectedCategory,
            status,
            categories ?? new[] { new ChestsAnywhereCategorySnapshot("farm", "Farm") },
            storages ?? new[] { Storage("farm-a", "Farm A", "farm", 1) },
            favorites ?? Array.Empty<string>(),
            recent ?? Array.Empty<string>(),
            last ?? new Dictionary<string, string>(StringComparer.Ordinal),
            current,
            RememberLastStoragePerCategory: true,
            Revision: 1);

    private static ChestsAnywhereStorageSnapshot Storage(string key, string name, string category, int? order)
        => new(key, name, category, $"Location {key}", order);

    private static UiSymbolId Id(string local)
        => new("Hatifect.ChestsAnywhereOverlay", $"navigator/test/{local}");

    private sealed class RecordingPort : IChestsAnywhereNavigatorPort
    {
        internal RecordingPort(ChestsAnywhereNavigatorSnapshot capture) => CaptureValue = capture;

        internal ChestsAnywhereNavigatorSnapshot CaptureValue { get; private set; }
        internal bool ThrowOnRefresh { get; set; }
        internal bool ThrowOnChangeView { get; set; }
        internal bool ThrowOnToggleFavorite { get; set; }
        internal bool RejectOpen { get; set; }
        internal List<string> OpenRequests { get; } = new();
        internal List<string> FavoriteRequests { get; } = new();

        public ChestsAnywhereNavigatorSnapshot Capture() => CaptureValue;

        public ChestsAnywhereNavigatorSnapshot Refresh()
        {
            if (ThrowOnRefresh) throw new InvalidOperationException("refresh failed");
            return CaptureValue;
        }

        public ChestsAnywhereNavigatorSnapshot ChangeView(ChestsAnywhereNavigatorMode mode, string selectedCategoryKey)
        {
            if (ThrowOnChangeView) throw new InvalidOperationException("view failed");
            CaptureValue = CaptureValue with
            {
                Mode = mode,
                SelectedCategoryKey = selectedCategoryKey,
                Revision = CaptureValue.Revision + 1
            };
            return CaptureValue;
        }

        public ChestsAnywhereNavigatorSnapshot ToggleFavorite(string storageKey)
        {
            if (ThrowOnToggleFavorite) throw new InvalidOperationException("favorite failed");
            FavoriteRequests.Add(storageKey);
            var values = new HashSet<string>(CaptureValue.FavoriteStorageKeys, StringComparer.Ordinal);
            if (!values.Add(storageKey)) values.Remove(storageKey);
            CaptureValue = CaptureValue with
            {
                FavoriteStorageKeys = values,
                Revision = CaptureValue.Revision + 1
            };
            return CaptureValue;
        }

        public ChestsAnywhereNavigatorMutationResult RequestOpenStorage(string storageKey)
        {
            OpenRequests.Add(storageKey);
            CaptureValue = CaptureValue with
            {
                StatusText = RejectOpen ? "Switch failed" : "Opening",
                CurrentStorageKey = RejectOpen ? CaptureValue.CurrentStorageKey : storageKey,
                Revision = CaptureValue.Revision + 1
            };
            return new ChestsAnywhereNavigatorMutationResult(!RejectOpen, CaptureValue);
        }
    }

    private sealed class IncompatibleApi
    {
        internal int MutationCount { get; private set; }
        internal void Mutate() => MutationCount++;
    }

    private sealed class CompatibleApi
    {
#pragma warning disable IDE0052
        private readonly Func<object?> GetOverlay = () => null;
#pragma warning restore IDE0052
        private bool IsOverlayActive() => false;
        private bool IsOverlayModal() => false;
    }
}
