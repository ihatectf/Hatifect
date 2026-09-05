using System;
using Hatifect.UI.Planning;

namespace Hatifect.UI.Runtime.Hosting;

public enum UiWindowChrome
{
    Standard,
    Tool,
    Borderless,
    None
}

public enum UiDismissPolicy
{
    Explicit,
    Escape,
    OutsideOrEscape,
    HostControlled
}

public enum UiModalPolicy
{
    Modeless,
    Modal,
    HostControlled
}

public enum UiFocusScopePolicy
{
    Shared,
    Contained,
    Trapped
}

public enum UiPopupPlacement
{
    Automatic,
    Anchor,
    Pointer,
    Center
}

/// <summary>Host behavior owned by the framework, never by the domain Experience.</summary>
public sealed record UiHostPolicy
{
    public UiHostPolicy(
        UiHostKind kind,
        UiWindowChrome chrome,
        UiDismissPolicy dismiss,
        UiModalPolicy modal,
        UiFocusScopePolicy focus,
        UiPopupPlacement popupPlacement,
        UiSymbolId? customPolicy = null)
    {
        if (customPolicy is { } id && !id.IsValid)
            throw new ArgumentException("A custom host policy ID must be valid.", nameof(customPolicy));
        if (kind is not (UiHostKind.Popup or UiHostKind.Context) && popupPlacement != UiPopupPlacement.Automatic)
            throw new ArgumentException("Explicit popup placement is valid only for popup and context hosts.", nameof(popupPlacement));
        Kind = kind;
        Chrome = chrome;
        Dismiss = dismiss;
        Modal = modal;
        Focus = focus;
        PopupPlacement = popupPlacement;
        CustomPolicy = customPolicy;
    }

    public UiHostKind Kind { get; }
    public UiWindowChrome Chrome { get; }
    public UiDismissPolicy Dismiss { get; }
    public UiModalPolicy Modal { get; }
    public UiFocusScopePolicy Focus { get; }
    public UiPopupPlacement PopupPlacement { get; }
    public UiSymbolId? CustomPolicy { get; }
}

public static class UiHostPolicies
{
    public static readonly UiHostPolicy Window = new(
        UiHostKind.Window, UiWindowChrome.Standard, UiDismissPolicy.Escape,
        UiModalPolicy.Modeless, UiFocusScopePolicy.Contained, UiPopupPlacement.Automatic);

    public static readonly UiHostPolicy Popup = new(
        UiHostKind.Popup, UiWindowChrome.Tool, UiDismissPolicy.OutsideOrEscape,
        UiModalPolicy.Modeless, UiFocusScopePolicy.Contained, UiPopupPlacement.Anchor);

    public static readonly UiHostPolicy Modal = new(
        UiHostKind.Popup, UiWindowChrome.Standard, UiDismissPolicy.Explicit,
        UiModalPolicy.Modal, UiFocusScopePolicy.Trapped, UiPopupPlacement.Center);

    public static readonly UiHostPolicy Sheet = new(
        UiHostKind.Sheet, UiWindowChrome.Tool, UiDismissPolicy.Escape,
        UiModalPolicy.HostControlled, UiFocusScopePolicy.Contained, UiPopupPlacement.Automatic);

    public static readonly UiHostPolicy Context = new(
        UiHostKind.Context, UiWindowChrome.None, UiDismissPolicy.OutsideOrEscape,
        UiModalPolicy.Modeless, UiFocusScopePolicy.Contained, UiPopupPlacement.Pointer);

    public static readonly UiHostPolicy Terminal = new(
        UiHostKind.Terminal, UiWindowChrome.None, UiDismissPolicy.HostControlled,
        UiModalPolicy.HostControlled, UiFocusScopePolicy.Shared, UiPopupPlacement.Automatic);

    public static readonly UiHostPolicy Fullscreen = new(
        UiHostKind.Fullscreen, UiWindowChrome.None, UiDismissPolicy.HostControlled,
        UiModalPolicy.HostControlled, UiFocusScopePolicy.Contained, UiPopupPlacement.Automatic);

    public static readonly UiHostPolicy Overlay = new(
        UiHostKind.Overlay, UiWindowChrome.None, UiDismissPolicy.HostControlled,
        UiModalPolicy.Modeless, UiFocusScopePolicy.Shared, UiPopupPlacement.Automatic);
}

/// <summary>
/// Internal host recipes under first-party dogfooding. They remain outside the supported API until
/// their product uses and platform behavior have passed the SDK review gate.
/// </summary>
internal static class UiProvisionalHostPolicies
{
    internal static readonly UiSymbolId OverlayTopRightId =
        new("Hatifect.UI", "host-policy/OverlayTopRight");
    internal static readonly UiSymbolId OverlayCenteredId =
        new("Hatifect.UI", "host-policy/OverlayCentered");

    internal static readonly UiHostPolicy OverlayTopRight = new(
        UiHostKind.Overlay,
        UiWindowChrome.None,
        UiDismissPolicy.HostControlled,
        UiModalPolicy.Modeless,
        UiFocusScopePolicy.Shared,
        UiPopupPlacement.Automatic,
        OverlayTopRightId);

    internal static UiHostPolicy OverlayCentered(bool closeOnOutsidePointer) => new(
        UiHostKind.Overlay,
        UiWindowChrome.Standard,
        closeOnOutsidePointer ? UiDismissPolicy.OutsideOrEscape : UiDismissPolicy.Escape,
        UiModalPolicy.Modal,
        UiFocusScopePolicy.Trapped,
        UiPopupPlacement.Automatic,
        OverlayCenteredId);
}
