using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.HotReload;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class LiveSemanticAssetsTests
{
    [Fact]
    public void ActiveHostReloadChangesTheFramePreservesDraftAndFocusAndRejectsInvalidPair()
    {
        var state = new UiState<string>("Draft");
        var experience = new UiExperienceBuilder(Id("live"), "Live").Search("Name", state).VisualRole("Name").Build();
        var registry = UiSemanticHostProjection.Register(experience, UiSemanticHostKind.Window);
        var composer = new UiSceneComposer(UiSemanticThemes.Resolve(UiSemanticTheme.Dark), registry);
        var invocations = new UiInvocationService(registry);
        var placement = new UiHostPlacementContext(new UiRect(0, 0, 1280, 800));
        var platform = new TestPlatform();
        UiHostRuntimeSession? host = null;
        UiSemanticLiveAssets? assets = null;
        assets = new UiSemanticLiveAssets(new[] { experience }, (id, candidate, accept) =>
        {
            var scene = composer.Compose(invocations.Invoke(id, UiPresentationProfiles.Wide, candidate.Presentation), candidate.Visual);
            new UiSceneLayoutEngine(platform).Build(scene, placement);
            host!.Update(composer.Compose(invocations.Invoke(id, UiPresentationProfiles.Wide, candidate.Presentation),
                candidate.Visual, host.Interactions.Snapshot), placement, renewActionGeneration: true, acceptOwnerState: accept);
        });
        host = new UiHostRuntimeSession(composer.Compose(invocations.Invoke(experience.Id, UiPresentationProfiles.Wide)), placement, platform);
        host.Interactions.MoveFocus(UiNavigationDirection.Next);
        var focus = host.Interactions.Snapshot.Focused;
        var before = host.Frame;
        string visual = "visual Live\n\nName\n    surface = Surface.Hover\n";

        var accepted = assets.Reload(experience.Id, null, visual);
        Assert.True(accepted.Accepted);
        Assert.True(accepted.Changed);
        Assert.NotSame(before, host.Frame);
        UiSymbolId inputId = experience.Elements[0].Id.Child("scene/input");
        Assert.True(host.Layout.TryGetEntry(inputId, out var inputLayout));
        var input = Assert.Single(host.Frame.Primitives.OfType<UiSurfacePrimitive>(),
            primitive => primitive.Node == inputId && primitive.Bounds == inputLayout!.Bounds);
        Assert.Equal(UiSemanticThemes.Resolve(UiSemanticTheme.Dark).Resolve(UiThemeTokens.SurfaceHover), input.Surface);
        Assert.Equal(focus, host.Interactions.Snapshot.Focused);
        Assert.Equal("Draft", state.Value);
        var frame = host.Frame;
        var retainedAssets = assets.For(experience.Id);
        var invalid = assets.Reload(experience.Id, "presentation Live\n\nName\n    view = Missing\n", visual.Replace("Hover", "Pressed"));
        Assert.False(invalid.Accepted);
        Assert.NotEmpty(invalid.Diagnostics);
        Assert.Equal(accepted.Version, invalid.Version);
        Assert.Same(frame, host.Frame);
        Assert.Same(retainedAssets, assets.For(experience.Id));
        Assert.False(assets.Reload(experience.Id, null, visual).Changed);
        Assert.Same(frame, host.Frame);
    }

    [Fact]
    public void PresentationReloadReplansAnActiveCollectionWithoutReplacingSelection()
    {
        var source = new UiSelectableCollectionState<int>(Enumerable.Range(0, 100).ToArray(),
            value => Id("item/" + value), value => "Item " + value, selectedItemId: Id("item/4"));
        var experience = new UiExperienceBuilder(Id("presentation"), "Presentation").Browse("Items", source).Build();
        var registry = UiSemanticHostProjection.Register(experience, UiSemanticHostKind.Window);
        var composer = new UiSceneComposer(UiSemanticThemes.Resolve(UiSemanticTheme.Dark), registry);
        var invocations = new UiInvocationService(registry);
        var placement = new UiHostPlacementContext(new UiRect(0, 0, 1280, 800));
        var host = new UiHostRuntimeSession(composer.Compose(invocations.Invoke(experience.Id, UiPresentationProfiles.Wide)),
            placement, new TestPlatform());
        UiSemanticLiveAssets? assets = null;
        assets = new UiSemanticLiveAssets(new[] { experience }, (id, candidate, accept) =>
        {
            host.Update(composer.Compose(invocations.Invoke(id, UiPresentationProfiles.Wide, candidate.Presentation),
                candidate.Visual, host.Interactions.Snapshot), placement, renewActionGeneration: true, acceptOwnerState: accept);
        });
        var before = Assert.Single(host.Scene.Root.Children.SelectMany(slot => slot.Children).OfType<UiCollectionSceneNode>());
        Assert.Equal(UiCollectionLayoutKind.AdaptiveGrid, before.Recipe.Layout);
        var result = assets.Reload(experience.Id, "presentation Live\n\nItems\n    view = List\n    itemSizing = Adaptive\n", null);
        var after = Assert.Single(host.Scene.Root.Children.SelectMany(slot => slot.Children).OfType<UiCollectionSceneNode>());
        Assert.True(result.Accepted);
        Assert.True(result.Changed);
        Assert.Equal(UiCollectionLayoutKind.List, after.Recipe.Layout);
        Assert.True(after.Recipe.IsAdaptive);
        Assert.Same(source, after.SourceIdentity);
        Assert.Equal(Id("item/4"), source.SelectedItemId);
        Assert.True(host.LastUpdate!.LayoutChanged);
    }

    [Fact]
    public void HostRejectionRestoresPreviousAssetsAndVersion()
    {
        var experience = new UiExperienceBuilder(Id("reject"), "Reject").Monitor("Status", new UiState<string>("Ready"))
            .VisualRole("Item").Build();
        int applyCalls = 0;
        bool reject = false;
        var assets = new UiSemanticLiveAssets(new[] { experience }, (_, _, accept) =>
        { applyCalls++; if (reject) { reject = false; throw new InvalidOperationException("Unsupported backend value"); } accept(); });
        string visual = "visual Reject\n\nItem\n    surface = Surface.Raised\n";
        Assert.True(assets.Reload(experience.Id, null, visual).Accepted);
        var before = assets.For(experience.Id);
        reject = true;
        var rejected = assets.Reload(experience.Id, null, visual.Replace("Raised", "Hover"));
        Assert.False(rejected.Accepted);
        Assert.Equal(1, rejected.Version);
        Assert.Same(before, assets.For(experience.Id));
        Assert.Equal(2, applyCalls);
    }

    [Fact]
    public void FailedHostPreparationNeverPublishesCandidateAssetsOrReappliesLastGood()
    {
        var experience = new UiExperienceBuilder(Id("atomic-reject"), "Atomic reject")
            .Monitor("Status", new UiState<string>("Ready")).VisualRole("Item").Build();
        UiSemanticLiveAssets? assets = null;
        UiTerminalSectionAssets? observed = null;
        int applications = 0;
        bool reject = false;
        assets = new UiSemanticLiveAssets(new[] { experience }, (id, _, accept) =>
        {
            applications++;
            if (!reject) { accept(); return; }
            observed ??= assets!.For(id);
            throw new InvalidOperationException("Host preparation refused the candidate.");
        });
        string visual = "visual Atomic\n\nItem\n    surface = Surface.Raised\n";
        Assert.True(assets.Reload(experience.Id, null, visual).Accepted);
        UiTerminalSectionAssets previous = assets.For(experience.Id);
        reject = true;

        UiSemanticReloadResult? result = null;
        Exception? failure = Record.Exception(() => result = assets.Reload(experience.Id, null,
            visual.Replace("Raised", "Hover")));

        Assert.Null(failure);
        Assert.NotNull(result);
        Assert.False(result!.Accepted);
        Assert.False(result.Changed);
        Assert.Equal(1, result.Version);
        Assert.Same(previous, observed);
        Assert.Same(previous, assets.For(experience.Id));
        Assert.Equal(2, applications);
    }

    [Fact]
    public async Task FileWatchLoadsReplacementRetainsInvalidLastGoodAndStopsOnDispose()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hatifect-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "live.visual");
            string initial = "visual Watch\n\nItem\n    surface = Surface.Raised\n";
            File.WriteAllText(path, initial);
            var experience = new UiExperienceBuilder(Id("watch"), "Watch").Monitor("Status", new UiState<string>("Ready"))
                .VisualRole("Item").Build();
            var assets = new UiSemanticLiveAssets(new[] { experience }, (_, _, accept) => accept());
            using var watches = new UiSemanticAssetWatches();
            UiSemanticReloadResult? last = null;
            int callbacks = 0;
            using var lease = watches.Add(experience.Id, null, path, (presentation, visual) =>
            { callbacks++; last = assets.Reload(experience.Id, presentation, visual); }, error => throw error);
            watches.Poll();
            Assert.True(last!.Accepted);
            int idleCallbacks = callbacks;
            watches.Poll();
            Assert.Equal(idleCallbacks, callbacks);
            string replacement = Path.Combine(directory, "next.visual");
            File.WriteAllText(replacement, initial.Replace("Raised", "Hover"));
            File.Move(replacement, path, overwrite: true);
            await Until(() => last?.Version == 2, watches);
            File.WriteAllText(path, "invalid document");
            await Until(() => last?.Accepted == false, watches);
            Assert.Equal(2, last!.Version);
            lease.Dispose();
            int stoppedCallbacks = callbacks;
            File.Delete(path);
            watches.Poll();
            Assert.Equal(stoppedCallbacks, callbacks);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void FileWatchRetriesTransientReadFailureWithoutAnotherWriteAndBoundsPermanentFailures()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hatifect-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "asset.visual");
            File.WriteAllText(path, "visual Retry");
            using var watches = new UiSemanticAssetWatches();
            int errors = 0;
            int loaded = 0;
            using var lease = watches.Add(Id("retry"), null, path, (_, _) => loaded++, _ => errors++);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                watches.Poll();
            Assert.Equal(1, errors);
            Assert.Equal(0, loaded);
            for (int tick = 0; tick < 30; tick++) watches.Poll();
            Assert.Equal(1, loaded);

            using var missing = watches.Add(Id("missing"), null, Path.Combine(directory, "missing.visual"),
                (_, _) => throw new InvalidOperationException("Missing file was loaded"), _ => errors++);
            for (int tick = 0; tick < 500; tick++) watches.Poll();
            Assert.Equal(5, errors);
            for (int tick = 0; tick < 500; tick++) watches.Poll();
            Assert.Equal(5, errors);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task Until(Func<bool> condition, UiSemanticAssetWatches watches)
    {
        for (int attempt = 0; attempt < 150 && !condition(); attempt++)
        { await Task.Delay(20); watches.Poll(); }
        Assert.True(condition(), "The file change did not arrive within the bounded wait.");
    }
    private static UiSymbolId Id(string name) => new("Reload.Tests", name);
    private sealed class TestPlatform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * 8, availableWidth), 20);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
