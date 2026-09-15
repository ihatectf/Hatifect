using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Hatifect.UI.Language.Syntax;
using Hatifect.UI.Language.Text;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;

namespace Hatifect.UI.Tooling.Editor;

internal enum UiEditorSymbolKind { Keyword, Type, Variable, Property, EnumMember, String, Number, Operator }

internal sealed record UiEditorSymbol(UiSymbolId? Id, string Name, UiTextSpan Span,
    UiEditorSymbolKind Kind, string Description, bool IsDeclaration = false);

internal sealed partial class UiEditorAnalysis
{
    internal IReadOnlyList<UiEditorSymbol> Symbols { get; }

    internal UiEditorSymbol? SymbolAt(int offset)
    {
        int low = 0;
        int high = Symbols.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            UiEditorSymbol symbol = Symbols[middle];
            if (offset < symbol.Span.Start) high = middle - 1;
            else if (offset >= symbol.Span.End) low = middle + 1;
            else return symbol;
        }
        return null;
    }

    private IReadOnlyList<UiEditorSymbol> BuildSymbols()
    {
        var symbols = new List<UiEditorSymbol>();
        if (DefinitionKind != null)
        {
            Add(null, Syntax.KindToken.Text, Syntax.KindToken.Span, UiEditorSymbolKind.Keyword,
                $"{DefinitionKind} document");
            if (!Syntax.NameToken.IsMissing)
                Add(Bindings.OwnerId.Child($"asset/{Syntax.DocumentKind}/{Syntax.NameToken.Text}"),
                    Syntax.NameToken.Text, Syntax.NameToken.Span, UiEditorSymbolKind.Type,
                    $"{DefinitionKind} {Syntax.NameToken.Text}\nOwner: {Bindings.OwnerId}", true);
        }
        var pending = new Stack<(UiStatementSyntax Statement, string? Matrix)>(
            Syntax.Statements.Reverse().Select(s => (s, (string?)null)));
        while (pending.TryPop(out var entry))
        {
            switch (entry.Statement)
            {
                case UiBlockSyntax block:
                {
                    string? matrix = AddBlockSymbols(block);
                    for (int i = block.Statements.Count - 1; i >= 0; i--)
                        pending.Push((block.Statements[i], matrix));
                    break;
                }
                case UiPlacementSyntax placement:
                    AddTarget(placement.Subject.ToString(), placement.Subject.Span);
                    AddNamed(Language.Regions, placement.Region.ToString(), placement.Region.Span, "Region");
                    break;
                case UiAssignmentSyntax assignment:
                    AddAssignmentSymbols(assignment, entry.Matrix);
                    break;
            }
        }
        return Array.AsReadOnly(symbols.OrderBy(s => s.Span.Start).ToArray());

        string? AddBlockSymbols(UiBlockSyntax block)
        {
            string? matrix = null;
            if (block.Target is { IsMissing: false } target)
            {
                if (TryMatrix(target.ToString(), out string element, out string property))
                {
                    UiSyntaxToken last = target.Segments[^1];
                    AddTarget(element, UiTextSpan.Between(target.Segments[0].Span, target.Segments[^2].Span));
                    AddProperty(Property(property), last.Span);
                    matrix = property;
                }
                else AddTarget(target.ToString(), target.Span);
            }
            if (block.Specialization is { IsMissing: false } specialization)
            {
                bool isProfile = block.Target == null;
                if (isProfile || DefinitionKind == UiDefinitionKind.Visual)
                    AddNamed(isProfile ? Language.Profiles : Language.States, specialization.Text,
                        specialization.Span, isProfile ? "Presentation profile" : "Visual state");
            }
            return matrix;
        }

        void AddAssignmentSymbols(UiAssignmentSyntax assignment, string? matrix)
        {
            UiPropertyMetadata? property = Property(matrix ?? assignment.Property.ToString());
            if (matrix == null) AddProperty(property, assignment.Property.Span);
            else if (assignment.Property.ToString() == "default")
                Add(null, "default", assignment.Property.Span, UiEditorSymbolKind.Keyword, "Default profile");
            else AddNamed(Language.Profiles, assignment.Property.ToString(), assignment.Property.Span, "Presentation profile");
            AddValues(assignment.Value, property);
        }

        void Add(UiSymbolId? id, string name, UiTextSpan span, UiEditorSymbolKind kind,
            string description, bool declaration = false)
        {
            if (span.Length > 0) symbols.Add(new UiEditorSymbol(id, name, span, kind, description, declaration));
        }

        void AddTarget(string name, UiTextSpan span)
        {
            if (DefinitionKind == UiDefinitionKind.Visual)
            {
                UiBindingRoleMetadata? role = Bindings.Roles.FirstOrDefault(r => r.Name == name);
                if (role != null || !Bindings.RequireDeclaredRoles)
                    Add(role?.Id ?? Bindings.OwnerId.Child($"role/{name}"), name, span,
                        UiEditorSymbolKind.Variable, $"Visual role {name}\nOwner: {Bindings.OwnerId}");
            }
            else if (DefinitionKind == UiDefinitionKind.Presentation)
            {
                UiBindingElementMetadata? element = Bindings.Elements.FirstOrDefault(e => e.Name == name);
                if (element != null || !Bindings.RequireDeclaredElements)
                    Add(element?.Id ?? Bindings.OwnerId.Child($"element/{name}"), name, span,
                        UiEditorSymbolKind.Variable, $"Semantic element {name}\nOwner: {Bindings.OwnerId}\nCapabilities: " +
                        (element == null ? "unspecified" : string.Join(", ", element.Capabilities)));
            }
        }

        void AddProperty(UiPropertyMetadata? property, UiTextSpan span)
        {
            if (property == null) return;
            string[] values = Language.EnumValues.Where(v => v.Property == property.Id).Select(v => v.Name).ToArray();
            Add(property.Id, property.Name, span, UiEditorSymbolKind.Property,
                $"{property.Name}: {property.Type}\nEffects: {property.Effects}\nAnimatable: {property.Animatable}" +
                (values.Length == 0 ? string.Empty : $"\nValues: {string.Join(", ", values)}"));
        }

        void AddNamed(IEnumerable<UiNamedSymbolMetadata> candidates, string name, UiTextSpan span, string description)
        {
            UiNamedSymbolMetadata? value = candidates.FirstOrDefault(n => n.Name == name);
            if (value != null) Add(value.Id, name, span, UiEditorSymbolKind.EnumMember, $"{name}: {description}");
        }

        void AddValues(UiValueSyntax value, UiPropertyMetadata? property)
        {
            if (property?.Type == UiSemanticType.String && value.Tokens.Count == 1 && !value.Tokens[0].IsMissing)
            {
                UiSyntaxToken literal = value.Tokens[0];
                Add(null, literal.Text, literal.Span, UiEditorSymbolKind.String, string.Empty);
                return;
            }
            for (int i = 0; i < value.Tokens.Count; i++)
            {
                UiSyntaxToken token = value.Tokens[i];
                if (token.IsMissing) continue;
                if (token.Kind is UiSyntaxKind.StringToken or UiSyntaxKind.NumberToken)
                {
                    Add(null, token.Text, token.Span, token.Kind == UiSyntaxKind.StringToken
                        ? UiEditorSymbolKind.String : UiEditorSymbolKind.Number, string.Empty);
                    continue;
                }
                if (token.Kind == UiSyntaxKind.ArrowToken)
                {
                    Add(null, token.Text, token.Span, UiEditorSymbolKind.Operator, "Transition");
                    continue;
                }
                if (token.Kind != UiSyntaxKind.IdentifierToken) continue;
                string name = token.Text;
                StringBuilder? qualified = null;
                UiTextSpan span = token.Span;
                while (i + 2 < value.Tokens.Count && value.Tokens[i + 1].Kind == UiSyntaxKind.DotToken
                    && value.Tokens[i + 2] is { Kind: UiSyntaxKind.IdentifierToken, IsMissing: false } part)
                {
                    qualified ??= new StringBuilder(name);
                    qualified.Append('.').Append(part.Text);
                    span = UiTextSpan.Between(span, part.Span);
                    i += 2;
                }
                if (qualified != null) name = qualified.ToString();
                UiTokenMetadata? known = Language.Tokens.FirstOrDefault(t => t.Name == name);
                if (known != null)
                {
                    Add(known.Id, name, span, UiEditorSymbolKind.EnumMember, $"{name}: {known.Type}");
                    continue;
                }
                if (property?.Type == UiSemanticType.Presentation)
                {
                    UiPresentationMetadata? presentation = Language.Presentations.FirstOrDefault(p => p.Name == name);
                    if (presentation != null) Add(presentation.Id, name, span, UiEditorSymbolKind.EnumMember,
                        $"{name}: Presentation\nCapabilities: {string.Join(", ", presentation.SupportedCapabilities)}");
                }
                else if (property?.Type == UiSemanticType.PresentationPattern)
                    AddNamed(Language.Patterns, name, span, "Presentation pattern");
                else if (property?.Type == UiSemanticType.Region)
                    AddNamed(Language.Regions, name, span, "Region");
                else if (property?.Type == UiSemanticType.EnumValue)
                {
                    UiEnumValueMetadata? enumValue = Language.EnumValues.FirstOrDefault(v => v.Property == property.Id && v.Name == name);
                    if (enumValue != null) Add(enumValue.Id, name, span, UiEditorSymbolKind.EnumMember, $"{name}: {property.Name} value");
                }
            }
        }
    }
}
