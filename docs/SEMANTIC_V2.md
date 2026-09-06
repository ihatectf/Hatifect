# B02 / D01 — минимальный semantic-v2

Дата: 2026-09-06. Решение принято в пределах явно утверждённой roadmap. База: B01, commit `de766c5`, код `55b8360`. Этот документ задаёт контракт для U01–U04/F11; перечисленные новые типы становятся реализованными только вместе с кодом и acceptance своего среза.

## D01: один graph и существующий runtime

Выбрана аддитивная эволюция. Semantics получает transport-neutral semantic graph и его binder. Experience связывает typed C# sources/actions с этим graph. Старый Experience builder, Quick, View и будущий Exact понижаются в один graph; существующие planner, scenes, reconciliation, input/focus, virtualization и hosts продолжают обслуживать результат. Второго renderer или отдельного runtime для v2 нет.

Альтернатива — breaking replacement всего Experience/host API — потребовала бы одновременной замены CA, live assets, Terminal и всех existing consumer, не устраняя отдельную задачу identity/publication. Она отклонена: generic sources и текущий runtime уже пригодны для расширения. Исключение: если старый overload не выражает отношение, framework не угадывает его. Он создаёт независимый node. Полный v2 consumer объявляет отношения явно.

Compatibility path ограничен одним понижением старых builder overloads в тот же graph. Старые overloads и `IUiSemanticSurfaceApi` v1 сохраняются как поддерживаемая совместимая API; отдельный дублирующий compatibility runtime не вводится. Legacy overload без nominal type/nullability создаёт независимый opaque node с прежними capabilities и source. Это постоянное ограниченное правило совместимости, в том числе для внешнего `.Inspect("Parcel", new UiConstantSource<MyRecord>(...))`; I01 его не удаляет. Opaque node не может участвовать в typed relations без явного descriptor, не получает выдуманную nullability и сохраняет существующие runtime проверки формы source. Strict pre-activation graph validation применяется к новым typed declarations и relations. Временной является только адаптация repository Flow/CA declarations: после миграции в U07/I01 удаляется их opaque authoring path и соответствующие private adapters; сам совместимый public overload остаётся. Новые Flow/CA модели сразу используют explicit typed declarations. Неоднозначность нового typed graph всегда даёт diagnostic, а не расширение legacy эвристики.

## Владельцы и область API

| Владелец | Добавление | Что остаётся за границей |
|---|---|---|
| Semantics | Data descriptors, immutable nodes/relations, graph diagnostics, explicit-ID binding | Никаких CLR source instances, UI host, game objects, произвольного исполнения C# |
| Experience | Typed source handles, graph authoring, publication owner и typed action declaration | Бизнес-фильтр, выбор полей и эффект команды задаёт consumer; geometry остаётся framework |
| Planning | Environment facets, coverage checks, bounded structured explanation | Не подписывается на Flow и не меняет sources |
| Runtime / Stardew | Снимок одной UI publication, bounded invalidation, async dispatch/generation ownership, host lifecycle | Не владеет Flow session и не откатывает доменные эффекты при reload |
| Tooling | Версионированный wire metadata и compiler/graph diagnostics | Не зависит от Experience assembly: получает экспортированный semantic contract |
| Flow Application / Persistence | Immutable read model, typed domain commands/results, revision/session semantics | Не зависит от UI и не экспортирует `Action<FlowRuntime>` |
| Flow semantic consumer / CA | Согласованная UI projection и explicit graph | Не создаёт собственную layout/input policy |

Новые assemblies и сторонние зависимости не нужны. Текущий .NET 6 runtime/.NET 8 SDK сохраняется. Frozen overlay contract v1 не меняется. Первый Flow UI использует уже реализованный optional `IUiSemanticHostApi.CreateSurface(..., Window, ...)`, открываемый из команды/кнопки Flow host; diagnostic fake mode виден в заголовке/status. При отсутствии optional host API Flow сообщает capability unavailable и не притворяется overlay над случайным native menu. Стандартный lifecycle `Create → Show → Closed → Dispose` остаётся единым.

Для U01 резервируется следующий UI producer version `1.0.0-alpha.32`: `.30` принадлежит Flow foundation `cdf9d2e`, `.31` объединяет его с ранее проверенными SDK/editor срезами и сохраняет оба API форм. Новая версия исключает использование прежнего `.30` из общего package cache. До package verification U01 синхронно обновляются все восемь producer packages, package authority/props, UI manifest/release follower versions и минимальная UI dependency всех affected consumer. Это локальная версия кандидата, не публикация. Перед интеграцией Flow сохраняется более новая общая authority и повторяются C/G/P на объединённом коде. В runtime-архиве единственный поставщик UI DLL — UI module; Flow semantic DLL добавляется в F12.

## Identity, alias, label и типы (U01)

`UiSymbolId` остаётся единственной identity nodes/actions/items. `Alias` — уникальное внутри соответствующего namespace Experience имя для authoring/DSL; `Label` — отображаемый, локализуемый текст, не ключ. Новый builder принимает explicit ID. Переименование alias/label не создаёт новый node и не меняет selection/focus/reload identity. Element/role IDs проверяются на валидность, принадлежность owner и глобальную уникальность внутри graph; alias namespace элементов и visual roles раздельный. Legacy overload вычисляет прежний canonical ID ровно один раз при объявлении.

Data descriptor отделён от `UiSemanticType`, который описывает presentation/visual значения DSL. Минимальный `UiDataType` содержит nominal `TypeId`, shape (`Scalar`, `Collection`, `Selection`, `Form`, `Validation`, `Action`) и явную nullability. Collection/selection указывают nominal item type. Action descriptor указывает input/result/target type, форму nullable target и capability. C# `UiSourceType<T>` связывает descriptor с CLR `T`; TypeId не выводится из label и не зависит от версии assembly. Встроенные string/bool/number/symbol types имеют foundation IDs; consumer record types получают owner-controlled IDs.

Reference nullability нельзя восстановить из `typeof(T)`: новые reference declarations указывают её явно. `Nullable<T>` проверяется вместе с descriptor. Required source с null, CLR/descriptor mismatch, scalar под collection capability и action с несовместимым target отклоняются до activation. Неизвестная nullability у legacy declaration не превращается в доказанную required/optional; typed relations с ней требуют явного уточнения. Framework копирует структуру graph/collections, но не клонирует произвольные mutable C# objects: immutable application values являются обязательством consumer и проверяются его тестами.

`UiSemanticGraph` содержит immutable nodes и relations. Node несёт ID, alias/label, data descriptor и capabilities. Projection node дополнительно содержит immutable `UiProjectionInput` slots: stable `Id` (child node ID), `Name`, `AcceptedType` (полный data descriptor) и `Required`. Relation содержит свой stable ID, kind, endpoints, optional `TargetInput` ID и provenance (C# declaration ID или DSL source span). Query/Filter обязательно указывают `TargetInput`; source output descriptor должен совпасть с slot AcceptedType, кроме допустимого required→optional. Required slot имеет ровно одно входящее relation, optional — не более одного; отсутствующий optional input не исполняется graph и обслуживается явно заданной consumer default policy. Дубли slots, отсутствующий/чужой input ID, два producers одного slot, required slot без producer, несовместимый nominal type/shape/nullability отклоняются. Остальные relation kinds используют собственные фиксированные проверки и не принимают TargetInput.

Пример CA Storages: slots `.../input/mode` принимают required nominal `StorageMode`, `.../input/category` — optional nominal `CategoryId`; соответствующие Filter relations связывают Mode и SelectedCategory с разными slot IDs одного target. Flow parcels имеет `.../input/query` required string. Slot metadata и TargetInput сохраняются в graph → binding context → schema-v2 export/import без вывода типов из labels. Acceptance U01 включает валидный round trip двух разных filter inputs и отрицательный import/bind после подмены slot/type/required relation. Binder возвращает отдельные адресные diagnostics с node/relation/input ID и endpoints; не бросает общую ошибку после регистрации host. Build с ошибкой не запечатывает builder, не подписывает источники и не активирует session.

| Relation | Направление и проверка | Исполнение |
|---|---|---|
| Selection | collection → selection | Collection item type совпадает с selection item type; selection представляет nullable stable item ID; target имеет Select. Runtime не выбирает объект по label |
| Details | selection → details | Details payload nominal type совпадает с выбранным item type; отсутствие selection допускается только nullable details. Consumer выбирает immutable value по ID |
| Query | query → collection | Query — string source с Search; target — typed collection с Browse; consumer объявляет query input collection projection и связывает тот же type |
| Filter | filter → collection | Source имеет Filter и явный value type; target projection объявляет совместимый filter input type. Не вводится неявное приведение enum/string/record |
| Validation | form → validation | Совпадает form schema/type; target — validation result этого form. Validator pure, сообщение/field ID адресные |
| Submission | form → action | Action input совместим с validated form payload; отсутствие mapping не считается преобразованием `UiFormSnapshot` в domain command |
| ActionTarget | action → target | Target ID существует, nominal target type/nullability и required capabilities совпадают; действие не получает объект renderer |

Дубликаты ID/alias/relation, отсутствующие endpoints, несколько конфликтующих selection owners, type/nullability/capability mismatch отклоняются. Data-dependency relations Selection/Details/Query/Filter/Validation образуют DAG; циклы запрещены. Submission/ActionTarget описывают намерения и не участвуют в data-evaluation DAG: успешное действие может опубликовать новое состояние без объявления запрещённого вычислительного цикла. Они всё равно проверяются на endpoints/type/capability и не допускают неявного повторного invoke.

## Полный путь identity и metadata

U01 меняет согласованно builder, `UiExperienceDefinition.CreateBindingContext`, `UiBindingContext` и tooling exporter/importer. Для elements и visual roles добавляются explicit-ID overloads; reconstruction использует сохранённый ID. Binder DSL уже получает resolved symbol ID и должен сохранить его в placements/assignments/provenance.

Schema-v1 import остаётся строгим: canonical `owner/element/name` и `owner/role/name`, duplicate/conflicting identity по-прежнему ошибка. Legacy contexts экспортируются в прежний deterministic v1 JSON. Schema-v2 вводится для независимых IDs и graph/type metadata; exporter выбирает v2, когда v1 не может выразить данные без потери, а importer никогда не подменяет ID вычисленным из alias. Unsupported schema даёт явную ошибку. Version tooling protocol и runtime API различаются. До активации v2 editor client получает advertised metadata versions; старый клиент вправе отклонить v2, но не принять его частично.

Обязательные U01 regression paths: explicit element и role ID → смена alias → Export/Import → Compile → тот же ID в IR; прежний canonical v1 byte round trip; прежние v1 conflicts/duplicates; unknown schema; неверный/cross-owner ID; valid Flow и package-only CA graph fixtures; все negative relations до activation. Минимальная v2 wire часть входит в U01, иначе новый публичный Export был бы сломан до T01. Полный planner trace/editor feedback остаётся T01.

### Реализация U01: alpha.32

Публичный builder получил отдельные `Element(id, alias, label, source, type, capabilities)`, `Source(...)`, `Input(nodeId, slot)`, `Relation(...)`, `Action(existingAction, alias, descriptor)` и `VisualRole(id, alias)`. `Element` запрашивает presentation, `Source` добавляет только наблюдаемый graph source. `UiSelectionSource` читает selected ID из существующего `IUiSelectableCollectionSource` и не создаёт второго владельца selection. Typed action metadata ссылается на тот же экземпляр `UiActionDefinition`, который передан в Actions group; подмена экземпляра с таким же ID отвергается до activation.

```csharp
var owner = new UiSymbolId("Example", "catalog");
var rows = new UiSelectableCollectionState<string>(
    new[] { "wood" }, value => owner.Child("item/" + value));
var selected = new UiSelectionSource(rows);
var experience = new UiExperienceBuilder(owner, "Catalog")
    .Element(owner.Child("items"), "Items", "Предметы", rows,
        UiSourceTypes.Collection(UiSourceTypes.String), UiCapabilities.Browse, UiCapabilities.Select)
    .Source(owner.Child("selected"), "Selected", "Выбранный предмет", selected,
        UiSourceTypes.Selection(UiSourceTypes.String), UiCapabilities.Select)
    .Relation(new(owner.Child("relation/selection"), UiRelationKind.Selection,
        owner.Child("items"), owner.Child("selected")))
    .Build();
```

Существующий `UiFormState` экспортирует `IReadOnlyList<UiSemanticFormField>`; `UiSourceTypes.Form(schema)` проверяет именно этот контракт. `UiValidationResult` хранит immutable schema/messages. `Submission.Mapping` явно описывает output consumer adapter; U01 не исполняет этот adapter и не добавляет вымышленный `UiFormSnapshot` или async dispatch. Их дальнейший контракт относится к U02/U03. Новые typed enum/record filters являются auxiliary sources до появления подходящего editable presentation; предъявленный Filter должен иметь поддержанный string source.

Flow Network использует независимые ID/alias/локализованные labels, typed immutable parcel collection, owner selection, nullable details payload и query/filter slots. Добавление Browse для Query/Filter включает действующую framework policy: History выбирает Gallery в Wide/Medium, List в Compact/Controller, generated Network pattern становится MasterDetail. CA объявляет Mode и Category как разные required inputs Storages, две owner selection relations и Open/Favorite ActionTarget. Пассивное старое CA Mode presentation пока использует совместимый opaque overload; private bridge удаляется в U07/I01 после появления typed choice authoring. CA перешёл на atomic publication в U02-a; Parcel и Network используют ту же boundary в U02-b.

Legacy canonical opaque names, включая ранее допустимые `0`, `A/B`, `A..B`, сохраняют ID и v1 wire. Новые explicit aliases используют dotted identifier grammar. Для независимых IDs, labels или graph metadata exporter выбирает v2; `[1,2]` объявляется tooling server до работы с документами. Неправильный graph не заменяет прежние bindings при refresh. Подробнее: [UI_AUTHORING.md](UI_AUTHORING.md).

Graph binder использует индексы и итеративный DAG pass; shared descriptor branches не обходятся экспоненциально при validation/equality/hash. Предельная глубина descriptor — 16 edges, wire expansion — 65 536 ненулевых entries, JSON depth — 32; превышение даёт явную ошибку. Tooling считает все nodes/inputs/relations/roles в пределах 4 096 symbols/context и 16 384/session. На draw/layout/dispatch новые graph обходы не добавлены. Результаты проверок и ограничения фиксируются отдельно в [ROADMAP-STATUS.md](ROADMAP-STATUS.md).

## Согласованное состояние (U02)

Три идентичности различаются: `Flow SessionId + Revision` у домена; `UiPublicationId + Version` у projection; `UiGenerationId` у runtime. Новая игровая session создаёт новую publication owner. UI reload может заменить generation, сохраняя publication/domain session; save switch никогда не переносит save-specific data.

`UiPublication` принадлежит owning UI thread. Consumer сначала строит immutable candidate значений всех связанных sources. `Commit` проверяет batch целиком, затем заменяет одну publication view и только после этого уведомляет. Чтение source любым observer во время notification видит весь новый batch. Ошибка подготовки сохраняет старое состояние; ошибка observer не откатывает опубликованное и не лишает остальные subscriptions уведомления. Reentrant commit запрещён с адресным результатом. `Dispose` терминален и снимает subscriptions. Runtime захватывает одну view на update и использует её в draw/layout; scene не читает новый live collection под старой revision.

Scalar/collection sources имеют монотонные version. Collection change содержит `BaseVersion`, `Version` и typed Insert/Remove/Move/Update либо Reset. ID уникальны; индексы относятся к последовательному candidate после предыдущей операции batch. Повтор идентичного уже применённого change — no-op; старый конфликтующий change отклоняется; gap требует полный Reset. Без ожидания бесконечной очереди: максимум 128 операций в batch, по умолчанию 64 retained delta batches на collection; медленный consumer получает текущий immutable snapshot/Reset. Общая source replacement остаётся поддержана для небольших данных.

Update-only publication сохраняет порядок/indices и повторно проецирует только итоговые значения затронутых элементов, в порядке их индексов. Identity и required-null проверяются у каждой последовательной операции, включая заменённые позднее. Consumer задаёт immutable payloads и стабильные callbacks для текущей projection policy; изменение общего форматирования/локали требует Reset/Replace. Эквивалентная explicit delta всё равно продвигает source version/history, сохраняя прежний content revision. Внутренние private leaves по128 элементов допускают копирование только затронутых blocks и массива ссылок без цепочек предыдущих snapshots.

Content revision учитывает payload/icon/text изменения; существующий hash label/supporting text сохраняет роль measurement key и не выдаётся за monotonic domain version. Reorder сохраняет selected ID и stable scroll/focus anchors. Удаление selection очищает её и связанные details в том же publication; runtime focus переносится на существующий соседний item по прежнему index (или на collection container при пустом списке), scroll anchor — на ближайший surviving predecessor/first item. Повтор delta не вставляет дубликат. Невалидная delta не меняет publication и не выдаёт Changed.

Первый сквозной consumer — CA projection: mode/category/status/categories/storages переключаются одним batch, а не последовательными notifications. Второй — Flow read model целиком. Acceptance проверяет наблюдение из callback, пропущенный/повторный change, reorder/removal/reset и отсутствие смешанного кадра; PERF использует фиксированные 100/1000/10000 item fixtures, сохраняя действующие UI budgets.

### Реализованная часть U02-a

В alpha.33 доступны `UiPublication`, `UiPublishedState<T>`, `UiPublishedCollection<T>`, `UiPublishedSelectableCollection<T>` и immutable `UiPublicationView`. Все sources объявляются до первого Capture/BeginUpdate. Nominal CLR registry общий для publication; невалидная регистрация не занимает ID. Пример согласованного обновления:

```csharp
var owner = new UiSymbolId("Example", "catalog");
using var publication = new UiPublication(owner);
var items = publication.SelectableCollection(owner.Child("items"), new[] { "wood" },
    UiSourceTypes.String, value => owner.Child("item/" + value));
var details = publication.State(owner.Child("details"), "Wood", UiSourceTypes.String);
UiPublicationView before = publication.Capture();
UiPublicationResult result = publication.BeginUpdate()
    .Replace(items, new[] { "stone" })
    .Select(items, owner.Child("item/stone"))
    .Set(details, "Stone")
    .Commit();
// При успешном commit callbacks читают stone/Stone вместе; before продолжает хранить wood/Wood.
```

Batch явно читает draft через `Read`; обычный `source.Value` до commit возвращает прежнее committed значение. Invalid/stale/foreign/reentrant/wrong-thread/disposed commits не меняют view. Ошибки подготовки, включая consumer equality/projection callbacks, делают весь batch невалидным. Observer errors доступны в результате и не откатывают commit. Явный Dispose во время notification завершает оставшуюся доставку и снимает subscriptions; уже committed view остаётся читаемой. Consumer request adapter проверяет lifecycle/reentry **до** внешнего эффекта, как в CA.

`UiCollectionChange<T>` задаёт final selection вместе с 0–128 typed operations. Один explicit change на source/batch не смешивается с Replace/Select того же source; несколько операций помещаются в сам change. Reset — единственная операция своего change; он может восстановить пропущенные версии. Повтор сверяется с bounded history, а retired/conflicting replay требует нового Reset. `ReadChanges(afterVersion)` возвращает доступную последовательность либо текущий immutable Reset; capacity от 1 до 64, default 64. Структуры/payloads копируются при подготовке; это не обещание O(1) materialization целого нового snapshot. Обычный capture переиспользует готовый объект, selection не перепроецирует items.

`IUiVersionedSemanticSource.Version` есть у новых published и совместимых legacy scalar/collection sources. Captured scalar/selection/collection сохраняют source version. Collection metadata `Revision` по-прежнему игнорирует selection-only changes; `UiSemanticCollectionItem.ItemRevision` учитывает payload/icon/text, а прежний `ContentVersion` остаётся measurement hash. `IUiPublicationReadableSource` позволяет request facade читать именно переданную view без повторного live capture и без provider calls.

Runtime захватывает все объявленные publications и nested form input/validation owners до форматирования. Старые scenes сохраняют значения, order/selection и lookup. Ввод пишет живому owner и затем повторно составляет сцену; сохраняется действующий host/planner/runtime. Legacy external collection без capture отклоняет чтение под изменёнными count/revision до live getter. Источник без metadata не даёт гарантии обнаружения изменения payload при прежнем count; его прежний запрет mutation во время layout остаётся обязательным.

Сквозной CA consumer завершён и проверен на exact packages. Flow ParcelExperience публикует backing snapshot, cargo/route/state/availability и pending command result в одном batch на следующем Pump; ошибка подготовки сохраняет прежний view для retry, а retirement прекращает внешние callbacks и publication. NetworkExperience публикует backing model, history/selection/details, route/recovery, cached inventory, формы и result одним batch. Supplementary owner reads проверяются по session/revision/state и notification epoch; обычный domain Pump не перечитывает physical inventory. Выбор source, явный refresh и post-send refresh включают inventory в тот же candidate. Последний ввод каждого из пяти полей и bounded page intent сохраняются до успешного commit, включая смену доменной ревизии и отказ подготовки. Runtime в кандидате alpha.35 реализует stable focus/scroll fallback для uniform/adaptive collections, положительный empty selectable container и явный navigation reveal без passive-wheel auto-scroll. Known index сохраняется и для внешних uniform sources без reverse lookup. Rendering/accessibility при reuse layout используют текущий captured selection и reconciled focus; bounded scene policy разрешает максимум16 state combinations без повторного owner capture. Чистый focus рисует border без заливки, в том числе на пустом container, а refill переносит его на первый item. Старый порядок для scroll predecessor читается только из immutable capture; без этой capability используется первый item. U02 **DONE**, implementation `356e52e`, alpha.36: Update-only path проецирует/валидирует только затронутые final values и копирует immutable root/touched blocks. Structural/Reset preparation остаётся O(N), а Update root copy — O(N/128); измеренные ограничения сохранены. Evidence: [ROADMAP-STATUS.md](ROADMAP-STATUS.md#u02-e-bounded-update-preparation-and-u02-acceptance).

## Действия и lifecycle (U03, R01)

`UiAction<TRequest,TResult>` получает immutable request и `CancellationToken`, возвращает typed result: success, rejected (code/message/field), failure (observed diagnostic), cancelled. State отдельно отражает Available/Disabled/Running/Completed/Rejected/Failed/Cancelled. Availability содержит причину; повтор invoke подчиняется объявленной policy RejectWhileRunning, RestartLatest или bounded Queue (capacity обязательно задана). FIFO сверх capacity возвращает rejection, не растёт неограниченно. UI state и итоговые callbacks меняются только owning thread через runtime dispatcher.

Каждый invoke захватывает publication/session identity и generation lease. Late completion после Close/Dispose/reload/save switch наблюдается и освобождает ресурсы, но не меняет новый UI. Cancellation не откатывает уже committed Flow command; typed domain result остаётся авторитетом. Синхронный `UiActionDefinition` адаптируется как unit-input/unit-result action с синхронным completion в общий lifecycle. Доменная idempotency не реализуется счётчиком UI и не заменяется отключением кнопки.

U03-a, alpha.37: добавлены immutable `UiAction<TRequest,TResult>`, typed outcomes/availability и internal Runtime dispatcher. Описание не хранит состояние host. Один dispatcher фиксирует session/publication identity и generation identity, проверяет owning thread и владеет максимум 4096 bindings. На action хранится одна active operation и максимум 128 ожидающих запросов; capacity Queue обязателен и относится только к ожидающим. RestartLatest — последовательная policy: немедленно запрашивает отмену active, завершает вытесненные UI submissions как Superseded и хранит только последний запрос. Новый executor начинается после фактического завершения предыдущего; noncooperative operation может задержать latest, но не создаёт дополнительные active tasks.

Pending submissions получают terminal Cancelled при отмене/retirement. Отмена текущего запроса сама по себе не заменяет успешный typed domain result; cancellation exception считается отменой только для запрошенного токена этого execution. Background observer удерживает только mailbox/result/cancellation resource, наблюдает exceptions и освобождает CTS даже без следующего Pump. UI callbacks выполняются на owning thread; callback/reentry/availability/cancellation errors не запускают второй доменный эффект. На один Pump приходится максимум один queued start для каждого action; очередь FIFO не допускает обгона новым Invoke. Retirement сначала убирает completion callbacks и ожидающие запросы, затем отменяет operation. Foundation пока не подключена к production input, legacy adapter и Stardew pump/reload: это следующий срез U03-b. Полный U03 остаётся IN_PROGRESS; RUNTIME acceptance ещё не выполнена.

U03-b, alpha.38: обычная перекомпоновка Terminal сохраняет committed Experience, включая Transient. Focus/text/profile/locale/theme и обновление assets заново планируют и рисуют ту же модель; фабрика вызывается при явном открытии. Новый раздел заменяет активную модель только после принятого host update. Недоступность раздела проверяется заново, неуспешный переход сохраняет прежнюю модель, Dispose освобождает ссылку на неё; ownership Cached/Singleton и публичная Transient activation policy не меняются. Это предпосылка сохранения per-host action execution между UI updates, а не уже подключённый async binding.

U03-b, alpha.39: Runtime execution можно отдельно retire без закрытия sibling actions. Его non-pumping RefreshAvailability готовит причины доступности и CanInvoke с учётом concurrency/queue capacity; disabled и availability-fault recovery восстанавливают доступность. InvokeCaptured вызывает request factory один раз после admission guards и до queue/start, наблюдает исходную ошибку подготовки, проверяет retirement/reentry и сохраняет snapshot до queued start. Capture не выполняется для unavailable/busy/full/retired input. Это private seam для production host binding; сам binding и Update pump/lifecycle wiring ещё не подключены.

R01 оформляет ownership manifest generation, R02–R04 — bundle/migration/multi-host transaction. U03 не обещает готовность полного multi-host reload: он обязан предоставить fencing и fault-observation primitives, которые будут использовать R01/R04.

## Первый Flow example и независимый F11

F11 не зависит от новых UI типов. Его минимальный контракт:

```csharp
// Согласованная форма boundary; реализацию проверяет F11.
public interface IFlowApplication
{
    FlowSnapshot ReadSnapshot();
    FlowCommandResult Execute(FlowParcelCommand command);
    event Action<long>? RevisionChanged;
}
```

`FlowSnapshot` содержит SessionId/NetworkId/Revision/State, provider mode (`DiagnosticFake`/`GameInventory`), объявленные supported operations и immutable arrays of station/link/parcel values. Каждый parcel содержит per-action `FlowActionAvailability(Action, Available, Code, ReasonKey)`. `Available=true` не содержит rejection; `false` обязательно содержит стабильный domain code и localization key причины. Минимум F11 — `Reserve`: доступность определяется владельцем domain/admission state. Согласованный enum последующих существующих операций: `Reserve`, `Cancel`, `RetryDelivery`, `ReconcileTransfer`, `ReturnToSource`; пока операция не поддерживается, availability явно возвращает `UnsupportedAction`, а UI не выводит её из ParcelState. Session capability также сообщает `Paused`, `RecoveryRequired`, `SessionClosed` или `Faulted`, если они запрещают команды. Причины provider/capacity/станций формируются у domain/application owner, а UI только локализует ReasonKey с fallback по Code.

`FlowParcelCommand` содержит SessionId/ExpectedRevision/ParcelId и Action. Result различает Applied/Rejected/Conflict/InvalidCommand/SessionClosed/Faulted, содержит текущую Revision и для отказа те же Code/ReasonKey (включая StaleRevision/StaleSession). DTO не содержит Parcel, inventory или callbacks. Базовые имена совпадают с доступным кандидатом соседней Flow-задачи; его текущий action bitmask ещё не заменяет причины availability. Дополнение DTO и проверка соответствия всем условиям F11 обязательны при интеграции. F13 расширяет player-facing набор команд только в пределах реально поддержанной domain semantics.

Чтение и команды выполняются на owner thread. Consumer подписывается до initial read и перечитывает revision при необходимости; change notification — dirty flag, который coalesce до одного update. F11 сохраняет semantic domain operation/recovery idempotency; UI повтор с устаревшей revision возвращает Conflict. Observer exception не откатывает успешно сохранённую команду и не допускает reentrant mutation. Closed/Faulted session fence делает callbacks и команды старого token недействительными. Это acceptance F11, не утверждение о готовности текущего candidate.

Конкретная read-only projection для M1:

| Stable node ID (под owner `Hatifect.Flow/network`) | Alias / typed value | Relations и consumer policy |
|---|---|---|
| `element/parcels` | `Shipments`: collection of immutable `FlowParcelSnapshot` | Browse; item ID из ParcelId, label локализуется |
| `element/selection` | `SelectedParcel`: optional stable item ID, item type `FlowParcelSnapshot` | Select; parcels → selection |
| `element/details` | `Details`: optional `FlowParcelSnapshot` | Inspect; selection → details |
| `element/query` | `Search`: required string | Search; query → parcels; filter formula принадлежит projection |
| `element/status` | `SessionStatus`: required localized status | Monitor; Active/Paused/RecoveryRequired/Closed/Faulted объясняются явно |

Один `ReadSnapshot()` превращается в candidate: отфильтрованный список, selected ID (если он ещё есть), соответствующий details и status. Затем один UI Commit. Парсинг UI alias не определяет ParcelId. При A→B→A новый SessionId создаёт новую projection/subscription, старая disposed. Первый read-only экран не ждёт async actions, Quick, Exact или полного каталога.

CA mapping U01/U02: Mode/Category filter → Storages; Categories → SelectedCategory; Storages → SelectedStorage; Open/Favorite action → SelectedStorage. IDs берутся из существующих provider keys, не labels. Candidate сначала нормализуется и валидируется, потом публикуется целиком. Сохраняются native handoff, capabilities, close/reopen и отсутствие private runtime доступа. U01 включает graph fixture и собираемость exact-package consumer; I01 завершает продуктовую миграцию/runtime matrix.

## Следующие обязательные срезы и acceptance

1. U01: graph/types/identity/binding/wire целиком, Flow immutable read-model fixture и CA fixture; негативные отношения до activation; C/U/P. До DONE нельзя оставить alias ID loss в Export или объявить только data classes готовым graph.
2. U02 и U04: publication/deltas и environment/coverage planner независимо используют U01. U04 facets: viewport, scale, pointer/keyboard/controller, locale, theme, reduced-motion/high-contrast; determinism и bounded rejected-alternative trace обязательны. Unsupported combination даёт адресную diagnostic/fallback, не исчезновение поля/action.
3. F11: интеграция готового application candidate с проверкой всех вышеописанных инвариантов и F gate. После U02/U04/F11 — F12 с release inventory/host/runtime, затем следующий dependency-ready ID по roadmap.

B02 меняет спецификацию, не C# API/persistence/host. Его C gate проверяет существующую базу; U/P/G/RUNTIME/VISUAL/PERF не объявляются выполненными за будущий код. Новые numeric limits publication относятся к bounded contract U02 и должны быть проверены тестами; существующие performance acceptance JSON не меняются. Полная roadmap, включая выбранный editor, Exact, component catalog, extension API и runtime/manual acceptance Q03, сохраняется без сокращения.


U03-b, alpha.40: Runtime.Update подготавливает layout, interaction, render frame и accessibility до принятия сцены. При отказе callback остаются прежние согласованные snapshots и тот же объект input session; повторный вход и изменение input во время подготовки запрещены до эффектов. Отклонённая перестановка или удаление коллекции не становятся исходным порядком для следующего scroll-anchor transition. В штатном Update не копируются measurement caches или adaptive height index; после неуспешной подготовки cache может быть холодным. Это внутренняя граница принятия сцены. Terminal validation/retirement, typed binding и owning Update pump продолжают U03.
