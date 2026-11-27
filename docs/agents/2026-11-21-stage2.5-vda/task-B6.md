# Задача B6: живая позиция на карте из visualization

Ветка `ai/stage2.5-vda`. Проект `C:\Users\User\source\repos\VR.RoutePlanner`.

## Проблема

Карта и админка читают позицию из `public."Agvs"`. Мы пишем туда из события `state`, а симулятор публикует `state` редко, поэтому на карте движения не видно. Позиция на 5 Гц приходит в топике `.../visualization`.

## Что сделать

1. Подписка на `APR/v2/+/+/visualization` в `MqttRobotStateReader` (или отдельный `MqttRobotVisualizationReader`, как чище).
2. Разбор `visualization`: `agvPosition` (`x`, `y`, `theta`, `mapId`), `batteryState.batteryCharge`, `driving`, `paused`, `safetyState`, `fork`, `velocity`.
3. Событие `VisualizationReceived` и обновление памяти снимка позиции отдельно от `state`: новые поля в `RobotSnapshot` или отдельный `RobotVisualization`.
4. Запись в `Agvs` из визуализации с троттлингом: не чаще 2 раз в секунду на робота. Позиция, theta, батарея, `driving` → `CurrentState` (`WORKING`/`IDLE`), `LastUpdateTime`. Только при `Legacy:WriteAgvsEnabled`.
5. `GET /api/agv/{serial}/state` продолжает работать; позицию брать самую свежую из visualization, остальное из `state`.
6. Тесты: разбор образца (ниже), троттлинг, обновление позиции при неизменном `state`, отсутствие записи при выключенном флаге.

Образец `visualization` снят с симулятора:

```json
{"agvPosition":{"deviationRange":0,"localizationScore":0,"mapDescription":"","mapId":"801","positionInitialized":false,"theta":2.5557217597961426,"x":12.640478134155273,"y":6.689114093780518},"batteryState":{"batteryCharge":98.83719635009766,"batteryHealth":100,"batteryVoltage":56855.8046875,"charging":false,"reach":0},"driving":true,"fork":{"forkPos":0,"liftPos":-0.05144999921321869},"headerId":1720,"loads":[],"manufacturer":"Tuskrobots","operatingMode":"AUTOMATIC","paused":false,"safetyState":{"eStop":"NONE","fieldViolation":false},"serialNumber":"98001","timestamp":"2026-11-22T11:44:47.195Z","velocity":{"omega":-0.5635315179824829,"vx":3.898255727108335e-07,"vy":-2.586850769148441e-07},"version":"2.1.0"}
```

## Ограничения

- Ничего не публиковать в MQTT.
- Схему БД не менять.
