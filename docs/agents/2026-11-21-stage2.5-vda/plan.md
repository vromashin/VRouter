# План этапа 2.5: VDA-сессия и исполнение заказа на симуляторе

Дата: 2026-11-21. Репозиторий `VR.RoutePlanner`.

Цель: один реальный робот (симулятор TUSKROBOTS 98001 на 192.168.89.131) принимает заказ от нашего сервиса и проезжает маршрут, построенный планировщиком этапа 2. Старый `agv-control-api` для этого серийного номера выключен.

## Границы

Входит:
- MQTT-клиент и VDA-сессия робота: state, connection, order, instantActions.
- Сборка VDA-заказа из плана, окно выпуска узлов, согласование nodeStates.
- Конечный автомат заказа с таймаутами и восстановлением после рестарта.
- Минимальный HTTP для оператора: заказ до узла, мгновенное действие, чтение состояния.
- Таблицы нашей схемы под сессию и заказ, без записи в legacy.

Не входит: Kafka и WMS, паритет админки, резервирования нескольких роботов (этап 3), factsheet, загрузка карт.

## Факты, на которых строим

- Брокер: 192.168.89.131:1883, учётка `rcs`. Неймспейс робота: `APR/v2/Tuskrobots/98001/{connection,state,visualization,factsheet,order,instantActions}`.
- Робот публикует `state` и `visualization` (5 Гц), `connection`; `order` и `instantActions` публикует мастер, то есть мы.
- В образцах `state` поля VDA 2.0: `agvPosition`, `batteryState`, `driving`, `errors`, `nodeStates`, `edgeStates`, `orderId`, `orderUpdateId`, `actionStates`, `safetyState`.
- Симулятор уже принимал заказы от старого сервиса, значит формат заказа рабочий; расхождение было только в идентификаторах (`TSK:` против `ORD_` и GUID).
- Старый сервис писал `Orders`, `Agvs`, `AgvStates`. Мы в них не пишем, пока робот не переключен полностью.

## Схема данных (своя, `vrt_`)

`vrt_RobotSession`: `AgvSerialNumber` (ключ), `Manufacturer`, `ProtocolVersion`, `ConnectionState`, `LastSeenAt`, `StateJson`, `UpdatedAt`.

`vrt_RobotOrder`: `OrderId` (ключ), `AgvSerialNumber`, `OrderUpdateId`, `Status`, `PlanJson`, `ReleasedNodeCount`, `TargetNodeId`, `ApproachNodeId`, `CreatedAt`, `SentAt`, `AcceptedAt`, `CompletedAt`, `FailureReason`.

`vrt_RobotOrderNode`: `OrderId`, `SequenceId`, `NodeId`, `Released`, `Reached`, `ActionCount`.

История миграций прежняя, `__EFMigrationsHistory_RoutePlanner`, baseline не трогаем.

## Канал MQTT

Клиент `MQTTnet`, `CleanSession=false`, QoS 1 на публикацию, `LastWill` на `connection` со статусом `CONNECTIONBROKEN`.

Подписки: `state`, `connection`, `order` (ответы `accept`/`reject`), `instantActions`.

Публикации: `order` (полный заказ и обновления), `instantActions` (пауза, продолжение, отмена, зарядка), при необходимости `factsheet` запрос.

Правила: одно соединение на робота, реконнект с backoff, дедупликация по `headerId`, строгая проверка `version` 2.0.0 и `manufacturer`.

## Заказ из плана

`RoutePlanResult` → VDA `order`: `orderId` GUID, монотонный `orderUpdateId`, узлы с `sequenceId`, `released`, `nodePosition`, действия (`pick`/`drop` в этапе 2.5 не ставим, только движение), ребра с `edgeId`, `sequenceId`, `released`, `trajectory`.

Окно выпуска: `Routing:Order:ReleasedNodes` (по умолчанию 5) узлов и ребер вперед. Продление при `nodeStates[] == REACHED`, откат при `FAILED` или при отсутствии прогресса `Routing:Order:ProgressTimeoutSeconds` (60 с).

Смена маршрута: `orderUpdateId + 1`, только для выпущенных узлов, не тронутых движением.

## Конечный автомат

Состояния: `Draft` → `Sent` → `Accepted` → `Executing` → `Completed`; терминальные отказы `Rejected`, `Failed`, `Cancelled`.

Переходы и таймауты: `Sent` → `Accepted` (`AcceptTimeoutSeconds`, 10 с), иначе `Rejected` с причиной `NO_ACCEPT`; `Accepted` → `Executing` по первому `REACHED`; `Executing` → `Completed` при последнем узле; молчание `state` дольше `StateSilenceSeconds` (15 с) это `Failed` с `STATE_SILENCE`.

Рестарт сервиса: сессия читается из `vrt_RobotSession`, заказ из `vrt_RobotOrder`; если робот в `driving=true` и `orderId` совпадает, продолжаем с текущего `orderUpdateId`, иначе отмена и новый заказ.

Один робот, один активный заказ: второй заказ возвращает `409 ORDER_IN_PROGRESS`.

## HTTP оператора

`POST /api/agv/{serial}/order` тело `{mapId, targetNodeId, hasLoad}`: строит план, публикует заказ, возвращает `orderId`, план и точку подхода.

`POST /api/agv/{serial}/instant-action` тело `{actionType, actionId, params}`: `pause`, `resume`, `cancelOrder`, `startCharging`, `stopCharging`.

`GET /api/agv/{serial}/state`: последнее состояние, статус заказа, ошибки робота, время последнего сообщения.

`GET /api/agv/{serial}/order/{orderId}`: статус и прогресс по узлам.

Конфигурация `Routing:Mqtt`: `Host`, `Port`, `Username`, `Password`, `ClientIdPrefix`, `KeepAliveSeconds`, `ReconnectBackoffMaxSeconds`. Секретов в репозитории нет, только через окружение.

## Развёртывание

Юнит `vrp-agent.service` рядом с существующими `agv-control-api` и `agv-fleet-manager`, каталог `/opt/rms/vrp-agent`, конфиг `/opt/rms/vrp-agent/appsettings.json`, пользователь `rms`.

Переключение по серийному номеру: пока `Routing:Mqtt:EnabledSerials` не содержит `98001`, сервис только читает брокер и ничего не публикует. Откат это удаление номера из списка и `systemctl restart` старого сервиса.

## Тесты

Unit: сборка VDA-заказа из плана (порядок узлов, released, orderUpdateId), переходы автомата и таймауты, продление окна по `REACHED`, откат при `FAILED`, `409` на второй заказ, восстановление после рестарта.

Integration без брокера: сериализация и десериализация образцов из `docs/contracts/samples`, проверка обязательных полей VDA.

Integration с брокером 131 (по переменной `VRP_MQTT_HOST`, иначе `Skip`): публикация заказа в тестовый топик и проверка, что симулятор отвечает `accept`.

Приёмка на живом роботе: заказ `wa128` → `wo-4-9`, робот принимает, доезжает по маршруту из 14 сегментов, заказ `Completed` не позже чем через 1.5× расчетного времени (около 45 с). Затем отмена на середине маршрута, робот останавливается, заказ `Cancelled`, ошибок в `state` нет.

## Риски и неизвестные

Образцы сняты 2026-11-21 и лежат в `evidence/` (`vda-order.json`, `vda-instantActions.json`, `vda-state-after.json`). Фактический формат отличается от предположений:

- Заказ: `version` **2.1.0**, `orderId` вида `ORD_04330`, `orderUpdateId` 0, 17 узлов и 16 ребер, **все released сразу**, окна нет.
- `sequenceId` честный: узлы четные 0..32, ребра нечетные 1..31.
- `nodeDescription` это строка `"{}"`, `allowedDeviationXY` 0.1 для маршрутных узлов и 0.3 для первого, `allowedDeviationTheta` 0.1 и 3.14.
- Ребро: `orientationType` TANGENTIAL, `trajectory` NURBS в том же формате, что в `store-map` (degree, knotVector, controlPoints с weight), направление берется из проезда, а не из порядка в `edgeId` (последнее ребро: `edgeId` `801-wa129-wa130`, а `startNodeId` `wa130`).
- InstantActions: `version` **"v2"**, `type` instantActions, `actionType` stateRequest, `blockingType` NONE.
- `state`: `version` 2.0.0, публикуется редко, по смене состояния (примерно раз в 15 с на узлах), `nodeStates` пуст, когда робот стоит.

Проверка исполнения старого заказа: робот проехал 17 узлов от `pa1` до `wa129` примерно за 5 минут, `errors` 0, `orderId` совпал. При этом `Orders.Status` остался `IN_PROGRESS` без `CompletedTime`, то есть старый сервис заказ не закрывает. Это подтверждает дефект, ради которого делается замена.

Остающийся риск: `state` приходит редко, поэтому прогресс надо вести и по `visualization` (5 Гц), иначе таймаут молчания будет срабатывать ложно.


## Оценка

6-9 человеко-дней с командой агентов: MQTT-сессия 2, сборка заказа и автомат 2, HTTP и данные 1, тесты и приёмка 2, запас 1-2.

Порядок работ: сначала снять образцы `order` и `instantActions` от старого сервиса, затем каркас MQTT и чтение состояния, затем публикация заказа и приёмка на симуляторе, затем отмена, рестарт и устойчивость.





