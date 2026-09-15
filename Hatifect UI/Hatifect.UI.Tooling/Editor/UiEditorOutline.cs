using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Language.Syntax;
using Hatifect.UI.Language.Text;

namespace Hatifect.UI.Tooling.Editor;

internal sealed record UiEditorOutlineNode(string Name, int Kind, UiTextSpan Span,
    UiTextSpan Selection, int Parent, int Depth, bool IsBlock);

internal sealed partial class UiEditorAnalysis
{
    internal IReadOnlyList<UiEditorOutlineNode> Outline { get; }

    private IReadOnlyList<UiEditorOutlineNode> BuildOutline()
    {
        var nodes = new List<UiEditorOutlineNode>();
        int root = -1;
        if (!Syntax.NameToken.IsMissing)
        {
            root = 0;
            nodes.Add(new UiEditorOutlineNode(Syntax.NameToken.Text, 2, Trim(Syntax.Span),
                Syntax.NameToken.Span, -1, 0, false));
        }
        var pending = new Stack<(UiStatementSyntax Statement, int Parent, int Depth)>(
            Syntax.Statements.Reverse().Select(s => (s, root, 1)));
        while (pending.TryPop(out var entry))
        {
            if (!TryDescribeStatement(entry.Statement, out string name, out UiTextSpan selection, out int kind))
                continue;
            int parent = entry.Parent;
            if (selection.Length > 0 && !string.IsNullOrWhiteSpace(name))
            {
                parent = nodes.Count;
                nodes.Add(new UiEditorOutlineNode(name, kind, Trim(entry.Statement.Span), selection,
                    entry.Parent, entry.Depth, entry.Statement is UiBlockSyntax));
            }
            if (entry.Statement is UiBlockSyntax container)
                for (int i = container.Statements.Count - 1; i >= 0; i--)
                    pending.Push((container.Statements[i], parent, entry.Depth + 1));
        }
        return Array.AsReadOnly(nodes.ToArray());

        UiTextSpan Trim(UiTextSpan span)
        {
            int end = span.End;
            while (end > span.Start && char.IsWhiteSpace(Text.Source[end - 1])) end--;
            return Text.Span(span.Start, end - span.Start);
        }
    }

    private static bool TryDescribeStatement(
        UiStatementSyntax statement, out string name, out UiTextSpan selection, out int kind)
    {
        switch (statement)
        {
            case UiBlockSyntax block:
                name = (block.Target?.ToString() ?? string.Empty) +
                    (block.Specialization == null ? string.Empty :
                        (block.Target == null ? "@" : " @") + block.Specialization.Text);
                selection = block.Target?.Span ?? block.Specialization?.Span ?? block.Span;
                kind = 5;
                return true;
            case UiAssignmentSyntax assignment:
                name = assignment.Property.ToString();
                selection = assignment.Property.Span;
                kind = 7;
                return true;
            case UiPlacementSyntax placement:
                name = $"{placement.Subject} -> {placement.Region}";
                selection = placement.Subject.Span;
                kind = 8;
                return true;
            default:
                name = string.Empty;
                selection = default;
                kind = 0;
                return false;
        }
    }
}
