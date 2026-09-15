using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Tooling.Metadata;

public sealed record UiNamedSymbolMetadata(UiSymbolId Id, string Name);

public sealed record UiTokenMetadata(UiSymbolId Id, string Name, UiSemanticType Type);

public sealed record UiEnumValueMetadata(UiSymbolId Id, string Name, UiSymbolId Property);

public sealed record UiPresentationMetadata(
    UiSymbolId Id,
    string Name,
    IReadOnlyList<UiSymbolId> SupportedCapabilities);

public sealed record UiPropertyMetadata(
    UiSymbolId Id,
    string Name,
    UiDefinitionKind DefinitionKind,
    UiSemanticType Type,
    UiPropertyEffects Effects,
    bool Animatable);

public sealed class UiLanguageMetadata
{
    internal UiLanguageMetadata(
        UiTokenMetadata[] tokens,
        UiPresentationMetadata[] presentations,
        UiNamedSymbolMetadata[] patterns,
        UiNamedSymbolMetadata[] regions,
        UiNamedSymbolMetadata[] profiles,
        UiNamedSymbolMetadata[] states,
        UiEnumValueMetadata[] enumValues,
        UiPropertyMetadata[] properties)
    {
        Tokens = Array.AsReadOnly(tokens);
        Presentations = Array.AsReadOnly(presentations);
        Patterns = Array.AsReadOnly(patterns);
        Regions = Array.AsReadOnly(regions);
        Profiles = Array.AsReadOnly(profiles);
        States = Array.AsReadOnly(states);
        EnumValues = Array.AsReadOnly(enumValues);
        Properties = Array.AsReadOnly(properties);
    }

    public IReadOnlyList<UiTokenMetadata> Tokens { get; }
    public IReadOnlyList<UiPresentationMetadata> Presentations { get; }
    public IReadOnlyList<UiNamedSymbolMetadata> Patterns { get; }
    public IReadOnlyList<UiNamedSymbolMetadata> Regions { get; }
    public IReadOnlyList<UiNamedSymbolMetadata> Profiles { get; }
    public IReadOnlyList<UiNamedSymbolMetadata> States { get; }
    public IReadOnlyList<UiEnumValueMetadata> EnumValues { get; }
    public IReadOnlyList<UiPropertyMetadata> Properties { get; }
}

/// <summary>Stable, sorted semantic metadata for editors, generators, and build integrations.</summary>
public static class UiLanguageMetadataExporter
{
    public static UiLanguageMetadata Export(UiSemanticCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new UiLanguageMetadata(
            ExportTokens(catalog),
            ExportPresentations(catalog),
            catalog.Patterns.Select(item => new UiNamedSymbolMetadata(item.Value, item.Key)).Sorted(),
            catalog.Regions.Select(item => new UiNamedSymbolMetadata(item.Value, item.Key)).Sorted(),
            catalog.Profiles.Select(item => new UiNamedSymbolMetadata(item.Value, item.Key)).Sorted(),
            catalog.States.Select(item => new UiNamedSymbolMetadata(item.Value, item.Key)).Sorted(),
            ExportEnumValues(catalog),
            ExportProperties(catalog));
    }

    private static UiTokenMetadata[] ExportTokens(UiSemanticCatalog catalog)
        => catalog.Tokens
            .OrderBy(token => token.Name, StringComparer.Ordinal)
            .Select(token => new UiTokenMetadata(token.Id, token.Name, token.Type))
            .ToArray();

    private static UiPresentationMetadata[] ExportPresentations(UiSemanticCatalog catalog)
        => catalog.Presentations
            .OrderBy(presentation => presentation.Name, StringComparer.Ordinal)
            .Select(presentation => new UiPresentationMetadata(
                presentation.Id,
                presentation.Name,
                Array.AsReadOnly(presentation.SupportedCapabilities
                    .OrderBy(id => id.ToString(), StringComparer.Ordinal)
                    .ToArray())))
            .ToArray();

    private static UiEnumValueMetadata[] ExportEnumValues(UiSemanticCatalog catalog)
        => catalog.EnumValues
            .OrderBy(value => value.Property.ToString(), StringComparer.Ordinal)
            .ThenBy(value => value.Name, StringComparer.Ordinal)
            .Select(value => new UiEnumValueMetadata(value.Id, value.Name, value.Property))
            .ToArray();

    private static UiPropertyMetadata[] ExportProperties(UiSemanticCatalog catalog)
        => catalog.Properties
            .OrderBy(property => property.DefinitionKind)
            .ThenBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => new UiPropertyMetadata(
                property.Id,
                property.Name,
                property.DefinitionKind,
                property.Type,
                property.Effects,
                property.Animatable))
            .ToArray();

    private static UiNamedSymbolMetadata[] Sorted(this IEnumerable<UiNamedSymbolMetadata> values)
        => values.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
}
