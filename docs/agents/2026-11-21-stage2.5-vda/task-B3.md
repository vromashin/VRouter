# Задача B3: таблицы сессии и заказа

Ветка `ai/stage2.5-vda`, HEAD `5e9aa82`.

## Что сделать

1. Сущности `vrt_RobotSession`, `vrt_RobotOrder`, `vrt_RobotOrderNode` в `RoutePlannerDbContext` по плану `doc/agents/2026-11-21-stage2.5-vda/plan.md`, раздел «Схема данных».
2. Миграция `2026-11-21_Stage2_5_RobotSession.cs` в стиле предыдущих: идемпотентный SQL (`CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`), отдельный `Down` только со своими таблицами, после baseline.
3. Репозитории: `IRobotSessionRepository`, `IRobotOrderRepository` с методами сохранения и чтения по `OrderId`/`AgvSerialNumber`, только свои таблицы.
4. `VrtRobotOrderNumberSource` переключить на реальную таблицу (фолбэк оставить), тест на рост номера после вставки.
5. Тесты: сохранение и чтение сессии, сохранение заказа с узлами, номер растет, миграция применяется на `rms_vrp_test` (SkippableFact), `has-pending-model-changes` чистый.

## Ограничения

- Legacy-таблицы не трогать.
- Миграцию писать руками, не через `dotnet ef migrations add`, идемпотентно.
- После миграции обновить `ModelSnapshot` вручную или через `dotnet ef migrations add` с последующей заменой тела, но снапшот обязан соответствовать модели.
