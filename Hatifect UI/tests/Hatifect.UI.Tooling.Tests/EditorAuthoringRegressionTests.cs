using System;
using System.Linq;
using System.Threading.Tasks;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Editor;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class EditorAuthoringRegressionTests
{
    [Fact]
    public async Task BlankLinesPreserveFollowingBlockCompletionAndCapabilityFiltering()
    {
        var session = await Open("visual Storage\nItem\n    surface = Surface.Raised\n\n    opa");
        var completion = await Send(session, "textDocument/completion", At(4, 7));
        Assert.Equal("opacity", Assert.Single(completion.Result!.Value.GetProperty("items").EnumerateArray()).GetProperty("label").GetString());
        session = await Open("presentation Storage\nItems\n    width = 100\n\n    view = ");
        completion = await Send(session, "textDocument/completion", At(4, 11));
        string?[] labels = completion.Result!.Value.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("label").GetString()).ToArray();
        Assert.Contains("Gallery", labels);
        Assert.DoesNotContain("NavigationList", labels);
    }

    [Fact]
    public async Task UnquotedStringDoesNotAliasCatalogTokenInHoverHighlightOrReferences()
    {
        const string source = "presentation Storage\nItems\n    density = Accent";
        var session = await Open(source);
        Assert.True(session.TryGetDocument(DocumentUri, out var document));
        Assert.True(document!.IsValid);
        UiEditorSymbol literal = document.Analysis.SymbolAt(source.IndexOf("Accent", StringComparison.Ordinal))!;
        Assert.Equal(UiEditorSymbolKind.String, literal.Kind);
        Assert.Null(literal.Id);
        var hover = await Send(session, "textDocument/hover", At(2, 17));
        Assert.Null(hover.Result);
        const string visualUri = "file:///Visual.hatifect";
        await Send(session, "textDocument/didOpen", new
        {
            textDocument = new { uri = visualUri, version = 1, text = "visual Colors\nItem\n    foreground = Accent" }
        }, notification: true);
        var references = await Send(session, "textDocument/references", new
        {
            textDocument = new { uri = visualUri }, position = new { line = 2, character = 19 },
            context = new { includeDeclaration = false }
        });
        Assert.Equal(visualUri, Assert.Single(references.Result!.Value.EnumerateArray()).GetProperty("uri").GetString());
    }

    [Fact]
    public void LongQualifiedRecoveryValueAvoidsQuadraticTemporaryStringAllocation()
    {
        string source = "visual Storage\nItem\n    surface = " + string.Join(".", Enumerable.Repeat("a", 20000));
        var workspace = new UiEditorWorkspace();
        var context = new UiBindingContext(new UiSymbolId("Author.Mod", "storage")).DeclareRole("Item");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var document = workspace.Update(DocumentUri, 1, source, context);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Id == "LUI2013");
        Assert.InRange(allocated, 1, 128 * 1024 * 1024);
    }

    [Fact]
    public void QuickFixDiagnosticPreparationHasBoundedAllocationWhenNoCorrectionExists()
    {
        string source = "visual Storage\nItem\n" + string.Concat(Enumerable.Range(0, 2000).Select(i => $"    zzzzzz{i:D5} = 0\n"));
        var catalog = UiSemanticCatalog.CreateFoundation();
        var context = new UiBindingContext(new UiSymbolId("Author.Mod", "storage")).DeclareRole("Item");
        var document = new UiEditorWorkspace(catalog).Update(DocumentUri, 1, source, context);
        Assert.Equal(2000, document.Diagnostics.Count);
        var compiler = new UiCompiler(catalog);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var fixes = UiEditorQuickFixes.Find(document, compiler, 0, source.Length, 1024 * 1024);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(fixes);
        Assert.InRange(allocated, 1, 8 * 1024 * 1024);
    }
}
