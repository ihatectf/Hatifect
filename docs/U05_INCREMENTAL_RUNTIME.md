# U05 — инкрементальный runtime

Полный U05 — **IN_PROGRESS**. Зависимости U02/U04 сверены с их implementation и общей приёмкой; U05-a **DONE в owner-ветке**: повторное использование измерений adaptive collection подтверждено; общая интеграция выполняется отдельно.

## U05-a: локальное изменение коллекции

Owning layer — UI Runtime. Раньше любая source revision очищала все measurement entries: изменение одного невидимого элемента повторно измеряло неизменившиеся видимые строки. Теперь owner replacement очищает cache, а revision сохраняет измерения. Индекс высот по-прежнему инвалидируется на revision, поэтому переиспользование не скрывает изменение геометрии.

Ключ содержит stable item ID и geometry context (icon presence, width, profile, locale, theme/typography, sizing/density). Cached value содержит version и exact label/supporting text: hit подтверждается ordinal equality, включая внешний source с отсутствующей или ненадёжной ContentVersion. Текст не хешируется в dictionary key; для обычного immutable item сравнение завершается по той же ссылке. Изменившееся содержимое заменяет value того же item/geometry slot.

LRU сохраняет прежнюю ёмкость 1024, exact rows 512; owner replacement сбрасывает cache, inactive collection удаляет owning state. Geometry variants остаются раздельными. Public API, persistence и consumer policy не меняются.

Implementation commits: [`8e0da27`](https://github.com/ihatectf/Hatifect/commit/8e0da2747844d9027e063f9b38b7cacad43ff444) — исходный срез; [`f0f68cf`](https://github.com/ihatectf/Hatifect/commit/f0f68cfcdcfaf7110f1b1841b25c5016829e4ecd) — исправление independent review. Между ними отдельный policy commit 6fa1c47 (=79e65e8).

## Проверки

Канонические команды: `./tools/hatifect-test ui --project 'Hatifect UI/tests/Hatifect.UI.Runtime.Tests/Hatifect.UI.Runtime.Tests.csproj'`, `./tools/hatifect-check`, `./tools/hatifect-check --platform`, `./tools/hatifect-isolated-ui-ca --keep`, `./tools/hatifect-ui-test semantic.performance`. Build/test используют проверенный x64 SDK8, указанный через HATIFECT_DOTNET/HATIFECT_TEST_DOTNET; для .NET test процессов установлен DOTNET_gcConcurrent=0. Этот override не передаётся native game процессу.

- Исходный RED run-j70lfcsv:529/531, два ожидаемых провала точного числа повторных измерений.
- Первоначальный GREEN run-vn5uk7h_:531/531; C run-31_8g7o8:1626.NET+381Python; G run-7m_cafra:1955.NET+381Python; P wu5nln33:101. Эти gates относятся только к 8e0da27 и не заменяют новые.
- Sol review обнаружил P2: text hashing и historical text/version entries. Исправлено f0f68cf; повторный source review finding closed.
- Fresh Runtime run-jh3xjyw5:532/532, включая cached offscreen mutation -> scroll-back, актуальную height/text и anchorY.
- C `run-35b4kxap`: **PASS 1627 .NET + 386 Python**, семь TRX и individual results сверены.
- G `run-3e9_t_ty`: **PASS 1956 .NET + 386 Python**, десять TRX и individual results сверены.
- P `emkky442`: **PASS 101**, 46 projection files, восемь exact packages, две CA DLL; UI source отсутствует. Все 1444 файла retained projection/evidence скопированы с проверкой SHA256.
- Native `semantic.performance`, request `18887925-3d97-43f2-b095-274373f02db9`: **PASS 2**, 620 frames; p95 **0.063374 ms**, p99 **0.447958 ms**, **5337.870967741936 B/frame**; measure/arrange miss ratio **0.0016129032258064516**. Стандартные budgets соблюдены; это не сравнительный benchmark collection deltas.
- Native request содержит source `f0f68cfcdcfaf7110f1b1841b25c5016829e4ecd`; runtime fingerprint **`00d0d842ff9ab733a0b697cfeff85bda7d057916ab7aa1cb3e6e13066ee97609`** пересчитан по deployed и retained runtime. Все восемь package/producer/game DLL совпали; retained producer DLL и полный game UI module сохранены. Process 76454 завершился с exit 0, teardown/errors/exceptions пусты; options Restored, временные файлы отсутствуют. SavePath=null, title-only workload.
- Тихое окно подтверждено GQ и FLOWLINE. Десять process observations содержат собственные prepare dotnet до launch, затем только game 76454 во время сценария и пустой набор после exit. Executor штатно остановлен.

Постоянные raw/audits в owner worktree: `artifacts/validation/run-35b4kxap`, `artifacts/validation/run-3e9_t_ty`, `artifacts/runtime/18887925-3d97-43f2-b095-274373f02db9`, `artifacts/u05-incremental-runtime/{c-f0f68cf-audit.json,g-f0f68cf-audit.json,isolated-p-f0f68cf-audit.json,isolated-p-f0f68cf,producer-game-f0f68cf,native-f0f68cf-audit.json,perf-18887925-process-observations.json}`. Эти evidence не подменяют результаты будущего общего candidate.

Authoring failures сохранены: run-xpjjgm2c CS7036 при изменении общего MeasuredItem; исправлено отдельным cached wrapper. run-d49exhoh CS1674 — fixture использовал using для session без IDisposable. run-yq6k8_0j:531PASS/1FAIL — fixture передавал absolute0 в delta-based ScrollCollection; исправлено -currentOffset. Эти провалы не обозначены как production regressions.

Политика Luna/Sol применена отдельно: portable agent-check PASS; --host BLOCKED «Codex rejected config/read», личные настройки не менялись.

## Ограничения и следующий шаг

Срез не доказывает полную инкрементальность scene reconciliation, отсутствие source scans во всех hosts или lifetime всей generation. Native semantic.performance — существующий Terminal diagnostic workload, не динамический collection-delta benchmark. Full U05 требует отдельной исходной acceptance matrix. Следующий готовый участок — ограничить allocation height index при revision большой коллекции, сохранив prefix/anchor/eviction correctness.
