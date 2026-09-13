using System;

namespace Hatifect.UI.Runtime.Visual;

public readonly record struct UiColor(byte R, byte G, byte B, byte A = byte.MaxValue)
{
    public static UiColor FromRgb(uint rgb, byte alpha = byte.MaxValue)
        => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, alpha);
}

public readonly record struct UiSpacing
{
    public UiSpacing(float value)
    {
        if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public float Value { get; }
}

public readonly record struct UiCornerRadius
{
    public UiCornerRadius(float value)
    {
        if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public float Value { get; }
}

public readonly record struct UiThickness
{
    public UiThickness(float left, float top, float right, float bottom)
    {
        Validate(left, nameof(left));
        Validate(top, nameof(top));
        Validate(right, nameof(right));
        Validate(bottom, nameof(bottom));
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public UiThickness(float uniform) : this(uniform, uniform, uniform, uniform) { }

    public float Left { get; }
    public float Top { get; }
    public float Right { get; }
    public float Bottom { get; }

    private static void Validate(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(name);
    }
}

public enum UiSurfaceKind
{
    Solid,
    Texture,
    NineSlice,
    Vanilla
}

/// <summary>Renderer-neutral surface description supporting modern and pixel/vanilla assets.</summary>
public sealed record UiSurface
{
    private UiSurface(
        UiSurfaceKind kind,
        UiColor tint,
        UiSymbolId? asset,
        UiThickness slices,
        bool pixelSnap)
    {
        if (kind != UiSurfaceKind.Solid && asset is not { IsValid: true })
            throw new ArgumentException("A textured surface requires a stable asset ID.", nameof(asset));
        Kind = kind;
        Tint = tint;
        Asset = asset;
        Slices = slices;
        PixelSnap = pixelSnap;
    }

    public UiSurfaceKind Kind { get; }
    public UiColor Tint { get; }
    public UiSymbolId? Asset { get; }
    public UiThickness Slices { get; }
    public bool PixelSnap { get; }

    public static UiSurface Solid(UiColor color)
        => new(UiSurfaceKind.Solid, color, null, default, pixelSnap: false);

    public static UiSurface Texture(UiSymbolId asset, UiColor tint, bool pixelSnap = false)
        => new(UiSurfaceKind.Texture, tint, asset, default, pixelSnap);

    public static UiSurface NineSlice(UiSymbolId asset, UiThickness slices, UiColor tint, bool pixelSnap = true)
        => new(UiSurfaceKind.NineSlice, tint, asset, slices, pixelSnap);

    public static UiSurface Vanilla(UiSymbolId asset, UiColor tint)
        => new(UiSurfaceKind.Vanilla, tint, asset, default, pixelSnap: true);
}

public enum UiFontWeight
{
    Regular,
    Medium,
    Bold
}

public sealed record UiTypography
{
    public UiTypography(string family, float size, float lineHeight, UiFontWeight weight = UiFontWeight.Regular)
    {
        if (string.IsNullOrWhiteSpace(family)) throw new ArgumentException("A font family is required.", nameof(family));
        if (!float.IsFinite(size) || size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (!float.IsFinite(lineHeight) || lineHeight < 1) throw new ArgumentOutOfRangeException(nameof(lineHeight));
        Family = family;
        Size = size;
        LineHeight = lineHeight;
        Weight = weight;
    }

    public string Family { get; }
    public float Size { get; }
    public float LineHeight { get; }
    public UiFontWeight Weight { get; }
}

public readonly record struct UiBorder
{
    public UiBorder(UiColor color, float width)
    {
        if (!float.IsFinite(width) || width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        Color = color;
        Width = width;
    }

    public UiColor Color { get; }
    public float Width { get; }
}

public sealed record UiElevation
{
    public UiElevation(float offsetX, float offsetY, float blur, UiColor color)
    {
        if (!float.IsFinite(offsetX)) throw new ArgumentOutOfRangeException(nameof(offsetX));
        if (!float.IsFinite(offsetY)) throw new ArgumentOutOfRangeException(nameof(offsetY));
        if (!float.IsFinite(blur) || blur < 0) throw new ArgumentOutOfRangeException(nameof(blur));
        OffsetX = offsetX;
        OffsetY = offsetY;
        Blur = blur;
        Color = color;
    }

    public float OffsetX { get; }
    public float OffsetY { get; }
    public float Blur { get; }
    public UiColor Color { get; }
}

public readonly record struct UiTransform
{
    public UiTransform(float translateX, float translateY, float scale = 1, float rotationDegrees = 0)
    {
        if (!float.IsFinite(translateX)) throw new ArgumentOutOfRangeException(nameof(translateX));
        if (!float.IsFinite(translateY)) throw new ArgumentOutOfRangeException(nameof(translateY));
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (!float.IsFinite(rotationDegrees)) throw new ArgumentOutOfRangeException(nameof(rotationDegrees));
        TranslateX = translateX;
        TranslateY = translateY;
        Scale = scale;
        RotationDegrees = rotationDegrees;
    }

    public float TranslateX { get; }
    public float TranslateY { get; }
    public float Scale { get; }
    public float RotationDegrees { get; }
}

public enum UiEasing
{
    Linear,
    EaseOut,
    EaseInOut
}

public sealed record UiMotion
{
    public UiMotion(TimeSpan duration, UiEasing easing = UiEasing.EaseOut)
    {
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        Duration = duration;
        Easing = easing;
    }

    public TimeSpan Duration { get; }
    public UiEasing Easing { get; }
}

public readonly record struct UiOpacity
{
    public UiOpacity(float value)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public float Value { get; }
}
