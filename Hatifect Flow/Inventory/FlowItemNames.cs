using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Content;
using Hatifect.UI.Experience;
using StardewValley;
using StardewValley.GameData.Objects;

namespace Hatifect.Flow.Inventory;

// Captures ordinary item-type names before publication; scene formatters never read game content.
internal static class FlowItemNames
{
    internal static UiLocalizedText Capture(string qualifiedItemId)
    {
        var data = ItemRegistry.GetDataOrErrorItem(qualifiedItemId);
        string fallback = string.IsNullOrWhiteSpace(data.DisplayName) ? qualifiedItemId : data.DisplayName;
        return Capture(fallback, (data.RawData as ObjectData)?.DisplayName, Game1.content);
    }

    internal static UiLocalizedText Capture(string fallback, string? rawName,
        LocalizedContentManager content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        // Custom tokens and compound names keep the native fallback; this is not a general token parser.
        const string prefix = "[LocalizedText ";
        if (rawName is null || !rawName.StartsWith(prefix, StringComparison.Ordinal) || !rawName.EndsWith(']'))
            return new(fallback, translations);
        ReadOnlySpan<char> path = rawName.AsSpan(prefix.Length, rawName.Length - prefix.Length - 1);
        int separator = path.IndexOf(':');
        if (separator <= 0 || separator == path.Length - 1) return new(fallback, translations);
        foreach (char character in path)
            if (char.IsWhiteSpace(character) || character is '[' or ']') return new(fallback, translations);
        string asset = path[..separator].ToString(), key = path[(separator + 1)..].ToString();
        string? english = Read(asset, LocalizedContentManager.LanguageCode.en);
        string? russian = Read(asset + ".ru-RU", LocalizedContentManager.LanguageCode.ru) ?? english;
        if (english is not null) { translations.Add("en", english); translations.Add("en-US", english); }
        if (russian is not null) { translations.Add("ru", russian); translations.Add("ru-RU", russian); }
        return new(fallback, translations);

        string? Read(string exactAsset, LocalizedContentManager.LanguageCode language)
        {
            string? name;
            try { name = ReadExact(content, asset, exactAsset, key, language); }
            catch (ContentLoadException) { return null; }
            // Unsupported nested text must not be presented as an explicitly resolved translation.
            if (name is null || name.Contains('[') || name.Contains(']')) return null;
            name = name.Replace("\\n", "\n").Replace('\u00a0', ' ').Replace("\u200b", string.Empty);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
    }

    private static string? ReadExact(LocalizedContentManager content, string asset, string exactAsset, string key, LocalizedContentManager.LanguageCode language)
    {
        // SMAPI's LoadImpl resolves this exact asset name without consulting its ambient-language map.
        var strings = content.LoadImpl<Dictionary<string, string>>(asset, exactAsset, language);
        if (!strings.TryGetValue(key + ".desktop", out string? name)) strings.TryGetValue(key, out name);
        return name is null ? null : content.PreprocessString(name);
    }
}
