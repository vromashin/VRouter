# Спека этапа 1.5: заморозка контрактов интеграции

Дата: 2026-11-21. Репозиторий `VR.RoutePlanner`, ветка `ai/stage1-graph`.

## Зачем

Мы заменяем блок `AGVControl` своим сервисом, и вызывающие модули не должны почувствовать подмену. Сейчас контракты существуют только в коде `AGVControl`, их надо зафиксировать в репозитории и проверять машиной, иначе при разработке замены расхождения обнаружатся на интеграции.

## Часть 1. Что кладем в репозиторий

Каталог `docs/contracts` в `VR.RoutePlanner`, коммитится:

1. `agvcontrol-swagger.json` — снимок `GET http://127.0.0.1:5231/swagger/v1/swagger.json` от 2026-11-21, 109 операций, 32 схемы. Взят из работающего сервиса на 131.
2. `parity-checklist.md` — таблица «канал, контракт, потребитель, уровень, что делает наш сервис». Заполняется по фактам ниже.
3. `samples/` — реальные сообщения, снятые с работающей системы 2026-11-21:
   - `mqtt-connection.json`, `mqtt-state.json`, `mqtt-visualization.json` (топики `APR/v2/Tuskrobots/98001/*`);
   - `kafka-simple-tasks.json`, `kafka-tasks-events.json`, `kafka-agv-waiting.json`.
   Каждый файл — ровно одно сообщение, без правок.

Файлы-источники для снимков лежат в `C:\Projects\VRouter\doc\agents\2026-11-21-stage1.5-contracts\evidence\`, переносить их содержимое один в один.

## Часть 2. Что фиксируем как уровни паритета

Уровень 1, обязателен к моменту вывода `AGVControl` из работы (вызывается сейчас):

| Канал | Контракт | Потребитель |
|---|---|---|
| HTTP | `POST /api/Order/move-to-node/{serial}/{nodeId}/{moveDir}` | `rms-admin`, `AGVControlService.cs:118` |
| HTTP | `POST /api/Order/move-to-charge/{serial}` | `rms-admin`, `AGVControlService.cs:131` |
| HTTP | `POST /api/AgvCommand/{serial}/{command}` | `rms-admin`, `AGVControlService.cs:157` |
| Kafka | consume `simple-tasks` (формат в `samples/kafka-simple-tasks.json`) | WMS |
| Kafka | produce `tasks-events` (формат в `samples/kafka-tasks-events.json`) | WMS |
| Kafka | produce `agv-waiting` (формат в `samples/kafka-agv-waiting.json`) | WMS |
| MQTT | `APR/v2/{manufacturer}/{serial}/order` и `.../instantActions` в формате VDA5050 2.0.0 | робот |
| MQTT | подписка на `.../state`, `.../connection`, `.../visualization`, `.../factsheet` | робот |
| БД | запись в `public."Orders"`, `public."Agvs"`, `public."AgvStates"` под существующими именами | `rms-admin`, отчеты |

Уровень 2, нужен при включении визуализации и ручного управления (сейчас `AGVControlSystem` на сервер не развернут):

`GET /api/AgvStateMonitor/states/all`, `GET /api/AgvStateMonitor/rawVisualizationAll`, маршруты `/api/Route/*`, телеметрия `/api/Telemetry/*`, оставшиеся команды `AgvCommand` и инстант-экшены `AgvInstantActions`.

Уровень 3, не реализуем без отдельного требования: управление картами (`/api/Map/*`), кастомные действия, отладочные эндпоинты.

Полный перечень путей и методов берется из `agvcontrol-swagger.json`; чек-лист перечисляет только те, что попали в уровни 1 и 2, с пометкой уровня.

## Часть 3. Машинная проверка

Тестовый проект `tests/VR.RoutePlanner.Tests.Contracts` (новый, xUnit), без базы и без сети:

1. `SwaggerSnapshot_Parses_AndHasExpectedOperations`: файл читается, `openapi` непустой, число операций 109, число схем 32. Числа зафиксированы как ожидаемые в тесте.
2. `Checklist_Level1Contracts_ExistInSnapshot`: каждый путь уровня 1 из чек-листа присутствует в снимке с тем же методом. Парный тест на уровень 2 без падения при отсутствии путей уровня 2.
3. `Samples_Parse` для каждого файла из `samples/`: JSON валиден, обязательные поля на месте (`headerId`, `timestamp`, `version` для MQTT; `taskId`, `status` для Kafka).
4. `PrepareForBuild` не требуется, файлы читаются через `AppContext.BaseDirectory` с копированием в выходной каталог (`CopyToOutputDirectory`).

Тест не проверяет наш будущий фасад (его еще нет), он фиксирует контракт и валит сборку, если снимок или чек-лист испорчены.

## Часть 4. Формат чек-листа

Таблица с колонками: канал, контракт, потребитель (файл и строка), уровень, наш статус (`не начат`, `в работе`, `паритет подтвержден`), комментарий. Ниже таблицы раздел «Расхождения» для известных несоответствий: три формата идентификатора заказа (`TSK:`, `ORD_`, GUID), 404 на `/api/AgvTask/lastnode` в текущей системе, отсутствие `factsheet` в топиках робота.

## Приёмочная выжимка

1. В репозитории есть `docs/contracts/agvcontrol-swagger.json`, побайтово совпадает с источником в `evidence/`.
2. Есть `docs/contracts/parity-checklist.md` с уровнем 1, 2, 3 и разделом «Расхождения».
3. Есть `docs/contracts/samples/` с шестью файлами, каждый валидный JSON. Обязательные поля проверяются по каналу: `headerId`, `timestamp`, `version` для MQTT, `taskId` и `status` для `simple-tasks` и `tasks-events`, `OrderId`, `Status` и `AgvSerialNumber` для `agv-waiting` в том виде, в каком сообщение снято с живой системы.
4. Новый тестовый проект `VR.RoutePlanner.Tests.Contracts` добавлен в решение, `dotnet test` по нему зеленый.
5. Тест валится, если удалить путь уровня 1 из снимка (проверить вручную приемом: скопировать снимок, убрать `paths`, прогнать, вернуть).
6. Сборка решения без предупреждений, остальные тесты не сломаны.
7. Никаких сетевых вызовов и обращений к базе в тестах контрактов.

## Не входит

Реализация HTTP-фасада, Kafka-консьюмеров и MQTT-клиента. Это этап 5.
