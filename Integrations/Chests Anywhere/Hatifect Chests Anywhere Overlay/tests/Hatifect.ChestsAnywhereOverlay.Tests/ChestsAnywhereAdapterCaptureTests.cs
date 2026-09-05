using Hatifect.ChestsAnywhereOverlay.Integration;
using Xunit;

namespace Hatifect.ChestsAnywhereOverlay.Tests;

public sealed class ChestsAnywhereAdapterCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryCapture_NullOrThrowingPrivateGetter_DisablesProductionAdapter(bool throws)
    {
        var messages = new List<string>();
        ChestsAnywhereAdapter adapter = ChestsAnywhereAdapter.CreateForCaptureTest(
            throws
                ? () => throw new InvalidOperationException("private getter drift")
                : () => null,
            messages.Add);

        Assert.True(adapter.IsSupported);

        Assert.False(adapter.TryCapture(out var snapshot));

        Assert.Null(snapshot);
        Assert.False(adapter.IsSupported);
        string message = Assert.Single(messages);
        Assert.Contains("disabled its integration", message, StringComparison.Ordinal);
        Assert.Contains("Native selectors and the original toggle were restored", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAcquire_NonNullOverlay_ReturnsExactObjectWithoutDisablingIntegration()
    {
        object expected = new();
        int failures = 0;

        bool acquired = ChestsAnywhereCaptureBoundary.TryAcquire(
            () => expected,
            _ => failures++,
            out object overlay);

        Assert.True(acquired);
        Assert.Same(expected, overlay);
        Assert.Equal(0, failures);
    }

    [Fact]
    public void NearCompatibleRawApi_IsRejectedByProductionBindingBeforeMutation()
    {
        var api = new NearCompatibleApi();
        var messages = new List<string>();

        ChestsAnywhereAdapter adapter = ChestsAnywhereAdapter.CreateForIncompatibleApiTest(
            api,
            messages.Add);

        Assert.False(adapter.IsSupported);
        Assert.False(adapter.HasSuppressedNativeSelectors);
        Assert.False(adapter.HasMutedNativeToggle);
        Assert.Equal(0, api.MethodCalls);
        Assert.Contains("private surface was not found", Assert.Single(messages), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryAcquire_NullOrThrowingGetter_InvokesExactRestorationCallback(bool throws)
        => AssertCaptureFailureRestoresExactNativeState(
            throws
                ? () => throw new InvalidOperationException("private getter drift")
                : () => null);

    private static void AssertCaptureFailureRestoresExactNativeState(Func<object?> getOverlay)
    {
        object originalSelector = new();
        object originalToggle = new();
        var originalBounds = new Bounds(11, 22, 33, 44);
        object? selector = null;
        object? toggle = null;
        Bounds bounds = new(-10000, -10000, 1, 1);
        bool disabled = false;
        int failures = 0;

        bool acquired = ChestsAnywhereCaptureBoundary.TryAcquire(
            getOverlay,
            _ =>
            {
                failures++;
                disabled = true;
                selector = originalSelector;
                toggle = originalToggle;
                bounds = originalBounds;
            },
            out object overlay);

        Assert.False(acquired);
        Assert.Null(overlay);
        Assert.True(disabled);
        Assert.Equal(1, failures);
        Assert.Same(originalSelector, selector);
        Assert.Same(originalToggle, toggle);
        Assert.Equal(originalBounds, bounds);
    }

    private readonly record struct Bounds(int X, int Y, int Width, int Height);

    private sealed class NearCompatibleApi
    {
        private readonly object GetOverlay = new();
        internal int MethodCalls { get; private set; }
        private bool IsOverlayActive()
        {
            MethodCalls++;
            return false;
        }
        private bool IsOverlayModal()
        {
            MethodCalls++;
            return false;
        }
    }
}
