# Старт WMS-задачи, стартовый узел и поза AGV

## 1. Вопрос
Как AGVControl начинает WMS-задачу, выбирает старт маршрута и обрабатывает рабочую позу без автоматической коррекции позиции и рестарта симулятора?

## 2. Точки входа
- `TaskWorker.ProcessTasksAsync` проверяет/назначает AGV и вызывает API обработки задачи (`AGVFleetManager/Services/TaskWorker.cs:453-470`).
- `AGVTaskService.ProcessAgvTasksAsync` передает задачу в `AGVOrderService.ExecuteTaskAsync` (`AGVControlApi/Services/AGVTaskService.cs:147-173`).
- `AGVOrderService.ExecuteTaskAsync` выбирает путь для задачи; одна action ведет к `AgvOneTaskActionExecuteAsync` (`AGVControlApi/Services/AGVOrderService.cs:168-207`).
- Выбор стартового узла находится в `AgvTaskExecuteAsync` (`AGVControlApi/Services/AGVOrderService.cs:291-336`).
- Построение Node/Edge DTO и публикация заказа выполняются через `MakeNodesAndEdgesForOrderAsync` и `SendOrderToAgvInternalAsync` (`AGVControlApi/Data/OrderService.cs:1116-1195`, `2277-2314`).
- State и завершение обрабатывает `ProcessStateMessage` -> `HandleWithOrderAsync` -> `OrderCompletedAsync` (`AGVControlApi/Monitor/StateMonitorService.cs:638-689`, `800-872`, `1487-1515`).

## 3. Поток
1. `TaskWorker` извлекает созданную задачу, назначает AGV, валидирует маршрут и вызывает API обработки (`TaskWorker.cs:453-470`).
2. `AGVTaskService` формирует `TaskQueueItem` и вызывает `ExecuteTaskAsync` (`AGVTaskService.cs:147-173`).
3. `ExecuteTaskAsync` отправляет WMS-событие о начале выполнения, затем строит заказ по действиям задачи (`AGVOrderService.cs:182-207`).
4. Для составной задачи `AgvTaskExecuteAsync` находит узел первой PICK; `agv.LastNodeId` используется, если позиция AGV не дальше 3 м от этого узла, иначе выбирается ближайший узел по текущим XY (`AGVOrderService.cs:273-309`; `NodeService.cs:33-39`).
5. Координаты текущей позы добавляются в описание текущей точки; если AGV не в выбранном узле, отправляется команда доезда к нему (`AGVOrderService.cs:311-330`).
6. Для отправки заказа строятся узлы/ребра; у первого узла повышаются допуски, затем X/Y/theta заменяются фактическими полями AGV (`OrderService.cs:2285-2309`). Заказ публикуется через выбранный VDA adapter после резервирования пути (проверка MQTT подтверждения в этом разборе не выполнялась).
7. State сохраняет позицию и `LastNodeId`; при пустых node/edge states заказ отмечается завершенным и публикуется событие WMS в Kafka (`StateMonitorService.cs:683-688`, `859-865`, `1487-1515`; `AgvService.cs:677-684`).

## 4. Поля и значения
| Поле | Значение/правило | Где задано | Зачем |
|---|---|---|---|
| `agv.LastNodeId` | Предпочтителен, когда AGV в пределах `max(3 м, node.AllowedDeviationXY)` | `AGVOrderService.cs:293-299`; `NodeService.cs:33-39` | Определить известную стартовую вершину |
| `nearest_node` | Ближайший узел карты по `PositionX/Y`, когда `LastNodeId` не прошел проверку | `AGVOrderService.cs:296-309` | Запасной старт маршрута |
| `agvStartPos` | По умолчанию `true` | `OrderService.cs:2277-2279` | Выбрать фактическую позу для первого NodePosition |
| Первый `NodePosition.X/Y/Theta` | `agv.PositionX/Y/Theta`, при наличии значений | `OrderService.cs:2297-2309` | Начальная позиция заказа совпадает с последней сохраненной позой AGV |
| Первый допуск XY | Не меньше `0.3` | `OrderService.cs:2301-2308` | Разрешить старт вне точной координаты вершины |
| Первый допуск theta | Не меньше `3.14` радиан | `OrderService.cs:2301-2308` | Разрешить старт при большой разнице курса |
| Рабочая поза | `PositionBeforeWorkingNode` берет единственный входящий узел; иначе predecessor из NodeSequence; затем `AgvStopPositionXY` | `OrderService.cs:1145-1159`, `1392-1432`; `Node.cs:182-201` | Рассчитать точку остановки перед рабочей точкой |
| `WrkLength` | `((Pallet.Length + Pallet.AgvLength) / 2 + Pallet.Span)` | `Node.cs:102-103` | Смещение передней/задней точки для forklift |
| Рабочий action `containerX/Y` | Равны исходным `positionX/Y` узла; `containerTheta` равен рассчитанному входному theta | `OrderService.cs:1617-1626`, `1637-1651` | Параметры pick/drop |
| `LastNodeId` после state | Берется из `state.LastNodeId`, только если в state есть `AgvPosition` | `AgvService.cs:677-684` | Сохранить последнюю reported вершину и позу |

## 5. Отличия и дефекты
- Узел маршрута и координата первого NodePosition не одно и то же: выбранный узел может быть nearest node, но первый отправляемый X/Y/theta подменяются позой AGV (`AGVOrderService.cs:291-309`; `OrderService.cs:2297-2309`).
- Проверка `IsAgvInNode` использует XY-дистанцию и допуск, не проверяет ориентацию (`NodeService.cs:33-39`).
- Обновление `LastNodeId` привязано к наличию `AgvPosition`; state без позиции не обновляет это поле в просмотренном методе (`AgvService.cs:677-684`).
- Не найден вызов `InitPosition` из потока создания/отправки заказа. Реализация initPosition доступна отдельным методом instantActions, который маппит позицию и публикует сообщение (`AgvInstantActionsService.cs:242-271`).
- Поиск по C# исходникам AGVControl не выявил механизма перезапуска симулятора; найденные совпадения `restart` были текстовыми рекомендациями, не вызовами рестарта (`ErrorService.cs:1121-1125`). Иные языки/внешние процессы не проверялись.
- Численное значение `WrkLength` для конкретной машины не вычислено: значения `Pallet.Length`, `Pallet.AgvLength`, `Pallet.Span` в этой проверке не установлены (`Node.cs:102-103`).
- В `HandleWithOrderAsync` есть ранний вызов `OrderCompletedAsync` при пустых node/edge states до проверки `order == null`; отдельно действие может завершить заказ, когда активных pick/drop больше нет (`StateMonitorService.cs:803-812`, `885-895`).

## 6. Что повторить у нас
- Стартовый узел выбирать по `LastNodeId` только при XY-дистанции в заданном допуске; иначе вычислять ближайший узел по актуальной позе (`AGVOrderService.cs:293-309`; `NodeService.cs:33-39`).
- Разделять идентификатор стартового узла и стартовую позу NodePosition: первая точка заказа может использовать текущие X/Y/theta робота (`OrderService.cs:2297-2309`).
- Не считать широкие стартовые допуски коррекцией позы: код меняет данные заказа, но в проверенном потоке не посылает initPosition (`OrderService.cs:2301-2309`; `AgvInstantActionsService.cs:242-271`).
- Для рабочих точек повторить выбор входной геометрии и расчет точки остановки согласно типу AGV; значения паллеты/вил брать из проверенных данных машины, не из фиксированной константы (`OrderService.cs:1145-1159`; `Node.cs:102-103`, `182-201`).
- Обновлять сохраненные XY/theta/LastNodeId из state и проверять, что источник state действительно содержит позицию (`AgvService.cs:677-684`).
- Не добавлять автоматический рестарт симулятора как предположение: подтвержденного вызова в просмотренном C# коде нет (поиск `restart` в `AGVControl/**/*.cs`, результат в `ErrorService.cs:1121-1125`).

## 7. Что проверить на стенде
- Снять state перед задачей и сравнить `LastNodeId`, AGV XY и дистанцию до узла; проверить границу допуска 3 м и `AllowedDeviationXY` (`AGVOrderService.cs:293-309`; `NodeService.cs:33-39`).
- Сопоставить выбранный стартовый NodeId с первым NodePosition в MQTT order: XY/theta должны отражать поля AGV при `agvStartPos=true` (`OrderService.cs:2277-2279`, `2297-2309`).
- Сверить outgoing `allowedDeviationXY/theta` с минимумами 0.3 и 3.14 и измерить, выполняет ли конкретный робот старт при смещенной позе (`OrderService.cs:2301-2308`).
- Для PICK/DROP снять рабочую точку, входящее/предыдущее ребро, `theta`, NodePosition и `containerX/Y/Theta`; измерить физический стоп относительно паллеты (`OrderService.cs:1145-1159`, `1392-1432`, `1617-1651`).
- Проверить `Pallet.Length`, `Pallet.AgvLength`, `Pallet.Span` для конкретной машины и вычислить фактический `WrkLength` (`Node.cs:102-103`).
- После движения сравнить поля `state.LastNodeId` и `state.AgvPosition` с БД; отдельно отправить тестовый state без `AgvPosition` и установить, остается ли `LastNodeId` прежним (`AgvService.cs:677-684`).
- Проверить завершение: пустые node/edge states, статусы actions, статус заказа в БД и Kafka-событие WMS (`StateMonitorService.cs:800-812`, `859-895`, `1487-1515`).
- На стенде проверить отсутствие автоматического reposition/restart по MQTT/процессам симулятора. Стенд не проверен.
