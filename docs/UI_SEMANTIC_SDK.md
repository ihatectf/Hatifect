# Semantic UI SDK: five additive slices

The public SDK lives in `Hatifect.UI.Experience`; consumers describe meaning, state and actions. Runtime owns placement, layout, focus, drawing, and resource lifetime. The frozen `IUiSemanticSurfaceApi` v1 source and `ApiVersion == 1` remain unchanged. Existing consumers and v1 test doubles need no new members. New capabilities are separate optional interfaces. Consumers continue to compile against the exact packages selected by `Hatifect.UI.Packages.props`.

## Hosts and ownership

Request `IUiSemanticHostApi` from the UI mod when standalone hosts are required. `CreateSurface` accepts `Window`, `Modal`, `Fullscreen`, or `Hud`. `CreateTerminal` accepts an immutable section list with group, order, optional icon ID and initial section. Terminal navigation and responsive presentation belong to Runtime.

Window, Modal, Fullscreen and Terminal require the native menu slot to be free. Use the existing `CreateActiveMenuOverlay` when augmenting an existing native menu. HUD does not occupy the native menu slot. Modal requires an explicit close action; Window, Fullscreen and Terminal accept root Escape/controller B. Closing a child portal does not dismiss its parent. `DimUnderlyingMenu` is supported only by the active-menu overlay; standalone sessions reject it. `CloseOnOutsidePointer` is an active-menu overlay policy; standalone kinds use their fixed framework policy.

Sessions are single-use: `Hide`/native dismissal is terminal, `Closed` fires once, and the caller must dispose the session. `Dispose` releases host resources and subscriptions, and supports retry after cleanup failure. Caller-owned semantic sources remain caller-owned. Call session methods and publish source changes on the UI thread. Different source objects remain independently subscribed even if their `Equals` implementations compare equal.

```csharp
IUiSemanticHostApi api = helper.ModRegistry.GetApi<IUiSemanticHostApi>("Hatifect.UI")!;
var id = new UiSymbolId("Example.Mod", "settings");
IUiSemanticSurfaceSession surface = api.CreateSurface(
    experience, UiSemanticHostKind.Window, new UiSemanticSurfaceOptions(experience.Id));
surface.Show();
// Keep the session alive for the intended menu lifetime, then Dispose it.
```

Keep the session in an owner field and dispose it when the owner retires. Never replace a foreign native menu to show a standalone surface.

## Typed forms

`UiFormFields.Text`, `Number`, `Toggle` and `Choice<T>` create typed fields with `DraftValue`, string editing source `Value`, `CommittedValue` and validation `Error`. `UiFormState.Apply()` validates every field before publishing all typed committed values and one form notification. Invalid drafts retain the last commit. `Reset()` restores that commit and clears errors. Validators must be pure. Numeric drafts use invariant decimal notation; range policy belongs to the consumer.

```csharp
var count = UiFormFields.Number(id.Child("count"), "Count", 10m,
    value => value >= 0 ? null : "Count must be nonnegative.");
var enabled = UiFormFields.Toggle(id.Child("enabled"), "Enabled", true);
var mode = UiFormFields.Choice(id.Child("mode"), "Mode", "Auto",
    new[] { "Auto", "Manual" }, value => value);
var form = new UiFormState(count, enabled, mode);
var experience = new UiExperienceBuilder(id, "Settings")
    .Configure("Settings", form)
    .Actions("Commands", new UiActionDefinition(id.Child("apply"), "Apply", () =>
    {
        if (form.Apply()) SaveSettings(count.CommittedValue, enabled.CommittedValue, mode.CommittedValue);
    }), new UiActionDefinition(id.Child("reset"), "Reset", form.Reset))
    .Build();
```

The original `new UiSemanticFormField(id, label, mutableStringSource)` constructor remains immediate binding. It does not promise rollback of external setters. Typed factories own the draft/commit pair; the consumer applies the committed values to its domain. Dispose `UiFormState` when its owner retires to detach its source subscriptions. Runtime provides labels, text/numeric editing, selected choice buttons, and error text in the scene/accessibility tree.

## Typed status

Use `UiStatus` when a monitored value represents empty, loading, success, error, or general status
rather than ordinary read-only text. The consumer owns the localized message; Runtime selects the
shared semantic color and accessibility role. Status nodes are read-only and do not enter controller
focus traversal.

```csharp
var status = new UiState<UiStatus>(
    new UiStatus(UiStatusKind.Loading, "Loading routes…"));
var experience = new UiExperienceBuilder(id, "Routes")
    .Status("State", status)
    .Build();

status.Value = new UiStatus(UiStatusKind.Empty, "No routes are configured.");
```

`Error` is exposed as an accessibility alert; the other kinds are status live regions. Typed Visual
recipes may target `@Empty`, `@Loading`, `@Success`, or `@Error`. Existing `Monitor<T>` values keep
their ordinary text behavior, while `Monitor<UiStatus>` opts into the same typed component as
`Status`. `Status` also declares the canonical `UiDataTypes.Status` graph descriptor through
`UiSourceTypes.Status`. The overload accepting an ID, alias and label lets localized consumers keep
stable authoring aliases independently from displayed text.

## Input prompts

Runtime adds the canonical activation prompt to action buttons, toggle/choice options, contributed
actions and routes, Terminal navigation, and every visible row of a selectable collection. Read-only
`Browse` collections do not claim an activation prompt. Consumers keep localized action, route and
item labels free of device text. The accepted environment selects `Enter` for keyboard mode and `A`
for controller mode; mouse-and-keyboard mode omits the prompt to keep pointer-first surfaces quiet.
A legacy environment-free Controller profile also uses `A`.

The prompt is a separate text primitive and reserves width during layout. Its color, typography and
gap use `Text.InputPrompt`, `Typography.InputPrompt` and `Space.S` through the typed
`prompt.foreground`, `prompt.typography` and `prompt.spacing` Visual properties. Dark, Light and
HighContrast themes resolve the same contract from their own accent color. Prompt changes within an
otherwise unchanged presentation invalidate measure, arrange and render without reactivating the
consumer Experience. Collection virtualization measures the prompt once per accepted layout,
reserves a right-side prompt column in each materialized row, and includes the prompt metrics in its
bounded measurement and adaptive-height scopes. Frame construction uses the retained row bounds and
does not call text metrics.

Accessibility keeps the action or item label as the accessible name and exposes the canonical
activation key separately as its shortcut. The Stardew adapter maps Enter or Space and controller A
to Submit; the visual keyboard prompt intentionally chooses Enter as the single canonical label.

## Tooltips

Attach localized semantic help to an already declared presented element, form field or action by its
stable ID. The consumer supplies meaning and translations; Runtime decides how to expose the help
visually and through accessibility.

```csharp
var send = new UiActionDefinition(id.Child("send"), "Send", SendSelectedCargo);
var experience = new UiExperienceBuilder(id, "Shipment")
    .Actions("Commands", send)
    .Tooltip(send.Id, new UiLocalizedText(
        "Send selected cargo",
        new[] { new KeyValuePair<string, string>("ru-RU", "Отправить выбранный груз") }))
    .Build();
```

For forms, declare the containing `Configure` element before targeting a field. Text/number fields
attach help to their input; toggle/choice fields attach the same help to every focusable option while
retaining the field ID as semantic origin.

Registered action, route and section contributions can carry the same immutable localized help in
the tooltip overloads of `UiActionContributionDescriptor` and `UiRouteContributionDescriptor`.
Runtime resolves that metadata with the locale captured for scene composition and projects it to the
contributed button through the existing tooltip overlay. The original descriptor constructors remain
valid and produce no tooltip.

Collection sources can project item help with the final tooltip selector overload of
`UiCollectionSource<T>`, `UiCollectionState<T>`, `UiSelectableCollectionState<T>`,
`UiPublication.Collection<T>`, or `UiPublication.SelectableCollection<T>`. Publication captures the
localized value in the immutable item snapshot. A tooltip-only edit advances both the collection and
item content revisions; a newly allocated `UiLocalizedText` with the same translations does not.
Runtime resolves and measures help only for the materialized window. Each active collection keeps a
bounded 1,024-entry LRU measurement cache, and the layout snapshot indexes materialized rows by
stable ID for constant-time hover, focus, accessibility and frame lookup. Selectable rows expose help
through hover or focus. Read-only `Browse` rows support hover help without becoming actionable.

Keyboard/controller focus exposes help immediately. Pointer hover takes precedence only after the
same eligible target has remained hovered for the active theme's `Motion.Normal` duration; the base
themes use 160 ms. Moving to another target restarts the dwell, leaving it hides hover help
immediately, and a focused tooltip remains visible while another hover is still pending. Themes can
choose zero duration for immediate pointer help. Active host updates advance the dwell and build one
new frame when it crosses the threshold; unchanged ticks do not rebuild layout or frames.

At most one tooltip is visible. The overlay uses `Surface.Popup`, `Text.Primary`,
`Typography.Body`, `Space.S`, `Radius.S`, and `Elevation.High` from the active theme. Tooltip text is
measured during layout, while selecting an already measured tooltip rebuilds only the frame.
Placement prefers the space below its target, falls back above it, and clamps both dimensions to the
host viewport. The tooltip never changes the main tree's desired size or focus order.

The target keeps its accessible name and exposes tooltip text separately as its accessibility
description. Unknown targets, fields whose containing form has not been declared, and duplicate
declarations are rejected before `Build`. Collection item labels likewise remain the accessible name,
with item help in the description. Native visual acceptance remains separate work.

## Collection density

Presentation assets select collection density from the closed `Default`, `Compact`, or
`Comfortable` catalog. Values are unquoted catalog identifiers; arbitrary strings and quoted
legacy values fail compilation with `LUI2018`. Omitting the assignment is equivalent to `Default`.

```text
presentation Storage

Items
    view = List
    itemSizing = Adaptive
    density = Compact
```

Planning preserves density as a stable catalog identity. Runtime converts that identity to its
internal collection policy during scene composition, then uses one factor calculation for both the
initial item-extent estimate and exact virtualized row measurement. Compact and Controller host
profiles apply their existing responsive factor once, after the selected density. Consumers do not
provide numeric spacing or profile multipliers.

## Themes, textures and icons

Production sessions implement `IUiSemanticAppearanceSession`. `SetTheme` selects framework `Dark`, `Light` or `HighContrast` presets without replacing semantic state or the Terminal activation cache. Register a PNG with a stable asset ID and keep its returned lease. The session owns decoded textures; lease disposal releases one registration and session disposal releases all remaining resources. Missing IDs use one reusable checkerboard placeholder per session. Duplicate IDs and invalid/over-budget PNGs are rejected before publication.

Limits: 256 registrations, 4 MiB encoded per image, 16 MiB encoded and 32 MiB decoded per session, dimensions from 1 through 4096. Registration and decoding run on the UI thread, outside draw. Idle drawing only looks up textures.

Decoded PNG color channels are premultiplied once before publication, including transparent pixels. Temporary pixel storage is pooled and returned on success or failure; failed registration releases its texture.

```csharp
var appearance = (IUiSemanticAppearanceSession)surface;
appearance.SetTheme(UiSemanticTheme.Light);
IDisposable iconLease = appearance.RegisterTexture(id.Child("icon"), File.ReadAllBytes(iconPath));
var rows = new UiCollectionSource<Item>(items, item => item.Id,
    item => item.Name, item => item.Description, item => item.IconId);
```

`UiSemanticCollectionItem.Icon` and `UiSemanticTerminalSection.Icon` reference these IDs. The framework reserves icon space before text measurement and emits the texture with the item/navigation clip. Existing collection constructor signatures are retained; icon mapping uses additive overloads.

## External adaptive collections

External `IUiSemanticCollectionSource` implementations may implement `IUiSemanticCollectionMetadata`: nonnegative `Revision`, aggregate `HasSupportingText`, and bounded `TryGetIndex`. Revision changes with content/order/count, but selection alone does not change it. `ContentVersion` changes when an item's measured content changes. Publish the complete snapshot before raising `Changed` and do not mutate during a dispatch/layout pass.

Adaptive collections require this capability. Uniform collections keep the existing fallback. Runtime checks a successful lookup's range and stable ID instead of trusting an external index. No complete source enumeration is introduced: tests retain bounded materialization and measurement for 10,000 items, stable scroll anchoring, and render-only selection invalidation.

## Live Presentation and Visual

Production sessions implement `IUiSemanticReloadSession`. `Reload(experienceId, presentationText, visualText)` compiles the pair against the registered experience, validates the candidate for the current host, and applies it while retaining source objects, selection, and editing state. Either null document uses framework defaults. A failed compile or host rejection retains the previous pair and version. Identical input text performs no recomposition. Results include accepted/changed flags, a session version, and diagnostic code/message/source.

```csharp
var reload = (IUiSemanticReloadSession)surface;
reload.AssetsReloaded += result => ReportDiagnostics(result.Diagnostics);
IDisposable watch = reload.WatchAssets(experience.Id, presentationPath, visualPath);
```

Watching is opt-in, at most one pair per experience and 16 pairs per session. File-system callbacks only mark dirty. Each UI tick processes at most one dirty pair with bounded reads and compilation; unchanged ticks allocate no watch snapshot and perform no file IO. Each document is at most 1 MiB UTF-8. File replacement, deletion, invalid input and read failures preserve the last accepted assets. Transient read failures retry after 30/60/120 UI polls, then wait for a new file event. Dispose the lease to stop one watch. Session dismissal/disposal stops all watches. Watching updates UI assets only; it does not reload C# assemblies, replace semantic sources or execute documents as code.

## Evidence and remaining acceptance

Tests cover host projection/Terminal routing, closing from pointer or keyboard actions, typed apply/reset/error behavior, resource ownership and admission, icon geometry, theme/focus preservation, corrupt external indexes, 10,000-item bounds, live frame updates, rollback, file replacement and watch disposal. `SemanticSdkCompilationTests` compiles the new contracts from CA's exact-package consumer with no Runtime reference or friend access.

Use `./tools/hatifect-check`, `./tools/hatifect-check --platform`, and `./tools/hatifect-isolated-ui-ca` from the repository root. Runtime/visual SMAPI acceptance and frame-time measurements remain a separate acceptance run; static success does not claim in-game verification. Editor completion/hover/LSP enhancements are outside these five slices.

Validation on 2026-09-05: the standard check passed 292 Python and 890 .NET tests; the platform check passed 292 Python and 970 .NET tests. The isolated consumer check passed with 43 projected files, 8 UI packages, 80 CA tests, and no UI source in the consumer checkout. After strengthening the final output assertions, the focused Runtime suite passed all 194 tests. The five slices add 28 executed test cases across 22 methods; existing tests were retained.
