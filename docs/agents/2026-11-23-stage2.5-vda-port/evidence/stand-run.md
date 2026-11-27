# Живой прогон шага 2, стенд 192.168.89.131

Дата: 2026-11-23. Ветка `ai/stage2.5-vda-port`, HEAD `2333202`.

## Что проверялось

- режим стенда `Routing:Order:ReleasedNodes = 0` (весь маршрут одним заказом);
- действие `drop` на рабочем узле `wo-2-12`;
- эндпоинт `GET api/AgvTask/lastnode/98001` в нашем сервисе (порт 5231 на стенде занят нашим `vrp-api`; `agv-control-api` остановлен).

## Команды и результат

```
curl -s -i http://127.0.0.1:5231/api/AgvTask/lastnode/98001
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
```

Заказ на слот `wo-2-12` (`hasLoad = true`, действие `drop`):

```
ORD_00156|Completed|-|wo-2-12
```

За время заказа `forkPos` в `visualization` достиг **1199.7 мм** (полное выдвижение вил),
после действия в `state.actionStates` действие `drop` = `FINISHED`,
`lastNodeId` = `wo-2-12`.

## Файлы

- `lastnode-200.txt` — ответ эндпоинта с заголовками;
- `order-response.json` — ответ API на создание заказа;
- `order-json.json` — фактический план заказа (`PlanJson`);
- `order-status.txt` — статус заказа;
- `state-after-action.json` — состояние робота после действия;
- `tests.txt` — сводка прогона тестов.
