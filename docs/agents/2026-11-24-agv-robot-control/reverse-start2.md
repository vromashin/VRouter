# Прием следующего заказа после завершения

## 1. Вопрос
Как AGVControl отменяет/завершает заказы, нумерует orderUpdateId и отправляет последующие заказы той же или новой WMS-задачи?

## 2. Точки входа
- WMS dispatch: `TaskWorker.ProcessTasksAsync` -> `AGVTaskService.ProcessAgvTasksAsync` -> `AGVOrderService.ExecuteTaskAsync` (`AGVFleetManager/Services/TaskWorker.cs:453-470`; `AGVControlApi/Services/AGVTaskService.cs:147-178`; `AGVControlApi/Services/AGVOrderService.cs:168-226`).
- Новая публикация: `OrderService.SendOrderToAgvAsync` -> `SendOrderToAgvInternalAsync` (`AGVControlApi/Data/OrderService.cs:841-907`, `2277-2362`).
- Order update: `SendOrderUpdateToAgvAsync` -> `SendOrderUpdateInternalAsync` (`OrderService.cs:917-954`, `2381-2466`).
- State завершения: `HandleWithOrderAsync` / `HandleActionsAsync` -> `OrderCompletedAsync` (`AGVControlApi/Monitor/StateMonitorService.cs:800-872`, `875-971`, `1487-1515`).
- Ручная instantAction отмены: `AgvInstantActionsService.CancelOrderAsync` (`AGVControlApi/Services/AgvInstantActionsService.cs:189-213`).

## 3. Ответы по пунктам

### 1. CancelOrder перед новым заказом
- В основном пути публикации нового order вызова `CancelOrderAsync` или `CancelOrderOnAgvAsync` перед publish не найдено: `SendOrderToAgvAsync` проверяет статус/доступность, затем непосредственно вызывает `SendOrderToAgvInternalAsync` (`OrderService.cs:841-870`); internal метод строит order и публикует его в order topic (`OrderService.cs:2325-2333`).
- `AgvInstantActionsService.CancelOrderAsync` отправляет `adapter.CancelOrder(agv)` в instantActions topic, но только по отдельному вызову; условие `CurrentOrderId` пустой приводит к ошибке, непустой разрешает публикацию. Метод не задает `blockingType` сам (`AgvInstantActionsService.cs:189-204`). Для TUSK он делегирует базовому adapter (`AGVVda.Adapters/TUSK/TuskAdapter.cs:233-239`). Какое значение adapter реально сериализует для instantAction в этой конкретной сборке по доступному чтению не установлено: `VdaAdapterBase.cs` прочитан инструментом одной физической строкой с усеченным содержимым, точная реализация не проверена.
- Отдельный `MqttOrderService.CancelOrderAsync` строит `cancelOrder` с `BlockingType="HARD"`, `orderId`, `orderUpdateId`, публикует в instantActions (`AGVControlApi/Mappers/VDA5050Mapper.cs:95-108`; `AGVControlApi/Services/MqttOrderService.cs:186-208`). Это ручной/API метод, не вызываемый перед новой публикацией по найденной цепочке.
- В `OrderService.CancelOrderOnAgvAsync` adapter cancel публикуется в order topic и после успеха увеличивает сохраненный `OrderUpdateId` на 1 (`OrderService.cs:2485-2529`). Это не instantAction; вызов перед каждым новым заказом не найден.

### 2. Ожидание перед следующим заказом
- В `SendOrderToAgvAsync` после проверки доступности AGV нет ожидания Completed/state; переход к internal publish непосредственный (`OrderService.cs:851-870`). `MqttService.PublishAsync` ждет MQTT publish, затем делает `Task.Delay(100)`; это задержка после MQTT publish, не ожидание robot acceptance или завершения (`AGVControlApi/Services/MqttService.cs:125-153`).
- В `TaskWorker` есть задержки 1 с перед циклом и 1 с после dequeue; после успешной отправки есть 5 с. Это задержки очереди после запуска обработки, не подтверждение завершения предыдущего заказа (`AGVFleetManager/Services/TaskWorker.cs:424-426`, `450-483`).
- Проверки `state.orderId` очистки/исчезновения перед отправкой, ожидания пустых `nodeStates` перед новой публикацией и ожидания `OrderStatus.COMPLETED` в `AGVOrderService` не найдено. См. поиски ниже; единственный state-based polling вокруг выполнения заказа в найденной цепочке отсутствует.
- Найдены `_orderCompletionTimeoutSeconds` и конфигурация `OrderPolling:OrderCompletionTimeoutSeconds` с default 300, но поиск его использования показывает только поле и присваивание, не ожидание (`AGVControlApi/Services/AGVOrderService.cs:51-53`, `79`; поиск `_orderCompletionTimeoutSeconds` дает эти два совпадения).

### 3. Завершение предыдущего заказа и действия между заказами
- `HandleWithOrderAsync` ищет сохраненный order по `state_message.OrderId` и `OrderUpdateId`; если node/edge states пусты и нет незавершенных actions, возвращает IDLE и вызывает `OrderCompletedAsync` (`StateMonitorService.cs:800-812`). При пустых node/edge states order status также переводится в COMPLETED (`StateMonitorService.cs:859-865`).
- При завершенных pick/drop actions и отсутствии активных pick/drop actions `HandleActionsAsync` также ставит IDLE и вызывает `OrderCompletedAsync` (`StateMonitorService.cs:880-895`). `OrderCompletedAsync` публикует WMS Finished в Kafka и обновляет статус order в БД (`StateMonitorService.cs:1487-1515`).
- Между этим событием и отправкой следующей WMS-задачи специальной паузы/ожидания в коде `AGVOrderService` не найдено. State обновляет `agv.CurrentOrderId = stateData.OrderId` как прямое присваивание, без отдельной проверки завершения предыдущего order (`AGVControlApi/Services/AgvService.cs:830-839`).
- Если новый `state.orderId` отличается, state-tracker фиксирует изменение пары OrderId/OrderUpdateId/LastNodeId и сбрасывает waiting marker; это не блокирует публикацию нового order (`StateMonitorService.cs:1419-1442`).

### 4. orderUpdateId
- Новый `Order` получает аргумент `orderUpdateId`, default 0 (`AGVControlApi/Data/OrderService.cs:92-107`). В вызовах создания нового стандартного order default 0, но методы позволяют передать ненулевое значение (`OrderService.cs:1865-1883`). В `AgvTaskExecuteAsync` стартовая локальная переменная тоже 0 (`AGVControlApi/Services/AGVOrderService.cs:271-279`).
- TUSK `BuildOrderMessage` сериализует `order.OrderUpdateId`, не вычисляет счетчик сам (`AGVVda.Adapters/TUSK/TuskAdapter.cs:121-135`). В новом order publish success response возвращает `order.OrderUpdateId + 1` как следующий ожидаемый ID; это само по себе не меняет отправленный ID (`OrderService.cs:2355-2362`).
- Update существующего order формируется как `newUpdateId = order.OrderUpdateId + 1`, передается адаптеру и возвращается в response (`OrderService.cs:2433-2466`). В flow multi-action полученный response OrderUpdateId переносится в следующую итерацию (`AGVControlApi/Services/AGVOrderService.cs:358-365`, `467-470`).
- Обработка `state.OrderUpdateId > 0` завершает предыдущую версию с ID-1 (`StateMonitorService.cs:819-820`).

### 5. Если робот не подтвердил новый заказ
- Публикация считается успешной по результату MQTT publish reason code; ожидания state с новым `orderId` после нее в `SendOrderToAgvAsync` нет (`OrderService.cs:2331-2343`; `MqttService.cs:146-153`).
- При неуспешном MQTT publish заказ помечается FAILED, AGV переводится в ERROR (`OrderService.cs:890-905`). Это ошибка публикации, а не отсутствие robot acceptance.
- Повтор отправки из-за отсутствия state с новым `orderId` и повтор cancelOrder в основном пути не найдено. Таймаут приема заказа не найден.
- MQTT publish выполняется через вызов клиента без локального `CancellationToken`/явного timeout в методе; исключение логируется и дает `false` (`MqttService.cs:125-158`). `TaskWorker` HTTP timeout равен 10 минутам, это timeout HTTP вызова обработки задачи, а не протокольного подтверждения робота (`AGVFleetManager/Services/TaskWorker.cs:43`; вызов API `:405-409`).
- `Task.Delay(100)` после publish не является timeout acceptance (`MqttService.cs:146-153`). Значение 300 секунд в `_orderCompletionTimeoutSeconds` не используется найденным путем (`AGVOrderService.cs:51-53`, `79`).

### 6. state.orderId прошлого заказа
- При сохранении state `CurrentOrderId` присваивается `stateData.OrderId` (`AGVControlApi/Services/AgvService.cs:837-839`).
- `HandleWithOrderAsync` использует state OrderId как ключ поиска текущей версии заказа; сравнения с предыдущим order для решения «ждать/очищать перед отправкой» там нет (`StateMonitorService.cs:800-816`).
- Единственное явное сравнение последовательных OrderId в просмотренном state monitor находится в `TrackOrderState`: сравнивает новое и предыдущее значения для сброса waiting state (`StateMonitorService.cs:1419-1442`).
- Очистка `CurrentOrderId = null` встречается в явном cancel path, а не как ожидание очистки state перед новым order (`AgvInstantActionsService.cs:194-209`; `OrderService.cs:2531-2538`).
- Поиск ожидания исчезновения `state.orderId`/предыдущего OrderId перед publish: не найдено.

### 7. Несколько действий pick, затем drop
- `ExecuteTaskAsync` для нескольких actions вызывает `AgvTaskExecuteAsync` один раз (`AGVOrderService.cs:209-224`). Там PICK и DROP сортируются по ActionOrder, пары перебираются в одном цикле и каждая вызывает `ExecutePickDropAsync` (`AGVOrderService.cs:339-366`).
- Внутри пары, если нужно доехать в loading zone, вызывается `MoveAGVToNodeAsync`; затем формируется маршрут от loading к drop (`AGVOrderService.cs:386-422`).
- Маршрут разбивается на base chunks. Каждый chunk создается и при `CanSendOrderViaMqttAsync` отправляется через `SendOrderUpdateToAgvAsync`; после publish следующий `orderUpdateId` сразу сохраняется локально для цикла (`AGVOrderService.cs:424-477`). В этом коде нет ожидания завершения предыдущего update перед переходом к следующему chunk или следующей паре actions.
- Следующая пара действий в цикле начинается после возврата `ExecutePickDropAsync`, а не после Kafka completion callback/state ожидания (`AGVOrderService.cs:348-366`). Следовательно, подтвержденный кодом механизм не реализует «ждать завершения pick order, затем событие, затем отправить drop order»; он последовательно создает/публикует сообщения в рамках обработки задачи. Точное фактическое число публикаций зависит от результатов маршрутизации, `CanSendOrderViaMqttAsync` и chunk count.
- В `ExecutePickDropAsync` orderId остается одним `orderId`, а update ID передается в `InitOrderWithPath` и затем увеличивается после ответа send-update (`AGVOrderService.cs:386-387`, `437-447`, `467-470`).

## 4. Поиск отсутствующих механизмов
В исходниках `AGVControl` искались шаблоны `cancelOrder`, `CancelOrderAsync`, `CancelOrderOnAgvAsync`, `OrderCompletedAsync`, `OrderUpdateId`, `_orderCompletionTimeoutSeconds`, `WaitForOrderCompletion`, `state_message.OrderId`, `stateData.OrderId`, `OrderId ==`, `OrderId !=`, `Task.Delay`, `NO_ACCEPT`, `ACCEPTED`, `OrderStatus.ACCEPTED`, `Retry`, `retry`, `Timeout`, `WaitAsync`.

Результаты по конкретным вопросам:
- Автоматическая отправка cancelOrder непосредственно перед новым order: не найдено.
- Ожидание state/очистки предыдущего `orderId` перед новым order: не найдено.
- Ожидание Completed в `AGVOrderService` при dispatch следующего order/update: не найдено.
- Повтор order publish после отсутствия acceptance state: не найдено.
- Таймаут robot acceptance: не найдено.
- Обработка строки `NO_ACCEPT` в C# коде: не найдено.

## 5. Что подтверждает/не подтверждает наблюдение стенда
- Интервалы 0.3 с, 4 с, 15 с и необходимость ручного cancelOrder не задаются найденными путями отправки нового order; объяснение конкретного поведения симулятора по одному AGVControl коду не проверено.
- Код подтверждает, что основной путь отправки не делает предварительный cancelOrder и не ждет исчезновения прошлого `state.orderId`/подтверждения нового order перед возвратом из MQTT publish (`OrderService.cs:841-870`, `2277-2333`; `AgvService.cs:837-839`).
- Интерпретация `NO_ACCEPT` симулятором, момент сброса его внутреннего order state и временные требования между order/cancel не проверялись.
