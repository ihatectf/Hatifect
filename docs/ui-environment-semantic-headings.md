# Заголовки интерфейса

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Актуальный статус на 2026-09-08: **«Адаптация интерфейса к окружению» owner DONE** на source `04def9e`; [итоговая приёмка](ui-environment-acceptance.md) подтверждена независимым GQ review восстановленных raw evidence. Общая alpha47 acceptance и полный «Просмотр состояния Flowline» учитываются отдельно. Ниже сохранены исторические результаты GQ и владельца, включая прежние FAIL и утраченные временные артефакты; их IN_PROGRESS/PENDING не задают текущий статус «Адаптация интерфейса к окружению».

Статус среза — **IN_PROGRESS**, полный «Адаптация интерфейса к окружению»/«Просмотр состояния Flowline» открыт. Actual Flow driver выявил, что принятые accessibility title/labels отсутствуют в render primitives. Native source06e66a, request47d5905b-8ef3-4aad-ae2a-699a8e11de6d завершился FAIL на title; accepted/rendered0/0 pass2,10 text primitives содержали только5 action captions и5 values. Исходный composed PNG просмотрен; заголовок/подписи действительно отсутствуют. Проверки Flow не менялись.

## Исправление

Runtime использует уже принятые `UiScene.DisplayName` и `UiSourceSceneNode.SemanticName`. Layout резервирует одну измеренную строку для host title и каждой scalar-source label. Framework policy — Ellipsis при недостаточной конечной ширине, значение сохраняет Wrap; пустое значение не создаёт пустую строку. TypographyTitle берётся из существующего theme token; scalar label/value используют текущую resolved typography. Heading и value получают раздельные bounds/clip и сохраняют owner Node ID. Обходов source, новых semantic nodes или accessibility rows нет. Terminal сохраняет заголовок Hatifect Terminal и семь slots, размещённых ниже него.

Title-only и label-only изменения вызывают Measure/Arrange/Render без structural recomposition; неизменная сцена сохраняет layout. Дополнительные fixed fields существуют только в layout snapshots; draw использует подготовленные primitives. Публичные contracts, consumer declarations и Flow persistence не меняются. Изменение заметно всем scalar-source consumers и titled hosts; выросла необходимая высота содержимого, поэтому полный platform/CA/native граф обязателен.

## Проверки

- `./tools/hatifect-test ui --project 'Hatifect UI/tests/Hatifect.UI.Runtime.Tests/Hatifect.UI.Runtime.Tests.csproj'`: RED `run-vk6lcpls` — **FAIL9/498**, восемь новых cases плюс усиленный existing observation case. После реализации `run-1af5fg1d` — FAIL3 только в новых exact-float geometry assertions: разница менее0.00002pixel. Геометрические сравнения исправлены на явный0.001pixel tolerance, прежние assertions не ослаблены. Итог `run-wsnfbqkf` — **PASS498**.
- `ScalarTitleLabelAndValueHaveSeparateVisibleRegions` (.75/1/1.25/1.5 metrics): actual host Render передаёт три отдельных run, owner IDs, порядок, положительная высота, containment и Wrap values.
- `NarrowRussianHeadingsHaveExplicitSingleLineEllipsisAndEmptyValueHasNoRun`: длинные кириллические strings, узкая конечная ширина, явный overflow, ровно две строки.
- `TitleOrLabelOnlyChangeInvalidatesAcceptedPresentation` (2cases): stable IDs/value, новые подписи действительно заменяют старые primitives, одинаковая сцена не перестраивает layout.
- `TerminalRetainsSevenSlotsBelowItsOwnRenderedShellTitle`: seven-slot contract и реальные title bounds.
- Existing `CaptureUsesAcceptedTextAndAvailabilityWithoutReadingLiveSources` теперь требует title/label/value Texts при единственной semantic row и poison-callback assertions.

На immutable source `b8b97eb` C `run-xagmg4_l` — PASS1548 .NET +373 Python, G `run-ur8gu9hc` — PASS1822 .NET +373 Python; независимый source/scoped review — PASS. GQ проверил все17 TRX/6source postimages. Isolated P `hatifect-ui-ca-isolated.au075dxm` — PASS90,44 projection files/8 package-producer DLL/3 independent package caches/2 CA deployment DLL независимо подтверждены. Native Flow `70bef1d0` на `ce8c996` — protocol PASS15, все19 composed PNG просмотрены GQ: title/labels видны. Visual acceptance остаётся FAIL из-за отдельного отсутствующего U+2192 glyph; исправление принадлежит UI Stardew adapter. Host-free geometry не объявляется pixel acceptance; forthcoming combined Flow native сохраняет реальные EN/RU/scale/controller-profile assertions. Базовый portal-count correction87fe76a — отдельный prerequisite. Общая интеграция/version authorities принадлежат GQ.

[Общая alpha.47 integration evidence и ограничения](ui-flow-alpha47-integration.md). GQ включает этот source в локальный candidate; окончательная общая приёмка выполняется после native glyph correction.
