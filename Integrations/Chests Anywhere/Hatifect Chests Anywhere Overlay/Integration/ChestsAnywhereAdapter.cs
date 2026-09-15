using System.Collections;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;
using Hatifect.ChestsAnywhereOverlay.Models;

namespace Hatifect.ChestsAnywhereOverlay.Integration;

internal interface IChestsAnywhereOverlayAdapter
{
    bool IsSupported { get; }
    bool IsOverlayActive { get; }
    bool IsOverlayModal { get; }
    bool HasSuppressedNativeSelectors { get; }
    bool HasMutedNativeToggle { get; }
    bool IsNativeToggleJustPressed(object overlay);
    bool TryCapture(out StorageSnapshot snapshot);
    bool SuppressNativeSelectors(object overlay, bool hide);
    bool RestoreNativeSelectors();
    bool MuteNativeToggle(object overlay);
    bool RestoreNativeToggle();
    void EndSession();
    bool SelectStorage(StorageSnapshot snapshot, StorageEntry entry);
}

/// <summary>
/// Narrow reflection boundary over Chests Anywhere internals. The rest of the mod never references
/// ManagedChest, BaseChestOverlay, ChestFactory, Dropdown, or any other non-public CA type.
/// </summary>
internal sealed class ChestsAnywhereAdapter : IChestsAnywhereOverlayAdapter
{
    private const string ModId = "Pathoschild.ChestsAnywhere";
    private const string IncompatibleAutomationScenario = "semantic.chests-anywhere-overlay.incompatible";
    private const string CaptureExceptionAutomationScenario = "semantic.chests-anywhere-overlay.capture-exception";
    private readonly Action<string> _logWarning;
    private readonly object? _api;
    private readonly FieldInfo? _getOverlayField;
    private readonly MethodInfo? _isOverlayActiveMethod;
    private readonly MethodInfo? _isOverlayModalMethod;
    private readonly Func<object?> _captureOverlay;
    private readonly Action<Exception> _captureFailure;
    private readonly bool _surfaceAvailable;
    private readonly bool _automatedIncompatibleFixture;
    private bool _unsupportedLogged;
    private bool _disabled;
    private bool _automatedCaptureFailurePending;
    private readonly ChestsAnywhereAutomatedOverlayLease _automatedOverlayLease = new();
    private Delegate? _automatedGetOverlay;
    private object? _automatedApiTarget;
    private MethodInfo? _automatedChangeOverlay;
    private bool _automatedTogglePulsePending;
    private readonly ChestsAnywhereNativeSelectorLease _selectorLease = new();

    private object? _mutedKeys;
    private PropertyInfo? _mutedToggleProperty;
    private object? _originalToggle;
    private object? _mutedToggleReplacement;

    public ChestsAnywhereAdapter(IModHelper helper, IMonitor monitor)
        : this(ResolveRawApi(helper), ResolveLogger(monitor), automatedIncompatibleFixture: false)
    {
    }

    private ChestsAnywhereAdapter(
        object? api,
        Action<string> logWarning,
        bool automatedIncompatibleFixture)
    {
        _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        _captureOverlay = CaptureOverlayForBoundary;
        _captureFailure = LogUnsupported;
        _automatedIncompatibleFixture = automatedIncompatibleFixture;

        // Deliberately request the raw API object. SMAPI's generic GetApi<T> may return an interface proxy,
        // which is ideal for public APIs but would hide the private GetOverlay delegate we isolate here.
        _api = api;
        if (_api == null)
        {
            DisableAdapter(new InvalidOperationException("Chests Anywhere did not provide its public API."));
            return;
        }

        Type apiType = _api.GetType();
        _getOverlayField = FindField(apiType, "GetOverlay");
        _isOverlayActiveMethod = FindMethod(apiType, "IsOverlayActive", parameterCount: 0);
        _isOverlayModalMethod = FindMethod(apiType, "IsOverlayModal", parameterCount: 0);
        _surfaceAvailable = _getOverlayField != null
            && _isOverlayActiveMethod != null
            && _isOverlayModalMethod != null;
        if (!ChestsAnywherePrivateApiShape.IsSupported(_api))
        {
            DisableAdapter(new InvalidOperationException("the expected Chests Anywhere 1.30.x API surface was not found"));
        }
    }

    /// <summary>
    /// Selects the production registry binding unless the exact isolated incompatible-API scenario
    /// requests a deliberately malformed raw API object. The fixture still traverses the adapter's
    /// real reflection/shape binding and cannot be enabled by a normal game process.
    /// </summary>
    internal static ChestsAnywhereAdapter CreateForCurrentHost(IModHelper helper, IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(monitor);
        object? installedApi = ResolveRawApi(helper);
        Action<string> log = ResolveLogger(monitor);
        return IsExactAutomatedScenario(IncompatibleAutomationScenario) && installedApi != null
            ? new ChestsAnywhereAdapter(
                new AutomatedIncompatibleApiFixture(),
                log,
                automatedIncompatibleFixture: true)
            : new ChestsAnywhereAdapter(
                installedApi,
                log,
                automatedIncompatibleFixture: false);
    }

    /// <summary>
    /// Minimal internal construction seam for deterministic capture-failure wiring tests. Production
    /// composition always uses the SMAPI constructor above and the same <see cref="LogUnsupported"/>
    /// callback; only private-overlay acquisition and log observation are substituted here.
    /// </summary>
    internal static ChestsAnywhereAdapter CreateForCaptureTest(
        Func<object?> captureOverlay,
        Action<string> log)
        => new(captureOverlay, log);

    internal static ChestsAnywhereAdapter CreateForIncompatibleApiTest(
        object rawApi,
        Action<string> log)
        => new(
            rawApi ?? throw new ArgumentNullException(nameof(rawApi)),
            log ?? throw new ArgumentNullException(nameof(log)),
            automatedIncompatibleFixture: false);

    private ChestsAnywhereAdapter(Func<object?> captureOverlay, Action<string> log)
    {
        _captureOverlay = captureOverlay ?? throw new ArgumentNullException(nameof(captureOverlay));
        ArgumentNullException.ThrowIfNull(log);
        _logWarning = log;
        _captureFailure = LogUnsupported;
        _api = new object();
        _surfaceAvailable = true;
        _automatedIncompatibleFixture = false;
    }

    public bool IsSupported => !_disabled && _api != null && _surfaceAvailable;
    public bool IsOverlayActive => InvokeApiBool(_isOverlayActiveMethod);
    public bool IsOverlayModal => InvokeApiBool(_isOverlayModalMethod);
    public bool HasSuppressedNativeSelectors => _selectorLease.IsActive;
    public bool HasMutedNativeToggle => _mutedKeys != null || _mutedToggleProperty != null;

    internal bool HasRejectedAutomatedIncompatibleApi
        => _automatedIncompatibleFixture && _disabled && _api is AutomatedIncompatibleApiFixture;

    internal bool HasAutomatedOverlayLease => _automatedOverlayLease.IsActive;

    internal bool TryReleaseAutomatedLeaseAfterReturnedToTitle(out string diagnostic)
    {
        if (!IsExactAutomatedScenario("semantic.chests-anywhere-overlay.return-to-title")
            || Context.IsWorldReady)
            throw new InvalidOperationException("Retirement observation requires the exact title-lifecycle scenario after world unload.");
        bool released = _automatedOverlayLease.TryReleaseAfterNativeRetirement(
            () => _automatedGetOverlay?.DynamicInvoke(),
            () => Game1.activeClickableMenu,
            out diagnostic);
        if (released)
        {
            _automatedGetOverlay = null;
            _automatedApiTarget = null;
            _automatedChangeOverlay = null;
        }
        return released;
    }

    internal void ArmAutomatedCaptureFailure()
    {
        if (!IsExactAutomatedScenario(CaptureExceptionAutomationScenario))
        {
            throw new InvalidOperationException(
                "Capture-failure injection is restricted to its exact automated TestHarness scenario.");
        }
        if (!IsSupported)
            throw new InvalidOperationException("Capture-failure injection requires a supported Chests Anywhere adapter.");
        if (_automatedCaptureFailurePending)
            throw new InvalidOperationException("A capture-failure injection is already pending.");
        _automatedCaptureFailurePending = true;
    }

    /// <summary>
    /// TestHarness-only observation that the exact native overlay/menu pair opened by automation is
    /// still current. This proves a semantic handoff did not close or replace the selected native
    /// Chests Anywhere menu; it grants no mutation authority.
    /// </summary>
    internal bool HasCurrentAutomatedOverlayLease
    {
        get
        {
            if (!_automatedOverlayLease.IsCommitted || _automatedGetOverlay == null)
                return false;
            try
            {
                return _automatedOverlayLease.Matches(
                    _automatedGetOverlay.DynamicInvoke(),
                    Game1.activeClickableMenu);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsNativeToggleJustPressed(object overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (_automatedTogglePulsePending)
        {
            _automatedTogglePulsePending = false;
            if (_automatedOverlayLease.Matches(overlay, Game1.activeClickableMenu))
                return true;
        }
        if (_originalToggle is KeybindList leasedToggle)
            return leasedToggle.JustPressed();
        try
        {
            object keys = RequiredField(overlay.GetType(), "Keys").GetValue(overlay)
                ?? throw new InvalidOperationException("Chests Anywhere parsed controls are unavailable.");
            object? toggle = RequiredProperty(keys.GetType(), "Toggle").GetValue(keys);
            return toggle is KeybindList keybind && keybind.JustPressed();
        }
        catch (Exception ex)
        {
            LogUnsupported(ex);
            return false;
        }
    }

    /// <summary>
    /// Opens Chests Anywhere's real overlay only for the exact automated TestHarness process.
    /// This is intentionally not a general integration fallback: it is pinned to the verified
    /// 1.30.x raw API delegate target and fails closed when that private shape drifts.
    /// </summary>
    internal bool TryOpenAutomatedOverlay(out string diagnostic)
    {
        diagnostic = string.Empty;
        bool nativeOpenAttempted = false;
        if (!IsExactAutomatedEnvironment())
        {
            diagnostic = "Opening Chests Anywhere through its private menu owner is restricted to the exact automated TestHarness environment.";
            return false;
        }
        if (!IsSupported || _api == null || _getOverlayField == null)
        {
            diagnostic = "Chests Anywhere's expected 1.30.x raw API surface is unavailable.";
            return false;
        }

        try
        {
            if (_automatedOverlayLease.IsActive)
                throw new InvalidOperationException("A Hatifect-owned automated Chests Anywhere menu is already active.");
            if (Game1.activeClickableMenu != null)
                throw new InvalidOperationException("The automated Chests Anywhere open refused to replace an existing menu.");
            if (_getOverlayField.GetValue(_api) is not Delegate getOverlay)
                throw new InvalidOperationException("Chests Anywhere's raw API did not expose the expected GetOverlay delegate.");
            if (getOverlay.DynamicInvoke() != null)
                throw new InvalidOperationException("The automated Chests Anywhere open refused a pre-existing native overlay.");
            object target = getOverlay.Target
                ?? throw new InvalidOperationException("Chests Anywhere's GetOverlay delegate did not retain its ModEntry target.");
            MethodInfo openMenu = FindMethod(target.GetType(), "OpenMenu", parameterCount: 0)
                ?? throw new MissingMethodException(target.GetType().FullName, "OpenMenu");
            MethodInfo changeOverlay = FindMethod(target.GetType(), "ChangeOverlayIfNeeded", parameterCount: 0)
                ?? throw new MissingMethodException(target.GetType().FullName, "ChangeOverlayIfNeeded");

            // Publish callbacks before the first native mutation. If OpenMenu mutates and then
            // throws, the catch path can still acquire and retire the exact provisional menu.
            _automatedGetOverlay = getOverlay;
            _automatedApiTarget = target;
            _automatedChangeOverlay = changeOverlay;

            nativeOpenAttempted = true;
            openMenu.Invoke(target, null);
            object provisionalMenu = Game1.activeClickableMenu
                ?? throw new InvalidOperationException("Chests Anywhere did not create a native menu after OpenMenu.");
            _automatedOverlayLease.AcquireProvisionalMenu(provisionalMenu);
            changeOverlay.Invoke(target, null);
            object overlay = getOverlay.DynamicInvoke()
                ?? throw new InvalidOperationException("Chests Anywhere did not create an overlay after OpenMenu and ChangeOverlayIfNeeded.");
            object menu = RequiredField(overlay.GetType(), "Menu").GetValue(overlay)
                ?? throw new InvalidOperationException("Chests Anywhere's active overlay did not retain its native menu.");
            if (!ReferenceEquals(provisionalMenu, menu)
                || Game1.activeClickableMenu == null
                || !ReferenceEquals(Game1.activeClickableMenu, menu))
                throw new InvalidOperationException("Chests Anywhere's overlay menu does not match the active native menu.");
            if (menu is not IClickableMenu)
                throw new InvalidOperationException("Chests Anywhere's active native menu has an unsupported type.");
            if (!_automatedOverlayLease.TryAttachProvisionalOverlay(overlay, menu))
                throw new InvalidOperationException("Chests Anywhere's overlay could not commit the exact provisional menu lease.");
            if (!IsOverlayActive)
                throw new InvalidOperationException("Chests Anywhere did not report the opened overlay as active.");
            // The private OpenMenu call models CA's half of the configured toggle. Publish one exact,
            // lease-bound JustPressed edge so the next production controller tick must traverse the
            // normal lifecycle transition instead of acceptance calling controller.Show directly.
            _automatedTogglePulsePending = true;
            return true;
        }
        catch (Exception error)
        {
            _automatedTogglePulsePending = false;
            string rollbackDiagnostic = string.Empty;
            try
            {
                _automatedOverlayLease.TryAcquireProvisionalMenuAfterOpenAttempt(
                    nativeOpenAttempted,
                    Game1.activeClickableMenu);
                if (_automatedOverlayLease.ShouldRollbackFailedOpen(nativeOpenAttempted))
                {
                    if (!TryCloseAutomatedOverlay(out rollbackDiagnostic)
                        && string.IsNullOrWhiteSpace(rollbackDiagnostic))
                    {
                        rollbackDiagnostic = "The exact committed automation lease could not be rolled back.";
                    }
                }
            }
            catch (Exception rollbackFailure)
            {
                rollbackDiagnostic = "Rollback failed: " + Unwrap(rollbackFailure).Message;
            }

            if (!_automatedOverlayLease.IsActive)
            {
                _automatedGetOverlay = null;
                _automatedApiTarget = null;
                _automatedChangeOverlay = null;
            }
            diagnostic = "Could not open the pinned Chests Anywhere 1.30.x automated overlay: "
                + Unwrap(error).Message
                + (string.IsNullOrWhiteSpace(rollbackDiagnostic) ? string.Empty : " " + rollbackDiagnostic);
            _logWarning(diagnostic);
            return false;
        }
    }

    /// <summary>
    /// Closes the Chests Anywhere menu opened by <see cref="TryOpenAutomatedOverlay"/> only for the
    /// exact automated TestHarness process. An unrelated active menu is never retired.
    /// </summary>
    internal bool TryCloseAutomatedOverlay(out string diagnostic)
    {
        diagnostic = string.Empty;
        _automatedTogglePulsePending = false;
        if (!IsExactAutomatedEnvironment())
        {
            diagnostic = "Closing Chests Anywhere through its private menu owner is restricted to the exact automated TestHarness environment.";
            return false;
        }
        try
        {
            Delegate? getOverlay = _automatedGetOverlay;
            object? target = _automatedApiTarget;
            MethodInfo? changeOverlay = _automatedChangeOverlay;
            bool closed = _automatedOverlayLease.TryClose(
                () => getOverlay?.DynamicInvoke(),
                () => Game1.activeClickableMenu,
                menu => ((IClickableMenu)menu).exitThisMenu(),
                () => changeOverlay?.Invoke(target, null),
                out diagnostic);
            if (closed && !_automatedOverlayLease.IsActive)
            {
                _automatedGetOverlay = null;
                _automatedApiTarget = null;
                _automatedChangeOverlay = null;
            }
            return closed;
        }
        catch (Exception error)
        {
            diagnostic = "Could not close the pinned Chests Anywhere 1.30.x automated overlay: " + Unwrap(error).Message;
            _logWarning(diagnostic);
            return false;
        }
    }

    public bool TryCapture(out StorageSnapshot snapshot)
    {
        snapshot = null!;
        if (!ChestsAnywhereCaptureBoundary.TryAcquire(
                _captureOverlay,
                _captureFailure,
                out object overlay))
            return false;
        try
        {
            snapshot = CaptureStorageSnapshot(overlay);
            return true;
        }
        catch (Exception ex)
        {
            LogUnsupported(ex);
            return false;
        }
    }

    private StorageSnapshot CaptureStorageSnapshot(object overlay)
    {
        FieldInfo chestsField = RequiredField(overlay.GetType(), "Chests");
        FieldInfo chestField = RequiredField(overlay.GetType(), "Chest");
        Array chests = chestsField.GetValue(overlay) as Array
            ?? throw new InvalidOperationException("Chests Anywhere overlay did not expose its storage array.");
        object current = chestField.GetValue(overlay)
            ?? throw new InvalidOperationException("Chests Anywhere overlay did not expose its current storage.");

        var entries = new List<StorageEntry>(chests.Length);
        foreach (object? chest in chests)
        {
            if (chest == null)
                continue;
            entries.Add(ReadStorage(chest));
        }

        string currentKey = BuildKey(current);
        IReadOnlyList<string> categories = ReadCategories(overlay, entries);
        return new StorageSnapshot(overlay, categories, entries, currentKey);
    }

    /// <summary>Remove CA's native chest/category dropdowns while preserving its underlying menu and navigation logic.</summary>
    public bool SuppressNativeSelectors(object overlay, bool hide)
    {
        if (!hide)
            return RestoreNativeSelectors();
        try
        {
            ArgumentNullException.ThrowIfNull(overlay);
            return _selectorLease.Suppress(new ReflectionNativeSelectorAccess(overlay));
        }
        catch (Exception ex)
        {
            LogUnsupported(ex);
            return false;
        }
    }

    /// <summary>Restore the exact native selector objects and edit-button bounds leased by Hatifect.</summary>
    public bool RestoreNativeSelectors()
    {
        if (_selectorLease.TryRestore(out Exception? error)) return true;
        DisableAdapter(error!);
        return false;
    }

    /// <summary>
    /// While CA is already open, its normal toggle binding would close the chest menu. Temporarily mute only
    /// that parsed binding so the same configured CA binding (B by default) can close the Hatifect overlay first.
    /// It is restored when CA closes.
    /// </summary>
    public bool MuteNativeToggle(object overlay)
    {
        try
        {
            object keys = RequiredField(overlay.GetType(), "Keys").GetValue(overlay)
                ?? throw new InvalidOperationException("Chests Anywhere parsed controls are unavailable.");
            PropertyInfo toggle = RequiredProperty(keys.GetType(), "Toggle");

            if (!ReferenceEquals(_mutedKeys, keys))
            {
                if (!RestoreNativeToggle()) return false;
                _mutedKeys = keys;
                _mutedToggleProperty = toggle;
                _originalToggle = toggle.GetValue(keys);
                _mutedToggleReplacement = new KeybindList();
            }

            // Keep this operation idempotent. Replacing the KeybindList every update allocated a new
            // object each frame and made CA's input state harder to reason about. If CA rebuilt its
            // parsed controls while the overlay stayed alive, reapply our one stable muted instance.
            if (!ReferenceEquals(toggle.GetValue(keys), _mutedToggleReplacement))
                toggle.SetValue(keys, _mutedToggleReplacement);
            return true;
        }
        catch (Exception ex)
        {
            LogUnsupported(ex);
            return false;
        }
    }

    public void EndSession()
    {
        _automatedTogglePulsePending = false;
        _automatedCaptureFailurePending = false;
        RestoreNativeSelectors();
        RestoreNativeToggle();
    }

    public bool RestoreNativeToggle()
    {
        if (_mutedKeys == null || _mutedToggleProperty == null)
            return true;
        bool restored = false;
        try
        {
            _mutedToggleProperty.SetValue(_mutedKeys, _originalToggle);
            restored = ReferenceEquals(_mutedToggleProperty.GetValue(_mutedKeys), _originalToggle);
        }
        catch (Exception ex)
        {
            _logWarning($"Could not restore the Chests Anywhere toggle binding: {ex.Message}");
        }
        if (restored)
        {
            _mutedKeys = null;
            _mutedToggleProperty = null;
            _originalToggle = null;
            _mutedToggleReplacement = null;
        }
        return restored;
    }

    public bool SelectStorage(StorageSnapshot snapshot, StorageEntry entry)
    {
        bool advanceAutomationLease = false;
        object? leasedMenu = null;
        bool selectionAttempted = false;
        bool leaseTransitionCaptured = false;
        try
        {
            MethodInfo method = FindMethod(snapshot.Overlay.GetType(), "SelectChest", parameterCount: 1)
                ?? throw new MissingMethodException(snapshot.Overlay.GetType().FullName, "SelectChest");
            advanceAutomationLease = _automatedOverlayLease.IsActive;
            leasedMenu = Game1.activeClickableMenu;
            if (advanceAutomationLease
                && !_automatedOverlayLease.Matches(snapshot.Overlay, leasedMenu))
            {
                throw new InvalidOperationException(
                    "The Chests Anywhere handoff no longer starts from Hatifect's exact automated ownership lease.");
            }
            selectionAttempted = true;
            method.Invoke(snapshot.Overlay, new[] { entry.Handle });
            if (advanceAutomationLease)
            {
                leaseTransitionCaptured = AdvanceAutomationLeaseAfterSelection(snapshot, leasedMenu!, selectionAttempted);
            }
            return true;
        }
        catch (Exception ex)
        {
            string recoveryDiagnostic = string.Empty;
            string rollbackDiagnostic = string.Empty;
            bool rollbackAuthorized = false;
            if (advanceAutomationLease && selectionAttempted && !leaseTransitionCaptured && leasedMenu != null)
            {
                recoveryDiagnostic = RecoverAutomationLeaseAfterFailedSelection(snapshot, leasedMenu, out rollbackAuthorized);
            }
            if (rollbackAuthorized && _automatedOverlayLease.IsActive)
            {
                rollbackDiagnostic = RollbackAutomationLease();
            }
            _logWarning(
                $"Chests Anywhere could not switch to '{entry.Name}': {Unwrap(ex).Message}{recoveryDiagnostic}{rollbackDiagnostic}");
            return false;
        }
    }

    private bool AdvanceAutomationLeaseAfterSelection(StorageSnapshot snapshot, object leasedMenu, bool selectionAttempted)
    {
        ChestsAnywhereAutomatedHandoffState handoffState = ObserveHandoffState(
            snapshot, leasedMenu, out object? nextOverlay, out object? nextMenu);
        if (handoffState == ChestsAnywhereAutomatedHandoffState.AwaitingOverlaySynchronization)
        {
            ReconcileNativeOverlay();
            handoffState = ObserveHandoffState(snapshot, leasedMenu, out nextOverlay, out nextMenu);
        }
        if (handoffState != ChestsAnywhereAutomatedHandoffState.Synchronized)
        {
            throw new InvalidOperationException(
                "The Chests Anywhere handoff did not reach its synchronized native overlay/menu state.");
        }
        if (!_automatedOverlayLease.TryAdvanceAfterMutation(
                snapshot.Overlay,
                leasedMenu,
                selectionAttempted,
                nextOverlay,
                nextMenu))
        {
            throw new InvalidOperationException(
                "The Chests Anywhere handoff could not advance the exact automated ownership lease.");
        }
        return true;
    }

    private ChestsAnywhereAutomatedHandoffState ObserveHandoffState(
        StorageSnapshot snapshot, object leasedMenu, out object? nextOverlay, out object? nextMenu)
    {
        nextOverlay = GetOverlayObject();
        nextMenu = Game1.activeClickableMenu;
        object? nextOverlayMenu = nextOverlay == null
            ? null
            : RequiredField(nextOverlay.GetType(), "Menu").GetValue(nextOverlay);
        return ChestsAnywhereAutomatedHandoffPolicy.Classify(
            snapshot.Overlay,
            leasedMenu,
            nextOverlay,
            nextMenu,
            nextOverlayMenu);
    }

    private void ReconcileNativeOverlay()
    {
        object target = _automatedApiTarget
            ?? throw new InvalidOperationException("The automated Chests Anywhere owner is unavailable during handoff.");
        MethodInfo changeOverlay = _automatedChangeOverlay
            ?? throw new InvalidOperationException("The automated Chests Anywhere overlay synchronizer is unavailable during handoff.");

        // Chests Anywhere 1.30.1 deliberately leaves its old overlay attached until
        // the next UpdateTicking/UpdateTicked callback. The automated acceptance runs
        // in one bounded host action, so invoke that same native reconciler explicitly.
        // Shipping Bin may replace the menu a second time inside this callback; only the
        // final overlay/menu pair is eligible to become the new exact ownership lease.
        changeOverlay.Invoke(target, null);
    }

    private string RecoverAutomationLeaseAfterFailedSelection(
        StorageSnapshot snapshot, object leasedMenu, out bool rollbackAuthorized)
    {
        rollbackAuthorized = false;
        try
        {
            // Capture the active menu before invoking the private overlay getter. If the
            // getter itself fails, diagnostics still preserve which causally-created menu
            // displaced the exact leased menu during this handoff attempt.
            object? recoveryMenu = Game1.activeClickableMenu;
            object? recoveryOverlay = null;
            object? recoveryOverlayMenu = null;
            Exception? overlayReadFailure = null;
            try
            {
                recoveryOverlay = GetOverlayObject();
                recoveryOverlayMenu = recoveryOverlay == null
                    ? null
                    : RequiredField(recoveryOverlay.GetType(), "Menu").GetValue(recoveryOverlay);
            }
            catch (Exception readFailure)
            {
                overlayReadFailure = Unwrap(readFailure);
            }

            bool recovered;
            string recoveryDiagnostic = string.Empty;
            if (overlayReadFailure != null)
            {
                recovered = _automatedOverlayLease.TryRecoverMenuAfterMutation(
                    snapshot.Overlay,
                    leasedMenu,
                    mutationAttempted: true,
                    recoveryMenu);
                recoveryDiagnostic = " Native overlay recovery read failed after the active menu was captured: "
                    + overlayReadFailure.Message + ".";
            }
            else
            {
                bool recoverable = ChestsAnywhereAutomatedHandoffPolicy.CanRecoverForRollback(
                    snapshot.Overlay,
                    leasedMenu,
                    recoveryOverlay,
                    recoveryMenu,
                    recoveryOverlayMenu);
                recovered = recoverable
                    && _automatedOverlayLease.TryAdvanceAfterMutation(
                        snapshot.Overlay,
                        leasedMenu,
                        mutationAttempted: true,
                        recoveryOverlay,
                        recoveryMenu);
                if (!recoverable && recoveryMenu != null)
                {
                    recovered = _automatedOverlayLease.TryRecoverMenuAfterMutation(
                        snapshot.Overlay,
                        leasedMenu,
                        mutationAttempted: true,
                        recoveryMenu);
                    recoveryDiagnostic =
                        " The invalid native pair was demoted to its causally-created menu for exact rollback.";
                }
                else if (!recoverable)
                {
                    recoveryDiagnostic =
                        " The observed native state was not eligible for exact automation rollback.";
                }
            }
            rollbackAuthorized = recovered;
            if (!recovered)
            {
                recoveryDiagnostic += " The exact automation lease could not recover the attempted native handoff.";
            }
            return recoveryDiagnostic;
        }
        catch (Exception recoveryFailure)
        {
            return " Native handoff recovery failed: " + Unwrap(recoveryFailure).Message;
        }
    }

    private string RollbackAutomationLease()
    {
        try
        {
            if (!TryCloseAutomatedOverlay(out string closeDiagnostic))
            {
                return string.IsNullOrWhiteSpace(closeDiagnostic)
                    ? " The failed native handoff could not roll back its exact automation lease."
                    : " " + closeDiagnostic;
            }
            return string.Empty;
        }
        catch (Exception rollbackFailure)
        {
            return " Native handoff rollback failed: " + Unwrap(rollbackFailure).Message;
        }
    }

    private bool InvokeApiBool(MethodInfo? method)
    {
        if (!IsSupported || _api == null || method == null)
            return false;
        try
        {
            return method.Invoke(_api, null) is true;
        }
        catch (Exception ex)
        {
            LogUnsupported(ex);
            return false;
        }
    }

    private object? GetOverlayObject()
    {
        if (!IsSupported || _api == null || _getOverlayField == null)
            return null;
        if (_getOverlayField.GetValue(_api) is not Delegate getter)
            return null;
        return getter.DynamicInvoke();
    }

    private object? CaptureOverlayForBoundary()
    {
        if (_automatedCaptureFailurePending)
        {
            _automatedCaptureFailurePending = false;
            throw new InvalidOperationException(
                "The exact automated capture-exception scenario injected one overlay acquisition failure.");
        }
        return GetOverlayObject();
    }

    private StorageEntry ReadStorage(object chest)
    {
        string name = ReadString(chest, "DisplayName", "Storage");
        string category = ReadString(chest, "DisplayCategory", "Other");
        object? location = ReadProperty(chest, "Location");
        string locationName = location == null
            ? category
            : ReadStorageLocationName(location, category);
        int? order = ReadNullableInt(chest, "Order");
        return new StorageEntry(BuildKey(chest), name, category, locationName, order, chest);
    }

    private static string ReadStorageLocationName(object location, string category)
    {
        string name = ReadString(location, "Name", category);
        string uniqueName = ReadString(location, "NameOrUniqueName", name);
        return ReadString(location, "DisplayName", uniqueName);
    }

    private static IReadOnlyList<string> ReadCategories(object overlay, IReadOnlyList<StorageEntry> entries)
    {
        FieldInfo? categoriesField = FindField(overlay.GetType(), "Categories");
        if (categoriesField?.GetValue(overlay) is IEnumerable values)
        {
            var categories = new List<string>();
            foreach (object? value in values)
            {
                string? text = value?.ToString();
                if (!string.IsNullOrWhiteSpace(text) && !categories.Contains(text, StringComparer.Ordinal))
                    categories.Add(text);
            }
            if (categories.Count > 0)
                return categories;
        }
        return entries.Select(p => p.Category).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static int RequiredCategoryCount(object overlay)
    {
        object categories = RequiredField(overlay.GetType(), "Categories").GetValue(overlay)
            ?? throw new InvalidOperationException("Chests Anywhere's category collection is unavailable.");
        if (categories is ICollection collection)
        {
            if (collection.Count < 1)
                throw new InvalidOperationException("Chests Anywhere's active overlay has no categories.");
            return collection.Count;
        }
        if (categories is not IEnumerable values)
            throw new InvalidOperationException("Chests Anywhere's category collection has an unsupported shape.");
        int count = 0;
        foreach (object? _ in values)
        {
            count++;
            if (count > 1) break;
        }
        if (count < 1)
            throw new InvalidOperationException("Chests Anywhere's active overlay has no categories.");
        return count;
    }

    private static string BuildKey(object chest)
    {
        object? location = ReadProperty(chest, "Location");
        string locationId;
        if (location == null)
            locationId = "unknown";
        else
        {
            string name = ReadString(location, "Name", location.GetType().Name);
            locationId = ReadString(location, "NameOrUniqueName", name);
        }
        object? tileRaw = ReadProperty(chest, "Tile");
        string tile = tileRaw is Vector2 point ? $"{point.X:0.###},{point.Y:0.###}" : "0,0";
        object? container = ReadProperty(chest, "Container");
        string containerType = container?.GetType().FullName ?? "container";
        return $"{locationId}|{tile}|{containerType}";
    }

    private static bool TryResolveBounds(
        object component,
        out FieldInfo? field,
        out PropertyInfo? property,
        out Rectangle value)
    {
        field = null;
        property = null;
        value = default;
        Type? type = component.GetType();
        while (type != null)
        {
            FieldInfo? candidateField = type.GetField("bounds", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (candidateField != null && candidateField.FieldType == typeof(Rectangle))
            {
                field = candidateField;
                value = (Rectangle)candidateField.GetValue(component)!;
                return true;
            }
            PropertyInfo? candidateProperty = type.GetProperty("bounds", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?? type.GetProperty("Bounds", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (candidateProperty?.CanRead == true && candidateProperty.CanWrite && candidateProperty.PropertyType == typeof(Rectangle))
            {
                property = candidateProperty;
                value = (Rectangle)candidateProperty.GetValue(component)!;
                return true;
            }
            type = type.BaseType;
        }
        return false;
    }

    private static void WriteBounds(
        object component,
        FieldInfo? field,
        PropertyInfo? property,
        Rectangle value)
    {
        if (field != null) field.SetValue(component, value);
        else if (property != null) property.SetValue(component, value);
        else throw new InvalidOperationException("No leased Chests Anywhere edit-button bounds member is available.");
    }

    private sealed class ReflectionNativeSelectorAccess : IChestsAnywhereNativeSelectorAccess
    {
        private readonly object _overlay;
        private readonly FieldInfo _chestDropdown;
        private readonly FieldInfo _categoryDropdown;
        private readonly FieldInfo _editButtonField;
        private readonly object _editButton;
        private readonly FieldInfo? _editBoundsField;
        private readonly PropertyInfo? _editBoundsProperty;

        internal ReflectionNativeSelectorAccess(object overlay)
        {
            _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
            _chestDropdown = RequiredField(overlay.GetType(), "ChestDropdown");
            _categoryDropdown = RequiredField(overlay.GetType(), "CategoryDropdown");
            _editButtonField = RequiredField(overlay.GetType(), "EditButton");
            _editButton = _editButtonField.GetValue(overlay)
                ?? throw new InvalidOperationException("Chests Anywhere's edit button was absent before Hatifect acquired it.");
            if (!TryResolveBounds(
                    _editButton,
                    out _editBoundsField,
                    out _editBoundsProperty,
                    out _))
            {
                throw new InvalidOperationException("Chests Anywhere's edit-button bounds could not be leased exactly.");
            }

            // Validate the collection shape during preflight, before the lease can mutate a selector.
            _ = RequiredCategoryCount(overlay);
        }

        public object Overlay => _overlay;
        public object? ChestSelector
        {
            get => _chestDropdown.GetValue(_overlay);
            set => _chestDropdown.SetValue(_overlay, value);
        }

        public object? CategorySelector
        {
            get => _categoryDropdown.GetValue(_overlay);
            set => _categoryDropdown.SetValue(_overlay, value);
        }

        public int CategoryCount => RequiredCategoryCount(_overlay);
        public object? EditButton => _editButtonField.GetValue(_overlay);
        public object EditBounds
        {
            get
            {
                if (_editBoundsField != null)
                    return (Rectangle)_editBoundsField.GetValue(_editButton)!;
                if (_editBoundsProperty != null)
                    return (Rectangle)_editBoundsProperty.GetValue(_editButton)!;
                throw new InvalidOperationException("No leased Chests Anywhere edit-button bounds member is available.");
            }
            set
            {
                if (value is not Rectangle bounds)
                    throw new InvalidOperationException("Chests Anywhere's edit-button bounds changed type during restoration.");
                WriteBounds(_editButton, _editBoundsField, _editBoundsProperty, bounds);
            }
        }

        public object HiddenEditBounds => new Rectangle(-10000, -10000, 1, 1);
    }

    private static object? ReadProperty(object target, string name)
        => FindProperty(target.GetType(), name)?.GetValue(target);

    private static string ReadString(object target, string name, string fallback)
    {
        string? value = ReadProperty(target, name)?.ToString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int? ReadNullableInt(object target, string name)
    {
        object? value = ReadProperty(target, name);
        return value switch
        {
            int number => number,
            _ when value != null && int.TryParse(value.ToString(), out int parsed) => parsed,
            _ => null
        };
    }

    private static FieldInfo RequiredField(Type type, string name)
        => FindField(type, name) ?? throw new MissingFieldException(type.FullName, name);

    private static PropertyInfo RequiredProperty(Type type, string name)
        => FindProperty(type, name) ?? throw new MissingMemberException(type.FullName, name);

    private static FieldInfo? FindField(Type? type, string name)
    {
        while (type != null)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null)
                return field;
            type = type.BaseType;
        }
        return null;
    }

    private static PropertyInfo? FindProperty(Type? type, string name)
    {
        while (type != null)
        {
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null)
                return property;
            type = type.BaseType;
        }
        return null;
    }

    private static MethodInfo? FindMethod(Type? type, string name, int parameterCount)
    {
        while (type != null)
        {
            MethodInfo? method = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(p => p.Name == name && p.GetParameters().Length == parameterCount);
            if (method != null)
                return method;
            type = type.BaseType;
        }
        return null;
    }

    private static object? ResolveRawApi(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        return helper.ModRegistry.GetApi(ModId);
    }

    private static Action<string> ResolveLogger(IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return message => monitor.Log(message, LogLevel.Warn);
    }

    private static bool IsExactAutomatedEnvironment()
        => string.Equals(Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE"), "1", StringComparison.Ordinal)
           && string.Equals(Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED"), "1", StringComparison.Ordinal);

    private static bool IsExactAutomatedScenario(string scenario)
        => IsExactAutomatedEnvironment()
           && string.Equals(
               Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO"),
               scenario,
               StringComparison.Ordinal);

    private void LogUnsupported(Exception ex) => DisableAdapter(Unwrap(ex));

    private void DisableAdapter(Exception ex)
    {
        _disabled = true;
        bool selectorsRestored = _selectorLease.TryRestore(out Exception? restorationFailure);
        bool toggleRestored = RestoreNativeToggle();
        if (_unsupportedLogged)
            return;
        _unsupportedLogged = true;
        string restoration = selectorsRestored && toggleRestored
            ? "Native selectors and the original toggle were restored."
            : $"Native restoration could not be proven complete: {restorationFailure?.Message ?? "toggle restoration failed"}.";
        _logWarning(
            $"Hatifect Chests Anywhere Overlay disabled its integration for this process because the expected 1.30.x private surface was not found: {Unwrap(ex).Message}. {restoration}");
    }

    private static Exception Unwrap(Exception ex)
        => ex is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException! : ex;

    private sealed class AutomatedIncompatibleApiFixture
    {
        // Intentionally near-compatible: the activity methods remain, while GetOverlay drifted
        // from a delegate to an opaque object. The side-effect-free production shape guard must
        // reject this before any overlay/native-menu member can be read or mutated.
        private readonly object GetOverlay = new();
        private bool IsOverlayActive() => false;
        private bool IsOverlayModal() => false;
    }
}
