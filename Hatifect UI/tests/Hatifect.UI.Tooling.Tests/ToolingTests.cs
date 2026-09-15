using System;
using System.Linq;
using Hatifect.UI;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Text;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Editor;
using Hatifect.UI.Tooling.Formatting;
using Hatifect.UI.Tooling.Inspection;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Validation;
using Xunit;

namespace Hatifect.UI.Tooling.Tests;

public sealed class ToolingTests
{
    [Fact]
    public void FormatterIsCanonicalIdempotentAndPreservesDottedValues()
    {
        const string source = "visual Storage\r\n\r\nItem@Hover\r\n\tsurface=Surface.Raised\r\n";
        const string expected = "visual Storage\n\nItem@Hover\n    surface = Surface.Raised\n";
        var formatter = new UiSourceFormatter();

        UiFormatResult first = formatter.Format(source, "Storage#visual");
        UiFormatResult second = formatter.Format(first.Text, "Storage#visual");

        Assert.True(first.CanApply);
        Assert.True(first.Changed);
        Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Id == "LUI0001");
        Assert.Equal(expected, first.Text);
        Assert.True(second.CanApply);
        Assert.False(second.Changed);
        Assert.Equal(expected, second.Text);
    }

    [Fact]
    public void FormatterDoesNotRewriteRecoveredInvalidSyntax()
    {
        const string source = "visual Storage\n\nItem\n surface Surface.Raised\n";

        UiFormatResult result = new UiSourceFormatter().Format(source, "Broken#visual");

        Assert.False(result.CanApply);
        Assert.False(result.Changed);
        Assert.Equal(source, result.Text);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == UiDiagnosticSeverity.Error);
    }

    [Fact]
    public void BuildValidatorUsesTheSameCompilerDiagnosticsAsRuntime()
    {
        const string source = "visual Storage\n\nItem\n    mystery = Surface.Raised\n";
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiBindingContext directContext = Context();
        UiBindingContext toolingContext = Context();
        UiCompilationResult direct = new UiCompiler(catalog).Compile(source, directContext, "Storage#visual");

        UiBuildValidationResult tooling = new UiBuildValidator(catalog).Validate(new[]
        {
            new UiBuildAsset("Storage#visual", source, toolingContext, UiDefinitionKind.Visual)
        });

        Assert.False(tooling.IsValid);
        Assert.Equal(direct.Diagnostics.Select(item => item.Id), tooling.Diagnostics.Select(item => item.Id));
    }

    [Fact]
    public void BuildValidatorOrdersInputsAndRejectsKindMismatchAndDuplicateIdentity()
    {
        const string source = "visual Storage\n\nItem\n    surface = Surface.Raised\n";
        var validator = new UiBuildValidator();

        UiBuildValidationResult result = validator.Validate(new[]
        {
            new UiBuildAsset("z.visual", source, Context(), UiDefinitionKind.Visual),
            new UiBuildAsset("a.visual", source, Context(), UiDefinitionKind.Presentation),
            new UiBuildAsset("b.visual", source, Context(), UiDefinitionKind.Visual)
        });

        Assert.False(result.IsValid);
        Assert.Equal(new[] { "LUI3001", "LUI3002" }, result.Diagnostics.Select(item => item.Id));
        UiValidatedDefinition definition = Assert.Single(result.Definitions);
        Assert.Equal("b.visual", definition.SourceName);
    }

    [Fact]
    public void MetadataExportIsSortedTypedAndCompleteEnoughForEditors()
    {
        UiLanguageMetadata metadata = UiLanguageMetadataExporter.Export(UiSemanticCatalog.CreateFoundation());

        Assert.Equal(metadata.Tokens.OrderBy(item => item.Name, StringComparer.Ordinal), metadata.Tokens);
        Assert.Equal(metadata.Presentations.OrderBy(item => item.Name, StringComparer.Ordinal), metadata.Presentations);
        Assert.Contains(metadata.Profiles, item => item.Name == "Compact");
        Assert.Contains(metadata.States, item => item.Name == "Focused");
        UiPropertyMetadata itemSizing = Assert.Single(
            metadata.Properties,
            item => item.Name == "itemSizing");
        Assert.Equal(UiSemanticType.EnumValue, itemSizing.Type);
        Assert.Equal(
            new[] { "Adaptive", "Uniform" },
            metadata.EnumValues
                .Where(item => item.Property == itemSizing.Id)
                .Select(item => item.Name));
        UiPropertyMetadata density = Assert.Single(
            metadata.Properties,
            item => item.Name == "density");
        Assert.Equal(UiSemanticType.EnumValue, density.Type);
        Assert.Equal(
            new[] { "Comfortable", "Compact", "Default" },
            metadata.EnumValues
                .Where(item => item.Property == density.Id)
                .Select(item => item.Name));
        Assert.Contains(metadata.Tokens, item =>
            item.Name == "Space.M" && item.Type == UiSemanticType.SpaceToken);
        Assert.Contains(metadata.Properties, item =>
            item.Name == "padding" &&
            item.DefinitionKind == UiDefinitionKind.Visual &&
            item.Type == UiSemanticType.SpaceToken &&
            item.Effects.HasFlag(UiPropertyEffects.Measure));
    }

    [Fact]
    public void SourceInspectionIndexesTypedPlacementsAssignmentsProfilesAndStates()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiSymbolId owner = new("Author.Mod", "storage");
        UiBindingContext context = new UiBindingContext(owner)
            .DeclareElement("Items", catalog.Capability("Browse"))
            .DeclareRole("Item");
        UiCompilationResult presentation = new UiCompiler(catalog).Compile(@"presentation Storage

Items -> Primary

Items.view
    default = Gallery
    Compact = List
", context, "Storage#presentation");
        UiCompilationResult visual = new UiCompiler(catalog).Compile(@"visual Storage

Item@Selected
    surface = Surface.Pressed
", context, "Storage#visual");
        Assert.True(presentation.IsValid);
        Assert.True(visual.IsValid);
        var index = new UiSourceInspectionIndex(new[]
        {
            presentation.Definition!,
            visual.Definition!
        });
        UiPresentationDefinition presentationDefinition = Assert.IsType<UiPresentationDefinition>(presentation.Definition);
        UiVisualDefinition visualDefinition = Assert.IsType<UiVisualDefinition>(visual.Definition);
        UiSymbolId items = owner.Child("element/Items");
        UiSymbolId compactProfile = new("Hatifect.UI", "profile/Compact");
        UiSymbolId selectedState = new("Hatifect.UI", "state/Selected");
        Assert.True(catalog.TryGetProperty(
            UiDefinitionKind.Presentation,
            "view",
            out UiPropertySymbol? view));
        Assert.True(catalog.TryGetVisualProperty("surface", out UiPropertySymbol? surface));

        UiSourceInspectionEntry placement = Assert.IsType<UiSourceInspectionEntry>(
            index.RevealPlacement(presentationDefinition.Id, items));
        UiSourceInspectionEntry compact = Assert.IsType<UiSourceInspectionEntry>(index.RevealAssignment(
            presentationDefinition.Id,
            items,
            view!.Id,
            compactProfile));
        UiSourceInspectionEntry selected = Assert.IsType<UiSourceInspectionEntry>(index.RevealAssignment(
            visualDefinition.Id,
            owner.Child("role/Item"),
            surface!.Id,
            state: selectedState));

        Assert.Equal(new UiSymbolId("Hatifect.UI", "region/Primary"), placement.Region);
        Assert.Equal(compactProfile, compact.Profile);
        Assert.Equal(selectedState, selected.State);
        Assert.Equal("Storage#visual", selected.Source.SourceName);
        Assert.Contains(selected, index.At(selected.Source.SourceName, selected.Source.Span.Start));
        Assert.Equal(
            index.Entries.OrderBy(entry => entry.Source.SourceName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Source.Span.Start),
            index.Entries);
    }

    [Fact]
    public void SourceInspectionTypedRevealDoesNotScanOrAllocateAcrossTenThousandEntries()
    {
        const int count = 10_000;
        UiSymbolId definitionId = new("Author.Mod", "visual/large");
        UiSymbolId propertyId = new("Hatifect.UI", "property/Visual/surface");
        var property = new UiPropertySymbol(
            propertyId,
            "surface",
            UiDefinitionKind.Visual,
            UiSemanticType.SurfaceToken,
            UiPropertyEffects.Render,
            Animatable: false);
        var recipes = new UiPropertyAssignmentIr[count];
        var targets = new UiSymbolId[count];
        for (int index = 0; index < count; index++)
        {
            UiSymbolId target = new("Author.Mod", $"role/item-{index:D5}");
            targets[index] = target;
            recipes[index] = new UiPropertyAssignmentIr(
                target,
                property,
                Profile: null,
                State: null,
                new UiSymbolValue(
                    new UiSymbolId("Hatifect.UI", "token/Surface/Raised"),
                    "Surface.Raised",
                    UiSemanticType.SurfaceToken),
                new UiSourceProvenance(
                    "Large#visual",
                    new UiTextSpan(index * 4, 1, index, 0)));
        }

        var inspection = new UiSourceInspectionIndex(new UiBoundDefinition[]
        {
            new UiVisualDefinition(definitionId, recipes)
        });
        UiSourceInspectionEntry expected = Assert.IsType<UiSourceInspectionEntry>(
            inspection.RevealAssignment(definitionId, targets[^1], propertyId));
        // Concurrent assemblies can delay tier promotion beyond a short warm-up.
        // Reach steady-state lookup code before measuring the allocation contract.
        for (int warmup = 0; warmup < count; warmup++)
            Assert.Same(expected, inspection.RevealAssignment(definitionId, targets[^1], propertyId));

        int matched = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int query = 0; query < count; query++)
            if (ReferenceEquals(expected, inspection.RevealAssignment(definitionId, targets[^1], propertyId))) matched++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(count, matched);
        Assert.Equal(count, inspection.Entries.Count);
        Assert.True(
            allocated <= 4096,
            $"Indexed typed source reveal allocated {allocated} bytes for {count} repeated queries.");

        IReadOnlyList<UiSourceInspectionEntry> atLast = inspection.At(
            "Large#visual",
            (count - 1) * 4,
            out int inspectedEntries);
        Assert.Same(expected, Assert.Single(atLast));
        Assert.True(
            inspectedEntries <= 32,
            $"Offset source reveal inspected {inspectedEntries} of {count} entries.");
    }

    [Fact]
    public void SourceInspectionOffsetIndexPreservesOverlapsZeroLengthAndSourceOrder()
    {
        UiSymbolId definitionId = new("Author.Mod", "visual/overlap");
        var property = new UiPropertySymbol(
            new UiSymbolId("Hatifect.UI", "property/Visual/surface"),
            "surface",
            UiDefinitionKind.Visual,
            UiSemanticType.SurfaceToken,
            UiPropertyEffects.Render,
            Animatable: false);
        var value = new UiSymbolValue(
            new UiSymbolId("Hatifect.UI", "token/Surface/Raised"),
            "Surface.Raised",
            UiSemanticType.SurfaceToken);
        UiSymbolId outer = new("Author.Mod", "role/outer");
        UiSymbolId inner = new("Author.Mod", "role/inner");
        UiSymbolId point = new("Author.Mod", "role/point");
        UiSymbolId foreign = new("Author.Mod", "role/foreign");
        var inspection = new UiSourceInspectionIndex(new UiBoundDefinition[]
        {
            new UiVisualDefinition(definitionId, new[]
            {
                new UiPropertyAssignmentIr(outer, property, null, null, value,
                    new UiSourceProvenance("Overlap#visual", new UiTextSpan(0, 10, 0, 0))),
                new UiPropertyAssignmentIr(inner, property, null, null, value,
                    new UiSourceProvenance("Overlap#visual", new UiTextSpan(4, 2, 1, 0))),
                new UiPropertyAssignmentIr(point, property, null, null, value,
                    new UiSourceProvenance("Overlap#visual", new UiTextSpan(5, 0, 2, 0))),
                new UiPropertyAssignmentIr(foreign, property, null, null, value,
                    new UiSourceProvenance("Other#visual", new UiTextSpan(5, 1, 0, 0)))
            })
        });

        IReadOnlyList<UiSourceInspectionEntry> atFive = inspection.At(
            "Overlap#visual",
            5,
            out int inspectedAtFive);
        IReadOnlyList<UiSourceInspectionEntry> atSix = inspection.At("Overlap#visual", 6);

        Assert.Equal(new[] { outer, inner, point }, atFive.Select(entry => entry.Target!.Value));
        Assert.Equal(new[] { outer }, atSix.Select(entry => entry.Target!.Value));
        Assert.True(inspectedAtFive <= 3);
        Assert.Empty(inspection.At("Missing#visual", 5));
    }

    [Fact]
    public void EditorWorkspaceCombinesSharedDiagnosticsFormattingAndTypedReveal()
    {
        const string source = "visual Storage\r\n\r\nItem@Hover\r\n\tsurface=Surface.Raised\r\n";
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiEditorDocumentSnapshot snapshot = new UiEditorWorkspace(catalog).Update(
            "Storage#visual",
            version: 4,
            source,
            Context(),
            UiDefinitionKind.Visual);

        Assert.True(snapshot.IsValid);
        Assert.Equal(4, snapshot.Version);
        Assert.True(snapshot.Format.CanApply);
        Assert.True(snapshot.Format.Changed);
        Assert.NotNull(snapshot.Inspection);
        UiVisualDefinition definition = Assert.IsType<UiVisualDefinition>(snapshot.Definition);
        Assert.True(catalog.TryGetVisualProperty("surface", out UiPropertySymbol? surface));
        UiSourceInspectionEntry entry = Assert.IsType<UiSourceInspectionEntry>(
            snapshot.Inspection!.RevealAssignment(
                definition.Id,
                Context().OwnerId.Child("role/Item"),
                surface!.Id,
                state: new UiSymbolId("Hatifect.UI", "state/Hover")));
        Assert.Equal("Storage#visual", entry.Source.SourceName);

        UiCompilationResult direct = new UiCompiler(catalog).Compile(source, Context(), "Storage#visual");
        Assert.Equal(direct.Diagnostics.Select(item => item.Id), snapshot.Diagnostics.Select(item => item.Id));
    }

    [Fact]
    public void EditorWorkspaceRejectsInvalidIrStaleVersionsAndUnboundedRetention()
    {
        var workspace = new UiEditorWorkspace(capacity: 2, maximumSourceLength: 128);
        UiEditorDocumentSnapshot first = workspace.Update(
            "a.visual", 1, "visual A\n\nItem\n    surface = Surface.Raised\n", Context());
        workspace.Update("b.visual", 1, "visual B\n", Context());
        Assert.True(workspace.TryGet("a.visual", out UiEditorDocumentSnapshot? touched));
        Assert.Same(first, touched);

        workspace.Update("c.visual", 1, "visual C\n", Context());

        Assert.Equal(2, workspace.Count);
        Assert.False(workspace.TryGet("b.visual", out _));
        Assert.True(workspace.TryGet("a.visual", out _));
        Assert.True(workspace.TryGet("c.visual", out _));
        Assert.Throws<InvalidOperationException>(() => workspace.Update(
            "a.visual", 0, first.Source, Context()));
        Assert.Throws<InvalidOperationException>(() => workspace.Update(
            "a.visual", 1, "visual Different\n", Context()));

        UiEditorDocumentSnapshot invalid = workspace.Update(
            "a.visual", 2, "visual A\n\nItem\n    mystery = Surface.Raised\n", Context());
        Assert.False(invalid.IsValid);
        Assert.Null(invalid.Definition);
        Assert.Null(invalid.Inspection);
        Assert.Contains(invalid.Diagnostics, item => item.Severity == UiDiagnosticSeverity.Error);

        UiEditorDocumentSnapshot mismatched = workspace.Update(
            "kind.asset", 1, "visual Kind\n", Context(), UiDefinitionKind.Presentation);
        Assert.False(mismatched.IsValid);
        Assert.Contains(mismatched.Diagnostics, item => item.Id == "LUI3001");
        Assert.Null(mismatched.Inspection);
        Assert.Throws<ArgumentException>(() => workspace.Update(
            "large.visual", 1, new string('x', 129), Context()));
    }

    private static UiBindingContext Context()
        => new UiBindingContext(new UiSymbolId("Author.Mod", "storage"))
            .DeclareRole("Item");
}
