using System;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Visual;

namespace Hatifect.UI.Runtime.Hosting;

internal enum UiHostPlacementKind
{
    Fill,
    Center,
    TopRight,
    SheetBottom,
    AnchorBelow,
    AnchorAbove,
    PointerDownRight,
    PointerDownLeft,
    PointerUpRight,
    PointerUpLeft
}

internal sealed record UiHostPlacementMetrics
{
    public UiHostPlacementMetrics(
        float edgeInset = 12,
        float popupGap = 8,
        float windowMinimumWidth = 240,
        float windowMinimumHeight = 120,
        float sheetMaximumHeightRatio = 0.6f)
    {
        ValidateNonNegative(edgeInset, nameof(edgeInset));
        ValidateNonNegative(popupGap, nameof(popupGap));
        ValidateNonNegative(windowMinimumWidth, nameof(windowMinimumWidth));
        ValidateNonNegative(windowMinimumHeight, nameof(windowMinimumHeight));
        if (!float.IsFinite(sheetMaximumHeightRatio) || sheetMaximumHeightRatio is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(sheetMaximumHeightRatio));
        EdgeInset = edgeInset;
        PopupGap = popupGap;
        WindowMinimumWidth = windowMinimumWidth;
        WindowMinimumHeight = windowMinimumHeight;
        SheetMaximumHeightRatio = sheetMaximumHeightRatio;
    }

    public float EdgeInset { get; }
    public float PopupGap { get; }
    public float WindowMinimumWidth { get; }
    public float WindowMinimumHeight { get; }
    public float SheetMaximumHeightRatio { get; }

    public static UiHostPlacementMetrics Default { get; } = new();

    private static void ValidateNonNegative(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(name);
    }
}

/// <summary>
/// Host input for placement. It is intentionally internal: domain Experiences publish semantics,
/// while the platform host supplies current viewport, anchor, and pointer geometry.
/// </summary>
internal sealed record UiHostPlacementContext
{
    public UiHostPlacementContext(
        UiRect viewport,
        UiRect? anchor = null,
        UiPoint? pointer = null,
        UiHostPlacementMetrics? metrics = null)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0)
            throw new ArgumentException("The host viewport must have finite positive geometry.", nameof(viewport));
        Viewport = viewport;
        Anchor = anchor;
        Pointer = pointer;
        Metrics = metrics ?? UiHostPlacementMetrics.Default;
    }

    public UiRect Viewport { get; }
    public UiRect? Anchor { get; }
    public UiPoint? Pointer { get; }
    public UiHostPlacementMetrics Metrics { get; }
}

internal sealed record UiHostPlacementResult(
    UiRect Bounds,
    UiHostPlacementKind Kind,
    bool UsedFallback,
    bool WasClamped);

/// <summary>Deterministic host-owned placement with explicit fallback provenance.</summary>
internal sealed class UiHostPlacementEngine
{
    public UiHostPlacementResult Place(
        UiHostPolicy policy,
        UiHostPlacementContext context,
        UiSize desired,
        UiSize minimum)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);
        UiRect viewport = context.Viewport;
        UiHostPlacementMetrics metrics = context.Metrics;

        if (policy.Kind is UiHostKind.Terminal or UiHostKind.Fullscreen)
            return new UiHostPlacementResult(viewport, UiHostPlacementKind.Fill, false, false);

        UiRect safe = viewport.Inset(new UiThickness(metrics.EdgeInset));
        if (policy.Kind == UiHostKind.Overlay)
            return PlaceOverlay(policy, safe, viewport, desired, minimum);
        if (policy.Kind == UiHostKind.Sheet)
            return PlaceSheet(safe, desired, minimum, metrics);
        return PlacePopupOrWindow(policy, context, safe, desired, minimum, metrics);
    }

    private static UiHostPlacementResult PlaceOverlay(
        UiHostPolicy policy,
        UiRect safe,
        UiRect viewport,
        UiSize desired,
        UiSize minimum)
    {
        if (policy.CustomPolicy == UiProvisionalHostPolicies.OverlayCenteredId)
        {
            UiSize centeredSize = Constrain(desired, minimum.Width, minimum.Height, safe);
            return Center(safe, centeredSize, usedFallback: false, desired);
        }

        if (policy.CustomPolicy != UiProvisionalHostPolicies.OverlayTopRightId)
            return new UiHostPlacementResult(viewport, UiHostPlacementKind.Fill, false, false);

        UiSize overlaySize = Constrain(desired, minimum.Width, minimum.Height, safe);
        return new UiHostPlacementResult(
            new UiRect(
                safe.Right - overlaySize.Width,
                safe.Y,
                overlaySize.Width,
                overlaySize.Height),
            UiHostPlacementKind.TopRight,
            false,
            overlaySize.Width + 0.01f < desired.Width || overlaySize.Height + 0.01f < desired.Height);
    }

    private static UiHostPlacementResult PlaceSheet(
        UiRect safe,
        UiSize desired,
        UiSize minimum,
        UiHostPlacementMetrics metrics)
    {
        float height = Math.Min(
            safe.Height,
            Math.Max(minimum.Height, Math.Min(desired.Height, safe.Height * metrics.SheetMaximumHeightRatio)));
        var bounds = new UiRect(safe.X, safe.Bottom - height, safe.Width, height);
        return new UiHostPlacementResult(
            bounds,
            UiHostPlacementKind.SheetBottom,
            false,
            height + 0.01f < desired.Height);
    }

    private static UiHostPlacementResult PlacePopupOrWindow(
        UiHostPolicy policy,
        UiHostPlacementContext context,
        UiRect safe,
        UiSize desired,
        UiSize minimum,
        UiHostPlacementMetrics metrics)
    {
        float minimumWidth = policy.Kind == UiHostKind.Window
            ? Math.Max(minimum.Width, metrics.WindowMinimumWidth)
            : minimum.Width;
        float minimumHeight = policy.Kind == UiHostKind.Window
            ? Math.Max(minimum.Height, metrics.WindowMinimumHeight)
            : minimum.Height;
        UiSize size = Constrain(desired, minimumWidth, minimumHeight, safe);

        if (policy.Kind == UiHostKind.Window || policy.PopupPlacement == UiPopupPlacement.Center)
            return Center(safe, size, usedFallback: false, desired);

        UiPopupPlacement placement = ResolveAutomatic(policy, context);
        if (placement == UiPopupPlacement.Anchor && context.Anchor is { } anchor)
            return PlaceAtAnchor(safe, anchor, size, desired, metrics.PopupGap);
        if (placement == UiPopupPlacement.Pointer && context.Pointer is { } pointer)
            return PlaceAtPointer(safe, pointer, size, desired, metrics.PopupGap);
        return Center(safe, size, usedFallback: true, desired);
    }

    private static UiPopupPlacement ResolveAutomatic(UiHostPolicy policy, UiHostPlacementContext context)
    {
        if (policy.PopupPlacement != UiPopupPlacement.Automatic) return policy.PopupPlacement;
        if (policy.Kind == UiHostKind.Context && context.Pointer != null) return UiPopupPlacement.Pointer;
        if (context.Anchor != null) return UiPopupPlacement.Anchor;
        if (context.Pointer != null) return UiPopupPlacement.Pointer;
        return UiPopupPlacement.Center;
    }

    private static UiHostPlacementResult PlaceAtAnchor(
        UiRect safe,
        UiRect anchor,
        UiSize size,
        UiSize desired,
        float gap)
    {
        float below = safe.Bottom - (anchor.Bottom + gap);
        float above = anchor.Y - gap - safe.Y;
        bool placeBelow = below + 0.01f >= size.Height || below >= above;
        float rawX = anchor.X;
        float rawY = placeBelow ? anchor.Bottom + gap : anchor.Y - gap - size.Height;
        UiRect bounds = Clamp(safe, rawX, rawY, size);
        bool clamped = IsClamped(bounds, rawX, rawY, desired, size);
        return new UiHostPlacementResult(
            bounds,
            placeBelow ? UiHostPlacementKind.AnchorBelow : UiHostPlacementKind.AnchorAbove,
            false,
            clamped);
    }

    private static UiHostPlacementResult PlaceAtPointer(
        UiRect safe,
        UiPoint pointer,
        UiSize size,
        UiSize desired,
        float gap)
    {
        bool right = pointer.X + gap + size.Width <= safe.Right;
        bool down = pointer.Y + gap + size.Height <= safe.Bottom;
        float rawX = right ? pointer.X + gap : pointer.X - gap - size.Width;
        float rawY = down ? pointer.Y + gap : pointer.Y - gap - size.Height;
        UiRect bounds = Clamp(safe, rawX, rawY, size);
        UiHostPlacementKind kind = (right, down) switch
        {
            (true, true) => UiHostPlacementKind.PointerDownRight,
            (false, true) => UiHostPlacementKind.PointerDownLeft,
            (true, false) => UiHostPlacementKind.PointerUpRight,
            _ => UiHostPlacementKind.PointerUpLeft
        };
        return new UiHostPlacementResult(bounds, kind, false, IsClamped(bounds, rawX, rawY, desired, size));
    }

    private static UiHostPlacementResult Center(UiRect safe, UiSize size, bool usedFallback, UiSize desired)
    {
        float x = safe.X + (safe.Width - size.Width) / 2;
        float y = safe.Y + (safe.Height - size.Height) / 2;
        return new UiHostPlacementResult(
            new UiRect(x, y, size.Width, size.Height),
            UiHostPlacementKind.Center,
            usedFallback,
            size.Width + 0.01f < desired.Width || size.Height + 0.01f < desired.Height);
    }

    private static UiSize Constrain(UiSize desired, float minimumWidth, float minimumHeight, UiRect safe)
        => new(
            Math.Min(safe.Width, Math.Max(desired.Width, minimumWidth)),
            Math.Min(safe.Height, Math.Max(desired.Height, minimumHeight)));

    private static UiRect Clamp(UiRect safe, float x, float y, UiSize size)
    {
        float maxX = Math.Max(safe.X, safe.Right - size.Width);
        float maxY = Math.Max(safe.Y, safe.Bottom - size.Height);
        return new UiRect(
            Math.Min(Math.Max(x, safe.X), maxX),
            Math.Min(Math.Max(y, safe.Y), maxY),
            size.Width,
            size.Height);
    }

    private static bool IsClamped(UiRect bounds, float rawX, float rawY, UiSize desired, UiSize actual)
        => Math.Abs(bounds.X - rawX) > 0.01f ||
           Math.Abs(bounds.Y - rawY) > 0.01f ||
           actual.Width + 0.01f < desired.Width ||
           actual.Height + 0.01f < desired.Height;
}
