using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
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

public sealed class InteractionPreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompositionCannotOverwriteAnAcceptedNestedReplacement(bool composePortal)
    {
        UiScene original = Scene("root", UiHostPolicies.Window);
        UiScene replacement = Scene("replacement", UiHostPolicies.Window);
        UiScene popup = Scene("popup", UiHostPolicies.Popup);
        UiPortalHostSession? host = null;
        bool armed = false;
        int calls = 0;
        UiScene? nestedScene = null;
        long nestedVersion = -1;
        UiScene Compose(UiInteractionSnapshot interaction)
        {
            if (armed)
            {
                armed = false;
                calls++;
                host!.UpdateRoot(replacement, Placement);
                nestedScene = host.Root.Scene;
                nestedVersion = host.Root.AcceptedVersion;
            }
            return composePortal ? popup : original;
        }
        host = new(original, Placement, new Platform(), composeInteraction: composePortal ? null : Compose);
        UiPortalHandle? portal = null;
        try
        {
            if (composePortal) portal = host.Present(new UiPortalRequest(Id("popup"),
                new UiPortalOwner(original.Root.Id), popup, Placement, Compose));
            armed = true;

            Exception? failure = Record.Exception(() => host.MoveFocus(UiNavigationDirection.Next));

            Assert.Equal(1, calls);
            if (composePortal) Assert.IsType<ObjectDisposedException>(failure);
            else Assert.IsType<InvalidOperationException>(failure);
            Assert.Same(replacement, nestedScene);
            Assert.Same(replacement, host.Root.Scene);
            Assert.Equal(nestedVersion, host.Root.AcceptedVersion);
            Assert.Empty(host.Accessibility.Portals);
            host.UpdateRoot(original, Placement);
            Assert.Same(original, host.Root.Scene);
        }
        finally { portal?.Dispose(); host.Deactivate(); }
    }

    [Fact]
    public void ThrowingCompositionReleasesItsOwnerTransactionForRetry()
    {
        UiScene original = Scene("root", UiHostPolicies.Window);
        UiScene replacement = Scene("replacement", UiHostPolicies.Window);
        bool fail = true;
        var host = new UiPortalHostSession(original, Placement, new Platform(), composeInteraction: _ =>
            fail ? throw new InvalidOperationException("Controlled composition rejection.") : original);
        try
        {
            Assert.Throws<InvalidOperationException>(() => host.MoveFocus(UiNavigationDirection.Next));
            Assert.Same(original, host.Root.Scene);
            fail = false;
            host.MoveFocus(UiNavigationDirection.Next);
            host.UpdateRoot(replacement, Placement);
            Assert.Same(replacement, host.Root.Scene);
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void RetirementDuringCompositionCannotPublishAPreparedScene()
    {
        UiScene original = Scene("root", UiHostPolicies.Window);
        UiScene replacement = Scene("replacement", UiHostPolicies.Window);
        UiPortalHostSession? host = null;
        host = new(original, Placement, new Platform(), composeInteraction: _ =>
        {
            host!.Deactivate();
            return replacement;
        });
        Assert.Throws<ObjectDisposedException>(() => host.MoveFocus(UiNavigationDirection.Next));
        Assert.False(host.Root.IsActive);
        Assert.Same(original, host.Root.Scene);
        Assert.False(host.PumpActions());
    }

    private static UiHostPlacementContext Placement => new(new UiRect(0, 0, 960, 600));
    private static UiSymbolId Id(string path) => new("Hatifect.Tests", "interaction-preparation/" + path);
    private static UiScene Scene(string path, UiHostPolicy policy)
    {
        var model = new UiExperienceBuilder(Id(path), path)
            .Monitor("Status", new UiConstantSource<string>(path))
            .Actions("Actions", new UiActionDefinition(Id(path + "/run"), "Run", () => { }),
                new UiActionDefinition(Id(path + "/other"), "Other", () => { })).Build();
        var registry = new UiRegistryBuilder().Window(Id(path), path, () => model, policy).Freeze();
        return new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(Id(path), UiPresentationProfiles.Wide));
    }
    private sealed class Platform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(availableWidth, text.Length * typography.Size * .5f), typography.Size * typography.LineHeight);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
