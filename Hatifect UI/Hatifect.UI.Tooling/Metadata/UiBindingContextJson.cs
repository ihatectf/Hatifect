using System;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Tooling.Metadata;

/// <summary>Public bridge from an Experience's binding context to deterministic editor metadata.</summary>
public static class UiBindingContextJson
{
    /// <summary>
    /// Exports a detached UTF-8 snapshot using the smallest lossless schema (v1 or v2). Pass the parsed JSON object as
    /// initialize.initializationOptions.bindingMetadata, or persist it in the consumer's tooling output.
    /// </summary>
    public static byte[] Export(UiBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return UiBindingContextMetadataWire.Serialize(UiBindingContextMetadataExporter.Export(context));
    }

    /// <summary>Imports validated schema-v1 or schema-v2 metadata into a new binding context with no shared mutable state.</summary>
    public static UiBindingContext Import(ReadOnlySpan<byte> utf8Json)
        => UiBindingContextMetadataWire.CreateBindingContext(UiBindingContextMetadataWire.Deserialize(utf8Json));
}
