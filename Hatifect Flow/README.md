# Flowline

Flowline — транспортная подсистема Hatifect. `Hatifect.Flow.Core` содержит модель и выполнение; `Hatifect.Flow.Persistence` сохраняет состояние и проверяет идентичность сейва. Игровой `Hatifect.Flow` host владеет lifecycle SMAPI. Домен не зависит от UI и конкретных сторонних модов.

Эта база сохраняет текущие десять инкрементов: очереди и операции, deterministic fake-provider execution, pause/recovery, persistence envelopes и изоляцию сейвов. Реальные inventory adapters и игровая production transport session ещё не реализованы. `Hatifect.Flow.UI.Semantic` содержит отдельный read-only ParcelExperience и пока не подключён к runtime host.

Проверка из корня: `./tools/hatifect-test flow`. При изменении host: `./tools/hatifect-check --platform`. При отдельной runtime-задаче доступны `flow.route.basic` и `flow.save.isolation` через изолированный harness.

Следующий интеграционный этап — application snapshots, команды и revision-уведомления для UI; он описан в [архитектуре](../ARCHITECTURE.md).

[Полная roadmap](../docs/ROADMAP.md) продолжает десять инкрементов работами F11–F20: application boundary, read-only host, управление fake session, реальный provider, production session и UI перевозок. Она связывает этот путь с модернизацией UI и отдельно фиксирует необходимость доказать согласованность inventory с игровыми сохранениями.
