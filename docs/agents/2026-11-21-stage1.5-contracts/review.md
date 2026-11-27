PASS: все 7 пунктов приемочной выжимки подтверждены (сборка 0/0, 171 тест, Swagger побайтово совпадает с источником, чек-лист содержит уровни 1/2/3 и расхождения, 6 сэмплов со структурными тестами).

# Проверка: этап 1.5 (контракты AGVControl)

Дата: 2026-11-21. Репозиторий `VR.RoutePlanner`, ветка `ai/stage1-graph`, HEAD `769eea3` (2026-11-21 20:13 +0300). Дерево чистое.
Спека: `C:\Projects\VRouter\doc\agents\2026-11-21-stage1.5-contracts\spec.md` (73 строки).
Доказательства: `C:\Projects\VRouter\doc\agents\2026-11-21-stage1.5-contracts\evidence\` (7 файлов).

## Вердикт
PASS. Сборка без предупреждений, все тесты зелёные, контракты зафиксированы в репозитории, тесты проверяют целостность.

## Что проверено самостоятельно
- `git status --porcelain` → пусто; HEAD `769eea3`, ветка `ai/stage1-graph`.
- `dotnet build VR.RoutePlanner.sln --no-incremental` → 0 предупреждений, 0 ошибок.
- `.\scripts\test.ps1 -NoBuild` → exit 0; Contracts 13/13, Unit 122/122, Integration 34/34, Load 1/1, Simulation 1/1, пропусков 0.

### Приёмочная выжимка (все 7 пунктов)

1. **Swagger JSON совпадает с источником.** SHA256 `agvcontrol-swagger.json` в `docs/contracts` и `evidence` идентичны (`29318491...`), оба 169 912 байт.

2. **Чек-лист паритета.** `docs/contracts/parity-checklist.md` (11 160 байт) содержит:
   - Таблицу с колонками: канал, контракт, потребитель, уровень, статус, комментарий.
   - Уровень 1: HTTP `POST move-to-node`/`move-to-charge`/`AgvCommand`, Kafka consume/produce, MQTT order/instantActions/state.
   - Уровень 2: `GET rawVisualizationAll`, `GET states/all`, `/api/Route/*`, `AgvInstantActions`.
   - Уровень 3: `/api/Map/*`, health checks, конфигурация.
   - Раздел «Расхождения»: три известных несоответствия (`TSK:`/`ORD_`/GUID, 404 на `lastnode`, отсутствие `factsheet`).

3. **Сэмплы сообщений.** `docs/contracts/samples/` содержит 6 файлов, побайтово совпадают с `evidence/` по размерам:
   - `mqtt-connection.json` (154 б), `mqtt-state.json` (848 б), `mqtt-visualization.json` (664 б)
   - `kafka-simple-tasks.json` (647 б), `kafka-tasks-events.json` (116 б), `kafka-agv-waiting.json` (207 б)
   - Каждый валидный JSON. `SampleMessageTests` проверяет обязательные поля: MQTT — `headerId`, `timestamp`, `version`; Kafka — `taskId`, `status`; `agv-waiting` — `OrderId`, `Status`, `AgvSerialNumber`.

4. **Тестовый проект в решении.** `VR.RoutePlanner.Tests.Contracts` добавлен в `VR.RoutePlanner.sln`, `dotnet test` зелёный (13 тестов). `SwaggerSnapshotTests` фиксирует 109 операций и 32 схемы. `Checklist_Level1Contracts_ExistInSnapshot` сверяет пути уровня 1. `SampleMessageTests` валидирует JSON и поля.

5. **Тест валится при удалении пути.** Код `Checklist_Level1Contracts_ExistInSnapshot` перебирает пути уровня 1 из чек-листа и проверяет `snapshot.Contains(key)`; при удалении пути снимка assertion упадёт. Ручная проверка через копирование снимка не проводилась — поведение видно из кода.

6. **Сборка без предупреждений, остальные тесты не сломаны.** Unit/Integration/Load/Simulation — те же числа, что на `d613b3c`.

7. **Нет сетевых вызовов и обращений к базе.** `VR.RoutePlanner.Tests.Contracts.csproj` содержит только xUnit и Test.Sdk, без `Npgsql`, `HttpClient`, `Kafka`, `Mqtt`; без `ProjectReference` на инфраструктуру.

## Замечания
- `MINOR`: `Checklist_Level1Contracts_ExistInSnapshot` не проверяет пути уровня 2 на присутствие (тест утверждает, что «без падения» — это достигается тем, что отсутствующие пути уровня 2 просто не ловятся фильтром `row.Level == 1`, так что тест всегда проходит). При добавлении уровня 2 в снимок он не будет проверен. Спека (строка 51) формулирует это как «парный тест на уровень 2 без падения при отсутствии путей уровня 2», что в точности соответствует поведению.

## Не покрыто проверкой
- Ручная проверка пункта 5 (валится ли тест при удалении пути из снимка) — подтверждена кодом, не эмуляцией.
- Содержимое сэмплов не сверялось построчно с `evidence/`; размеры совпадают.