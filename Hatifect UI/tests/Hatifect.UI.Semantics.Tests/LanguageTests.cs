using System.Linq;
using Hatifect.UI.Language;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Syntax;
using Xunit;

namespace Hatifect.UI.Semantics.Tests;

public sealed class LanguageTests
{
    [Fact]
    public void OneParserHandlesPresentationStructureAndProfileMatrix()
    {
        const string source = @"presentation Storage

use = MasterDetail

Items -> Primary
Inspector -> Context

Items
    view = Gallery
    density = Compact

Inspector.view
    default = Side
    Compact = Sheet
    Controller = Route
";

        UiDocumentSyntax document = UiSyntaxTree.Parse(source, "Storage#presentation");

        Assert.Equal(UiDocumentKind.Presentation, document.DocumentKind);
        Assert.Equal("Storage", document.NameToken.Text);
        Assert.Contains(document.Statements, statement => statement is UiPlacementSyntax);
        Assert.Contains(document.Statements, statement => statement is UiBlockSyntax block && block.Target?.ToString() == "Inspector.view");
        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Severity == UiDiagnosticSeverity.Error);
    }

    [Fact]
    public void SameParserHandlesVisualRolesAndStates()
    {
        const string source = @"visual Storage

Item
    surface = Surface.Raised
    radius = Radius.M

Item@Hover
    surface = Surface.Hover
    offset.y = -2
";

        UiDocumentSyntax document = UiSyntaxTree.Parse(source, "Storage#visual");

        Assert.Equal(UiDocumentKind.Visual, document.DocumentKind);
        UiBlockSyntax hover = Assert.IsType<UiBlockSyntax>(document.Statements.Last());
        Assert.Equal("Item", hover.Target?.ToString());
        Assert.Equal("Hover", hover.Specialization?.Text);
        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Severity == UiDiagnosticSeverity.Error);
    }

    [Fact]
    public void IncompleteInputProducesDiagnosticsAndAUsableTree()
    {
        const string source = "visual Storage\n\nItem@Hover\n    surface =\n";

        UiDocumentSyntax document = UiSyntaxTree.Parse(source, "Incomplete#visual");

        Assert.Equal(UiDocumentKind.Visual, document.DocumentKind);
        Assert.Single(document.Statements);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Id == "LUI1015");
        Assert.True(document.EndOfFileToken.Kind == UiSyntaxKind.EndOfFileToken);
    }
}
