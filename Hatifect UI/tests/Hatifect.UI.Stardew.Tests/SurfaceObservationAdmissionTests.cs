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
