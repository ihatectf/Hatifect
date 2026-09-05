using System;

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
}

public sealed record UiPresentationProfile(UiSymbolId Id, string Name);

public static class UiPresentationProfiles
{
    public static readonly UiPresentationProfile Wide = Create("Wide");
    public static readonly UiPresentationProfile Medium = Create("Medium");
    public static readonly UiPresentationProfile Compact = Create("Compact");
    public static readonly UiPresentationProfile Controller = Create("Controller");

    private static UiPresentationProfile Create(string name)
        => new(new UiSymbolId("Hatifect.UI", $"profile/{name}"), name);
}
