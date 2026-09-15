using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using Hatifect.UI.Runtime.Layout;
using RuntimeTextOverflow = Hatifect.UI.Runtime.Layout.UiTextOverflow;

namespace Hatifect.UI.Stardew.Semantic;

/// <summary>
/// Native (non-GPU) text preparation: glyph-availability fallback, wrapping, line fitting,
/// prefix measurement, and clipping. The sprite-batch bridge owns GPU resources, font/texture
/// resolution, drawing, and the bounded layout cache; this module only turns semantic text
/// into the exact lines the bridge measures and draws.
/// </summary>
internal static class UiNativeTextLayout
{
    internal static string[] BuildLines(
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

    internal static string FitLine(
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

    internal static string FitClippedLine(
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

    internal static int MaximumPrefix(string text, SpriteFont font, float scale, float width)
        => MaximumPrefix(text, 0, font, scale, width);

    internal static int MaximumPrefix(string text, int start, SpriteFont font, float scale, float width)
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

    internal static float Width(SpriteFont font, string text, float scale)
        => text.Length == 0 ? 0 : font.MeasureString(text).X * scale;

    private readonly record struct PrefixMeasurement(string Text, SpriteFont Font, float Scale, int Start);
}
