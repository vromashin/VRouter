# Передача знаний: порт VDA-заказа к поведению AGVControl

Документ для последующего использования. Собрано по итогам работ 2026-11-20 … 2026-11-27.
Всё, что здесь написано, проверено на стенде 131 или подтверждено кодом с указанием файла и строки.

## 1. Задача и границы

Цель: `VR.RoutePlanner` заменяет AGVControl. Совместимость нужна только на границах
(HTTP, Kafka, MQTT, имена таблиц). Поведение движения и вил должно совпадать с AGVControl.

Правила, которые действовали всю работу:

- старый код AGVControl не переиспользуется, переносится только поведение;
- планировщик A* и таблицы `vrt_*` не меняются, миграции не добавляются;
- приоритет: сохранность груза и стеллажей важнее времени цикла;
- чужие репозитории только читаются.

## 2. Состояние на сейчас

Ветка `ai/stage2.5-vda-port-step3` в `C:\Users\User\source\repos\VR.RoutePlanner`.

| Коммит | Что сделано |
|---|---|
| `9cd32c1` | Форма заказа по эталону: действие вил только в целевом узле, курс рабочего узла к паллете, курс первого ребра, курс первого узла, без `maxSpeed`/`maxRotationSpeed`, стоянка в целевом узле |
| `df8468f` | Старт заказа как в AGVControl: убрана коррекция позы (`initPosition`), перезапуск симулятора не ждёт 28 с при отказе sudo |
| `37b8d07` | Одна WMS-задача = один VDA-заказ, плечи как `orderUpdateId + 1`, сквозной `sequenceId` |
| `d1d92c4` | Сообщение 0 без действий, узел-якорь с `VIRTUAL_START_<node>` и дублем узла, плечи без ожидания подтверждения |
| `d43e1e1` | Траектория ребра уходит кривой из карты (`degree`, `knotVector`, `controlPoints`), с разворотом для обратного проезда |

Стенд: наш стек (`vrp-api`, `vrp-workers`) и симулятор, собранный из master от 27.11.2026.

Проверено живьём: `ORD_00206` (`wo-3-1 -> wo-0-1`, 1:52) и `ORD_00207`
(`wo-2-3 -> wo-3-19`, 2:09) закрылись `Completed`, оба действия вил выполнены.

## 3. Как устроен заказ в AGVControl

Репозиторий `C:\Projects\VRouter\AGVControl`, файл `AGVControlApi\Data\OrderService.cs`.

### Один заказ на задачу

Одна WMS-задача это один `orderId`, плечи идут как order update с растущим `orderUpdateId`
(`OrderService.cs:2381-2466`, `AGVOrderService.cs:437-470`). Живой эталон:
`ref-rev-leg1-to-pick.json` и `ref-rev-leg2-pick-drop.json` имеют один `orderId`
`TSK:53ed401c-…`, update 0 и 2, опубликованы в одну секунду.

`cancelOrder` перед новым заказом не отправляется, очистка `state.orderId` не ожидается,
подтверждения заказа нет (поиск `NO_ACCEPT` по коду пуст).

### Узлы

- Координаты из карты, `theta` только у LOADING/UNLOADING (`OrderService.cs:1137-1173`).
- Допуски: `max(value ?? 0, 0.1)` (`:1188-1189`).
- Первый узел сообщения: X/Y/Theta берутся из состояния AGV, при этом
  `theta >= 3.14` и `XY >= 0.3` (`:2301-2310`); в update только при `OrderUpdateId == 0`
  (`:2405-2417`).

### Рёбра

| Поле | Правило | Строки |
|---|---|---|
| `trajectory` | Кривая из карты как есть (`degree`, `knotVector`, `controlPoints` с весами) | `1229-1265` |
| разворот точек | `ControlPoints.Reverse()`, если `Bidirectional` и направление против движения | `1236-1246`, `1045-1070` |
| `orientation` | Первое ребро `0` или `PI` по `MoveDirection`, остальные `0` | `1200`, `1268` |
| `orientationType` | пусто → `TANGENTIAL` | `1259-1261` |
| виртуальное ребро | `OrientationType = null`, `Orientation = null`, `RotationAllowed = false` | `1221-1224` |

### Действия

`pick`/`drop` строятся в `GetCarrierActionsByNode` (`:1679`), `blockingType = HARD` (`:1693`).
В основном пути `containerX/Y` = позиция узла (`:1685-1686`), без смещения на длину вил.
Второй путь, `ActionService.cs:708`, считает `X + (Length + 0.05) * cos(Theta)`;
какой из них реально попадает в заказ для Tusk, не установлено (живые снимки дают смещение
1.475 м, что ближе ко второму).

### Чанки

`SplitPath` шаг `base-1`, `SplitEdges` `base-1` (`:1985-2023`). Размер `BaseNodesCount`
по умолчанию 40 в `OrderService.cs:65` и 4 в `AGVOrderService.cs:84` (расхождение дефолтов).
При update пересылается весь `NodeSequence` текущего чанка с виртуальным стартом
(`:1099-1101`, `:2389`), при этом тот же физический узел может получить другой `sequenceId`.

## 4. Как устроен симулятор

Исходники: `https://git.whsystems.ru/robots/robotssimulator` (Qt5 C++),
логика движения в отдельном репозитории `https://git.whsystems.ru/robots/vdalogic`.

### Траектория и кривая

`core/vdalogic`, `src/MotionController/MotionController.cpp:378-398`:

```cpp
if (!edge.trajectory.isEmpty() && edge.trajectory.controlPoints.size() > 2)
    traj = e.trajectory;                        // едем по NURBS из заказа
else {
    controlPoints.append(nodeStates[start].nodePosition);
    controlPoints.append(nodeStates[end].nodePosition);
    traj.setControlPoints(controlPoints);       // прямая между узлами
}
```

Вывод: **если в заказе две контрольные точки, `trajectory` игнорируется и строится прямая
между позициями узлов**. Дуга из карты учитывается только при трёх и более точках.
Именно это резало угол на ребре `801-wa148-wa104` (в карте `degree 3`, 4 точки).

Флаг `System/kostil` (`SettingsManager.cpp:250-252`, по умолчанию `false`) делит
`controlPoints[0]` и `controlPoints[3]` на 100 (`MotionController.cpp:383-387`).
При выключенном флаге точки передаются как есть, в метрах.

### Движение

- `TrajectoryFollower.h` считает NURBS по `degree`, `knotVector`, `controlPoints`.
- `MotionController::update()` возвращает `false`, если LUT траектории пуст
  (`MotionController.cpp:406-410`) — при вырожденной прямой робот просто не начинает перегон.
- `VDARobotModel.cpp:140-145`: при `trajFollow() == false` управление не выдаётся.
- `lookaheadDistance = 0.5` м (`:198-209`), линейная скорость не больше `1.0` (`:22`),
  при ошибке курса больше 5° линейная скорость ноль (`:252-260`).
- Заблокированный маршрут (`:374-378`) останавливает интегрирование динамики.

### Требования к продолжению заказа

`docs/vdalogic.md:112`: первый узел продолжения должен совпадать по `id` и `sequenceId`
с последним узлом предыдущего сообщения. Наш билдер это выполняет: сообщение начинается
тем же узлом с тем же номером.

## 5. Стенд 131: доступ и операции

Доступ: `vr@192.168.89.131`, пароль в рабочей переписке. SSH-ключ не настроен,
используются `plink`/`pscp` из `C:\Program Files\PuTTY` с флагом `-pw`.

Важно про конфигурацию: архив выкладки содержит `appsettings.json` и перезаписывает его
на стенде при каждом обновлении. Настройки, которые должны пережить выкладку, держать
в `appsettings.Production.json` (он в архив не входит). Один раз на этом потеряли
`Routing:Charging:Enabled`, и триггеры молча не работали.

### Сервисы

| Сервис | Назначение |
|---|---|
| `vrp-api`, `vrp-workers` | наш сервис |
| `robots.simulator` | симулятор робота |
| `agv-control-api`, `agv-fleet-manager` | старый стек, нужен только для снятия эталонов |
| `rms-common`, `rms-wms-common`, `rms-store-map`, `rms-admin` | инфраструктура RMS |

### Порты стенда 131

| Порт | Сервис | Примечание |
|---|---|---|
| 22 | `sshd` | вход на стенд |
| 1883 | `mosquitto` | MQTT, пользователь `rcs` |
| 5432 | `postgresql@14-main` | база `rms` |
| 5000 | `RMS.Admin` (`rms-admin`) | чужой сервис, не наш |
| 5020 | `RobotsSimulator` HTTP | только `127.0.0.1`; `/status`, `/charge`, `/power`, `/action` |
| 5050 | `pgadmin4` | веб-админка Postgres |
| 5078 | `RMS.WMS.Common` (`rms-wms-common`) | его опрашивает симулятор |
| 5173 | `ntls.map` (`rms-map`) | `node server.js`, карты и раздача PNG |
| 5231 | `vrp-api` | наш основной HTTP API |
| 5246 | `RMS.Store.Map` (`rms-store-map`) | карты и узлы |
| 5251 | `agv-control-api` | старый стек, когда запущен (уточнять `ss -ltnp`) |
| 5257 | `RMS.Common` (`rms-common`) | общий сервис |
| 5310 | `vrp-api` | второй адрес из `Urls` в `appsettings.Production.json`: `http://0.0.0.0:5231;http://0.0.0.0:5310` |
| 8080 | `redpanda-consol` | веб-консоль Kafka |
| 8081, 8082, 9644, 33145 | `redpanda` | служебные порты |
| 9092 | `redpanda` | Kafka bootstrap |
| 5231/5310 | `vrp-api` | оба порта один процесс `VR.RoutePlanner.Api.dll` |

`vrp-workers` портов не слушает: это фоновый процесс. Симулятор дополнительно держит IPC-сокет
`RobotsSimulatorIPC` для `rssconsole`, это не TCP.

Проверять актуальный список: `ss -ltnp` на стенде.


### MQTT

Брокер на `127.0.0.1:1883`, пользователь `rcs` / `ntls1234`.
Топики: `APR/v2/Tuskrobots/98001/{order,instantActions,state,visualization,connection}`.

### Постановка задачи WMS

```bash
rpk topic produce simple-tasks < task.json
```

`task.json` это задача с `actions` (поле `actionType`: 0 = pick, 1 = drop, `location` = `wo-*`).

### База

```bash
sudo -u postgres psql -d rms
```

Ключевые таблицы: `vrt_RobotOrder` (`Status`, `OrderUpdateId`, `TargetNodeId`, `FailureReason`),
`vrt_RobotOrderNode`, `Agvs`, `Nodes`, `Edges` (поле `Trajectory` в jsonb).

### Сброс симулятора

Дрейф и зависание позы лечатся только заменой файла состояния:

```bash
sudo systemctl stop robots.simulator
sudo cp /root/RobotsSimulatorStates.ini.bak-1795369289 \
        /opt/rms/ntls.simulator.robotssimulator/RobotsSimulatorStates.ini
sudo chown root:root /opt/rms/ntls.simulator.robotssimulator/RobotsSimulatorStates.ini
sudo systemctl start robots.simulator
```

### Выкладка нашего сервиса

```powershell
dotnet publish src\VR.RoutePlanner.Workers -c Release -r linux-x64 --self-contained false -o C:\Temp\vrp-publish\workers
dotnet publish src\VR.RoutePlanner.Api     -c Release -r linux-x64 --self-contained false -o C:\Temp\vrp-publish\api
tar -czf C:\Temp\workers.tgz -C C:\Temp\vrp-publish\workers .
tar -czf C:\Temp\api.tgz     -C C:\Temp\vrp-publish\api .
pscp -batch -pw <пароль> C:\Temp\workers.tgz C:\Temp\api.tgz vr@192.168.89.131:/tmp/
```

На стенде: остановить, распаковать в `/opt/rms/vrp-workers` и `/opt/rms/vrp-api`, запустить.
Перед выкладкой делать бэкап в `/tmp/bk-workers-<ts>.tgz`.

### Как снять эталон со старого AGV

1. Остановить `vrp-api` и `vrp-workers`.
2. Запустить `agv-control-api` и `agv-fleet-manager`.
3. Подписаться на `APR/v2/Tuskrobots/98001/order` и `instantActions`.
4. Опубликовать задачу в `simple-tasks`.
5. Собрать сообщения, вернуть наш стек.

Скрипты: `evidence/capture_old_full.sh`, `capture_updates.sh`, `capture_old_curve.sh`.

## 6. Сборка симулятора (если понадобится снова)

Важно: **с самого стенда клонировать нельзя**, GitLab требует авторизацию
(`could not read Username for 'https://git.whsystems.ru'`). Клонировать надо на рабочей
машине и переносить архивом.

Что нужно:

- `robotssimulator` (master), `logic` (master) в `core/logic`, `vdalogic` (master) в `core/vdalogic`;
- `qtmqtt` (ветка 5.15.2), собирается и ставится в систему;
- `components5` **не нужен**: в `RobotsSimulator.pro` GUI-приложение закомментировано,
  собираются только `core`, `assets`, `console`, `server`;
- пакеты: `build-essential qtbase5-dev qtbase5-private-dev libqt5serialport5-dev
  libqt5opengl5-dev libvulkan-dev glslang-tools`;
- `VULKAN_SDK=/opt/vulkan-sdk` как заглушка со ссылками на `/usr/include`, `/usr/lib/<multiarch>`,
  `/usr/bin` (как в CI).

Порядок сборки: `core/RobotsCore.pro`, затем
`assets/VDARobots/VDAForkLiftPlugin/VDAForkLiftPlugin.pro`,
затем `RobotsSimulatorConsole.pro` и `RobotsSimulatorServer.pro`.
Результат: `bin_RobotsSimulator/linux/{RobotsSimulatorService, rssconsole, librobotscore.so,
lidar_raytrace.spv, assets/libVDAForkLiftPlugin.so}`.

Установка: заменить эти файлы в `/opt/rms/ntls.simulator.robotssimulator/bin`, добавить
`bin/config/pallet_types.json` (без него сервис ругается при старте), оставить
`map_801.png` и `libQt5Mqtt*`. RNS-плагин можно отключить, если ядро новее:
стенд использует `VDA Forklift`.

Скрипты: `evidence/build_simulator4.sh`, `evidence/install_simulator.sh`.

Рабочий пример сборки: `/tmp/simbuild2` на стенде, пакет исходников `/tmp/simsources.tgz`.

## 7. Что было сломано и как лечилось

| Симптом | Причина | Решение |
|---|---|---|
| Вилы срабатывали не там, робот вставал под 45° | действие ставилось на любой рабочий узел, курс в узле был не к паллете | `9cd32c1`: действие и `theta` только в целевом узле |
| Робот ехал в стеллаж при старте | `CorrectPoseAsync` сверял позу с координатами узла, а рабочий узел стоит в позиции паллеты (смещение 1.475 м) | `df8468f`: коррекция позы из потока заказа убрана |
| Следующий заказ не принимался (`NO_ACCEPT`) | на каждое действие создавался новый `orderId`, робот держал прошлый заказ | `37b8d07`: одна WMS-задача = один заказ, плечи как updates |
| Update отвергался: `AGV not valid continuation of the currently still running order` | в первом узле update были координаты позы робота вместо узла карты, не было тройки якоря | `d1d92c4`: раскладка как в `ref-rev-leg2` |
| Робот вставал на ребре `wa148 -> wa104`, `state` переставал идти | в заказ уходила прямая из двух точек, симулятор игнорировал кривую и резал дугу | `d43e1e1` + обновление симулятора |

Прогоны для истории:

| Заказ | Симулятор | Траектория | Итог |
|---|---|---|---|
| `ORD_00204` (24.11) | старый, 13.10 | прямая | встал на `wa148 -> wa104`, `STATE_SILENCE` |
| `ORD_00205` (27.11) | старый, 13.10 | прямая | встал там же, `PROGRESS_TIMEOUT` |
| `ORD_00206` (27.11) | новый, master | кривая | `Completed`, 1:52 |
| `ORD_00207` (27.11) | новый, master | кривая | `Completed`, 2:09 |

## 8. Открытые вопросы и риски

1. **`containerX/Y`**. Живые снимки дают смещение 1.475 м (длина вил), а в основном пути
   AGVControl смещения нет. Нужно доопределить, какой путь используется для Tusk.
   На работу вил в симуляторе это пока не влияло, но на реальном роботе важно.
2. **`PROGRESS_TIMEOUT` и `STATE_SILENCE`**. Заказ закрывается `Failed`, но робот остаётся
   с грузом. Алгоритм оператора (снять заказ, перевести в ручной режим, перевыдать задачу)
   не описан.
3. **Ошибки авторизации симулятора**: `AuthenticationRequiredError` на
   `WMS.Common/v2.0/Robot/GetAll` примерно раз в 30 с, 25.11 одно обновление токена упало
   с `Forbidden`. Токен надо перевыпустить, к движению отношения не имеет.
4. **`HasLoad` в симуляторе не меняется**: захват груза подтверждается только статусами
   действий, физически не проверяется.
5. **Многоплечевые цепочки** (три и более действий в задаче) не проверялись.
6. **`_lastUpdateSequence`** в `RobotOrderService` не сбрасывается при новом заказе
   (замечание разработчика, не менялось).
7. **Расхождение `sequenceId` в состоянии робота**: в снимке 24.11 робот отдавал узлы
   24..42, тогда как в заказе 26..42; робот не присылает признак `reached`.
8. **GPU-лидар недоступен** на виртуалке (`vkCreateInstance failed`), работает CPU-бэкенд.
   На поведение не влияет, но производительность ниже.
9. **Порог `orientation`** `> pi/2` проверен только на разворотах около 180°.

## 9. Где что лежит

Все документы в `C:\Projects\VRouter\doc\agents\2026-11-24-agv-robot-control\`.

| Файл | Содержание |
|---|---|
| `spec.md` | спека D1-D5: форма заказа, вилы, курсы, допуски |
| `spec-order-updates.md` | спека «одна задача = один `orderId` + updates» |
| `spec-order-updates-2.md` | поправка П-1..П-6 по итогам первого прогона |
| `reverse.md` | ориентировка, движение, вилы в AGVControl |
| `reverse-start.md`, `reverse-start2.md` | старт заказа, отсутствие `cancelOrder` и ожиданий |
| `reverse-order-build.md` | сборка заказа в AGVControl целиком |
| `reverse-simulator-motion.md` | движение симулятора (без `vdalogic`) |
| `ref-*.json` | эталонные заказы старого AGV |
| `our-*.json`, `d2-*.json` | наши заказы для сравнения |
| `evidence/` | прогоны, снимки, скрипты, доказательства |

Ключевые скрипты в `evidence/`: `analyze_shape.py`, `diff_same_route.py`,
`capture_*.sh` (снятие заказов), `build_simulator4.sh`, `install_simulator.sh`,
`collect_stuck2.sh` (снятие логов зависания), `run_curve_test.sh`.

## 10. Как быстро воспроизвести проверку

1. Убедиться, что запущены `vrp-api`, `vrp-workers`, `robots.simulator`;
   старый стек (`agv-control-api`, `agv-fleet-manager`) остановлен.
2. Сбросить симулятор выровненным файлом состояния (раздел 5).
3. Подписаться на `order` и запустить задачу `pick wo-3-1 -> drop wo-0-1`.
4. Проверить: в заказе два сообщения с одним `orderId`; ребро `801-wa104-wa148` идёт
   с `degree 3` и четырьмя точками; в журнале симулятора есть
   `start: "wa148-28" end: "wa104-30"` и следующая строка перегона; в БД статус `Completed`.
5. Сравнить форму с `ref-rev-leg1-to-pick.json` и `ref-rev-leg2-pick-drop.json`.
