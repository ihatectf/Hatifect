using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Tests;

// Test-only observations of the real compositor/host, with no consumer dependency or private production API exposure.
public sealed class ExperienceTextProbe : IDisposable
{
    private readonly UiSymbolId _id;
    private readonly UiInvocationService _invoker;
    private readonly UiSceneComposer _composer;
    private UiPortalHostSession? _host;
    private bool _disposed;
    private static readonly UiHostPlacementContext Placement = new(new UiRect(0, 0, 1280, 720));

    public ExperienceTextProbe(UiExperienceDefinition experience)
    {
        _id = experience.Id;
        var registry = new UiRegistryBuilder().Window(_id, "Test", () => { Activations++; return experience; },
            lifetime: UiExperienceLifetime.Cached).Freeze();
        _invoker = new UiInvocationService(registry);
        _composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
    }

    public int Activations { get; private set; }
    public UiExperienceDefinition? ActiveExperience { get; private set; }

    public ExperienceTextSnapshot Compose(string locale)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ExperienceTextProbe));
        var environment = new UiEnvironment(new UiEnvironmentViewport(1280, 720), 1, UiInputMode.MouseKeyboard,
            locale, new UiSymbolId("Hatifect.Tests", "theme/dark"));
        var invocation = _invoker.InvokeInEnvironment(_id, environment);
        UiScene scene = _composer.Compose(invocation, interaction: _host?.Root.Interactions.Snapshot);
        if (_host is null) _host = new UiPortalHostSession(scene, Placement, new Platform());
        else _host.UpdateRoot(scene, Placement);
        ActiveExperience = invocation.Experience;
        return new ExperienceTextSnapshot(scene, _host.Root.Accessibility.Root);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host?.Deactivate();
        _host = null;
    }

    private sealed class Platform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * typography.Size * .6f, availableWidth), typography.Size * typography.LineHeight);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}

// Retain the actual accepted objects. Accessors observe them again so old-frame assertions do not merely test copied strings.
public sealed class ExperienceTextSnapshot
{
    private readonly UiScene _scene;
    private readonly UiAccessibilityNodeSnapshot _accessibility;
    internal ExperienceTextSnapshot(UiScene scene, UiAccessibilityNodeSnapshot accessibility)
    { _scene = scene; _accessibility = accessibility; }

    public string DisplayName => _scene.DisplayName;
    public IReadOnlyList<ExperienceTextValue> Values => Nodes(_scene.Root).OfType<UiSourceSceneNode>()
        .Select(value => new ExperienceTextValue(value.Id, value.SemanticName, value.DisplayText)).ToArray();
    public IReadOnlyList<ExperienceTextAction> Actions => Nodes(_scene.Root).OfType<UiButtonSceneNode>()
        .Select(value => new ExperienceTextAction(value.Id, value.Label, value.Action)).ToArray();
    public IReadOnlyList<ExperienceAccessibleText> Accessibility => Accessible(_accessibility)
        .Select(value => new ExperienceAccessibleText(value.Id, value.Name, value.Value)).ToArray();

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var descendant in Nodes(child)) yield return descendant;
    }
    private static IEnumerable<UiAccessibilityNodeSnapshot> Accessible(UiAccessibilityNodeSnapshot node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var descendant in Accessible(child)) yield return descendant;
    }
}

public sealed record ExperienceTextValue(UiSymbolId Id, string SemanticName, string DisplayText);
public sealed record ExperienceTextAction(UiSymbolId Id, string Label, UiActionDefinition Action);
public sealed record ExperienceAccessibleText(UiSymbolId Id, string? Name, string? Value);
