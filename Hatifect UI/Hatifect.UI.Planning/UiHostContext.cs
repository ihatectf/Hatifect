using System;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Planning;

public enum UiHostKind
{
    Window = 0,
    Popup = 1,
    Sheet = 2,
    Context = 3,
    Terminal = 4,
    Fullscreen = 5,
    Overlay = 6
}

public sealed record UiHostContext(UiHostKind HostKind, UiSymbolId Profile)
{
    public UiHostContext(UiHostKind hostKind, UiPresentationProfile profile)
        : this(hostKind, (profile ?? throw new ArgumentNullException(nameof(profile))).Id) { }

    private UiHostContext(UiHostKind hostKind, UiPresentationProfile profile, UiEnvironment environment)
        : this(hostKind, profile)
        => Environment = environment;

    public static UiHostContext InEnvironment(UiHostKind hostKind, UiEnvironment environment)
        => new(hostKind, UiPresentationProfiles.Resolve(environment), environment);

    /// <summary>Null only for the legacy caller-selected profile path.</summary>
    public UiEnvironment? Environment { get; }
}

public sealed record UiPresentationProfile(UiSymbolId Id, string Name);

public static class UiPresentationProfiles
{
    public static readonly UiPresentationProfile Wide = Create("Wide");
    public static readonly UiPresentationProfile Medium = Create("Medium");
    public static readonly UiPresentationProfile Compact = Create("Compact");
    public static readonly UiPresentationProfile Controller = Create("Controller");

    /// <summary>Framework policy over logical width; platform scale is already reflected in it.</summary>
    public static UiPresentationProfile Resolve(UiEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.InputMode == UiInputMode.Controller) return Controller;
        if (environment.Viewport.Width < 720) return Compact;
        if (environment.Viewport.Width < 1100) return Medium;
        return Wide;
    }

    private static UiPresentationProfile Create(string name)
        => new(new UiSymbolId("Hatifect.UI", $"profile/{name}"), name);
}
