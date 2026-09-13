using Hatifect.ChestsAnywhereOverlay.Integration;
using Xunit;

namespace Hatifect.ChestsAnywhereOverlay.Tests;

public sealed class ChestsAnywhereAutomatedOverlayLeaseTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RetirementObservationRejectsEitherRetainedNativeIdentity(bool retainsOverlay, bool retainsMenu)
    {
        object overlay = new();
        object menu = new();
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(overlay, menu);

        Assert.False(lease.TryReleaseAfterNativeRetirement(
            () => retainsOverlay ? overlay : null,
            () => retainsMenu ? menu : new object(),
            out string diagnostic));
        Assert.True(lease.IsCommitted);
        Assert.NotEmpty(diagnostic);
    }

    [Fact]
    public void RetirementObservationReleasesOnlyBookkeepingWhenTitleOwnsANewMenu()
    {
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(new object(), new object());
        object titleMenu = new();

        Assert.True(lease.TryReleaseAfterNativeRetirement(() => null, () => titleMenu, out string diagnostic));
        Assert.False(lease.IsActive);
        Assert.Empty(diagnostic);
    }

    [Fact]
    public void HandoffPolicy_RecognizesNativeMenuBeforeOverlaySynchronization()
    {
        object originalOverlay = new();
        object originalMenu = new();
        object replacementMenu = new();

        ChestsAnywhereAutomatedHandoffState state = ChestsAnywhereAutomatedHandoffPolicy.Classify(
            originalOverlay,
            originalMenu,
            originalOverlay,
            replacementMenu,
            originalMenu);

        Assert.Equal(ChestsAnywhereAutomatedHandoffState.AwaitingOverlaySynchronization, state);
    }

    [Fact]
    public void HandoffPolicy_RecognizesFinalReplacementPair()
    {
        object originalOverlay = new();
        object originalMenu = new();
        object replacementOverlay = new();
        object replacementMenu = new();

        ChestsAnywhereAutomatedHandoffState state = ChestsAnywhereAutomatedHandoffPolicy.Classify(
            originalOverlay,
            originalMenu,
            replacementOverlay,
            replacementMenu,
            replacementMenu);

        Assert.Equal(ChestsAnywhereAutomatedHandoffState.Synchronized, state);
    }

    [Fact]
    public void HandoffPolicy_RejectsUnchangedPairWithoutNativeHandoff()
    {
        object originalOverlay = new();
        object originalMenu = new();

        ChestsAnywhereAutomatedHandoffState state = ChestsAnywhereAutomatedHandoffPolicy.Classify(
            originalOverlay,
            originalMenu,
            originalOverlay,
            originalMenu,
            originalMenu);

        Assert.Equal(ChestsAnywhereAutomatedHandoffState.Invalid, state);
    }

    [Fact]
    public void HandoffPolicy_RejectsOverlayBoundToUnrelatedMenu()
    {
        object originalOverlay = new();
        object originalMenu = new();
        object replacementOverlay = new();
        object replacementMenu = new();

        ChestsAnywhereAutomatedHandoffState state = ChestsAnywhereAutomatedHandoffPolicy.Classify(
            originalOverlay,
            originalMenu,
            replacementOverlay,
            replacementMenu,
            new object());

        Assert.Equal(ChestsAnywhereAutomatedHandoffState.Invalid, state);
    }

    [Fact]
    public void HandoffPolicy_RejectsReplacementOverlayBoundToOriginalMenu()
    {
        object originalOverlay = new();
        object originalMenu = new();

        ChestsAnywhereAutomatedHandoffState state = ChestsAnywhereAutomatedHandoffPolicy.Classify(
            originalOverlay,
            originalMenu,
            currentOverlay: new object(),
            currentMenu: originalMenu,
            currentOverlayMenu: originalMenu);

        Assert.Equal(ChestsAnywhereAutomatedHandoffState.Invalid, state);
    }

    [Fact]
    public void HandoffPolicy_RejectsOriginalOverlayReboundToReplacementMenu()
    {
        object originalOverlay = new();
        object originalMenu = new();
        object replacementMenu = new();

        ChestsAnywhereAutomatedHandoffState state = ChestsAnywhereAutomatedHandoffPolicy.Classify(
            originalOverlay,
            originalMenu,
            currentOverlay: originalOverlay,
            currentMenu: replacementMenu,
            currentOverlayMenu: replacementMenu);

        Assert.Equal(ChestsAnywhereAutomatedHandoffState.Invalid, state);
    }

    [Fact]
    public void HandoffPolicy_RejectsRetiredOverlayOrMenu()
    {
        object originalOverlay = new();
        object originalMenu = new();

        Assert.Equal(
            ChestsAnywhereAutomatedHandoffState.Invalid,
            ChestsAnywhereAutomatedHandoffPolicy.Classify(
                originalOverlay,
                originalMenu,
                currentOverlay: null,
                currentMenu: new object(),
                currentOverlayMenu: new object()));
        Assert.Equal(
            ChestsAnywhereAutomatedHandoffState.Invalid,
            ChestsAnywhereAutomatedHandoffPolicy.Classify(
                originalOverlay,
                originalMenu,
                currentOverlay: new object(),
                currentMenu: null,
                currentOverlayMenu: new object()));
    }

    [Fact]
    public void HandoffRecoveryPolicy_AcceptsOnlyOwnedOrCausalStates()
    {
        object originalOverlay = new();
        object originalMenu = new();
        object replacementOverlay = new();
        object replacementMenu = new();

        Assert.True(ChestsAnywhereAutomatedHandoffPolicy.CanRecoverForRollback(
            originalOverlay,
            originalMenu,
            originalOverlay,
            originalMenu,
            originalMenu));
        Assert.True(ChestsAnywhereAutomatedHandoffPolicy.CanRecoverForRollback(
            originalOverlay,
            originalMenu,
            originalOverlay,
            replacementMenu,
            originalMenu));
        Assert.True(ChestsAnywhereAutomatedHandoffPolicy.CanRecoverForRollback(
            originalOverlay,
            originalMenu,
            replacementOverlay,
            replacementMenu,
            replacementMenu));
        Assert.True(ChestsAnywhereAutomatedHandoffPolicy.CanRecoverForRollback(
            originalOverlay,
            originalMenu,
            currentOverlay: null,
            replacementMenu,
            currentOverlayMenu: null));
        Assert.False(ChestsAnywhereAutomatedHandoffPolicy.CanRecoverForRollback(
            originalOverlay,
            originalMenu,
            replacementOverlay,
            replacementMenu,
            currentOverlayMenu: new object()));
        Assert.False(ChestsAnywhereAutomatedHandoffPolicy.CanRecoverForRollback(
            originalOverlay,
            originalMenu,
            replacementOverlay,
            currentMenu: null,
            currentOverlayMenu: replacementMenu));
    }

    [Fact]
    public void OverlayBoundaryFailureRecovery_DemotesToMenuOnlyAndClosesMenuBeforeReadingOverlay()
    {
        object originalOverlay = new();
        object originalMenu = new();
        object? replacementOverlay = new object();
        object? replacementMenu = new object();
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(originalOverlay, originalMenu);
        var operations = new List<string>();

        Assert.True(lease.TryRecoverMenuAfterMutation(
            originalOverlay,
            originalMenu,
            mutationAttempted: true,
            replacementMenu));
        Assert.False(lease.IsCommitted);

        bool closed = lease.TryClose(
            () =>
            {
                operations.Add("read-overlay");
                return replacementOverlay;
            },
            () =>
            {
                operations.Add("read-menu");
                return replacementMenu;
            },
            ownedMenu =>
            {
                operations.Add("close-menu");
                Assert.Same(replacementMenu, ownedMenu);
                replacementMenu = null;
            },
            () =>
            {
                operations.Add("synchronize-overlay");
                replacementOverlay = null;
            },
            out string diagnostic);

        Assert.True(closed, diagnostic);
        Assert.Equal(
            new[] { "read-menu", "close-menu", "synchronize-overlay", "read-overlay", "read-menu" },
            operations);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void OverlayBoundaryFailureRecovery_RejectsMissingMutationMenuOrOwnedPair()
    {
        object originalOverlay = new();
        object originalMenu = new();
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(originalOverlay, originalMenu);

        Assert.False(lease.TryRecoverMenuAfterMutation(
            originalOverlay,
            originalMenu,
            mutationAttempted: false,
            nextMenu: new object()));
        Assert.False(lease.TryRecoverMenuAfterMutation(
            originalOverlay,
            originalMenu,
            mutationAttempted: true,
            nextMenu: null));
        Assert.False(lease.TryRecoverMenuAfterMutation(
            new object(),
            originalMenu,
            mutationAttempted: true,
            nextMenu: new object()));
        Assert.True(lease.Matches(originalOverlay, originalMenu));
    }

    [Fact]
    public void InvalidPairRecovery_DemotesCausalMenuInsteadOfLeasingUnrelatedOverlayBinding()
    {
        object originalOverlay = new();
        object originalMenu = new();
        object? replacementOverlay = new object();
        object? replacementMenu = new object();
        object unrelatedBoundMenu = new();
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(originalOverlay, originalMenu);
        int closes = 0;

        Assert.False(ChestsAnywhereAutomatedHandoffPolicy.CanRecoverForRollback(
            originalOverlay,
            originalMenu,
            replacementOverlay,
            replacementMenu,
            unrelatedBoundMenu));
        Assert.True(lease.TryRecoverMenuAfterMutation(
            originalOverlay,
            originalMenu,
            mutationAttempted: true,
            replacementMenu));

        bool closed = lease.TryClose(
            () => replacementOverlay,
            () => replacementMenu,
            ownedMenu =>
            {
                Assert.Same(replacementMenu, ownedMenu);
                Assert.NotSame(unrelatedBoundMenu, ownedMenu);
                closes++;
                replacementMenu = null;
            },
            () => replacementOverlay = null,
            out string diagnostic);

        Assert.True(closed, diagnostic);
        Assert.Equal(1, closes);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void ExactOwnedPair_ClosesAndClearsEvenAfterIntegrationStateChanges()
    {
        object? overlay = new object();
        object? menu = new object();
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(overlay, menu);
        int closes = 0;

        bool closed = lease.TryClose(
            () => overlay,
            () => menu,
            ownedMenu =>
            {
                Assert.Same(menu, ownedMenu);
                closes++;
                menu = null;
            },
            () => overlay = null,
            out string diagnostic);

        Assert.True(closed, diagnostic);
        Assert.Equal(1, closes);
        Assert.False(lease.IsActive);
        Assert.True(lease.TryClose(() => overlay, () => menu, _ => closes++, () => { }, out diagnostic), diagnostic);
        Assert.Equal(1, closes);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MismatchedIdentity_NeverClosesAndRetainsLease(bool replaceOverlay, bool replaceMenu)
    {
        object ownedOverlay = new();
        object ownedMenu = new();
        object? currentOverlay = replaceOverlay ? new object() : ownedOverlay;
        object? currentMenu = replaceMenu ? new object() : ownedMenu;
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(ownedOverlay, ownedMenu);
        int closes = 0;

        bool closed = lease.TryClose(
            () => currentOverlay,
            () => currentMenu,
            _ => closes++,
            () => { },
            out string diagnostic);

        Assert.False(closed);
        Assert.Contains("no longer matches", diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, closes);
        Assert.True(lease.IsActive);
    }

    [Fact]
    public void FailedClosePostcondition_RetainsLeaseForSafeRetry()
    {
        object overlay = new();
        object menu = new();
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(overlay, menu);

        bool closed = lease.TryClose(
            () => overlay,
            () => menu,
            _ => { },
            () => { },
            out string diagnostic);

        Assert.False(closed);
        Assert.Contains("did not retire", diagnostic, StringComparison.Ordinal);
        Assert.True(lease.IsActive);
        Assert.True(lease.Matches(overlay, menu));
    }

    [Fact]
    public void HandoffAdvancesOnlyAfterMutationFromExpectedOwnedPair()
    {
        object originalOverlay = new();
        object originalMenu = new();
        object nextOverlay = new();
        object nextMenu = new();
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(originalOverlay, originalMenu);

        Assert.False(lease.TryAdvanceAfterMutation(
            originalOverlay,
            originalMenu,
            mutationAttempted: false,
            nextOverlay,
            nextMenu));
        Assert.False(lease.TryAdvanceAfterMutation(
            new object(),
            originalMenu,
            mutationAttempted: true,
            nextOverlay,
            nextMenu));
        Assert.True(lease.Matches(originalOverlay, originalMenu));
        Assert.True(lease.TryAdvanceAfterMutation(
            originalOverlay,
            originalMenu,
            mutationAttempted: true,
            nextOverlay,
            nextMenu));
        Assert.True(lease.Matches(nextOverlay, nextMenu));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HandoffMutation_PreservesRetiredMemberIdentityForCleanup(
        bool overlayRetired,
        bool menuRetired)
    {
        object originalOverlay = new();
        object originalMenu = new();
        object? currentOverlay = overlayRetired ? null : new object();
        object? currentMenu = menuRetired ? null : new object();
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(originalOverlay, originalMenu);

        Assert.True(lease.TryAdvanceAfterMutation(
            originalOverlay,
            originalMenu,
            mutationAttempted: true,
            currentOverlay,
            currentMenu));
        Assert.True(lease.TryClose(
            () => currentOverlay,
            () => currentMenu,
            _ => currentMenu = null,
            () => currentOverlay = null,
            out string diagnostic), diagnostic);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void HandoffMutationThatRetiresBothMembers_ClearsLeaseImmediately()
    {
        object originalOverlay = new();
        object originalMenu = new();
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(originalOverlay, originalMenu);

        Assert.True(lease.TryAdvanceAfterMutation(
            originalOverlay,
            originalMenu,
            mutationAttempted: true,
            nextOverlay: null,
            nextMenu: null));
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void PartialCloseAfterSynchronizationException_CanRetryExactRemainingOverlay()
    {
        object? overlay = new object();
        object? menu = new object();
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(overlay, menu);
        int closes = 0;
        int synchronizations = 0;

        Assert.Throws<InvalidOperationException>(() => lease.TryClose(
            () => overlay,
            () => menu,
            _ =>
            {
                closes++;
                menu = null;
            },
            () =>
            {
                synchronizations++;
                throw new InvalidOperationException("native synchronization failed");
            },
            out _));

        Assert.True(lease.IsActive);
        Assert.True(lease.TryClose(
            () => overlay,
            () => menu,
            _ => closes++,
            () =>
            {
                synchronizations++;
                overlay = null;
            },
            out string diagnostic), diagnostic);
        Assert.Equal(1, closes);
        Assert.Equal(2, synchronizations);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void ProvisionalMenuOnly_RollsBackWithoutACommittedOverlay()
    {
        object? overlay = null;
        object? menu = new object();
        object provisionalMenu = menu;
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.AcquireProvisionalMenu(provisionalMenu);
        int closes = 0;

        bool rolledBack = lease.TryClose(
            () => overlay,
            () => menu,
            _ =>
            {
                closes++;
                menu = null;
            },
            () => overlay = null,
            out string diagnostic);

        Assert.True(rolledBack, diagnostic);
        Assert.Equal(1, closes);
        Assert.Null(overlay);
        Assert.Null(menu);
    }

    [Fact]
    public void FailedPreflight_NeverAcquiresOrClosesTheUnrelatedCurrentMenu()
    {
        object unrelatedMenu = new();
        var lease = new ChestsAnywhereAutomatedOverlayLease();

        Assert.False(lease.TryAcquireProvisionalMenuAfterOpenAttempt(
            nativeOpenAttempted: false,
            unrelatedMenu));
        Assert.False(lease.IsActive);
        Assert.False(lease.TryClose(
            () => null,
            () => unrelatedMenu,
            _ => throw new InvalidOperationException("An unrelated menu must never be closed."),
            () => throw new InvalidOperationException("An unrelated overlay must never be synchronized."),
            out string diagnostic));
        Assert.Contains("No Hatifect automation lease", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeOpenAttempt_AcquiresOnlyTheFirstProvisionalMenu()
    {
        object ownedMenu = new();
        var lease = new ChestsAnywhereAutomatedOverlayLease();

        Assert.True(lease.TryAcquireProvisionalMenuAfterOpenAttempt(
            nativeOpenAttempted: true,
            ownedMenu));
        Assert.False(lease.TryAcquireProvisionalMenuAfterOpenAttempt(
            nativeOpenAttempted: true,
            new object()));
        Assert.True(lease.IsActive);
        Assert.False(lease.IsCommitted);
    }

    [Fact]
    public void FailedRepeatedOpen_DoesNotAuthorizeRollbackOfTheExistingLease()
    {
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.Acquire(new object(), new object());

        Assert.False(lease.ShouldRollbackFailedOpen(nativeOpenAttempted: false));
        Assert.True(lease.ShouldRollbackFailedOpen(nativeOpenAttempted: true));
        Assert.True(lease.IsActive);
        Assert.True(lease.IsCommitted);
    }

    [Fact]
    public void ProvisionalMenu_ClosesBeforeReadingAnUnattachableOverlay()
    {
        object? overlay = new object();
        object? menu = new object();
        object provisionalMenu = menu;
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.AcquireProvisionalMenu(provisionalMenu);
        int closes = 0;
        int overlayReads = 0;

        bool rolledBack = lease.TryClose(
            () =>
            {
                overlayReads++;
                if (menu != null)
                    throw new InvalidOperationException("Overlay reflection ran before exact-menu cleanup.");
                return overlay;
            },
            () => menu,
            ownedMenu =>
            {
                Assert.Same(provisionalMenu, ownedMenu);
                closes++;
                menu = null;
            },
            () => overlay = null,
            out string diagnostic);

        Assert.True(rolledBack, diagnostic);
        Assert.Equal(1, closes);
        Assert.Equal(1, overlayReads);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void ProvisionalLease_NeverClosesAnUnrelatedMenu()
    {
        object ownedMenu = new();
        object? currentMenu = new object();
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.AcquireProvisionalMenu(ownedMenu);
        int closes = 0;

        bool rolledBack = lease.TryClose(
            () => null,
            () => currentMenu,
            _ => closes++,
            () => { },
            out string diagnostic);

        Assert.False(rolledBack);
        Assert.Contains("no longer matches", diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, closes);
        Assert.NotNull(currentMenu);
        Assert.True(lease.IsActive);
        Assert.False(lease.IsCommitted);
    }

    [Fact]
    public void ProvisionalSynchronizationException_CanRetryWithoutReclosingTheRetiredMenu()
    {
        object ownedMenu = new();
        object? currentMenu = ownedMenu;
        object? currentOverlay = new object();
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.AcquireProvisionalMenu(ownedMenu);
        int closes = 0;
        int synchronizations = 0;

        Assert.Throws<InvalidOperationException>(() => lease.TryClose(
            () => currentOverlay,
            () => currentMenu,
            _ =>
            {
                closes++;
                currentMenu = null;
            },
            () =>
            {
                synchronizations++;
                throw new InvalidOperationException("overlay synchronization failed");
            },
            out _));

        Assert.True(lease.IsActive);
        Assert.False(lease.IsCommitted);
        Assert.True(lease.TryClose(
            () => currentOverlay,
            () => currentMenu,
            _ => throw new InvalidOperationException("The exact menu was already retired."),
            () =>
            {
                synchronizations++;
                currentOverlay = null;
            },
            out string diagnostic), diagnostic);
        Assert.Equal(1, closes);
        Assert.Equal(2, synchronizations);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void ProvisionalSynchronizationMutationThenException_RetryObservesCompletedCleanup()
    {
        object? currentMenu = new object();
        object? currentOverlay = new object();
        object ownedMenu = currentMenu;
        var lease = new ChestsAnywhereAutomatedOverlayLease();
        lease.AcquireProvisionalMenu(ownedMenu);
        int closes = 0;
        int synchronizations = 0;

        Assert.Throws<InvalidOperationException>(() => lease.TryClose(
            () => currentOverlay,
            () => currentMenu,
            _ =>
            {
                closes++;
                currentMenu = null;
            },
            () =>
            {
                synchronizations++;
                currentOverlay = null;
                throw new InvalidOperationException("synchronization threw after completing cleanup");
            },
            out _));

        Assert.True(lease.IsActive);
        Assert.True(lease.TryClose(
            () => currentOverlay,
            () => currentMenu,
            _ => throw new InvalidOperationException("The exact menu was already retired."),
            () => throw new InvalidOperationException("Completed synchronization must not be retried."),
            out string diagnostic), diagnostic);
        Assert.Equal(1, closes);
        Assert.Equal(1, synchronizations);
        Assert.False(lease.IsActive);
    }
}
