# Шаг 3 — перенос автомата состояний и диспетчеризации трафика из AGVControl в VR.RoutePlanner

Ветка: `ai/stage2.5-vda-port`. База диффа: `2333202` (после закрытия замечаний шага 2 это будет новый HEAD — базу уточнить по `git log`, в спеке и в отчёте разработчика должен быть один и тот же хеш).

## Цель

Робот получает заказ, едет по нему, а `VR.RoutePlanner` сам ведёт заказ по сообщениям `state` (включая `nodeStates`/`edgeStates`, `actionStates`, `errors`, `newBaseRequest`, `orderUpdateId`), резервирует за роботом узлы и рёбра впереди него, освобождает их позади и не выдаёт двум роботам один и тот же узкий коридор. Перенос делается из `AGVControl` (`StateMonitorService`, `TrafficManager`, часть `TaskWorker`), но **не копированием**, а переносом поведения на существующие модели `vrt_*` и существующий A*.

## Затронутые репозитории и проекты

- `VR.RoutePlanner` → `VR.RoutePlanner.Application`, `VR.RoutePlanner.Infrastructure`, `VR.RoutePlanner.Workers`, `VR.RoutePlanner.Api`, тесты. Всё.
- `AGVControl` — **только чтение**, эталон. Ни одного изменённого файла.

## 1. Таблица соответствия AGVControl → VR.RoutePlanner

| AGVControl | Что берём | У нас | Действие |
|---|---|---|---|
| `AGVControlApi/Monitor/StateMonitorService.cs` → `ProcessStateMessageAsync` (стр. 621–763) | Разбор `state`, порядок обработки: сначала заказ, потом ошибки/позиция, потом `newBaseRequest` | `Infrastructure/Mqtt/RobotStateParser` (разбор) + `Application/Robots/RobotOrderService.ApplyStateAsync` (автомат) | **переписать**: у нас событийная модель (снимок приходит из `MqttRobotStateReader`), а не опрос; общий «порядок» сохраняем |
| `HandleWithOrderAsync` (стр. 800–972): `nodeStates`/`edgeStates` → статус, освобождение пройденного, резерв выпущенного | Смысл: пройденное освобождается, впередистоящее держится | новый `Application/Traffic/RobotTrafficCoordinator` | **переписать**: не «освободить всё и зарезервировать заново», а посчитать целевое множество и сделать diff |
| `ProcessActionStates` (стр. 974–991) | `pick`/`drop`/`cancelOrder` + `FINISHED` | `RobotOrderService.ApplyStateAsync` (обработка `ActionStates` снимка) | **адаптировать**: у нас нет `IActionService` и таблицы действий; `cancelOrder`+`FINISHED` → `Cancelled`, `pick`/`drop` `FAILED` → блокирующая причина |
| `GetAgvStatusFromState` (стр. 993–1015) | — | — | **не переносим**: у нас нет `AgvCurrentState` (`AUTO/WORKING/IDLE`), статус заказа играет эту роль |
| `HandleNewBaseRequestAsync` (стр. 1098–1190) | Реакция на `newBaseRequest` = продолжение плана | `RobotOrderService.ExtendPlanAsync` (стр. 439–556) — **уже есть** | **адаптировать**: дописать продление резерва под новый кусок. Перестройку маршрута из `HandleNewBaseRequestAsync` (`BuildRouteUpdateSafeAsync` от `LastNodeId` к `GoalNodeId`) **не переносим**: у нас план построен целиком, продолжение берётся из `PlanJson` |
| `CheckWaitingAgvsAsync`, `TryRerouteAsync`, `MarkWaitingForReroute`, `TrackOrderState` (стр. 1190–1342, 1419–1472) | — | — | **не переносим** (перестройка маршрута — отдельный шаг). Детектор «нет прогресса» остаётся у нас в `CheckTimeoutsAsync` |
| `OrderUpdateComplete(orderId, orderUpdateId-1)` (стр. 1473–1486) | — | — | **не переносим**, это дефект: см. §7 Д-1 |
| `OrderCompletedAsync` / `OrderCancelledAsync` / `OrderFailedAsync` + Kafka (стр. 1487–1580) | — | — | **не переносим** в шаге 3: Kafka-события завершения — отдельный шаг, в нашем коде их нет |
| `Order2TaskId`, `_sentEvents` идемпотентность | — | — | **не переносим**: относится к Kafka |
| `AGVControlApi/Routing/TrafficManager.cs` | Резервирование/освобождение/проверка доступности | новый `Application/Traffic/TrafficReservationRegistry` + `ITrafficCoordinator` | **переписать**: у нас ключ это `vrt_TrafficResource.Id` (емкость, тип, направление), а не строка `mapId_nodeId` |
| `AGVControlApi/Services/TrafficCleanupService.cs` | Периодическая очистка просроченных резервов | новый `Workers/TrafficReservationCleanupWorker` | **адаптировать** |
| `AGVControlApi.Tests/Routing/TrafficManagerTests.cs` | 5 сценариев | новый `tests/VR.RoutePlanner.Tests.Unit/Traffic/TrafficReservationRegistryTests.cs` | **адаптировать** (те же сценарии + новые на емкость/направление/TTL) |
| `AGVFleetManager/Services/TaskWorker.cs` → `AgvLastNodeCheckAsync` (стр. 288–311) | Смысл: «на каком узле стоит робот и какого он типа» | `LegacyAgvTaskController.LastNodeAsync` — **уже есть**, `lastnode` отдаёт 200 всегда | **адаптировать**: вынести резолв в `IRobotLastNodeResolver` (`snapshot.LastNodeId` + граф → узел и `NodeType`), использовать в контроллере и в трафике. HTTP-вызов `GET /api/AgvTask/lastnode/{serial}` и `AgvMoveToChargeStationAsync` из `TaskWorker` **не переносим** |

Файлов `AGVControlApi/Services/TrafficManager*.cs` не существует: менеджер трафика лежит в `AGVControlApi/Routing/TrafficManager.cs`, сервис очистки — в `AGVControlApi/Services/TrafficCleanupService.cs`.

## 2. Модель резервирования

### 2.1 Единица резервирования

Резервируется **транспортный ресурс** (`vrt_TrafficResource.Id`), а не узел/ребро по отдельности. Таблицы уже есть, миграции не нужны.

| Таблица | Что даёт | Использование в шаге 3 |
|---|---|---|
| `vrt_TrafficResource` | `Id`, `MapVersionId`, `AgvProfile`, `ResourceType`, `Capacity`, `MinEntryInterval`, `MaxOccupancyTime` | ключ резерва, лимит владельцев, правило направления |
| `vrt_TrafficResourceEdge` | `(TrafficResourceId, EdgeId, Direction, Sequence, AgvProfile)` | ребро плана → ресурс |
| `vrt_TrafficResourceNode` | `(TrafficResourceId, NodeId, AgvProfile)` — узел принадлежит ровно одному ресурсу на профиль | узел плана → ресурс |
| `vrt_ResourceConflict` | пары конфликтующих ресурсов | **в шаге 3 не используется** (остаётся планировщику на следующий шаг) |

Правила «что резервируем»:

- `ResourceType = INDEPENDENT` (класс C5) — не резервируется никогда.
- Коридорный ресурс классов C1 (`EXCLUSIVE`), C2 (`DIRECTIONAL`), C3/C4 (`PARALLEL`) — резервируется в пределах горизонта.
- Узловой ресурс типа `CONFLICT_ZONE` (узел степени > 2, код `<MapId>-<Profile>-CZ-<NodeId>`) — резервируется.
- Узловой ресурс типа `EXCLUSIVE` (код `<MapId>-<Profile>-NODE-<NodeId>`, узел степени ≤ 2) — **не резервируется**: такой узел лежит внутри коридора, который уже держится, а отдельный резерв на нём создаёт взаимную блокировку двух коридоров на стыке. Это осознанное отличие от AGVControl (там резервировался каждый узел).

### 2.2 Структуры в памяти

Новые типы (все в `Application/Traffic`):

- `TrafficReservation` — `Guid MapVersionId`, `Guid ResourceId`, `string ResourceCode`, `string OwnerSerial`, `RouteEdgeDirection? Direction`, `TrafficResourceType ResourceType`, `int Capacity`, `DateTimeOffset AcquiredAt`, `DateTimeOffset RenewedAt`, `DateTimeOffset? PassedAt`.
- `TrafficReservationRegistry` — singleton, потокобезопасный (`ConcurrentDictionary<(Guid MapVersionId, Guid ResourceId), ImmutableList<TrafficReservation>>` под `lock` на ключ либо `ConcurrentDictionary` + CAS).
- `TrafficReservationResult` — `bool Accepted`, `string? ReasonCode`, `string? BlockedBySerial`, `Guid? BlockedResourceId`, `string? BlockedResourceCode`. Коды причин: `TRAFFIC_BLOCKED`, `TRAFFIC_NO_RESOURCE`.
- `ITrafficResourceLookup` / `TrafficResourceLookup` (Infrastructure) — читает `vrt_TrafficResource` + `vrt_TrafficResourceNode` + `vrt_TrafficResourceEdge` для `(MapVersionId, AgvProfile)` и кэширует в памяти процесса. Инвалидация ключа — при смене `vrt_MapVersion.Id` для карты.
- `TrafficHorizonCalculator` — чистая функция: `PlanJson` + `lastNodeSequenceId` + `ITrafficResourceLookup` + `RoutePlannerOptions` → `TrafficHorizon(IReadOnlyList<Guid> ResourceIds, IReadOnlyList<(Guid, RouteEdgeDirection)> Edges)`. Без БД и без времени — тестируется юнитом.
- `ITrafficCoordinator` / `TrafficCoordinator` — `OnStateAsync`, `TryReserveForOrderAsync`, `ReleaseAsync(serial)`, `SweepAsync(now)`.
- `NoOpTrafficCoordinator` — реализация-заглушка: те же методы, ничего не резервирует и всегда отвечает «свободно». Регистрируется в `AddInfrastructure` по умолчанию (её получает Api), `Workers/Program.cs` переопределяет её настоящим координатором (см. §7 Р-2).

Ключ резерва: `(MapVersionId, ResourceId)`. Владелец — серийный номер робота. Время — только через `TimeProvider` (в тестах `TestTimeProvider`).

### 2.3 Ёмкость и направление

- `EXCLUSIVE` и `CONFLICT_ZONE`: один владелец (`Capacity` из строки ресурса, у узловых = 1).
- `DIRECTIONAL`: держать может только один робот, и только в том направлении, в котором ресурс уже занят (направление первого владельца сохраняется в `TrafficReservation.Direction`; встречное — `TRAFFIC_BLOCKED`).
- `PARALLEL`: до `Capacity` владельцев, все — в одном направлении.
- Повторный захват тем же владельцем идемпотентен: счётчик владельцев не растёт, `RenewedAt` обновляется.

### 2.4 Горизонт

Считается от `lastNodeSequenceId` из `state` (`RouteSegment.CumulativeSeconds` и `sequenceId` исходного плана):

- Узлы: `sequenceId` в `[lastNodeSequenceId, lastNodeSequenceId + 2 * ReleaseHorizonNodes]` (`sequenceId` узла = `2 * index`, см. `VdaOrderUpdater`).
- Рёбра: все сегменты до последнего узла горизонта включительно.
- Из множества выбрасываются `INDEPENDENT` и узловые `EXCLUSIVE`.

Значения берутся из уже существующих параметров (менять дефолты нельзя): `Routing:ReleaseHorizonNodes` (15), `Routing:ReservationHorizonSeconds` (30), `Routing:ReleaseHorizonSeconds` (60). Они сейчас объявлены и не используются — шаг 3 их наконец читает.

### 2.5 Таймауты и кто освобождает

| Событие | Кто | Что происходит |
|---|---|---|
| Робот проехал узел (`lastNodeId` ушёл вперёд / узел пропал из `nodeStates`) | `TrafficCoordinator.OnStateAsync` | ресурсы позади получают `PassedAt = now` и держатся ещё `ReleaseHorizonSeconds`, затем снимаются на следующем `state` или свипом |
| Новый `state` того же робота | `OnStateAsync` | `RenewedAt = now` для всего, что осталось в горизонте; недостающее из горизонта добирается (partial acquire допускается, отказ не рвёт заказ) |
| `state` не приходил дольше `ReservationHorizonSeconds` | `TrafficReservationCleanupWorker` каждые 5 с | все резервы робота снимаются, пишется `Warning` |
| Заказ перешёл в `Completed` / `Failed` / `Cancelled` / `Rejected` | `RobotOrderService` (после сохранения статуса) | `ReleaseAsync(serial)` — немедленно всё |
| Заказ провален таймаутом в `CheckTimeoutsAsync` | `CheckTimeoutsAsync` | `ReleaseAsync(serial)` |
| Остановка сервиса | — | ничего не сохраняется, состояние в памяти теряется |

Освобождает всегда **наш** код по `state` и по статусу заказа; робот освобождает неявно — тем, что двигает `lastNodeId` и перестаёт перечислять узел в `nodeStates`. Робот никогда не освобождает напрямую «по `released`»: `released` значит «разрешено войти», а не «проехал» (см. §3 АС-19).

### 2.6 Рестарт сервиса

Резервы живут в памяти процесса, поэтому после рестарта реестр пуст. Восстановление:

1. `RobotOrderWorker.RecoverAsync` (уже есть) для каждого робота с активным заказом (`vrt_RobotOrder.Status` ∈ {`Sent`, `Accepted`, `Executing`}) берёт `vrt_RobotSession.StateJson`.
2. Если состояние свежее (`now - LastSeenAt <= AgvMonitoring:StaleStateSeconds`), из него читаются `lastNodeId`, `lastNodeSequenceId`, `nodeStates`, `edgeStates`, из `vrt_RobotOrder.PlanJson` — горизонт, и резерв ставится тем же `TrafficHorizonCalculator`.
3. Состояния нет или оно старое — резерв не ставится, пишется `Warning` «резерв не восстановлен, ждём state»; первый `state` робота восстановит горизонт обычным путём.

Двух владельцев на один ресурс рестарт не создаёт: старый процесс уже мёртв (в памяти), а `vrt_*` резервов не хранит.

## 3. Автомат состояний

Статусы — существующие `RobotOrderStatuses`. Единственная точка входа — `RobotOrderService.ApplyStateAsync` (плюс `CheckTimeoutsAsync` по таймеру).

### 3.1 Полная таблица переходов

| # | Из | Событие `state` | Условие | В |
|---|---|---|---|---|
| 1 | `Sent` | `orderId` == наш, `orderUpdateId` ≥ наш | нет блокирующей ошибки | `Accepted`, `AcceptedAt = now` |
| 2 | `Sent` | блокирующая ошибка (`errorLevel` ∈ {`FATAL`, `ERROR`}) | `lastNodeId` не сдвинулся с первого узла плана | `Rejected`, `FailureReason = ROBOT_ERROR` |
| 3 | `Sent` | блокирующая ошибка | робот уже проехал хотя бы один узел плана | `Failed`, `FailureReason = ROBOT_ERROR` |
| 4 | `Accepted` | `driving == true` **или** хотя бы один узел плана `Reached` | — | `Executing` |
| 5 | `Sent`/`Accepted`/`Executing` | блокирующая ошибка | — | `Failed`, `ROBOT_ERROR` |
| 6 | `Executing` | `lastNodeId` == последний узел плана **или** == `TargetNodeId` | `driving == false` и нет действия в `RUNNING`/`WAITING` | `Completed`, `CompletedAt = now` |
| 7 | `Executing` | `orderId` пустой, у робота есть активный заказ | `lastNodeId` == `TargetNodeId` или последний узел плана | `Completed` (см. §7 Р-1) |
| 8 | `Executing` | `actionStates` содержит `cancelOrder` + `FINISHED` | — | `Cancelled` |
| 9 | `Executing` | `newBaseRequest == true` | `ReleasedNodes > 0`, `orderUpdateId < MaxOrderUpdates`, прошло ≥ 2 с с прошлой отправки, `sequenceId` вырос | `Executing` + публикация `orderUpdateId + 1` (`ExtendPlanAsync`) |
| 10 | любой активный | `orderUpdateId` < наш | — | без изменений (сообщение игнорируется) |
| 11 | любой активный | `orderUpdateId` > наш | — | наши данные не понижаются; продление **не** выполняется, `OrderUpdateId` не переписывается |
| 12 | `Executing` | нет прогресса (`lastNodeId`/`lastNodeSequenceId` не менялись) дольше `ProgressTimeoutSeconds` | — | `Failed`, `PROGRESS_TIMEOUT` |
| 13 | `Sent` | нет подтверждения дольше `AcceptTimeoutSeconds` | — | `Failed`, `NO_ACCEPT` |
| 14 | `Sent`/`Accepted`/`Executing` | `state` не приходил дольше `StateSilenceSeconds` | — | `Failed`, `STATE_SILENCE` |
| 15 | `Sent`/`Accepted`/`Executing` | оператор отправил `cancelOrder` | — | `Cancelled` (уже реализовано) |
| 16 | любой терминальный | — | — | только `ReleaseAsync`, никаких других переходов |

Терминальные: `Completed`, `Rejected`, `Failed`, `Cancelled`. Из них переходов нет.

### 3.2 Где наш код расходится с AGVControl и какой вариант правильный

| # | AGVControl | У нас сейчас | Правильно |
|---|---|---|---|
| АС-1 | Заказ закрывается, когда `nodeStates` **и** `edgeStates` пусты и все действия `FINISHED` (`HandleWithOrderAsync`, стр. 806–812) — отсутствие данных как доказательство | закрытие только по `lastNodeId` == цель/последний узел | **наш вариант**. Пустые списки — не доказательство: робот шлёт `state` и без базы. Оставляем `lastNodeId`, добавляем АС-16 |
| АС-2 | `OrderUpdateComplete(orderId, orderUpdateId - 1)` закрывает предыдущую версию заказа как `COMPLETED` при получении новой и освобождает все её узлы/рёбра | такого нет, `orderUpdateId` только сравнивается | **наш вариант**. `orderUpdateId` — счётчик версий, не признак завершения |
| АС-3 | `ReleaseNodes(agv, map)` → `ReleaseEdges(agv, map)` → резерв заново по `released` на каждом `state` (стр. 822–848), окно, когда робот не держит ничего | резервов нет вовсе | **новый вариант**: diff целевого множества, без обнуления |
| АС-4 | `_ = HandleNewBaseRequestAsync(...)` — fire-and-forget, два `state` подряд могут дать два `orderUpdateId` | `RobotOrderWorker.OnSnapshotReceived` запускает `Task.Run` на каждое сообщение; `_lastUpdate`/`_lastUpdateSequence` — два отдельных словаря (check-then-act не атомарен) | **ограничить одним обработчиком на робота** (сериализация по серийному номеру) |
| АС-5 | `if (agv == null) return;` — сообщение неизвестного робота отбрасывается целиком | зависимости от строки `Agv` нет | **наш вариант** |
| АС-6 | `state_message.EdgeStates.Any()` без проверки на `null` → `NullReferenceException` → теряется всё сообщение (батарея, резервы, позиция) | `RobotStateParser` возвращает пустой список, `null` не бывает | **наш вариант** |
| АС-7 | `GetAgvLastNodeAsync` падает `NullReferenceException` при неизвестном роботе и отдаёт 404 при неизвестном узле; `TaskWorker` на 404 получает исключение и считает «не на узле» | `LegacyAgvTaskController` уже отдаёт 200 + `null` | **наш вариант**, вынести резолв в `IRobotLastNodeResolver` |
| АС-8 | — | `MergeNodes` (стр. 963): при пустом `lastNodeId` ставит `Reached = node.Reached \|\| state.Released` | `Reached` **никогда** не выводится из `released`; при пустом `lastNodeId` `Reached` не меняется вовсе |
| АС-9 | — | `PROGRESS_TIMEOUT` считается от `AcceptedAt` (стр. 796–801), т.е. любой заказ дольше 60 с проваливается | считать от последнего прогресса (`lastNodeId`/`lastNodeSequenceId`) |
| АС-10 | — | `ApplyStateAsync` при пустом `snapshot.OrderId` сразу возвращает `null` (стр. 356–359) | если у робота есть активный заказ и `lastNodeId` доказывает финиш — закрыть заказ (АС-16); иначе — только освобождение резервов |

## 4. Что удаляем и заменяем у нас

`RobotOrderService` **не переписывается** и второй автомат не появляется: `ApplyStateAsync` остаётся единственной точкой входа, меняется начинка и добавляется сотрудник. Конкретно:

| Что | Действие | Почему |
|---|---|---|
| `RobotOrderService.ApplyStateAsync` | **остаётся**, дополняется: пустой `orderId` + активный заказ, `Reached` без `released`, порядок «действия → статус → трафик» | два автомата не нужны |
| `RobotOrderService.ExtendPlanAsync` | **остаётся** единственным путём продления; добавляется продление резерва после успешной публикации | AGVControl-перестройка маршрута не нужна |
| `RobotOrderService.CheckTimeoutsAsync` | **остаётся**; `PROGRESS_TIMEOUT` считается от прогресса; добавлен `ReleaseAsync` при провале | таймауты AGV (`_waitingTimeout`, перестройка) не переносим |
| `RobotOrderService.RecoverAsync` | **остаётся**, дополняется восстановлением резервов | — |
| `RobotOrderService.MergeNodes` | **правится**: убрать `Reached = released` (АС-8) | — |
| `VdaOrderUpdater`, `VdaOrderComposer`, `VdaOrderBuilder` | **не трогаем** | формат заказа уже принят на стенде |
| `AStarPathPlanner`, `CostFormula`, `TrafficResourceBuilder` | **не трогаем** | A* и ресурсы остаются как есть; трафик влияет только на выдачу заказа, не на стоимость |
| `TrafficManager`-аналог | **новый код**, старый отсутствует | — |
| `RobotOrderWorker.OnSnapshotReceived` | **правится**: сериализация по роботу | АС-4 |
| `LegacyAgvTaskController.LastNodeAsync` | **правится**: использует `IRobotLastNodeResolver` | не дублировать резолв в трафике |
| `AgvController.CreateOrderAsync` | **дополняется**: `TRAFFIC_BLOCKED` → 409 | контракт ошибок уже есть (`OrderInProgress` = 409) |
| `RoutePlannerOptions.ReservationHorizonSeconds` / `ReleaseHorizonNodes` / `ReleaseHorizonSeconds` | **начинают читаться**, значения не меняются | объявлены под этот шаг |

## 5. Тесты

### 5.1 Юнит (`tests/VR.RoutePlanner.Tests.Unit`)

- `Traffic/TrafficReservationRegistryTests.cs`: эксклюзив (второй владелец отказан); идемпотентность того же владельца; освобождение только владельцем; ёмкость `PARALLEL` = `Capacity`; `DIRECTIONAL` в одном направлении / отказ встречному; атомарность набора (при отказе на третьем ресурсе не остаётся ничего); `INDEPENDENT` не захватывается; узловой `EXCLUSIVE` не захватывается, `CONFLICT_ZONE` захватывается; TTL `ReservationHorizonSeconds`; грейс `ReleaseHorizonSeconds` после `PassedAt`.
- `Traffic/TrafficHorizonCalculatorTests.cs`: горизонт по `ReleaseHorizonNodes`; `sequenceId` = `2 * index`; ребра до последнего узла горизонта; пустой план; `lastNodeSequenceId` в середине плана; ребро без ресурса → `TRAFFIC_NO_RESOURCE`, не исключение.
- `Traffic/TrafficCoordinatorTests.cs`: `OnStateAsync` добирает горизонт; повторный `state` не плодит владельцев; пройденное уходит в грейс; терминальный статус снимает всё; отказ трафика не переводит заказ в `Failed`.
- `Robots/RobotOrderServiceTests.cs` (дополняется): строка 1–16 таблицы §3.1, каждая отдельным тестом; `Reached` не берётся из `released`; `PROGRESS_TIMEOUT` от прогресса (заказ 5 минут в `Executing` с растущим `lastNodeId` остаётся `Executing`); пустой `orderId` + `lastNodeId` == цель → `Completed`; два одновременных `ApplyStateAsync` дают одну публикацию продления.
- `Robots/RobotLastNodeResolverTests.cs`: известный узел → `NodeType`; неизвестный узел/нет снимка → `null`, без исключения.

### 5.2 Интеграционные (живая база `rms_vrp_test`)

- `Traffic/TrafficResourceLookupTests.cs`: на карте 801 и профиле `tusk-forklift-1102` каждый из 293 узлов даёт ровно один ресурс; число `CONFLICT_ZONE` совпадает с числом узлов степени > 2; для всех 365 рёбер ресурс разрешается по `(EdgeId, Direction)`.
- `Traffic/TrafficReservationRecoveryTests.cs`: подготовить `vrt_RobotSession` + `vrt_RobotOrder`(`Executing`) с `PlanJson`, вызвать восстановление, проверить состав реестра; повторный вызов идемпотентен; при отсутствии состояния реестр пуст и есть `Warning`.
- `Persistence/ModelSchemaTests.cs`, `Migrations/MigrationScriptTests.cs` — должны остаться зелёными и **не изменяться**.

### 5.3 Живые критерии приёмки (стенд)

1. Два робота, заказы в один узкий коридор: первый получает заказ, второй — 409 `TRAFFIC_BLOCKED` в пределах горизонта.
2. Первый завершает заказ: `vrt_RobotOrder.Status = Completed`, резервы робота исчезают не позже `ReleaseHorizonSeconds` после проезда последнего узла.
3. Рестарт `VR.RoutePlanner.Workers` в середине заказа: после подъёма следующий `state` робота восстанавливает горизонт; заказ другого робота в тот же коридор снова получает 409.
4. Остановить симулятор на время > `ReservationHorizonSeconds`: резервы робота снимаются свипом, коридор отдаётся другому роботу.
5. `GET api/AgvTask/lastnode/{serial}` на порту VR.RoutePlanner (`5292`): 200 с узлом для робота на узле и 200 с `null` для неизвестного.

## 6. Границы

- Меняются только файлы `VR.RoutePlanner`. `AGVControl` и остальные репозитории — только чтение.
- **Миграции не добавляются**, схема `vrt_*` не меняется. Появление любого файла в `**/Migrations/**` — BLOCK.
- `appsettings*.json` не меняются: новых ключей нет, дефолты `Routing:*` и `AgvMonitoring:*` те же.
- Публичные HTTP-контракты не меняются по форме: новый только код ошибки `TRAFFIC_BLOCKED` (409) в уже существующем формате `ProblemDetails`.
- A* (`AStarPathPlanner`), формула стоимости, `TrafficResourceBuilder`, `DeterministicId` — не трогаются.
- Формат VDA-заказа (`VdaOrderBuilder`, `VdaOrderUpdater`) — не трогается.
- Секретов, строк подключения, адресов стенда в диффе быть не должно.

## 7. Риски и ловушки

- **Р-1. Закрытие заказа по пустому `orderId`.** Робот может очистить `orderId` в том же сообщении, где доехал. Если смотреть только на `snapshot.OrderId`, заказ останется `Executing` и через `ProgressTimeoutSeconds` уйдёт в `Failed`, а резервы провисят лишнее. Поэтому нужен `IRobotOrderRepository.GetActiveForRobotAsync(serial)` и переход 7. Обратный риск — закрыть чужой/старый заказ: закрываем только при точном совпадении `lastNodeId` с целью плана.
- **Р-2. Два процесса.** `state` обрабатывается в `VR.RoutePlanner.Workers` (`RobotOrderWorker`), а заказ создаётся ещё и в `VR.RoutePlanner.Api` (`AgvController`), у которого свой процесс и своя пустая память. Реестр резервов — **один на процесс**, поэтому владельцем трафика объявляется только Workers: `AddInfrastructure` регистрирует `NoOpTrafficCoordinator`, а `Workers/Program.cs` переопределяет его на настоящий. Заказ, созданный через Api, получает резерв на первом `state` от робота. Если в эксплуатации Api и Workers разносятся на разные машины — резерв из Api не работает вообще, это надо явно сказать в отчёте, а не прятать.
- **Р-3. `sequenceId`.** План VDA: узел `2 * index`, ребро `2 * index + 1` (`VdaOrderUpdater`, стр. 132). Любое место, где `sequenceId` делится на 2 или сравнивается как порядковый номер узла, ломается на плане с другой нумерацией. Горизонт считать только по этому правилу и покрыть тестом.
- **Р-4. Гонка `state`.** `RobotOrderWorker` запускает `Task.Run` на каждое сообщение; два сообщения одного робота могут дать два продления с одним `orderUpdateId` и два разных резерва. Без сериализации по серийному номеру acceptance-тест на «одну публикацию» будет плавать.
- **Р-5. Ресурс без строки.** Если профиль робота не совпадает с `AgvProfile` в `vrt_TrafficResource` (робот другого типа), ресурсов нет вовсе: горизонт пустой. Это не ошибка трафика, а повод вернуть `TRAFFIC_NO_RESOURCE` и записать `Warning`, а не молча пускать.
- **Р-6. Кодировки.** `AGVControl` частично в CP1251, `doc/A297_RoutePlanner_TrafficManager.md` в CP866 — читать в правильной кодировке, наши файлы писать UTF-8. `.ps1` с кириллицей — UTF-8 **с BOM**.
- **Р-7. Чужая таблица `rms."Agvs"`.** `ILegacyAgvStateWriter` пишет в неё при `Legacy:WriteAgvsEnabled`. Шаг 3 её не трогает и семантику не меняет.

### 7.1 Дефекты AGVControl, которые переносить нельзя

- **Д-1. `OrderUpdateComplete(orderId, orderUpdateId - 1)`** — каждый новый `orderUpdateId` помечает предыдущую версию заказа выполненной и освобождает её узлы/рёбра. Приводит к «перепутанным» `orderUpdateId`/`sequenceId` и ложным завершениям.
- **Д-2. Завершение по пустым `nodeStates`/`edgeStates`** — ложное завершение, если робот прислал `state` без базы (пауза, переинициализация).
- **Д-3. `ReservePathAsync` при отказе откатывается через `ReleasePathAsync(nodes, edges, ...)`** полным списком, а не горизонтом: снимает и те резервы, что держались законно, и делает это без блокировки — окно, в которое ресурс успевает уйти другому роботу.
- **Д-4. `CheckPathAvailabilityAsync` в `OrderService` (стр. 528–540) возвращает `true; //false`** после таймаута ожидания: «путь недоступен» молча превращается в «доступен».
- **Д-5. Результат `ReservePathAsync` в `OrderService` игнорируется** (`bool reserved = ...`, дальше не используется), плюс `agv!.SerialNumber` падает при неизвестном роботе.
- **Д-6. Потеря `state`:** любое исключение в `ProcessStateMessageAsync` (в т.ч. `NullReferenceException` на `EdgeStates.Any()` и на `agv.MapId!`) отбрасывает сообщение целиком — вместе с батареей, позицией и резервами.
- **Д-7. 404 `lastnode`:** `GetAgvLastNodeAsync` → `NullReferenceException` при неизвестном роботе и 404 при неизвестном узле; `TaskWorker.AgvLastNodeCheckAsync` через `EnsureSuccessStatusCode()` превращает это в `false`.
- **Д-8. `_ = HandleNewBaseRequestAsync(...)`** — необслуженный `Task`, дубли обновлений при двух сообщениях подряд.
- **Д-9. Резерв `state_message.LastNodeId` без проверки** (стр. 658): `agv.MapId!` может быть `null`, тогда ключ резерва получается мусорным.

## 8. План проверки

```powershell
cd C:\Projects\VRouter\VR.RoutePlanner
git log --oneline 2333202..HEAD
git diff --stat 2333202..HEAD
git diff --name-only 2333202..HEAD        # ни Migrations/**, ни appsettings*.json, ни .env/.pem/.key
dotnet build VR.RoutePlanner.sln --no-incremental -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1    # живая база, пропущено 0
git -C C:\Projects\VRouter\AGVControl status --porcelain                   # пусто
```

- `dotnet ef` не запускать: миграций нет. Появилась миграция — BLOCK.
- Живой стенд, рестарт Workers, два робота и `curl` — контролёр не воспроизводит, проверяет по файлам в `doc/agents/2026-11-23-stage2.5-vda-port/evidence/`.

## 9. Приёмочная выжимка

Все утверждения проверяемы по диффу, тестам или файлу-доказательству.

**Границы**
1. Дифф только в `VR.RoutePlanner`; `git -C AGVControl status --porcelain` пуст.
2. В диффе нет `**/Migrations/**` и `appsettings*.json`; `ModelSchemaTests` и `MigrationScriptTests` зелёные и не изменены. Новых таблиц в `RoutePlannerDbContext` нет.
3. `AStarPathPlanner`, `TrafficResourceBuilder`, `VdaOrderBuilder`, `VdaOrderUpdater`, `DeterministicId` не изменены (`git diff --name-only` их не содержит).
4. Публичный HTTP не меняется, кроме нового 409 `TRAFFIC_BLOCKED` в существующем `ProblemDetails`.

**Резервирование**
5. Резерв — только в памяти: тип `TrafficReservationRegistry`, ключ `(MapVersionId, TrafficResourceId)`, ёмкость из `vrt_TrafficResource.Capacity`.
6. `INDEPENDENT` и узловые `EXCLUSIVE` (код `*-NODE-*`) не резервируются; `CONFLICT_ZONE` и коридоры C1–C4 резервируются.
7. `DIRECTIONAL`/`PARALLEL` отказывают встречному направлению; `EXCLUSIVE` — любому второму владельцу; повторный захват своим идемпотентен.
8. Набор ресурсов захватывается атомарно: при отказе не остаётся ни одного.
9. Горизонт: узлы `[lastNodeSequenceId, +2*ReleaseHorizonNodes]`, рёбра до последнего узла горизонта; `sequenceId` узла = `2*index`.
10. Проеханное уходит в грейс `ReleaseHorizonSeconds`; резерв без `state` дольше `ReservationHorizonSeconds` снимается свипом; терминальный статус снимает всё сразу.
11. После рестарта резервы восстанавливаются из `vrt_RobotSession.StateJson` + `vrt_RobotOrder.PlanJson` для роботов с активным заказом; без свежего состояния — ничего и `Warning`.
12. Заказ создаётся с 409 `TRAFFIC_BLOCKED`, если горизонт занят (тест); настоящий координатор зарегистрирован только в Workers, в Api — заглушка.

**Автомат**
13. Реализованы все 16 строк таблицы §3.1, на каждую есть тест.
14. Завершение только по положительному доказательству (`lastNodeId` == цель/последний узел, `driving == false`, нет действия в `RUNNING`/`WAITING`). Пустые `nodeStates`+`edgeStates` заказ не завершают.
15. Пустой `orderId` + `lastNodeId` == цель активного заказа → `Completed`.
16. `Reached` не выводится из `released` (в `MergeNodes` нет `Reached = ... state.Released`).
17. `PROGRESS_TIMEOUT` считается от последнего прогресса, не от `AcceptedAt`; тест: 5 минут `Executing` с растущим `lastNodeId` — заказ жив.
18. Продление (`ExtendPlanAsync`) публикуется один раз на `UpdateThrottle` даже при двух одновременных `state` (сериализация по серийному номеру).
19. `lastnode` использует `IRobotLastNodeResolver`, всегда 200 (узел либо `null`), без дублирования резолва.
20. `ApplyStateAsync` остаётся единственной точкой входа автомата; второго автомата/дублирующих таймаутов нет.

**Тесты и доказательства** (`doc/agents/2026-11-23-stage2.5-vda-port/evidence/`)
21. `tests.txt` — полный прогон `scripts/test.ps1` с живой базой, пропущено 0.
22. `traffic-409.txt` — два робота в один коридор, второй получает 409 `TRAFFIC_BLOCKED`.
23. `restart-recovery.txt` — лог Workers после рестарта: горизонт восстановлен, 409 у второго робота сохраняется.
24. `stand-run.txt` — завершение заказа: `Status=Completed`, резервы робота сняты; `lastnode` на порту VR.RoutePlanner даёт 200.
