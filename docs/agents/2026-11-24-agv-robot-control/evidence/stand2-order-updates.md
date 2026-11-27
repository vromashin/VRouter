# Живой прогон после поправки П-1..П-6: один orderId, плечи подряд

Дата: 2026-11-24. Ветка `ai/stage2.5-vda-port-step3`, коммит `d1d92c4`. Стенд 131,
симулятор `Tuskrobots/98001`, карта 801. Симулятор сброшен выровненным состоянием
(`/root/RobotsSimulatorStates.ini.bak-1795369289`), робот стартовал из `wa57`.

## Задача

WMS-задача `pick wo-2-7 -> drop wo-1-6`, действия `actionType 0` и `1`.
Заказ `ORD_00202`, оба действия выполнены под одним `orderId`.

## Сообщения в MQTT

`ordеr` опубликован двумя сообщениями с разницей 126 мс.

### Update 0 (15:55:53.858)

```
seq=0   wa57    xy=11.3899,24.5166 theta=-1.5701 dev=0.3/3.14 acts=[]
seq=2   wa58    xy=11.3900,25.2900 theta=absent  dev=0.1/0.1
seq=4   wa59    xy=11.3900,26.3000
seq=6   wa60    xy=11.3900,27.1700
seq=8   wa61    xy=11.3900,28.1900
seq=10  wa62    xy=11.3900,29.0500
seq=12  wo-2-7  xy=11.1850,29.0500 theta=absent  dev=0.1/0.1 acts=[]
edge 1  801-wa57-wa58   ori=pi
edge 3..11              ori=0
```

Действий нет, последний узел плеча в координатах карты без курса. Совпадает по правилу
с `ref-rev-leg1-to-pick.json`.

### Update 1 (15:55:53.984)

```
seq=12  wo-2-7  xy=11.1850,29.0500 theta=absent  dev=0.3/3.14 acts=[]
seq=14  wo-2-7  xy=11.1850,29.0500 theta=pi      dev=0.1/0.1  acts=[pick]
seq=16  wa62    xy=11.3900,29.0500
seq=18  wa63    xy=11.3900,30.0800
seq=20  wa163   xy=12.6400,30.2300
seq=22  wa178   xy=12.6400,31.2600
seq=24  wo-1-6  xy=12.6950,31.2600 theta=0       dev=0.1/0.1  acts=[drop]
edge 13 VIRTUAL_START_wo-2-7  start=end=wo-2-7  rotationAllowed=false
edge 15 801-wo-2-7-wa62  ori=pi
edge 17..23              ori=0
```

Тройка «поза карты без курса, виртуальное ребро, дубль с курсом к паллете и действием»
совпадает с `ref-rev-leg2-pick-drop.json` (seq 12, 13, 14). Нумерация продолжает
предыдущее сообщение (12 -> 12..24), как в эталоне.

## Результат

- `vrt_RobotOrder`: `ORD_00202`, статус `Completed`, `OrderUpdateId = 1`,
  `TargetNodeId = wo-1-6`, `FailureReason` пуст, завершён 18:56:52.
- `instantActions` за прогон: пусто, `cancelOrder` не отправлялся.
- Состояние робота: `orderId = ORD_00202`, `orderUpdateId = 1`, `driving = false`,
  `lastNodeId = wo-1-6`, `lastNodeSequenceId = 24`, `errors = []`,
  `actionStates = [drop FINISHED, pick FINISHED]`.
- Робот физически стоит в `wo-1-6` (12.6439, 31.1781).
- WMS-события по задаче `f71d6fa3-a949-4434-8851-2f36aaf89f93`: `Running`, затем `Finished`,
  события `Failed` нет.

То есть оба действия вил выполнились в одном заказе, отказ
`AGV not valid continuation of the currently still running order` больше не возникает.

## Сравнение с прошлым прогоном

| Что | ORD_00201 (до поправки) | ORD_00202 (после) |
|---|---|---|
| orderId на задачу | один | один |
| update 0 с действием | да, `pick` в конце | нет, только движение |
| update 1 первый узел | поза робота 11.1104,29.0106, с `theta` | узел карты 11.1850,29.0500, без `theta`, 0.3/3.14 |
| виртуальное ребро | нет | `VIRTUAL_START_wo-2-7` |
| действие на якоре | нет | `pick` в дубле узла |
| публикация | через 16 с после подтверждения | через 0.13 с, подряд |
| итог | `Failed / UPDATE_NOT_ACCEPTED` | `Completed` |

## Ограничения проверки

- Симулятор не меняет `HasLoad` после `pick` (`Agvs.HasLoad = false`), поэтому захват
  груза подтверждён только по `actionStates: pick FINISHED`, физически не проверен.
- Промежуточный маршрут разошёлся с эталоном (`wa63` вместо `wa177`), это выбор A*,
  оба пути допустимы.
- Проверен один маршрут. Многоплечевые цепочки (pick, drop, pick, drop) не проверялись.
