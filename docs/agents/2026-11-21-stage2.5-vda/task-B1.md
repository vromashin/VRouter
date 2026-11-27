# Задача B1: MQTT-клиент и чтение состояния робота

Ветка `ai/stage2.5-vda`. Проект `C:\Users\User\source\repos\VR.RoutePlanner`.

## Что сделать

1. Пакет `MQTTnet` в `VR.RoutePlanner.Infrastructure`.
2. `Infrastructure/Mqtt/MqttOptions` (секция `Routing:Mqtt`): `Host`, `Port`, `Username`, `Password`, `ClientIdPrefix`, `KeepAliveSeconds`, `ReconnectMaxSeconds`, `Enabled`.
3. `IRobotStateReader` и `RobotSnapshot`: `SerialNumber`, `ConnectionState`, `Driving`, `OrderId`, `OrderUpdateId`, `PositionX`, `PositionY`, `Theta`, `MapId`, `BatteryCharge`, `Errors`, `NodeStates`, `ReceivedAt`.
4. `MqttRobotStateReader`: подписка на `APR/v2/{manufacturer}/{serial}/state`, разбор JSON версии 2.0.0 и 2.1.0, сохранение последнего снимка в памяти.
5. Хостед-сервис в `VR.RoutePlanner.Workers`, включается по `Routing:Mqtt:Enabled`.
6. Тесты: разбор двух образцов из `docs/contracts/samples/mqtt-state.json` и `doc/agents/2026-11-21-stage2.5-vda/evidence/vda-state-after.json`, плюс тест на битый JSON (не падает, возвращает null и логирует).

## Ограничения

- Ничего не публиковать в MQTT на этом шаге, только читать.
- Не трогать существующие миграции и таблицы.
- Секретов в коде и конфиге нет, только из окружения или appsettings без пароля.
