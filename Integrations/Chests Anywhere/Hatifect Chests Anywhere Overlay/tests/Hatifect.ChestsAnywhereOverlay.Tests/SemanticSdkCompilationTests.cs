using System;
using System.Collections.Generic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.ChestsAnywhereOverlay.Tests;

/// <summary>Compiles against exact public packages with no Runtime reference or friend access.</summary>
public sealed class SemanticSdkCompilationTests
{
    [Fact]
    public void ExternalConsumerCanAuthorTypedFormsHostsAndIndexedCollections()
    {
        var field = UiFormFields.Number(new UiSymbolId("External", "count"), "Count", 1m);
        using var form = new UiFormState(field);
        field.DraftValue = 2m;
        Assert.True(form.Apply());
        Assert.Equal(2m, field.CommittedValue);
        var source = new ExternalSource();
        var experience = new UiExperienceBuilder(new UiSymbolId("External", "settings"), "Settings")
            .Configure("Form", form).Browse("Items", source).Build();
        var terminal = new UiSemanticTerminalDefinition(new UiSymbolId("External", "terminal"),
            new[] { new UiSemanticTerminalSection(experience) });
        Assert.Equal(experience.Id, terminal.InitialSection);
        Assert.True(source.TryGetIndex(source.GetItem(0).Id, out int index));
        Assert.Equal(0, index);
        Assert.True(typeof(IUiSemanticSurfaceApi).IsAssignableFrom(typeof(IUiSemanticHostApi)));
        Assert.True(typeof(IUiSemanticSurfaceSession).IsAssignableFrom(typeof(IUiSemanticAppearanceSession)));
    }

    private sealed class ExternalSource : IUiSemanticSource<IReadOnlyList<int>>,
        IUiSemanticCollectionSource, IUiSemanticCollectionMetadata
    {
        public IReadOnlyList<int> Value { get; } = Array.AsReadOnly(new[] { 1 });
        public Type ValueType => typeof(IReadOnlyList<int>);
        public object UntypedValue => Value;
        public int Count => 1;
        public long Revision => 0;
        public bool HasSupportingText => true;
        public event Action? Changed { add { } remove { } }
        public UiSemanticCollectionItem GetItem(int index) => index == 0
            ? new(new UiSymbolId("External", "row"), "Row", 1, "Supporting text")
            : throw new ArgumentOutOfRangeException(nameof(index));
        public bool TryGetIndex(UiSymbolId item, out int index)
        { index = item == new UiSymbolId("External", "row") ? 0 : -1; return index == 0; }
    }
}
