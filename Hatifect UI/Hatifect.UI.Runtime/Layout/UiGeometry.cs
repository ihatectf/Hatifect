using System;
using Hatifect.UI.Runtime.Visual;

namespace Hatifect.UI.Runtime.Layout;

internal readonly record struct UiPoint
{
    public UiPoint(float x, float y)
    {
        if (!float.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!float.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        X = x;
        Y = y;
    }

    public float X { get; }
    public float Y { get; }
}

internal readonly record struct UiSize
{
    public UiSize(float width, float height)
    {
        if (!float.IsFinite(width) || width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(height) || height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
    }

    public float Width { get; }
    public float Height { get; }
}

internal readonly record struct UiRect
{
    public UiRect(float x, float y, float width, float height)
    {
        if (!float.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!float.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!float.IsFinite(width) || width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(height) || height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public float X { get; }
    public float Y { get; }
    public float Width { get; }
    public float Height { get; }
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public bool Contains(UiPoint point)
        => point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;

    public UiRect Inset(UiThickness thickness)
    {
        float width = Math.Max(0, Width - thickness.Left - thickness.Right);
        float height = Math.Max(0, Height - thickness.Top - thickness.Bottom);
        return new UiRect(X + Math.Min(thickness.Left, Width), Y + Math.Min(thickness.Top, Height), width, height);
    }

    public static UiRect Intersect(UiRect left, UiRect right)
    {
        float x = Math.Max(left.X, right.X);
        float y = Math.Max(left.Y, right.Y);
        float width = Math.Max(0, Math.Min(left.Right, right.Right) - x);
        float height = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - y);
        return new UiRect(x, y, width, height);
    }
}
