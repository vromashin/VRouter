# Чек-лист паритета контрактов AGVControl

Снимок API: `agvcontrol-swagger.json` (`GET http://127.0.0.1:5231/swagger/v1/swagger.json`, 2026-11-21,
109 операций, 32 схемы). Сообщения: `samples/` (`APR/v2/Tuskrobots/98001/*`, Kafka).
Потребитель в колонке «файл и строка» дан по коду `rms-admin` (`ntls.rms.admin.ng`); пути без ссылки
на строку пока не зафиксированы.

Уровни: 1 — обязателен к моменту вывода `AGVControl` из работы, 2 — при включении визуализации и ручного
управления, 3 — без отдельного требования не реализуется.

| Канал | Контракт | Потребитель (файл и строка) | Уровень | Наш статус | Комментарий |
|---|---|---|---|---|---|
| HTTP | `POST /api/Order/move-to-node/{serial}/{nodeId}/{moveDir}` | `Services/AGVControlService.cs:118` | 1 | не начат | В снимке `/api/Order/move-to-node/{agvSerialNumber}/{toNodeId}/{agvMoveDirection}`; `moveDir` всегда `0` (`AGVControlService.cs:116`) |
| HTTP | `POST /api/Order/move-to-charge/{serial}` | `Services/AGVControlService.cs:131` | 1 | не начат | В снимке `{agvSerialNumber}`; при заданном узле вызывается `/api/Order/move-to-charge/{serial}/{nodeId}` (`AGVControlService.cs:135`) |
| HTTP | `POST /api/AgvCommand/{serial}/pause` | `Services/AGVControlService.cs:157` | 1 | не начат | Вызывается через `SendCommandToAgv(agv, "pause")` |
| HTTP | `POST /api/AgvCommand/{serial}/resume` | `Services/AGVControlService.cs:157` | 1 | не начат | `SendCommandToAgv(agv, "resume")` |
| HTTP | `POST /api/AgvCommand/{serial}/start-charging` | `Services/AGVControlService.cs:157` | 1 | не начат | `SendCommandToAgv(agv, "start-charging")` |
| HTTP | `POST /api/AgvCommand/{serial}/stop-charging` | `Services/AGVControlService.cs:157` | 1 | не начат | `SendCommandToAgv(agv, "stop-charging")` |
| HTTP | `POST /api/AgvCommand/{serial}/cancel-order` | `Services/AGVControlService.cs:157` | 1 | не начат | `SendCommandToAgv(agv, "cancel-order")` |
| HTTP | `POST /api/AgvCommand/{serial}/request-state` | `Services/AGVControlService.cs:157` | 1 | не начат | `SendCommandToAgv(agv, "request-state")` |
| HTTP | `POST /api/AgvCommand/{serial}/move-forward` | `Services/AGVControlService.cs:157` | 1 | не начат | `SendCommandToAgv(agv, "move-forward", {"distance": ...})` |
| HTTP | `POST /api/AgvCommand/{serial}/move-backward` | `Services/AGVControlService.cs:157` | 1 | не начат | `SendCommandToAgv(agv, "move-backward", {"distance": ...})` |
| HTTP | `POST /api/AgvCommand/{serial}/pick-load` | `Services/AGVControlService.cs:157` | 1 | не начат | `SendCommandToAgv(agv, "pick-load")` |
| HTTP | `POST /api/AgvCommand/{serial}/drop-load` | `Services/AGVControlService.cs:157` | 1 | не начат | `SendCommandToAgv(agv, "drop-load")` |
| HTTP | `POST /api/AgvCommand/{serial}/set-angle` | `Services/AGVControlService.cs:157` | 1 | не начат | `SendCommandToAgv(agv, "set-angle", {"angle": ...})` |
| HTTP | `POST /api/AgvCommand/{serial}/move-to-node` | `Services/AGVControlService.cs:157` | 1 | не начат | `SendCommandToAgv(agv, "move-to-node", {"nodeId": ...})`; отличается от `/api/Order/move-to-node` выше |
| Kafka | consume `simple-tasks` | WMS (строка не зафиксирована) | 1 | не начат | Формат: `samples/kafka-simple-tasks.json`, поля `taskId`, `status` |
| Kafka | produce `tasks-events` | WMS (строка не зафиксирована) | 1 | не начат | Формат: `samples/kafka-tasks-events.json`, поля `taskId`, `status`, `agvId` |
| Kafka | produce `agv-waiting` | WMS (строка не зафиксирована) | 1 | не начат | Формат: `samples/kafka-agv-waiting.json`, поля в PascalCase |
| MQTT | publish `APR/v2/{manufacturer}/{serial}/order` в формате VDA5050 2.0.0 | робот (внешний потребитель) | 1 | не начат | Заказ роботу, `version` = `2.0.0` |
| MQTT | publish `APR/v2/{manufacturer}/{serial}/instantActions` в формате VDA5050 2.0.0 | робот (внешний потребитель) | 1 | не начат | Мгновенные действия, `version` = `2.0.0` |
| MQTT | subscribe `APR/v2/{manufacturer}/{serial}/state` | робот (внешний потребитель) | 1 | не начат | Пример: `samples/mqtt-state.json` |
| MQTT | subscribe `APR/v2/{manufacturer}/{serial}/connection` | робот (внешний потребитель) | 1 | не начат | Пример: `samples/mqtt-connection.json` |
| MQTT | subscribe `APR/v2/{manufacturer}/{serial}/visualization` | робот (внешний потребитель) | 1 | не начат | Пример: `samples/mqtt-visualization.json` |
| MQTT | subscribe `APR/v2/{manufacturer}/{serial}/factsheet` | робот (внешний потребитель) | 1 | не начат | Сообщений в снимках нет, см. «Расхождения» |
| БД | запись `public."Orders"`, `public."Agvs"`, `public."AgvStates"` | `rms-admin`, отчеты (строка не зафиксирована) | 1 | не начат | Имена таблиц и колонок менять нельзя |
| HTTP | `GET /api/AgvStateMonitor/states/all` | `AGVControlSystem` (не развернут) | 2 | не начат | Визуализация состояния всех роботов |
| HTTP | `GET /api/AgvStateMonitor/state/{serial}` | `AGVControlSystem` (не развернут) | 2 | не начат | Состояние одного робота |
| HTTP | `GET /api/AgvStateMonitor/rawVisualizationAll` | `AGVControlSystem` (не развернут) | 2 | не начат | В снимке отсутствует, см. «Расхождения» |
| HTTP | `POST /Route/build-and-send` | `AGVControlSystem` (не развернут) | 2 | не начат | В снимке путь без префикса `/api` |
| HTTP | `POST /Route/update` | `AGVControlSystem` (не развернут) | 2 | не начат | |
| HTTP | `POST /Route/cancel/{agvId}/{orderId}` | `AGVControlSystem` (не развернут) | 2 | не начат | |
| HTTP | `GET /Route/info/{orderId}` | `AGVControlSystem` (не развернут) | 2 | не начат | |
| HTTP | `GET /api/Telemetry/rawStateall` | `AGVControlSystem` (не развернут) | 2 | не начат | Телеметрия, имя метода с двойной `l` в конце |
| HTTP | `GET /api/Telemetry/rawVisualisationAll` | `AGVControlSystem` (не развернут) | 2 | не начат | Обратите внимание: `Visualisation` через `s` |
| HTTP | `POST /api/AgvCommand/{serial}/move-to-node/{moveDir}` | `AGVControlSystem` (не развернут) | 2 | не начат | Ручное управление с направлением |
| HTTP | `POST /api/AgvCommand/move-to-nearest-node/{serial}` | `AGVControlSystem` (не развернут) | 2 | не начат | |
| HTTP | `POST /api/AgvCommand/{serial}/init-position` | `AGVControlSystem` (не развернут) | 2 | не начат | |
| HTTP | `POST /api/AgvCommand/{serial}/order` | `AGVControlSystem` (не развернут) | 2 | не начат | Передача произвольного VDA5050-заказа |
| HTTP | `GET /api/AgvCommand/{serial}/can-send-command` | `AGVControlSystem` (не развернут) | 2 | не начат | |
| HTTP | `GET /api/AgvCommand/{serial}/available-commands` | `AGVControlSystem` (не развернут) | 2 | не начат | |
| HTTP | `POST /api/AgvInstantActions/*` | `AGVControlSystem` (не развернут) | 2 | не начат | В снимке отсутствует, см. «Расхождения» |
| HTTP | `/api/Map/*`, `/map/{Id}`, `/node/{Id}/{mapId}` — управление картами | `AGVControlSystem` (не развернут) | 3 | не начат | В снимке пути без префикса `/api` |
| HTTP | `POST /api/AgvCommand/{serial}/custom-action` — кастомные действия | нет | 3 | не начат | |
| HTTP | `эндпоинты /api/AgvCommand/{serial}/{download-map,enable-map,delete-map}` — управление картами на роботе | нет | 3 | не начат | |
| HTTP | `эндпоинты /api/AgvStateMonitor/{start,stop,status,subscribe,unsubscribe,configure,monitored,statistics,updates}` | нет | 3 | не начат | Отладочные и служебные эндпоинты монитора |
| HTTP | `эндпоинты /api/AgvTask/*, /api/Visualization/*, /Traffic/*` | нет | 3 | не начат | Вне уровней 1 и 2, требует отдельного требования |

## Расхождения

1. Три формата идентификатора заказа в системе: `TSK:` (внутренний WMS), `ORD_` (приходит от робота,
   например `"orderId":"ORD_04329"` в `samples/mqtt-state.json`) и GUID (Kafka `simple-tasks`,
   `samples/kafka-simple-tasks.json`, а также `agv-waiting`). Нужен явный маппинг на границе нашего сервиса.
2. `GET /api/AgvTask/lastnode/{agvSerialNumber}` в текущей системе отвечает `404`: путь есть в снимке,
   но не работает, поэтому в уровень 1 не включен.
3. Топик `factsheet` робота не встретился ни в снимках MQTT, ни в подписках работающего сервиса; формат
   сообщения неизвестен, пока его не увидим в живой системе.
4. `GET /api/AgvStateMonitor/rawVisualizationAll` и `POST /api/AgvInstantActions/*` из уровня 2 в снимке
   отсутствуют: у монитора есть другие пути, а инстант-экшены представлены семейством
   `/api/agv/{agvSerialNumber}/actions/*`.
5. Kafka `agv-waiting` приходит в PascalCase и без полей `taskId`/`status`: поля называются `OrderId`,
   `Status`, `AgvSerialNumber`. Требование «`taskId`, `status` для Kafka» выполняется только для
   `simple-tasks` и `tasks-events`; у `agv-waiting` в тестах свой набор обязательных полей.
6. Пути маршрутов и телеметрии в снимке идут без префикса `/api` (`/Route/*`), а упоминания в спеке — с ним.
7. `moveDir` в `rms-admin` всегда `0` (`AGVControlService.cs:116`), хотя в снимке есть отдельный путь
   `/api/AgvCommand/{serial}/move-to-node/{moveDir}`.
