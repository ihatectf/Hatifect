using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Actions;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Semantics;
using Xunit;
using Xunit.Abstractions;

namespace Hatifect.UI.Runtime.Tests;

public sealed class SceneStructureAllocationTests
{
    private readonly ITestOutputHelper _output;
    public SceneStructureAllocationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SuccessiveCandidatesReuseAcceptedStructuralWorkWithoutSkippingContentChecks()
    {
        var reconciler = new UiSceneReconciler();
        UiScene warmPrevious = Scene(8), warmNext = Scene(8);
        for (int index = 0; index < 32; index++) reconciler.Compare(warmPrevious, warmNext);
        const int repetitions = 16;
        long constructionBefore = GC.GetAllocatedBytesForCurrentThread();
        UiScene[] scenes = Enumerable.Range(0, repetitions + 2).Select(_ => Scene(512)).ToArray();
        long construction = (GC.GetAllocatedBytesForCurrentThread() - constructionBefore) / scenes.Length;
        long before = GC.GetAllocatedBytesForCurrentThread();
        UiSceneDiff initial = reconciler.Compare(scenes[0], scenes[1]);
        long cold = GC.GetAllocatedBytesForCurrentThread() - before;
        bool unchanged = true;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < repetitions; index++)
            unchanged &= reconciler.Compare(scenes[index + 1], scenes[index + 2]).Effects == UiPropertyEffects.None;
        long repeated = (GC.GetAllocatedBytesForCurrentThread() - before) / repetitions;

        _output.WriteLine($"512-node scene construction={construction} B; cold Compare={cold} B; successive Compare={repeated} B.");
        Assert.Equal(UiPropertyEffects.None, initial.Effects);
        Assert.True(unchanged);
        Assert.True(repeated * 100 < cold * 85,
            $"Successive candidates must reuse accepted structural allocation: cold={cold} B, repeated={repeated} B.");
        UiScene changed = Scene(512, changedIndex: 321);
        UiSceneDiff diff = reconciler.Compare(scenes[^1], changed);
        Assert.True(diff.RequiresLayout);
        Assert.Equal(new UiSymbolId("test", "node/321"), Assert.Single(diff.ChangedNodes));
    }

    [Fact]
    public void PublishedSceneStructureDoesNotAliasTheAuthoringChildrenArray()
    {
        UiScene original = Scene(2);
        UiSceneNode[] children = original.Root.Children.ToArray();
        var root = new UiHostSceneNode(original.Root.Id, original.Root.Role,
            original.Root.Visual, original.Root.Policy, children);
        var accepted = new UiScene(original.Experience, original.DisplayName, root,
            original.MeasurementContext);
        var reconciler = new UiSceneReconciler();
        Assert.Equal(UiPropertyEffects.None, reconciler.Compare(original, accepted).Effects);

        // The scene is already published; a retained builder array must not rewrite it.
        children[0] = children[1];

        Assert.Same(original.Root.Children[0], accepted.Root.Children[0]);
        Assert.NotEqual(accepted.Root.Children[0].Id, accepted.Root.Children[1].Id);
        Assert.Equal(UiPropertyEffects.None, reconciler.Compare(original, accepted).Effects);
    }

    [Fact]
    public void CachedStructureDoesNotSkipAvailabilityAndAllowsRetryAfterCallbackFailure()
    {
        UiScene template = Scene(0);
        var action = new UiActionDefinition(new("test", "action"), "Run", () => { });
        var button = new UiButtonSceneNode(new("test", "button"), new("test", "button-role"),
            template.Root.Visual, action);
        UiScene previous = WithChildren(template, new[] { button });
        UiScene next = WithChildren(template, new[] { button });
        var resolver = new AvailabilityResolver();
        var reconciler = new UiSceneReconciler();
        Assert.Equal(UiPropertyEffects.None,
            reconciler.Compare(previous, next, previousActions: resolver, nextActions: resolver).Effects);
        Assert.Equal(2, resolver.Reads);
        var failure = new InvalidOperationException("Availability failed after structure was cached.");
        resolver.Failure = failure;

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            reconciler.Compare(previous, next, previousActions: resolver, nextActions: resolver)));

        resolver.Failure = null;
        resolver.Reads = 0;
        Assert.Equal(UiPropertyEffects.None,
            reconciler.Compare(previous, next, previousActions: resolver, nextActions: resolver).Effects);
        Assert.Equal(2, resolver.Reads);
        Assert.Same(button, Assert.Single(previous.Root.Children));
        Assert.Same(button, Assert.Single(next.Root.Children));
    }

    [Fact]
    public void DuplicateCandidateRemainsRejectedWithoutPoisoningAnotherScene()
    {
        UiScene previous = Scene(2);
        UiScene duplicate = WithChildren(previous,
            new[] { previous.Root.Children[0], previous.Root.Children[0] });
        var reconciler = new UiSceneReconciler();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var failure = Assert.Throws<InvalidOperationException>(() => reconciler.Compare(previous, duplicate));
            Assert.Equal($"Scene contains duplicate node ID '{previous.Root.Children[0].Id}'.", failure.Message);
        }

        UiSceneDiff recovered = reconciler.Compare(previous, Scene(2));

        Assert.Equal(UiPropertyEffects.None, recovered.Effects);
        Assert.Empty(recovered.ChangedNodes);
        Assert.Equal(2, previous.Root.Children.Count);
    }

    private static UiScene WithChildren(UiScene template, UiSceneNode[] children)
        => new(template.Experience, template.DisplayName,
            new(template.Root.Id, template.Root.Role, template.Root.Visual, template.Root.Policy, children),
            template.MeasurementContext);

    private sealed class AvailabilityResolver : IUiActionResolver
    {
        internal int Reads;
        internal Exception? Failure;
        public bool CanInvoke(UiActionDefinition definition)
        {
            Reads++;
            if (Failure is { } failure) throw failure;
            return true;
        }
        public bool Invoke(UiActionDefinition definition) => throw new InvalidOperationException("Comparison must not invoke actions.");
        public UiHostActionStatus? Status(UiActionDefinition definition) => null;
    }

    private static UiScene Scene(int count, int changedIndex = -1)
    {
        var visual = new UiVisualResolution(new Dictionary<UiSymbolId, UiResolvedVisualProperty>(),
            Array.Empty<UiVisualResolutionStep>());
        var nodes = Enumerable.Range(0, count).Select(index => (UiSceneNode)new UiSourceSceneNode(
            new("test", "node/" + index), UiSceneNodeKind.Text, new("test", "text"), visual,
            "Value", new UiConstantSource<string>(index == changedIndex ? "Changed" : "Stable"))).ToArray();
        return new(new("test", "scene"), "Scene",
            new(new("test", "root"), new("test", "window"), visual, UiHostPolicies.Window, nodes),
            new(new("test", "profile"), "en", new("test", "theme")));
    }
}
