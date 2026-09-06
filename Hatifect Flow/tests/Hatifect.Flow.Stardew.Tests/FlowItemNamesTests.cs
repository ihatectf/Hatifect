using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework.Content;
using Hatifect.Flow.Inventory;
using StardewValley;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class FlowItemNamesTests
{
    private const string Asset = "Strings\\Objects";
    private const string Key = "CopperOre_Name";
    private const string RawName = "[LocalizedText Strings\\Objects:CopperOre_Name]";

    [Fact]
    public void CapturesExactEnglishAndRussianTablesAndAliasesWithoutRetainingLoader()
    {
        using var content = new NameContent();
        var english = new Dictionary<string, string> { [Key] = "Copper ore", [Key + ".desktop"] = "Desktop copper ore" };
        var russian = new Dictionary<string, string> { [Key] = "Медная руда" };
        content.Tables[Asset] = english;
        content.Tables[Asset + ".ru-RU"] = russian;

        var names = FlowItemNames.Capture("Captured native name", RawName, content);

        Assert.Equal(new[] { (Asset, Asset, LocalizedContentManager.LanguageCode.en),
            (Asset, Asset + ".ru-RU", LocalizedContentManager.LanguageCode.ru) }, content.Reads);
        Assert.Equal(new[] { "Desktop copper ore", "Медная руда" }, content.Preprocessed);
        Assert.Equal("Prepared Desktop copper ore", names.Resolve("en"));
        Assert.Equal("Prepared Desktop copper ore", names.Resolve("en-US"));
        Assert.Equal("Prepared Медная руда", names.Resolve("ru"));
        Assert.Equal("Prepared Медная руда", names.Resolve("ru-RU"));
        Assert.Equal("Captured native name", names.Resolve(""));
        Assert.Equal("Captured native name", names.Resolve("fr-FR"));
        Assert.Equal("Captured native name", names.Resolve("RU"));
        english[Key + ".desktop"] = "Changed";
        russian[Key] = "Изменено";
        content.Failure = new InvalidOperationException("Capture must be complete");
        Assert.Equal("Prepared Desktop copper ore", names.Resolve("en"));
        Assert.Equal("Prepared Медная руда", names.Resolve("ru-RU"));
        Assert.Equal(2, content.Reads.Count);
        Assert.Equal(2, content.Preprocessed.Count);
    }

    [Theory]
    [InlineData("absent-table")]
    [InlineData("absent-key")]
    [InlineData("blank-name")]
    public void MissingRussianNameUsesCapturedEnglishWithoutAmbientLocaleRead(string missing)
    {
        using var content = new NameContent { Prepare = false };
        content.Tables[Asset] = new() { [Key] = "Copper ore" };
        if (missing != "absent-table")
            content.Tables[Asset + ".ru-RU"] = missing == "absent-key" ? new() : new() { [Key] = " " };

        var names = FlowItemNames.Capture("Ambient French name", RawName, content);

        Assert.Equal("Copper ore", names.Resolve("ru-RU"));
        Assert.Equal("Copper ore", names.Resolve("en"));
        Assert.Equal("Ambient French name", names.Fallback);
        Assert.Equal(2, content.Reads.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[ItemName (O)378]")]
    public void UnavailableNamesKeepTheCapturedNativeFallback(string? unresolved)
    {
        using var content = new NameContent { Prepare = false };
        if (unresolved is not null)
        {
            content.Tables[Asset] = new() { [Key] = unresolved };
            content.Tables[Asset + ".ru-RU"] = new() { [Key] = unresolved };
        }

        var names = FlowItemNames.Capture("Readable native identity", RawName, content);

        Assert.Empty(names.Translations);
        Assert.Equal("Readable native identity", names.Resolve("en"));
        Assert.Equal("Readable native identity", names.Resolve("ru-RU"));
        Assert.Equal(2, content.Reads.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Literal name")]
    [InlineData("[CustomName copper]")]
    [InlineData("[LocalizedText [OtherKey copper]]")]
    [InlineData("[LocalizedText Strings\\Objects:Name argument]")]
    [InlineData("[LocalizedText Strings\\Objects]")]
    [InlineData("[LocalizedText :Name]")]
    [InlineData("[LocalizedText Strings\\Objects:]")]
    [InlineData("[LocalizedText Strings\\Objects:Name")]
    [InlineData("[LocalizedText Strings\\Objects:Name] suffix")]
    public void UnsupportedNameFormsNeverInvokeTheContentLoader(string? rawName)
    {
        using var content = new NameContent { Failure = new InvalidOperationException("No content request expected") };

        var names = FlowItemNames.Capture("Native name", rawName, content);

        Assert.Empty(names.Translations);
        Assert.Equal("Native name", names.Resolve("ru-RU"));
        Assert.Empty(content.Reads);
        Assert.Empty(content.Preprocessed);
    }

    [Fact]
    public void UnexpectedContentFailureRejectsCaptureForTheExistingPublicationRetry()
    {
        var failure = new InvalidOperationException("content callback failed");
        using var content = new NameContent { Failure = failure };

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => FlowItemNames.Capture("Native name", RawName, content)));

        Assert.Single(content.Reads);
        Assert.Empty(content.Preprocessed);
    }

    [Fact]
    public void NormalizesCapturedPlainNamesWithoutRunningTokenResolvers()
    {
        using var content = new NameContent { Prepare = false };
        content.Tables[Asset] = new() { [Key] = "Copper\\nore\u00a0\u200b" };
        content.Tables[Asset + ".ru-RU"] = new() { [Key] = "\u200b" };

        var names = FlowItemNames.Capture("Native name", RawName, content);

        Assert.Equal("Copper\nore ", names.Resolve("en"));
        Assert.Equal("Copper\nore ", names.Resolve("ru-RU"));
        Assert.Equal(2, content.Reads.Count);
    }

    [Fact]
    public void MissingEnglishNameDoesNotDiscardAnAvailableRussianTranslation()
    {
        using var content = new NameContent { Prepare = false };
        content.Tables[Asset + ".ru-RU"] = new() { [Key] = "Медная руда" };

        var names = FlowItemNames.Capture("Native name", RawName, content);

        Assert.Equal("Native name", names.Resolve("en"));
        Assert.Equal("Медная руда", names.Resolve("ru-RU"));
        Assert.Equal(2, names.Translations.Count);
        Assert.Equal(2, content.Reads.Count);
    }

    // Uses the actual game content contract; the real SMAPI content pipeline still needs native acceptance.
    private sealed class NameContent : LocalizedContentManager
    {
        internal NameContent() : base(new EmptyServices(), string.Empty, CultureInfo.InvariantCulture) { }
        internal Dictionary<string, Dictionary<string, string>> Tables { get; } = new(StringComparer.Ordinal);
        internal List<(string BaseAsset, string ExactAsset, LanguageCode Language)> Reads { get; } = new();
        internal List<string> Preprocessed { get; } = new();
        internal bool Prepare { get; set; } = true;
        internal Exception? Failure { get; set; }
        public override T Load<T>(string assetName) => throw new InvalidOperationException("Ambient load is forbidden");
        public override T Load<T>(string assetName, LanguageCode language) => throw new InvalidOperationException("Localized map load is forbidden");
        public override T LoadImpl<T>(string baseAssetName, string localizedAssetName, LanguageCode languageCode)
        {
            Reads.Add((baseAssetName, localizedAssetName, languageCode));
            if (Failure is not null) throw Failure;
            if (!Tables.TryGetValue(localizedAssetName, out var table)) throw new ContentLoadException("Missing exact asset");
            return (T)(object)table;
        }
        public override string PreprocessString(string text)
        {
            Preprocessed.Add(text);
            return Prepare ? "Prepared " + text : text;
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
