using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class SemanticFormTests
{
    [Fact]
    public void InvalidDraftKeepsAllCommittedValuesAndResetRestoresTheLastCommit()
    {
        var name = UiFormFields.Text(Id("name"), "Name", "Before", value => value.Length > 0 ? null : "Required");
        var count = UiFormFields.Number(Id("count"), "Count", 1m, value => value >= 0 ? null : "Must be positive");
        using var form = new UiFormState(name, count);
        name.DraftValue = "After";
        count.Value.Value = "invalid";

        Assert.False(form.Apply());
        Assert.Equal("Before", name.CommittedValue);
        Assert.Equal(1m, count.CommittedValue);
        Assert.NotNull(count.Error);
        count.DraftValue = -1m;
        Assert.False(form.Apply());
        Assert.Equal("Must be positive", count.Error);
        Assert.Equal("Before", name.CommittedValue);
        Assert.Equal(1m, count.CommittedValue);
        count.DraftValue = 2.5m;
        int notifications = 0;
        form.Changed += () => { notifications++; Assert.Equal("After", name.CommittedValue); Assert.Equal(2.5m, count.CommittedValue); };
        Assert.True(form.Apply());
        Assert.Equal(1, notifications);
        name.Value.Value = "Discard";
        count.Value.Value = "-99";
        form.Reset();
        Assert.Equal("After", name.DraftValue);
        Assert.Equal(2.5m, count.DraftValue);
        Assert.Null(count.Error);
    }

    [Fact]
    public void ChoiceSnapshotsOptionsAndRejectsUnknownValues()
    {
        string[] options = { "one", "two" };
        var choice = UiFormFields.Choice(Id("choice"), "Mode", "one", options, value => value);
        options[0] = "changed";
        using var form = new UiFormState(choice);
        choice.DraftValue = "two";
        Assert.True(form.Apply());
        Assert.Equal("two", choice.CommittedValue);
        choice.Value.Value = "999";
        Assert.False(form.Apply());
        Assert.Equal("two", choice.CommittedValue);
        Assert.Equal("one", choice.Options[0].Label);
        Assert.Throws<ArgumentException>(() => choice.DraftValue = "missing");
    }

    [Fact]
    public void FailedConstructionDoesNotSubscribeAndDisposeDetachesSharedSourcesOnce()
    {
        var source = new CountingSource();
        var first = new UiSemanticFormField(Id("first"), "First", source);
        Assert.Throws<ArgumentException>(() => new UiFormState(first, first));
        Assert.Equal(0, source.Subscribers);
        var form = new UiFormState(first, new UiSemanticFormField(Id("second"), "Second", source));
        Assert.Equal(1, source.Subscribers);
        int changes = 0;
        form.Changed += () => changes++;
        source.Value = "live";
        Assert.Equal(1, changes);
        form.Dispose();
        form.Dispose();
        source.Value = "detached";
        Assert.Equal(0, source.Subscribers);
        Assert.Equal(1, changes);
        Assert.Throws<ObjectDisposedException>(() => form.Apply());
    }

    [Fact]
    public void TypedFieldsUseRuntimeInputAndValidationErrorsAppearInTheScene()
    {
        var number = UiFormFields.Number(Id("number"), "Quantity", 5m);
        var toggle = UiFormFields.Toggle(Id("toggle"), "Enabled", false);
        using var form = new UiFormState(number, toggle);
        var experience = new UiExperienceBuilder(Id("form"), "Settings").Configure("Fields", form)
            .Actions("Commands", new UiActionDefinition(Id("apply"), "Apply", () => form.Apply())).Build();
        var registry = UiSemanticHostProjection.Register(experience, UiSemanticHostKind.Window);
        var invocation = new UiInvocationService(registry).Invoke(experience.Id, UiPresentationProfiles.Wide);
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        var host = new UiPortalHostSession(composer.Compose(invocation),
            new UiHostPlacementContext(new UiRect(0, 0, 1280, 800)), new TestPlatform(),
            composeInteraction: state => composer.Compose(invocation, interaction: state));
        void Focus(UiSymbolId node)
        {
            for (int index = 0; index < 10; index++) host.MoveFocus(UiNavigationDirection.Previous);
            for (int index = 0; index < 10 && host.Root.Interactions.Snapshot.Focused != node; index++)
                host.MoveFocus(UiNavigationDirection.Next);
            Assert.Equal(node, host.Root.Interactions.Snapshot.Focused);
        }
        Focus(number.Id.Child("scene/input"));
        Assert.True(host.ReplaceText("broken").Interaction?.TextChanged);
        Focus(toggle.Id.Child("scene/input/0"));
        Assert.True(host.Submit().Interaction?.ActionInvoked);
        Assert.True(toggle.DraftValue);
        Assert.False(toggle.CommittedValue);
        Focus(Id("apply").Child("scene/button"));
        host.Submit();

        Assert.Equal(5m, number.CommittedValue);
        Assert.False(toggle.CommittedValue);
        Assert.Contains(Nodes(host.Root.Scene.Root).OfType<UiTextSceneNode>(), node => node.Text == "Quantity: Enter a valid value.");
        Assert.NotEmpty(host.Root.Frame.Primitives);
    }

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var nested in Nodes(child)) yield return nested;
    }
    private static UiSymbolId Id(string name) => new("Form.Tests", name);
    private sealed class TestPlatform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * 8, availableWidth), 20);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
    private sealed class CountingSource : IUiMutableSemanticSource<string>
    {
        private string _value = "";
        private Action? _changed;
        public int Subscribers { get; private set; }
        public string Value { get => _value; set { _value = value; _changed?.Invoke(); } }
        public Type ValueType => typeof(string);
        public object UntypedValue => Value;
        public event Action? Changed { add { _changed += value; Subscribers++; } remove { _changed -= value; Subscribers--; } }
    }
}
