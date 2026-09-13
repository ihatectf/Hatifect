using System;
using System.Reflection;
using StardewModdingAPI;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.UI.Stardew.Tests;

public sealed class SurfaceObservationAdmissionTests
{
    [Fact]
    public void ProductionApiRejectsObservationBeforeTouchingTheHandleOrNativeHelper()
    {
        var helper = DispatchProxy.Create<IModHelper, RejectNativeAccess>();
        IUiSemanticSurfaceObservationApi api = new UiStardewApiBridge(helper);
        Assert.False(api.Observation.IsEnabled);
        var error = Assert.Throws<InvalidOperationException>(() => api.Observation.Capture(new RejectSessionAccess()));
        Assert.Contains("exact automated TestHarness", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionApiRejectsActionInputBeforeTouchingTheHandleOrNativeHelper()
    {
        var helper = DispatchProxy.Create<IModHelper, RejectNativeAccess>();
        IUiSemanticSurfaceActionAutomationApi api = new UiStardewApiBridge(helper);
        Assert.False(api.ActionAutomation.IsEnabled);
        var error = Assert.Throws<InvalidOperationException>(() =>
            api.ActionAutomation.Activate(new RejectSessionAccess(), new("test", "action/run")));
        Assert.Contains("exact automated TestHarness", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionInputRejectsNullHandleBeforeNativeAccess()
    {
        var helper = DispatchProxy.Create<IModHelper, RejectNativeAccess>();
        IUiSemanticSurfaceActionAutomationApi api = new UiStardewApiBridge(helper);
        var error = Assert.Throws<ArgumentNullException>(() =>
            api.ActionAutomation.Activate(null!, new("test", "action/run")));
        Assert.Equal("session", error.ParamName);
    }

    [Fact]
    public void ActionInputRejectsInvalidIdentityBeforeNativeAccess()
    {
        var helper = DispatchProxy.Create<IModHelper, RejectNativeAccess>();
        IUiSemanticSurfaceActionAutomationApi api = new UiStardewApiBridge(helper);
        var error = Assert.Throws<ArgumentException>(() =>
            api.ActionAutomation.Activate(new RejectSessionAccess(), default));
        Assert.Equal("action", error.ParamName);
    }

    [Fact]
    public void ProductionApiRejectsRevealBeforeTouchingTheHandleOrNativeHelper()
    {
        var helper = DispatchProxy.Create<IModHelper, RejectNativeAccess>();
        IUiSemanticSurfaceRevealAutomationApi api = new UiStardewApiBridge(helper);
        Assert.False(api.RevealAutomation.IsEnabled);

        var error = Assert.Throws<InvalidOperationException>(() =>
            api.RevealAutomation.Reveal(new RejectSessionAccess(), new("test", "element/result")));

        Assert.Contains("exact automated TestHarness", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RevealRejectsNullHandleBeforeNativeAccess()
    {
        var helper = DispatchProxy.Create<IModHelper, RejectNativeAccess>();
        IUiSemanticSurfaceRevealAutomationApi api = new UiStardewApiBridge(helper);

        var error = Assert.Throws<ArgumentNullException>(() =>
            api.RevealAutomation.Reveal(null!, new("test", "element/result")));

        Assert.Equal("session", error.ParamName);
    }

    [Fact]
    public void RevealRejectsInvalidSemanticIdentityBeforeNativeAccess()
    {
        var helper = DispatchProxy.Create<IModHelper, RejectNativeAccess>();
        IUiSemanticSurfaceRevealAutomationApi api = new UiStardewApiBridge(helper);

        var error = Assert.Throws<ArgumentException>(() =>
            api.RevealAutomation.Reveal(new RejectSessionAccess(), default));

        Assert.Equal("semantic", error.ParamName);
    }

    public class RejectNativeAccess : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new Exception("Disabled observation touched the native helper.");
    }

    private sealed class RejectSessionAccess : IUiSemanticSurfaceSession
    {
        public bool Visible => throw new Exception("Disabled observation touched a handle.");
        public event Action? Rendered { add => throw new Exception(); remove => throw new Exception(); }
        public event Action? Closed { add => throw new Exception(); remove => throw new Exception(); }
        public void Show() => throw new Exception();
        public void Hide() => throw new Exception();
        public void Configure(UiSemanticSurfaceOptions options) => throw new Exception();
        public void Refresh() => throw new Exception();
        public void Synchronize() => throw new Exception();
        public void Dispose() => throw new Exception();
    }
}
