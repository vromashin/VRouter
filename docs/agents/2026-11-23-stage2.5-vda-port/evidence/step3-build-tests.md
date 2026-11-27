# Шаг 3 (перенос автомата и трафика): подтверждение сборки и тестов

Репозиторий: `C:\Users\User\source\repos\VR.RoutePlanner`
Ветка: `ai/stage2.5-vda-port-step3` (от `ai/stage2.5-vda-port`, база `5d7df3d`)
Коммит: см. отчёт разработчика.

## Сборка Release

```
dotnet build VR.RoutePlanner.sln -c Release --nologo -v q
Сборка успешно завершена.
    Предупреждений: 0
    Ошибок: 0
```

## scripts/test.ps1 (живая база rms_vrp_test)

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
Пройден! : не пройдено 0, пройдено  13, пропущено 0, всего  13 — Tests.Contracts
Пройден! : не пройдено 0, пройдено   1, пропущено 0, всего   1 — Tests.Load
Пройден! : не пройдено 0, пройдено   1, пропущено 0, всего   1 — Tests.Simulation
Пройден! : не пройдено 0, пройдено 309, пропущено 0, всего 309 — Tests.Unit
Пройден! : не пройдено 0, пройдено  44, пропущено 0, всего  44 — Tests.Integration
EXIT=0
```

## Новые тесты шага 3

- `tests/VR.RoutePlanner.Tests.Unit/Traffic/TrafficReservationRegistryTests.cs` — 12 сценариев:
  эксклюзив, идемпотентность владельца, `Release` только своим, емкость `PARALLEL`,
  направление, атомарность набора, `INDEPENDENT` и узловой `EXCLUSIVE` не резервируются,
  `CONFLICT_ZONE` резервируется, TTL и грейс после проезда.
- `tests/VR.RoutePlanner.Tests.Unit/Traffic/TrafficHorizonCalculatorTests.cs` — 8 сценариев:
  `sequenceId = 2 * index`, границы горизонта, ребра, `TRAFFIC_NO_RESOURCE` по ребру,
  виртуальное стартовое ребро.
- `tests/VR.RoutePlanner.Tests.Unit/Traffic/TrafficCoordinatorTests.cs` — 6 сценариев.
- `tests/VR.RoutePlanner.Tests.Unit/Robots/RobotOrderStateMachineTests.cs` — переходы 1–8,
  11, 16 таблицы §3.1, АС-8 (`MergeNodes` при пустом `lastNodeId`), АС-9
  (`PROGRESS_TIMEOUT` от последнего прогресса), АС-4 (одна публикация продления),
  освобождение резервов и `TRAFFIC_BLOCKED` без перевода заказа в `Failed`.
- `tests/VR.RoutePlanner.Tests.Unit/Robots/RobotLastNodeResolverTests.cs` — 3 сценария.
- `AgvControllerTests.CreateOrder_TrafficBlocked_Returns409`.

## Что требует живого прогона владельцем

1. `vrt_TrafficResource`/`vrt_TrafficResourceNode`/`vrt_TrafficResourceEdge` для карты 801
   и профиля `tusk-forklift-1102` заполнены прогревом (`Warmup`), иначе `ITrafficResourceLookup`
   вернёт `null` и выдача заказа пойдёт с `TRAFFIC_NO_RESOURCE` в журнале.
2. Реальное распределение двух роботов по узкому коридору: второй заказ получает 409.
3. Восстановление резервов после рестарта `Workers` (состояние в памяти, `vrt_*` резервов не хранит).
4. `Workers` должен быть единственным владельцем трафика: `Api` получает `NoOpTrafficCoordinator`.
