using System;

namespace Hatifect.UI.Language.Text;

public readonly record struct UiTextSpan(int Start, int Length, int Line, int Column)
{
    public int End => checked(Start + Length);

    public static UiTextSpan Between(UiTextSpan first, UiTextSpan last)
    {
        int end = Math.Max(first.End, last.End);
        return new UiTextSpan(first.Start, end - first.Start, first.Line, first.Column);
    }
}
