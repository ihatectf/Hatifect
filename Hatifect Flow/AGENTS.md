# Flowline

Прочитай корневые инструкции и `README.md`, затем owning Core/Persistence/host код и соседние поведенческие тесты. Модель десяти инкрементов — действующая реализация; не заменяй её старой transport/pipe моделью.

Core владеет идентичностью объектов, состояниями операций, очередями, routing и dispatch. Persistence владеет envelope, совместимостью сохранения, recovery и проверкой идентичности сейва. Stardew host владеет игровым lifecycle. Core/Persistence не читают UI, игровые inventory или конкретный сторонний API.

При изменении перехода состояния проверь повторную команду, отказ provider, прерывание/восстановление и изоляцию сессии/сейва. Не теряй и не дублируй операции при retry/reload. Детерминизм и ограничения обходов должны следовать из кода и проверяться конкретными состояниями.

`Hatifect.Flow.UI.Semantic` пока содержит отдельный read-only ParcelExperience. Production inventory adapters, transport session и полноценная связь с UI ещё требуют реализации. Следующий application boundary проектируй как snapshots для чтения, команды для действий и revision-уведомления. Состояние отображения не становится источником истины домена. Сначала зафиксируй контракт и миграцию; не представляй план как существующий API.

Команда из корня: `./tools/hatifect-test flow`; для host — `./tools/hatifect-check --platform`. Runtime `flow.route.basic` и `flow.save.isolation` относятся к изолированному fake-provider/lifecycle harness и не доказывают готовность реальных перевозок. Не меняй реальные сейвы пользователя.
