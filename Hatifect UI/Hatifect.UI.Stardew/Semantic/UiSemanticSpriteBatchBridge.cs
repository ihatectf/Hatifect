using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Hatifect.UI.Runtime.Caching;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Rendering;
using RuntimeColor = Hatifect.UI.Runtime.Visual.UiColor;
using RuntimeRect = Hatifect.UI.Runtime.Layout.UiRect;
using RuntimeSize = Hatifect.UI.Runtime.Layout.UiSize;
using RuntimeSurface = Hatifect.UI.Runtime.Visual.UiSurface;
using RuntimeSurfaceKind = Hatifect.UI.Runtime.Visual.UiSurfaceKind;
using RuntimeSymbolId = Hatifect.UI.UiSymbolId;
using RuntimeTextOverflow = Hatifect.UI.Runtime.Layout.UiTextOverflow;
using RuntimeThickness = Hatifect.UI.Runtime.Visual.UiThickness;
using RuntimeTransform = Hatifect.UI.Runtime.Visual.UiTransform;
using RuntimeTypography = Hatifect.UI.Runtime.Visual.UiTypography;

namespace Hatifect.UI.Stardew.Semantic;

/// <summary>
/// Direct backend for the semantic runtime. Borrowed fonts/textures are resolved by the host;
/// the bridge owns a bounded text-layout index plus its generated pixel and rounded masks,
/// and only the generated GPU resources are disposed here.
/// </summary>
internal sealed class UiSemanticSpriteBatchBridge : IUiPlatformBridge, IDisposable
{
    private const int MaximumRoundedMasks = 32;
    private const int MaximumTextLayouts = 512;
    private readonly GraphicsDevice _graphicsDevice;
    private readonly Func<RuntimeTypography, SpriteFont> _resolveFont;
    private readonly Func<RuntimeSymbolId, Texture2D> _resolveTexture;
    private readonly UiBoundedCache<MaskKey, Texture2D> _roundedMasks =
        new(MaximumRoundedMasks, release: mask => mask.Dispose());
    private readonly UiBoundedCache<TextLayoutKey, string[]> _textLayouts = new(MaximumTextLayouts);
    private SpriteBatch? _batch;
    private Texture2D? _pixel;
    private bool _disposed;

    public UiSemanticSpriteBatchBridge(
        GraphicsDevice graphicsDevice,
        Func<RuntimeTypography, SpriteFont> resolveFont,
        Func<RuntimeSymbolId, Texture2D> resolveTexture)
    {
        _graphicsDevice = graphicsDevice ?? throw new ArgumentNullException(nameof(graphicsDevice));
        _resolveFont = resolveFont ?? throw new ArgumentNullException(nameof(resolveFont));
        _resolveTexture = resolveTexture ?? throw new ArgumentNullException(nameof(resolveTexture));
    }

    public void Render(UiPortalHostSession host, SpriteBatch batch)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(batch);
        if (_batch != null) throw new InvalidOperationException("A semantic UI frame is already rendering.");
        _batch = batch;
        try
        {
            host.Render();
        }
        finally
        {
            _batch = null;
        }
    }

    public RuntimeSize Measure(
        string text,
        RuntimeTypography typography,
        float availableWidth,
        RuntimeTextOverflow overflow)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(typography);
        if (!float.IsFinite(availableWidth) || availableWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(availableWidth));
        SpriteFont font = ResolveFont(typography);
        float scale = FontScale(font, typography);
        IReadOnlyList<string> lines = LayoutLines(text, font, scale, availableWidth, overflow);
        float width = lines.Count == 0 ? 0 : lines.Max(line => Width(font, line, scale));
        float height = Math.Max(1, lines.Count) * LineHeight(typography);
        return new RuntimeSize(Math.Min(availableWidth, width), height);
    }

    public void DrawSurface(UiSurfacePrimitive primitive)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(primitive);
        SpriteBatch batch = CurrentBatch();
        RuntimeRect bounds = Transform(primitive.Bounds, primitive.Transform);
        RuntimeRect clip = primitive.Clip;
        float opacity = primitive.Opacity.Value;

        if (primitive.Elevation is { } elevation && elevation.Color.A > 0)
        {
            RuntimeRect shadow = new(
                bounds.X + elevation.OffsetX,
                bounds.Y + elevation.OffsetY,
                bounds.Width,
                bounds.Height);
            DrawRounded(batch, shadow, primitive.Radius.Value, 0, elevation.Color, opacity, clip);
        }

        switch (primitive.Surface.Kind)
        {
            case RuntimeSurfaceKind.Solid:
                DrawRounded(batch, bounds, primitive.Radius.Value, 0, primitive.Surface.Tint, opacity, clip);
                break;
            case RuntimeSurfaceKind.Texture:
            case RuntimeSurfaceKind.Vanilla:
                DrawTextureClipped(
                    batch,
                    ResolveTexture(primitive.Surface),
                    ToRectangle(bounds),
                    source: null,
                    ToColor(primitive.Surface.Tint, opacity),
                    ToRectangle(clip));
                break;
            case RuntimeSurfaceKind.NineSlice:
                Texture2D texture = ResolveTexture(primitive.Surface);
                DrawNineSlice(
                    batch,
                    texture,
                    new Rectangle(0, 0, texture.Width, texture.Height),
                    ToRectangle(bounds),
                    primitive.Surface.Slices,
                    ToColor(primitive.Surface.Tint, opacity),
                    ToRectangle(clip));
                break;
            default:
                throw new NotSupportedException($"Unsupported semantic surface '{primitive.Surface.Kind}'.");
        }

        if (primitive.Border is { Width: > 0 } border)
            DrawRounded(batch, bounds, primitive.Radius.Value, border.Width, border.Color, opacity, clip);
    }

    public void DrawText(UiTextPrimitive primitive)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(primitive);
        SpriteBatch batch = CurrentBatch();
        RuntimeRect bounds = Transform(primitive.Bounds, primitive.Transform);
        SpriteFont font = ResolveFont(primitive.Typography);
        float scale = FontScale(font, primitive.Typography) * primitive.Transform.Scale;
        float lineHeight = LineHeight(primitive.Typography) * primitive.Transform.Scale;
        IReadOnlyList<string> lines = LayoutLines(
            primitive.Text,
            font,
            scale,
            bounds.Width,
            primitive.Overflow);
        Color color = ToColor(primitive.Foreground, primitive.Opacity.Value);
        Rectangle clip = ToRectangle(primitive.Clip);
        float y = bounds.Y;
        foreach (string line in lines)
        {
            if (y >= bounds.Bottom) break;
            if (y >= clip.Top && y + lineHeight <= clip.Bottom)
            {
                string visible = FitClippedLine(
                    line, font, scale, bounds.X, clip.Left, clip.Right, out float xOffset);
                if (visible.Length > 0)
                {
                    batch.DrawString(
                        font,
                        visible,
                        new Vector2(bounds.X + xOffset, y),
                        color,
                        0,
                        Vector2.Zero,
                        scale,
                        SpriteEffects.None,
                        0);
                }
            }
            y += lineHeight;
        }
    }

    public void ReleaseGeneratedResources()
    {
        ThrowIfDisposed();
        if (_batch != null) throw new InvalidOperationException("Generated resources cannot reset during Render().");
        ReleaseGeneratedResourcesCore();
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            ReleaseGeneratedResourcesCore();
        }
        finally
        {
            _disposed = true;
            _batch = null;
        }
    }

    private void ReleaseGeneratedResourcesCore()
    {
        List<Exception>? failures = null;
        try
        {
            _pixel?.Dispose();
        }
        catch (Exception ex)
        {
            (failures ??= new List<Exception>()).Add(ex);
        }
        finally
        {
            _pixel = null;
        }

        try
        {
            _roundedMasks.Clear();
        }
        catch (AggregateException ex)
        {
            (failures ??= new List<Exception>()).AddRange(ex.InnerExceptions);
        }
        catch (Exception ex)
        {
            (failures ??= new List<Exception>()).Add(ex);
        }
        finally
        {
            _textLayouts.Clear();
        }

        if (failures != null)
            throw new AggregateException("One or more generated semantic Stardew resources failed to release.", failures);
    }

    private SpriteFont ResolveFont(RuntimeTypography typography)
        => _resolveFont(typography)
           ?? throw new InvalidOperationException($"Font resolver returned null for '{typography.Family}'.");

    private Texture2D ResolveTexture(RuntimeSurface surface)
    {
        if (surface.Asset is not { } asset)
            throw new InvalidOperationException($"Surface '{surface.Kind}' has no asset ID.");
        return _resolveTexture(asset)
               ?? throw new InvalidOperationException($"Texture resolver returned null for '{asset}'.");
    }

    private void DrawRounded(
        SpriteBatch batch,
        RuntimeRect bounds,
        float radiusValue,
        float borderValue,
        RuntimeColor color,
        float opacity,
        RuntimeRect clip)
    {
        Rectangle destination = ToRectangle(bounds);
        int radius = Math.Clamp((int)MathF.Round(radiusValue), 0, Math.Min(destination.Width, destination.Height) / 2);
        int border = Math.Max(0, (int)MathF.Ceiling(borderValue));
        if (radius <= 0)
        {
            if (border <= 0)
            {
                DrawTextureClipped(batch, Pixel, destination, null, ToColor(color, opacity), ToRectangle(clip));
                return;
            }
            DrawSquareBorder(batch, destination, border, ToColor(color, opacity), ToRectangle(clip));
            return;
        }

        border = Math.Min(border, radius);
        Texture2D mask = RoundedMask(radius, border);
        DrawNineSlice(
            batch,
            mask,
            new Rectangle(0, 0, mask.Width, mask.Height),
            destination,
            new RuntimeThickness(radius),
            ToColor(color, opacity),
            ToRectangle(clip));
    }

    private Texture2D RoundedMask(int radius, int border)
    {
        var key = new MaskKey(radius, border);
        if (_roundedMasks.TryGetValue(key, out Texture2D? existing)) return existing;

        int size = checked(radius * 2 + 1);
        var colors = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            bool outer = InsideRounded(x, y, size, radius);
            bool inner = border > 0 && border < radius &&
                         x >= border && y >= border && x < size - border && y < size - border &&
                         InsideRounded(x - border, y - border, size - border * 2, Math.Max(0, radius - border));
            byte coverage = outer && !inner ? byte.MaxValue : (byte)0;
            colors[y * size + x] = new Color(coverage, coverage, coverage, coverage);
        }
        var mask = new Texture2D(_graphicsDevice, size, size, false, SurfaceFormat.Color);
        mask.SetData(colors);
        _roundedMasks.Set(key, mask);
        return mask;
    }

    private static bool InsideRounded(int x, int y, int size, int radius)
    {
        if (radius <= 0) return x >= 0 && y >= 0 && x < size && y < size;
        float px = x + 0.5f;
        float py = y + 0.5f;
        float left = radius;
        float right = size - radius;
        if (px >= left && px <= right || py >= left && py <= right) return true;
        float cx = px < left ? left : right;
        float cy = py < left ? left : right;
        float dx = px - cx;
        float dy = py - cy;
        return dx * dx + dy * dy <= radius * radius;
    }

    private void DrawSquareBorder(
        SpriteBatch batch,
        Rectangle bounds,
        int thickness,
        Color color,
        Rectangle clip)
    {
        int value = Math.Min(thickness, Math.Min(bounds.Width, bounds.Height));
        DrawTextureClipped(batch, Pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width, value), null, color, clip);
        DrawTextureClipped(batch, Pixel, new Rectangle(bounds.X, bounds.Bottom - value, bounds.Width, value), null, color, clip);
        DrawTextureClipped(batch, Pixel, new Rectangle(bounds.X, bounds.Y, value, bounds.Height), null, color, clip);
        DrawTextureClipped(batch, Pixel, new Rectangle(bounds.Right - value, bounds.Y, value, bounds.Height), null, color, clip);
    }

    private static void DrawNineSlice(
        SpriteBatch batch,
        Texture2D texture,
        Rectangle source,
        Rectangle destination,
        RuntimeThickness slices,
        Color tint,
        Rectangle clip)
    {
        if (source.Width <= 0 || source.Height <= 0 || destination.Width <= 0 || destination.Height <= 0) return;
        int left = Math.Clamp((int)MathF.Round(slices.Left), 0, source.Width / 2);
        int right = Math.Clamp((int)MathF.Round(slices.Right), 0, source.Width - left);
        int top = Math.Clamp((int)MathF.Round(slices.Top), 0, source.Height / 2);
        int bottom = Math.Clamp((int)MathF.Round(slices.Bottom), 0, source.Height - top);
        int destinationLeft = Math.Min(left, destination.Width / 2);
        int destinationRight = Math.Min(right, destination.Width - destinationLeft);
        int destinationTop = Math.Min(top, destination.Height / 2);
        int destinationBottom = Math.Min(bottom, destination.Height - destinationTop);

        int[] sx = { source.X, source.X + left, source.Right - right };
        int[] sy = { source.Y, source.Y + top, source.Bottom - bottom };
        int[] sw = { left, source.Width - left - right, right };
        int[] sh = { top, source.Height - top - bottom, bottom };
        int[] dx = { destination.X, destination.X + destinationLeft, destination.Right - destinationRight };
        int[] dy = { destination.Y, destination.Y + destinationTop, destination.Bottom - destinationBottom };
        int[] dw = { destinationLeft, destination.Width - destinationLeft - destinationRight, destinationRight };
        int[] dh = { destinationTop, destination.Height - destinationTop - destinationBottom, destinationBottom };
        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 3; x++)
        {
            if (sw[x] <= 0 || sh[y] <= 0 || dw[x] <= 0 || dh[y] <= 0) continue;
            DrawTextureClipped(
                batch,
                texture,
                new Rectangle(dx[x], dy[y], dw[x], dh[y]),
                new Rectangle(sx[x], sy[y], sw[x], sh[y]),
                tint,
                clip);
        }
    }

    private static void DrawTextureClipped(
        SpriteBatch batch,
        Texture2D texture,
        Rectangle destination,
        Rectangle? source,
        Color tint,
        Rectangle clip)
    {
        Rectangle visible = Rectangle.Intersect(destination, clip);
        if (visible.Width <= 0 || visible.Height <= 0 || destination.Width <= 0 || destination.Height <= 0) return;
        Rectangle original = source ?? new Rectangle(0, 0, texture.Width, texture.Height);
        float scaleX = original.Width / (float)destination.Width;
        float scaleY = original.Height / (float)destination.Height;
        int sourceX = original.X + (int)MathF.Floor((visible.X - destination.X) * scaleX);
        int sourceY = original.Y + (int)MathF.Floor((visible.Y - destination.Y) * scaleY);
        int sourceRight = original.X + (int)MathF.Ceiling((visible.Right - destination.X) * scaleX);
        int sourceBottom = original.Y + (int)MathF.Ceiling((visible.Bottom - destination.Y) * scaleY);
        var clippedSource = new Rectangle(
            sourceX,
            sourceY,
            Math.Max(0, sourceRight - sourceX),
            Math.Max(0, sourceBottom - sourceY));
        if (clippedSource.Width <= 0 || clippedSource.Height <= 0) return;
        batch.Draw(texture, visible, clippedSource, tint);
    }

    private IReadOnlyList<string> LayoutLines(
        string? text,
        SpriteFont font,
        float scale,
        float availableWidth,
        RuntimeTextOverflow overflow)
    {
        var key = new TextLayoutKey(font, text ?? string.Empty, scale, availableWidth, overflow);
        if (_textLayouts.TryGetValue(key, out string[]? cached)) return cached;

        string[] created = BuildLines(text, font, scale, availableWidth, overflow);
        _textLayouts.Set(key, created);
        return created;
    }

    private static string[] BuildLines(
        string? text,
        SpriteFont font,
        float scale,
        float availableWidth,
        RuntimeTextOverflow overflow)
    {
        string normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        // Resolve unsupported direction before measuring or wrapping. The cached lines
        // are shared by Measure and DrawText; the semantic string remains unchanged.
        if (normalized.Contains('→') && !font.Characters.Contains('→'))
        {
            if (!font.Characters.Contains('-') || !font.Characters.Contains('>'))
                throw new UiSemanticStardewCapabilityException(
                    "The selected font cannot represent rightward direction or its ASCII fallback.");
            normalized = normalized.Replace("→", "->", StringComparison.Ordinal);
        }
        // Literal punctuation needs the same glyph policy as a generated truncation
        // suffix, even when the entire message fits. Expand before measuring/wrapping.
        if (normalized.Contains('…') && !font.Characters.Contains('…'))
        {
            if (!font.Characters.Contains('.'))
                throw new UiSemanticStardewCapabilityException(
                    "The selected font cannot represent an ellipsis or its ASCII fallback.");
            normalized = normalized.Replace("…", "...", StringComparison.Ordinal);
        }
        if (overflow != RuntimeTextOverflow.Wrap)
        {
            string single = normalized.Replace('\n', ' ');
            return new[] { FitLine(single, font, scale, availableWidth, overflow == RuntimeTextOverflow.Ellipsis) };
        }

        var lines = new List<string>();
        foreach (string hardLine in normalized.Split('\n'))
        {
            if (hardLine.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }
            int offset = 0;
            while (offset < hardLine.Length)
            {
                int length = MaximumPrefix(hardLine, offset, font, scale, availableWidth);
                if (length <= 0) length = NextBoundary(hardLine, offset) - offset;
                int end = offset + length;
                if (end < hardLine.Length)
                {
                    int whitespace = LastWrapSeparator(hardLine, offset, length);
                    if (whitespace >= offset) end = whitespace + 1;
                }
                lines.Add(hardLine[offset..end].TrimEnd());
                offset = end;
                while (offset < hardLine.Length && char.IsWhiteSpace(hardLine[offset])) offset++;
            }
        }
        return lines.Count == 0 ? new[] { string.Empty } : lines.ToArray();
    }

    private static string FitLine(
        string text,
        SpriteFont font,
        float scale,
        float availableWidth,
        bool ellipsis)
    {
        if (availableWidth <= 0 || text.Length == 0) return string.Empty;
        if (Width(font, text, scale) <= availableWidth) return text;
        string suffix = string.Empty;
        if (ellipsis)
        {
            if (font.Characters.Contains('…')) suffix = "…";
            else if (font.Characters.Contains('.')) suffix = "...";
            else throw new UiSemanticStardewCapabilityException(
                "The selected font cannot represent an ellipsis or its ASCII fallback.");
        }
        float suffixWidth = Width(font, suffix, scale);
        int length = MaximumPrefix(text, font, scale, Math.Max(0, availableWidth - suffixWidth));
        return length <= 0 ? (suffixWidth <= availableWidth ? suffix : string.Empty) : text[..length] + suffix;
    }

    private static string FitClippedLine(
        string text,
        SpriteFont font,
        float scale,
        float x,
        float clipLeft,
        float clipRight,
        out float xOffset)
    {
        var measurement = new PrefixMeasurement(text, font, scale, 0);
        int start = UiTextBoundarySearch.MinimumReachingLength(
            text,
            Math.Max(0, clipLeft - x),
            measurement,
            MeasurePrefix);
        xOffset = Width(font, text[..start], scale);
        float width = Math.Max(0, clipRight - (x + xOffset));
        int length = MaximumPrefix(text, start, font, scale, width);
        return length <= 0 ? string.Empty : text.Substring(start, length);
    }

    private static int MaximumPrefix(string text, SpriteFont font, float scale, float width)
        => MaximumPrefix(text, 0, font, scale, width);

    private static int MaximumPrefix(string text, int start, SpriteFont font, float scale, float width)
    {
        var measurement = new PrefixMeasurement(text, font, scale, start);
        return UiTextBoundarySearch.MaximumFittingLength(text, start, width, measurement, MeasurePrefix);
    }

    private static float MeasurePrefix(PrefixMeasurement measurement, int length)
        => Width(
            measurement.Font,
            measurement.Text.Substring(measurement.Start, length),
            measurement.Scale);

    private static int LastWrapSeparator(string text, int start, int length)
    {
        for (int index = start + length - 1; index >= start; index--)
        {
            if (text[index] is ' ' or '\t') return index;
        }
        return -1;
    }

    private static int NextBoundary(string text, int index)
        => index + 1 < text.Length && char.IsHighSurrogate(text[index]) && char.IsLowSurrogate(text[index + 1])
            ? index + 2
            : Math.Min(text.Length, index + 1);

    private static float Width(SpriteFont font, string text, float scale)
        => text.Length == 0 ? 0 : font.MeasureString(text).X * scale;

    private static float FontScale(SpriteFont font, RuntimeTypography typography)
        => typography.Size / Math.Max(1, font.LineSpacing);

    private static float LineHeight(RuntimeTypography typography)
        => typography.Size * typography.LineHeight;

    private static RuntimeRect Transform(RuntimeRect bounds, RuntimeTransform transform)
    {
        if (Math.Abs(transform.RotationDegrees) > 0.01f)
            throw new UiSemanticStardewCapabilityException(
                "A rotated primitive escaped semantic Stardew capability validation.");
        float width = bounds.Width * transform.Scale;
        float height = bounds.Height * transform.Scale;
        return new RuntimeRect(
            bounds.X + transform.TranslateX + (bounds.Width - width) / 2,
            bounds.Y + transform.TranslateY + (bounds.Height - height) / 2,
            width,
            height);
    }

    private static Rectangle ToRectangle(RuntimeRect value)
    {
        int x = (int)MathF.Floor(value.X);
        int y = (int)MathF.Floor(value.Y);
        int right = (int)MathF.Ceiling(value.Right);
        int bottom = (int)MathF.Ceiling(value.Bottom);
        return new Rectangle(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static Color ToColor(RuntimeColor color, float opacity)
    {
        float alpha = color.A / 255f * Math.Clamp(opacity, 0, 1);
        return new Color(
            (byte)MathF.Round(color.R * alpha),
            (byte)MathF.Round(color.G * alpha),
            (byte)MathF.Round(color.B * alpha),
            (byte)MathF.Round(255 * alpha));
    }

    private Texture2D Pixel
    {
        get
        {
            if (_pixel != null) return _pixel;
            _pixel = new Texture2D(_graphicsDevice, 1, 1, false, SurfaceFormat.Color);
            _pixel.SetData(new[] { Color.White });
            return _pixel;
        }
    }

    private SpriteBatch CurrentBatch()
        => _batch ?? throw new InvalidOperationException("Draw primitives can only execute inside Render().");

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UiSemanticSpriteBatchBridge));
    }

    private readonly record struct MaskKey(int Radius, int Border);
    private readonly record struct PrefixMeasurement(string Text, SpriteFont Font, float Scale, int Start);
    private readonly record struct TextLayoutKey(
        SpriteFont Font,
        string Text,
        float Scale,
        float AvailableWidth,
        RuntimeTextOverflow Overflow);
}
