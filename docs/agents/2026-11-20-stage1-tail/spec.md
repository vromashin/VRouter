# Спека: хвосты этапа 1 (ModelSnapshot и наполнение vrt_*)

Дата: 2026-11-20. Репозиторий `VR.RoutePlanner`, ветка `ai/stage1-graph`.

## Зачем

После PASS этапа 1 остались две дыры: нет `ModelSnapshot`, поэтому `dotnet ef migrations add` для любой новой таблицы сгенерирует `CreateTable` на все семь `vrt_*`; и нет рабочего кода, который наполняет `vrt_*`, сейчас это делают только тесты.

## Часть 1. Baseline-миграция и snapshot

Цель: `dotnet ef migrations has-pending-model-changes` отвечает «no changes», новые миграции диффятся от текущей модели, а существующая база не получает повторный `CreateTable`.

Порядок:

1. В каталоге репозитория выполнить `dotnet tool restore`, затем `dotnet dotnet-ef migrations add Baseline --project src/VR.RoutePlanner.Infrastructure --startup-project src/VR.RoutePlanner.Infrastructure`. EF сгенерирует `Migrations/20261120193125_Baseline.cs` и `Migrations/RoutePlannerDbContextModelSnapshot.cs`.
2. Тело `Up` и `Down` baseline-миграции заменить пустым содержимым с комментарием: схема создана миграциями `2026-11-20_Stage1_Graph` и `2026-11-20_Stage1_Rev4`, baseline фиксирует только модель для последующих диффов.
3. Snapshot закоммитить как есть, без правок руками.
4. Проверить: `dotnet dotnet-ef migrations has-pending-model-changes` возвращает «No changes have been detected».
5. Проверить: `dotnet dotnet-ef migrations script` создает таблицы `vrt_*` только в первых двух миграциях; после baseline допустимы лишь `CREATE TABLE public."vrt_WarmupState"` и `CREATE INDEX "IX_vrt_EdgeWidth_EdgeId"`, baseline дает только `INSERT INTO "__EFMigrationsHistory_RoutePlanner"`.
6. Применить на живой базе `rms_vrp_test`: `database update` добавляет третью строку в историю и не меняет схему.

Риски и правила: baseline идет последней среди миграций схемы, после него допускаются только миграции служебных таблиц; при любом изменении модели сначала `migrations add <Имя>`, затем глазами проверить сгенерированный SQL и заменить повторное создание объектов на идемпотентные операции, как было в `Stage1_Rev4`.

## Часть 2. Наполнение vrt_* рабочим кодом

Сейчас порядок «прочитал граф, посчитал ширины, построил ресурсы, сохранил» существует только в тестах. Нужен сервис-оркестратор и хостед-сервис.

Новые типы в `VR.RoutePlanner.Application`:

- `Startup/IMapVersionRepository` с `GetOrCreateAsync(string mapId, WarehouseGraph graph, CancellationToken)` возвращает `Guid`. Реализация в Infrastructure: ищет строку в `vrt_MapVersion` по паре `MapId` и `GraphVersion`, создает при отсутствии, повторный вызов возвращает тот же идентификатор.
- `Startup/IGraphWarmupService` с `WarmupAsync(string mapId, CancellationToken)` возвращает `WarmupResult`: `MapVersionId`, `EdgeCount`, `WidthSource`, `ResourceCount` по профилям, `ImpassableCount` по профилям, `DurationMs`, `ReusedWidths` (ширины взяты из базы, а не посчитаны заново).
- `Startup/GraphWarmupService` оркестратор, порядок строго такой: загрузка графа, `GetOrCreate` версии карты, `IEdgeWidthRepository.GetOrCreateAsync`, `TrafficResourceBuilder.BuildAsync`, `IResourceRepository.SaveAsync`. Ошибку любого шага логировать с указанием шага и пробрасывать наверх.

Конфигурация, секция `Routing:Warmup`:

- `Enabled` по умолчанию `true`
- `MapId` по умолчанию `801`
- `RunOnStartup` по умолчанию `true`
- `RefreshIntervalMinutes` по умолчанию `0` (только при старте, ноль отключает периодичность)
- `FailFast` по умолчанию `false` (при `true` исключение валит хост)

Запуск:

- `VR.RoutePlanner.Workers`: `GraphWarmupWorker` как `BackgroundService`, первый прогон при старте, далее по интервалу. Ошибки логируются, при `FailFast` останавливают хост.
- `VR.RoutePlanner.Api`: `POST /api/graph/warmup` запускает прогрев и возвращает `WarmupResult`, `GET /api/graph/status` отдает последний результат или `404`, если прогрева не было. Регистрация сервисов в `ServiceCollectionExtensions` инфраструктуры.

Требования к поведению:

- Однопоточность: параллельный вызов прогрева не запускает второй расчет, второй вызов ждет завершения первого и получает тот же результат.
- Идемпотентность: повторный прогрев не создает дублей, счетчики строк `vrt_*` не растут, ширины переиспользуются, если источник совпадает.
- Профильная изоляция: сохранение набора одного профиля не удаляет строки других профилей той же версии карты. Удаление в `vrt_TrafficResource`, `vrt_TrafficResourceEdge`, `vrt_TrafficResourceNode`, `vrt_ResourceConflict` и `vrt_EdgePassability` ограничено профилями сохраняемого набора. Прогрев с конфигом из одного профиля не должен стирать `narrow-0600`.
- Ничего не пишется в таблицы `AGVControl` (`Orders`, `Agvs`, `AgvStates`) и в `Maps`, `Nodes`, `Edges`.
- Секретов в коде и конфиге нет: строка подключения только из окружения или `ConnectionStrings:Rms`.

Таблица итога прогрева `public."vrt_WarmupState"` (одна строка на `MapId`, первичный ключ `MapId`): `MapVersionId uuid`, `GraphVersion varchar(128)`, `EdgeCount int`, `WidthSource varchar(16)`, `ResourceCount text` (JSON словаря профиль → число), `ImpassableCount text` (то же), `DurationMs bigint`, `ReusedWidths boolean`, `CompletedAt timestamptz`. Нужна, чтобы процесс Api видел результат прогрева из процесса Workers.

Порядок применения: миграция `20261120195040_WarmupState` идет после baseline, дополнений к `vrt_*` сверх этой таблицы и индекса `IX_vrt_EdgeWidth_EdgeId` нет.

## Часть 3. Тесты и критерии приемки

Unit:

- оркестратор вызывает шаги в заданном порядке и пробрасывает ошибку шага с его именем;
- при `FailFast = false` ошибка не валит хостед-сервис, при `true` валит;
- второй параллельный вызов ждет первого и не запускает второй расчет (фейки считают число вызовов);
- `WarmupResult` заполнен, `ReusedWidths` корректно отражает попадание в кэш.

Integration на живой базе (пропуск без `VRP_RMS_CONNECTION`):

- прогрев карты 801 на пустых `vrt_*` создает одну строку `vrt_MapVersion`, 365 строк ширины, ресурсы и проходимость по двум профилям;
- повторный прогрев не добавляет строк, счетчики совпадают, `ReusedWidths = true`;
- после прогрева `GetPendingMigrations()` пуст, а число записей истории равно числу миграций, включая baseline;
- `ModelSchemaTests` остается зеленым, то есть модель и DDL не разошлись после baseline.

Приемка целиком:

- `dotnet build --no-incremental` без предупреждений;
- `.\scripts\test.ps1` зеленый с переменными окружения, без пропусков;
- `migrations has-pending-model-changes` чистый;
- `POST /api/graph/warmup` на тестовой базе возвращает те же числа, что видны в `vrt_*`;
- в `AGVControl` и в таблицы карты не пишем.

## Часть 4. Границы

Не входит: планировщик A*, резервирования, MQTT и VDA, правки `AGVControl`, оптимизация производительности прогрева.

Допускается сверх baseline служебная таблица `public."vrt_WarmupState"` с ключом `MapId` и отдельная миграция с индексом `IX_vrt_EdgeWidth_EdgeId` (в базе, где `Rev4` применена до появления индекса, он создается этой миграцией). Других изменений схемы `vrt_*` на этом шаге нет.


