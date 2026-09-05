using Hatifect.UI.Language.Text;

namespace Hatifect.UI.Tooling.Editor;

internal readonly record struct UiEditorPosition(int Line, int Character);

/// <summary>Immutable UTF-16 line index. Lexer indentation columns are not editor coordinates.</summary>
internal sealed class UiEditorText
{
    private readonly int[] _starts;
    private readonly int[] _ends;

    internal UiEditorText(string source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        var starts = new List<int> { 0 };
        var ends = new List<int>();
        for (int offset = 0; offset < source.Length; offset++)
        {
            if (source[offset] is not ('\r' or '\n')) continue;
            ends.Add(offset);
            if (source[offset] == '\r' && offset + 1 < source.Length && source[offset + 1] == '\n') offset++;
            starts.Add(offset + 1);
        }
        ends.Add(source.Length);
        _starts = starts.ToArray();
        _ends = ends.ToArray();
    }

    internal string Source { get; }
    internal int LineCount => _starts.Length;
    internal int LineStart(int line) => _starts[line];
    internal int LineEnd(int line) => _ends[line];

    internal int OffsetAt(int line, int character)
    {
        if ((uint)line >= (uint)_starts.Length || character < 0)
            throw new ArgumentOutOfRangeException(nameof(line), "The position must refer to a document line and a nonnegative UTF-16 column.");
        int offset = _starts[line] + Math.Min(character, _ends[line] - _starts[line]);
        if (offset > _starts[line] && offset < _ends[line]
            && char.IsHighSurrogate(Source[offset - 1]) && char.IsLowSurrogate(Source[offset]))
            throw new ArgumentException("A position cannot split a UTF-16 surrogate pair.", nameof(character));
        return offset;
    }

    internal UiEditorPosition PositionAt(int offset)
    {
        if ((uint)offset > (uint)Source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        int index = Array.BinarySearch(_starts, offset);
        int line = index >= 0 ? index : ~index - 1;
        return new UiEditorPosition(line, Math.Min(offset, _ends[line]) - _starts[line]);
    }

    internal UiTextSpan Span(int start, int length)
    {
        if (length < 0 || start < 0 || start > Source.Length - length)
            throw new ArgumentOutOfRangeException(nameof(length));
        UiEditorPosition position = PositionAt(start);
        return new UiTextSpan(start, length, position.Line, position.Character);
    }
}
