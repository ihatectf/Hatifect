namespace Hatifect.UI.Runtime.Layout;

/// <summary>
/// Finds UTF-16 scalar boundaries for a non-decreasing prefix-width function without measuring
/// every preceding prefix. Platform adapters retain ownership of the actual font metrics.
/// </summary>
internal static class UiTextBoundarySearch
{
    public static int MaximumFittingLength<TState>(
        string text,
        float availableWidth,
        TState state,
        Func<TState, int, float> measurePrefix)
        => MaximumFittingLength(text, 0, availableWidth, state, measurePrefix);

    public static int MaximumFittingLength<TState>(
        string text,
        int start,
        float availableWidth,
        TState state,
        Func<TState, int, float> measurePrefix)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measurePrefix);
        ValidateStart(text, start);
        if (!float.IsFinite(availableWidth) || availableWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(availableWidth));
        int rangeLength = text.Length - start;
        if (rangeLength == 0 || availableWidth == 0) return 0;

        int fitting = 0;
        int tooWide = NextBoundary(text, start, fitting);
        while (Measure(state, tooWide, measurePrefix) <= availableWidth)
        {
            fitting = tooWide;
            if (fitting == rangeLength) return fitting;
            tooWide = NextProbe(text, start, fitting, rangeLength);
        }

        while (true)
        {
            int next = NextBoundary(text, start, fitting);
            if (next >= tooWide) return fitting;

            int candidate = BoundaryAtOrBefore(text, start, fitting + (tooWide - fitting) / 2);
            if (candidate <= fitting) candidate = next;
            if (candidate >= tooWide) return fitting;

            if (Measure(state, candidate, measurePrefix) <= availableWidth)
                fitting = candidate;
            else
                tooWide = candidate;
        }
    }

    public static int MinimumReachingLength<TState>(
        string text,
        float targetWidth,
        TState state,
        Func<TState, int, float> measurePrefix)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measurePrefix);
        if (!float.IsFinite(targetWidth) || targetWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidth));
        if (text.Length == 0 || targetWidth == 0) return 0;

        int below = 0;
        int reaching = NextBoundary(text, 0, below);
        while (Measure(state, reaching, measurePrefix) < targetWidth)
        {
            below = reaching;
            if (below == text.Length) return below;
            reaching = NextProbe(text, 0, below, text.Length);
        }

        while (true)
        {
            int next = NextBoundary(text, 0, below);
            if (next >= reaching) return reaching;

            int candidate = BoundaryAtOrBefore(text, 0, below + (reaching - below) / 2);
            if (candidate <= below) candidate = next;
            if (candidate >= reaching) return reaching;

            if (Measure(state, candidate, measurePrefix) < targetWidth)
                below = candidate;
            else
                reaching = candidate;
        }
    }

    private static float Measure<TState>(
        TState state,
        int length,
        Func<TState, int, float> measurePrefix)
    {
        float width = measurePrefix(state, length);
        if (!float.IsFinite(width) || width < 0)
            throw new InvalidOperationException("Text prefix measurement must be finite and non-negative.");
        return width;
    }

    private static void ValidateStart(string text, int start)
    {
        if (start < 0 || start > text.Length) throw new ArgumentOutOfRangeException(nameof(start));
        if (start > 0 && start < text.Length &&
            char.IsHighSurrogate(text[start - 1]) && char.IsLowSurrogate(text[start]))
        {
            throw new ArgumentException("Text ranges must start on a Unicode scalar boundary.", nameof(start));
        }
    }

    private static int BoundaryAtOrBefore(string text, int start, int index)
    {
        int boundary = Math.Clamp(start + index, start, text.Length);
        if (boundary > 0 && boundary < text.Length &&
            char.IsHighSurrogate(text[boundary - 1]) && char.IsLowSurrogate(text[boundary]))
        {
            boundary--;
        }
        return boundary - start;
    }

    private static int NextBoundary(string text, int start, int index)
    {
        int absolute = start + index;
        int next = absolute + 1 < text.Length &&
                   char.IsHighSurrogate(text[absolute]) && char.IsLowSurrogate(text[absolute + 1])
            ? absolute + 2
            : Math.Min(text.Length, absolute + 1);
        return next - start;
    }

    private static int NextProbe(string text, int start, int current, int rangeLength)
    {
        int target = current > rangeLength / 2 ? rangeLength : current * 2;
        int absolute = start + target;
        if (absolute > start && absolute < text.Length &&
            char.IsHighSurrogate(text[absolute - 1]) && char.IsLowSurrogate(text[absolute]))
        {
            absolute++;
        }
        int probe = Math.Min(rangeLength, absolute - start);
        return probe > current ? probe : NextBoundary(text, start, current);
    }
}
