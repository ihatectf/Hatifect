using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Actions;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class ActionMessageTests
{
    [Fact]
    public void AlternatingCachedEquivalentAvailabilityMessagesDoesNotAllocatePerRefresh()
    {
        var first = UiActionAvailability.Disabled(UiActionRejection.Localized(Message("Нет места.")));
        var second = UiActionAvailability.Disabled(UiActionRejection.Localized(Message("Нет места.")));
        bool alternate = false;
        using var owner = new UiActionDispatcher(Guid.NewGuid(), Guid.NewGuid());
        var action = new UiAction<int, int>(Id("action"), "Action",
            (value, _) => new(UiActionResult<int>.Success(value)), UiActionConcurrency.RejectWhileRunning,
            () => (alternate = !alternate) ? first : second);
        var execution = owner.Bind(action);
        for (int i = 0; i < 32; i++) execution.RefreshAvailability();

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool changed = false;
        for (int i = 0; i < 1024; i++) changed |= execution.RefreshAvailability();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(changed);
        Assert.Equal(UiActionState.Disabled, execution.State);
        Assert.InRange(allocated, 0, 64);
    }

    [Fact]
    public void LocalizedRejectionRetainsImmutableExactLocaleTextAndField()
    {
        var translations = new Dictionary<string, string> { ["ru-RU"] = "Выберите маршрут." };
        var message = new UiActionMessage("route.required", new("Choose a route.", translations), Id("route"));
        var rejection = UiActionRejection.Localized(message);
        translations["ru-RU"] = "changed";

        Assert.Equal("route.required", rejection.Code);
        Assert.Equal(Id("route"), rejection.Field);
        Assert.Equal("Choose a route.", rejection.Message);
        Assert.Same(message, rejection.LocalizedMessage);
        Assert.Equal("Выберите маршрут.", rejection.LocalizedMessage!.Text.Resolve("ru-RU"));
        Assert.Equal("Choose a route.", rejection.LocalizedMessage.Text.Resolve("ru"));
        Assert.Equal("Choose a route.", rejection.LocalizedMessage.Text.Resolve("RU-ru"));
        Assert.Equal("Choose a route.", rejection.LocalizedMessage.Text.Resolve(""));
    }

    [Fact]
    public void DomainFailureIsTerminalFailureWithoutDiagnosticExceptionOrSuccessfulValue()
    {
        var message = Message("Недостаточно места.");
        var result = UiActionResult<int>.DomainFailure(message);

        Assert.Equal(UiActionOutcome.Failure, result.Outcome);
        Assert.Same(message, result.FailureMessage);
        Assert.Null(result.Error);
        Assert.Null(result.Rejection);
        Assert.Null(result.Cancellation);
        Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Equal("Недостаточно места.", result.FailureMessage!.Text.Resolve("ru-RU"));
    }

    [Fact]
    public void DiagnosticFailureKeepsExceptionSeparateAndLegacyNullCallUnambiguous()
    {
        var error = new InvalidOperationException("private diagnostic path");
        var message = Message("Недостаточно места.");
        var result = UiActionResult<int>.Failure(error, message);
        var legacy = UiActionResult<int>.Failure(error);

        Assert.Equal(UiActionOutcome.Failure, result.Outcome);
        Assert.Same(error, result.Error);
        Assert.Same(message, result.FailureMessage);
        Assert.Equal("Not enough space.", result.FailureMessage!.Text.Fallback);
        Assert.Same(error, legacy.Error);
        Assert.Null(legacy.FailureMessage);
        Assert.Throws<ArgumentNullException>(() => UiActionResult<int>.Failure(null!));
        Assert.Throws<ArgumentNullException>(() => UiActionResult<int>.Failure(null!, message));
        Assert.Throws<ArgumentNullException>(() => UiActionResult<int>.Failure(error, null!));
        Assert.Throws<ArgumentNullException>(() => UiActionResult<int>.DomainFailure(null!));
    }

    [Fact]
    public void MessageContentComparisonIncludesTranslationsAndIgnoresInsertionOrder()
    {
        var first = new UiActionMessage("space", new("No space", new Dictionary<string, string>
            { ["ru"] = "Нет места", ["en"] = "No space" }), Id("cargo"));
        var equal = new UiActionMessage("space", new("No space", new Dictionary<string, string>
            { ["en"] = "No space", ["ru"] = "Нет места" }), Id("cargo"));
        var changed = new UiActionMessage("space", new("No space", new Dictionary<string, string>
            { ["en"] = "No space", ["ru"] = "Место закончилось" }), Id("cargo"));

        Assert.True(first.HasSameContent(equal));
        Assert.False(first.HasSameContent(changed));
        Assert.False(first.HasSameContent(new("space", first.Text, Id("route"))));
        Assert.False(first.HasSameContent(new("other", first.Text, Id("cargo"))));
        Assert.False(first.HasSameContent(null));
    }

    [Fact]
    public void MessageRejectsMissingCodeTextOrInvalidFieldBeforePublication()
    {
        var text = Message("Нет места").Text;
        Assert.Throws<ArgumentException>(() => new UiActionMessage(" ", text));
        Assert.Throws<ArgumentNullException>(() => new UiActionMessage("space", null!));
        Assert.Throws<ArgumentException>(() => new UiActionMessage("space", text, default(UiSymbolId)));
        Assert.Throws<ArgumentNullException>(() => UiActionRejection.Localized(null!));
        var legacy = new UiActionRejection("space", "No space", Id("cargo"));
        Assert.Null(legacy.LocalizedMessage);
        Assert.Equal("No space", legacy.Message);
    }

    private static UiActionMessage Message(string russian)
        => new("space", new("Not enough space.", new Dictionary<string, string> { ["ru-RU"] = russian }));
    private static UiSymbolId Id(string name) => new("Tests", name);
}
