using System.IO;
using System.Text;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using Xunit;

namespace Hatifect.UI.Tooling.Tests;

public sealed class PublicBindingMetadataTests
{
    [Fact]
    public void PublicExportImportPreservesCanonicalIdentityCapabilitiesAndDetachedOwnership()
    {
        var catalog = UiSemanticCatalog.CreateFoundation();
        var source = new UiBindingContext(new UiSymbolId("External.Author", "catalog"))
            .DeclareElement("Items", catalog.Capability("Select"), catalog.Capability("Browse"))
            .DeclareRole("Item");
        byte[] bytes = UiBindingContextJson.Export(source);
        UiBindingContext imported = UiBindingContextJson.Import(bytes);
        Assert.True(typeof(UiBindingContextJson).IsPublic);
        Assert.Equal(bytes, UiBindingContextJson.Export(imported));
        Assert.Equal(source.OwnerId, imported.OwnerId);
        Assert.True(imported.TryGetElement("Items", out var element));
        Assert.Contains(catalog.Capability("Browse"), element!.Capabilities);
        Assert.Contains(catalog.Capability("Select"), element.Capabilities);
        source.DeclareRole("Later");
        Assert.False(imported.TryGetRole("Later", out _));
        Assert.True(new UiCompiler(catalog).Compile("presentation Catalog\nItems\n    view = Gallery", imported).IsValid);
    }

    [Fact]
    public void PublicImportRejectsUnknownSchemaInsteadOfPartiallyConstructingContext()
        => Assert.Throws<InvalidDataException>(() => UiBindingContextJson.Import(
            Encoding.UTF8.GetBytes("{\"schemaVersion\":99}")));
}
