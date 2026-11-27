# Поправка 2: плечи подряд, действия вил на узлах-якорях

Дата: 2026-11-24. Поправка к `spec-order-updates.md` (реализована в `37b8d07`, ветка
`ai/stage2.5-vda-port-step3`, репозиторий `C:\Users\User\source\repos\VR.RoutePlanner`).
Меняет Д-4, Д-5, Д-7 и уточняет Д-3. Д-1, Д-2, Д-6, Д-8, Д-9 остаются в силе.

## Цель

Робот должен принимать второе и последующие сообщения того же `orderId`: первое сообщение задачи
везёт только движение (без действий), действие вил уходит в сообщении, которое начинается на своём
узле-якоре по раскладке `ref-rev-leg2-pick-drop.json`, а плечи публикуются подряд без ожидания
подтверждения предыдущего.

## Факт стенда 131 (задача `ORD_00201`, `pick wo-2-7 -> drop wo-1-6`)

- Один `orderId`, `orderUpdateId` 0 и 1, `instantActions` пусто, журнал публикаций есть.
- update 0 принят и выполнен (доехал до `wo-2-7`, `pick` FINISHED, стоит на точке остановки).
- update 1 отвергнут роботом: `state: errors[0].errorDescription = "AGV not valid continuation of
  the currently still running order"`, заказ `Failed` / `UPDATE_NOT_ACCEPTED` по
  `UpdateAcceptTimeoutSeconds`.
- Наш update 1 (`RobotOrderService.cs:550-551` + `VdaOrderBuilder.cs:299-310`) начинался узлом
  `wo-2-7` с координатами **фактической позы** робота (11.1104, 29.0106), с `theta`, допусками
  0.3/3.14, без действия и без виртуального ребра (`NeedsVirtualStart` возвращает `false` при
  `FirstNodeIsPose`, `VdaOrderBuilder.cs:195-201`). Координаты последнего узла update 0 были
  координатами карты (11.185, 29.05) — узел «не тот же», продолжение заказа невалидно.

## Что делает эталон

- `ref-rev-leg1-to-pick.json`, `orderUpdateId` 0: 7 узлов, **ни одного действия**; первый узел
  `pa1` — поза робота (14.2313/35.3590, `theta` 0.0096, 0.3/3.14); последний `wo-1-6` — координаты
  карты (12.695/31.26), `theta` нет, 0.1/0.1.
- `ref-rev-leg2-pick-drop.json`, `orderUpdateId` 2: seq 12 `wo-1-6` — координаты карты
  (12.695/31.26), `theta` нет, 0.3/3.14, действий нет; seq 13 ребро `VIRTUAL_START_wo-1-6`
  (start=end=`wo-1-6`, `rotationAllowed=false`); seq 14 `wo-1-6` — те же координаты карты,
  `theta` 0, 0.1/0.1, `pick` `containerX` 14.17 / `containerY` 31.26 / `containerTheta` 0; далее
  `wa178`, `wa163`, `wa177`, `wa62` и seq 24 `wo-2-7` (`theta` 3.1416, 0.1/0.1, `drop`
  `containerX` 9.71 / `containerY` 29.05 / `containerTheta` 3.1416).
- Оба сообщения в одну секунду: ожидания подтверждения предыдущего плеча нет.
- `ref-pick-drop-wo-2-7-wo-1-6.json` (`orderUpdateId` 1, робот уже стоял в `wo-2-7`): `pick` стоит
  прямо на первом узле-позе без дубля — робот принимает и такой вариант.

## Затронутые репозитории и проекты

- `VR.RoutePlanner`: `VR.RoutePlanner.Infrastructure` (`Mqtt/VdaOrderBuilder.cs`,
  `Kafka/SimpleTaskWorker.cs`), `VR.RoutePlanner.Application` (`Robots/RobotOrderService.cs`,
  `Robots/VdaOrderRequest.cs`), тесты `VR.RoutePlanner.Tests.Unit`.

## Решения

### П-1. Плечи публикуются подряд (отменяет Д-5 целиком)

Гейт `IsLegFinished` (`RobotOrderService.cs:469-476`), константа `ReasonLegNotFinished`
(`:39-40`), метод `IsLegFinishedAsync` и цикл ожидания `AppendLegWhenFinishedAsync`
(`SimpleTaskWorker.cs:184-225`) удаляются. `PublishLegsAsync` (`:150-175`) вызывает
`CreateTaskOrderAsync` для первого действия и `AppendLegAsync` для каждого следующего **без
проверки `state`**; отказ по незавершённому плечу больше не существует.

Причина: робот исполняет действия по узлам, а не по факту прихода следующего сообщения. Ожидание не
даёт ничего (подтверждение приходит уже после того, как робот поехал) и ломает сценарий из двух
действий. Порядок публикации внутри плеча при `Routing:Order:ReleasedNodes = 3`
(`OrderOptions.cs:12`) сохраняется: плечо идёт чанками по мере продвижения, **следующее плечо
публикуется сразу после последнего чанка предыдущего**. Требование «в ту же секунду, что в
эталоне» не ставится; запрещено именно ожидание завершения плеча по `state`.

### П-2. Первое сообщение задачи — без действий (пересмотр Д-4)

Правило «действие вил только в последнем узле плана сообщения» (`VdaOrderBuilder.cs:254,
324-329`) заменяется на «действие вил стоит в узле, где оно выполняется, и ровно один раз за
задачу».

Сообщение 0 задачи действий не содержит никогда. Действие вил уходит в сообщении, которое
**начинается** на узле-якоре, и раскладка этого сообщения — как `ref-rev-leg2-pick-drop.json`:

1. узел-якорь: `nodeId` узла карты, координаты карты, **без** `theta`, допуски 0.3/3.14,
   действий нет;
2. ребро `VIRTUAL_START_<nodeId>`: `startNodeId = endNodeId = узел-якорь`, `rotationAllowed =
   false`, `orientation`/`orientationType`/`trajectory` не задаются (`VdaOrderBuilder.cs:473-491`);
3. дубль узла-якоря: тот же `nodeId`, координаты карты, `theta` = курс к паллете, допуски
   0.1/0.1, действие вил с `containerX/containerY/containerTheta` по правилам D1 из `spec.md`;
4. маршрут до следующей цели; последний узел — если на нём выполняется действие — получает
   `theta` курса к паллете, допуски 0.1/0.1 и это действие.

Правило привязки действий в терминах `SimpleTaskWorker` (`:152`, `hasLoad = action.ActionType !=
PickUp`):

- плечо с `hasLoad = false` (едем порожним к узлу `pick`) действий не несёт ни в начале, ни в
  конце;
- плечо с `hasLoad = true` (едем с грузом к узлу `drop`) несёт `pick` на узле-якоре (пункты 1-3) и
  `drop` в своём последнем узле;
- `AppendLegAsync` получает явный тип действия якоря (`pick`) и тип действия в конце (`drop`):
  выводить тип из `HasLoad`/`NodeType` внутри `BuildForkAction` (`VdaOrderBuilder.cs:495-512`)
  недостаточно — на узле-якоре нужен `pick`, а `HasLoad = true` даёт `drop`;
- крайний случай «задача заканчивается `pick`» (после него нет гружёного плеча): действие
  ставится в последнем узле его плеча, без дубля и виртуального ребра.

`TerminalActionType` (`OrderOptions.cs:42`) по-прежнему только в последнем плече задачи.

### П-3. Первый узел сообщения (уточняет Д-3)

- Сообщение 0 задачи: первый узел — поза робота, `x/y` = фактические `RobotX/RobotY`, `theta` при
  известном курсе (`FirstNodeTheta`, `OrderOptions.cs:48`), допуски 0.3/3.14. Текущее поведение
  (`VdaOrderBuilder.cs:299-310`) сохраняется.
- Сообщение N > 0 (`AppendLegAsync`): первый узел — **узел карты**, `x/y` берутся из карты (не из
  позы робота, не из точки остановки), `theta` не задаётся, допуски 0.3/3.14. Значения `x/y`
  должны совпадать с координатами, которые были в последнем узле предыдущего сообщения: это и есть
  проверка «valid continuation» на стороне робота.
- Флаг `FirstNodeIsPose` (`VdaOrderRequest.cs:62-63`) означает только «первый узел — поза
  робота» и передаётся только сообщению 0. Для сообщений N > 0 раскладка первого узла задаётся
  отдельным признаком (`FirstNodeIsPose = false` + широкие допуски), координаты — карта.

### П-4. Стационарный случай (пересмотр Д-7)

Д-7 сводится к П-2: если робот уже стоит в узле-якоре, порожнее плечо к нему не публикуется
(движения нет), а сообщение с действием всё равно собирается по раскладке пунктов 1-4 П-2
(«поза — ребро — дубль с действием — маршрут»). Отдельная ветка `StationaryPlan`
(`VdaOrderBuilder.cs:236-237`) расширяется до общего случая «первое сообщение начинается на
узле-якоре, за дублем идёт маршрут», а не только до «в плане два одинаковых узла».
`pick` и `drop` в одном узле (робот стоит на стеллаже) остаются поддержанными.

### П-5. Стартовый узел плеча — узел карты, а не поза робота

`AppendLegCoreAsync` строит маршрут не от `RobotStartNodeResolver.Resolve(graph, snapshot)`
(`RobotOrderService.cs:494`), а от последнего узла текущего плана заказа
(`RobotOrderService.cs:575` хранит полный план плеча), то есть от цели предыдущего плеча. Поза
робота в этот момент ещё не достигнута — при публикации подряд её брать нельзя.
`RobotStartNodeResolver` остаётся для сообщения 0 (`CreateTaskOrderAsync`). Проверка «робот уже в
цели» (`:501-513`) считается по паре узел-карты/узел-карты.

### П-6. Что не меняется

- Д-6: `Completed` только при `Executing && !Driving && !HasRunningAction && reachedLast &&
  LastNodeId == order.TargetNodeId` (`RobotOrderService.cs:670-684`).
- Д-8: `cancelOrder` в автоматическом потоке не отправляется.
- Д-9: таймаут `UpdateAcceptTimeoutSeconds` (`OrderOptions.cs:24`) остаётся, но отсчёт ведётся от
  **последней** публикации: `RememberUpdate` хранит только актуальный `(orderId, updateId,
  момент)`, иначе при двух подряд опубликованных сообщениях таймаут срабатывает по устаревшей
  версии.
- Логирование каждой публикации (`orderId`, `orderUpdateId`, диапазон `sequenceId`, цель плеча)
  сохраняется, как и отсутствие миграций.

## Критерии приёмки

Юнит-тесты (`VR.RoutePlanner.Tests.Unit`):

1. Сообщение 0 задачи не содержит ни одного элемента `actions` ни в одном узле (тест на
   `VdaOrderBuilder` и на `CreateTaskOrderAsync`).
2. Сообщение N > 0, начинающееся на узле-якоре, содержит ровно три первых элемента: узел-позу
   (координаты карты, `theta` нет, 0.3/3.14), ребро `VIRTUAL_START_<node>` (`startNodeId =
   endNodeId`, `rotationAllowed = false`, без `orientation`), дубль узла (`theta` курса к паллете,
   0.1/0.1, действие вил); за дублем идёт маршрут.
3. `VdaOrderBuilder`: первый узел сообщения 0 берёт `RobotX/RobotY` и `theta`, первый узел
   сообщения N > 0 берёт координаты карты и `theta` не ставит; в обоих случаях 0.3/3.14.
4. `AppendLegAsync`: публикует без чтения `state` (при `state`, где робот `Driving` и
   `LastNodeId` — старт, сообщение всё равно уходит); второй `orderUpdateId` = 1, тот же
   `orderId`, база нумерации = `sequenceId` последнего узла первого сообщения, маршрут строится от
   цели предыдущего плеча.
5. `AppendLegAsync` с `hasLoad = true` ставит `pick` на узле-якоре и `drop` в последнем узле;
   с `hasLoad = false` — действий нет.
6. Задача из двух действий: два сообщения — 0 без действий (движение до `pick`-узла), 1 с
   `pick` на якоре + маршрут + `drop`; `IRobotOrderNumberSource.NextAsync` вызван один раз.
7. Стационарный случай: робот уже в узле-якоре — порожнее плечо не публикуется, сообщение с
   действием собрано по П-2.
8. `CheckTimeoutsAsync` считает таймаут от последней публикации; при неподтверждённом последнем
   update — `Failed` / `UPDATE_NOT_ACCEPTED`, резерв освобождён, `cancelOrder` не отправлен.
9. Переходы статусов и завершение (`Completed` только при `LastNodeId == TargetNodeId`) —
   поведение из Д-6 сохраняется, существующие тесты зелёные.
10. Миграции не менялись (`MigrationScriptTests`, `ModelSchemaTests`, `MigrationApplyTests`
    зелёные), схемы `vrt_RobotOrder`/`vrt_RobotOrderNode` без диффа.

Сравнение с эталоном (объединение сообщений задачи, скрипт/тест `stand-upd-diff.txt`): порядок и
типы узлов, глобальная нумерация `sequenceId` (12 → 12, 14, 16, … 24), `theta` только у узлов с
действием, `allowedDeviationXY/Theta`, `actions[].actionType/blockingType/actionParameters`
(`containerTypeId`, `containerX`, `containerY`, `containerTheta`), `edgeId/startNodeId/endNodeId/
orientation/orientationType`, наличие `VIRTUAL_START_<node>` с `rotationAllowed = false`,
отсутствие `maxSpeed`/`maxRotationSpeed`, действие вил не стоит в узлах отправления без якоря.

## Границы

- Менять можно только `VR.RoutePlanner`. `AGVControl`, чужие репозитории и файлы эталона
  (`ref-*.json`, `reverse-start*.md`) — не трогать.
- Не менять: миграции и схемы `vrt_RobotOrder`/`vrt_RobotOrderNode`, таблицы
  `rms."Agvs"`/`public."Agvs"`, `AStarPathPlanner`, `TrafficCoordinator`, HTTP-контракты
  `AgvController`/`RouteController`, решения D1-D5 из `spec.md`, значения по умолчанию
  `Routing:Order` (включая `ReleasedNodes = 3`, `TerminalActionType = "detectObject"`).
- Новых параметров конфигурации не добавлять; удалять можно только `ReasonLegNotFinished` и
  внутренние методы ожидания.

## Риски и ловушки

- **`blockingType: SOFT`.** Робот формально может покинуть узел с незавершённым действием.
  Эталон публикует так же, живой прогон показал `pick` FINISHED при стоящем роботе; риск принят
  пользователем, страховки в коде нет.
- **`ReleasedNodes = 3`.** Плечо уходит 2-3 сообщениями, следующее плечо — после последнего
  чанка. Сравнивать с эталоном надо по объединению сообщений, а не по времени публикации.
- **Свежесть `state`.** `RobotOrderService.cs:631-634` игнорирует `state` со старым
  `orderUpdateId`; при публикации подряд принятие заказа видно только со `state` текущей версии.
  Единственная страховка — таймаут Д-9, он должен считать от последней публикации (П-6).
- **`PlanJson` хранит полный план плеча** (`RobotOrderService.cs:575`), поэтому цель прошлого
  плеча берётся как последний узел плана. Если чанк попадёт в `PlanJson` вместо полного плана,
  стартовый узел следующего плеча будет неверным.
- **Чанк не должен разрывать тройку** «поза — виртуальное ребро — дубль»: минимальный размер
  чанка при такой раскладке — 3 узла.
- **Узел-якорь и его дубль имеют одинаковый `nodeId`, но разные `sequenceId`.** Слияние `state`
  (`MergeNodes`) должно различать их по `(nodeId, sequenceId)`.
- **Координаты рабочего узла.** Эталон держит в `nodePosition` координаты карты (12.695/31.26), а
  смещение 1.475 м к паллете — только в `containerX/Y`. Если наша сборка кладёт в `nodePosition`
  точку остановки, это видно в сравнении п.5 и должно быть приведено к эталону.
- **Резерв трафика** живёт на `PlanJson` (`TrafficCoordinator.OnStateAsync`): при публикации двух
  плеч подряд горизонт пересобирается дважды за секунду, ручного освобождения нет.
- Кодировка CP1251 в части файлов репозитория — правки не должны её ломать.

## План проверки

```
dotnet build VR.RoutePlanner.sln -c Release
scripts/test.ps1
git -C <repo> diff --stat 37b8d07..HEAD
git -C <repo> diff 37b8d07..HEAD -- src/VR.RoutePlanner.Application/Robots/RobotOrderService.cs
git -C <repo> diff 37b8d07..HEAD -- src/VR.RoutePlanner.Infrastructure/Mqtt/VdaOrderBuilder.cs
git -C <repo> diff 37b8d07..HEAD -- src/VR.RoutePlanner.Infrastructure/Kafka/SimpleTaskWorker.cs
grep -rn "LEG_NOT_FINISHED\|AppendLegWhenFinishedAsync\|IsLegFinished" src tests   # пусто
grep -n "cancelOrder" src/VR.RoutePlanner.Application/Robots/RobotOrderService.cs   # только HasFinishedCancelAction
git -C <repo> status   # нет новых файлов в src/VR.RoutePlanner.Infrastructure/Migrations
```

Живой прогон (владелец, стенд 131) в `doc/agents/2026-11-24-agv-robot-control/evidence/`:
`stand2-<orderId>-u0.json`, `stand2-<orderId>-u1.json` (все сообщения задачи), `stand2-db.txt`,
`stand2-kafka.log`, `stand2-diff.txt`.

## Приёмочная выжимка

1. Нет ожидания подтверждения плеча: удалены `ReasonLegNotFinished` (`RobotOrderService.cs:39-40`),
   гейт `IsLegFinished` (`:469-476`), `IsLegFinishedAsync`, `AppendLegWhenFinishedAsync`
   (`SimpleTaskWorker.cs:184-225`); `grep -rn "LEG_NOT_FINISHED\|IsLegFinished" src tests` — пусто.
2. `AppendLegCoreAsync` строит маршрут от последнего узла плана заказа, а не от позы робота
   (было `RobotOrderService.cs:494`), и публикует без чтения `state`.
3. Сообщение 0 задачи не содержит действий ни в одном узле (было: действие в последнем узле,
   `VdaOrderBuilder.cs:324-329`).
4. Сообщение N > 0, начинающееся на узле-якоре, содержит подряд: узел-позу (координаты карты,
   без `theta`, 0.3/3.14) → ребро `VIRTUAL_START_<nodeId>` (`startNodeId = endNodeId`,
   `rotationAllowed = false`, без `orientation`) → дубль узла (`theta` к паллете, 0.1/0.1,
   действие вил) → маршрут. Проверяется тестом сборки и диффом `VdaOrderBuilder.cs`.
5. Первый узел сообщения N > 0 берёт `x/y` из карты, `theta` не ставит, 0.3/3.14; первый узел
   сообщения 0 берёт `RobotX/RobotY` + `theta`, 0.3/3.14. Остальные узлы — 0.1/0.1.
6. Плечо с `hasLoad = true` несёт `pick` на узле-якоре и `drop` в последнем узле; с
   `hasLoad = false` — действий нет. Тип действия якоря передаётся явно, не выводится из
   `BuildForkAction` (`VdaOrderBuilder.cs:495-512`).
7. Тесты: п.1-9 тела спеки, в том числе «задача из двух действий = 2 сообщения (движение без
   действий; `pick` на якоре + маршрут + `drop`)», отсутствие отказа по незавершённому плечу,
   стационарный случай без публикации порожнего плеча, таймаут от последней публикации.
8. Удалены/переписаны тесты `AppendLeg_RobotDriving_RejectsWithoutPublishing` и
   `AppendLeg_RobotNotAtLegTarget_RejectsWithoutPublishing`
   (`tests/VR.RoutePlanner.Tests.Unit/Robots/TaskOrderLegTests.cs:290,305`).
9. Д-6 (`Completed` только при `LastNodeId == TargetNodeId`), Д-8 (нет `cancelOrder`), Д-9
   (`UPDATE_NOT_ACCEPTED`, отсчёт от последней публикации), логирование публикаций — на месте.
10. Нет миграций, нет диффа в `AStarPathPlanner`, `TrafficCoordinator`, `AgvController`,
    `RouteController`, `OrderOptions` (кроме удаления константы причины), файлы эталона не
    изменены.
11. `dotnet build VR.RoutePlanner.sln -c Release` — 0 предупреждений; `scripts/test.ps1` —
    Unit/Integration/Contracts зелёные, пропусков 0.
12. Стенд 131 (файлы в `doc/agents/2026-11-24-agv-robot-control/evidence/`, иначе пункт
    непроверен и вердикт `BLOCK` на живую часть): `stand2-<orderId>-u0.json` без действий;
    `stand2-<orderId>-u1.json` с тройкой «поза — `VIRTUAL_START_` — дубль с `pick`» и `drop` в
    конце; один `orderId`, `orderUpdateId` строго возрастает; `stand2-db.txt` — одна строка
    `vrt_RobotOrder` со `Status = Completed`, `FailureReason` пуст; `stand2-kafka.log` — `Running`
    и один `Finished`; `stand2-diff.txt` — совпадение с `ref-rev-leg1-to-pick.json` +
    `ref-rev-leg2-pick-drop.json` по полям из раздела «Сравнение с эталоном».
