using System.Collections.Generic;
using Hatifect.ChestsAnywhereOverlay.Presentation;
using Hatifect.ChestsAnywhereOverlay.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.ChestsAnywhereOverlay.UI.Semantic;

/// <summary>
/// Product composition for the semantic Chests Anywhere Overlay. The controller owns native CA
/// capture and handoff; Hatifect UI owns scene, layout, input, rendering, and platform lifecycle
/// behind <see cref="IUiSemanticSurfaceApi"/>.
/// </summary>
internal sealed class SemanticChestsAnywhereOverlayFrontend : IChestsAnywhereOverlayFrontend
{
    private static readonly UiSymbolId ExperienceId =
        new("Hatifect.ChestsAnywhereOverlay", "navigator/overlay");

    private readonly ChestsAnywhereOverlayController _controller;
    private readonly IUiSemanticSurfaceApi _surfaces;
    private ChestsAnywhereOverlayConfig _options = new();
    private IUiSemanticSurfaceSession? _surface;
    private ChestsAnywhereNavigatorExperienceSession? _experience;
    private bool _refreshRequested;
    private bool _preserveViewState = true;
    private bool _closeRequested;
    private bool _retirementPending;
    private bool _retirementInProgress;
    private bool _surfaceCleanupCompleted;
    private bool _experienceCleanupCompleted;
    private bool _notifyClosedWhenRetired;
    private bool _disposed;

    internal SemanticChestsAnywhereOverlayFrontend(
        ChestsAnywhereOverlayController controller,
        IUiSemanticSurfaceApi surfaces)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
        if (_surfaces.ApiVersion < 1)
        {
            throw new InvalidOperationException(
                $"Hatifect Chests Anywhere Overlay requires semantic surface API v1; found v{_surfaces.ApiVersion}.");
        }
    }

    public bool Visible => !_retirementPending && _surface?.Visible == true;
    public event Action? Rendered;
    public event Action? Closed;

    // Product-private acceptance observes semantic/domain state only. UI platform internals remain
    // inaccessible across the package boundary.
    internal ChestsAnywhereNavigatorExperienceSession? AcceptanceExperience => _experience;
    internal IUiSemanticSurfaceSession? AcceptanceSurface => _surface;

    internal void PumpAutomatedAcceptance()
    {
        if (!_surfaces.Automation.IsEnabled)
        {
            throw new InvalidOperationException(
                "Semantic Chests Anywhere Overlay acceptance pumping requires the exact TestHarness environment.");
        }
        Pump();
    }

    public void Refresh(bool preserveViewState)
    {
        if (_disposed) return;
        _refreshRequested = true;
        _preserveViewState &= preserveViewState;
    }

    public void UpdateOptions(ChestsAnywhereOverlayConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_disposed) return;
        ChestsAnywhereOverlayConfig next = CopyOptions(config);
        bool semanticStateChanged =
            _options.RememberLastStoragePerCategory != next.RememberLastStoragePerCategory;
        bool surfaceChanged = !OptionsEqual(_options, next);
        _options = next;
        if (semanticStateChanged) _refreshRequested = true;
        if (surfaceChanged && !_retirementPending && _surface != null)
            _surface.Configure(CreateSurfaceOptions());
        Pump();
    }

    public bool Show()
    {
        ThrowIfDisposed();
        if (_retirementPending)
        {
            IUiSemanticSurfaceSession retiringSurface = _surface
                ?? throw new InvalidOperationException("A retiring semantic surface has no owner.");
            RetireActive(retiringSurface, notifyClosed: false);
            return false;
        }
        if (_surface != null)
        {
            if (!_surface.Visible) _surface.Show();
            return true;
        }
        Activate();
        return true;
    }

    public void Hide()
    {
        IUiSemanticSurfaceSession? surface = _surface;
        if (surface == null) return;
        if (!_retirementPending)
        {
            _retirementPending = true;
            _notifyClosedWhenRetired = true;
            surface.Hide();
        }
        if (ReferenceEquals(_surface, surface))
            RetireActive(surface, notifyClosed: true);
    }

    public void Dispose()
    {
        if (_disposed && _surface == null) return;
        _disposed = true;
        IUiSemanticSurfaceSession? surface = _surface;
        if (surface != null)
            RetireActive(surface, notifyClosed: false);
    }

    private void Activate()
    {
        ChestsAnywhereNavigatorExperienceSession? stagedExperience = null;
        IUiSemanticSurfaceSession? stagedSurface = null;
        try
        {
            stagedExperience = new ChestsAnywhereNavigatorExperienceSession(
                ExperienceId,
                new ChestsAnywhereNavigatorPortAdapter(_controller),
                () => _closeRequested = true,
                Translate);
            stagedSurface = _surfaces.CreateActiveMenuOverlay(
                stagedExperience.Experience,
                CreateSurfaceOptions());
            _experience = stagedExperience;
            _surface = stagedSurface;
            _refreshRequested = false;
            _preserveViewState = true;
            _closeRequested = false;
            stagedSurface.Rendered += OnRendered;
            stagedSurface.Closed += () => OnSurfaceClosed(stagedSurface);
            stagedSurface.Show();
        }
        catch (Exception activationFailure)
        {
            var failures = new List<Exception> { activationFailure };
            if (stagedSurface != null && ReferenceEquals(_surface, stagedSurface))
            {
                try
                {
                    RetireActive(stagedSurface, notifyClosed: false);
                }
                catch (AggregateException cleanupFailure)
                {
                    failures.AddRange(cleanupFailure.InnerExceptions);
                }
                catch (Exception cleanupFailure)
                {
                    failures.Add(cleanupFailure);
                }
            }
            else
            {
                Attempt(() => stagedSurface?.Dispose(), failures);
                Attempt(() => stagedExperience?.Dispose(), failures);
            }
            if (failures.Count > 1)
            {
                throw new AggregateException(
                    "Semantic Chests Anywhere Overlay activation and staged cleanup failed.",
                    failures);
            }
            throw;
        }
    }

    private void Pump()
    {
        IUiSemanticSurfaceSession? surface = _surface;
        ChestsAnywhereNavigatorExperienceSession? experience = _experience;
        if (_retirementPending || surface == null || experience == null || !surface.Visible) return;
        if (_closeRequested)
        {
            _closeRequested = false;
            _controller.Hide();
            return;
        }

        if (_refreshRequested)
        {
            bool preserve = _preserveViewState;
            _refreshRequested = false;
            _preserveViewState = true;
            experience.Refresh(preserve);
            surface.Refresh();
        }
        else
        {
            surface.Synchronize();
        }
    }

    private void OnRendered() => Rendered?.Invoke();

    private void OnSurfaceClosed(IUiSemanticSurfaceSession? surface)
    {
        if (surface == null
            || _retirementInProgress
            || !ReferenceEquals(_surface, surface))
        {
            return;
        }
        RetireActive(surface, notifyClosed: true);
    }

    private void RetireActive(IUiSemanticSurfaceSession surface, bool notifyClosed)
    {
        if (!ReferenceEquals(_surface, surface)) return;
        _retirementPending = true;
        _notifyClosedWhenRetired |= notifyClosed;
        if (_retirementInProgress) return;

        ChestsAnywhereNavigatorExperienceSession? experience = _experience;
        var failures = new List<Exception>();
        _retirementInProgress = true;
        try
        {
            if (!_surfaceCleanupCompleted)
                _surfaceCleanupCompleted = AttemptCleanup(surface.Dispose, failures);
            if (!_experienceCleanupCompleted)
                _experienceCleanupCompleted = AttemptCleanup(() => experience?.Dispose(), failures);
        }
        finally
        {
            _retirementInProgress = false;
        }

        if (!_surfaceCleanupCompleted || !_experienceCleanupCompleted)
        {
            throw new AggregateException(
                "Semantic Chests Anywhere Overlay session cleanup failed.",
                failures);
        }

        bool raiseClosed = _notifyClosedWhenRetired;
        ClearActiveState();
        if (raiseClosed) Attempt(() => Closed?.Invoke(), failures);
        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Semantic Chests Anywhere Overlay session cleanup failed.",
                failures);
        }
    }

    private void ClearActiveState()
    {
        _surface = null;
        _experience = null;
        _refreshRequested = false;
        _preserveViewState = true;
        _closeRequested = false;
        _retirementPending = false;
        _surfaceCleanupCompleted = false;
        _experienceCleanupCompleted = false;
        _notifyClosedWhenRetired = false;
    }

    private UiSemanticSurfaceOptions CreateSurfaceOptions()
        => new(
            ExperienceId,
            _options.CloseOnOutsideClick,
            _options.DimUnderlyingMenu);

    private string Translate(string key, string fallback)
    {
        string value = _controller.T(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static ChestsAnywhereOverlayConfig CopyOptions(ChestsAnywhereOverlayConfig source) => new()
    {
        HideNativeSelectors = source.HideNativeSelectors,
        DimUnderlyingMenu = source.DimUnderlyingMenu,
        CloseOnOutsideClick = source.CloseOnOutsideClick,
        RememberLastStoragePerCategory = source.RememberLastStoragePerCategory,
        RecentLimit = source.RecentLimit
    };

    private static bool OptionsEqual(ChestsAnywhereOverlayConfig left, ChestsAnywhereOverlayConfig right)
        => left.HideNativeSelectors == right.HideNativeSelectors
            && left.DimUnderlyingMenu == right.DimUnderlyingMenu
            && left.CloseOnOutsideClick == right.CloseOnOutsideClick
            && left.RememberLastStoragePerCategory == right.RememberLastStoragePerCategory
            && left.RecentLimit == right.RecentLimit;

    private static void Attempt(Action action, ICollection<Exception> failures)
    {
        try { action(); }
        catch (Exception failure) { failures.Add(failure); }
    }

    private static bool AttemptCleanup(Action action, ICollection<Exception> failures)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception failure)
        {
            failures.Add(failure);
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SemanticChestsAnywhereOverlayFrontend));
    }
}
