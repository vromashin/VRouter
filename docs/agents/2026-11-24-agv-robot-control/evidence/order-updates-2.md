# Поправка 2 к order update: раскладка узла-якоря (VR.RoutePlanner)

Ветка `ai/stage2.5-vda-port-step3`, база `37b8d07`. Реализация П-1..П-6 из
`spec-order-updates-2.md`. Ниже — что проверено локально и что осталось на стенд.

## Что сделано по пунктам

- **П-1.** Ожидание подтверждения плеча убрано: удалены `RobotOrderService.ReasonLegNotFinished`,
  `IsLegFinished`, `IsLegFinishedAsync`, `SimpleTaskWorker.AppendLegWhenFinishedAsync`.
  `AppendLegAsync` публикует сообщение, не глядя на `state` (`state` читается только для
  геометрии точки остановки). Вместо гейта по подтверждению роботом — ожидание
  `SimpleTaskWorker.WaitLegReleasedAsync`: следующий плечо уходит сразу после последнего
  отправленного чанка предыдущего (ожидание по `order.Nodes[].released`, не по `state`).
- **П-2.** Действие вил задается явным типом, а не выводится из типа узла/груза
  (`VdaOrderRequest.AnchorActionType` / `TargetActionType`). Сообщение 0 задачи действий не
  несет (`CreateTaskOrderAsync` получает `targetActionType = null` для промежуточного плеча).
  Действие ставится в сообщении, начинающемся на узле-якоре: узел-якорь (координаты карты,
  без `theta`, 0.3/3.14) + `VIRTUAL_START_<node>` (`start = end`, `rotationAllowed = false`,
  `orientation = null`) + дубль узла (`theta` к паллете, 0.1/0.1, действие) + маршрут до цели.
- **П-3.** Первый узел сообщения N > 0 — узел-якорь с координатами карты и без `theta`,
  допуски 0.3/3.14. `FirstNodeIsPose` передается только сообщением 0.
- **П-4.** `VdaOrderBuilder.StationaryPlan` обобщен в `AnchorPlan(anchor, route)`: пустой
  маршрут дает ровно два узла и одно ребро, непустой — тройку с продолжением маршрута.
- **П-5.** Стартовый узел плеча — `RobotOrderService.LegStartNode` (последний узел
  `PlanJson` заказа), `RobotStartNodeResolver` остался только запасным вариантом.
- **П-6.** Завершение по `TargetNodeId` и `UPDATE_NOT_ACCEPTED` от последней публикации не
  менялись; `cancelOrder` не отправляется.

## Форма сообщений (эталон → наша сборка)

| элемент | сообщение 0 | сообщение 1 (плечо) |
| --- | --- | --- |
| узел 0 | поза робота, `theta` при `FirstNodeTheta`, 0.3/3.14 | узел-якорь: координаты карты, без `theta`, 0.3/3.14 |
| ребро 0 | ребро маршрута | `VIRTUAL_START_<node>`, `start = end`, `rotationAllowed = false` |
| узел 1 | узел маршрута | дубль узла-якоря: `theta` к паллете, 0.1/0.1, действие вил |
| действия | нет ни в одном узле | действие якоря + действие цели в последнем узле |

Курс к паллете (`containerTheta`), координаты паллеты (`containerX/Y`), допуски,
отсутствие `maxSpeed`/`maxRotationSpeed`, `blockingType: SOFT` и параметры
`actionParameters` не менялись.

## Тесты (что именно закреплено)

`tests/VR.RoutePlanner.Tests.Unit/Robots`:

- `TaskOrderServiceTests.CreateTaskOrder_PublishesOneMessageWithoutActions_KeepsFinalTarget` —
  сообщение 0 без `actions` (критерий 1).
- `TaskOrderServiceTests.AppendLeg_StartsWithAnchorTripleAndRoute` — ровно три первых
  элемента: якорь без `theta`, `VIRTUAL_START_*`, дубль с `theta`/0.1/0.1/действием, за ним
  маршрут (критерий 2, 3).
- `TaskOrderServiceTests.AppendLeg_PublishesWithoutWaitingForConfirmation` — публикация при
  `driving = true` и `lastNodeId` = старт, маршрут от цели прошлого плеча (критерий 4).
- `TaskOrderServiceTests.AppendLeg_NoActionTypes_HasNoActions` — `hasLoad = true` не дает `drop`
  (критерий 5).
- `TaskOrderServiceTests.AppendLeg_RobotAlreadyInAnchor_PublishesAnchorMessageOnly` —
  стоянка в узле-якоре, сообщение по П-2 без маршрута (критерий 7).
- `SimpleTaskWorkerLegTests.PublishLegs_TwoActions_OneOrderTwoMessages` — два сообщения,
  `pick` на якоре, `drop` в цели, `NextAsync` вызван один раз (критерий 6).
- `VdaOrderReferenceTests.Build_MessageStartingAtAnchor_FirstNodeIsAnchorWithoutTheta`,
  `Build_StationaryAtWorkingNode_PoseNodeVirtualEdgeAndActionNode` — форма по эталону
  `ref-rev-leg2-pick-drop.json`.
- `VdaOrderNodeTests.Build_NoActionTypes_HasNoActionsAndNoTheta` — без действия вил нет ни
  действия, ни курса в рабочем узле.

## Локальные прогоны

```
dotnet build VR.RoutePlanner.sln -c Release
  Сборка успешно завершена. Предупреждений: 0  Ошибок: 0

dotnet test tests\VR.RoutePlanner.Tests.Unit -c Release
  Пройден! : не пройдено 0, пройдено 338, пропущено 0, всего 338
```

`scripts/test.ps1` (с интеграционными тестами на `rms_vrp_test`): результат — в конце файла,
см. `order-updates-2-ps1.txt`.

## Что проверить владельцу на стенде

1. Форму реальных MQTT-сообщений для задачи «pick `wo-1-6`, drop `wo-2-7`»: сравнить с
   `ref-rev-leg1-to-pick.json` (update 0, действий нет) и `ref-rev-leg2-pick-drop.json`
   (update 2, тройка якоря, `pick`, `drop` в `wo-2-7`). Доказательство локально:
   юнит-тесты `AppendLeg_StartsWithAnchorTripleAndRoute` и
   `PublishLegs_TwoActions_OneOrderTwoMessages`.
2. Приемку update 1 роботом: раньше отказ был `AGV not valid continuation of the currently
   still running order`; проверяется только живым прогоном.
3. Раскладку при `Routing:Order:ReleasedNodes = 3`: первое сообщение плеча кончается на
   тройке якоря (3 узла), остаток маршрута уходит следующим чанком по прогрессу — проверить,
   что `drop` приходит отдельным сообщением и робот его принимает.
