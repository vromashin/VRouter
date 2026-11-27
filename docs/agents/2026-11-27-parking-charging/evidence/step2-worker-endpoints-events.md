# Доказательства: шаг 2 спеки parking-charging (воркер, эндпоинты, событие)

Репозиторий `VR.RoutePlanner`, ветка `ai/charging-parking`, коммит `2a2223e`.

## Сборка

```
dotnet build VR.RoutePlanner.sln -c Release --nologo -v q
```
Результат: exit 0, предупреждений 0, ошибок 0 (в выводе нет строк с `warning`/`error`).

## Тесты

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
```
Хвост вывода (банк `rms_vrp_test` подключен, ничего не пропущено):

```
Пройден!   : не пройдено 0, пройдено   1, пропущено 0, всего   1, длительность  3 ms. - VR.RoutePlanner.Tests.Load.dll
Пройден!   : не пройдено 0, пройдено   1, пропущено 0, всего   1, длительность  3 ms. - VR.RoutePlanner.Tests.Simulation.dll
Пройден!   : не пройдено 0, пройдено  13, пропущено 0, всего  13, длительность 60 ms. - VR.RoutePlanner.Tests.Contracts.dll
Пройден!   : не пройдено 0, пройдено 410, пропущено 0, всего 410, длительность  1 s. - VR.RoutePlanner.Tests.Unit.dll
Пройден!   : не пройдено 0, пройдено  44, пропущено 0, всего  44, длительность 30 s. - VR.RoutePlanner.Tests.Integration.dll
```

Отдельный прогон новых тестов:

```
dotnet test tests\VR.RoutePlanner.Tests.Unit\VR.RoutePlanner.Tests.Unit.csproj -c Release --filter "FullyQualifiedName~Relocation|FullyQualifiedName~Charging"
```
Результат: `не пройдено 0, пройдено 70, пропущено 0, всего 70`.

## Что нельзя проверить в роли разработчика

1. **Заказ в MQTT и в `vrt_RobotOrder`.** Юнит-тесты работают на подстановках `IVdaOrderComposer`
   и `FakePublisher`, реальный брокер и база не задействованы. Проверка владельцем: запустить
   `Workers` с `Routing:Charging:Enabled=true` (флаг в репозитории оставлен `false`) и убедиться,
   что при заряде ниже `LowerLimit` в `vrt_RobotOrder` появляется заказ до узла `CHARGING`,
   а в топике `APR/v2/{manufacturer}/{serial}/order` — сообщение с действием `startCharging`
   в последнем узле. Файл-доказательство семантики действия: `src/VR.RoutePlanner.Application/Robots/ChargingNodeAction.cs`
   и тест `tests/VR.RoutePlanner.Tests.Unit/Robots/VdaOrderChargingActionTests.cs`.
2. **Kafka `agv-charging-completed`.** `KafkaRobotEventPublisher` в тестах не запускается
   (брокера нет). Проверка владельцем: `kafka-console-consumer --topic agv-charging-completed`
   при роботе на станции с зарядом выше `TargetLimit`; ключ сообщения — серийный номер.
   Файл-доказательство: `src/VR.RoutePlanner.Infrastructure/Kafka/KafkaRobotEventPublisher.cs`,
   тест защёлки и независимости событий — `tests/VR.RoutePlanner.Tests.Unit/Robots/RobotRelocationWorkerTests.cs`.
3. **Маршрутная часть заказа на реальной карте.** Планировщик в тестах подставной (`FakePlanner`),
   поэтому реальные ребра графа 801 и точки подхода не проверены. Проверка владельцем:
   прогон на стенде с `Routing:Charging:Enabled=true` и роботом в симуляторе.
   Файл-доказательство: `tests/VR.RoutePlanner.Tests.Unit/Robots/RobotRelocationServiceTests.cs`
   (создание заказа через `RobotOrderService.CreateTaskOrderAsync`, а не собственным сборщиком).
