using Hatifect.ChestsAnywhereOverlay.Integration;
using Hatifect.ChestsAnywhereOverlay.Models;
using Hatifect.ChestsAnywhereOverlay.UI.Semantic;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.ChestsAnywhereOverlay.Tests;

public sealed class SemanticChestsAnywhereOverlayFrontendTests
{
    [Fact]
    public void FailedSurfaceDispose_RetainsOwnerUntilRetryWithoutOverlappingSessionOrDuplicateClosed()
    {
        var adapter = new SnapshotAdapter();
        ChestsAnywhereOverlayController controller = ChestsAnywhereOverlayController.CreateForLifecycleTest(
            adapter,
            () => new ChestsAnywhereOverlayConfig(),
            (key, _) => key);
        controller.UpdateActiveOverlay();
        var surfaces = new FakeSurfaceApi();
        var frontend = new SemanticChestsAnywhereOverlayFrontend(controller, surfaces);
        int closedCalls = 0;
        frontend.Closed += () => closedCalls++;

        Assert.True(frontend.Show());
        FakeSurfaceSession first = Assert.IsType<FakeSurfaceSession>(frontend.AcceptanceSurface);

        AggregateException failure = Assert.Throws<AggregateException>(frontend.Hide);

        Assert.Single(failure.InnerExceptions);
        Assert.Same(first, frontend.AcceptanceSurface);
        Assert.False(frontend.Visible);
        Assert.False(first.OwnerReleased);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(0, closedCalls);
        Assert.Equal(1, surfaces.CreateCalls);

        Assert.False(frontend.Show());

        Assert.Null(frontend.AcceptanceSurface);
        Assert.True(first.OwnerReleased);
        Assert.Equal(2, first.DisposeCalls);
        Assert.Equal(1, closedCalls);
        Assert.Equal(1, surfaces.CreateCalls);

        frontend.Hide();
        Assert.Equal(1, closedCalls);

        Assert.True(frontend.Show());
        Assert.Equal(2, surfaces.CreateCalls);
        frontend.Dispose();
    }

    private sealed class SnapshotAdapter : IChestsAnywhereOverlayAdapter
    {
        private readonly StorageSnapshot _snapshot = new(
            new object(),
            new[] { "Farm" },
            new[]
            {
                new StorageEntry("farm", "Farm Chest", "Farm", "Farm", 0, new object())
            },
            "farm");

        public bool IsSupported => true;
        public bool IsOverlayActive => true;
        public bool IsOverlayModal => false;
        public bool HasSuppressedNativeSelectors => false;
        public bool HasMutedNativeToggle => false;
        public bool IsNativeToggleJustPressed(object overlay) => false;
        public bool TryCapture(out StorageSnapshot snapshot)
        {
            snapshot = _snapshot;
            return true;
        }
        public bool SuppressNativeSelectors(object overlay, bool hide) => true;
        public bool RestoreNativeSelectors() => true;
        public bool MuteNativeToggle(object overlay) => true;
        public bool RestoreNativeToggle() => true;
        public void EndSession() { }
        public bool SelectStorage(StorageSnapshot snapshot, StorageEntry entry) => true;
    }

    private sealed class FakeSurfaceApi : IUiSemanticSurfaceApi
    {
        private readonly List<FakeSurfaceSession> _sessions = new();

        public int ApiVersion => 1;
        public IUiSemanticSurfaceAutomation Automation { get; } = new DisabledAutomation();
        internal int CreateCalls { get; private set; }

        public IUiSemanticSurfaceSession CreateActiveMenuOverlay(
            UiExperienceDefinition experience,
            UiSemanticSurfaceOptions options)
        {
            Assert.DoesNotContain(_sessions, session => !session.OwnerReleased);
            CreateCalls++;
            var session = new FakeSurfaceSession(failFirstDispose: CreateCalls == 1);
            _sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeSurfaceSession : IUiSemanticSurfaceSession
    {
        private readonly bool _failFirstDispose;

        internal FakeSurfaceSession(bool failFirstDispose) => _failFirstDispose = failFirstDispose;

        public bool Visible { get; private set; }
        internal bool OwnerReleased { get; private set; }
        internal int DisposeCalls { get; private set; }
        public event Action? Rendered;
        public event Action? Closed;

        public void Show()
        {
            if (OwnerReleased) throw new ObjectDisposedException(nameof(FakeSurfaceSession));
            Visible = true;
        }

        public void Hide() => Visible = false;
        public void Configure(UiSemanticSurfaceOptions options) { }
        public void Refresh() => Rendered?.Invoke();
        public void Synchronize() { }

        public void Dispose()
        {
            DisposeCalls++;
            if (_failFirstDispose && DisposeCalls == 1)
                throw new InvalidOperationException("Injected first surface cleanup failure.");
            OwnerReleased = true;
            Visible = false;
        }

        internal void RaiseClosed()
        {
            Visible = false;
            Closed?.Invoke();
        }
    }

    private sealed class DisabledAutomation : IUiSemanticSurfaceAutomation
    {
        public bool IsEnabled => false;
        public void Register(UiAutomatedAcceptanceScenario scenario) { }
        public void Cancel(IUiSemanticSurfaceSession session, UiSemanticSurfaceCancelInput input) { }
    }
}
