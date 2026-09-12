using System.Reflection;
using Hatifect.ChestsAnywhereOverlay.UI.Semantic;
using Hatifect.UI.Experience;
using Xunit;
using Request = Hatifect.ChestsAnywhereOverlay.UI.Semantic.ChestsAnywhereNavigatorExperienceSession.NavigatorActionRequest;
using Receipt = Hatifect.ChestsAnywhereOverlay.UI.Semantic.ChestsAnywhereNavigatorExperienceSession.NavigatorActionReceipt;

namespace Hatifect.ChestsAnywhereOverlay.Tests;

public sealed partial class ChestsAnywhereOverlayExperienceTests
{
    // Inspect the actual exact-package binding without importing Runtime or emulating its dispatcher.
    // These tests own consumer capture/effect/publication; host lifecycle and queues have Runtime tests.
    private sealed record ActualBinding(Func<Request> Capture,
        Func<Request, CancellationToken, ValueTask<UiActionResult<Receipt>>> Execute,
        Func<UiActionAvailability> Availability, UiPublication Owner, UiActionConcurrency Concurrency);

    private static ActualBinding Binding(UiActionDefinition definition)
    {
        object description = typeof(UiActionDefinition).GetProperty("Binding", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(definition) ?? throw new InvalidOperationException("Expected an actual typed package binding.");
        Type type = description.GetType();
        var action = (UiAction<Request, Receipt>)type.GetProperty("Action")!.GetValue(description)!;
        var execute = (Func<Request, CancellationToken, ValueTask<UiActionResult<Receipt>>>)action.GetType()
            .GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<Request, CancellationToken, ValueTask<UiActionResult<Receipt>>>), action);
        var available = (Func<UiActionAvailability>)action.GetType()
            .GetMethod("ReadAvailability", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<UiActionAvailability>), action);
        Assert.Null(type.GetProperty("Completed")!.GetValue(description));
        return new((Func<Request>)type.GetProperty("Capture")!.GetValue(description)!, execute, available,
            (UiPublication)type.GetProperty("Owner")!.GetValue(description)!, action.Concurrency);
    }

    private static UiActionResult<Receipt> Invoke(UiActionDefinition definition)
    {
        ActualBinding binding = Binding(definition);
        Assert.True(binding.Availability().CanExecute);
        ValueTask<UiActionResult<Receipt>> pending = binding.Execute(binding.Capture(), CancellationToken.None);
        return Completed(pending);
    }

    private static UiActionResult<Receipt> Completed(ValueTask<UiActionResult<Receipt>> result)
    {
        Assert.True(result.IsCompletedSuccessfully);
        return result.Result;
    }

    [Theory]
    [InlineData("open")]
    [InlineData("toggle-favorite")]
    public async Task CapturedStorageRequestRejectsChangedPublicationBeforeAnyEffect(string actionName)
    {
        var port = new RecordingPort(TwoCategorySnapshot());
        using var session = new ChestsAnywhereNavigatorExperienceSession(Id("captured"), port);
        ActualBinding binding = Binding(Action(session, actionName));
        Request request = binding.Capture();
        Assert.Equal("farm-a", request.Storage!.Key);
        session.SelectCategory("mine");
        long accepted = session.Publication.Version;
        var result = await binding.Execute(request, CancellationToken.None);
        Assert.Equal(UiActionOutcome.Rejected, result.Outcome);
        Assert.Equal("CA_STALE", result.Rejection!.Code);
        Assert.Equal(accepted, session.Publication.Version);
        Assert.Equal("mine", session.SelectedCategory.Value);
        Assert.Empty(port.OpenRequests);
        Assert.Empty(port.FavoriteRequests);
        Assert.Equal("farm-a", request.Storage.Key);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("toggle-favorite")]
    public async Task TypedStorageEffectReturnsCapturedIdentityAndCommittedReceipt(string actionName)
    {
        var port = new RecordingPort(TwoCategorySnapshot());
        using var session = new ChestsAnywhereNavigatorExperienceSession(Id("receipt"), port);
        ActualBinding binding = Binding(Action(session, actionName));
        Request request = binding.Capture();
        Assert.Same(session.Publication, binding.Owner);
        Assert.Same(UiActionConcurrency.RejectWhileRunning, binding.Concurrency);
        var result = await binding.Execute(request, CancellationToken.None);
        Assert.Equal(UiActionOutcome.Success, result.Outcome);
        Assert.Equal(request.Storage!.Id, result.Value.StorageId);
        Assert.Equal("farm-a", result.Value.StorageKey);
        Assert.Equal(session.Publication.Version, result.Value.PublicationVersion);
        Assert.Equal(session.Publication.Id, result.Value.PublicationId);
        Assert.Equal(port.CaptureValue.Revision, result.Value.ProviderRevision);
        Assert.True(result.Value.PublicationVersion > request.Context.Version);
        Assert.Equal("farm-a", Assert.Single(actionName == "open" ? port.OpenRequests : port.FavoriteRequests));
        Assert.False(result.Value.Closed);
    }

    [Fact]
    public void TypedRejectionPublishesCompleteProviderStateAndSafeLocalizedReason()
    {
        var port = new RecordingPort(TwoCategorySnapshot()) { RejectOpen = true };
        using var session = new ChestsAnywhereNavigatorExperienceSession(Id("rejected"), port);
        var result = Invoke(Action(session, "open"));
        Assert.Equal(UiActionOutcome.Rejected, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal("CA_OPEN_REJECTED", result.Rejection!.Code);
        Assert.Equal("Не удалось открыть это хранилище.", result.Rejection.LocalizedMessage!.Text.Resolve("ru-RU"));
        Assert.Equal("Switch failed", session.Status.Value.Message);
        Assert.Equal(UiStatusKind.Error, session.Status.Value.Kind);
        Assert.Null(session.Handoff.Value);
        Assert.Single(port.OpenRequests);
    }

    [Fact]
    public void TypedProviderFailureKeepsAcceptedProjectionAndSeparatesDiagnosticText()
    {
        var port = new RecordingPort(TwoCategorySnapshot()) { ThrowOnRefresh = true };
        using var session = new ChestsAnywhereNavigatorExperienceSession(Id("failure"), port);
        long version = session.Publication.Version;
        UiStatus status = session.Status.Value;
        var result = Invoke(Action(session, "refresh"));
        Assert.Equal(UiActionOutcome.Failure, result.Outcome);
        Assert.Equal("refresh failed", Assert.IsType<InvalidOperationException>(result.Error).Message);
        Assert.Equal("The navigator request failed.", result.FailureMessage!.Text.Resolve("en"));
        Assert.Equal(version, session.Publication.Version);
        Assert.Equal(status, session.Status.Value);
        Assert.Equal(1, port.RefreshRequests);
    }

    [Fact]
    public void TypedProviderAndPublicationObserversCannotReenterAnotherAction()
    {
        var port = new RecordingPort(TwoCategorySnapshot());
        using var session = new ChestsAnywhereNavigatorExperienceSession(Id("typed-reentry"), port);
        ActualBinding favorite = Binding(Action(session, "toggle-favorite"));
        Request captured = favorite.Capture();
        var outcomes = new List<UiActionResult<Receipt>>();
        port.OnOpen = () => outcomes.Add(Completed(favorite.Execute(captured, CancellationToken.None)));
        session.Handoff.Changed += () => outcomes.Add(Completed(favorite.Execute(captured, CancellationToken.None)));
        Assert.Equal(UiActionOutcome.Success, Invoke(Action(session, "open")).Outcome);
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome =>
        {
            Assert.Equal(UiActionOutcome.Rejected, outcome.Outcome);
            Assert.Equal("CA_BUSY", outcome.Rejection!.Code);
        });
        Assert.Empty(port.FavoriteRequests);
        Assert.Single(port.OpenRequests);
        Assert.True(favorite.Availability().CanExecute);
        Assert.Empty(session.Publication.LastResult.ObserverErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedCapturedRequestCannotReachProviderAfterRetirementOrCancellation(bool cancel)
    {
        var port = new RecordingPort(TwoCategorySnapshot());
        using var session = new ChestsAnywhereNavigatorExperienceSession(Id("retired-request"), port);
        ActualBinding binding = Binding(Action(session, "open"));
        Request request = binding.Capture();
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel(); else session.Dispose();
        var result = await binding.Execute(request, cancellation.Token);
        Assert.Equal(UiActionOutcome.Cancelled, result.Outcome);
        Assert.Equal(cancel ? UiActionCancellationReason.Requested : UiActionCancellationReason.OwnerRetired, result.Cancellation);
        Assert.Empty(port.OpenRequests);
        if (!cancel) Assert.False(binding.Availability().CanExecute);
    }

    [Fact]
    public void PublicationChangedInsideProviderKeepsNewStateAndDoesNotApplyLateSnapshot()
    {
        var port = new RecordingPort(TwoCategorySnapshot());
        using var session = new ChestsAnywhereNavigatorExperienceSession(Id("provider-version"), port);
        long accepted = 0;
        port.OnOpen = () =>
        {
            Assert.True(session.Publication.BeginUpdate()
                .Set((UiPublishedState<string>)session.Status, "New external status").Commit().Succeeded);
            accepted = session.Publication.Version;
        };
        var result = Invoke(Action(session, "open"));
        Assert.Equal(UiActionOutcome.Failure, result.Outcome);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Single(port.OpenRequests);
        Assert.Equal("Opening", port.CaptureValue.StatusText);
        Assert.Equal("New external status", session.Status.Value.Message);
        Assert.Equal(accepted, session.Publication.Version);
        Assert.Null(session.Handoff.Value);
    }

    [Fact]
    public void RetirementInsideProviderPreservesCommittedEffectWithoutPublishingLateResult()
    {
        var port = new RecordingPort(TwoCategorySnapshot());
        using var session = new ChestsAnywhereNavigatorExperienceSession(Id("during-provider"), port);
        port.OnOpen = session.Dispose;
        long version = session.Publication.Version;
        var result = Invoke(Action(session, "open"));
        Assert.Single(port.OpenRequests);
        Assert.Equal(UiActionOutcome.Failure, result.Outcome);
        Assert.IsType<ObjectDisposedException>(result.Error);
        Assert.True(session.Publication.IsDisposed);
        Assert.Equal(version, session.Publication.Version);
        Assert.Null(session.Handoff.Value);
    }
}
