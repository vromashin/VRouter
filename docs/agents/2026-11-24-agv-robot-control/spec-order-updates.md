# Один заказ на WMS-задачу: плечи как order update

Дата: 2026-11-24. Репозиторий `C:\Users\User\source\repos\VR.RoutePlanner`, ветка
`ai/stage2.5-vda-port-step3`, HEAD `df8468f` (база для диффа контролёра). Предыдущая спека
`spec.md` (D1-D5) реализована в `9cd32c1`, её решения не меняются.

## Цель

WMS-задача должна исполняться одним VDA-заказом: один `orderId` на всю задачу, а плечи, куски
маршрута и действия вил идут в нём как `order update` с растущим `orderUpdateId`. Новый `orderId`
на каждое действие убирается вместе с `cancelOrder` и 15-секундным ожиданием очистки состояния.

## Затронутые репозитории и проекты

- `VR.RoutePlanner` (единственный): `VR.RoutePlanner.Infrastructure` (`Kafka/SimpleTaskWorker.cs`,
  `Mqtt/VdaOrderBuilder.cs`), `VR.RoutePlanner.Application` (`Robots/RobotOrderService.cs`,
  `Robots/VdaOrderRequest.cs`, `Configuration/OrderOptions.cs`), тесты `VR.RoutePlanner.Tests.Unit`.

## Факты, на которых стоит решение

- Эталон: `ref-rev-leg1-to-pick.json` и `ref-rev-leg2-pick-drop.json` — один `orderId`
  `TSK:53ed401c-...`, `orderUpdateId` 0 и 2, публикация в одну секунду; нумерация плеча 2
  продолжает нумерацию плеча 1 (12 -> 12,14,...,24); первый узел каждого сообщения — поза робота
  с допусками 0.3/3.14; действие вил только в целевом узле плеча.
- AGVControl (`reverse-start2.md`): `cancelOrder` перед новой публикацией нет, ожидания очистки
  `state.orderId` нет, таймаута подтверждения заказа нет; `orderUpdateId` — накопленный счётчик
  вершины задачи (`AGVOrderService.cs:424-477` эталона).
- Наш поток: `SimpleTaskWorker.ExecuteAsync` создаёт новый заказ на каждое действие
  (`SimpleTaskWorker.cs:113-135`), `RobotOrderService.CreateOrderAsync` шлёт `cancelOrder`,
  ждёт до 15 с и при необходимости перезапускает симулятор (`RobotOrderService.cs:171-217`).
  Снимки `our-fixed-ORD_00185.json` / `our-fixed-ORD_00186.json`: два `orderId`, у обоих
  `orderUpdateId = 0`, нумерация узлов начинается с 0 -> робот не принимает следующий заказ.

## Решение

### Д-1. Один `orderId` на WMS-задачу

Первый цикл `SimpleTaskWorker` создаёт заказ по первому действию задачи, остальные действия —
`AppendLegAsync` в тот же заказ (второй `orderId` не создаётся, `IRobotOrderNumberSource`
вызывается один раз на задачу).

`RobotOrderService` получает два публичных метода:

- `CreateOrderAsync(mapId, serialNumber, targetNodeId, hasLoad, ct)` — как сейчас, плюс
  перегрузка `CreateTaskOrderAsync(mapId, serialNumber, firstLegTargetNodeId, hasLoad,
  finalTargetNodeId, ct)`: маршрут строится до `firstLegTargetNodeId`, а `RobotOrder.TargetNodeId`
  сохраняется равным `finalTargetNodeId` (цель всей задачи). `AgvController` продолжает
  вызывать `CreateOrderAsync` (`AgvController.cs:74`) без изменений контракта.
- `AppendLegAsync(string orderId, string targetNodeId, bool hasLoad, CancellationToken ct)`:
  строит маршрут от текущего узла робота до `targetNodeId`, собирает сообщение с
  `orderUpdateId = order.OrderUpdateId + 1` и публикует его; заменяет `PlanJson`, `Nodes`,
  `ReleasedNodeCount` и `OrderUpdateId` заказа.

`AppendLegAsync` обязан брать тот же per-робот `SemaphoreSlim _serialGates`
(`RobotOrderService.cs:78,398`), что и `ApplyStateAsync`: иначе продление по `state` и смена
плеча могут опубликовать один и тот же `orderUpdateId` (АС-4).

### Д-2. Нумерация `sequenceId` продолжается через плоские сообщения

`VdaOrderRequest` получает поле `int SequenceIdBase = 0`. В `VdaOrderBuilder`:
узел `sequenceId = SequenceIdBase + index*2` (`VdaOrderBuilder.cs:342`), ребро
`SequenceIdBase + index*2 + 1` (`:371`), виртуальный узел старта `SequenceIdBase` (`:458`),
при `VirtualStart` реальные узлы сдвигаются на 2 сверх базы. Первое сообщение заказа — база 0;
первое сообщение плеча N — база, равная `sequenceId` последнего узла предыдущего сообщения
(эталон: 12 -> 12). Обновления внутри плеча по-прежнему собирает `VdaOrderUpdater.BuildUpdate`,
он сохраняет номера из плана, менять его не нужно.

### Д-3. Первый узел каждого сообщения — поза робота

Правило `first && request.OrderUpdateId <= 0` (`VdaOrderBuilder.cs:297`) заменяется на
`first && request.SliceStartIndex == 0 && request.FirstNodeIsPose`: узел получает фактические
`RobotX/RobotY`, `theta` при известном курсе (кроме узла позы при стоянке) и допуски
0.3 / 3.14. `FirstNodeIsPose` — новое поле `VdaOrderRequest`, `true` для первого сообщения
плеча (и заказа, и `AppendLegAsync`). Остальные узлы — 0.1 / 0.1 (`:316-317`), как сейчас.

### Д-4. Действия вил: по одному в целевом узле своего плеча

Сохраняются правила D1 спеки `spec.md`: действие вил только в последнем узле плана сообщения
(`VdaOrderBuilder.cs:254,324-327`), курс к паллете и точка остановки 1.475 м
(`WorkingNodeStopResolver`), `blockingType: SOFT`. Одно сообщение содержит не более одного
действия вил; в заказе (по всем его обновлениям) их столько же, сколько действий в WMS-задаче,
и каждое стоит в узле своего плеча. Параметры `containerX/containerY/containerTheta` —
по правилам D1 из `spec.md` (точка остановки + курс к паллете), без изменений.

`TerminalActionType` (`OrderOptions`, `VdaOrderBuilder.cs:330-337`) передаётся только в
последнем плече задачи; в промежуточных плечах — `null`.

### Д-5. Следующее плечо — только после подтверждения предыдущего

`AppendLegAsync` публикует сообщение, только если по последнему `state` предыдущее плечо
завершено: робот не `Driving`, `snapshot.LastNodeId` равен цели предыдущего плеча и
`snapshot.LastNodeSequenceId` равен `sequenceId` последнего узла предыдущего сообщения (если
номер не пришёл — достаточно совпадения `LastNodeId` и `!Driving`), и нет действий в
`RUNNING/WAITING`, а действие плеча не в `FAILED`. Иначе — `OrderRejectedException` с причиной
`LEG_NOT_FINISHED`, публикации нет. Это же условие проверяет воркер перед вызовом.

Причина: `blockingType: SOFT` не удерживает робота на узле, поэтому запланированное заранее
второе плечо позволяло бы ему поехать к выгрузке с незавершённым `pick`. Приоритет —
сохранность груза и стеллажей, а не время цикла. Расхождение с эталоном (AGVControl публикует
куски подряд) осознанное и должно быть отражено в коммите.

### Д-6. Завершение задачи

Заказ закрывается `Completed` только когда достигнута цель всей задачи:
`status == Executing && !snapshot.Driving && !HasRunningAction(snapshot) && reachedLast &&
snapshot.LastNodeId == order.TargetNodeId`, где `reachedLast` — последний узел текущего
сообщения отмечен достигнутым в `MergeNodes` (`RobotOrderService.cs:1043`). Условия
`IsLastPlanNode` и `reachedTarget` из проверки завершения убираются (`:465-475`): иначе конец
первого плеча закрывает задачу. `CloseWithoutOrderIdAsync` (`:543-552`) тоже не должен
закрывать заказ по последнему узлу плана — только по `TargetNodeId`.

`Rejected`, `Failed`, `Cancelled` — без изменений. `SimpleTaskWorker`: `Completed` -> Kafka
`Finished`, любой иной терминальный статус -> `Failed`.

### Д-7. Стационарный случай

- Робот уже в целевом узле плеча и узел рабочий: сборка как в D2 `spec.md` — узел позы
  (допуски 0.3/3.14, без действия и без курса), дубль узла с курсом к паллете и действием,
  виртуальное ребро `VIRTUAL_START_<nodeId>` с `rotationAllowed: false`; номера сдвигаются на
  `SequenceIdBase` (поза — база, ребро — база+1, дубль — база+2).
- То же для плеча N > 1: стоянка публикуется как `order update` с базой предыдущего сообщения
  (`pick` и `drop` в одном узле — рабочий случай, должен поддерживаться).
- Робот в целевом узле плеча, узел не рабочий: движения и действий нет, сообщение не
  публикуется, плечо считается выполненным (запись в журнал). Для первого плеча задачи
  сохраняется текущее поведение (`RobotOrderService.cs:241-268`).

### Д-8. Убрать `cancelOrder` и ожидание очистки

Блок `RobotOrderService.cs:171-217` удаляется целиком (instantAction `cancelOrder`, ожидание до
15 с, перезапуск симулятора перед заказом). Публикация заказа идёт сразу после готовности
маршрута. Ручной `cancelOrder` через HTTP (`AgvController.InstantActionAsync`) не трогать.
Параметр конструктора `ISimulatorResetService? simulatorReset = null` (`:110`) остаётся
(необязательный, DI не ломает); поле `_simulatorReset` удаляется, если после правки компилятор
ругается на неиспользуемое поле.

### Д-9. Робот не подтвердил update

- Сообщение публикуется один раз. Повтор с тем же или меньшим `orderUpdateId` запрещён,
  `cancelOrder` в автоматическом потоке не отправляется ни при каком исходе.
- Факт публикации update запоминается в памяти: `(serial, orderId, updateId, момент)`.
  Если в `CheckTimeoutsAsync` заказ в `Executing`, а `snapshot.OrderUpdateId < order.OrderUpdateId`
  дольше `Routing:Order:UpdateAcceptTimeoutSeconds` (новый параметр, по умолчанию 10 с),
  заказ переводится в `Failed` с причиной `UPDATE_NOT_ACCEPTED` (новая константа рядом с
  `ReasonNoAccept`), резерв трафика освобождается, задача закрывается `Failed`.
- Робот при этом остаётся на последнем узле предыдущего подтверждённого сообщения (груз на
  вилах не теряется), новых команд ему не уходит, решение принимает оператор. После рестарта
  сервиса память о публикации теряется — тогда работает только `ProgressTimeoutSeconds` (60 с).
- Ошибка действия вил (`FAILED` в `actionStates`) — `Failed` с причиной `ROBOT_ERROR`;
  `cancelOrder` не отправляется.

## Критерии приёмки

Юнит-тесты (`VR.RoutePlanner.Tests.Unit`):

1. `VdaOrderBuilder`: `SequenceIdBase = 12` даёт узлы 12,14,16 и ребра 13,15; при `VirtualStart`
   виртуальный узел 12, первый узел маршрута 14.
2. `VdaOrderBuilder`: у сообщения с `OrderUpdateId = 1, FirstNodeIsPose = true` первый узел —
   `RobotX/RobotY` с допусками 0.3/3.14 и `theta` курса робота; остальные узлы 0.1/0.1.
3. `VdaOrderBuilder`: в сообщении с `OrderUpdateId = 1` действие вил стоит ровно в одном
   узле — последнем; `TerminalActionType` присутствует только когда передан.
4. `VdaOrderService`: `CreateTaskOrderAsync` публикует одно сообщение с `orderUpdateId = 0`,
   `RobotOrder.TargetNodeId == finalTargetNodeId`, номер заказа запрошен один раз.
5. `AppendLegAsync` публикует второе сообщение с тем же `orderId`, `orderUpdateId = 1`, базой
   номеров из первого сообщения, действием вил в цели второго плеча и `TargetNodeId` без
   изменений.
6. `AppendLegAsync` при незавершённом предыдущем плече (робот `Driving` или `LastNodeId` не равен
   цели плеча) бросает `OrderRejectedException(LEG_NOT_FINISHED)` и ничего не публикует.
7. `AppendLegAsync` при `from == to` для рабочего узла публикует стоянку (поза, виртуальное
   ребро, дубль с действием) с базой предыдущего сообщения.
8. Переходы статусов: конец промежуточного плеча не даёт `Completed`; `Completed` — только при
   `LastNodeId == TargetNodeId` и достигнутом последнем узле сообщения.
9. `CheckTimeoutsAsync`: робот не поднял `orderUpdateId` за `UpdateAcceptTimeoutSeconds` ->
   `Failed` / `UPDATE_NOT_ACCEPTED`, резерв освобождён.
10. `CreateOrderAsync` не содержит `cancelOrder`: после вызова в `FakePublisher.Actions` пусто,
    задержек нет (тест с `TestTimeProvider` завершается без сдвига времени).
11. `SimpleTaskWorker`: задача из двух действий создаёт один заказ (один вызов
    `IRobotOrderNumberSource.NextAsync`), второе действие уходит как `AppendLegAsync`.

Интеграционные тесты: миграции не менялись (`MigrationScriptTests`, `ModelSchemaTests`,
`MigrationApplyTests` зелёные), схема `vrt_RobotOrder`/`vrt_RobotOrderNode` без диффа.

Живой прогон на стенде 131 (симулятор `Tuskrobots/98001`, карта 801), задача
`pick wo-2-7 -> drop wo-1-6`, робот стоит в `wo-1-6`:

12. В MQTT ровно один `orderId` на задачу, `orderUpdateId` строго возрастает, публикаций
    `cancelOrder` нет, нового `orderId` при живом первом нет; между сменой плеч нет паузы 15 с.
13. В `vrt_RobotOrder` одна строка на задачу: `Status` последовательно `Sent` -> `Executing` ->
    `Completed`, `OrderUpdateId >= 1`, `TargetNodeId` = узел выгрузки, `FailureReason` пуст.
14. Сравнение с эталоном по полям (по объединению сообщений задачи): `orderId` един, набор узлов
    и действий совпадает с `ref-rev-leg1-to-pick.json` + `ref-rev-leg2-pick-drop.json` по
    `nodeId`, `sequenceId` (глобальная нумерация), `released`, `nodePosition.x/y`, наличие
    `theta`, `allowedDeviationXY/Theta` (первый узел 0.3/3.14, остальные 0.1/0.1),
    `actions[].actionType/blockingType/actionParameters` (`containerTypeId`, `containerX`,
    `containerY`, `containerTheta`, `onlyFork`, `autoReturnNode`, `noScan`, `adjustWithoutOA`),
    ребра: `edgeId`, `sequenceId`, `startNodeId`, `endNodeId`, `orientation`, `orientationType`,
    `released`, `trajectory.degree/knotVector/controlPoints`; отсутствие `maxSpeed` и
    `maxRotationSpeed`; действие вил не стоит ни в одном узле отправления.
15. WMS-события: `Running`, затем один `Finished`; `Failed` не приходит.
16. `dotnet build VR.RoutePlanner.sln -c Release` без предупреждений, `scripts/test.ps1` —
    все наборы зелёные, пропусков 0.

## Границы

- Менять можно только `VR.RoutePlanner`. Чужие репозитории, `AGVControl` (код) — нет.
- Не менять: схему и миграции (`vrt_RobotOrder`, `vrt_RobotOrderNode`, `Migrations/*`), таблицы
  `rms."Agvs"`/`public."Agvs"`, планировщик A* (`AStarPathPlanner`), `TrafficCoordinator`,
  HTTP-контракты `AgvController`/`RouteController`, решения D1-D5 из `spec.md`, поведение ручного
  `cancelOrder`/`pause`/`resume` через instantActions.
- Не менять существующие значения по умолчанию в `Routing:Order`. Новый параметр
  `UpdateAcceptTimeoutSeconds = 10` — единственное добавление, оно проверяется в
  `OrderOptions.Validate()`.

## Риски и ловушки

- **История плеч не сохраняется.** `vrt_RobotOrder.PlanJson` и `vrt_RobotOrderNode` хранят только
  текущее плечо (миграции запрещены); после смены плеча узлы прошлого плеча из таблицы исчезают.
  Для разбора нужны журнальные записи: на каждую публикацию логировать `orderId`,
  `orderUpdateId`, диапазон `sequenceId` и цель плеча.
- **`MaxOrderUpdates` (32) общий** для продлений внутри плеча и для смены плеч. Если предел
  достигнут, `AppendLegAsync` возвращает отказ с явной причиной, а не молчит; задача
  закрывается `Failed`.
- **Гонка продления и смены плеча** — только через общий `_serialGates`; тест на два
  параллельных `ApplyStateAsync` (`ApplyState_TwoStatesInParallel_PublishSingleExtension`) должен
  остаться зелёным.
- **Гейт по `state` может не сработать** у робота, который не присылает `LastNodeSequenceId`;
  тогда работает резервное условие (`LastNodeId` + `!Driving`), а `ProgressTimeoutSeconds`
  страхует зависание. Симулятор стенда `LastNodeSequenceId` присылает.
- **`state.orderId` остаётся от завершённого заказа** — это норма, `cancelOrder` больше не
  отправляется; ошибочный `NO_ACCEPT` теперь виден только через `AcceptTimeoutSeconds` при
  создании новой задачи.
- **Резерв трафика живёт на `PlanJson`** (`TrafficCoordinator.OnStateAsync`): после смены плеча
  горизонт пересобирается по новому плану, ручного освобождения между плечами нет; при
  `Failed`/`Completed` резерв освобождается как сейчас.
- **Точка остановки 1.475 м**: после `pick` робот стоит не в узле, а у паллеты. Первый узел
  следующего плеча — фактические координаты робота (0.3/3.14), поэтому допуск соблюдается;
  подменять координаты узла карты нельзя.
- Кодировка: часть комментариев в репозитории в CP1251 (`RobotStartNodeResolver.cs:22-23`) —
  правки в этих файлах не должны её ломать.
- `ISimulatorResetService` после удаления блока остаётся в DI; если поле стало неиспользуемым,
  удалять его вместе с параметром, а не глушить предупреждение.

## Что придётся переделать в существующих тестах

- `VdaOrderReferenceTests`: `Build_StartAtWorkingNode_DepartureNodeHasNoActionAndTheta` и
  `Build_FirstMessageFromWorkingNode_NoPickOnDeparture` ожидают у первого узла обновления
  «обычный узел плана» — теперь это поза робота (передать `firstNodeIsPose` и проверить
  допуски/курс). Стационарные проверки не меняются.
- `OrderUpdateTests`: `ApplyState_CompletedOrder_DoesNotPublishUpdate` и любые проверки
  завершения по последнему узлу плана — переписать под правило «`LastNodeId == TargetNodeId`».
- `RobotOrderServiceTests`: `Row6_Executing_OnLastPlanNodeOrTarget_Completed` — цель отличается от
  последнего узла плана, ожидание `Completed` меняется на `Executing`;
  `Row7_EmptyOrderId_AtTarget_ClosesOrder` — проверить, что `TargetNodeId` в фикстуре совпадает.
  Добавить тесты 4-9 из списка приёмки; тест 10 требует проверки, что `FakePublisher.Actions` пуст.
- `OrderChunkingTests`: без изменений по смыслу, проверить, что `TargetNodeId` в
  `CreateOrder_TargetNode_IsStoredForCompletion` равен цели плеча (в задаче из одного действия
  это одно и то же значение).
- `AgvControllerTests`: контракт HTTP не меняется, тесты должны остаться зелёными.

## План проверки

Контролёр сам запускает:

```
dotnet build VR.RoutePlanner.sln -c Release
scripts/test.ps1
git -C <repo> diff --stat df8468f..HEAD
git -C <repo> diff df8468f..HEAD -- src/VR.RoutePlanner.Application/Robots/RobotOrderService.cs
git -C <repo> diff df8468f..HEAD -- src/VR.RoutePlanner.Infrastructure/Mqtt/VdaOrderBuilder.cs
git -C <repo> diff df8468f..HEAD -- src/VR.RoutePlanner.Infrastructure/Kafka/SimpleTaskWorker.cs
```

Дополнительно: `git status` — нет новых файлов в `src/VR.RoutePlanner.Infrastructure/Migrations`,
нет изменений в `AStarPathPlanner`, `TrafficCoordinator`, `AgvController`, `RouteController`;
`grep -n "cancelOrder" src/VR.RoutePlanner.Application/Robots/RobotOrderService.cs` — только в
`HasFinishedCancelAction`.

Живой прогон (владелец, стенд 131) кладёт в `doc/agents/2026-11-24-agv-robot-control/evidence/`:
`stand-upd-<orderId>-u0.json`, `stand-upd-<orderId>-u1.json` (сырые MQTT-сообщения задачи),
`stand-upd-db.txt` (вывод `select "OrderId","Status","OrderUpdateId","TargetNodeId",
"FailureReason" from vrt_RobotOrder order by "CreatedAt" desc limit 3;` и
`select "OrderId","SequenceId","NodeId","Released","Reached","ActionCount" from
vrt_RobotOrderNode order by "SequenceId";`), `stand-upd-kafka.log` (события задачи),
`stand-upd-diff.txt` (результат сравнения с `ref-rev-leg1-to-pick.json` и
`ref-rev-leg2-pick-drop.json` по полям из п.14).

## Приёмочная выжимка

Проверяемо по диффу и тестам:

1. `VdaOrderRequest` содержит `SequenceIdBase` и `FirstNodeIsPose`; в `VdaOrderBuilder` номера
   узлов = `SequenceIdBase + index*2`, ребер = `+1`, виртуальный узел старта = `SequenceIdBase`
   (`VdaOrderBuilder.cs:342,371,458`).
2. Первый узел сообщения при `FirstNodeIsPose && SliceStartIndex == 0` получает `RobotX/RobotY`,
   `theta` при известном курсе и допуски 0.3/3.14; остальные узлы 0.1/0.1.
3. Блок `cancelOrder` + ожидание очистки удалён из `RobotOrderService.CreateOrderAsync`
   (был `:171-217`); в `RobotOrderService` слово `cancelOrder` встречается только в
   `HasFinishedCancelAction`.
4. Есть `CreateTaskOrderAsync(..., finalTargetNodeId, ct)` (или перегрузка `CreateOrderAsync`):
   `RobotOrder.TargetNodeId` = цель всей задачи, маршрут = цель первого плеча.
5. Есть `AppendLegAsync(orderId, targetNodeId, hasLoad, ct)`: публикует сообщение с тем же
   `orderId`, `orderUpdateId = Order.OrderUpdateId + 1`, базой номеров из предыдущего сообщения,
   действием вил в цели плеча; при незавершённом предыдущем плече бросает
   `OrderRejectedException(LEG_NOT_FINISHED)` и не публикует; берёт общий `_serialGates`.
6. `SimpleTaskWorker` вызывает `AppendLegAsync` вместо `CreateOrderAsync` для действий со
   второго; `IRobotOrderNumberSource.NextAsync` вызывается один раз на задачу.
7. Завершение: `Completed` только при `Executing && !Driving && !HasRunningAction &&
   достигнут последний узел сообщения && snapshot.LastNodeId == order.TargetNodeId`;
   `IsLastPlanNode` не участвует в завершении и в `CloseWithoutOrderIdAsync`.
8. `OrderOptions.UpdateAcceptTimeoutSeconds` (10 с) с валидацией в `Validate()`;
   причина `UPDATE_NOT_ACCEPTED`; при неподтверждённом update — `Failed`, резерв освобождён,
   `cancelOrder` не отправляется.
9. Тесты: `SequenceIdBase` (12/14/16), поза в первом узле обновления, одно действие вил в
   последнем узле, второй `orderUpdateId = 1` с тем же `orderId`, отказ `LEG_NOT_FINISHED`,
   стоянка как update, конец промежуточного плеча не `Completed`, `UPDATE_NOT_ACCEPTED`,
   отсутствие `cancelOrder` при создании заказа, один номер заказа на два действия.
10. `dotnet build VR.RoutePlanner.sln -c Release` — 0 предупреждений, 0 ошибок;
    `scripts/test.ps1` — Unit/Integration/Contracts зелёные, пропусков 0.
11. Нет новых файлов в `src/VR.RoutePlanner.Infrastructure/Migrations`, нет диффа в
    `AStarPathPlanner`, `TrafficCoordinator`, `AgvController`, `RouteController`,
    схема `vrt_RobotOrder`/`vrt_RobotOrderNode` не менялась.
12. Стенд 131 (владелец кладёт файлы в `doc/agents/2026-11-24-agv-robot-control/evidence/`,
    контролёр проверяет по ним п.12-16 тела спеки):
    `stand-upd-<orderId>-u0.json`, `stand-upd-<orderId>-u1.json` — одни `orderId`,
    `orderUpdateId` 0 и 1, глобальная нумерация, поза в первом узле, одно действие вил в цели
    плеча, нет `maxSpeed`/`maxRotationSpeed`;
    `stand-upd-db.txt` — одна строка `vrt_RobotOrder` со `Status = Completed`,
    `OrderUpdateId >= 1`, `TargetNodeId` = узел выгрузки, `FailureReason` пуст;
    `stand-upd-kafka.log` — `Running` и один `Finished`;
    `stand-upd-diff.txt` — совпадение с `ref-rev-leg1-to-pick.json` + `ref-rev-leg2-pick-drop.json`
    по полям из п.14 тела спеки.
    Если файла нет — пункт считается непроверенным, вердикт `BLOCK` на живую часть.
