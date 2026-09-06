using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Syntax;
using Hatifect.UI.Language.Text;

namespace Hatifect.UI.Semantics;

internal sealed class UiBinder
{
    private readonly record struct AssignmentKey(
        UiSymbolId? Target,
        UiSymbolId? State,
        UiSymbolId? Profile,
        UiSymbolId Property);

    private readonly UiDocumentSyntax _syntax;
    private readonly UiBindingContext _context;
    private readonly UiSemanticCatalog _catalog;
    private readonly List<UiDiagnostic> _diagnostics;
    private readonly HashSet<AssignmentKey> _assignmentKeys = new();
    private readonly List<UiPlacementIr> _placements = new();
    private readonly List<UiPropertyAssignmentIr> _assignments = new();

    public UiBinder(UiDocumentSyntax syntax, UiBindingContext context, UiSemanticCatalog catalog)
    {
        _syntax = syntax;
        _context = context;
        _catalog = catalog;
        _diagnostics = new List<UiDiagnostic>(syntax.Diagnostics);
    }

    public UiCompilationResult Bind()
    {
        UiBoundDefinition? definition = null;
        if (_syntax.DocumentKind == UiDocumentKind.Presentation)
        {
            foreach (UiStatementSyntax statement in _syntax.Statements)
                BindPresentationStatement(statement, activeProfile: null);
            ValidatePresentationContracts();
            definition = new UiPresentationDefinition(DocumentId(), _placements.ToArray(), _assignments.ToArray());
        }
        else if (_syntax.DocumentKind == UiDocumentKind.Visual)
        {
            foreach (UiStatementSyntax statement in _syntax.Statements)
                BindVisualStatement(statement, activeProfile: null);
            definition = new UiVisualDefinition(DocumentId(), _assignments.ToArray());
        }

        return new UiCompilationResult(_syntax, definition, _diagnostics.ToArray());
    }

    private void BindPresentationStatement(UiStatementSyntax statement, UiSymbolId? activeProfile)
    {
        switch (statement)
        {
            case UiPlacementSyntax placement:
                BindPlacement(placement);
                return;
            case UiAssignmentSyntax assignment:
                BindAssignment(UiDefinitionKind.Presentation, target: (UiSymbolId?)null, state: null, activeProfile, assignment.Property.ToString(), assignment.Value);
                return;
            case UiBlockSyntax block when block.Target == null:
            {
                UiSymbolId? profile = BindProfile(block.Specialization, block.Span);
                foreach (UiStatementSyntax child in block.Statements) BindPresentationStatement(child, profile);
                return;
            }
            case UiBlockSyntax block:
                BindPresentationBlock(block, activeProfile);
                return;
            default:
                Report("LUI2001", "Statement is not valid in a Presentation document.", statement.Span);
                return;
        }
    }

    private void BindPresentationBlock(UiBlockSyntax block, UiSymbolId? activeProfile)
    {
        string[] targetSegments = block.Target!.Segments.Select(segment => segment.Text).ToArray();
        if (targetSegments.Length > 1 && block.Statements.All(statement => statement is UiAssignmentSyntax))
        {
            string targetName = string.Join(".", targetSegments[..^1]);
            string propertyName = targetSegments[^1];
            UiElementSymbol target = ResolveElement(targetName, block.Target.Span);
            foreach (UiAssignmentSyntax matrixEntry in block.Statements.Cast<UiAssignmentSyntax>())
            {
                string profileName = matrixEntry.Property.ToString();
                UiSymbolId? profile = string.Equals(profileName, "default", StringComparison.Ordinal)
                    ? activeProfile
                    : BindProfileName(profileName, matrixEntry.Property.Span);
                BindAssignment(UiDefinitionKind.Presentation, target, state: null, profile, propertyName, matrixEntry.Value);
            }
            return;
        }

        UiElementSymbol element = ResolveElement(block.Target.ToString(), block.Target.Span);
        foreach (UiStatementSyntax child in block.Statements)
        {
            if (child is UiAssignmentSyntax assignment)
                BindAssignment(UiDefinitionKind.Presentation, element, state: null, activeProfile, assignment.Property.ToString(), assignment.Value);
            else
                Report("LUI2002", "Presentation element blocks may contain only property assignments.", child.Span);
        }
    }

    private void BindVisualStatement(UiStatementSyntax statement, UiSymbolId? activeProfile)
    {
        if (statement is UiBlockSyntax profileBlock && profileBlock.Target == null)
        {
            UiSymbolId? profile = BindProfile(profileBlock.Specialization, profileBlock.Span);
            foreach (UiStatementSyntax child in profileBlock.Statements) BindVisualStatement(child, profile);
            return;
        }

        if (statement is not UiBlockSyntax block || block.Target == null)
        {
            Report("LUI2003", "Visual documents require role blocks.", statement.Span);
            return;
        }

        UiSymbolId role = ResolveRole(block.Target.ToString(), block.Target.Span);
        UiSymbolId? state = null;
        if (block.Specialization != null)
        {
            if (!_catalog.TryGetState(block.Specialization.Text, out UiSymbolId resolved))
                Report("LUI2004", $"Unknown visual state '{block.Specialization.Text}'.", block.Specialization.Span);
            else state = resolved;
        }

        foreach (UiStatementSyntax child in block.Statements)
        {
            if (child is UiAssignmentSyntax assignment)
                BindAssignment(UiDefinitionKind.Visual, role, state, activeProfile, assignment.Property.ToString(), assignment.Value);
            else
                Report("LUI2005", "Visual role blocks may contain only property assignments.", child.Span);
        }
    }

    private void BindPlacement(UiPlacementSyntax placement)
    {
        UiElementSymbol element = ResolveElement(placement.Subject.ToString(), placement.Subject.Span);
        if (!_catalog.TryGetRegion(placement.Region.ToString(), out UiSymbolId region))
        {
            Report("LUI2006", $"Unknown region '{placement.Region}'.", placement.Region.Span);
            region = _context.OwnerId.Child($"invalid-region/{placement.Region}");
        }
        _placements.Add(new UiPlacementIr(element.Id, region, Provenance(placement.Span)));
    }

    private void BindAssignment(
        UiDefinitionKind definitionKind,
        UiElementSymbol? target,
        UiSymbolId? state,
        UiSymbolId? profile,
        string propertyName,
        UiValueSyntax valueSyntax)
        => BindAssignment(definitionKind, target?.Id, target, state, profile, propertyName, valueSyntax);

    private void BindAssignment(
        UiDefinitionKind definitionKind,
        UiSymbolId? target,
        UiSymbolId? state,
        UiSymbolId? profile,
        string propertyName,
        UiValueSyntax valueSyntax)
        => BindAssignment(definitionKind, target, targetElement: null, state, profile, propertyName, valueSyntax);

    private void BindAssignment(
        UiDefinitionKind definitionKind,
        UiSymbolId? target,
        UiElementSymbol? targetElement,
        UiSymbolId? state,
        UiSymbolId? profile,
        string propertyName,
        UiValueSyntax valueSyntax)
    {
        if (!_catalog.TryGetProperty(definitionKind, propertyName, out UiPropertySymbol? property) || property == null)
        {
            Report("LUI2007", $"Unknown {definitionKind} property '{propertyName}'.", valueSyntax.Span);
            return;
        }

        var assignmentKey = new AssignmentKey(target, state, profile, property.Id);
        if (!_assignmentKeys.Add(assignmentKey))
        {
            Report("LUI2008", $"Property '{propertyName}' is assigned more than once in the same resolution layer.", valueSyntax.Span);
            return;
        }

        UiBoundValue value = BindValue(property, valueSyntax);
        if (value.Type == UiSemanticType.Error) return;

        if (targetElement != null && value is UiSymbolValue presentationValue && presentationValue.SymbolType == UiSemanticType.Presentation &&
            _catalog.TryGetPresentation(presentationValue.Name, out UiPresentationSymbol? presentation) && presentation != null &&
            targetElement.Capabilities.Count > 0 && !presentation.SupportedCapabilities.Overlaps(targetElement.Capabilities))
        {
            Report("LUI2009",
                $"Presentation '{presentation.Name}' is not capability-compatible with semantic element '{targetElement.Name}'.",
                valueSyntax.Span);
            return;
        }

        _assignments.Add(new UiPropertyAssignmentIr(target, property, profile, state, value, Provenance(valueSyntax.Span)));
    }

    private UiBoundValue BindValue(UiPropertySymbol property, UiValueSyntax syntax)
    {
        UiSyntaxToken[] tokens = syntax.Tokens.ToArray();
        int arrow = Array.FindIndex(tokens, token => token.Kind == UiSyntaxKind.ArrowToken);
        if (arrow >= 0)
        {
            if (!property.Animatable || arrow == 0 || arrow == tokens.Length - 1)
            {
                Report("LUI2010", $"Property '{property.Name}' does not accept this transition value.", syntax.Span);
                return ErrorValue.Instance;
            }
            UiBoundValue from = BindAtomicValue(property, tokens[..arrow], syntax.Span);
            UiBoundValue to = BindAtomicValue(property, tokens[(arrow + 1)..], syntax.Span);
            if (from.Type == UiSemanticType.Error || to.Type == UiSemanticType.Error) return ErrorValue.Instance;
            if (from.Type != to.Type)
            {
                Report("LUI2011", "Transition endpoints must have the same type.", syntax.Span);
                return ErrorValue.Instance;
            }
            return new UiTransitionValue(from, to);
        }
        return BindAtomicValue(property, tokens, syntax.Span);
    }

    private UiBoundValue BindAtomicValue(UiPropertySymbol property, UiSyntaxToken[] tokens, UiTextSpan span)
    {
        if (tokens.Length == 0 || tokens.Any(token => token.IsMissing)) return ErrorValue.Instance;
        UiSemanticType expected = property.Type;

        if (_catalog.HasPropertyValues(property))
        {
            string enumName = string.Concat(tokens.Select(token => token.Text));
            if (tokens.Length == 1 &&
                _catalog.TryGetPropertyValue(property, enumName, out UiEnumValueSymbol? enumValue) &&
                enumValue != null)
                return new UiSymbolValue(enumValue.Id, enumValue.Name, property.Type);
            Report("LUI2018",
                $"Value '{string.Join(" ", tokens.Select(token => token.Text))}' is not a catalog value for property '{property.Name}'.",
                span);
            return ErrorValue.Instance;
        }

        if (expected == UiSemanticType.Bool && tokens.Length == 1 && tokens[0].Kind == UiSyntaxKind.IdentifierToken &&
            bool.TryParse(tokens[0].Text, out bool boolean))
            return new UiBooleanValue(boolean);
        if (expected == UiSemanticType.Int && TrySingleInt(tokens, out int integer)) return new UiIntegerValue(integer);
        if (expected == UiSemanticType.Float && TrySingleNumber(tokens, out double number)) return new UiFloatValue(number);
        if (expected == UiSemanticType.Length && TrySingleInt(tokens, out int length)) return new UiLengthValue(length);
        if (expected == UiSemanticType.Opacity && TrySingleNumber(tokens, out double opacity))
        {
            if (opacity is >= 0 and <= 1) return new UiOpacityValue(opacity);
            Report("LUI2017", "Opacity must be between 0 and 1.", span);
            return ErrorValue.Instance;
        }
        if (expected == UiSemanticType.String && tokens.Length == 1)
            return new UiStringValue(tokens[0].Value?.ToString() ?? tokens[0].Text);

        string compact = string.Concat(tokens.Select(token => token.Text));
        if (expected == UiSemanticType.PresentationPattern && _catalog.TryGetPattern(compact, out UiSymbolId pattern))
            return new UiSymbolValue(pattern, compact, UiSemanticType.PresentationPattern);
        if (expected == UiSemanticType.Presentation && _catalog.TryGetPresentation(compact, out UiPresentationSymbol? presentation) && presentation != null)
            return new UiSymbolValue(presentation.Id, presentation.Name, UiSemanticType.Presentation);
        if (expected == UiSemanticType.Region && _catalog.TryGetRegion(compact, out UiSymbolId region))
            return new UiSymbolValue(region, compact, UiSemanticType.Region);

        if (expected.Kind is UiSemanticTypeKind.SurfaceToken or UiSemanticTypeKind.ColorToken or
            UiSemanticTypeKind.SpaceToken or UiSemanticTypeKind.RadiusToken or UiSemanticTypeKind.MotionToken or
            UiSemanticTypeKind.TypographyToken or UiSemanticTypeKind.ElevationToken or UiSemanticTypeKind.TransformToken or
            UiSemanticTypeKind.Opacity or UiSemanticTypeKind.Border)
        {
            if (_catalog.TryGetToken(compact, out UiTokenSymbol? token) && token != null)
            {
                if (token.Type == expected) return new UiSymbolValue(token.Id, token.Name, token.Type);
                Report("LUI2012", $"Token '{compact}' has type {token.Type}, expected {expected}.", span);
                return ErrorValue.Instance;
            }
        }

        if (expected == UiSemanticType.Border && tokens.Length >= 2 && TrySingleInt(new[] { tokens[^1] }, out int width))
        {
            string colorName = string.Concat(tokens[..^1].Select(token => token.Text));
            if (_catalog.TryGetToken(colorName, out UiTokenSymbol? color) && color?.Type == UiSemanticType.ColorToken)
                return new UiBorderValue(new UiSymbolValue(color.Id, color.Name, color.Type), width);
        }

        Report("LUI2013", $"Value '{string.Join(" ", tokens.Select(token => token.Text))}' is not assignable to {expected}.", span);
        return ErrorValue.Instance;
    }

    private void ValidatePresentationContracts()
    {
        if (!_catalog.TryGetPresentation("NavigationList", out UiPresentationSymbol? navigationList) ||
            navigationList == null ||
            !_catalog.TryGetPresentationProperty("itemSizing", out UiPropertySymbol? itemSizing) ||
            itemSizing == null ||
            !_catalog.TryGetPropertyValue(itemSizing, "Adaptive", out UiEnumValueSymbol? adaptive) ||
            adaptive == null)
            return;

        foreach (IGrouping<UiSymbolId?, UiPropertyAssignmentIr> targetAssignments in
                 _assignments.Where(assignment => assignment.Target != null).GroupBy(assignment => assignment.Target))
        {
            UiSymbolId target = targetAssignments.Key!.Value;
            UiPropertyAssignmentIr[] assignments = targetAssignments.ToArray();
            UiSymbolId?[] profiles = assignments.Select(assignment => assignment.Profile)
                .Append(null)
                .Distinct()
                .ToArray();
            var reported = new HashSet<UiPropertyAssignmentIr>();
            foreach (UiSymbolId? profile in profiles)
            {
                UiPropertyAssignmentIr? sizing = Effective(assignments, "itemSizing", profile);
                if (sizing?.Value is not UiSymbolValue sizingValue || sizingValue.Symbol != adaptive.Id)
                    continue;
                UiPropertyAssignmentIr? view = Effective(assignments, "view", profile);
                bool isNavigationList = view?.Value is UiSymbolValue viewValue &&
                                        viewValue.Symbol == navigationList.Id;
                if (!isNavigationList && view == null &&
                    _context.TryGetElement(target, out UiElementSymbol? element) && element != null)
                {
                    UiSymbolId navigate = _catalog.Capability("Navigate");
                    UiSymbolId browse = _catalog.Capability("Browse");
                    isNavigationList = element.Capabilities.Contains(navigate) &&
                                       !element.Capabilities.Contains(browse);
                }
                if (!isNavigationList || !reported.Add(sizing)) continue;
                Report(
                    "LUI2019",
                    "NavigationList is Uniform-only in v1; Adaptive item sizing and wrapping are not supported.",
                    sizing.Provenance.Span);
            }
        }
    }

    private static UiPropertyAssignmentIr? Effective(
        IReadOnlyList<UiPropertyAssignmentIr> assignments,
        string property,
        UiSymbolId? profile)
        => assignments.FirstOrDefault(assignment =>
               assignment.Profile == profile &&
               assignment.State == null &&
               string.Equals(assignment.Property.Name, property, StringComparison.Ordinal))
           ?? (profile == null
               ? null
               : assignments.FirstOrDefault(assignment =>
                   assignment.Profile == null &&
                   assignment.State == null &&
                   string.Equals(assignment.Property.Name, property, StringComparison.Ordinal)));

    private UiElementSymbol ResolveElement(string name, UiTextSpan span)
    {
        if (_context.TryGetElement(name, out UiElementSymbol? element) && element != null) return element;
        if (_context.TryGetGraphNode(name, out UiSemanticNode? node) && node != null)
        {
            Report("UIG021", $"Graph node '{node.Id}' is auxiliary and cannot be a presentation target.", span);
            return _context.CreateUndeclaredElement(name);
        }
        if (_context.RequireDeclaredElements) Report("LUI2014", $"Unknown semantic element '{name}'.", span);
        return _context.CreateUndeclaredElement(name);
    }

    private UiSymbolId ResolveRole(string name, UiTextSpan span)
    {
        if (_context.TryGetRole(name, out UiSymbolId role)) return role;
        if (_context.RequireDeclaredRoles) Report("LUI2015", $"Unknown visual role '{name}'.", span);
        return _context.CreateUndeclaredRole(name);
    }

    private UiSymbolId? BindProfile(UiSyntaxToken? token, UiTextSpan fallback)
        => token == null ? null : BindProfileName(token.Text, token.Span);

    private UiSymbolId? BindProfileName(string name, UiTextSpan span)
    {
        if (_catalog.TryGetProfile(name, out UiSymbolId profile)) return profile;
        Report("LUI2016", $"Unknown presentation profile '{name}'.", span);
        return null;
    }

    private UiSymbolId DocumentId()
    {
        string name = string.IsNullOrWhiteSpace(_syntax.NameToken.Text) ? "invalid" : _syntax.NameToken.Text;
        return _context.OwnerId.Child($"asset/{_syntax.DocumentKind}/{name}");
    }

    private UiSourceProvenance Provenance(UiTextSpan span) => new(_syntax.SourceName, span);

    private void Report(string id, string message, UiTextSpan span)
        => _diagnostics.Add(new UiDiagnostic(id, UiDiagnosticSeverity.Error, message, span, _syntax.SourceName));

    private static bool TrySingleInt(UiSyntaxToken[] tokens, out int value)
    {
        value = default;
        if (tokens.Length != 1 || tokens[0].Value is not int integer) return false;
        value = integer;
        return true;
    }

    private static bool TrySingleNumber(UiSyntaxToken[] tokens, out double value)
    {
        value = default;
        if (tokens.Length != 1) return false;
        if (tokens[0].Value is int integer) { value = integer; return true; }
        if (tokens[0].Value is double number) { value = number; return true; }
        return double.TryParse(tokens[0].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private sealed record ErrorValue() : UiBoundValue(UiSemanticType.Error)
    {
        public static readonly ErrorValue Instance = new();
    }
}
