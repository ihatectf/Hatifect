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
using Hatifect.UI.Runtime.Visual.Theming;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

// The stale outer composition must preserve an accepted nested scene, matching Terminal reentry semantics.
public sealed class LocaleFormatterReentryTests
{
    [Fact]
    public void FormatterDuringInteractionRefreshCannotOverwriteANestedAcceptedScene()
    {
        UiSymbolId owner = new("Hatifect.Tests", "text/reentry");
        using var publication = new UiPublication(owner);
        UiSymbolId element = owner.Child("element/state");
        var source = publication.State(owner.Child("source/state"), 1,
            UiSourceTypes.Scalar<int>(owner.Child("type/count"), false));
        UiPortalHostSession? host = null;
        UiScene? replacement = null;
        bool armed = false;
        int attempts = 0;
        Exception? nestedError = null;
        var placement = new UiHostPlacementContext(new UiRect(0, 0, 1280, 720));
        var experience = new UiExperienceBuilder(owner, "Original")
            .Element(element, "State", "State", source, UiCapabilities.Monitor)
            .Actions("Actions", new UiActionDefinition(owner.Child("action/send"), "Send", () => { }))
            .FormatText<int>(element, (value, _) =>
            {
                if (armed)
                {
                    armed = false;
                    attempts++;
                    nestedError = Record.Exception(() => host!.UpdateRoot(replacement!, placement));
                }
                return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }).Build();
        var registry = new UiRegistryBuilder().Window(owner, "Original", () => experience).Freeze();
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        var scene = composer.Compose(new UiInvocationService(registry).Invoke(owner, UiPresentationProfiles.Wide));
        UiSymbolId other = owner.Child("other");
        var otherExperience = new UiExperienceBuilder(other, "Replacement").Monitor("Other", new UiState<string>("Other")).Build();
        var otherRegistry = new UiRegistryBuilder().Window(other, "Replacement", () => otherExperience).Freeze();
        replacement = new UiSceneComposer(UiThemePresets.Dark(), otherRegistry).Compose(
            new UiInvocationService(otherRegistry).Invoke(other, UiPresentationProfiles.Wide));
        host = new UiPortalHostSession(scene, placement, new Platform());
        try
        {
            armed = true;
            Assert.Throws<InvalidOperationException>(() => host.MoveFocus(UiNavigationDirection.Next));
            Assert.Equal(1, attempts);
            Assert.Null(nestedError);
            Assert.Same(replacement, host.Root.Scene);
            Assert.Equal(other, host.Root.Scene.Experience);
            Assert.Equal("Replacement", host.Root.Scene.DisplayName);
        }
        finally { host.Deactivate(); }
    }

    private sealed class Platform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * typography.Size * .6f, availableWidth), typography.Size * typography.LineHeight);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
