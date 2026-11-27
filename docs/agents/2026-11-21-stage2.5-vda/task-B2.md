# Задача B2: сборка заказа и публикация

Ветка `ai/stage2.5-vda`, последний коммит `e52fa9e` (чтение состояния уже есть).

## Что сделать

1. `Infrastructure/Mqtt/VdaOrderBuilder`: из `RoutePlanResult` собрать JSON заказа строго по образцу `doc/agents/2026-11-21-stage2.5-vda/evidence/vda-order.json`: `headerId`, `timestamp`, `version` `2.1.0`, `manufacturer`, `serialNumber`, `orderId` (`ORD_<число>`), `orderUpdateId` 0, `nodes` (четные `sequenceId`, `released`, `nodePosition` с `allowedDeviationXY` 0.1 и 0.3 у первого, `nodeDescription` `"{}"`, `actions` пустой), `edges` (нечетные `sequenceId`, `edgeId`, `startNodeId`, `endNodeId`, `orientation` 0, `orientationType` `TANGENTIAL`, `released`, `trajectory` NURBS из точек ребра, `actions` пустой).
2. `Infrastructure/Mqtt/VdaOrderPublisher`: публикация в `APR/v2/{manufacturer}/{serial}/order`, QoS 1, `headerId` из таймера.
3. `Infrastructure/Mqtt/RobotOrderStatusParser`: из `state` достать `orderId`, `orderUpdateId`, `driving`, `nodeStates`, `errors`.
4. Тест-эталон: сериализовать заказ для маршрута из двух ребер и сравнить набор ключей и значений с образцом структурно, не текстом.
5. Тест: номер заказа берется из `IMapVersionRepository`-независимого счетчика на основе `max("Orders"."OrderId")`? Нет: сделай просто `IRobotOrderNumberSource` с реализацией из БД `vrt_RobotOrder` и фолбэком 1.

## Ограничения

- Публиковать только в тестах с фейковым клиентом; реальную отправку включим на следующем шаге.
- Никаких записей в legacy-таблицы.
- Секретов в коде нет.
