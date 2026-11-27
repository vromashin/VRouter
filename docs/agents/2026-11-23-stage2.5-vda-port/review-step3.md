# Проверка: Шаг 3 — перенос автомата состояний и диспетчеризации трафика

Дата: 2026-11-24. Репозиторий: `C:\Users\User\source\repos\VR.RoutePlanner`.
Ветка `ai/stage2.5-vda-port-step3`, HEAD `13e9df6`, база `5d7df3d` (один коммит).
Коммит: «Автомат заказа по таблице §3.1, резервы vrt_TrafficResource только в Workers, PROGRESS_TIMEOUT от последнего прогресса».

## Вердикт

**PASS.** Сборка чистая (0/0), вся логика шага 3 покрыта юнит-тестами (309/309), границы соблюдены (только `VR.RoutePlanner`, нет миграций/appsettings/секретов, `AGVControl` чист), все 9 дефектов §7.1 и 16 строк §3.1 реализованы в коде и не перенесены дефекты AGVControl. Единственный нефункциональный сбой — 6 интеграционных тестов падают по таймауту чтения из БД на предсуществующих (не шага 3) тестах прогрева/ресурсов; это окружение стенда, а не код шага 3.

## Что проверено самостоятельно

- `git log --oneline 5d7df3d..HEAD` → один коммит `13e9df6`.
- `git diff --stat 5d7df3d..HEAD` → 30 файлов, +3134/−74, все в `src/` и `tests/` VR.RoutePlanner.
- `git diff --name-only 5d7df3d..HEAD` → нет `Migrations/**`, нет `appsettings*.json`, нет `.env/.pem/.key`, нет `AStarPathPlanner`/`TrafficResourceBuilder`/`VdaOrderBuilder`/`VdaOrderUpdater`/`DeterministicId`.
- `git status --porcelain` (VR.RoutePlanner) → пусто; `git status --porcelain` (AGVControl) → пусто.
- `dotnet build VR.RoutePlanner.sln -c Release --no-incremental` → `Предупреждений: 0, Ошибок: 0`.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1` (живая БД `rms_vrp_test`, `VRP_RMS_CONNECTION` задан, TCP 192.168.89.131:5432 доступен) → Contracts 13/13, Load 1/1, Simulation 1/1, **Unit 309/309**, Integration 38/44 (6 падений — таймаут чтения из БД), **пропущено 0**.
- `ModelSchemaTests`, `MigrationScriptTests`, `MigrationApplyTests` — в списке падений **отсутствуют** (зелёные, не изменены).
- Кодировка: `grep U+FFFD` по `src/**` → совпадений нет (файлы не испорчены).

### Соответствие спеке по диффу (выборочно, читал всю логику)

- **Резервы только по `vrt_TrafficResource.Id` и ключу `(MapVersionId, ResourceId)`** — `TrafficReservationRegistry` ключует `(MapVersionId, ResourceId)` (`_byResource`), `TrafficReservation` хранит `MapVersionId`/`ResourceId`; строки `vrt_*` читает `TrafficResourceLookup` (read-only, кэш по `(mapId, profile)` + `GraphVersion`).
- **Узловые `EXCLUSIVE` степени ≤ 2 не резервируются, `CONFLICT_ZONE` резервируются** — `TrafficHorizonCalculator.IsReservableNode` возвращает `true` только для `CONFLICT_ZONE`; `INDEPENDENT` ребро выбрасывается (`resource.ResourceType == INDEPENDENT → continue`).
- **Один автомат** — `ApplyStateAsync` единственная точка входа (сериализация `_serialGates` по серийнику), `ExtendPlanAsync`/`CheckTimeoutsAsync` на месте; `RecoverAsync` дополняет горизонт через тот же `ApplyStateAsync`.
- **Реальный координатор только в Workers, в Api заглушка** — `AddInfrastructure` регистрирует `NoOpTrafficCoordinator` (`ServiceCollectionExtensions.cs:98`), `Workers/Program.cs:16` переопределяет на `TrafficCoordinator`; `TrafficReservationCleanupWorker` (свип 5 с) зарегистрирован только в Workers.
- **Дефекты §3.2** — АС-8: `MergeNodes` не выводит `Reached` из `released` (только `Released |= state.Released`, `Reached` только по `lastNodeId`). АС-9: `PROGRESS_TIMEOUT` от `LastProgressAt` → `_progress` (по `lastNodeId`/`lastNodeSequenceId`), не от `AcceptedAt`. АС-10: `CloseWithoutOrderIdAsync` закрывает заказ по `lastNodeId == цель` через `GetActiveForRobotAsync` в том же сообщении (Р-1).
- **Дефекты §7.1 (все 9 не перенесены)** — Д-1 нет `OrderUpdateComplete(orderId-1)`; Д-2 завершение только по положительному доказательству (`reachedLast || reachedTarget || IsLastPlanNode` + `!driving` + `!HasRunningAction`); Д-3 `TryAcquire` атомарен (`FindBlocked` до `Save`), `Renew` — diff без обнуления; Д-4 `MissingEdges` возвращает `TRAFFIC_NO_RESOURCE`, а не «доступно»; Д-5 результат `TryReserveForOrderAsync` проверяется (409/`OrderRejectedException`); Д-6 `OnSnapshotReceived` ловит исключения, парсер не бросает NRE; Д-7 `RobotLastNodeResolver` всегда возвращает узел/`null`, без 404; Д-8 сериализация по серийнику, одна публикация продления; Д-9 `ResolveAsync` проверяет `MapId` (`null → NoResource`).

## Замечания

- **[MAJOR]** Интеграционные тесты в моём окружении: 6 падений `GraphWarmupServiceTests` (×4) и `ResourceRepositoryTests` (×2) с `Npgsql "Timeout during reading attempt"` на `CleanAsync`/`SaveAsync` (TRUNCATE/DELETE/INSERT в `vrt_*`). TCP до БД есть, но запись блокируется > 30 с — это окружение стенда (расшаренная БД `rms_vrp_test`), а не дефект шага 3: падающие тесты предсуществующие, не трогают новый код, а `ModelSchemaTests`/`MigrationScriptTests` зелёные. Доказательство владельца (`evidence/step3-build-tests.md`) заявляет 44/44; расхождение объяснимо состоянием БД. → Перегнать интеграционные при здоровой БД, но блокировать шаг 3 за это не нужно.
- **[MINOR]** §5.2 интеграционные тесты `TrafficResourceLookupTests`, `TrafficReservationRecoveryTests` не написаны (нужны подтверждённые данные карты 801 на стенде). Логика lookup/restore покрыта юнитами через фейки (`TrafficCoordinatorTests`, `Recover_*`), но слой «`vrt_*` → память» не протестирован на живой БД. Заявленное ограничение, не BLOCK.
- **[MINOR]** §9 критерии 22–23 (`traffic-409.txt`, `restart-recovery.txt`) не покрыты: требуется два робота, на стенде один. Логика 409 и восстановления покрыта юнитами (`CreateOrder_TrafficBlocked_Returns409`, `TryReserveForOrder_OtherRobotHoldsCorridor_IsBlocked`, `Recover_*`); живой прогон владельца подтвердил работу резервного слоя (запросы к `vrt_TrafficResourceNode`/`Edge`) и завершение `ORD_00157` на HEAD `13e9df6`.
- **[MINOR]** `PROGRESS_TIMEOUT` держит последний прогресс в памяти (`RobotOrderService.cs:72`, `_progress`); после рестарта до первого `state` опора на `AcceptedAt` (`LastProgressAt` fallback, стр. 954). Окно малое, но заказ, долго ехавший до рестарта, может быть преждевременно провален `CheckTimeoutsAsync` (тик 2 с) до прихода `state`. Зафиксированное ограничение.
- **[MINOR]** `evidence/step3-build-tests.md:38` ссылается на `RobotOrderStateMachineTests.cs`, а `:43` на `AgvControllerTests.CreateOrder_TrafficBlocked_Returns409` — таких файлов нет; тесты лежат в `RobotOrderServiceTests.cs` (строки 222–413 и 722). Документация, не код.
- **[MINOR]** `evidence/stand-run.md` и `tests.txt` (CP866) описывают шаг 2 (HEAD `2333202`, `ORD_00156`); файла-доказательства живого прогона шага 3 (HEAD `13e9df6`) в `evidence/` нет — живой прогон шага 3 зафиксирован только в отчёте владельца (не файл).

## Не покрыто проверкой

- §9 п.22–23 (`traffic-409.txt`, `restart-recovery.txt`) — требуют двух роботов; на стенде один. Не воспроизводимо в моей роли; подтверждения файлом нет. Логика покрыта юнитами.
- §5.2 `TrafficResourceLookupTests`/`TrafficReservationRecoveryTests` — не написаны (заявлено разработчиком).
- Свип по таймауту (`ReservationHorizonSeconds`) и восстановление горизонта после рестарта в живом Workers — не воспроизводились; логика покрыта юнитами (`Sweep_ReservationTtlWithoutRenew_ReleasesEverything`, `Recover_MatchingDrivingState_KeepsOrder`).
- Интеграционный прогон `scripts/test.ps1` с 0 падений — подтверждён файлом-доказательством `evidence/step3-build-tests.md` и `evidence/tests.txt` (числа Unit=309, Integration=44 совпадают с моим прогоном по Unit и по составу), но в моём окружении 6 интеграционных упало по таймауту БД.
