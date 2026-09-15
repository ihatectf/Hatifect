# Locale-aware text on a retained Experience

The Experience authoring API can attach localized labels and typed formatters to an existing semantic model. Runtime resolves them while composing a scene, using one locale and the values captured for that composition. Changing the locale does not require a new Experience, new semantic IDs or replacement action objects.

This is the bounded common «Адаптация интерфейса к окружению»/«Просмотр состояния Flowline» text contract. Native environment propagation and the Flow consumer migration have their own acceptance; this API alone does not establish complete «Адаптация интерфейса к окружению» or «Просмотр состояния Flowline» readiness.

## Authoring

Keep stable IDs and aliases independent from display text. Declare an element or action group before attaching its text metadata:

```csharp
var owner = new UiSymbolId("Example.Mod", "shipment");
var quantityId = owner.Child("element/quantity");
var sendId = owner.Child("action/send");
var quantity = new UiState<int>(12);
var send = new UiActionDefinition(sendId, "Send", SubmitShipment);

var experience = new UiExperienceBuilder(owner, "Shipment")
    .Element(quantityId, "Quantity", "Quantity", quantity, UiCapabilities.Inspect)
    .Actions("Actions", send)
    .LocalizeDisplayName(new UiLocalizedText("Shipment",
        new Dictionary<string, string> { ["ru-RU"] = "Отправление" }))
    .LocalizeElement(quantityId, new UiLocalizedText("Quantity",
        new Dictionary<string, string> { ["ru-RU"] = "Количество" }))
    .LocalizeAction(sendId, new UiLocalizedText("Send",
        new Dictionary<string, string> { ["ru-RU"] = "Отправить" }))
    .FormatText<int>(quantityId, (value, locale) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + (locale == "ru-RU" ? " шт." : " items"))
    .Build();
```

`UiLocalizedText` copies the supplied locale map and exposes a read-only view. Matching uses `StringComparer.Ordinal`: `ru-RU`, `ru` and `RU-ru` are different keys. There is no inferred parent culture. An unknown locale, including an unknown custom language, selects the explicit fallback; add both `ru` and `ru-RU` entries if the consumer supports both spellings. Missing/blank labels and duplicate keys reject at construction.

The fallback must equal the originally authored display name, element label or action title. Existing `DisplayName`, `Name`/`Label`, `Title`, aliases and graph metadata retain those original strings. The built Experience exposes `LocalizedDisplayName`, `LocalizedElementLabels` and `LocalizedActionTitles` for inspecting its optional immutable text metadata. Bindings and tooling continue to use stable IDs and aliases. This change does not add formatter callbacks to the semantic graph wire protocol.

## Typed values and captured state

`FormatText<T>` must match the declared source's CLR `ValueType` exactly. Its callback receives the captured payload and captured locale. Nullable payloads remain supported by the existing source contract, and an empty result is valid. Returning null or throwing rejects scene preparation with the element ID and locale in the error; an already accepted host scene can remain active.

Formatters must not read live sources, issue application commands, alter global culture or change the game language. For related values, use one `UiPublication` and publish immutable facts rather than translated strings. Runtime captures participating publications before invoking formatters. A shared immutable record source may supply several presented elements with different formatters, preserving a single data revision for the complete scene.

Keep rejection codes, command results and other facts in the published model. Formatting a previously translated string cannot recover its original meaning when locale changes. Domain wording and locale-specific number/date policy belong to the consumer. A native item-name adapter must resolve names for the captured locale; an implicit global-language lookup does not establish that guarantee.

Runtime materializes labels and values into the scene. Layout, rendering and accessibility use those same strings. Drawing does not call the formatter again. An environment-backed invocation uses `Plan.Host.Environment.Locale`; an explicitly supplied conflicting scene locale rejects before publication capture or consumer value reads. Legacy invocations capture their existing explicit or ambient locale once per composition.

The original source and action objects remain in use. A locale-only scene update does not replace the typed action binding, cancel its pending operation or deliver its completion outside the normal owner pump. Candidate formatting and measurement still participate in the existing host prepare/commit behavior; failed preparation retains the accepted scene and focus, and can be retried.

## Supported scope

Value formatters apply to read-only Text/Inspector presentations. Search/Filter inputs, Configure/forms, semantic collections and action groups reject value formatters; their editing, collection-row projection and parsing contracts remain separate. Localizing an element's outer label does not translate form-field labels, options, validation messages, collection item text or input drafts. Those values retain their existing owners and contracts.

Experience action titles are localized without replacing `UiActionDefinition`. Contributions outside that Experience and Terminal navigation descriptor titles retain their existing strings. The Terminal shell title remains shell-owned. Frozen `IUiSemanticSurfaceApi` v1, host interfaces, persistence formats and package version authorities are unchanged by this common contract.

Behavioral checks live in `ExperienceTextTests`, `LocaleCompositionTests`, `LocaleFormatterReentryTests` and the owning `InteractionPreparationTests`. They cover exact lookup and metadata validation; EN → RU → EN on a retained model/host; preservation of an invariant empty locale during publication callbacks; coherent publication reads despite a formatter-triggered publication; materialized accessibility text; formatting/measurement rejection and retry; nullable/empty values; zero formatter calls during drawing; and preservation/completion of a pending typed action. A formatter which accepts a nested host replacement cannot overwrite that accepted frame with its stale outer composition. Native Flow language switching still requires the combined host/consumer runtime evidence described in [FLOW_UI_READINESS.md](FLOW_UI_READINESS.md).
