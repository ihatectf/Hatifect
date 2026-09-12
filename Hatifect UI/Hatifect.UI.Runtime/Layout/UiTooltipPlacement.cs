using System;

namespace Hatifect.UI.Runtime.Layout;

/// <summary>Positions transient help without changing the accepted scene layout.</summary>
internal static class UiTooltipPlacement
{
    internal const float MaximumLineCharacters = 24;

    public static UiRect Place(UiRect anchor, UiSize desired, UiRect viewport, float gap)
    {
        if (!float.IsFinite(gap) || gap < 0) throw new ArgumentOutOfRangeException(nameof(gap));
        float width = Math.Min(desired.Width, viewport.Width);
        float height = Math.Min(desired.Height, viewport.Height);
        float x = Math.Clamp(anchor.X, viewport.X, Math.Max(viewport.X, viewport.Right - width));
        float below = anchor.Bottom + gap;
        float above = anchor.Y - gap - height;
        float y = below + height <= viewport.Bottom
            ? below
            : above >= viewport.Y
                ? above
                : Math.Clamp(below, viewport.Y, Math.Max(viewport.Y, viewport.Bottom - height));
        return new UiRect(x, y, width, height);
    }
}
