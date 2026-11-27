# Разбор возврата AGV на зарядку и парковку

## 1. Вопрос
Как FleetManager инициирует перемещение на зарядку/парковку, как API строит заказ и что происходит с зарядкой после прибытия.

## 2. Точки входа
- `AGVFleetManager/Services/KafkaConsumerService.cs:218-219,254-267` подписывает FleetManager на WMS tasks events, низкий заряд и WAITING.
- `AGVFleetManager/Services/KafkaConsumerService.cs:393-423,438-459` преобразует CRITICAL/WAITING события в `TaskQueueItem`.
- `AGVFleetManager/Services/TaskWorker.cs:288-392,424-431` проверяет последний узел, готовность и выполняет очередь.
- `AGVControlApi/Controllers/OrdersController.cs:236-260,298-324` endpoints auto charge/parking.
- `AGVControlApi/Data/OrderService.cs:1963-1983,2053-2173` проверка charge-node и общий путь формирования заказа.
- `AGVControlApi/Monitor/StateMonitorService.cs:1021-1086,1190-1280` публикация battery/waiting Kafka событий.

## 3. Поток
1. State monitor публикует `CRITICAL`, если батарея ниже lower limit и AGV не сообщает Charging; FleetManager слушает именно battery topic и ставит задачу с этим статусом (`StateMonitorService.cs:1027-1045`; `KafkaConsumerService.cs:218-219,393-417`).
2. Для ожидания monitor по истечении таймаута публикует WAITING в Kafka; FleetManager ставит `WAITING`-item (`StateMonitorService.cs:1190-1196,1222-1224,1267-1280`; `KafkaConsumerService.cs:264-267,438-459`). Это не просто пустая очередь и не событие завершения WMS-задачи.
3. Worker извлекает item, для `CRITICAL` проверяет последний узел CHARGING и `getready`, затем POST `/api/order/move-to-charge/{serial}`. Для WAITING аналогично проверяет PARKING и POST `/api/order/move-to-parking/{serial}` (`TaskWorker.cs:319-380,491-510`).
4. API выбирает ближайший доступный по расстоянию NodeType CHARGING/PARKING; проверяет занятость. Charge вызывает order-service `MoveAGVToChargeStationAsync`, parking вызывает `MoveAGVToNodeAsync(...PARKING)` (`OrdersController.cs:240-260,302-323`; `NodeService.cs:238-272`).
5. Общий метод определяет стартовый узел из LastNodeId/ближайшего узла, получает путь, делит его на базы, инициализирует/сохраняет/отправляет обычный VDA order (`OrderService.cs:2072-2082,2097-2102,2109-2163`).

## 4. Поля и значения
| Поле | Значение | Где задано | Зачем |
|---|---|---|---|
| `Charging:lowerLimit` | API appsettings 20%; AGVOrderService fallback 20 | `appsettings.api.json:34-42`; `AGVOrderService.cs:81-82` | Порог события критической батареи и NeedsCharging. |
| `Charging:targetLimit` | 80% | `appsettings.api.json:40-42`; `AGVOrderService.cs:81-82` | Порог готовности/заряда; CRITICAL monitor завершение не инициирует. |
| `Charging:LowerLimit`, `TargetLimit` | fallback каждого 0 | `StateMonitorService.cs:125-126` | Monitor читает ключи с иной капитализацией, чем конфиг. |
| `AgvMonitoring:IntervalSeconds` | 180с конфиг и fallback | `appsettings.api.json:51-55`; `StateMonitorService.cs:128-134` | Опрос состояния; таймер ожидания проверяет каждые 30с. |
| `WaitingTimeoutMinutes` | 5 минут | `appsettings.api.json:54-55`; `StateMonitorService.cs:131-134` | Порог WAITING по простою заказа. |
| `MaxRerouteAttempts` | 3 | `StateMonitorService.cs:83-84,1225-1241` | Перед WAITING пробуются reroute до трех раз. |
| `chg_id`, `chargerX/Y`, `thetaWhileCharging`, `mapId` | appsettings.api: 20373, 42.5, 48.42, -3.14, 78 | `appsettings.api.json:34-39` | Значения для ручного instantAction и action-параметров. |
| charger defaults | 20373, X=0, Y=1, theta=2, map=1883 | `AgvInstantActionsService.cs:63-67` | Дефолты API instantAction start-charging. |
| charging polling | 20с, timeout 1200с | `AgvService.cs:22,875-905` | Только отдельный метод ожидания зарядки, не вызванный найденным auto-flow. |

## 5. Отличия и дефекты
- Автоперемещение зарядки вызывается при CRITICAL Kafka status; вызов API не проверяет уровень батареи, только `getready`/последний узел (`TaskWorker.cs:323-340`). Проверка `getready` возвращает true при IDLE без проверки батареи (`AGVTaskService.cs:49-56`).
- Monitor читает `Charging:LowerLimit/TargetLimit`, а конфиг содержит `lowerLimit/targetLimit`; при стандартной case-sensitive конфигурации monitor получает fallback 0. Тогда CRITICAL при обычной батарее не возникнет, а условие завершения зарядки окажется >0 (`StateMonitorService.cs:125-126,1027-1029,1061-1063`; `appsettings.api.json:40-41`).
- `ChargingCompleted` публикуется, когда батарея пересекает строго `> targetLimit` при Charging=true; сообщение `BatteryMessage` содержит serial, NewBatteryLevel, Threshold, Status=OK, CompletedAt (`StateMonitorService.cs:1061-1079`; `BatteryMessage.cs:5-18`). FleetManager подписан на BatteryState (низкий заряд), но не на ChargingCompleted; `OK` в его обработчике лишь логируется при Threshold>=80, без stop/перемещения (`KafkaConsumerService.cs:218-219,393-423`; `AGVFleetManager/appsettings.json:18-26`).
- В авто-маршруте charge отдельный endpoint не отправляет instantAction. Он строит заказ до узла; `startCharging` может быть добавлен как node action при построении charging-node order (`OrderService.cs:1164-1167,1742-1773`). Автоматический вызов stopCharging по достижении target не найдено: искали stopCharging/StopChargingAsync и все ссылки на agv-charging-completed.
- `MoveAGVToNodeAsync` берет путь через `IEdgeService.GetShortestPathIdsAsync` и общий Order pipeline; AStar имеется в реализации routing (`OrderService.cs:2097-2102`; `AStarPathFinder.cs:213-237`). Особые maxSpeed/orientation для auto-charge/parking не найдено в изученных вызовах; charge node action задает параметры charger и SOFT для forklift (`OrderService.cs:1746-1771`).
- `MarkAsCharging` лишь добавляет taskId в список; `IsCharging` не используется в выборе очереди/приостановке (`SimpleTaskQueueService.cs:152-155,174`).
- Worker проверяет занятость цели в API; при уже последнем узле правильного типа ничего не отправляет (`TaskWorker.cs:323-325,363-365`; `OrdersController.cs:252-256,315-319`). Статус/online сам Worker прямо не валидирует; API readiness проверяет IDLE (`AGVTaskService.cs:49-56`).
- Ошибки Worker HTTP/timeout логирует и возвращает false, встроенного retry перемещения не найдено (`TaskWorker.cs:342-350,382-390`). Общий polling зарядки ограничен 1200с и возвращает текущий заряд по timeout, но в auto-flow не вызывается (`AgvService.cs:875-905`).
- `Guid.NewGuid().ToString()` для CRITICAL/WAITING даёт пустой GUID, не уникальный новый ID; downstream `SendTaskQueueItem` выполняет `Guid.Parse` (`KafkaConsumerService.cs:408-415,448-455,359-363`). Это дефект формирования событий.
- «Зона парковки» в коде представлена отдельными узлами NodeType PARKING, не множеством/областью PARKS; выбирается ближайший в пределах карты по квадрату расстояния. Выбор ближайшего не фильтрует занятость до выбора альтернативы: при занятом ближайшем узле endpoint возвращает ошибку (`NodeService.cs:245-272`; `OrdersController.cs:307-319`).

## 6. Что повторить у нас
- Разделять триггеры: низкая батарея через Kafka CRITICAL и простой/зависание заказа через WAITING; пустая очередь сама по себе не запускает парковку (`StateMonitorService.cs:1027-1045,1190-1224`; `TaskWorker.cs:491-510`).
- Перед командой проверять последний NodeType, готовность/IDLE и занятость целевого узла; учитывать, что эталон прекращает попытку при занятой ближайшей цели, без выбора следующей (`TaskWorker.cs:323-340,363-380`; `OrdersController.cs:245-256,307-319`).
- Для перемещения применять общий route/order pipeline и обычный VDA order, цель выбирать ближайшим узлом нужного типа (`OrdersController.cs:245-258,307-321`; `OrderService.cs:2053-2163`).
- Не считать `agv-charging-completed` командой stop: в эталоне это уведомление Kafka, автоматическая остановка в просмотренном потоке не найдена (`StateMonitorService.cs:1061-1085`; `KafkaConsumerService.cs:218-219,393-423`).

## 7. Что проверить на стенде
- Низкая батарея при корректном регистре ключей: порог, одноразовость CRITICAL до восстановления >= lowerLimit, реальное назначение AGV.
- CRITICAL, когда робот уже на CHARGING; недоступный/занятый ближайший charging-node и наличие альтернативного узла.
- WAITING после 5 минут и отправка на ближайший PARKING; повторный WAITING после reroute/3 попыток.
- По прибытии на CHARGING: фактическое исполнение node action `startCharging`, параметры для разных AGVClass/adapter и блокирующий тип.
- Пересечение 80%: публикация `agv-charging-completed`, что потребитель делает с OK и кто прекращает заряд физически.
- Таймаут/ошибка HTTP и рост заряда: проверенный retry отсутствует; проверить эксплуатационное поведение.
