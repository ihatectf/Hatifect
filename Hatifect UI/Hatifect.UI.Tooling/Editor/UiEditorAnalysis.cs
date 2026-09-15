using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Language.Syntax;
using Hatifect.UI.Language.Text;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;

namespace Hatifect.UI.Tooling.Editor;

internal sealed record UiEditorCompletion(string Label, int Kind, string Detail, UiTextSpan Span);

/// <summary>One immutable authoring view per version, including recovery syntax and frozen bindings.</summary>
internal sealed partial class UiEditorAnalysis
{
    private readonly UiBlockSyntax?[] _scopes;

    internal UiEditorAnalysis(UiEditorText text, UiDocumentSyntax syntax,
        UiBindingContextMetadata bindings, UiLanguageMetadata language)
    {
        Text = text;
        Syntax = syntax;
        Bindings = bindings;
        Language = language;
        _scopes = BuildScopeMap(text, syntax);
        Symbols = BuildSymbols();
        _occurrences = Symbols.Where(symbol => symbol.Id != null).GroupBy(symbol => symbol.Id!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<UiEditorSymbol>)Array.AsReadOnly(group.ToArray()));
        Outline = BuildOutline();
        SemanticTokens = BuildSemanticTokens();
    }

    private static UiBlockSyntax?[] BuildScopeMap(UiEditorText text, UiDocumentSyntax syntax)
    {
        var scopes = new UiBlockSyntax?[text.LineCount];
        var headers = new Dictionary<int, UiBlockSyntax>();
        var pending = new Stack<UiStatementSyntax>(syntax.Statements.Reverse());
        while (pending.TryPop(out UiStatementSyntax? statement))
        {
            if (statement is not UiBlockSyntax block) continue;
            headers.TryAdd(text.PositionAt(block.Span.Start).Line, block);
            for (int i = block.Statements.Count - 1; i >= 0; i--) pending.Push(block.Statements[i]);
        }
        var parents = new Stack<(int Indent, UiBlockSyntax Block)>();
        for (int line = 0; line < text.LineCount; line++)
        {
            int indent = 0;
            bool blank = true;
            for (int i = text.LineStart(line); i < text.LineEnd(line); i++)
            {
                if (text.Source[i] == ' ') indent++;
                else if (text.Source[i] == '\t') indent += 4;
                else { blank = false; break; }
            }
            if (!blank)
                while (parents.Count > 0 && indent <= parents.Peek().Indent) parents.Pop();
            scopes[line] = parents.Count > 0 && indent > parents.Peek().Indent ? parents.Peek().Block : null;
            if (headers.TryGetValue(line, out UiBlockSyntax? header)) parents.Push((indent, header));
        }
        return scopes;
    }

    internal UiEditorText Text { get; }
    internal UiDocumentSyntax Syntax { get; }
    internal UiBindingContextMetadata Bindings { get; }
    internal UiLanguageMetadata Language { get; }
    internal UiDefinitionKind? DefinitionKind => Syntax.DocumentKind switch
    {
        UiDocumentKind.Presentation => UiDefinitionKind.Presentation,
        UiDocumentKind.Visual => UiDefinitionKind.Visual,
        _ => null
    };

    internal IReadOnlyList<UiEditorCompletion> Complete(int offset, bool filterPrefix = true)
    {
        UiEditorPosition position = Text.PositionAt(offset);
        int start = offset;
        while (start > Text.LineStart(position.Line) && IsName(Text.Source[start - 1])) start--;
        int end = offset;
        while (end < Text.LineEnd(position.Line) && IsName(Text.Source[end])) end++;
        UiTextSpan span = Text.Span(start, end - start);
        string prefix = Text.Source[start..offset];
        string before = Text.Source[Text.LineStart(position.Line)..start].Trim();
        var choices = new List<(string Label, int Kind, string Detail)>();
        if (offset <= Syntax.KindToken.Span.End && before.Length == 0)
        {
            choices.Add(("presentation", 14, "Presentation document"));
            choices.Add(("visual", 14, "Visual document"));
        }
        else if (position.Line == Text.PositionAt(Syntax.KindToken.Span.Start).Line)
            return Array.Empty<UiEditorCompletion>();
        else if (before.Contains('"'))
            return Array.Empty<UiEditorCompletion>();
        else if (before.EndsWith('@'))
        {
            bool state = before.Length > 1 && DefinitionKind == UiDefinitionKind.Visual;
            AddNames(state ? Language.States : Language.Profiles, state ? "Visual state" : "Presentation profile");
        }
        else if (before.Contains('='))
        {
            string propertyName = before[..before.IndexOf('=')].Trim();
            string? target = _scopes[position.Line]?.Target?.ToString();
            if (TryMatrix(target, out string element, out string matrixProperty))
            {
                target = element;
                propertyName = matrixProperty;
            }
            UiPropertyMetadata? property = Property(propertyName);
            if (property != null)
                foreach (var choice in Values(property, target)) choices.Add(choice);
        }
        else if (before.EndsWith("->", StringComparison.Ordinal))
        {
            if (DefinitionKind == UiDefinitionKind.Presentation) AddNames(Language.Regions, "Region");
        }
        else if (before.Length == 0 && DefinitionKind != null)
        {
            string? target = _scopes[position.Line]?.Target?.ToString();
            if (target != null)
            {
                if (TryMatrix(target, out _, out _))
                {
                    AddNames(Language.Profiles, "Presentation profile");
                    choices.Add(("default", 14, "Default profile"));
                }
                else
                    foreach (UiPropertyMetadata property in Language.Properties.Where(p => p.DefinitionKind == DefinitionKind))
                        choices.Add((property.Name, 10, $"{property.Type}; {property.Effects}"));
            }
            else if (DefinitionKind == UiDefinitionKind.Visual)
                choices.AddRange(Bindings.Roles.Select(role => (role.Name, 6, $"Role {role.Id}")));
            else
            {
                choices.AddRange(Bindings.Elements.Select(element => (element.Name, 6, $"Element {element.Id}")));
                choices.AddRange(Language.Properties.Where(p => p.DefinitionKind == DefinitionKind)
                    .Select(p => (p.Name, 10, $"{p.Type}; {p.Effects}")));
            }
        }
        return choices.Where(c => !filterPrefix || c.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(c => c.Label).OrderBy(c => c.Label, StringComparer.Ordinal)
            .Select(c => new UiEditorCompletion(c.Label, c.Kind, c.Detail, span)).ToArray();

        void AddNames(IEnumerable<UiNamedSymbolMetadata> names, string detail)
            => choices.AddRange(names.Select(name => (name.Name, 12, detail)));
    }

    internal UiPropertyMetadata? Property(string name)
        => Language.Properties.FirstOrDefault(p => p.DefinitionKind == DefinitionKind && p.Name == name);

    private IEnumerable<(string Label, int Kind, string Detail)> Values(UiPropertyMetadata property, string? target)
    {
        foreach (UiTokenMetadata token in Language.Tokens.Where(t => t.Type == property.Type))
            yield return (token.Name, 12, token.Type.ToString());
        foreach (UiEnumValueMetadata value in Language.EnumValues.Where(v => v.Property == property.Id))
            yield return (value.Name, 13, $"{property.Name} value");
        if (property.Type == UiSemanticType.Presentation)
        {
            UiBindingElementMetadata? element = Bindings.Elements.FirstOrDefault(e => e.Name == target);
            foreach (UiPresentationMetadata presentation in Language.Presentations)
                if (element == null || element.Capabilities.Count == 0
                    || presentation.SupportedCapabilities.Any(element.Capabilities.Contains))
                    yield return (presentation.Name, 12, "Presentation");
        }
        if (property.Type == UiSemanticType.PresentationPattern)
            foreach (UiNamedSymbolMetadata pattern in Language.Patterns)
                yield return (pattern.Name, 12, "Presentation pattern");
        if (property.Type == UiSemanticType.Region)
            foreach (UiNamedSymbolMetadata region in Language.Regions)
                yield return (region.Name, 12, "Region");
        if (property.Type == UiSemanticType.Bool)
        {
            yield return ("true", 12, "Bool");
            yield return ("false", 12, "Bool");
        }
    }

    private bool TryMatrix(string? target, out string element, out string property)
    {
        int dot = target?.LastIndexOf('.') ?? -1;
        element = dot > 0 ? target![..dot] : string.Empty;
        property = dot > 0 ? target![(dot + 1)..] : string.Empty;
        return DefinitionKind == UiDefinitionKind.Presentation && dot > 0;
    }

    private static bool IsName(char value) => char.IsLetterOrDigit(value) || value is '_' or '-' or '.';
}
