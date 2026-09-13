using Hatifect.ChestsAnywhereOverlay.Integration;
using Xunit;

namespace Hatifect.ChestsAnywhereOverlay.Tests;

public sealed class ChestsAnywhereSelectorSynchronizationPolicyTests
{
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    public void SynchronizesOnlyOnOwnershipOrStructuralTransitions(
        bool nativeShapeMayHaveChanged,
        bool hideNativeSelectors,
        bool hasSuppressionLease,
        bool expected)
    {
        bool actual = ChestsAnywhereSelectorSynchronizationPolicy.ShouldSynchronize(
            nativeShapeMayHaveChanged,
            hideNativeSelectors,
            hasSuppressionLease);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StableVisibleSession_DoesNotReinitializeSelectorAccessOnEveryTick()
    {
        bool hasSuppressionLease = false;
        int synchronizations = 0;

        for (int tick = 0; tick < 600; tick++)
        {
            if (!ChestsAnywhereSelectorSynchronizationPolicy.ShouldSynchronize(
                    nativeShapeMayHaveChanged: false,
                    hideNativeSelectors: true,
                    hasSuppressionLease))
            {
                continue;
            }

            synchronizations++;
            hasSuppressionLease = true;
        }

        Assert.Equal(1, synchronizations);
    }

    [Fact]
    public void PendingNativeSelection_IsConsumedAsOneReacquisitionEdge()
    {
        bool resynchronizationPending = true;
        int synchronizations = 0;

        for (int tick = 0; tick < 120; tick++)
        {
            bool synchronize = ChestsAnywhereSelectorSynchronizationPolicy.ShouldSynchronize(
                nativeShapeMayHaveChanged: resynchronizationPending,
                hideNativeSelectors: true,
                hasSuppressionLease: true);
            if (!synchronize) continue;
            synchronizations++;
            resynchronizationPending = false;
        }

        Assert.Equal(1, synchronizations);
    }
}
