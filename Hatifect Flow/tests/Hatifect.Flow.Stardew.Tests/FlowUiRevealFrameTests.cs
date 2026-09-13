using System;
using Hatifect.Flow.Diagnostics;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class FlowUiRevealFrameTests
{
    [Fact]
    public void RevealNeedsAFreshDrawOfTheSameAcceptedSceneAndLayout()
    {
        Guid instance = Guid.NewGuid();
        var frame = new UiSemanticSurfaceFrame(2, 7);
        var reveal = new FlowUiRevealFrame(instance, frame, 20);
        Assert.False(reveal.IsCompletedBy(instance, frame, frame, 20));
        Assert.False(reveal.IsCompletedBy(instance, frame, null, 21));
        Assert.False(reveal.IsCompletedBy(Guid.NewGuid(), frame, frame, 21));
        Assert.True(reveal.IsCompletedBy(instance, frame, frame, 21));
    }

    [Fact]
    public void RecompositionOrScrollInvalidatesVisibilityUntilRevealedAgain()
    {
        Guid instance = Guid.NewGuid();
        var original = new UiSemanticSurfaceFrame(2, 7);
        var changedScene = new UiSemanticSurfaceFrame(3, 7);
        var changedLayout = new UiSemanticSurfaceFrame(2, 8);
        var reveal = new FlowUiRevealFrame(instance, original, 20);
        Assert.False(reveal.IsCompletedBy(instance, changedScene, changedScene, 21));
        Assert.False(reveal.IsCompletedBy(instance, changedLayout, changedLayout, 21));
        Assert.False(reveal.IsCompletedBy(instance, original, changedScene, 21));
        var repeated = new FlowUiRevealFrame(instance, changedScene, 21);
        Assert.False(repeated.IsCompletedBy(instance, changedScene, changedScene, 21));
        Assert.True(repeated.IsCompletedBy(instance, changedScene, changedScene, 22));
    }
}
