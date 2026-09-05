using System;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.UI.Planning.Tests;

public sealed class FormAuthoringTests
{
    [Fact]
    public void FailedDetachCanBeRetriedWhileDisposedFormStopsNotifying()
    {
        var source = new CountingSource { FailRemove = true };
        var id = new UiSymbolId("Test", "form");
        using var form = new UiFormState(new UiSemanticFormField(id.Child("name"), "Name", source));
        int changes = 0;
        form.Changed += () => changes++;
        Assert.Throws<AggregateException>(form.Dispose);
        Assert.Equal(1, source.Subscribers);
        source.Notify();
        Assert.Equal(0, changes);
        source.FailRemove = false;
        form.Dispose();
        Assert.Equal(0, source.Subscribers);
    }

    [Fact]
    public void LocalizedNamesKeepExplicitIdentityAndDuplicateIdsAreRejectedBeforeMutation()
    {
        var id = new UiSymbolId("Test", "localized");
        var field = id.Child("station");
        var source = new UiState<string>("");
        var builder = new UiExperienceBuilder(id, "Сеть Flowline").Inspect(field, "Станция отправления", source);
        Assert.Throws<InvalidOperationException>(() => builder.Monitor(field, "Станция назначения", source));
        var experience = builder.Build();
        Assert.Equal(field, Assert.Single(experience.Elements).Id);
        Assert.Equal("Станция отправления", experience.Elements[0].Name);
    }

    [Fact]
    public void PublicForm_CopiesFieldsForwardsValidationAndDetachesBorrowedSources()
    {
        var id = new UiSymbolId("Test", "form");
        var value = new UiState<string>("Orchard");
        var error = new UiState<string?>(null);
        var fields = new[] { new UiSemanticFormField(id.Child("name"), "Name", value, error) };
        using var form = new UiFormState(fields);
        fields[0] = new UiSemanticFormField(id.Child("other"), "Other", value);
        Assert.Equal("Name", Assert.Single(form.Fields).Label);
        Assert.NotNull(new UiExperienceBuilder(id, "Station").Configure("Details", form).Build());
        int changes = 0;
        form.Changed += () => changes++;
        error.Value = "Name already exists";
        Assert.False(form.IsValid);
        Assert.Equal(1, changes);
        error.Value = " ";
        Assert.True(form.IsValid);
        form.Dispose();
        form.Dispose();
        value.Value = "Farm";
        error.Value = "Unavailable";
        Assert.Equal(2, changes);
        Assert.Equal("Farm", value.Value);
    }

    [Fact]
    public void InvalidConstructorLeavesNoSubscribersAndSharedSourceNotifiesOnce()
    {
        var source = new CountingSource();
        var id = new UiSymbolId("Test", "form");
        var field = new UiSemanticFormField(id.Child("name"), "Name", source);
        Assert.Throws<ArgumentException>(() => new UiFormState(field, field));
        Assert.Equal(0, source.Subscribers);
        using (var form = new UiFormState(field, new UiSemanticFormField(id.Child("alias"), "Alias", source)))
        {
            Assert.Equal(1, source.Subscribers);
            int changes = 0;
            form.Changed += () => changes++;
            source.Notify();
            Assert.Equal(1, changes);
        }
        Assert.Equal(0, source.Subscribers);
    }

    private sealed class CountingSource : IUiMutableSemanticSource<string>
    {
        private Action? _changed;
        internal int Subscribers { get; private set; }
        internal bool FailRemove { get; set; }
        public string Value { get; set; } = "";
        public Type ValueType => typeof(string);
        public object UntypedValue => Value;
        public event Action? Changed
        { add { Subscribers++; _changed += value; } remove { if (FailRemove) throw new InvalidOperationException("detach"); Subscribers--; _changed -= value; } }
        internal void Notify() => _changed?.Invoke();
    }
}
