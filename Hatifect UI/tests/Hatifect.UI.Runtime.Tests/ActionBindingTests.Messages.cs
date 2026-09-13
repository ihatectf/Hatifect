using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Actions;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed partial class ActionBindingTests
{
    [Fact]
    public void LegacyPreparationRetiringEarlierTypedOwnerCannotPublishStaleEnabledAction()
    {
        using var publication = new UiPublication(Id("publication"));
        var typed = Action((value, _) => new(UiActionResult<int>.Success(value))).Bind(() => 1, owner: publication);
        bool armed = false;
        int reads = 0;
        var legacy = new UiActionDefinition(Id("legacy"), "Legacy", () => { }, () =>
        {
            if (armed && ++reads == 2) publication.Dispose();
            return true;
        });
        var scene = Scene(typed, legacy);
        armed = true;
        var host = Host(scene);
        try
        {
            Assert.True(publication.IsDisposed);
            Assert.False(host.Actions.CanInvoke(typed));
            Assert.False(FindAccessibility(host, typed).Enabled);
            Assert.False(FindAccessibility(host, typed).Focused);
            Assert.NotEqual(typed.Id.Child("scene/button"), host.Interactions.Snapshot.Focused);
            Assert.Equal("Waiting cancelled.", FindAccessibility(host, typed).Value);
            Assert.Contains(host.Frame.Primitives.OfType<UiTextPrimitive>(), p => p.Text == "Waiting cancelled.");
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void RetirementFromTextMetricsRejectsCandidateMessageAndNextPumpAcceptsCancellation()
    {
        using var publication = new UiPublication(Id("publication"));
        var pending = Completion();
        var action = Action((_, _) => new(pending.Task)).Bind(() => 7, owner: publication);
        var platform = new Platform();
        var scene = SearchScene(action);
        var host = new Hatifect.UI.Runtime.Platform.UiHostRuntimeSession(scene, Viewport, platform,
            new Hatifect.UI.Runtime.Input.UiInteractionSnapshot(
                Focused: Assert.Single(Nodes(scene.Root).OfType<UiTextInputSceneNode>()).Id));
        try
        {
            Assert.True(host.Actions.Invoke(action));
            var frame = host.Frame;
            var accessibility = host.Accessibility;
            platform.OnMeasure = () => publication.Dispose();
            Assert.Throws<InvalidOperationException>(() => host.PumpActions());
            Assert.Same(frame, host.Frame);
            Assert.Same(accessibility, host.Accessibility);
            Assert.False(host.Actions.CanInvoke(action));
            platform.OnMeasure = null;
            Assert.True(host.PumpActions());
            Assert.False(FindAccessibility(host, action).Enabled);
            Assert.Equal("Waiting cancelled.", FindAccessibility(host, action).Value);
            Assert.Contains(host.Frame.Primitives.OfType<UiTextPrimitive>(), p => p.Text == "Waiting cancelled.");
        }
        finally { host.Deactivate(); pending.TrySetResult(UiActionResult<int>.Success(7)); }
    }

    [Theory]
    [InlineData(UiActionOutcome.Success, "Completed.")]
    [InlineData(UiActionOutcome.Rejected, "Choose a route.")]
    [InlineData(UiActionOutcome.Failure, "Not enough space.")]
    [InlineData(UiActionOutcome.Cancelled, "Waiting cancelled.")]
    public void TypedTerminalMessageIsVisibleAndAccessibleWithoutChangingLayout(UiActionOutcome outcome, string expected)
    {
        var message = new UiActionMessage("space", new("Not enough space.", new Dictionary<string, string>()));
        var result = outcome switch
        {
            UiActionOutcome.Success => UiActionResult<int>.Success(9283),
            UiActionOutcome.Rejected => UiActionResult<int>.Rejected(new("route", "Choose a route.")),
            UiActionOutcome.Failure => UiActionResult<int>.DomainFailure(message),
            _ => UiActionResult<int>.Cancelled()
        };
        var action = Action((_, _) => new(result)).Bind(() => 7);
        var host = Host(Scene(action));
        try
        {
            var layout = host.Layout;
            var focused = host.Interactions.Snapshot.Focused;
            Assert.Null(FindAccessibility(host, action).Value);
            Assert.True(host.Interactions.Submit().ActionInvoked);
            host.PumpActions();
            var messageRun = Assert.Single(host.Frame.Primitives.OfType<UiTextPrimitive>(), p => p.Text == expected);
            var label = Assert.Single(host.Frame.Primitives.OfType<UiTextPrimitive>(), p => p.Text == "Action");
            Assert.True(messageRun.Bounds.Height > 0);
            Assert.True(messageRun.Bounds.Width >= expected.Length * messageRun.Typography.Size * 0.6f,
                "A single action with room available should show its ordinary status text in full.");
            Assert.True(messageRun.Bounds.Y >= label.Bounds.Bottom);
            Assert.Equal(label.Node, messageRun.Node);
            Assert.Equal(Hatifect.UI.Runtime.Layout.UiTextOverflow.Ellipsis, messageRun.Overflow);
            Assert.Equal(expected, FindAccessibility(host, action).Value);
            Assert.Equal("Action", FindAccessibility(host, action).Name);
            Assert.Same(layout, host.Layout);
            Assert.Equal(focused, host.Interactions.Snapshot.Focused);
            Assert.DoesNotContain(host.Frame.Primitives.OfType<UiTextPrimitive>(), p => p.Text.Contains("9283"));
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void WorkerCompletionKeepsAcceptedRunningMessageUntilPumpAndHidesDiagnosticException()
    {
        var pending = Completion();
        var action = Action((_, _) => new(pending.Task)).Bind(() => 7);
        var host = Host(Scene(action));
        try
        {
            Assert.True(host.Interactions.Submit().ActionInvoked);
            host.PumpActions();
            Assert.Equal("Running…", FindAccessibility(host, action).Value);
            var running = host.Frame;
            OnWorker(() => pending.SetResult(UiActionResult<int>.Failure(new InvalidOperationException("private-path-secret"))));
            host.Render();
            Assert.Same(running, host.Frame);
            Assert.Equal("Running…", FindAccessibility(host, action).Value);
            PumpUntil(host, () => host.Actions.Status(action)?.State == UiActionState.Failed);
            Assert.Equal("The action could not be completed.", FindAccessibility(host, action).Value);
            Assert.Contains(host.Frame.Primitives.OfType<UiTextPrimitive>(), p => p.Text == "The action could not be completed.");
            Assert.DoesNotContain(host.Frame.Primitives.OfType<UiTextPrimitive>(), p => p.Text.Contains("private-path-secret"));
            Assert.Equal("private-path-secret", host.Actions.Status(action)!.Error!.Message);
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void RetainedDomainFailureResolvesAgainForAcceptedLocaleWithoutExecutingAgain()
    {
        int effects = 0;
        var message = new UiActionMessage("space", new("Not enough space.",
            new Dictionary<string, string> { ["ru-RU"] = "Недостаточно места." }));
        var action = Action((_, _) => { effects++; return new(UiActionResult<int>.DomainFailure(message)); }).Bind(() => 7);
        var scene = Scene(action);
        var host = Host(scene);
        try
        {
            Assert.True(host.Interactions.Submit().ActionInvoked);
            host.PumpActions();
            Assert.Equal("Not enough space.", FindAccessibility(host, action).Value);
            host.Update(new(scene.Experience, scene.DisplayName, scene.Root,
                scene.MeasurementContext with { Locale = "ru-RU" }), Viewport);
            Assert.Equal("Недостаточно места.", FindAccessibility(host, action).Value);
            Assert.Contains(host.Frame.Primitives.OfType<UiTextPrimitive>(), p => p.Text == "Недостаточно места.");
            Assert.Equal(1, effects);
            Assert.Null(host.Actions.Status(action)!.Error);
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void RecreatedEqualAvailabilityMessagesReuseFrameAndTranslationChangeReplacesIt()
    {
        string russian = "Выберите маршрут.";
        var action = Action((request, _) => new(UiActionResult<int>.Success(request)), availability: () =>
            UiActionAvailability.Disabled(UiActionRejection.Localized(new("route", new("Choose a route.",
                new Dictionary<string, string> { ["ru-RU"] = russian }))))).Bind(() => 7);
        var scene = Scene(action);
        var host = Host(new(scene.Experience, scene.DisplayName, scene.Root,
            scene.MeasurementContext with { Locale = "ru-RU" }));
        try
        {
            var frame = host.Frame;
            Assert.False(host.PumpActions());
            Assert.Same(frame, host.Frame);
            Assert.Equal("Выберите маршрут.", FindAccessibility(host, action).Value);
            russian = "Сначала выберите маршрут.";
            Assert.True(host.PumpActions());
            Assert.NotSame(frame, host.Frame);
            Assert.Equal(russian, FindAccessibility(host, action).Value);
            Assert.Contains(host.Frame.Primitives.OfType<UiTextPrimitive>(), p => p.Text == russian);
            Assert.False(FindAccessibility(host, action).Enabled);
        }
        finally { host.Deactivate(); }
    }
}
