using Hatifect.ChestsAnywhereOverlay.Integration;
using Xunit;

namespace Hatifect.ChestsAnywhereOverlay.Tests;

public sealed class ChestsAnywhereAdapterLeaseTests
{
    [Fact]
    public void SingleCategoryNullSelector_SuppressesAndRestoresExactNativeState()
    {
        object chestSelector = new();
        object editButton = new();
        var originalBounds = new Bounds(11, 22, 33, 44);
        var access = new FakeSelectorAccess(
            new object(), chestSelector, categorySelector: null, categoryCount: 1, editButton, originalBounds);
        var lease = new ChestsAnywhereNativeSelectorLease();

        Assert.True(lease.Suppress(access));
        Assert.True(lease.IsActive);
        Assert.Null(access.ChestSelector);
        Assert.Null(access.CategorySelector);
        Assert.Equal(FakeSelectorAccess.HiddenBounds, access.EditBounds);

        Assert.True(lease.TryRestore(out Exception? error));
        Assert.Null(error);
        Assert.False(lease.IsActive);
        Assert.Same(chestSelector, access.ChestSelector);
        Assert.Null(access.CategorySelector);
        Assert.Equal(originalBounds, access.EditBounds);
    }

    [Fact]
    public void MultipleCategoriesWithoutCategorySelector_FailsClosedBeforeAnyMutation()
    {
        object chestSelector = new();
        var originalBounds = new Bounds(1, 2, 30, 40);
        var access = new FakeSelectorAccess(
            new object(), chestSelector, categorySelector: null, categoryCount: 2, new object(), originalBounds);
        var lease = new ChestsAnywhereNativeSelectorLease();

        Assert.Throws<InvalidOperationException>(() => lease.Suppress(access));

        Assert.Same(chestSelector, access.ChestSelector);
        Assert.Null(access.CategorySelector);
        Assert.Equal(originalBounds, access.EditBounds);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void SingleCategoryWithUnexpectedSelector_FailsClosedBeforeAnyMutation()
    {
        object chestSelector = new();
        object categorySelector = new();
        var originalBounds = new Bounds(4, 5, 33, 43);
        var access = new FakeSelectorAccess(
            new object(), chestSelector, categorySelector, categoryCount: 1, new object(), originalBounds);
        var lease = new ChestsAnywhereNativeSelectorLease();

        Assert.Throws<InvalidOperationException>(() => lease.Suppress(access));

        Assert.Same(chestSelector, access.ChestSelector);
        Assert.Same(categorySelector, access.CategorySelector);
        Assert.Equal(originalBounds, access.EditBounds);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void EmptyCategoryCollection_FailsClosedBeforeAnyMutation()
    {
        object chestSelector = new();
        var originalBounds = new Bounds(6, 7, 34, 44);
        var access = new FakeSelectorAccess(
            new object(), chestSelector, categorySelector: null, categoryCount: 0, new object(), originalBounds);
        var lease = new ChestsAnywhereNativeSelectorLease();

        Assert.Throws<InvalidOperationException>(() => lease.Suppress(access));

        Assert.Same(chestSelector, access.ChestSelector);
        Assert.Equal(originalBounds, access.EditBounds);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void MissingChestSelector_FailsClosedBeforeAnyMutation()
    {
        var originalBounds = new Bounds(2, 3, 31, 41);
        var access = new FakeSelectorAccess(
            new object(), chestSelector: null, categorySelector: null, categoryCount: 1, new object(), originalBounds);
        var lease = new ChestsAnywhereNativeSelectorLease();

        Assert.Throws<InvalidOperationException>(() => lease.Suppress(access));

        Assert.Null(access.ChestSelector);
        Assert.Null(access.CategorySelector);
        Assert.Equal(originalBounds, access.EditBounds);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void MissingEditButton_FailsClosedBeforeSelectorMutation()
    {
        object chestSelector = new();
        object categorySelector = new();
        var originalBounds = new Bounds(3, 4, 32, 42);
        var access = new FakeSelectorAccess(
            new object(), chestSelector, categorySelector, categoryCount: 2, editButton: null, originalBounds);
        var lease = new ChestsAnywhereNativeSelectorLease();

        Assert.Throws<InvalidOperationException>(() => lease.Suppress(access));

        Assert.Same(chestSelector, access.ChestSelector);
        Assert.Same(categorySelector, access.CategorySelector);
        Assert.Equal(originalBounds, access.EditBounds);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void TwoSelectors_RoundTripExactReferencesAndBounds()
    {
        object chestSelector = new();
        object categorySelector = new();
        object editButton = new();
        var originalBounds = new Bounds(5, 6, 70, 80);
        var access = new FakeSelectorAccess(
            new object(), chestSelector, categorySelector, categoryCount: 2, editButton, originalBounds);
        var lease = new ChestsAnywhereNativeSelectorLease();

        Assert.True(lease.Suppress(access));
        Assert.True(lease.TryRestore(out Exception? error));

        Assert.Null(error);
        Assert.Same(chestSelector, access.ChestSelector);
        Assert.Same(categorySelector, access.CategorySelector);
        Assert.Same(editButton, access.EditButton);
        Assert.Equal(originalBounds, access.EditBounds);
    }

    [Fact]
    public void RepeatedSuppression_IsIdempotentForNullableCategoryLease()
    {
        object chestSelector = new();
        var originalBounds = new Bounds(7, 8, 90, 100);
        var access = new FakeSelectorAccess(
            new object(), chestSelector, categorySelector: null, categoryCount: 1, new object(), originalBounds);
        var lease = new ChestsAnywhereNativeSelectorLease();

        Assert.True(lease.Suppress(access));
        Assert.True(lease.Suppress(access));
        Assert.True(lease.TryRestore(out Exception? error));

        Assert.Null(error);
        Assert.Same(chestSelector, access.ChestSelector);
        Assert.Null(access.CategorySelector);
        Assert.Equal(originalBounds, access.EditBounds);
    }

    [Fact]
    public void NativeReinitialization_RestoresNewestSelectorReferences()
    {
        object overlay = new();
        var first = new FakeSelectorAccess(
            overlay, new object(), categorySelector: null, categoryCount: 1, new object(), new Bounds(1, 1, 10, 10));
        var lease = new ChestsAnywhereNativeSelectorLease();
        Assert.True(lease.Suppress(first));

        object newestChest = new();
        object newestCategory = new();
        object newestEdit = new();
        var newestBounds = new Bounds(20, 30, 40, 50);
        var newest = new FakeSelectorAccess(
            overlay, newestChest, newestCategory, categoryCount: 2, newestEdit, newestBounds);

        Assert.True(lease.Suppress(newest));
        Assert.True(lease.TryRestore(out Exception? error));

        Assert.Null(error);
        Assert.Same(newestChest, newest.ChestSelector);
        Assert.Same(newestCategory, newest.CategorySelector);
        Assert.Same(newestEdit, newest.EditButton);
        Assert.Equal(newestBounds, newest.EditBounds);
    }

    [Fact]
    public void PartialSelectorReinitialization_PreservesOriginalEditBoundsAndNewestSelectors()
    {
        object chestSelector = new();
        object editButton = new();
        var originalBounds = new Bounds(21, 31, 41, 51);
        var access = new FakeSelectorAccess(
            new object(), chestSelector, categorySelector: null, categoryCount: 1, editButton, originalBounds);
        var lease = new ChestsAnywhereNativeSelectorLease();
        Assert.True(lease.Suppress(access));

        object newestChest = new();
        object newestCategory = new();
        access.ChestSelector = newestChest;
        access.CategorySelector = newestCategory;
        access.CategoryCount = 2;

        Assert.True(lease.Suppress(access));
        Assert.Equal(FakeSelectorAccess.HiddenBounds, access.EditBounds);
        Assert.True(lease.TryRestore(out Exception? error));

        Assert.Null(error);
        Assert.Same(newestChest, access.ChestSelector);
        Assert.Same(newestCategory, access.CategorySelector);
        Assert.Same(editButton, access.EditButton);
        Assert.Equal(originalBounds, access.EditBounds);
    }

    [Fact]
    public void FailedEditBoundsWrite_IsDetectedAndExactStateCanStillBeRestored()
    {
        object chestSelector = new();
        object editButton = new();
        var originalBounds = new Bounds(9, 10, 110, 120);
        var access = new FakeSelectorAccess(
            new object(), chestSelector, categorySelector: null, categoryCount: 1, editButton, originalBounds)
        {
            IgnoreEditBoundsWrites = true
        };
        var lease = new ChestsAnywhereNativeSelectorLease();

        Assert.Throws<InvalidOperationException>(() => lease.Suppress(access));
        Assert.True(lease.IsActive);
        Assert.Null(access.ChestSelector);
        Assert.Equal(originalBounds, access.EditBounds);

        access.IgnoreEditBoundsWrites = false;
        Assert.True(lease.TryRestore(out Exception? error));
        Assert.Null(error);
        Assert.Same(chestSelector, access.ChestSelector);
        Assert.Equal(originalBounds, access.EditBounds);
    }

    [Fact]
    public void ReplacedEditButton_DoesNotBlockSelectorOrOwnedBoundsRestore()
    {
        object chestSelector = new();
        object originalEditButton = new();
        var originalBounds = new Bounds(12, 13, 130, 140);
        var access = new FakeSelectorAccess(
            new object(),
            chestSelector,
            categorySelector: null,
            categoryCount: 1,
            originalEditButton,
            originalBounds);
        var lease = new ChestsAnywhereNativeSelectorLease();
        Assert.True(lease.Suppress(access));
        object replacementEditButton = new();
        access.EditButton = replacementEditButton;

        Assert.True(lease.TryRestore(out Exception? error));

        Assert.Null(error);
        Assert.False(lease.IsActive);
        Assert.Same(chestSelector, access.ChestSelector);
        Assert.Same(replacementEditButton, access.EditButton);
        Assert.Equal(originalBounds, access.EditBounds);
    }

    private readonly record struct Bounds(int X, int Y, int Width, int Height);

    private sealed class FakeSelectorAccess : IChestsAnywhereNativeSelectorAccess
    {
        internal static readonly Bounds HiddenBounds = new(-10000, -10000, 1, 1);

        internal FakeSelectorAccess(
            object overlay,
            object? chestSelector,
            object? categorySelector,
            int categoryCount,
            object? editButton,
            Bounds editBounds)
        {
            Overlay = overlay;
            ChestSelector = chestSelector;
            CategorySelector = categorySelector;
            CategoryCount = categoryCount;
            EditButton = editButton;
            _editBounds = editBounds;
        }

        public object Overlay { get; }
        public object? ChestSelector { get; set; }
        public object? CategorySelector { get; set; }
        public int CategoryCount { get; set; }
        public object? EditButton { get; set; }
        public bool IgnoreEditBoundsWrites { get; set; }
        public object EditBounds
        {
            get => _editBounds;
            set
            {
                if (!IgnoreEditBoundsWrites)
                    _editBounds = value;
            }
        }
        public object HiddenEditBounds => HiddenBounds;

        private object _editBounds;
    }
}
