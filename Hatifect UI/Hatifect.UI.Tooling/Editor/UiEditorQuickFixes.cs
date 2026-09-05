using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Syntax;
using Hatifect.UI.Language.Text;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;

namespace Hatifect.UI.Tooling.Editor;

internal sealed record UiEditorQuickFix(UiTextSpan Span, string NewText, UiDiagnostic Diagnostic);

internal static class UiEditorQuickFixes
{
    // Interactive requests have a fixed compilation budget, independent of diagnostic count.
    internal static IReadOnlyList<UiEditorQuickFix> Find(UiEditorDocumentSnapshot document,
        UiCompiler compiler, int start, int end, int maximumSourceLength)
    {
        var fixes = new List<UiEditorQuickFix>();
        var seen = new HashSet<(int Start, string Text)>();
        UiDiagnostic[] errors = document.Diagnostics.Where(d => d.Severity == UiDiagnosticSeverity.Error).ToArray();
        var errorCounts = errors.GroupBy(d => (d.Id, d.Message)).ToDictionary(group => group.Key, group => group.Count());
        var propertyAnchors = new Dictionary<int, UiTextSpan>();
        var pending = new Stack<(UiStatementSyntax Statement, UiTextSpan? Matrix)>(
            document.Analysis.Syntax.Statements.Reverse().Select(s => (s, (UiTextSpan?)null)));
        while (pending.TryPop(out var item))
        {
            if (item.Statement is UiAssignmentSyntax assignment)
                propertyAnchors[assignment.Value.Span.Start] = item.Matrix ?? assignment.Property.Span;
            else if (item.Statement is UiBlockSyntax block)
            {
                UiTextSpan? matrix = document.Analysis.DefinitionKind == UiDefinitionKind.Presentation
                    && block.Target?.Segments.Count > 1 ? block.Target.Segments[^1].Span : null;
                for (int i = block.Statements.Count - 1; i >= 0; i--) pending.Push((block.Statements[i], matrix));
            }
        }
        UiBindingContext context = UiBindingContextMetadataWire.CreateBindingContext(document.Analysis.Bindings);
        int attempts = 0;
        int inspected = 0;
        foreach (UiDiagnostic diagnostic in errors)
        {
            if (diagnostic.Id is not ("LUI2004" or "LUI2006" or "LUI2007" or "LUI2013" or "LUI2014" or "LUI2015" or "LUI2016" or "LUI2018"))
                continue;
            UiTextSpan anchor = diagnostic.Id == "LUI2007" && propertyAnchors.TryGetValue(diagnostic.Span.Start, out UiTextSpan property)
                ? property : diagnostic.Span;
            if (!Intersects(anchor) && !Intersects(diagnostic.Span)) continue;
            if (++inspected > 32) break;
            var candidates = document.Analysis.Complete(anchor.Start, filterPrefix: false)
                .Select(c => (Completion: c, Distance: Distance(document.Source.Substring(c.Span.Start, c.Span.Length), c.Label)))
                .Where(c => c.Distance is > 0 and <= 2).OrderBy(c => c.Distance)
                .ThenBy(c => c.Completion.Label, StringComparer.Ordinal).Take(4);
            foreach (var candidate in candidates)
            {
                UiEditorCompletion completion = candidate.Completion;
                if (!seen.Add((completion.Span.Start, completion.Label))) continue;
                if (++attempts > 16) return fixes;
                if ((long)document.Source.Length - completion.Span.Length + completion.Label.Length > maximumSourceLength) continue;
                string edited = string.Concat(document.Source.AsSpan(0, completion.Span.Start), completion.Label.AsSpan(),
                    document.Source.AsSpan(completion.Span.End));
                UiDiagnostic[] remaining = compiler.Compile(edited, context, document.SourceName).Diagnostics
                    .Where(d => d.Severity == UiDiagnosticSeverity.Error).ToArray();
                if (remaining.Length >= errors.Length || remaining.GroupBy(d => (d.Id, d.Message))
                    .Any(group => !errorCounts.TryGetValue(group.Key, out int count)
                        || group.Count() > count - (group.Key == (diagnostic.Id, diagnostic.Message) ? 1 : 0))) continue;
                fixes.Add(new UiEditorQuickFix(completion.Span, completion.Label, diagnostic));
            }
        }
        return fixes;

        bool Intersects(UiTextSpan span) => start == end ? span.Start <= start && start <= span.End
            : span.Start < end && start < span.End;
    }

    private static int Distance(string source, string target)
    {
        if (source.Length > 128 || target.Length > 128 || Math.Abs(source.Length - target.Length) > 2) return 3;
        Span<int> previous = stackalloc int[129];
        Span<int> current = stackalloc int[129];
        for (int j = 0; j <= target.Length; j++) previous[j] = j;
        for (int i = 1; i <= source.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= target.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (source[i - 1] == target[j - 1] ? 0 : 1));
            Span<int> swap = previous;
            previous = current;
            current = swap;
        }
        return previous[target.Length];
    }
}
