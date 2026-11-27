# Сборка заказа в AGVControl: разбор (только чтение)

Примечание: `OrderService.cs` лежит в `AGVControlApi\Data\`, а не в `Services\` (как в задании). Пути ниже от `C:\Projects\VRouter\AGVControl\`.
Ограничения: shell заблокирован (отказ permission), сборку и тесты не запускал. Файлы `AGVVda.Adapters\Standard\VdaAdapterBase.cs` построены с CR-только переводами строк: grep отдает весь файл одной строкой, номера строк не получить. Поэтому маппинг адаптера помечен «не проверено».

## 1. Траектория ребра
- Чтение из карты: `Edge.Trajectory` это `string?` (`AGVModels\Def\Edge.cs:101`). Таблица `Edges`, без переименования (`AppDbContext.cs:47`). Конвертер для Edge в `AppDbContext.cs:44-55` не найден; `HasConversion` в `:61-142` не сопоставлял с Edge (не проверено по каждой строке).
- Загрузка ребра: `_edgeService.GetEdgeByEdgeIdAsync` (`OrderService.cs:1211`). Метод в `EdgeService.cs` не открывал: не проверено, что он не трогает JSON.
- Пусто или длина <= 2: `trajectoryData = null` (`OrderService.cs:1229, 1231`). В `EdgeDto.Trajectory` уходит `null` (`:1264`). Fallback на прямую в этом файле нет (не найден при поиске по Trajectory/Fallback).
- Непустая строка: `JsonSerializer.Deserialize<TrajectoryDto>` без try/catch (`OrderService.cs:1233`). Ошибка уходит в catch `SendOrderToAgvInternalAsync` (`:2364-2371`) и превращается в ErrorResponse. В `RouteCreator.cs:522` та же десериализация обернута в try.
- `degree`, `knotVector`, `controlPoints`: `TrajectoryDto` с умолчаниями `Degree=1.0` (`AGVModels\DTOs\TrajectoryDto.cs:16`), `KnotVector` (`:22`), `ControlPoints` (`:28`). Объект передается как есть (`OrderService.cs:1264`). Поля весов в `ControlPoint` не проверял (не проверено).

## 2. Поля EdgeDto (`OrderService.cs:1250-1265`)
| Поле | Значение | Откуда |
|---|---|---|
| EdgeId | `edge.EdgeId` | Edge |
| SequenceId | `seq + 2*i + 1` | `:1217`, `seq` из `order.SequenceId` `:1104` |
| Released | `true` | `:1254` |
| StartNodeId / EndNodeId | из `nodes[i]`, `nodes[i+1]` | `:1214-1215, 1255-1256` |
| MaxSpeed | `edge.MaxSpeed` | `:1257` |
| Orientation | см. ниже | `:1258` |
| OrientationType | `edge.OrientationType`, пусто → `TANGENTIAL` | `:1259-1261` |
| MaxRotationSpeed | `edge.MaxRotationSpeed` | `:1263` |
| Trajectory | `trajectoryData` | `:1264` |
| actions | не заполняется, у ребра нет actions | `:1250-1265` |

- Orientation первого ребра: `MoveDirection == forward ? 0 : Math.PI` (`:1200`). Для остальных после каждого ребра присваивается 0 (`:1268`).
- Виртуальное ребро (`edge == null`): `CreateVirtualEdge` (`:1028-1041`) задает `EdgeId`, `SequenceId`, `Released=true`, узлы. Затем `OrientationType=null`, `Orientation=null`, `RotationAllowed=false` (`:1221-1224`). Trajectory и MaxSpeed не заданы.
- Для `Bidirectional == true` с непустой траекторией: `ShouldReverseControlPoints` (`:1236, 1243`) и `ControlPoints.Reverse()` (`:1245`).
- Если `edge.Bidirectional != true`, разворота нет.

## 3. Поля узла (`OrderService.cs:1177-1193`)
- NodeId `node.NodeId` (`:1179`). SequenceId `seq + 2*i_node` (`:1180`). Released `true` (`:1181`). NodeDescription `node.NodeDescription` (`:1191`). Actions — список из `:1142`, `:1192`.
- Первый узел в `nodes` это узел карты, а не поза робота. Поза подставляется позже в `SendOrderToAgvInternalAsync`: при `agvStartPos` X/Y/Theta берутся из AGV (`:2304-2308`, по умолчанию true в `:2278`). `SendOrderUpdateInternalAsync` делает то же только при `OrderUpdateId == 0` (`:2405, 2412-2417`).
- Координаты: `node.PositionX/Y ?? 0` (`:1137-1138`). Для LOADING/UNLOADING/WORKING X/Y заменяются `AgvStopPositionXY` (`:1159`, определение `AGVModels\Def\Node.cs:182`). Тип `theta` для WAYPOINT, WORKING и CHARGING: `null` (`:1162, 1169, 1173`). Для LOADING/UNLOADING theta из `PositionBeforeWorkingNode` (`:1149`, тело не читал).
- Допуски: `AllowedDeviationXY = Math.Max(node.AllowedDeviationXY ?? 0, 0.1)`, `AllowedDeviationTheta = Math.Max(... ?? 0, 0.1)` (`:1188-1189`). У первого узла дополнительно `Theta >= 3.14` и `XY >= 0.3` (`:2301-2302`; в update `:2409-2410`).
- Значения по умолчанию в NodeService: `AllowedDeviationXY ??= 0.1`, `AllowedDeviationTheta ??= 0.087` (`Data\NodeService.cs:493-494`, контекст не читал).
- `mapId`: `order.MapId` (`:1187`).

## 4. Шапка заказа
- Основной путь: `SendOrderToAgvInternalAsync` вызывает `adapter.BuildOrderMessage` (`:2326`). Шапку строит адаптер: не проверено (в `VdaAdapterBase.cs` метод есть, номера строк не получены; в `Vda21Adapter.cs` `BuildOrderMessage` не найден).
- `orderUpdateId` в ответе: `order.OrderUpdateId + 1` (`:2361`). `messageId`: `Guid.NewGuid()` (`:2360`).
- Альтернативные сборщики (не основной путь):
  - `TuskSendOrderToAgvInternalAsync` (`:1455-1491`): `headerId = GetNextHeaderId()`, timestamp формата `yyyy-MM-ddTHH:mm:ss.fffZ` (`:1460`), `version = _version` (`:1489`), `manufacturer` (`:1490`), `serialNumber = order.AgvSerialNumber` (`:1491`). Вызовов нет (grep по `TuskSend*` в AGVControlApi: только определения).
  - `CancelOrder` (`:582-590`): `headerId = messageId`, `version = _version`, таймстамп.
  - `ActionService.cs:976-985`: `version = "v2"` захардкожено.
- `orderDescription`, `zoneSetId`: в `OrderService.cs` не найдено. `zoneSetId` закомментирован (`:594`).
- `_version`: `Mqtt:Version`, по умолчанию `v2` (`OrderService.cs:63`).

## 5. Действия
- Построение: `GetCarrierActionsByNode` (`:1679`) и `GetForkliftActionsByNode`, `GetChargingActionsByNode` (`:1152, 1154, 1167`). Тип: `ActionType` = `pick`/`drop`, `ActionDescription` тот же (`:1691-1692`). `BlockingType = HARD` (`:1693`). `ActionId = Guid` (`:1694`). Параметры: `jack_height=0.04`, `recognize=true`, `recfile="shelf/magnit_2.shelf"` (`:1698-1707`) для LOADING.
- Сохранение: `SaveActionsToDatabaseAsync` (`:1156, 1168`).
- containerX/Y: в `GetCarrierActionsByNode` `containerX = positionX`, `containerY = positionY` (`:1685-1686`). Это позиция узла до замены на stop-позицию (замена на `:1159` идет после построения actions на `:1154`). Смещения на длину AGV нет. Для Tusk то же (`:1624`).
- В `ActionService.cs:708` другой расчет: `X + (Length + 0.05) * cos(Theta)`. Этот путь не основной для заказа.
- Не найдено: явной сборки `blockingType`/`actionParameters` в `OrderService` для заказа (только `ActionDto`). Сериализацию в адаптере не проверил.

## 6. Разбиение на чанки и updates
- Размер: `Routing:BaseNodesCount`, по умолчанию 40 в `Data\OrderService.cs:65`, 4 в `Services\AGVOrderService.cs:84`. Расхождение дефолтов.
- Разбиение: `SplitPath` (`OrderService.cs:1985-2001`): шаг `base-1`, блок `Take(base)`. Соседние чанки делят один узел. `SplitEdges` (`:2003-2023`): `base-1` ребер в блоке.
- Места вызова: `AGVOrderService.cs:424-425`, `OrderService.cs:2101-2102`, `:2215-2216`.
- Цикл: `AGVOrderService.cs:429-480`. Каждый чанк: `NodeSequence = baseNodes` (`:449`), `EdgeSequence = baseEdges` (`:450`). `sequenceId` накапливается: `+= result.SequenceId` (`:325, 405, 469`). `baseCounter++` (`:476`).
- Отправка update: `SendOrderUpdateToAgvAsync` (`OrderService.cs:917`) → `SendOrderUpdateInternalAsync` (`:2381`) → `MakeNodesAndEdgesForOrderAsync(order, agv, addVirtualEdge)`, где `addVirtualEdge = OrderUpdateId > 0` (`:2389-2391`).
- Пересылаются ли узлы заново целиком или только хвост: пересылается целиком `NodeSequence` текущего чанка (`:1117-1196` берет все узлы `order.NodeSequence`). Хвост не режется.
- Совпадают ли координаты и sequenceId у повторно отправленных узлов:
  - Координаты: `GetNodeByNodeIdAsync` каждый раз (`:1120`), значит одинаковые для одного узла. Исключение: X/Y первого узла меняются на позу AGV, если `OrderUpdateId == 0` (`:2405-2417`).
  - SequenceId: `seq + 2*i`, где `seq = order.SequenceId` (`:1104`). При update перед циклом вставляется `firstNodeId` в начало (`:1099-1101`), поэтому индексы сдвигаются на 1. Узел с тем же ID получает другой `sequenceId`, если `order.SequenceId` у сообщения другой.
  - Значит, при повторе тот же физический узел может иметь другой `sequenceId` и другой индекс.
- Виртуальный старт: `VIRTUAL_START_{id}` (`:1101`). Ребро не находится в карте, поэтому строится `CreateVirtualEdge` (`:1219-1225`).

## 7. Спрямление, упрощение, отбрасывание controlPoints, degree
- Найдено только `ControlPoints.Reverse()` (`OrderService.cs:1245`).
- Поиск по `Simplif|Decimat|RemovePoints|Degree\s*=|ControlPoints\s*=` в `AGVControlApi` совпадений не дал.
- `VDATrajectory.cs:82-148` (прямая при `Degree==1`, кубика при 4 точках) используется в `AGVModels\State\EdgeState.cs:39`. В пути заказа не найден (не проверено полностью).
- `RouteCreator.cs:519-539` строит ребро маршрута отдельно: тоже без упрощения.

## 8. Карта
- `Edge.Trajectory` передается как строка, без преобразований в `OrderService.cs:1231-1233`. Тип `string?` (`Def\Edge.cs:101`). EF-маппинг по соглашению, HasConversion для Edge не найден (см. п.1).

## Что повторить у нас
- Траектория берется из карты целиком, как JSON. Вход `Trajectory` из `Edges` (`Def\Edge.cs:101`) и разворот `ControlPoints` при `Bidirectional` (`OrderService.cs:1236-1246`). Degree, knot и веса не трогаем.
- Пустая или короткая траектория: `Trajectory = null` (`:1231, 1264`), без подмены прямой.
- Ориентация: первое ребро `0` или `PI` по `MoveDirection`, дальше `0` (`:1200, 1268`). `OrientationType` по умолчанию `TANGENTIAL` (`:1259-1261`).
- Допуски узла: `max(value ?? 0, 0.1)` (`:1188-1189`). Первый узел: `theta >= 3.14`, `XY >= 0.3`, позиция AGV при `agvStartPos` (`:2301-2310`).
- Чанки: `SplitPath` шаг `base-1`, `SplitEdges` `base-1` (`:1985-2023`). `sequenceId` накапливается по чанкам (`AGVOrderService.cs:325, 405, 469`).
- Update: полный `NodeSequence` текущего чанка с виртуальным стартом (`:1099-1101, 2389`).

## Что проверить на стеноде
- Совпадает ли поле `trajectory` в MQTT-сообщении с `TrajectoryDto` карты (degree, knotVector, controlPoints, веса) — адаптер не проверен.
- Для двунаправленного ребра: сообщение с разворотом `ControlPoints` (`:1245`) совпадает с направлением движения по `ShouldReverseControlPoints` (`:1045-1069`).
- `sequenceId` узлов в повторном update: совпадает ли для одного узла при сдвиге `VIRTUAL_START` (`:1099-1101`).
- `Routing:BaseNodesCount`: 40 или 4 в конфигурации (`OrderService.cs:65`, `AGVOrderService.cs:84`).
- Поведение при сбое `JsonSerializer.Deserialize` в `OrderService.cs:1233` (нет try/catch).
- Пропуск узла при `node == null` (`:1122-1125`): `nodes` короче `NodeSequence`, а `nodes[i]` (`:1214-1215`) начинает указывать не на тот узел.
- Допуски узлов в БД: значения `??=` в `NodeService.cs:493-494`.
- Адаптер (`VdaAdapterBase.BuildOrderMessage`): заголовок, `orientation` и `trajectory` в сообщении. Не проверено.
