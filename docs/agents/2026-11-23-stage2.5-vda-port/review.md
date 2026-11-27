# Проверка: Шаг 2.5 — перенос сборки и ведения VDA-заказа из AGVControl в VR.RoutePlanner

Дата: 2026-11-23 (повторная проверка после блокеров). Репозиторий: `VR.RoutePlanner` (`C:\Users\User\source\repos\VR.RoutePlanner`), ветка `ai/stage2.5-vda-port`, HEAD `5d7df3d`, база диффа `bd9cb69`. Два коммита в диапазоне: `2333202` (перенос) и `5d7df3d` (виртуальный старт + Front/Rear по формулам спеки). Эталон `AGVControl` — только чтение.

## Вердикт

PASS — все блокеры прошлой проверки закрыты: обязательные доказательства стенда (`state-after-action.json`, `lastnode-200.txt`, `stand-run.md`, `order-json.json`) положены и сходятся с тем, что видно в коде; виртуальное ребро §2.5 реализовано и покрыто 4 тестами; `Front`/`Rear` приведены к формулам спеки. Сборка `-c Release --no-incremental` и полный `.\scripts\test.ps1` прогнаны контролёром самостоятельно — 0 ошибок, 0 пропущено.

## Что проверено самостоятельно

- `dotnet build VR.RoutePlanner.sln -c Release --no-incremental` → **Сборка успешно завершена. Предупреждений: 0, Ошибок: 0** (6.1 с).
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1` (полный прогон с живой БД `rms_vrp_test`, без `-NoDb`) → все зелёные, **пропущено 0**: Simulation 1/1, Load 1/1, Contracts 13/13, Unit **260/260**, Integration **44/44**. Интеграционные не пропущены.
- `git -C C:\Projects\VRouter\AGVControl status --porcelain` → пусто (эталон не тронут).
- `git diff --name-only bd9cb69..HEAD` → 27 файлов, все в `VR.RoutePlanner/src` и `tests`; **нет** `**/Migrations/**`, `appsettings*.json`, `.env`/`.pem`/`.key`. Таблицы `vrt_RobotOrder`/`vrt_RobotSession`/`vrt_RobotOrderNode` не изменены.
- `git status --porcelain` (VR.RoutePlanner) → пусто; ветка `ai/stage2.5-vda-port`; два коммита с сообщениями в стиле репозитория.
- `WorkingNodeGeometry.cs:14,62,73` — `WrkLength = 1.475`; `Front = N − 1.475·(cosθ, sinθ)`, `Rear = N + 1.475·(cosθ, sinθ)`; `StopPoint` берёт ближайшую к точке подхода. Сверено с живым `order-json.json`: узел `wo-2-12` (9.71, 24.42), подход с +x (θ=π), точка остановки x=11.185 = 9.71 + 1.475 — сходится с формулой.
- `WorkingNodeStopResolver.Resolve` — один входящий узел → он; иначе предыдущий узел маршрута; иначе поза робота; θ = atan2(N−A). Соответствует §2.2.
- `VdaOrderBuilder` — `sequenceId` узла `2i`, ребра `2i+1`; `released=true`; рабочий узел → `nodePosition`=точка остановки, `containerX/Y`=координаты узла, `containerTheta`=θ; действие с 8 параметрами в порядке `containerTypeId=2, containerX, containerY, containerTheta, onlyFork=true, autoReturnNode=true, noScan=false, adjustWithoutOA=true`, `blockingType="SOFT"`; `WAYPOINT`/`WORKING` без `theta`; первый узел при `orderUpdateId=0` — поза робота, допуски 0.3/3.14, при `FirstNodeTheta=false` поля `theta` нет; `orientation` только у первого ребра (`PI` при `Backward`, `0` при `Forward`, у остальных `0`); `maxSpeed`/`maxRotationSpeed`/`trajectory{degree:1,knotVector:[0,0,1,1]}`.
- Виртуальный старт (`NeedsVirtualStart`/`PrependVirtualStart`/`BuildVirtualStartNode`/`BuildVirtualStartEdge`) — включается только при `orderUpdateId > 0` и позе робота дальше 0.1 м от первого узла; виртуальное ребро `VIRTUAL_START_<nodeId>` с `orientation=null`, `orientationType=null`, `rotationAllowed=false` (соответствует `CreateVirtualEdge` в AGV); нумерация сдвигается на два (узел 0/ребро 1). 4 теста `VdaOrderVirtualStartTests` (260-й unit подтверждает).
- `ExtendPlanAsync` — семь условий §3.3 в коде, троттлинг 2 с, монотонный `orderUpdateId+1`, без пересчёта `sequenceId`, ранний выход при `ReleasedNodes <= 0`. `OrderChunkPlanner` — `<= 0` весь маршрут, `> 0` куски с перекрытием 1.
- `OrderOptions` — `MaxOrderUpdates = 32` (новый), `FirstNodeTheta` без инициализатора → `false`, `ReleasedNodes` остался `3`, `StopBeforeSlot`/`SlotApproachResolver` удалены (`grep` → 0 вхождений). `RoutePlannerOptions.BaseNodesCount` — `[Obsolete]`, дефолт 40, в нарезке не участвует.
- `LegacyAgvTaskController` — `GET api/AgvTask/lastnode/{serial}` единственный ответ `Ok(...)` (узел либо `null`), 404 нет.
- `RobotSnapshot`/`RobotStateParser` — `EdgeStates` (`edgeId`, `sequenceId`, `released`) есть и читаются.
- `grep "1\.475" src` → 3 вхождения: ровно одно кодовое (`WorkingNodeGeometry.cs:14`), два в XML-комментариях.

## Сверка доказательств стенда

- `state-after-action.json`: `lastNodeId="wo-2-12"` (узел действия), `actionStates[0] = {actionId: b11d6ce7-…, actionType: drop, actionStatus: FINISHED}`, `driving:false`, `orderId:ORD_00156`. `actionId` **совпадает** с `actionId` действия `drop` в `order-json.json` — критерий 12/выжимка п.21 подтверждён.
- `order-json.json`: один заказ `orderUpdateId=0`, два узла (`wa57` → `wo-2-12`); первый узел без `theta`, `allowedDeviationTheta=3.14`; рабочий узел `wo-2-12` смещён на 1.475 м (`x=11.185` при узле 9.71), `containerTheta=π`, 8 параметров в заданном порядке, `autoReturnNode=true`; ребро `orientation=0`, `trajectory degree 1, knotVector [0,0,1,1]`.
- `order-status.txt`: `ORD_00156|Completed|-|wo-2-12`.
- `lastnode-200.txt`: `HTTP/1.1 200 OK`, `Content-Type: application/json`; тело повторяет контракт `AgvNodeResponse` (`nodeId=wo-2-12`, `nodeType=WORKING`, `positionX=9.71`/`positionY=24.42` = координаты узла, `mapId` — GUID ключа карты) — это ответ **нашего** контроллера, а не AGVControl. `stand-run.md` поясняет, что порт 5231 на стенде занят `vrp-api`, а `agv-control-api` остановлен (закрыт прошлый BLOCKER про порт).

## Замечания

- [MINOR] `src/VR.RoutePlanner.Infrastructure/Mqtt/VdaOrderBuilder.cs:189-221` — виртуальный старт реализован только в `VdaOrderBuilder`, а сервис вызывает его исключительно с `OrderUpdateId: 0` (`RobotOrderService.cs:289`); путь обновлений идёт через `VdaOrderUpdater.BuildUpdate` (`RobotOrderService.cs:521`), который виртуальный старт не добавляет. Это согласуется с выжимкой п.9 (обновление начинается с узла-якоря `lastNodeSequenceId` и сохраняет исходные `sequenceId`), поэтому функционального дефекта нет, но ребро п.6 в проде достигается только тестами, а не потоком обновлений.
- [MINOR] `doc/agents/2026-11-23-stage2.5-vda-port/evidence/stand-run.md:3` — файл фиксирует HEAD `2333202`, тогда как финальный коммит `5d7df3d`. Дельту составляют только виртуальный старт (`orderUpdateId > 0`) и формулы `Front`/`Rear`, которые не влияют на режим `ReleasedNodes = 0`, проверенный в прогоне, поэтому доказательство остаётся валидным.
- [MINOR] `evidence/order-json.json`, `evidence/stand-run.md` — имена отличаются от перечня спеки §10 (`order-chunked.json`, `stand-run.txt`); содержимое эквивалентно требуемому (опубликованный заказ целиком + описание прогона до `Completed`).
- [MINOR] `src/VR.RoutePlanner.Application/Robots/RobotOrderService.cs:705-717` — `TriggerForkAsync` использует 7-параметрический формат instant-action из `ExecutePickDropAsync` AGV (`containerTypeId="0"`, `autoReturnNode=false`, без `adjustWithoutOA`), а не 8 параметров §2.3. Это корректный перенос эталона, дефект `agv.PositionX ?? 0 + distance*cos` действительно не перенесён (здесь `snapshot.PositionX + distance*cos`). Формулировка спеки §1 «параметры как в разделе 2.3» неточна для instant-action — уточнить текст спеки, код не править.

## Не покрыто проверкой

- Режим `ReleasedNodes > 0` (чанки + обновления + `newBaseRequest`) на стенде не используется (основной режим `<= 0`); корректность держится только на юнит-тестах `OrderChunkingTests`/`OrderUpdateTests` (зелёные). Виртуальный старт из §2.5 в проде достижим только через этот необкатанный путь — подтверждено тестами, а не живым прогоном.
- Доступ к стенду (`192.168.89.131:5231`, `rms_vrp_test`) у контролёра отсутствует; живой доезд подтверждён файлами-доказательствами владельца, числа в которых сходятся с кодом и контрактами (см. выше).
