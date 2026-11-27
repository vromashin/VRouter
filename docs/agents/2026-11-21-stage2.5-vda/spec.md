# Спека этапа 2.5

Ветка `ai/stage2.5-vda`. План и образцы: `doc/agents/2026-11-21-stage2.5-vda/`.

## Задачи

1. MQTT-слой (`Infrastructure/Mqtt`): MQTTnet, один клиент на процесс, реконнект с backoff, QoS 1, CleanSession=false, события потери соединения.
2. Читатель состояния: разбор `state` (2.0.0) и `visualization` (5 Гц) в `RobotSnapshot` с полями позиция, driving, orderId, orderUpdateId, nodeStates, errors, battery.
3. Сборка VDA-заказа из `RoutePlanResult`: version 2.1.0, orderId `ORD_<номер>`, orderUpdateId 0, sequenceId четный у узлов и нечетный у ребер, nodePosition с allowedDeviationXY 0.1 (0.3 у первого), nodeDescription "{}", trajectory NURBS из наших точек.
4. Публикация в `APR/v2/{manufacturer}/{serial}/order`, ожидание accept/reject по `state`.

