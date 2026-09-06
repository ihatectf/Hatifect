# U03/U04: общая интеграция alpha.46

Статус общей приёмки — **IN_PROGRESS**. Frozen handoff UI — [`b6f60b4`](https://github.com/ihatectf/Hatifect/commit/b6f60b49af5c7f94469bab4021ab12f2b61acd88), immutable source [`0cd356f`](https://github.com/ihatectf/Hatifect/commit/0cd356f0fd252a0744fe6a871e46ba631193f876), implementation `902d834`. Основа GQ — опубликованная alpha.45 `12e7aab` с успешным exact CI34058056333 во всех 10 jobs. Все 17 version authorities уже переведены на alpha.46 владельцем UI; повторного изменения версий при интеграции нет.

## Область и контракт

Успешный active reload принимает assets, invocation, environment и scene до cancellation callbacks старого поколения. Rejected preparation сохраняет принятую scene, assets и pending root/portal work. Неизменённые документы не создают новое поколение. Window, Terminal, HUD и active-menu hosts передают один immutable environment через Invocation, Planning и composition; environment-only recomposition сохраняет модель и pending actions.

Кандидат theme/composer остаётся закрытым до принятия. Native owner, configuration и accepted scene/theme versions проверяются после callback-bearing preparation; устаревший внешний кандидат не перезаписывает принятую вложенную операцию. Retirement фиксируется перед fallible cleanup, поэтому оставшиеся event/watch delegates не доставляют уведомления закрытому владельцу; cleanup можно повторить. Изменены внутренние Runtime/Stardew seams. Opaque surface API v1 и Flow Core/Persistence не меняются.

## Проверка входящего checkpoint

GQ прочитал delta всех 20 изменённых source/test files, включая четыре Runtime test files, native fixtures и lifecycle checks. Все 48 source/version postimages сверены с immutable Git и frozen owner worktree; отдельные 17 non-Markdown version changes содержат только alpha.45→alpha.46. Version commit также добавляет один документационный абзац; финальный handoff меняет только три Markdown-файла. Все прежние 32 scenario objects сохранены, добавлены ровно `semantic.actions.reload` и `semantic.environment`. Исходные U03/U04 acceptance paragraphs остались без изменений.

Повторно вычисленные GQ owner audits совпали с retained results: C1528 .NET +366 Python, G1801 +366, все 17 actual TRX; P90, 44 projected files, 8 exact package DLL, 3 isolated assets/cache entries и 2 deployed CA DLL. Native reload `57ee8df6-0147-4417-9a1d-e5bd8376612a` — PASS18, environment `26d7b6c8-a88c-4df9-a60e-b2e9028e9b26` — PASS25. Fingerprint `a5eed91fe33c86c54c7cfda90d796f4aa40934c6da5edd1760ef606fae06bc06` пересчитан по игровой поставке; все 8 package/producer/game DLL совпадают. Options original bytes восстановлены, оба процесса завершились с exit0, обе рабочие копии удалены. Открытых замечаний в этом ограниченном review нет.

Артефакты GQ: `artifacts/alpha46-integration-preflight/owner-final-audit.json` и заново вычисленные audits в `owner/`. [Отчёт UI](U03_U04_HOST_ACCEPTANCE.md) сохраняет owner commands, RED→GREEN и отдельное заключение reviewer Kepler. Owner results не подменяют собственные проверки объединённого GQ worktree.

## Общие проверки и ограничения

Merge frozen handoff поверх alpha.45 прошёл без конфликтов, все 48 исходных postimages сохранены. Предстоят `./tools/hatifect-check`, `./tools/hatifect-check --platform`, `./tools/hatifect-isolated-ui-ca --keep`, подготовка exact game delivery и свежие canonical reload/environment, UI/CA aggregate, Flow/isolation и native PERF. Ожидаемые C/G totals — 1528/1801 .NET +366 Python, Runtime478; PASS появится только после фактического выполнения и сверки source/artifact identities. Артефакты общей приёмки сохраняются в `artifacts/alpha46-integration-preflight/`.

Automatic Window/Terminal environment может обновиться через native Update или существующую Draw-time viewport synchronization; Update-only подготовка здесь не доказана. Нулевые allocations относятся к 256 неизменным public Synchronize на владельца, а не к полному frame/Update/Pump. Полные U03/U04/F12, actual action save-switch, concrete typed consumer/messages, consumer localization и representative PERF остаются открытыми. Следующие владельцы согласованы: UI — actual A→title→B с pending root/portal actions в отдельном worktree; FLOWLINE — live text/Parcel projection после принятия host checkpoint. Физический Backspace этим срезом не закрывается.
