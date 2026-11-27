# Спека этапа 2: A* и функция стоимости

Дата: 2026-11-21. Репозиторий `VR.RoutePlanner`, ветка `ai/stage2-planner`.
Основание: план `doc/A297_Plan_RoutePlanner_TrafficManager.md`, раздел «Этап 2».

## Зачем

Научить систему строить маршрут по сети движения из этапа 1. Недопустимые ребра исключаются,
цена считается по времени с штрафом за поворот и тесноту. Приоритет владельца от 2026-11-21:
сохранность груза и стеллажей важнее времени цикла.

## Факты из системы (проверено)

- 293 узла, 365 ребер, 430 реберных концов, длины ребер от 0.84 до 13.82 м, средняя 1.41.
- `Edges.MaxSpeed` равен 10 у всех 365 ребер, это заглушка, скорость берётся из профиля робота
  (tusk-forklift-1102: ширина 1.102, длина 1.455, профиль даёт 2.0 м/с как потолок этапа 3).
- Ширины `vrt_EdgeWidth` 0.800..22.415 м: 67 ребер уже 1.5 м, 58 в диапазоне 1.5-2.5 м,
  127 в 2.5-4 м, 113 шире 4 м.
- `vrt_EdgePassability`: 730 строк на два профиля, непроходимых 67 (tusk) и 42 (narrow-0600).
- Траектория ребра уже дискретизирована в `WarehouseEdge.TrajectoryPoints`, парсить NURBS на этапе 2 не нужно.

## Часть 1. Домен и конфигурация

Новое в `Domain/Planning`:

- `RouteSegment(RouteEdgeDirection Direction, Guid EdgeId, string EdgeCode, double LengthMeters,
  double SpeedMetersPerSecond, double ClearanceMeters, double CostSeconds, double CumulativeSeconds,
  double CumulativeCost, bool IsNarrow)`.
- `PlannedRoute(string MapId, string Profile, Guid FromNodeId, Guid ToNodeId, IReadOnlyList<RouteSegment> Segments,
  double TotalLengthMeters, double TotalSeconds, double TotalCost, int TurnCount, double MinClearanceMeters,
  bool PredictedRisk, string? Reason)`.
- `RouteEdgeDirection { Forward, Backward }`.

Новое в `Application/Planning`:

- `IPathPlanner.PlanAsync(RoutePlanRequest request, CancellationToken ct)`.
- `RoutePlanRequest(string MapId, string Profile, Guid FromNodeId, Guid ToNodeId, bool HasLoad)`.
- `IWaitEstimator.EstimateAsync(...)`, на этапе 2 возвращает ноль, на этапе 3 подставит резервирования.

Конфигурация `Routing:Cost` в `RoutePlannerOptions`:

```
"Cost": {
  "TurnPenaltySeconds": 2.0,
  "ClearanceWeight": 5.0,
  "ClearanceFloorMeters": 0.4,
  "LengthWeight": 0.0,
  "NarrowClearanceMeters": 0.4,
  "SafeClearanceMeters": 0.7,
  "SafetyOverrideRatio": 2.0,
  "LoadSpeedFactor": 0.7,
  "SpeedBands": [
    { "BelowClearanceMeters": 0.4, "MaxSpeedMetersPerSecond": 0.3 },
    { "BelowClearanceMeters": 0.7, "MaxSpeedMetersPerSecond": 0.6 },
    { "BelowClearanceMeters": 1.2, "MaxSpeedMetersPerSecond": 1.2 }
  ],
  "ProfileTopSpeedMetersPerSecond": 2.0
}
```

Валидация на старте: все числа больше нуля, `ClearanceFloorMeters <= NarrowClearanceMeters < SafeClearanceMeters`,
`SafetyOverrideRatio >= 1`, полосы скоростей строго возрастают по порогу, `LoadSpeedFactor` в диапазоне 0.1..1.0.

## Часть 2. Цена ребра и поиск

**Допустимость.** Ребро участвует в поиске, только если для профиля есть строка `vrt_EdgePassability`
с `IsPassable = true`, есть `vrt_EdgeWidth`, ребро и оба его узла активны, а направление разрешено:
`Bidirectional = true` либо направление совпадает с `FromNodeId -> ToNodeId`.

**Запас и скорость.** `запас = ширина - ширина_профиля`, снизу ограничен `ClearanceFloorMeters`.
Скорость ребра берётся из первой полосы `SpeedBands`, порог которой выше запаса; если запас больше
последнего порога, скорость равна `ProfileTopSpeedMetersPerSecond`. При `HasLoad = true`
скорость умножается на `LoadSpeedFactor`.

**Поворот.** Угол считается между касательной входящего ребра в узле и касательной исходящего ребра:
направление касательной берётся по первым и последним точкам `TrajectoryPoints`. Угол больше 5 градусов
считается поворотом, цена `TurnPenaltySeconds * (угол / 90)` ограничена сверху `2 * TurnPenaltySeconds`.
Если у узла или ребра `RotationAllowed = false` и угол больше 90 градусов, переход запрещён.

**Формула.** `цена_ребра = длина / скорость + ClearanceWeight * (ClearanceFloorMeters / запас)^2 + LengthWeight * длина`,
плюс стоимость поворота на входе в ребро и `IWaitEstimator.EstimateAsync` (ноль на этапе 2).
Все компоненты сохраняются в `RouteSegment` для объяснения маршрута.

**Эвристика.** `h(n) = евклидово_расстояние(n, цель) / ProfileTopSpeedMetersPerSecond`. Она допустима,
так как ни одно ребро не может быть быстрее верхней скорости профиля. Повороты в эвристику не входят.

**Правило безопасности, два прохода.**
1. Первый проход обычным A*. Если в маршруте нет ребер с запасом меньше `NarrowClearanceMeters`,
   он и возвращается.
2. Иначе второй проход с запретом ребер, у которых запас меньше `NarrowClearanceMeters`.
   Если второй маршрут существует и его цена не больше `SafetyOverrideRatio * цена_первого`,
   возвращается он, `PredictedRisk = false`.
3. Если второй дороже лимита или не существует, возвращается первый с `PredictedRisk = true`
   и `Reason = "SAFE_DETOUR_TOO_EXPENSIVE"` либо `"NO_SAFE_DETOUR"`.

**Ошибки.** Нет узла, нет профиля, нет строк ширин или проходимости для версии карты, старт равен цели,
пути нет: возвращается `RoutePlanResult` с `Found = false` и кодом причины, исключения не бросаются
кроме отмены и ошибок инфраструктуры.

## Часть 3. HTTP-контракт

`POST /api/route/plan`, тело:

```json
{ "mapId": "801", "profile": "tusk-forklift-1102",
  "fromNodeId": "wo-1-3", "toNodeId": "wo-1-4", "hasLoad": false }
```

`fromNodeId` и `toNodeId` это внешние коды узлов (`Nodes.NodeId`), а не `Guid`, потому что именно их
используют заказы и карта. Ответ 200:

```json
{ "found": true, "totalLengthMeters": 12.4, "totalSeconds": 9.8, "totalCost": 14.2,
  "turnCount": 2, "minClearanceMeters": 0.85, "predictedRisk": false,
  "segments": [ { "edgeCode": "801-wa145-wa146", "direction": "Forward",
                  "lengthMeters": 1.4, "speed": 1.2, "clearanceMeters": 0.85,
                  "costSeconds": 1.9, "cumulativeSeconds": 1.9, "isNarrow": false } ] }
```

Ответ 404, если карта или узел не найдены; 400, если профиль неизвестен или старт равен цели.
Раздел `explain` с разложением цены возвращается только при `?explain=true`.

`GET /api/route/graph/{mapId}/summary?profile=...` возвращает число узлов, ребер, проходимых и
непроходимых ребер, минимальный и максимальный запас. Эндпоинт нужен для диагностики и проверки
после прогрева.

## Часть 4. Тесты

Unit, на синтетическом графе без базы:

1. Цена ребра совпадает с формулой на пяти комбинациях запас/скорость/длина.
2. Поворот 90 градусов добавляет ожидаемую долю `TurnPenaltySeconds`, поворот на 10 градусов меньше.
3. Уменьшение запаса повышает цену квадратично.
4. Непроходимое ребро и ребро без строки проходимости не попадают в маршрут.
5. Одностороннее ребро в обратном направлении не используется.
6. Старт равен цели: маршрут из нуля сегментов, `Found = true`.
7. Нет пути: `Found = false`, код причины `NO_PATH`.
8. Правило безопасности: при наличии обхода с запасом 0.8 м берётся обход, `PredictedRisk = false`.
9. Правило безопасности: обход дороже `SafetyOverrideRatio`, берётся короткий путь, `PredictedRisk = true`,
   `Reason = "SAFE_DETOUR_TOO_EXPENSIVE"`.
10. Эвристика не переоценивает: для набора узлов `h(n)` не больше реальной стоимости маршрута.
11. `HasLoad = true` понижает скорость и повышает время маршрута.

Integration, на живой базе `rms_vrp_test`, с `SkippableFact` и `VRP_RMS_CONNECTION`:

12. Маршрут между `wo-1-3` и `wo-1-4` (узлы из живого заказа WMS) находится, все сегменты проходимы.
13. Сумма длин сегментов совпадает с суммой геометрии ребер с точностью 1 см.
14. На маршруте нет ребра с запасом меньше `NarrowClearanceMeters`, если обход укладывается в лимит.
15. Сто планов подряд между случайными активными узлами: нет исключений, среднее время одного плана
    меньше 200 мс на карте 801.

## Приёмочная выжимка

1. Сборка решения без предупреждений, все прежние тесты зеленые, новых пропусков нет.
2. `Routing:Cost` читается и валидируется, значения по умолчанию совпадают со спекой.
3. Непроходимое ребро не попадает в маршрут ни при каких настройках (проверяется тестом 4).
4. Правило безопасности работает в обе стороны (тесты 8 и 9).
5. HTTP `POST /api/route/plan` отвечает по формату выше, `?explain=true` добавляет разложение цены.
6. Integration-тесты 12-15 проходят на `rms_vrp_test`, либо помечаются `Skip` без переменной окружения.
7. Контрактный тест `Tests.Contracts` не сломан, эндпоинты этапа 2 в снимок не входят и не притязают на паритет.
8. Формула и полосы скоростей взяты из конфигурации, хардкода чисел в коде нет.

## Не входит

Резервирования и временные интервалы (этап 3), SIPP, VDA и MQTT, изменение схемы `vrt_*`
(этап 2 пишет только в память и читает из базы), покрытие уровней 1 и 2 контрактов (этап 5).

## План проверки

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
dotnet test tests\VR.RoutePlanner.Tests.Unit --filter "FullyQualifiedName~Planning"
```

Контролеру: `has-pending-model-changes` не требуется, схема БД на этапе 2 не меняется.
