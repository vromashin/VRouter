# Шаг 3: живой прогон и прогон тестов

Дата: 2026-11-24. Ветка `ai/stage2.5-vda-port-step3`, HEAD `13e9df6`.

## Тесты

`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1` (живая база):

```
Unit 309/309, Integration 44/44, Contracts 13/13, Load 1/1, Simulation 1/1, пропущено 0
```

Первые два прогона давали 6 падений `Integration` с `Npgsql: Timeout during reading attempt`
на `DELETE FROM vrt_TrafficResource` / `TRUNCATE`. Причина не в коде шага 3: в базе
`rms_vrp_test` осталась сессия в состоянии `idle in transaction`, державшая блокировку
после ранее прерванного прогона. Сессия завершена через `pg_terminate_backend`,
после этого набор `Integration` полностью зелёный.

Вывод для стенда: после прерывания интеграционных прогонов проверять
`pg_stat_activity` на `idle in transaction` и снимать такие сессии, иначе тесты падают по таймауту.

## Живой прогон

Заказ `ORD_00158` на `wo-2-12`, `hasLoad = true`:

```
ORD_00158|Completed|-|wo-2-12
forkPos max, мм: 1199.7
```

То есть после переноса автомата и слоя резервов заказ доезжает, вилы выдвигаются
на полную длину, заказ закрывается в `Completed`, `TargetNodeId` совпадает с узлом действия.

## Ограничения

- сценарий 409 `TRAFFIC_BLOCKED` требует двух роботов, на стенде один;
- `PROGRESS_TIMEOUT` после рестарта процесса до первого `state` опирается на `AcceptedAt`.
