using System;
using System.IO;
using System.Linq;
using System.Text;
using Hatifect.UI;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using Xunit;

namespace Hatifect.UI.Tooling.Tests;

public sealed class BindingContextMetadataTests
{
    [Fact]
    public void ExportIsTypedSortedAndDetachedFromMutableBindingContext()
    {
        var owner = new UiSymbolId("Author.Mod", "storage");
        var capabilityA = new UiSymbolId("Author.Mod", "capability/a");
        var capabilityZ = new UiSymbolId("Author.Mod", "capability/z");
        var context = new UiBindingContext(owner)
        {
            RequireDeclaredElements = false,
            RequireDeclaredRoles = true
        };
        context
            .DeclareElement("Zulu", capabilityZ, capabilityA, capabilityZ)
            .DeclareElement("Alpha", capabilityA)
            .DeclareRole("Secondary")
            .DeclareRole("Primary");

        UiBindingContextMetadata metadata = UiBindingContextMetadataExporter.Export(context);
        context.DeclareElement("Later", capabilityZ).DeclareRole("Later");

        Assert.Equal(owner, metadata.OwnerId);
        Assert.False(metadata.RequireDeclaredElements);
        Assert.True(metadata.RequireDeclaredRoles);
        Assert.Equal(new[] { "Alpha", "Zulu" }, metadata.Elements.Select(element => element.Name));
        Assert.Equal(
            new[] { capabilityA, capabilityZ },
            metadata.Elements.Single(element => element.Name == "Zulu").Capabilities);
        Assert.Equal(new[] { "Primary", "Secondary" }, metadata.Roles.Select(role => role.Name));
        Assert.Equal(owner.Child("element/Alpha"), metadata.Elements[0].Id);
        Assert.Equal(owner.Child("role/Primary"), metadata.Roles[0].Id);
        Assert.DoesNotContain(metadata.Elements, element => element.Name == "Later");
        Assert.DoesNotContain(metadata.Roles, role => role.Name == "Later");
    }

    [Fact]
    public void SchemaV1RoundTripIsDeterministicAndReconstructsBindingContext()
    {
        var capabilityA = new UiSymbolId("Author.Mod", "capability/a");
        var capabilityZ = new UiSymbolId("Author.Mod", "capability/z");
        var source = new UiBindingContext(new UiSymbolId("Author.Mod", "storage"))
        {
            RequireDeclaredElements = false,
            RequireDeclaredRoles = true
        };
        source.DeclareElement("Zulu", capabilityZ, capabilityA).DeclareRole("Primary");
        UiBindingContextMetadata exported = UiBindingContextMetadataExporter.Export(source);

        byte[] first = UiBindingContextMetadataWire.Serialize(exported);
        UiBindingContextMetadata imported = UiBindingContextMetadataWire.Deserialize(first);
        byte[] second = UiBindingContextMetadataWire.Serialize(imported);
        UiBindingContext reconstructed = UiBindingContextMetadataWire.CreateBindingContext(imported);

        Assert.Equal(first, second);
        Assert.Contains("\"schemaVersion\":1", Encoding.UTF8.GetString(first));
        Assert.Equal(exported.OwnerId, reconstructed.OwnerId);
        Assert.False(reconstructed.RequireDeclaredElements);
        Assert.True(reconstructed.RequireDeclaredRoles);
        Assert.True(reconstructed.TryGetElement("Zulu", out UiElementSymbol? element));
        Assert.Equal(new[] { capabilityA, capabilityZ }, element!.Capabilities);
        Assert.True(reconstructed.TryGetRole("Primary", out UiSymbolId role));
        Assert.Equal(exported.OwnerId.Child("role/Primary"), role);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"ownerId\":\"Author.Mod/storage\",\"requireDeclaredElements\":true,\"requireDeclaredRoles\":true,\"elements\":[],\"roles\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"ownerId\":\"Author.Mod/storage\",\"requireDeclaredElements\":true,\"requireDeclaredRoles\":true,\"elements\":[{\"id\":\"Author.Mod/storage/element/Items\",\"name\":\"Items\",\"capabilities\":[]},{\"id\":\"Author.Mod/storage/element/Items\",\"name\":\"Items\",\"capabilities\":[]}],\"roles\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"ownerId\":\"Author.Mod/storage\",\"requireDeclaredElements\":true,\"requireDeclaredRoles\":true,\"elements\":[{\"id\":\"Author.Mod/storage/element/Wrong\",\"name\":\"Items\",\"capabilities\":[]}],\"roles\":[]}")]
    public void ImportRejectsUnknownSchemaDuplicateAndConflictingIdentity(string json)
        => Assert.Throws<InvalidDataException>(() =>
            UiBindingContextMetadataWire.Deserialize(Encoding.UTF8.GetBytes(json)));
}
