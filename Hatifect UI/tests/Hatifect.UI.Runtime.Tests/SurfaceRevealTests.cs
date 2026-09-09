using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Diagnostics;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Runtime.Visual.Theming;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class SurfaceRevealTests
{
    [Fact]
    public void ReadOnlyResultIsRevealedThroughScrollingWithoutFocusOrActionEffects()
    {
        var fixture = new Fixture();
        var root = fixture.Host.Root;
        Assert.True(fixture.ResultEntry.Clip.Height < fixture.ResultEntry.Bounds.Height);
        var focus = root.Interactions.Snapshot.Focused;
        var rendered = root.LastCompletedRender;
        long version = root.FrameVersion;

        Assert.True(fixture.Reveal());

        Assert.InRange(fixture.Inputs, 1, 64);
        Assert.True(root.FrameVersion > version);
        AssertFullyVisible(fixture.ResultEntry);
        Assert.Equal(focus, root.Interactions.Snapshot.Focused);
        Assert.Equal(0, fixture.Effects);
        Assert.Equal(rendered, root.LastCompletedRender);
    }

    [Fact]
    public void AlreadyVisibleAndMissingTargetsDoNotDispatchInputOrReplaceTheFrame()
    {
        var fixture = new Fixture(count: 1);
        var frame = fixture.Host.Root.Frame;

        Assert.True(fixture.Reveal());
        Assert.False(UiSurfaceReveal.Reveal(fixture.Host, new("test", "missing"), () => { }, fixture.Scroll));

        Assert.Equal(0, fixture.Inputs);
        Assert.Same(frame, fixture.Host.Root.Frame);
    }

    [Fact]
    public void FailureTraceCapturesOneAcceptedGeometrySnapshotWithoutAdditionalInput()
    {
        var fixture = new Fixture();
        var frame = fixture.Host.Root.Frame;
        var layout = fixture.Host.Root.Layout;
        var expectedEntry = fixture.ResultEntry;
        UiSurfaceRevealFailure? failure = null;
        int reports = 0;
        int inputs = 0;
        UiPoint point = default;
        float delta = 0;

        Assert.False(UiSurfaceReveal.Reveal(fixture.Host, fixture.Result, () => { }, (p, d) =>
        {
            inputs++;
            point = p;
            delta = d;
            return new(true, null, new(true, false, false, layout.RootScroll!.Offset));
        }, value => { reports++; failure = value; }));

        Assert.Equal(1, reports);
        Assert.Equal(1, inputs);
        Assert.NotNull(failure);
        Assert.Equal("root-offset-did-not-change", failure.Reason);
        Assert.Equal(fixture.Result, failure.Semantic);
        Assert.Equal(1, failure.Inputs);
        Assert.Equal(fixture.Host.Root.AcceptedVersion, failure.SceneVersion);
        Assert.Equal(fixture.Host.Root.FrameVersion, failure.FrameVersion);
        Assert.Equal(expectedEntry.Bounds, failure.TargetBounds);
        Assert.Equal(expectedEntry.Clip, failure.TargetClip);
        Assert.Equal(layout.RootScroll, failure.RootScroll);
        Assert.Equal(point, failure.LastPoint);
        Assert.Equal(delta, failure.LastDelta);
        Assert.True(failure.LastConsumed);
        Assert.Same(frame, fixture.Host.Root.Frame);
        Assert.Same(layout, fixture.Host.Root.Layout);
        Assert.Equal(0, fixture.Effects);

        using JsonDocument serialized = JsonDocument.Parse(JsonSerializer.Serialize(new { failure }));
        JsonElement record = serialized.RootElement.GetProperty("failure");
        Assert.Equal(expectedEntry.Bounds.Y, record.GetProperty("TargetBounds").GetProperty("Y").GetSingle());
        Assert.Equal(expectedEntry.Bounds.Height, record.GetProperty("TargetBounds").GetProperty("Height").GetSingle());
        Assert.Equal(expectedEntry.Clip.Bottom, record.GetProperty("TargetClip").GetProperty("Bottom").GetSingle());
        Assert.Equal(layout.RootScroll!.Offset, record.GetProperty("RootScroll").GetProperty("Offset").GetSingle());
        Assert.Equal(layout.RootScroll.MaximumOffset, record.GetProperty("RootScroll").GetProperty("MaximumOffset").GetSingle());
    }

    [Fact]
    public void MissingTargetTraceHasNoInventedGeometryAndSuccessProducesNoFailure()
    {
        var fixture = new Fixture(count: 1);
        var failures = new List<UiSurfaceRevealFailure>();
        var missing = new UiSymbolId("test", "absent");

        Assert.True(UiSurfaceReveal.Reveal(fixture.Host, fixture.Result, () => { }, fixture.Scroll, failures.Add));
        Assert.Empty(failures);
        Assert.False(UiSurfaceReveal.Reveal(fixture.Host, missing, () => { }, fixture.Scroll, failures.Add));

        UiSurfaceRevealFailure failure = Assert.Single(failures);
        Assert.Equal("target-missing", failure.Reason);
        Assert.Equal(missing, failure.Semantic);
        Assert.Equal(0, failure.Inputs);
        Assert.Null(failure.TargetBounds);
        Assert.Null(failure.TargetClip);
        Assert.Null(failure.LastPoint);
        Assert.Null(failure.LastDelta);
        Assert.Null(failure.LastConsumed);
        Assert.Equal(0, fixture.Inputs);
    }

    [Fact]
    public void RevealCanReturnToAnEarlierReadOnlyFieldWithoutMovingFocus()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Reveal());
        var first = Nodes(fixture.Host.Root.Scene.Root).First(node => node.SemanticId == fixture.First);
        Assert.True(fixture.Host.Root.Layout.TryGetEntry(first.Id, out var hidden));
        Assert.Equal(0, hidden!.Clip.Height);
        var focus = fixture.Host.Root.Interactions.Snapshot.Focused;

        Assert.True(UiSurfaceReveal.Reveal(fixture.Host, fixture.First, () => { }, fixture.Scroll));

        Assert.True(fixture.Host.Root.Layout.TryGetEntry(first.Id, out var visible));
        AssertFullyVisible(visible!);
        Assert.Equal(focus, fixture.Host.Root.Interactions.Snapshot.Focused);
    }

    [Fact]
    public void OwnerRejectionPrecedesAnyInputOrAcceptedStateChange()
    {
        var fixture = new Fixture();
        var frame = fixture.Host.Root.Frame;

        Assert.Throws<InvalidOperationException>(() => UiSurfaceReveal.Reveal(fixture.Host, fixture.Result,
            () => throw new InvalidOperationException("Owner changed."), fixture.Scroll));

        Assert.Equal(0, fixture.Inputs);
        Assert.Same(frame, fixture.Host.Root.Frame);
    }

    [Fact]
    public void RetirementAfterAnInputCannotBeReportedAsSuccessfulVisibility()
    {
        var fixture = new Fixture();

        Assert.Throws<ObjectDisposedException>(() => UiSurfaceReveal.Reveal(fixture.Host, fixture.Result,
            () => { }, (point, delta) =>
            {
                var result = fixture.Scroll(point, delta);
                fixture.Host.Deactivate();
                return result;
            }));

        Assert.Equal(1, fixture.Inputs);
        Assert.False(fixture.Host.Root.IsActive);
    }

    [Fact]
    public void AChangedSceneCannotRedirectTheRemainingInputsToANewTarget()
    {
        var fixture = new Fixture();
        var replacement = new Fixture();

        Assert.Throws<InvalidOperationException>(() => UiSurfaceReveal.Reveal(fixture.Host, fixture.Result,
            () => { }, (point, delta) =>
            {
                var result = fixture.Scroll(point, delta);
                fixture.Host.UpdateRoot(replacement.Host.Root.Scene, Fixture.Placement);
                return result;
            }));

        Assert.Equal(1, fixture.Inputs);
    }

    [Fact]
    public void ConsumedInputWithoutLayoutProgressStopsInsteadOfSpinning()
    {
        var fixture = new Fixture();
        int inputs = 0;
        var frame = fixture.Host.Root.Frame;

        Assert.False(UiSurfaceReveal.Reveal(fixture.Host, fixture.Result, () => { }, (_, _) =>
        {
            inputs++;
            return new(true, null, new(true, false, false, 0));
        }));

        Assert.Equal(1, inputs);
        Assert.Same(frame, fixture.Host.Root.Frame);
    }

    [Fact]
    public void UnrelatedCollectionOffsetsArePreservedByTheRootInputRoute()
    {
        var fixture = new Fixture(includeCollection: true);
        var offsets = fixture.Host.Root.Layout.CollectionWindows.ToDictionary(window => window.Collection, window => window.ScrollOffset);
        Assert.NotEmpty(offsets);

        Assert.True(fixture.Reveal());

        AssertFullyVisible(fixture.ResultEntry);
        foreach (var window in fixture.Host.Root.Layout.CollectionWindows)
            Assert.Equal(offsets[window.Collection], window.ScrollOffset);
    }

    [Fact]
    public void SlowInputProgressStillStopsAtTheDocumentedBudget()
    {
        var fixture = new Fixture();
        UiSurfaceRevealFailure? failure = null;

        Assert.False(UiSurfaceReveal.Reveal(fixture.Host, fixture.Result, () => { },
            (point, _) => fixture.Scroll(point, 1), value => failure = value));

        Assert.Equal(64, fixture.Inputs);
        Assert.True(fixture.ResultEntry.Clip.Height < fixture.ResultEntry.Bounds.Height);
        Assert.NotNull(failure);
        Assert.Equal("input-budget-exhausted", failure.Reason);
        Assert.Equal(64, failure.Inputs);
        Assert.Equal(fixture.Host.Root.Layout.RootScroll, failure.RootScroll);
    }

    [Fact]
    public void AnOpenPortalRejectsRevealBeforeAnyRootOrPortalInput()
    {
        var fixture = new Fixture();
        var popup = new Fixture(count: 1, policy: UiHostPolicies.Popup);
        using var portal = fixture.Host.Present(new(new("test", "popup"),
            new(fixture.Host.Root.Scene.Root.Id), popup.Host.Root.Scene, Fixture.Placement));
        var frame = fixture.Host.Root.Frame;

        Assert.Throws<InvalidOperationException>(() => fixture.Reveal());

        Assert.Equal(0, fixture.Inputs);
        Assert.Same(frame, fixture.Host.Root.Frame);
        Assert.Equal(1, fixture.Host.ActivePortalCount);
    }

    [Fact]
    public void OversizedSceneIsRejectedBeforeDispatchingInput()
    {
        var fixture = new Fixture(count: 1100);

        var error = Assert.Throws<InvalidOperationException>(() => fixture.Reveal());

        Assert.Contains("bounded node limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Inputs);
    }

    [Fact]
    public void ForeignThreadIsRejectedBeforeTheNativeOwnerCallback()
    {
        var fixture = new Fixture();
        int ownerReads = 0;
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { UiSurfaceReveal.Reveal(fixture.Host, fixture.Result, () => ownerReads++, fixture.Scroll); }
            catch (Exception error) { failure = error; }
        });

        thread.Start();
        thread.Join();

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(0, ownerReads);
        Assert.Equal(0, fixture.Inputs);
    }

    [Fact]
    public void AFailedLaterInputPreservesEarlierAcceptedProgressAndAllowsRetry()
    {
        var fixture = new Fixture();
        UiRenderFrame? firstFrame = null;
        int attempted = 0;

        var error = Assert.Throws<InvalidOperationException>(() =>
            UiSurfaceReveal.Reveal(fixture.Host, fixture.Result, () => { }, (point, delta) =>
            {
                if (++attempted == 2) throw new InvalidOperationException("Input failed.");
                var result = fixture.Scroll(point, delta);
                firstFrame = fixture.Host.Root.Frame;
                return result;
            }));

        Assert.Equal("Input failed.", error.Message);
        Assert.Equal(2, attempted);
        Assert.NotNull(firstFrame);
        Assert.Same(firstFrame, fixture.Host.Root.Frame);
        Assert.True(fixture.Reveal());
        AssertFullyVisible(fixture.ResultEntry);
    }

    [Fact]
    public void RootScrollMaximumReachesTheEdgeAfterActualFloatTranslation()
    {
        var viewport = new UiRect(0, .2f, 100, 103.4f);
        const float extent = 360;

        float maximum = UiRootScrollLayout.MaximumFor(viewport, extent);

        Assert.True((viewport.Y - maximum) + extent <= viewport.Bottom);
        Assert.True((viewport.Y - MathF.BitDecrement(maximum)) + extent > viewport.Bottom);
        Assert.Equal(maximum, new UiRootScrollLayout(viewport, extent, 0).MaximumOffset);
    }

    [Theory]
    [InlineData(1707, 960)]
    [InlineData(1280, 720)]
    public void FractionalNestedResultGeometryRemainsRevealable(int width, int height)
    {
        // The native RU75 failure has a fully reachable root viewport but a nested
        // result clip one float step below its bounds. Vary stack depth, retaining
        // the framework's fractional typography and real Window layout/scroll.
        for (int count = 1; count <= 40; count++)
        {
            var fixture = new Fixture(count: count, semanticWindow: true, includeCollection: true,
                placement: new(new UiRect(0, 0, width, height)), emptyResult: true, locale: "ru-RU");
            UiSurfaceRevealFailure? failure = null;
            Assert.True(UiSurfaceReveal.Reveal(fixture.Host, fixture.Result, () => { }, fixture.Scroll,
                value => failure = value), $"count={count}: {JsonSerializer.Serialize(failure)}");
            UiLayoutEntry result = fixture.ResultEntry;
            Assert.Equal(20, result.Bounds.Height);
            Assert.NotNull(result.HeadingBounds);
            Assert.Equal(20, result.HeadingBounds.Value.Height);
            Assert.True(result.HeadingBounds.Value.Bottom <= result.Clip.Bottom);
            Assert.True(result.Bounds.Y >= result.Clip.Y);
            Assert.True(result.Bounds.Bottom <= result.Clip.Bottom);
            Assert.True(result.Bounds.Right <= result.Clip.Right);
            UiTextPrimitive heading = Assert.Single(fixture.RenderTexts(), text => text.Text == "Result");
            Assert.Equal(20, heading.Bounds.Height);
            Assert.True(heading.Bounds.Y >= heading.Clip.Y && heading.Bounds.Bottom <= heading.Clip.Bottom);
            Assert.True(heading.Bounds.X >= heading.Clip.X && heading.Bounds.Right <= heading.Clip.Right);
            Assert.InRange(fixture.Inputs, 0, 64);
            Assert.Equal(0, fixture.Effects);
        }
    }

    [Fact]
    public void ASubpixelClippedEdgeRequiresInputInsteadOfAnOptimisticSuccess()
    {
        var fixture = new Fixture();
        UiRootScrollLayout root = fixture.Host.Root.Layout.RootScroll!;
        float delta = fixture.ResultEntry.Bounds.Bottom - root.Viewport.Bottom - .005f;
        fixture.Host.ScrollAt(new(root.Viewport.X, root.Viewport.Y), delta);
        float clipped = fixture.ResultEntry.Bounds.Bottom - fixture.ResultEntry.Clip.Bottom;
        Assert.InRange(clipped, .001f, .009f);

        Assert.True(fixture.Reveal());

        Assert.Equal(1, fixture.Inputs);
        Assert.True(fixture.ResultEntry.Bounds.Bottom <= fixture.ResultEntry.Clip.Bottom);
        AssertFullyVisible(fixture.ResultEntry);
    }

    [Fact]
    public void ACollectionCoveringTheRootViewportRejectsRevealWithoutScrollingEitherOwner()
    {
        var fixture = new Fixture(includeCollection: true);
        UiScene original = fixture.Host.Root.Scene;
        UiCollectionSceneNode originalCollection = Nodes(original.Root).OfType<UiCollectionSceneNode>().Single();
        var properties = originalCollection.Visual.Properties.ToDictionary(property => property.Property.Id,
            property => property.Property.Name switch
            {
                "padding" => property with { Value = new UiSpacing(0) },
                "border" => property with { Value = new UiBorder(default, 0) },
                _ => property
            });
        var visual = new UiVisualResolution(properties, originalCollection.Visual.Trace.ToArray());
        var collection = new UiCollectionSceneNode(originalCollection.Id, originalCollection.Role, visual,
            originalCollection.SelectedItemVisual, originalCollection.SemanticName,
            (IUiSemanticCollectionSource)originalCollection.SourceIdentity,
            originalCollection.Recipe with { PreviewRows = 64 }) { SemanticId = originalCollection.SemanticId };
        var children = new UiSceneNode[] { collection }.Concat(Nodes(original.Root).OfType<UiSourceSceneNode>()).ToArray();
        var scene = new UiScene(original.Experience, original.DisplayName,
            new(original.Root.Id, original.Root.Role, original.Root.Visual, original.Root.Policy, children),
            original.MeasurementContext);
        var host = new UiPortalHostSession(scene, Fixture.Placement, new Platform());
        UiRect viewport = host.Root.Layout.RootScroll!.Viewport;
        Assert.True(host.Root.Layout.TryGetEntry(collection.Id, out var entry));
        Assert.True(entry!.ContentBounds.X <= viewport.X && entry.ContentBounds.Y <= viewport.Y
            && entry.ContentBounds.Right >= viewport.Right && entry.ContentBounds.Bottom >= viewport.Bottom);
        Assert.Equal(viewport, entry.Clip);
        Assert.True(host.Root.Layout.TryGetCollection(collection.Id, out var window));
        float collectionOffset = window!.ScrollOffset;
        float rootOffset = host.Root.Layout.RootScroll.Offset;
        var frame = host.Root.Frame;
        int inputs = 0;

        Assert.False(UiSurfaceReveal.Reveal(host, fixture.Result, () => { }, (point, delta) =>
        {
            inputs++;
            return host.ScrollAt(point, delta);
        }));

        Assert.Equal(0, inputs);
        Assert.Equal(rootOffset, host.Root.Layout.RootScroll.Offset);
        Assert.True(host.Root.Layout.TryGetCollection(collection.Id, out var unchanged));
        Assert.Equal(collectionOffset, unchanged!.ScrollOffset);
        Assert.Same(frame, host.Root.Frame);
    }

    [Fact]
    public void SeparateCollectionsCoveringTheGridStillLeaveAUsableRootInputGap()
    {
        var fixture = new Fixture(count: 1, includeCollection: true, semanticWindow: true);
        UiScene original = fixture.Host.Root.Scene;
        UiCollectionSceneNode template = Nodes(original.Root).OfType<UiCollectionSceneNode>().Single();
        var properties = template.Visual.Properties.ToDictionary(property => property.Property.Id,
            property => property.Property.Name switch
            {
                "padding" => property with { Value = new UiSpacing(0) },
                "border" => property with { Value = new UiBorder(default, 0) },
                _ => property
            });
        var visual = new UiVisualResolution(properties, template.Visual.Trace.ToArray());
        var children = new List<UiSceneNode>();
        UiSourceSceneNode field = Nodes(original.Root).OfType<UiSourceSceneNode>().First();
        for (int index = 0; index < 3; index++)
        {
            children.Add(new UiCollectionSceneNode(new("test", "collection/" + index), template.Role, visual,
                template.SelectedItemVisual, "Items " + index, new UiSelectableCollectionState<int>(
                    Enumerable.Range(index * 100, 100).ToArray(), item => new("test", "item/" + item),
                    item => "Item " + item),
                template.Recipe with { PreviewRows = 7 }));
            if (index < 2) children.Add(new UiSourceSceneNode(new("test", "gap/" + index), field.Kind,
                field.Role, field.Visual, "Gap", new UiState<string>("Free root input")));
        }
        children.Add(Nodes(original.Root).Single(node => node.SemanticId == fixture.Result));
        for (int index = 0; index < 20; index++)
            children.Add(new UiSourceSceneNode(new("test", "after/" + index), field.Kind,
                field.Role, field.Visual, "After", new UiState<string>("Additional details")));
        var scene = new UiScene(original.Experience, original.DisplayName,
            new(original.Root.Id, original.Root.Role, original.Root.Visual, original.Root.Policy, children.ToArray()),
            original.MeasurementContext);
        var host = new UiPortalHostSession(scene, Fixture.Placement, new Platform());
        UiRect viewport = host.Root.Layout.RootScroll!.Viewport;
        float[] xs = { MathF.Ceiling(viewport.X), MathF.Floor((viewport.X + viewport.Right) / 2), MathF.Ceiling(viewport.Right) - 1 };
        float[] ys = { MathF.Ceiling(viewport.Y), MathF.Floor((viewport.Y + viewport.Bottom) / 2), MathF.Ceiling(viewport.Bottom) - 1 };
        foreach (float x in xs)
        foreach (float y in ys) Assert.True(IsCollectionPoint(host.Root.Layout, new(x, y)));
        Assert.True(host.Root.Layout.TryGetEntry(new("test", "gap/0"), out var gap));
        UiPoint free = new(xs[1], MathF.Floor((gap!.Bounds.Y + gap.Bounds.Bottom) / 2));
        Assert.True(viewport.Contains(free));
        Assert.False(IsCollectionPoint(host.Root.Layout, free));
        var offsets = host.Root.Layout.CollectionWindows.ToDictionary(window => window.Collection, window => window.ScrollOffset);
        UiSymbolId? focus = host.Root.Interactions.Snapshot.Focused;
        int inputs = 0;
        UiSurfaceRevealFailure? failure = null;

        bool revealed = UiSurfaceReveal.Reveal(host, fixture.Result, () => { }, (point, delta) =>
        {
            Assert.False(IsCollectionPoint(host.Root.Layout, point));
            inputs++;
            return host.ScrollAt(point, delta);
        }, value => failure = value);

        Assert.True(revealed, JsonSerializer.Serialize(failure));
        Assert.InRange(inputs, 1, 64);
        var target = children.Single(node => node.SemanticId == fixture.Result);
        Assert.True(host.Root.Layout.TryGetEntry(target.Id, out var result));
        AssertFullyVisible(result!);
        Assert.Equal(focus, host.Root.Interactions.Snapshot.Focused);
        foreach (UiCollectionLayoutWindow window in host.Root.Layout.CollectionWindows)
            Assert.Equal(offsets[window.Collection], window.ScrollOffset);
    }

    private static bool IsCollectionPoint(UiLayoutSnapshot layout, UiPoint point)
        => layout.CollectionWindows.Any(window => layout.TryGetEntry(window.Collection, out var entry)
            && entry is not null && entry.ContentBounds.Contains(point) && entry.Clip.Contains(point));

    private static void AssertFullyVisible(UiLayoutEntry entry)
    {
        Assert.True(entry.Bounds.Width > 0 && entry.Bounds.Height > 0);
        Assert.Equal(entry.Bounds.X, entry.Clip.X, 3);
        Assert.Equal(entry.Bounds.Y, entry.Clip.Y, 3);
        Assert.Equal(entry.Bounds.Right, entry.Clip.Right, 3);
        Assert.Equal(entry.Bounds.Bottom, entry.Clip.Bottom, 3);
    }

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var descendant in Nodes(child)) yield return descendant;
    }

    private sealed class Fixture
    {
        internal static readonly UiHostPlacementContext Placement = new(new UiRect(0, 0, 1280, 720),
            anchor: new UiRect(400, 300, 30, 30));
        internal readonly UiPortalHostSession Host;
        private readonly Platform _platform = new();
        internal readonly UiSymbolId Result;
        internal readonly UiSymbolId First;
        internal int Inputs;
        internal int Effects;

        internal Fixture(int count = 40, bool includeCollection = false, UiHostPolicy? policy = null,
            bool semanticWindow = false, UiHostPlacementContext? placement = null,
            bool emptyResult = false, string locale = "en")
        {
            var id = new UiSymbolId("test", "reveal");
            var builder = new UiExperienceBuilder(id, "Network");
            for (int i = 0; i < count; i++) builder.Monitor("Field/" + i, new UiState<string>("Value"));
            if (includeCollection) builder.Select("Items", new UiSelectableCollectionState<int>(
                Enumerable.Range(0, 100).ToArray(), item => id.Child("item/" + item), item => "Item " + item));
            builder.Monitor("Result", new UiState<string>(emptyResult ? "" : "Created station."));
            if (!emptyResult) builder.Monitor("After", new UiState<string>("Additional details."));
            var experience = builder.Actions("Actions",
                new UiActionDefinition(id.Child("send"), "Send", () => Effects++)).Build();
            Result = experience.Elements.Single(item => item.Alias == "Result").Id;
            First = experience.Elements.Single(item => item.Alias == "Field/0").Id;
            var registry = semanticWindow ? UiSemanticHostProjection.Register(experience, UiSemanticHostKind.Window)
                : new UiRegistryBuilder().Window(id, "Network", () => experience,
                    policy ?? UiProvisionalHostPolicies.OverlayCentered(true)).Freeze();
            var scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
                new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide), locale: locale);
            Host = new(scene, placement ?? Placement, _platform);
        }

        internal UiLayoutEntry ResultEntry
        {
            get
            {
                var node = Nodes(Host.Root.Scene.Root).Single(node => node.SemanticId == Result);
                Assert.True(Host.Root.Layout.TryGetEntry(node.Id, out var entry));
                return entry!;
            }
        }

        internal IReadOnlyList<UiTextPrimitive> RenderTexts()
        {
            _platform.Texts.Clear();
            Host.Root.Render();
            return _platform.Texts;
        }

        internal bool Reveal() => UiSurfaceReveal.Reveal(Host, Result, () => { }, Scroll);
        internal UiPortalScrollDispatch Scroll(UiPoint point, float delta)
        {
            Inputs++;
            return Host.ScrollAt(point, delta);
        }
    }

    private sealed class Platform : IUiPlatformBridge
    {
        internal List<UiTextPrimitive> Texts { get; } = new();
        public UiSize Measure(string text, UiTypography typography, float width, UiTextOverflow overflow)
            => new(Math.Min(text.Length * 8, width), typography.Size * typography.LineHeight);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) => Texts.Add(text);
    }
}
