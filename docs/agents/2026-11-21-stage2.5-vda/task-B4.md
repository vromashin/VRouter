# Задача B4: автомат заказа и HTTP оператора

Ветка `ai/stage2.5-vda`, HEAD `afe7cb1` (таблицы, репозитории, публикация и разбор состояния готовы).

## Что сделать

1. `Application/Robots/RobotOrderService`: по заказу строит план (`IPathPlanner`), собирает и публикует VDA-заказ, сохраняет `RobotOrder` и `RobotOrderNode`, ведет статус.
2. Автомат статусов: `Draft` → `Sent` → `Accepted` → `Executing` → `Completed`; отказы `Rejected`, `Failed`, `Cancelled`. Таймауты `AcceptTimeoutSeconds` 10 и `StateSilenceSeconds` 15 из `Routing:Order`.
3. Обновление статуса при получении `state`: совпадение `orderId`, рост `orderUpdateId`, переход по `driving` и `nodeStates`, закрытие по последнему узлу и `driving=false`.
4. Восстановление при старте: прочитать `RobotOrder` в незавершенном статусе; если `state` подтверждает тот же `orderId` и робот едет, продолжить, иначе `Failed` с причиной `STATE_MISMATCH`.
5. HTTP: `POST /api/agv/{serial}/order` (тело `mapId`, `targetNodeId`, `hasLoad`), `POST /api/agv/{serial}/instant-action` (тип действия), `GET /api/agv/{serial}/state`, `GET /api/agv/{serial}/order/{orderId}`. Ответы через DTO, коды 409 при активном заказе, 404 при неизвестном заказе.
6. Тесты: переходы автомата, таймауты, закрытие по последнему узлу, конфликт 409, восстановление по двум сценариям, HTTP-контракты через `WebApplicationFactory` или прямые вызовы контроллера.

## Ограничения

- Реальную публикацию в MQTT включать не нужно: в тестах фейковый транспорт.
- Legacy-таблицы не трогать.
- Секретов нет.
