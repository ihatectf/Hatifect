using System.Reflection;
using Hatifect.ChestsAnywhereOverlay.Integration;
using Hatifect.ChestsAnywhereOverlay.Models;
using Hatifect.ChestsAnywhereOverlay.Presentation;
using Xunit;

namespace Hatifect.ChestsAnywhereOverlay.Tests;

public sealed class ChestsAnywhereOverlayCapturePolicyTests
{
    [Fact]
    public void FailedCapture_RetiresVisibleFrontendAndEndsSessionImmediately()
    {
        var adapter = new RejectingAdapter();
        bool frontendVisible = true;
        bool sessionOwned = true;

        bool captured = ChestsAnywhereOverlayCapturePolicy.TryCaptureOrRetire(
            adapter,
            () =>
            {
                frontendVisible = false;
                sessionOwned = false;
                adapter.EndSession();
            },
            out StorageSnapshot snapshot);

        Assert.False(captured);
        Assert.Null(snapshot);
        Assert.False(frontendVisible);
        Assert.False(sessionOwned);
        Assert.Equal(1, adapter.EndSessionCalls);
    }

    [Fact]
    public void SuccessfulCapture_ReturnsExactSnapshotWithoutRetiringSession()
    {
        var expected = new StorageSnapshot(
            new object(),
            Array.Empty<string>(),
            Array.Empty<StorageEntry>(),
            string.Empty);
        var adapter = new SuccessfulAdapter(expected);
        int retireCalls = 0;

        bool captured = ChestsAnywhereOverlayCapturePolicy.TryCaptureOrRetire(
            adapter,
            () => retireCalls++,
            out StorageSnapshot snapshot);

        Assert.True(captured);
        Assert.Same(expected, snapshot);
        Assert.Equal(0, retireCalls);
        Assert.Equal(0, adapter.EndSessionCalls);
    }

    [Fact]
    public void FailedCaptureThroughController_RetiresFrontendAndNativeSessionSynchronously()
    {
        var adapter = new RejectingAdapter();
        var frontend = new RecordingFrontend();
        ChestsAnywhereOverlayController controller = ChestsAnywhereOverlayController.CreateForLifecycleTest(
            adapter,
            () => new ChestsAnywhereOverlayConfig(),
            (key, _) => key);
        controller.AttachFrontend(frontend);

        controller.UpdateActiveOverlay();

        Assert.False(frontend.Visible);
        Assert.Equal(1, frontend.HideCalls);
        Assert.Equal(1, adapter.RestoreNativeSelectorsCalls);
        Assert.Equal(1, adapter.RestoreNativeToggleCalls);
        Assert.Equal(1, adapter.EndSessionCalls);
    }

    [Fact]
    public void FrontendClosedAfterNativeMenuReplacement_CancelsPendingRenderedCloseBeforeReopen()
    {
        var adapter = new RejectingAdapter();
        var frontend = new RecordingFrontend();
        ChestsAnywhereOverlayController controller = ChestsAnywhereOverlayController.CreateForLifecycleTest(
            adapter,
            () => new ChestsAnywhereOverlayConfig(),
            (key, _) => key);
        controller.AttachFrontend(frontend);
        SetPrivateField(controller, "_pendingSelectedKey", "farm");
        SetPrivateField(controller, "_selectionWaitFrames", 37);
        SetPrivateField(controller, "_closeAfterRenderedFrames", 2);

        frontend.RaiseClosed();

        Assert.Null(GetPrivateField(controller, "_pendingSelectedKey"));
        Assert.Equal(0, GetPrivateField(controller, "_selectionWaitFrames"));
        Assert.Equal(0, GetPrivateField(controller, "_closeAfterRenderedFrames"));

        frontend.Show();
        int hideCallsBeforeRender = frontend.HideCalls;
        frontend.RaiseRendered();
        frontend.RaiseRendered();

        Assert.True(frontend.Visible);
        Assert.Equal(hideCallsBeforeRender, frontend.HideCalls);
    }

    [Fact]
    public void FirstNativeToggleEdgeOpensExactlyOneFrontendThroughControllerLifecycle()
    {
        var overlay = new object();
        var adapter = new SuccessfulAdapter(new StorageSnapshot(
            overlay,
            new[] { "Farm" },
            new[] { new StorageEntry("farm/chest", "Chest", "Farm", "Farm", 1, new object()) },
            "farm/chest"))
        {
            NativeToggleJustPressed = true
        };
        var frontend = new RecordingFrontend(visible: false);
        ChestsAnywhereOverlayController controller = ChestsAnywhereOverlayController.CreateForLifecycleTest(
            adapter,
            () => new ChestsAnywhereOverlayConfig(),
            (key, _) => key);
        controller.AttachFrontend(frontend);

        controller.UpdateActiveOverlay();
        controller.UpdateActiveOverlay();

        Assert.True(frontend.Visible);
        Assert.Equal(1, frontend.ShowCalls);
        Assert.Equal(2, adapter.ToggleReads);
        Assert.False(adapter.NativeToggleJustPressed);
    }

    [Fact]
    public void Shutdown_RetiresVisibleFrontendAndRestoresNativeOwnershipSynchronously()
    {
        var overlay = new object();
        var adapter = new SuccessfulAdapter(new StorageSnapshot(
            overlay,
            new[] { "Farm" },
            new[] { new StorageEntry("farm/chest", "Chest", "Farm", "Farm", 1, new object()) },
            "farm/chest"))
        {
            NativeToggleJustPressed = true
        };
        var frontend = new RecordingFrontend(visible: false);
        int saveCalls = 0;
        bool visibleDuringSave = false;
        ChestsAnywhereOverlayController controller = ChestsAnywhereOverlayController.CreateForLifecycleTest(
            adapter,
            () => new ChestsAnywhereOverlayConfig(),
            (key, _) => key,
            saveStateOnShutdown: () =>
            {
                saveCalls++;
                visibleDuringSave = frontend.Visible;
            });
        controller.AttachFrontend(frontend);
        controller.UpdateActiveOverlay();
        Assert.True(frontend.Visible);
        int selectorRestoresBeforeShutdown = adapter.RestoreNativeSelectorsCalls;
        int toggleRestoresBeforeShutdown = adapter.RestoreNativeToggleCalls;

        controller.Shutdown();

        Assert.False(frontend.Visible);
        Assert.False(controller.HasActiveSessionForAcceptance);
        Assert.Equal(1, saveCalls);
        Assert.True(visibleDuringSave);
        Assert.Equal(1, frontend.HideCalls);
        Assert.Equal(selectorRestoresBeforeShutdown + 1, adapter.RestoreNativeSelectorsCalls);
        Assert.Equal(toggleRestoresBeforeShutdown + 1, adapter.RestoreNativeToggleCalls);
        Assert.Equal(1, adapter.EndSessionCalls);
    }

    private static object? GetPrivateField(ChestsAnywhereOverlayController controller, string name)
        => typeof(ChestsAnywhereOverlayController)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(controller);

    private static void SetPrivateField(
        ChestsAnywhereOverlayController controller,
        string name,
        object? value)
        => typeof(ChestsAnywhereOverlayController)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller, value);

    private sealed class RejectingAdapter : IChestsAnywhereOverlayAdapter
    {
        internal int EndSessionCalls { get; private set; }
        internal int RestoreNativeSelectorsCalls { get; private set; }
        internal int RestoreNativeToggleCalls { get; private set; }
        public bool IsSupported => true;
        public bool IsOverlayActive => true;
        public bool IsOverlayModal => false;
        public bool HasSuppressedNativeSelectors => true;
        public bool HasMutedNativeToggle => true;
        public bool IsNativeToggleJustPressed(object overlay) => false;
        public bool TryCapture(out StorageSnapshot snapshot)
        {
            snapshot = null!;
            return false;
        }
        public bool SuppressNativeSelectors(object overlay, bool hide) => true;
        public bool RestoreNativeSelectors()
        {
            RestoreNativeSelectorsCalls++;
            return true;
        }
        public bool MuteNativeToggle(object overlay) => true;
        public bool RestoreNativeToggle()
        {
            RestoreNativeToggleCalls++;
            return true;
        }
        public void EndSession() => EndSessionCalls++;
        public bool SelectStorage(StorageSnapshot snapshot, StorageEntry entry) => false;
    }

    private sealed class SuccessfulAdapter : IChestsAnywhereOverlayAdapter
    {
        private readonly StorageSnapshot _snapshot;

        internal SuccessfulAdapter(StorageSnapshot snapshot) => _snapshot = snapshot;

        internal int EndSessionCalls { get; private set; }
        internal int ToggleReads { get; private set; }
        internal int RestoreNativeSelectorsCalls { get; private set; }
        internal int RestoreNativeToggleCalls { get; private set; }
        internal bool NativeToggleJustPressed { get; set; }
        public bool IsSupported => true;
        public bool IsOverlayActive => true;
        public bool IsOverlayModal => false;
        public bool HasSuppressedNativeSelectors => false;
        public bool HasMutedNativeToggle => false;
        public bool IsNativeToggleJustPressed(object overlay)
        {
            ToggleReads++;
            bool pressed = NativeToggleJustPressed;
            NativeToggleJustPressed = false;
            return pressed;
        }
        public bool TryCapture(out StorageSnapshot snapshot)
        {
            snapshot = _snapshot;
            return true;
        }
        public bool SuppressNativeSelectors(object overlay, bool hide) => true;
        public bool RestoreNativeSelectors()
        {
            RestoreNativeSelectorsCalls++;
            return true;
        }
        public bool MuteNativeToggle(object overlay) => true;
        public bool RestoreNativeToggle()
        {
            RestoreNativeToggleCalls++;
            return true;
        }
        public void EndSession() => EndSessionCalls++;
        public bool SelectStorage(StorageSnapshot snapshot, StorageEntry entry) => false;
    }

    private sealed class RecordingFrontend : IChestsAnywhereOverlayFrontend
    {
        internal RecordingFrontend(bool visible = true) => Visible = visible;

        public bool Visible { get; private set; }
        internal int HideCalls { get; private set; }
        internal int ShowCalls { get; private set; }
        public event Action? Rendered;
        public event Action? Closed;
        public void Refresh(bool preserveViewState) { }
        public void UpdateOptions(ChestsAnywhereOverlayConfig config) { }
        public bool Show()
        {
            ShowCalls++;
            Visible = true;
            return true;
        }
        public void Hide()
        {
            HideCalls++;
            Visible = false;
        }
        internal void RaiseRendered() => Rendered?.Invoke();
        internal void RaiseClosed()
        {
            Visible = false;
            Closed?.Invoke();
        }
        public void Dispose() { }
    }
}
