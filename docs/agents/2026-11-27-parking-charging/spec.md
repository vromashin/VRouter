# Возврат робота на зарядку и в парковку (порт поведения AGVControl)

Дата: 2026-11-27. Репозиторий: `C:\Users\User\source\repos\VR.RoutePlanner`, ветка
`ai/stage2.5-vda-port-step3`, HEAD `d43e1e1`. Слаг ветки разработчика: `ai/charging-parking`.
Эталон разобран в `doc/agents/2026-11-27-parking-charging/reverse.md` (ссылки на файлы даны ниже),
форма заказа — в `doc/agents/2026-11-24-agv-robot-control/spec.md` и `spec-order-updates-2.md`.

## Цель

Робот сам уезжает на узел `CHARGING` при низком заряде и на узел `PARKING` при простое,
пользуясь обычным VDA-заказом; по достижении `TargetLimit` публикуется событие
`agv-charging-completed`. Админка получает рабочие `move-to-charge` и `move-to-parking`.

## Затронутые репозитории и проекты

Только `VR.RoutePlanner`:

- `VR.RoutePlanner.Application` — `Configuration/ChargingOptions.cs` (новый),
  `Robots/RobotRelocationService.cs` (новый), `Robots/IRobotEventPublisher.cs` (новый),
  `Robots/RelocationTargetSelector.cs` (новый), `Robots/VdaOrderRequest.cs`,
  `Robots/RobotSnapshot.cs`, `Robots/RobotOrderService.cs`.
- `VR.RoutePlanner.Infrastructure` — `Mqtt/VdaOrderBuilder.cs`, `Mqtt/RobotStateParser.cs`,
  `Kafka/KafkaRobotEventPublisher.cs` (новый), `ServiceCollectionExtensions.cs`.
- `VR.RoutePlanner.Api` — `Controllers/LegacyOrderController.cs`.
- `VR.RoutePlanner.Workers` — `RobotRelocationWorker.cs` (новый), `Program.cs`, `appsettings.json`.
- `tests/VR.RoutePlanner.Tests.Unit`, `tests/VR.RoutePlanner.Tests.Integration`.

## Решения

### Д-1. Два триггера, оба внутри нашего сервиса

В эталоне триггеры разнесены по Kafka: `agv-low-battery` публикует StateMonitor
(`AGVControlApi/Monitor/StateMonitorService.cs:1028-1054`), `agv-waiting` — FleetManager
(`appsettings.api.json:31`), а заказ строит FleetManager. Мы Kafka для триггеров **не используем**:
оба условия проверяются в одном фоновом сервисе `RobotRelocationWorker` (Д-6).

1. Низкий заряд: `snapshot.BatteryCharge < Charging:LowerLimit` (20) и робот не на зарядке.
2. Простой: `Charging:WaitingMinutes` (5) без активности. Пустая очередь WMS сама по себе
   парковку не запускает.

События `agv-low-battery` и `agv-waiting` не публикуются (осознанное расхождение, см. Д-11).

### Д-2. Определение простоя (своё, в эталоне его нет)

Робот считается простаивающим в тике, когда выполнено всё:

- нет активного заказа в `vrt_RobotOrder` (`IRobotOrderRepository.GetActiveAsync`, статусы
  `Draft/Sent/Accepted/Executing` — те же, что `RobotOrderService.ActiveStatuses`,
  `RobotOrderService.cs:47-53`; для одного робота достаточно `HasActiveOrderAsync`,
  `RobotOrderService.cs:948-952`);
- `snapshot.Driving == false` и `snapshot.Errors` пуст;
- нет задачи в работе: активного заказа нет (единственный источник правды о задаче —
  `vrt_RobotOrder`; отдельного признака задачи у нас не существует);
- робот не на узле `CHARGING`/`PARKING`;
- состояние не устарело: `IRobotStateReader.GetAllAsync` уже отбрасывает снимки старше
  `AgvMonitoring:StaleStateSeconds` (`DatabaseRobotStateReader.cs:109-124`).

«Робот на узле» = `snapshot.LastNodeId` совпал с узлом карты, иначе ближайший узел карты в
радиусе `Charging:IdleNodeToleranceMeters` (0.5 м) от `PositionX/PositionY`.

Момент начала простоя хранится в памяти сервиса (`Dictionary<string, DateTimeOffset>`), сбрасывается
при любом нарушении условий выше. Простой переживает рестарт как «начался заново» — это допустимо.

### Д-3. Несколько роботов, несколько узлов: выбор цели, занятость, освобождение

Логика независима по каждому роботу: у робота свой автомат (Д-3.4), свой таймер простоя, свои
защёлки и свой счётчик попыток. Никакого глобального «текущего робота» в сервисе нет.

#### Д-3.1. Выбор узла

Карта берётся из `snapshot.MapId`, при пустом — `Charging:MapId` (801). Тип узла сравнивается
строго: `node.NodeType == "CHARGING"` / `"PARKING"` (`WarehouseNode.cs:14`), не по префиксу кода.
Кандидаты — все узлы нужного типа на карте робота; занятые исключаются (Д-3.2); из свободных
берётся минимум по ключу сортировки:

1. `(node.X - robotX)^2 + (node.Y - robotY)^2` — квадрат расстояния;
2. при равенстве — `NodeId` по `StringComparer.Ordinal` (детерминированный тай-брейк, результат
   воспроизводим при повторных прогонах и в тестах).

Функция выбора — чистая (`RelocationTargetSelector.Select(nodes, robotPosition, isOccupied)`):
без обращения к БД, состояниям и времени, поэтому проверяется юнит-тестами на любом числе роботов
и узлов.

#### Д-3.2. Занятость узла

Узел занят, если верно хотя бы одно:

- **(а) там физически стоит другой робот**: его `LastNodeId` равен узлу, либо
  `PositionX/PositionY` в радиусе `Charging:OccupiedRadiusMeters` (0.75 м) от узла;
- **(б) на него уже едет другой робот**: у другого робота активный заказ
  (`Draft/Sent/Accepted/Executing`) с `TargetNodeId` = этот узел;
- **(в) он зарезервирован**: узел закреплён в памяти процесса за роботом, которому выдан
  автопереезд, пока заказ не терминален. Резерв закрывает гонку между тиком и появлением строки
  заказа в базе и повторно проверяется перед созданием заказа; резерв действует в пределах одного
  процесса (ограничение разобрано в «Рисках»), а межпроцессная правда — правило (б).

Свои собственные признаки занятостью не считаются: робот, который сам стоит на узле или сам туда
едет, не блокирует себя.

#### Д-3.3. Освобождение занятости

Узел снова свободен, когда перестают выполняться все три условия:

- **парковка**: как только робот покинул узел — его `LastNodeId` больше не равен узлу и расстояние
  до узла больше `OccupiedRadiusMeters`; заказ к этому моменту уже `Completed` (правило (б) не
  действует), резерв снимается по завершении заказа;
- **зарядка**: узел занят всё время, пока робот на нём, включая сам заряд, и освобождается только
  после завершения заряда и отъезда робота (те же признаки по `state`);
- резерв (в) снимается, когда заказ стал `Completed/Failed/Cancelled/Rejected` **и** робот ушёл с
  узла; при `Failed/Rejected/Cancelled` — сразу, не дожидаясь отъезда;
- робот пропал из `IRobotStateReader.GetAllAsync` (состояние протухло) — его резерв снимается по
  таймауту `Charging:RetryDelaySeconds * 2`, иначе узел навсегда останется «занятым».

Никакой отдельной таблицы занятости нет: источник правды — `vrt_RobotOrder` (`TargetNodeId` +
`Status`) плюс live-состояние робота; резерв — только надстройка в памяти.

#### Д-3.4. Автомат состояния робота

Состояние выводится каждый тик из базы и `state` (не хранится отдельно, поэтому переживает
рестарт; в памяти живут только таймеры, защёлки и резервы):

| Состояние | Признак |
|---|---|
| `Busy` | есть активный заказ, чей `TargetNodeId` — не `CHARGING` и не `PARKING` (задача WMS) |
| `GoingToCharge` | есть активный заказ с `TargetNodeId` = узел `CHARGING` |
| `Charging` | робот на узле `CHARGING` (`snapshot.Charging == true` либо `LastNodeId`/радиус) |
| `GoingToParking` | есть активный заказ с `TargetNodeId` = узел `PARKING` |
| `Parked` | робот на узле `PARKING`, активного заказа нет |
| `Idle` | активного заказа нет, робот не `Driving`, ошибок нет, не на `CHARGING`/`PARKING` (Д-2) |

Переходы разрешены только по одному автопереезду на робота: из `Idle` в `GoingToCharge` или
`GoingToParking`, затем в `Charging`/`Parked`. Повторная отправка запрещена: пока робот не в `Idle`,
новые заказы релокации не создаются, а собственный активный заказ — единственный (проверка
`_orders.HasActiveOrderAsync` + `OrderInProgressException` как второй барьер). Автоматического
перехода `Charging → GoingToParking` нет (решение 7 первого сообщения).

#### Д-3.5. Приоритеты при конкуренции

Тик обслуживает роботов пакетом, а не по одному в порядке перечисления (иначе порядок из
`GetAllAsync` решал бы, кому достался узел):

1. собрать заявки на зарядку (все роботы, у кого выполнен Д-1.1 и истекла пауза повтора);
2. отсортировать по `(BatteryCharge asc, SerialNumber ordinal)` — первым едет робот с самым низким
   зарядом, тай-брейк по серийному номеру детерминирован;
3. выдавать заказы в этом порядке, помечая выбранный узел резервом, чтобы следующий робот его уже
   не увидел;
4. если свободных узлов не осталось — заявки этого робота остаются в ожидании (Д-5), заказ не
   создаётся, робот продолжает стоять где стоял;
5. парковка обслуживается после зарядки тем же пакетным способом, порядок заявок —
   `(IdleSince asc, SerialNumber ordinal)`: первым паркуется тот, кто дольше простаивает.

Один робот не может получить два заказа в одном тике; зарядка приоритетнее парковки (Д-6).

### Д-4. Перемещение — обычный VDA-заказ, действие `startCharging` — действием узла

Заказ строится существующим сборщиком (`VdaOrderBuilder.Build`, `VdaOrderBuilder.cs:60-105`) и
сервисом (`CreateTaskOrderAsync`, `RobotOrderService.cs:203`) как одиночное плечо:
`hasLoad = false`, `finalTargetNodeId = target`, вилы не запрашиваются. Специальных полей заказа нет.

Для узла `CHARGING` в последний узел плана добавляется действие `startCharging`
(`SOFT`, как эталон `OrderService.cs:1746-1773` для погрузчика) с параметрами:

- `chg_id` = `Charging:ChargerId` (20373);
- `chargerX` = `node.X + (L/2 + 0.25) * cos(-node.Theta)`, `chargerY` аналогично по `sin`,
  где `L` — `Routing:AgvProfiles[Routing:DefaultAgvProfile].LengthMeters` (1.455),
  `node` — целевой узел карты (эталон считает от координат узла и его `Theta`,
  `OrderService.cs:1164-1170` + `1749-1756`);
- `thetaWhileCharging` = `-node.Theta`.

Для `PARKING` действий в узле нет.

Реализация в сборщике: `VdaOrderRequest` получает необязательные поля
`TargetNodeActionType`, `TargetNodeActionBlockingType` (по умолчанию `SOFT`) и
`TargetNodeActionParameters` (`IReadOnlyDictionary<string, object?>`); `BuildNode`
(`VdaOrderBuilder.cs:380-519`) добавляет такое действие в последний узел плана рядом с
`TerminalActionType`, парameters строятся отдельным методом без вилочных ключей
(существующий `BuildAction`, `:677-703`, не меняется). Поведение вил, курсов, допусков,
`VIRTUAL_START_`, нумерации не меняется — это проверяют текущие тесты `VdaOrderReferenceTests`,
`VdaOrderNodeTests`, `VdaOrderOrientationTests`.

### Д-5. Занятый целевой узел — откладываем, робота не двигаем

Эталон падает с ошибкой (`reverse.md`, раздел про busy-узел). Мы: заказ не создаём, заказ
предыдущего робота не отменяем, робот продолжает стоять там, где стоял, в журнал пишется
`LogInformation` с серийником, узлом и причиной. Повторная попытка — не раньше
`Charging:RetryDelaySeconds` (60). Тот же порядок для любого отказа
`OrderInProgressException`/`OrderRejectedException` из `CreateTaskOrderAsync`.

Попытки ограничены: у робота счётчик `_attempts[serial]` (растёт на каждой отложенной попытке,
сбрасывается при успешном создании заказа, при смене триггера или при выходе из `Idle`).
После `Charging:MaxAttempts` (60, то есть ~1 час при дефолтах) попытки прекращаются: одно
`LogWarning` «релокация {serial} приостановлена: {attempts} попыток, все узлы {тип} заняты»,
и новых попыток нет, пока не изменится условие (заряд вырос выше `LowerLimit` для зарядки,
робот ушёл с места простоя для парковки). Бесконечного цикла заказов и бесконечного спама в журнал
не возникает.

Отложенная попытка считается на робота, а не глобально: один робот, ожидающий свободную станцию,
не блокирует выдачу заказа другому роботу (Д-3.5).

### Д-6. Фоновый сервис `RobotRelocationWorker`

`BackgroundService` в `VR.RoutePlanner.Workers`, регистрируется в `Program.cs` при
`Charging:Enabled == true` (как `MqttRobotStateWorker`/`SimpleTaskWorker`, `Program.cs:27-40`).
Зависимости: `IRobotStateReader`, `RobotRelocationService`, `IRobotOrderRepository`,
`IOptions<ChargingOptions>`, `TimeProvider`, `ILogger`.

Тик: `Task.Delay(Charging:PollSeconds)` (5 с), тело в `try/catch` с `LogError` — одна ошибка
(в том числе ошибка по одному роботу) не останавливает сервис и не блокирует остальных.
Порядок в тике:

1. `states = await _states.GetAllAsync(ct)`; для каждого робота обновить состояние автомата (Д-3.4)
   и таймер простоя (Д-2). Роботы обрабатываются независимо: исключение по одному ловится
   локально и логируется с серийником.
2. Событие завершения заряда по каждому роботу в состоянии `Charging` (Д-7).
3. Пакет зарядки (Д-3.5): заявки → сортировка по заряду и серийнику → выдача заказов по одной
   с резервированием узла (Д-3.2).
4. Пакет парковки (Д-3.5) для роботов в `Idle`, у кого истекло `WaitingMinutes` и кто не получил
   заказ на зарядку в этом тике. Порядок — по длительности простоя и серийнику.
5. Обновить `_lastAttempt`, `_attempts`, снять резервы по терминальным заказам и ушедшим роботам.

Идемпотентность/анти-спам:

- пока у робота есть активный заказ, заказы не создаются (проверка на каждом тике);
- `_lastAttempt[serial]` (момент последней попытки, успешной или нет) блокирует повторы на
  `RetryDelaySeconds`, `_attempts[serial]` ограничивает их сверху (Д-5);
- собственный заказ на зарядку не отличим от заказа WMS по виду: назначение выводится из
  `TargetNodeId` → тип узла карты (`CHARGING`/`PARKING`), отдельного поля и колонки нет (Д-9);
- после прибытия на `CHARGING` пункт Д-1.1 больше не срабатывает (робот на узле зарядки),
  после прибытия в `PARKING` не срабатывает Д-2 (робот на узле парковки).

### Д-7. Событие `agv-charging-completed` и `stopCharging`

Событие публикуется один раз при переходе через порог: `BatteryCharge >= Charging:TargetLimit`
(80) и робот на зарядке (`snapshot.Charging == true` либо узел `CHARGING`), если
`Charging:PublishChargingCompleted` (по умолчанию `true`). Защёлка в памяти
`_completedSent[serial]`, сбрасывается при `BatteryCharge < LowerLimit`.

Топик — `Charging:ChargingCompletedTopic` (по умолчанию `agv-charging-completed`), key — серийный
номер (как эталон, `StateMonitorService.cs:1078`), тело — как `StateMonitorService.cs:1065-1072`
плюс собственный идентификатор события вместо пустых GUID:

```json
{ "EventId": "3f6c1a9e0b7d4c2f8a15d94e6b0c7a21", "AgvSerialNumber": "98001",
  "NewBatteryLevel": 81.5, "Threshold": 80, "Status": "OK",
  "CompletedAt": "2026-11-27T10:00:00.000Z" }
```

`EventId` — `Guid.NewGuid().ToString("N")` на каждую публикацию, чтобы потребитель мог
дедуплицировать; `AgvId`-подобных полей с `Guid.Empty` нет (дефект эталона, `Д-11`). Защёлка
`_completedSent[serial]` — на робота, поэтому несколько роботов публикуют события независимо.

Публикация через новый `IRobotEventPublisher` (Application) и `KafkaRobotEventPublisher`
(Infrastructure/Kafka, `ProducerBuilder<string,string>`, брокер из
`Routing:Kafka:BootstrapServers`). Ошибка публикации — `LogError`, тик продолжается.

Автоматический `stopCharging` по умолчанию **выключен** (`Charging:AutoStopCharging = false`):
в эталоне автоматической остановки заряда не найдено. При `true` — один `instantAction`
`stopCharging` в момент публикации события.

### Д-8. HTTP: `move-to-charge` и `move-to-parking`

`LegacyOrderController` (`Controllers/LegacyOrderController.cs:39-54`) переводится на
`RobotRelocationService`. Пути:

- `POST /api/Order/move-to-charge/{serial}` и `POST /api/Order/move-to-charge/{serial}/{nodeId}`;
- `POST /api/Order/move-to-parking/{serial}` и `POST /api/Order/move-to-parking/{serial}/{nodeId}`
  (новый).

Поведение:

1. Робот уже стоит на узле нужного типа: заказ не создаётся. Для зарядки уходит прежний
   `instantAction startCharging` (`RobotOrderService.SendInstantActionAsync:1159-1187`), для
   парковки — успех без действий. Робота не двигаем.
2. Иначе создаётся заказ до указанного `nodeId` либо до ближайшего свободного узла этого типа (Д-3.1).
   Для `CHARGING` — с действием `startCharging` в целевом узле (Д-4).
3. Узел нужного типа на карте отсутствует → `success:false, message:"CHARGE_NODE_NOT_FOUND"` /
   `"PARKING_NODE_NOT_FOUND"`; указанный в пути `nodeId` не найден или его тип не тот →
   `"NODE_NOT_CHARGING"` / `"NODE_NOT_PARKING"`.
4. Все узлы этого типа заняты (в том числе явно указанный `nodeId`) →
   `success:false, message:"CHARGE_NODE_BUSY"` / `"PARKING_NODE_BUSY"`.
   Откладывает и повторяет только фоновый сервис, эндпоинт — нет.
5. У робота активный заказ → как сейчас (`OrderInProgressException` → `success:false`).
6. Остальные ошибки — как в `move-to-node` (`OrderRejectedException` → `Failure`, код `200`).

Форма ответа — объединение текущих: `{ success, orderId, orderUpdateId, sequenceId, agvSerialNumber,
actionId, message }`; `orderId` заполнен для заказа, `actionId` — для мгновенного действия, второе
поле `null`. Так не ломается ни админка (`ntls.rms.admin.ng/Services/AGVControlService.cs:131`,
которая ждёт `ApiResponse`), ни текущий вызов из `move-to-charge`.

### Д-9. Взаимодействие с очередью WMS-задач

- Заказ на зарядку/парковку — обычная строка `vrt_RobotOrder`, поэтому задача WMS, пришедшая во
  время перегона, ждёт освобождения робота в `SimpleTaskWorker.WaitFreeAsync`
  (`SimpleTaskWorker.cs:267-279`, до 20 минут). Заказ на зарядку не отменяется, `cancelOrder`
  не отправляется (Д-8 из `spec-order-updates`).
- `SimpleTaskWorker.WaitFreeAsync` дополнить: если `CreateTaskOrderAsync` бросил
  `OrderInProgressException`, ждать ещё 3 с и повторить (не более 20 минут), вместо падения задачи
  в `Failed`. Гонка «тик релокации между проверкой и созданием заказа» иначе съедает задачу WMS.
- Приоритет: уже выданный заказ важнее; новая задача WMS никогда не вытесняет заказ на зарядку,
  а релокация никогда не вытесняет задачу.
- Пока робот едет заряжаться, новые заказы релокации не создаются (активный заказ).

### Д-10. Конфигурация: новая секция `Routing:Charging`

`ChargingOptions` в `Application/Configuration`, `SectionName = "Routing:Charging"`
(как `Routing:Order`, `Routing:Warmup`, `Routing:Kafka`). Биндинг и `ValidateOnStart` — в
`ServiceCollectionExtensions.AddInfrastructure` рядом с остальными (`:54-78`), валидатор —
`ChargingOptionsValidator : IValidateOptions<ChargingOptions>`. Секция добавляется в
`appsettings.json` обоих процессов (`Api`, `Workers`).

| Ключ | Тип | Дефолт | Смысл |
|---|---|---|---|
| `Enabled` | bool | `false` | Включает фоновые триггеры (Д-1). Эндпоинты работают всегда |
| `MapId` | string | `"801"` | Карта, если `snapshot.MapId` пуст |
| `LowerLimit` | double | `20` | Порог низкого заряда, % |
| `TargetLimit` | double | `80` | Порог «заряд завершён», % |
| `WaitingMinutes` | double | `5` | Простой до парковки, минуты |
| `PollSeconds` | double | `5` | Интервал опроса |
| `RetryDelaySeconds` | double | `60` | Пауза между попытками (занятый узел, отказ) |
| `MaxAttempts` | int | `60` | Предел подряд отложенных попыток на робота, после — стоп до смены условия (Д-5) |
| `IdleNodeToleranceMeters` | double | `0.5` | Радиус «робот стоит на узле» |
| `OccupiedRadiusMeters` | double | `0.75` | Радиус «узел занят другим роботом» |
| `ChargerId` | string | `"20373"` | Параметр `chg_id` действия `startCharging` |
| `PublishChargingCompleted` | bool | `true` | Публиковать событие Д-7 |
| `ChargingCompletedTopic` | string | `"agv-charging-completed"` | Топик события |
| `AutoStopCharging` | bool | `false` | Слать `stopCharging` после `TargetLimit` |

`Validate()` возвращает текст ошибки (не бросает) и проверяет: `MapId` не пуст;
`0 < LowerLimit < TargetLimit <= 100`; `WaitingMinutes > 0`; `PollSeconds > 0`;
`RetryDelaySeconds >= 0`; `MaxAttempts > 0`; `IdleNodeToleranceMeters > 0`;
`OccupiedRadiusMeters > 0`; при `PublishChargingCompleted` — `ChargingCompletedTopic` не пуст.
Сообщения содержат полное имя ключа (`Routing:Charging:MaxAttempts ...`) и не содержат секретов.

### Д-11. Дефекты эталона исправляем, а не копируем

- **Регистр ключей конфигурации** — единый PascalCase (`LowerLimit`, `TargetLimit`, `ChargerId`),
  а не `lowerLimit`/`chg_id`/`thetaWhileCharging` (`appsettings.api.json:34-42`). Чтение — только
  через `IOptions<ChargingOptions>`, `GetValue("Charging:...")` в коде не появляется; опечатка в
  ключе видна на старте благодаря `ValidateOnStart`, а не молча даёт дефолт.
- **Проверка в `Validate()`** — все ключи из таблицы Д-10, включая `MaxAttempts`; эталон не
  проверяет ничего и падает позже (деление, отрицательные лимиты).
- **Уникальные идентификаторы событий**: `EventId` — новый `Guid` на публикацию (Д-7); пустых
  `Guid.Empty` (эталон, `SimpleTaskWorker.cs:264`) и полей-заглушек нет.
- **Повторы с интервалом и ограничением** (Д-5): `RetryDelaySeconds` + `MaxAttempts`; эталон
  падает с ошибкой на первом же занятом узле и не повторяет.
- **Журнал без секретов и без шума**: в сообщениях только серийник, тип узла, `TargetNodeId`,
  заряд, число попыток; ни пароля MQTT, ни строки подключения, ни тела заказа целиком. Отложенная
  попытка — `LogInformation` один раз на попытку, исчерпание лимита — одно `LogWarning`, а не
  строка на каждый тик.
- **Класс робота**: для неизвестного класса эталон даёт `blockingType: HARD`
  (`OrderService.cs:1775-1789`); у нас класса нет, всегда `SOFT` (профиль на стенде — погрузчик).
- **Многоузловость**: эталон работает с единственным настроенным `chargerX/chargerY`
  (`Charging:chargerX`, `:35-38`) и не различает несколько станций; у нас цель — узел карты, их
  может быть сколько угодно (Д-3).

## Критерии приёмки

### Юнит-тесты (обязаны существовать и проходить)

1. `tests/.../Configuration/ChargingOptionsTests.cs`: дефолты равны таблице Д-10;
   `LowerLimit >= TargetLimit`, `TargetLimit > 100`, `WaitingMinutes <= 0`, `PollSeconds <= 0`,
   `RetryDelaySeconds < 0`, `MaxAttempts <= 0`, пустой `MapId`, пустой топик при
   `PublishChargingCompleted` → текст ошибки с именем ключа; корректный набор → `Validate() == null`.
2. `tests/.../Robots/RelocationTargetSelectorTests.cs`: ближайший по квадрату расстояния;
   при равном расстоянии выбирается меньший `NodeId` (ordinal) — результат воспроизводим;
   занятый узел исключается (активный заказ другого робота, поза другого робота в радиусе,
   резерв в памяти — три случая); все заняты → `null`; узел `WAYPOINT` с похожим кодом не
   выбирается; карта без узлов типа → `null`; три узла, занят средний → выбирается следующий
   свободный, а не ближайший занятый.
3. `tests/.../Robots/ChargingNodeActionTests.cs` (сборщик): заказ до `CHARGING` содержит в
   последнем узле действие `startCharging`, `blockingType: "SOFT"`, параметры `chg_id`,
   `chargerX`, `chargerY`, `thetaWhileCharging` с ожидаемыми числами от координат узла;
   заказ до `PARKING` действий не содержит; порядок и число узлов/ребер не изменились.
4. `tests/.../Robots/RobotRelocationServiceTests.cs`: робот на узле `CHARGING` → только
   `instantAction startCharging`, заказа нет; занятый узел → отказ без заказа; два узла, один
   занят → выбирается второй; явный `nodeId` из запроса занят → отказ `CHARGE_NODE_BUSY` /
   `PARKING_NODE_BUSY` без заказа; нет узлов типа →
   `CHARGE_NODE_NOT_FOUND`/`PARKING_NODE_NOT_FOUND`; активный заказ → `OrderInProgressException`;
   созданный заказ имеет `TargetNodeId` = выбранный узел и `Status = Sent`.
5. `tests/.../Robots/RobotRelocationWorkerTests.cs`: запись ниже `LowerLimit` → один заказ;
   повторный тик при активном заказе → второго заказа нет; простой меньше `WaitingMinutes` →
   ничего; простой больше → заказ в `PARKING`; робот на `PARKING` → парковки нет; робот на
   `CHARGING` → зарядки нет; `Driving = true` или ошибки робота → триггеров нет; `Enabled = false` →
   триггеров нет; после неудачи повтор не раньше `RetryDelaySeconds`; после `MaxAttempts`
   отложенных попыток новых заказов нет и в журнале одно предупреждение; зарядка и простой
   одновременно → создаётся только заказ на зарядку; состояние автомата по каждому роботу
   соответствует Д-3.4 (шесть состояний, включая `Busy` для задачи WMS).
   **Сценарии на несколько роботов и узлов (обязательны, тот же файл):**
   - два робота, один узел `CHARGING`: первый тик — ровно один заказ, у второго заказа нет;
     на следующем тике второй робот ждёт (заказ не создан, узел занят по правилу (б)/резерву);
   - два робота, две станции: каждый получает свою ближайшую станцию, ни один не выбирает
     станцию другого, оба заказа созданы в одном тике;
   - все станции заняты: ни одного заказа, роботы остаются на местах, после освобождения узла
     (робот уехал / заказ завершён) заказ создаётся на следующем разрешённом тике;
   - приоритет: станций меньше, чем роботов с низким зарядом → заказ получает робот с меньшим
     зарядом, при равном заряде — меньший серийник (ordinal); остальные ждут;
   - занятый парковочный узел: робот в `Idle` ждёт, заказ не создан, второй узел `PARKING`
     достаётся другому роботу;
   - освобождение: после отъезда робота с `PARKING` узел снова доступен следующему роботу;
     пока робот стоит на `CHARGING` (в том числе во время заряда) второй робот туда не едет;
   - один активный переезд на робота: два тика подряд не дают двух заказов одному роботу.
6. `tests/.../Robots/ChargingCompletedEventTests.cs`: переход через `TargetLimit` публикует одно
   событие с полями из Д-7 (`EventId` — непустой `Guid`, уникален между публикациями двух
   роботов); повторный тик событие не дублирует; сброс защёлки ниже `LowerLimit` позволяет
   следующую публикацию; `AutoStopCharging = false` → `stopCharging` нет, `true` → ровно один
   `stopCharging`; `PublishChargingCompleted = false` → публикаций нет; два робота одновременно
   на двух станциях → два независимых события; ключ сообщения = серийный номер.
7. `tests/.../Robots/RobotStateParserTests.cs` — новый случай: `batteryState.charging = true`
   попадает в `RobotSnapshot.Charging`; отсутствие поля даёт `false`; существующие случаи зелёные.
8. `tests/.../Robots/TaskOrderLegTests.cs`, `VdaOrderReferenceTests.cs`, `VdaOrderNodeTests.cs`,
   `VdaOrderOrientationTests.cs`, `OrderUpdateTests.cs`, `LegacyAgvTaskControllerTests.cs` —
   проходят без изменения ожиданий.

### Интеграционные

9. `tests/.../Integration/Robots/RelocationOrderTests.cs` (живая база стенда, `TestDatabase`):
   заказ до `ch1` сохраняется в `vrt_RobotOrder` с `TargetNodeId = 'ch1'`, `Status = 'Sent'`,
   `FailureReason IS NULL`, а `PlanJson` содержит `"actionType":"startCharging"`; повторный вызов
   при активном заказе не создаёт вторую строку.
10. `dotnet build VR.RoutePlanner.sln -c Release` — 0 предупреждений; `.\scripts\test.ps1` —
    Unit/Integration/Contracts зелёные, `SkippableFact` не пропущены.

### Живой прогон на стенде 131 (файлы в `doc/agents/2026-11-27-parking-charging/evidence/`)

На стенде один робот (`98001`) и по одному узлу каждого типа (`ch1`, `pa1`): живой прогон
подтверждает форму заказа, действия в узле, HTTP и событие, но не многороботность. Сценарии
конкуренции за узлы закрываются юнит-тестами (п. 2, 4, 5), и это фиксируется в `stand-report.md`.

11. Имитация низкого заряда — **через конфиг**, не через робота: в
    `/opt/rms/vrp-workers/appsettings.json` поставить `Routing:Charging:Enabled = true`,
    `LowerLimit = 100`, `WaitingMinutes = 1000` (чтобы не сработал простой), перезапустить
    `vrp-workers`. Ожидается: `stand-charge-order.json` — сообщение `order` с `orderId`, где
    последний узел `ch1`, в нём действие `startCharging` (`SOFT`, параметры Д-4), и
    `stand-db.txt` — строка `vrt_RobotOrder` с `TargetNodeId = 'ch1'`, статус `Completed`,
    `FailureReason` пуст; `stand-workers.log` — строка о создании заказа на зарядку.
12. Имитация простоя: `LowerLimit = 1`, `WaitingMinutes = 1`, перезапуск `vrp-workers`, задачи WMS
    не подавать. Ожидается `stand-parking-order.json` — заказ до `pa1` **без** действий в узлах,
    `stand-db.txt` — `TargetNodeId = 'pa1'`, `Completed`. Повторного заказа после прибытия нет
    (`stand-workers.log` за 5 минут).
13. Событие: при стоянке робота на `ch1` поставить `TargetLimit = 1` (ниже текущего заряда),
    `AutoStopCharging = false`, перезапустить. Ожидается `stand-charging-event.log`
    (`rpk topic consume agv-charging-completed`) — ровно одно сообщение с полями Д-7, серийник
    `98001`, `Threshold = 1`, без пустых GUID; в журнале нет `stopCharging`.
14. HTTP: `stand-http.txt` — `POST /api/Order/move-to-parking/98001` возвращает `success:true`
    с `orderId` и место прибытия; `POST /api/Order/move-to-charge/98001` при роботе на `ch1`
    возвращает `success:true` с `actionId`; `POST /api/Order/move-to-charge/98001/wa1"`
    возвращает `success:false` с `NODE_NOT_FOUND`-подобным текстом (узел не `CHARGING`).
15. `stand-report.md` — что и как проверено, к какому заказу относится каждый файл, конфиг стенда
    до и после прогона (значения вернуть к дефолтам).

## Границы

- Только репозиторий `VR.RoutePlanner`. `ntls.rms.admin.ng`, `AGVControl`, `ntls.rms.*` — не трогать
  (в старой админке ничего менять не нужно, её запросы уже совпадают с новыми путями).
- Миграции не добавлять, схему `vrt_*` и `rms."Agvs"` не менять. Назначение заказа
  (`CHARGING`/`PARKING`) выводится из `TargetNodeId`, отдельной колонки нет.
- Планировщик (`AStarPathPlanner`), `TrafficCoordinator`, `TrafficReservationRegistry` не менять.
- Существующее поведение сборщика для вил, курсов, допусков, `VIRTUAL_START_`, нумерации и
  `TerminalActionType` не менять; `BuildAction` (`VdaOrderBuilder.cs:677-703`) не менять.
- Сигнатуры `CreateOrderAsync`/`CreateTaskOrderAsync` и `SimpleTaskWorker.PublishLegsAsync`
  сохранить совместимыми (новые параметры — только необязательные), иначе падают текущие тесты.
- `Routing:Order` и `Routing:Kafka` не менять (кроме чтения `BootstrapServers`).
- Файлы `AGVControl` (CP1251) не редактировать и не пересохранять.

## Риски и ловушки

- **`batteryState.charging` не парсится** (`RobotStateParser.cs:69-81` читает только
  `batteryCharge`). Без нового поля `RobotSnapshot.Charging` триггер Д-1.1 и событие Д-7 опираются
  только на тип узла; добавляем необязательное поле в конец записи, `vrt_RobotSession` хранит
  сырой payload, миграция не нужна. Симулятор может не отдавать `charging` — тогда событие Д-7
  проверяется по варианту «узел `CHARGING` + заниженный `TargetLimit`» (п. 13).
- **Два действия в одном узле** (`detectObject` из `Routing:Order:TerminalActionType` и
  `startCharging`). Ставим рядом, как только остановку и зарядку в одной точке. Если робот
  отвергнет заказ, на стенде достаточно выставить `Routing:Order:TerminalActionType` в пустую
  строку — правка конфига, не кода. Это единственное место, где нужен живой ответ робота.
- **Гонка с задачей WMS**: заказ релокации занимает робота на время перегона. Без правки
  `WaitFreeAsync` (Д-9.2) задача WMS падает в `Failed`; правка минимальная, но затрагивает общий
  поток задач — покрыть тестом на повтор.
- `IRobotStateReader.GetAllAsync` в `DatabaseRobotStateReader` читает **только память процесса**
  (`:109-124`), то есть сервис видит роботов лишь пока `Routing:Mqtt:Enabled` и `vrp-workers`
  читает брокер. Для API (другой процесс) это значит: релокация из HTTP работает, фоновые
  триггеры — только в `vrp-workers`.
- Порог «состояние не устарело» — общий `AgvMonitoring:StaleStateSeconds = 5`
  (`appsettings.json:78`); на 5-секундном окне простой измеряется по собственному таймеру сервиса,
  поэтому тик должен идти чаще (`PollSeconds = 5`).
- `appsettings.json` содержит секцию `Kafka` в корне, а `KafkaOptions` биндится из
  `Routing:Kafka` (`KafkaOptions.cs:6`); значит брокер фактически берётся из дефолта
  `127.0.0.1:9092`. Не «чинить» это в рамках задачи, но событие Д-7 будет уходить на дефолтный
  брокер — на стенде это тот же хост.
- Карта 801 содержит ровно один `CHARGING` (`ch1`, 14.61/5.19) и один `PARKING` (`pa1`,
  14.33/35.36), а симулятор на стенде — один робот `98001`. Поэтому «ближайший свободный» на
  стенде вырождается в единственный узел, а проверка занятости — в «робот уже там или туда уже
  едет». Многороботность и многоузловость проверяются юнит-тестами на синтетических картах
  (п. 2, 4, 5 критериев), а не стендом; это надо явно написать в `stand-report.md`.
- **Резерв узла (Д-3.2в) живёт в памяти процесса**, а `vrp-api` и `vrp-workers` — разные процессы.
  Между выбором узла и появлением строки заказа остаётся окно, в котором два процесса могут
  выбрать один узел. Авторитетна проверка (б) по `vrt_RobotOrder`, поэтому окно узкое; закрывать
  его нужно перепроверкой активных заказов непосредственно перед `CreateTaskOrderAsync` и
  обработкой `OrderInProgressException` с повторным выбором узла, а не блокировкой в БД.
- Тесты, которые придётся править: `RobotStateParserTests` (новое поле),
  конструкторы `RobotOrderService` в `OrderServiceFakes`/`RobotOrderServiceTests` (только если
  добавляются обязательные зависимости — делать их необязательными), `SimpleTaskWorker`-тесты при
  появлении повтора в `WaitFreeAsync`. `SwaggerSnapshotTests` не затрагиваются: снимок
  `agvcontrol-swagger.json` — это контракт старого AGVControl, наших путей там нет.
- Регистр ключей конфига: `Charging:*` пишется PascalCase, чтение — только через `IOptions`,
  никаких `GetValue("Charging:lowerLimit")`.

## План проверки

Контролёр обязан прогнать сам:

```powershell
cd C:\Users\User\source\repos\VR.RoutePlanner
git log --oneline d43e1e1..HEAD
git diff --stat d43e1e1..HEAD
dotnet build VR.RoutePlanner.sln -c Release
.\scripts\test.ps1 -NoDb
```

```powershell
# состав изменений и отсутствие запрещённых правок
git diff --name-only d43e1e1..HEAD
grep -rn "startCharging\|move-to-parking\|ChargingOptions" src tests
grep -rn "migrations" tests/VR.RoutePlanner.Tests.Integration/Migrations   # новых миграций быть не должно
grep -rn "AgvId\|Guid.Empty" src/VR.RoutePlanner.Infrastructure/Kafka
```

Живая часть (п. 11-15) проверяется только по файлам в
`doc/agents/2026-11-27-parking-charging/evidence/`; отсутствие любого из них — `BLOCK` на эту часть.

## Приёмочная выжимка

1. `Application/Configuration/ChargingOptions.cs`, секция `Routing:Charging`, ключи и дефолты Д-10
   (включая `MaxAttempts = 60`), `Validate()` с перечисленными проверками, `ValidateOnStart`.
2. Есть `RobotRelocationService`, `RelocationTargetSelector`, `IRobotEventPublisher` +
   `KafkaRobotEventPublisher`; эндпоинты и воркер вызывают сервис, логика не дублируется.
3. `LegacyOrderController`: есть `move-to-parking/{serial}[/{nodeId}]`, `move-to-charge` принимает
   необязательный `{nodeId}`; ответ содержит `orderId` и/или `actionId`; занятые узлы →
   `CHARGE_NODE_BUSY`/`PARKING_NODE_BUSY`, чужой тип → `NODE_NOT_CHARGING`/`NODE_NOT_PARKING`.
4. `VdaOrderBuilder`: для `CHARGING` в последнем узле `startCharging` (`SOFT`) с
   `chg_id`/`chargerX`/`chargerY`/`thetaWhileCharging`; для `PARKING` действий нет; `BuildAction`,
   вилы, курсы, допуски и нумерация не изменены.
5. Выбор узла (Д-3.1-3.2): ближайший **свободный** по квадрату расстояния, при равенстве — меньший
   `NodeId` (ordinal); занят, если там стоит робот, если туда едет другой робот по активному заказу
   (`TargetNodeId`) или узел зарезервирован; все заняты → заказа нет, робот стоит, повтор по
   `RetryDelaySeconds`, ошибки нет.
6. Автомат на робота (Д-3.4): `Idle`/`GoingToCharge`/`Charging`/`GoingToParking`/`Parked`/`Busy`;
   состояние выводится из `state` и `vrt_RobotOrder`, таймеры/защёлки/резервы/попытки — в словарях
   по серийному номеру; один активный переезд на робота.
7. Приоритеты (Д-3.5): зарядка — `(BatteryCharge asc, SerialNumber)`, парковка —
   `(IdleSince asc, SerialNumber)`, выдача пакетом с резервом узла; освобождение узлов по Д-3.3.
8. `Workers/RobotRelocationWorker.cs` регистрируется при `Charging:Enabled`; тик `PollSeconds`,
   проверка активного заказа, `RetryDelaySeconds`, `MaxAttempts` с одним `LogWarning`.
9. `RobotSnapshot.Charging` и разбор `batteryState.charging`; текущие тесты парсера зелёные.
10. Событие `agv-charging-completed`: один раз на переход порога, `EventId` — новый `Guid`,
    key = серийный номер, защёлка на робота; `AutoStopCharging` по умолчанию `false`.
11. `SimpleTaskWorker.WaitFreeAsync` повторяет ожидание при `OrderInProgressException`.
12. Тесты п. 1-8 существуют и проходят, включая многороботные сценарии (два робота и одна станция,
    два робота и две станции, все станции заняты, занятая парковка, освобождение, приоритет по
    заряду); `dotnet build -c Release` — 0 предупреждений; `scripts/test.ps1 -NoDb` — без пропусков.
13. Нет новых миграций и изменений схемы `vrt_*`/`Agvs`; нет правок в `AGVControl`, `ntls.rms.*`,
    `ntls.rms.admin.ng`, `AStarPathPlanner`, `TrafficCoordinator`; ключи конфига — PascalCase.
14. Живой прогон (иначе `BLOCK` на живую часть), файлы в `doc/agents/2026-11-27-parking-charging/evidence/`:
    `stand-charge-order.json` (заказ до `ch1` с `startCharging`), `stand-parking-order.json` (до
    `pa1` без действий), `stand-db.txt` (`vrt_RobotOrder` со `Status = Completed`, `FailureReason`
    пуст), `stand-charging-event.log` (одно событие), `stand-http.txt`, `stand-workers.log` (нет
    повторов 5 минут), `stand-report.md` — с указанием, что на стенде один робот `98001` и одна
    станция `ch1`, поэтому многороботность подтверждается юнит-тестами п. 12, а не стендом.
