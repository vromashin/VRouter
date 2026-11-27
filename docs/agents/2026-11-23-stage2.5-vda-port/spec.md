# Шаг 2.5: перенос сборки и ведения VDA-заказа из AGVControl в VR.RoutePlanner

Ветка: `ai/stage2.5-vda`, база диффа `bd9cb69`. Каталог артефактов: `doc/agents/2026-11-23-stage2.5-vda-port`.

## Цель

Воспроизвести в `VR.RoutePlanner` сборку и ведение VDA-заказа один-в-один с `AGVControl`
(точка остановки перед рабочим узлом, параметры действий вил, ориентация рёбер, базовый план
и обновления по `newBaseRequest`), сохранив наш планировщик A* и наши таблицы `vrt_*`,
и исправив четыре известных дефекта AGVControl.

## Затронутые репозитории и проекты

- `VR.RoutePlanner` (`C:\Users\User\source\repos\VR.RoutePlanner`, он же `C:\Projects\VRouter\VR.RoutePlanner`):
  `VR.RoutePlanner.Domain`, `VR.RoutePlanner.Application`, `VR.RoutePlanner.Infrastructure`,
  `VR.RoutePlanner.Api`, `VR.RoutePlanner.Workers`, тесты. Правки только здесь.
- `AGVControl` — эталон, только чтение. Ни одного изменённого файла.
- Остальные репозитории workspace (`RMS.Store.Map`, `RMS.WMS.Common`, `ntls.*`) — не трогать.

## 1. Таблица соответствия

| AGVControl | У нас | Что делаем |
|---|---|---|
| `AGVControlApi/Data/OrderService.cs:1079-1288` `MakeNodesAndEdgesForOrderAsync` | `src/VR.RoutePlanner.Infrastructure/Mqtt/VdaOrderBuilder.cs` | Переписать сборку узлов/рёбер/действий по правилам раздела 2 |
| `OrderService.cs:1391-1450` `PositionBeforeWorkingNode` | новый `src/VR.RoutePlanner.Application/Robots/WorkingNodeStopResolver.cs` | Портировать расчёт точки подхода и курса |
| `AGVModels/Def/Node.cs:100-136` (`WrkLength`, `AgvPositionX/Y_Front/Rear`), `Node.cs:182` `AgvStopPositionXY` | новый `src/VR.RoutePlanner.Domain/Graph/WorkingNodeGeometry.cs` | Портировать геометрию остановки, `WrkLength = 1.475` |
| `OrderService.cs:1616-1674` `GetForkliftActionsByNode` | `VdaOrderBuilder` (сборка actions) | Портировать параметры `pick`/`drop` |
| `OrderService.cs:1968-1984` `SplitPath`, `:2084`, `:2198` (базовые куски, `Routing:BaseNodesCount`) | `RobotOrderService.CreateOrderAsync` + новый `src/VR.RoutePlanner.Application/Robots/OrderChunkPlanner.cs` | Нарезка по `Routing:Order:ReleasedNodes`: `<= 0` — весь маршрут в одном сообщении, всё `released = true`; `> 0` — куски по N узлов с перекрытием 1 |
| `OrderService.cs:1454-1499` (`SendOrderToAgvInternalAsync`, поза робота в первом узле, допуски 0.3/3.14) | `RobotOrderService.CreateOrderAsync`, `VdaOrderBuilder.Build` | Сохранить, `theta` первого узла — по `Routing:Order:FirstNodeTheta` (по умолчанию не писать) |
| `AGVModels/State/StateMessage.cs` | `src/VR.RoutePlanner.Application/Robots/RobotSnapshot.cs`, `src/VR.RoutePlanner.Infrastructure/Mqtt/RobotStateParser.cs` | Добавить `edgeStates`, `distanceSinceLastNode` не нужен |
| `AGVControlApi/Monitor/StateMonitorService.cs:1098-1181` `HandleNewBaseRequestAsync` | `RobotOrderService.ExtendPlanAsync` | Условия, троттлинг, порядок `sequenceId` |
| `StateMonitorService.cs:621-973` (`ProcessStateMessageAsync`, `HandleWithOrderAsync`, `HandleActionsAsync`) | `RobotOrderService.ApplyStateAsync` | Правила статусов, ошибок, `lastNodeId`, `driving` |
| `AGVControlApi/Routing/RouteCreator.cs:155-620` `CreateRouteUpdateAsync` | `src/VR.RoutePlanner.Application/Robots/VdaOrderUpdater.cs` | Нарезка плана с сохранением исходной нумерации |
| `AGVControlApi/Services/AGVOrderService.cs:606` `GetAgvLastNodeAsync` + контроллер `api/AgvTask/lastnode/{serial}` | новый `src/VR.RoutePlanner.Api/Controllers/LegacyAgvTaskController.cs` | Всегда 200, без 404 |
| `AGVControlApi/Services/AgvInstantActionsService.cs:461-508` `ExecutePickDropAsync` | `RobotOrderService.TriggerForkAsync` | Параметры как в разделе 2.3; дефект `agv.PositionX ?? 0 + distance*cos` **не** переносить |

## 2. Точные правила сборки узла, ребра и действия

Общее: `version = "2.1.0"`, `manufacturer`, `serialNumber`, `orderId = ORD_%05d`, `orderUpdateId`,
`headerId` из `IHeaderIdSource`, `timestamp` в формате `yyyy-MM-ddTHH:mm:ss.fffZ`.
Сообщение = `headerId, timestamp, version, manufacturer, serialNumber, orderId, orderUpdateId, nodes, edges`.

### 2.1 Узел

Для узла с глобальным индексом `i` в полном плане маршрута (нумерация **не** пересчитывается
при нарезке на куски и обновления):

- `nodeId` = `WarehouseNode.NodeId`;
- `sequenceId` = `2 * i`;
- `released` = `true` (весь отправленный кусок выпущен, горизонта внутри сообщения нет);
- `nodePosition.mapId` = идентификатор карты заказа;
- `nodePosition.allowedDeviationXY` = `max(node.AllowedDeviationXY ?? 0, 0.1)`;
- `nodePosition.allowedDeviationTheta` = `max(node.AllowedDeviationTheta ?? 0, 0.1)`;
- `nodePosition.x/y` = координаты узла, кроме случая 2.2;
- `nodeDescription` = описание узла из графа, при пустом — `"{}"`;
- `actions` = действия по 2.3.

Особый случай — первый узел сообщения при `orderUpdateId = 0` (как `SendOrderToAgvInternalAsync`):

- `nodePosition.x` = `snapshot.PositionX`, `nodePosition.y` = `snapshot.PositionY`;
- `allowedDeviationXY = 0.3`, `allowedDeviationTheta = 3.14`;
- `nodePosition.theta` определяется новым параметром `Routing:Order:FirstNodeTheta`
  (`OrderOptions.FirstNodeTheta`, `bool`, по умолчанию `false`):
  - `false` (по умолчанию) — поле `theta` в `nodePosition` первого узла **отсутствует в JSON**.
    Так собран живой заказ старого AGV, снятый 23.11: у первого узла `theta` нет,
    `allowedDeviationTheta = 3.14`;
  - `true` — `nodePosition.theta` = `snapshot.Theta`; если поза робота неизвестна — `node.Theta`
    (поле присутствует всегда).

Почему по умолчанию `false`: `allowedDeviationTheta = 3.14` уже разрешает роботу принять любой курс,
поэтому `theta` не добавляет ничего к приёму заказа, а любой выдуманный или устаревший `snapshot.Theta`
(снимок `state` на стенде приходит редко) заставляет робота доворачивать на старте. Отсутствие поля
воспроизводит эталон, который на стенде доезжал. Включение `true` — только конфигом, без правки кода.

Курс узла (`nodePosition.theta`) для остальных узлов:

- `LOADING`, `UNLOADING` → `theta` = курс подхода из 2.2 (курс на стеллаж);
- `WORKING`, `WAYPOINT`, `PARKING`, `CHARGING` → поле `theta` отсутствует в JSON;
- узел слота `wo-*` обрабатывается как рабочий (см. 2.2).

### 2.2 Точка остановки перед рабочим узлом (`PositionBeforeWorkingNode` + `AgvStopPositionXY`)

Рабочими считаются узлы с типом `LOADING`, `UNLOADING`, `WORKING` и узлы с префиксом `wo-`
(слоты рабочего места; на карте 801 они и есть цели WMS).

Точка подхода и курс для узла `N` с индексом `i`:

1. Входящие узлы `N` — все узлы `P`, для которых есть ребро `P → N`; для двунаправленного ребра
   входящим считается и узел на другом конце. Дубликаты по `nodeId` убираются.
2. Если входящий узел ровно один — `A` = он, `theta = atan2(N.Y - A.Y, N.X - A.X)`.
3. Иначе, если `i > 0` — `A` = узел `i-1` маршрута, `theta = atan2(N.Y - A.Y, N.X - A.X)`.
4. Иначе (первый узел, несколько входящих) — `A` = текущая поза робота, `theta = 0`.

Точка остановки (`WrkLength = (Pallet.Length 1.2 + Pallet.AgvLength 1.25) / 2 + Pallet.Span 0.25 = 1.475` м):

- `Front = (N.X - 1.475*cos(theta), N.Y - 1.475*sin(theta))`;
- `Rear = (N.X + 1.475*cos(theta), N.Y + 1.475*sin(theta))`;
- выбирается точка, ближайшая к `A` (в нормальном случае — `Front`).

`nodePosition.x/y` рабочего узла = выбранная точка остановки; `containerTheta` действия = `theta`.
Координаты паллеты (`containerX/containerY`) — координаты **узла** `N`, не точки остановки.

Если маршрут всё же построен до узла подхода (`ApproachNodeResolver`, у слота нет проезжих рёбер),
узел слота в план не попадает: точку остановки не смещаем, `pick`/`drop` ставится на узле подхода,
`containerX/Y` = координаты узла слота, `containerTheta` = курс от узла подхода на слот,
`nodePosition.theta` = этот же курс для `LOADING`/`UNLOADING` и отсутствует для `WORKING`.

### 2.3 Действие `pick` / `drop`

Действие добавляется в `actions` рабочего узла, если `Routing:Order:SendForkActions = true`.

- `actionType`: `LOADING` → `pick`; `UNLOADING` → `drop`; прочие рабочие узлы и слоты `wo-*` →
  `pick`, если робот едет без груза (`hasLoad = false`), иначе `drop`;
- `actionId` = `Guid.NewGuid().ToString()`;
- `actionDescription` = `actionType`;
- `blockingType` = `"SOFT"`;
- `actionParameters` — ровно восемь записей `{key, value}` в этом порядке:
  `containerTypeId = 2` (число), `containerX` = `N.X`, `containerY` = `N.Y`,
  `containerTheta` = `theta` из 2.2, `onlyFork = true`, `autoReturnNode = true`,
  `noScan = false`, `adjustWithoutOA = true`.

Если `SendForkActions = false` — `actions` пуст, кроме завершающего действия из 2.4.

### 2.4 Завершающее действие (наш стендовый обход)

Если `Routing:Order:TerminalActionType` задан и узел — последний узел **всего плана**,
в его `actions` добавляется действие с этим типом (по умолчанию `detectObject`),
`blockingType = "SOFT"`, `actionId` = GUID, `actionDescription` = тип,
единственный параметр `loadId` = `orderId`. Правило не менять: без действия в конечной точке
симулятор не начинает движение.

### 2.5 Ребро

Для ребра между узлами `i` и `i+1`:

- `edgeId` = `WarehouseEdge.EdgeId`; при отсутствии ребра в графе — виртуальное ребро
  `VIRTUAL_START_<nodeId>` с `orientationType = null`, `orientation = null`, `rotationAllowed = false`
  (как `CreateVirtualEdge` в AGV);
- `sequenceId` = `2 * i + 1` (всегда нечётный, всегда между своими узлами);
- `released` = `true`;
- `startNodeId` / `endNodeId` — узлы в направлении движения (при обратном прохождении меняются местами);
- `maxSpeed` = `edge.MaxSpeed`;
- `maxRotationSpeed` = `edge.MaxRotationSpeed` (поле отсутствует, если в БД `NULL`);
- `orientationType` = `edge.OrientationType`, при пустом — `"TANGENTIAL"`;
- `orientation`: у **первого** ребра сообщения `PI`, если первый сегмент маршрута проходится против
  направления ребра (`RouteEdgeDirection.Backward`), иначе `0`; у всех последующих рёбер `0`
  (как в AGV: «далее всегда движемся передом по ордеру»);
- `trajectory` = `{ degree: 1, knotVector: [0,0,1,1], controlPoints: [{x,y,weight:1}, {x,y,weight:1}] }`,
  точки — первая и последняя точки ребра в направлении движения (для обратного прохождения
  список разворачивается);
- `actions` = пустой массив.

### 2.6 Проверки при сборке

- Маршрут обязан быть связным: `segment[i].EdgeCode` начинается в узле, где закончился предыдущий,
  иначе `InvalidOperationException` (как сейчас).
- Число узлов = число рёбер + 1, иначе исключение.
- Все `sequenceId` узлов чётные, рёбер — нечётные, строго возрастают.

## 3. Базовый план, горизонт и обновления

### 3.1 Режим отправки: весь маршрут или куски

Режим задаётся значением `Routing:Order:ReleasedNodes` (`OrderOptions.ReleasedNodes`).
Это единственный параметр, управляющий нарезкой; `Routing:BaseNodesCount` в нарезке больше
не участвует (см. ниже).

**Режим `ReleasedNodes <= 0` — основной режим стенда, «весь маршрут released».**
Проверено живыми прогонами на стенде 23.11 (заказы 00133, 00136/00137, 00149, 00151):

- весь маршрут отправляется одним сообщением `order` с `orderUpdateId = 0`;
- все узлы и рёбра сообщения `released = true`; перекрытий, чанков и горизонта нет;
- обновления плана не отправляются: `ExtendPlanAsync` выходит немедленно, `newBaseRequest = true`
  в состоянии только пишется в журнал и не приводит к отправке;
- `MaxOrderUpdates` не расходуется, `orderUpdateId` остаётся `0` на весь заказ.

**Режим `ReleasedNodes > 0` — логика AGV (на стенде не используется).**

- Размер куска — `ReleasedNodes` узлов.
- Куски режутся по узлам с перекрытием в один узел: кусок `k` начинается с индекса
  `k * (ReleasedNodes - 1)` и содержит `ReleasedNodes` узлов (как `OrderService.SplitPath`).
- Каждый кусок отправляется как отдельное сообщение `order` / `order update`: все узлы и рёбра
  куска `released = true`; горизонта внутри сообщения нет.
- Если маршрут не длиннее `ReleasedNodes`, отправляется одно сообщение.
- Работают обновления по разделу 3.3, включая `newBaseRequest`, троттлинг и лимит
  `Routing:Order:MaxOrderUpdates` (новый параметр, по умолчанию 32).

Значение по умолчанию в коде (`OrderOptions.ReleasedNodes = 3`) не меняется — границы репозитория
запрещают менять дефолты; `0` приходит из конфигурации стенда. Обе ветки обязаны быть покрыты
юнит-тестами, потому что на стенде проверяется только ветка `<= 0`.

`Routing:BaseNodesCount` остаётся в `appsettings.json` и в `RoutePlannerOptions` без изменения
значения по умолчанию (40), из кода нарезки удаляется и помечается устаревшим в XML-комментарии;
валидация `ReleasedNodes < 0` сохраняется как есть (значение `0` допустимо).

### 3.2 Первый заказ

`orderUpdateId = 0`. В `vrt_RobotOrder.PlanJson` сохраняется **полный** план маршрута
(все узлы и рёбра, с глобальной нумерацией `sequenceId`), а в `vrt_RobotOrderNode` — все узлы плана.
В MQTT уходит первый кусок (при `ReleasedNodes <= 0` — весь маршрут целиком).

### 3.3 Условия отправки обновления

Обновления существуют только в режиме `Routing:Order:ReleasedNodes > 0`; при `<= 0` их нет
(см. 3.1). Обновление отправляется из `RobotOrderService.ExtendPlanAsync`, если выполнено всё:

1. заказ в статусе `Sent`, `Accepted` или `Executing`;
2. `snapshot.OrderId == order.OrderId` и `snapshot.OrderUpdateId >= order.OrderUpdateId`;
3. в полном плане после `lastNodeId`/`lastNodeSequenceId` есть хотя бы один узел;
4. сработал триггер: `snapshot.NewBaseRequest == true` **или** в текущем куске осталось
   не более одного недостигнутого узла (считается по `released` из состояния и `lastNodeSequenceId`);
5. с момента прошлого обновления этого робота прошло не меньше 2 с;
6. `lastNodeSequenceId` строго больше значения, при котором отправлялось прошлое обновление
   (защита от повторной отправки на том же узле);
7. число обновлений заказа меньше `Routing:Order:MaxOrderUpdates` (новый параметр, по умолчанию 32).

### 3.4 Содержимое обновления

- Первый узел обновления — узел с `sequenceId == snapshot.LastNodeSequenceId` (если поле пришло)
  либо последний узел плана с `nodeId == snapshot.LastNodeId`; при отсутствии обоих — первый
  недостигнутый узел плана.
- Обновление содержит этот узел и следующие `ReleasedNodes - 1` узлов; рёбра — между ними,
  с исходными `edgeId` и исходными нечётными `sequenceId` из полного плана.
- Нумерация `sequenceId` **не** пересчитывается: первый узел обновления имеет тот же
  `sequenceId`, что и в исходном заказе, первое ребро — `sequenceId + 1`.
- `orderUpdateId` = предыдущий + 1, монотонно; `headerId` — новый; `timestamp` — момент сборки.
- Узлы и рёбра, уже пройденные, в обновление не попадают.
- После успешной публикации: `PlanJson` = JSON обновления, `OrderUpdateId` = новый,
  `vrt_RobotOrderNode` пересобирается по новому плану, пройденные узлы сохраняют `Reached = true`.
- Если публикация не удалась — состояние заказа не меняется, ошибка в журнал.

## 4. Обработка сообщения `state`

`RobotStateParser` дополняется полем `edgeStates` (`edgeId`, `sequenceId`, `released`);
в `RobotSnapshot` появляется `IReadOnlyList<RobotEdgeState> EdgeStates`. Остальные поля уже есть.

Правила `ApplyStateAsync`:

- `snapshot.OrderId` пуст или заказ не найден — выход без изменения;
- `snapshot.OrderUpdateId < order.OrderUpdateId` — состояние игнорируется (уже есть);
- `driving`/`nodeStates`/`lastNodeId` — прогресс:
  - узел считается достигнутым, если его `nodeId == snapshot.LastNodeId` или он стоит в плане
    раньше него (уже есть);
  - `released = true` в `nodeStates` означает готовность узла, но не проезд (уже есть);
- `Sent` → `Accepted` при первом `state` с нашим `orderId`; `Accepted` → `Executing`, когда
  `driving = true` или есть достигнутые узлы;
- `Executing` → `Completed`, когда `driving = false` и (`lastNodeId` равен целевому узлу заказа
  `TargetNodeId` **или** последнему узлу полного плана); проверка по целевому узлу обязательна,
  это исправление дефекта «заказ не закрывается»;
- `errors`: блокирующей считается ошибка уровня, отличной от `W`/`WARNING`/`N`
  (`RobotErrorClassifier`); такая ошибка переводит активный заказ в `Failed` с `ROBOT_ERROR`;
- `actionStates`: используются для контроля `pick`/`drop` по `actionId` (статусы `FINISHED`/`FAILED`);
  на статус заказа не влияют;
- `edgeStates` сохраняются в снимке; на статус заказа в этом шаге не влияют (нужны этапу 3 для трафика).

Таймауты (уже есть, не менять значения по умолчанию): `AcceptTimeoutSeconds = 10`,
`StateSilenceSeconds = 15`, `ProgressTimeoutSeconds = 60`. Любой активный заказ обязан
выйти из активного статуса не позже `StateSilenceSeconds` после пропажи `state`
(`STATE_SILENCE`), это исправление дефекта «заказ висит при потере состояния».

## 5. Что удаляем и заменяем

Удаляется:

- `SlotApproachResolver` и `OrderOptions.StopBeforeSlot` — их роль переходит к
  `WorkingNodeStopResolver` и правилу «маршрут строится до самого узла цели, точка остановки
  смещается на `WrkLength`»; выбор соседнего узла как цели заказа больше не делается;
- вычисление `stopTheta`/`containerX/Y/Theta` внутри `RobotOrderService.CreateOrderAsync` —
  всё это считает сборщик заказа из графа;
- параметры `VdaOrderBuilder.Build(firstNodeX/Y/Theta, releasedNodeCount, terminalActionLoadId,
  containerX/Y/Theta)` — заменяются объектом запроса сборки (поза робота, границы куска,
  `orderUpdateId`, признак `SendForkActions`, `hasLoad`, тип завершающего действия);
- `IVdaOrderComposer`/`VdaOrderComposer` перестают быть местом подстановки `container*`;
  их сигнатура приводится к новому запросу сборки.

Сохраняется без изменения поведения: `AStarPathPlanner`, `ApproachNodeResolver` (остаётся
страховкой для узлов без проезжих рёбер), `MqttRobotStateReader`, `VdaOrderPublisher`,
`RobotStateParser` (кроме `edgeStates`), схема `vrt_*`, `RobotStartNodeResolver`,
`RobotErrorClassifier`, `CorrectPoseAsync`, очистка состояния перед новым заказом
(`cancelOrder`, ожидание до 15 с, сброс симулятора).

Исправляемые дефекты AGV (обязательные к проверке):

1. заказ не закрывается при потере `state` — закрывается по `TargetNodeId` и таймаутам;
2. `lastnode` 404 — новый эндпоинт всегда отвечает 200;
3. перепутанные `sequenceId` узлов и рёбер при обновлениях — нумерация исходного плана сохраняется,
   узел чётный, ребро нечётное, ребро = узел + 1;
4. отсутствие очистки состояния перед новым заказом — очистка выполняется и покрыта тестом.

## 6. Критерии приёмки

1. `VdaOrderBuilder` собирает действия и точку остановки по правилам раздела 2; константа
   `1.475` встречается в коде ровно один раз (в `WorkingNodeGeometry`).
2. Для рабочего узла `nodePosition` равен точке остановки, `containerX/Y` равны координатам узла,
   `containerTheta` равен курсу подхода; для `WAYPOINT` поля `theta` в `nodePosition` нет.
3. `orientation` задаётся только у первого ребра сообщения: `PI`, если первый сегмент проходится
   против направления ребра (`Backward`), и `0`, если по направлению (`Forward`); у всех
   последующих рёбер `orientation = 0` независимо от направления. Явный тест обязателен.
4. Первый узел при `orderUpdateId = 0`: `nodePosition` = поза робота, `allowedDeviationXY = 0.3`,
   `allowedDeviationTheta = 3.14`; при `Routing:Order:FirstNodeTheta = false` (по умолчанию)
   поля `theta` нет, при `true` — `theta = snapshot.Theta` (при неизвестной позе `node.Theta`).
5. `Routing:Order:ReleasedNodes <= 0` — весь маршрут уходит одним сообщением, все узлы и рёбра
   `released = true`, чанков и обновлений нет, `newBaseRequest` не порождает публикацию.
6. `Routing:Order:ReleasedNodes > 0` — маршрут режется на куски по N узлов с перекрытием 1 узел,
   все узлы и рёбра куска `released = true`.
7. Обновление отправляется по условиям 3.3, содержит исходные `sequenceId`, увеличивает
   `orderUpdateId` на 1, повторная отправка на том же узле невозможна.
8. `GET /api/AgvTask/lastnode/{serial}` возвращает 200 всегда: объект узла либо `null`.
9. `RobotSnapshot` содержит `EdgeStates`, парсер читает `edgeStates` из `state`.
10. Заказ закрывается `Completed` по `TargetNodeId` при `driving = false` и проваливается
    по `STATE_SILENCE` при пропаже `state`.
11. Юнит-тесты на сборку, оба режима нарезки, обновления, `state` и очистку перед заказом
    существуют и зелёные; интеграционные тесты проходят на стенде.
12. Живой прогон с доказательством: `state.lastNodeId` равен узлу действия и `actionStates`
    этого действия = `FINISHED` (см. §8 и evidence `state-after-action.json`).
13. Миграций нет: `git diff --stat bd9cb69..HEAD` не содержит файлов из `**/Migrations/**`.
14. `AGVControl` не изменён: `git -C C:\Projects\VRouter\AGVControl status --porcelain` пуст.

## 7. Границы

- Не менять `AGVControl` и остальные репозитории workspace.
- Не менять схему БД, не добавлять миграции, не трогать `vrt_*` таблицы и `public."Agvs"`.
- Не менять `public."Nodes"`/`public."Edges"` и данные карты.
- Не менять публичные HTTP-контракты `RouteController`, `AgvController`, `VisualizationController`;
  добавляется только новый маршрут `api/AgvTask/lastnode/{serial}`.
- Не менять значения по умолчанию существующих параметров `Routing:*`; разрешено добавить
  `Routing:Order:MaxOrderUpdates = 32` и `Routing:Order:FirstNodeTheta = false`.
- `Routing:Order:ReleasedNodes` остаётся параметром режима нарезки: значение по умолчанию в коде
  (`3`) не меняется, `0` приходит из конфигурации стенда. `Routing:BaseNodesCount` не меняет
  значение по умолчанию (40) и не участвует в нарезке.
- Не менять формат MQTT-топиков и QoS.
- Секретов в коде нет и появляться не должно.

## 8. Риски и ловушки

- Смещение точки остановки на 1.475 м уже ломало доезд 22.11: робот встал в 0.5 м от цели
  и завис, заказ не закрылся. Поэтому смещение применяется только к рабочим узлам, а обязательным
  доказательством приёмки является живой прогон, в котором `state.lastNodeId` равен **узлу действия**
  (узлу, на котором стоит `pick`/`drop`, а не соседнему) и `actionStates` этого действия = `FINISHED`;
  файл `doc/agents/2026-11-23-stage2.5-vda-port/evidence/state-after-action.json`. Если робот снова
  не доезжает, владелец останавливает выкатку и ставит новую задачу; править геометрию «на ходу» нельзя.
- `Routing:Order:ReleasedNodes = 0` (весь маршрут released) — проверенный живыми прогонами режим
  (00133, 00136/00137, 00149, 00151). Ветка `> 0` с чанками и обновлениями на стенде не проверяется,
  поэтому её корректность держится только на юнит-тестах: ошибка в ней не проявится в живом прогоне.
- `autoReturnNode = true` (как в AGV) отличается от прежней стендовой настройки `false`.
  Поведение подтверждается живым прогоном (доказательство `order-chunked.json`); молча
  возвращать `false` в коде запрещено — это решает владелец отдельной задачей.
- Симулятор держит `orderId` прошлого заказа: без очистки новый заказ получает `NO_ACCEPT`.
  Очистка уже реализована, её нельзя ослабить.
- `state` публикуется редко; на стенде `AgvMonitoring:StaleStateSeconds = 300`. Таймаут
  `StateSilenceSeconds` на стенде поднимать, а не менять в коде.
- Карта 801 распадается на компоненты: часть `wo-*` недостижима, это данные, а не код.
  Интеграционные тесты на реальной карте должны пропускаться (`SkippableFact`) при отсутствии
  переменных окружения, но прогон на стенде обязателен.
- `knotVector` и `degree` нельзя менять: на длинной NURBS-кривой симулятор не начинает движение.
- Живой заказ старого AGV от 23.11 у первого узла не содержит `theta` при `allowedDeviationTheta = 3.14`:
  появление `theta` — изменение поведения, поэтому оно закрыто флагом `FirstNodeTheta` с дефолтом `false`.
- Файлы `AGVControl` в CP1251: при чтении эталона не пересохранять их в UTF-8.
- PowerShell 5.1 читает `.ps1` без BOM как ANSI: скрипты с кириллицей только UTF-8 с BOM.

## 9. Тесты

Юнит (`tests/VR.RoutePlanner.Tests.Unit`):

- `VdaOrderTests`: рабочий узел — точка остановки, `container*`, `blockingType`, восемь параметров
  в заданном порядке; `WAYPOINT` без `theta`; первый узел — поза робота и допуски 0.3/3.14,
  при `FirstNodeTheta = false` поля `theta` нет, при `true` `theta = snapshot.Theta`;
  `maxSpeed`/`maxRotationSpeed`; виртуальное ребро; `SendForkActions = false` — нет `pick`/`drop`;
  завершающее действие только в последнем узле плана.
- `VdaOrderOrientationTests` (явный, отдельный тест): маршрут из 4+ узлов, где первый сегмент
  проходится против хранения ребра — первое ребро `orientation = PI`; тот же маршрут по направлению
  ребра — первое ребро `0`; во всех случаях рёбра 2..N имеют `orientation = 0`, в том числе когда
  сегмент проходится против направления своего ребра. Проверяется на всех рёбрах, а не на первом.
- `OrderChunkingTests` (unit, оба режима `Routing:Order:ReleasedNodes`):
  - `0` — маршрут из 10 узлов даёт одно сообщение, все узлы и рёбра `released = true`,
    `orderUpdateId = 0`, `ExtendPlanAsync` при `newBaseRequest = true` ничего не публикует;
  - отрицательное значение по-прежнему отклоняется существующей валидацией `ReleasedNodes < 0`
    (валидацию не менять), поэтому ветка `<= 0` проверяется значением `0`;
  - `3` — маршрут из 7 узлов режется на 3 куска по 3 узла с перекрытием 1 узел, стартовые индексы
    0, 2, 4, нумерация `sequenceId` сквозная, всё `released = true`.
- `OrderUpdateTests`: обновление начинается с `lastNodeSequenceId`; `sequenceId` исходные;
  `orderUpdateId + 1`; нет повторной отправки на том же узле; нет обновления после `Completed`;
  превышение `MaxOrderUpdates`.
- `RobotStateParserTests`: `edgeStates` разбираются; отсутствие `edgeStates` не ломает разбор.
- `RobotOrderServiceTests`: закрытие по `TargetNodeId`; `newBaseRequest` в состоянии вызывает
  ровно одно обновление; очистка перед новым заказом отправляет `cancelOrder`;
  `lastnode` неизвестного робота — 200 и `null`.
- `OrderServiceFakes`/`OrderFixtures` дополняются графом с рабочим узлом и слотом.

Интеграционные (`tests/VR.RoutePlanner.Tests.Integration`, стенд `rms_vrp_test`):

- `OrderAssemblyRealMapTests` (`SkippableFact`): на реальном графе 801 маршрут `wa128 → wo-4-9`
  даёт рабочий узел, точку остановки на 1.475 м от слота и `pick` с `containerX/Y` = координаты слота;
- `OrderChunkingRealMapTests`: маршрут длиннее 3 узлов при `Routing:Order:ReleasedNodes = 3` режется
  на ожидаемое число кусков, нумерация сквозная; тот же маршрут при `ReleasedNodes = 0` — одно
  сообщение со всем маршрутом.

Эталонные заказы для сверки структуры: `doc/agents/2026-11-21-stage2.5-vda/evidence/vda-order.json`,
`vda-state-after.json`, `vda-instantActions.json`. Тест читает их как файл и проверяет,
что наш сборщик даёт тот же набор полей и типов (значения координат — из фикстуры).

Прогон на стенде (владелец, не контролёр) при `Routing:Order:ReleasedNodes = 0`: заказ на слот,
наблюдение `Sent → Accepted → Executing → Completed`, и отдельный заказ на отмену. Для прогона
на слот обязателен снимок `state` после действия: `lastNodeId` = узел действия, `actionStates`
этого `actionId` = `FINISHED` (файл `state-after-action.json`). Прогон с `newBaseRequest`
и обновлением относится к режиму `ReleasedNodes > 0` и на стенде не выполняется.

## 10. План проверки

Контролёр обязан прогнать сам:

```powershell
cd C:\Users\User\source\repos\VR.RoutePlanner
git diff --stat bd9cb69..HEAD
git diff --name-only bd9cb69..HEAD | Select-String -Pattern 'Migrations|appsettings'
dotnet build VR.RoutePlanner.sln -c Release
.\scripts\test.ps1 -NoDb
git -C C:\Projects\VRouter\AGVControl status --porcelain
```

Проверки по диффу (чтением файлов с логикой, не пересказом):

1. `WorkingNodeGeometry` — `WrkLength = 1.475`, `Front`/`Rear` по формулам 2.2, выбор ближайшей
   к точке подхода; поиск константы `1.475` по `src` даёт одно вхождение.
2. `VdaOrderBuilder` — `sequenceId` чётный/нечётный, `released = true`, восемь параметров
   действия в порядке 2.3, `blockingType = "SOFT"`, `orientation` PI только у первого ребра
   при `Backward` (и `0` при `Forward`, у последующих рёбер всегда `0`), первый узел без `theta`
   при `FirstNodeTheta = false`, `maxSpeed`/`maxRotationSpeed`, `knotVector [0,0,1,1]`, `degree 1`.
3. `RobotOrderService.ExtendPlanAsync` — семь условий 3.3 в коде, троттлинг 2 с, монотонный
   `orderUpdateId`, отсутствие пересчёта `sequenceId`, и ранний выход при `ReleasedNodes <= 0`.
   `OrderChunkPlanner` — режимы `<= 0` (весь маршрут) и `> 0` (перекрытие 1 узел).
4. `VdaOrderUpdater` — нарезка от `lastNodeSequenceId`, исходные `sequenceId`.
5. `RobotSnapshot`/`RobotStateParser` — `EdgeStates` есть, читаются.
6. `LegacyAgvTaskController` — единственный ответ 200, 404 не возвращается.
7. `OrderOptions` — `MaxOrderUpdates` есть, `FirstNodeTheta` есть и равен `false`,
   `ReleasedNodes` не изменил значение по умолчанию, `StopBeforeSlot` удалён,
   `SlotApproachResolver` удалён, `BaseNodesCount` в нарезке не используется.
8. Отсутствие миграций и изменений в `AGVControl`.

Доказательства от владельца стенда в `doc/agents/2026-11-23-stage2.5-vda-port/evidence/`:

- `order-chunked.json` — опубликованный заказ (при `ReleasedNodes = 0` — весь маршрут одним
  сообщением) со смещённой точкой остановки и `pick`/`drop`;
- `state-newbaserequest.json` — `state` с `newBaseRequest = true` (обязателен только если владелец
  прогонял режим `ReleasedNodes > 0`; в основном режиме достаточно записи журнала в `stand-run.txt`);
- `state-after-action.json` — `state` после действия на рабочем узле: `lastNodeId` = узел действия,
  `actionStates` этого `actionId` со статусом `FINISHED`;
- `order-update.json` — опубликованное обновление с продолженными `sequenceId` (только для режима
  `ReleasedNodes > 0`; в основном режиме обновлений нет, файл не требуется);
- `lastnode-200.txt` — вывод `curl -i` на `api/AgvTask/lastnode/{serial}`;
- `stand-run.txt` — строки журнала со сменой статусов до `Completed` и метками времени;
- `tests.txt` — сводка прогона `.\scripts\test.ps1`.

## Приёмочная выжимка

Код — дифф `bd9cb69..HEAD`, пути от корня `VR.RoutePlanner`:

1. `Domain/Graph/WorkingNodeGeometry.cs`: `WrkLength = 1.475`, `Front = node - 1.475*(cos θ, sin θ)`, `Rear = node + 1.475*(cos θ, sin θ)`, берётся ближайшая к точке подхода; `1.475` в `src` — одно вхождение.
2. `Application/Robots/WorkingNodeStopResolver.cs`: один входящий узел → он; иначе предыдущий узел маршрута; иначе поза робота; `θ = atan2(N - A)`.
3. `VdaOrderBuilder`: `sequenceId` узла `2*i`, ребра `2*i+1`; `released = true` у всех узлов и рёбер; рабочий узел (`LOADING`/`UNLOADING`/`WORKING`/`wo-*`) → `nodePosition` = точка остановки + `pick`/`drop`; `containerX/Y` = координаты узла, `containerTheta` = θ подхода.
4. Действие: `blockingType="SOFT"`, параметры ровно в порядке `containerTypeId=2, containerX, containerY, containerTheta, onlyFork=true, autoReturnNode=true, noScan=false, adjustWithoutOA=true`; при `SendForkActions=false` действий нет.
5. `WAYPOINT` без `theta`; первый узел при `orderUpdateId=0` — поза робота, допуски 0.3/3.14, при `Routing:Order:FirstNodeTheta=false` (дефолт) поля `theta` нет, при `true` `theta=snapshot.Theta`.
6. `orientation` только у первого ребра: `PI` при `Backward`, `0` при `Forward`; у всех последующих рёбер `0` независимо от направления. Есть `maxSpeed`, `maxRotationSpeed`, `trajectory{degree:1,knotVector:[0,0,1,1]}`; виртуальное ребро при отсутствии ребра в графе.
7. `Routing:Order:ReleasedNodes <= 0` — весь маршрут одним сообщением, все узлы и рёбра `released=true`, чанков и обновлений нет, `newBaseRequest` публикаций не порождает. Это основной режим стенда (прогоны 00133, 00136/00137, 00149, 00151).
8. `Routing:Order:ReleasedNodes > 0` — куски по N узлов с перекрытием 1 узел, всё `released=true`. `Routing:BaseNodesCount` в нарезке не участвует, дефолт 40 не изменён; добавлен `Routing:Order:MaxOrderUpdates=32`; `ReleasedNodes` в коде остался 3 (значение `0` приходит из конфига стенда).
9. `ExtendPlanAsync` (только при `ReleasedNodes > 0`): обновление при `newBaseRequest` или остатке ≤ 1 узла; троттлинг 2 с; обязателен рост `lastNodeSequenceId`; `orderUpdateId+1`; исходные `sequenceId` не пересчитываются; при `<= 0` — ранний выход без публикации.
10. `RobotSnapshot`/`RobotStateParser`: `EdgeStates` (`edgeId`, `sequenceId`, `released`) есть и читаются.
11. `Api/Controllers/LegacyAgvTaskController.cs`: `GET api/AgvTask/lastnode/{serial}` — всегда 200 (объект узла или `null`), 404 нет.
12. Заказ: `Completed` по `TargetNodeId` при `driving=false`; пропажа `state` → `Failed/STATE_SILENCE`; очистка перед новым заказом (`cancelOrder` + ожидание) на месте.
13. Удалены `SlotApproachResolver`, `OrderOptions.StopBeforeSlot`, расчёт `container*` в `CreateOrderAsync`.

Тесты и запреты — контролёр запускает сам:

14. `dotnet build VR.RoutePlanner.sln -c Release` — 0 ошибок.
15. `.\scripts\test.ps1 -NoDb` — все зелёные, 0 упавших.
16. Есть и проходят: `VdaOrderTests` (точка остановки, параметры действия, первый узел с `FirstNodeTheta` и без, `maxSpeed`, виртуальное ребро), `VdaOrderOrientationTests` (первое ребро `PI` при `Backward` против хранения, `0` при `Forward`, все последующие `0`), `OrderChunkingTests` (`ReleasedNodes = 0` — одно сообщение, `3` — три куска с перекрытием), `OrderUpdateTests` (исходные `sequenceId`, `orderUpdateId+1`, без повторной отправки), `RobotStateParserTests` (`edgeStates`), `RobotOrderServiceTests` (закрытие по `TargetNodeId`, `newBaseRequest`, очистка, `lastnode` 200).
17. `.\scripts\test.ps1` на стенде — интеграционные не пропущены, зелёные.
18. `git diff --stat bd9cb69..HEAD` без `**/Migrations/**`; `vrt_RobotOrder`, `vrt_RobotSession`, `vrt_RobotOrderNode` не изменены.
19. `git -C C:\Projects\VRouter\AGVControl status --porcelain` — пусто.

Доказательства владельца в `doc/agents/2026-11-23-stage2.5-vda-port/evidence/`:

20. `order-chunked.json` — при `ReleasedNodes=0` один заказ со всем маршрутом; `pick`/`drop` с восемью параметрами; `nodePosition` рабочего узла смещён от координат узла на 1.475 м; у первого узла нет `theta`, `allowedDeviationTheta=3.14`.
21. `state-after-action.json` — `lastNodeId` равен узлу действия и `actionStates` этого `actionId` = `FINISHED` (единственное доказательство доезда после смещения на 1.475 м).
22. `lastnode-200.txt` — `HTTP/1.1 200`; `stand-run.txt` — заказ до `Completed` с метками времени; `tests.txt` — сводка без пропусков.

## Изменения ревизии 2

1. **`theta` первого узла.** В ревизии 1 требовалось писать `snapshot.Theta`. Живой заказ старого
   AGV от 23.11 показывает: у первого узла `theta` нет, `allowedDeviationTheta = 3.14`. Введён
   `Routing:Order:FirstNodeTheta` (по умолчанию `false` — не писать, `true` — писать `snapshot.Theta`),
   добавлен критерий 4, тест в `VdaOrderTests` и пункт 5 выжимки.
2. **`BaseNodesCount`.** В ревизии 1 нарезка шла по `Routing:BaseNodesCount`, а `ReleasedNodes`
   объявлялся устаревшим. На стенде `Routing:Order:ReleasedNodes = 0` и это проверено живыми
   прогонами (00133, 00136/00137, 00149, 00151), поэтому `0` стал основным режимом: `<= 0` — весь
   маршрут released, без чанков и обновлений; `> 0` — логика AGV (куски с перекрытием 1 узел,
   `newBaseRequest`, до 32 обновлений). Переписаны 3.1, 3.3, критерии 5–6, §7, §8, тесты
   (`OrderChunkingTests`, `OrderChunkingRealMapTests`) и пункты 7–9 выжимки.
3. **`orientation`.** Добавлен отдельный тест `VdaOrderOrientationTests`: первое ребро против
   хранения ребра даёт `PI`, при движении по направлению — `0`, все последующие рёбра `0` независимо
   от направления.
4. **Риск смещения.** В §8 уточнено: 1.475 м уже ломали доезд 22.11 (робот встал в 0.5 м и завис);
   обязателен живой прогон с доказательством `state.lastNodeId` = узел действия и `actionStates`
   = `FINISHED` (`state-after-action.json`), критерий 12 и пункт 21 выжимки.
