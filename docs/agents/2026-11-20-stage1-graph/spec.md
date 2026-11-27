# Этап 1: Загрузка графа и вывод транспортных ресурсов

## Цель
Реализовать доменную модель складского графа, загрузку карты 801 из PostgreSQL в память с версионированием, расчет ширины коридоров из растра один раз на версию графа и детерминированный вывод транспортных ресурсов и конфликтов отдельно для каждого профиля робота (`AgvProfile`). Этап 1 создает фундамент для A* и SIPP, но сам поиск путей и резервирование не реализует.

## Затронутые репозитории и проекты
- `VR.RoutePlanner` → `VR.RoutePlanner.Domain` (доменные модели, перечисления, интерфейсы)
- `VR.RoutePlanner` → `VR.RoutePlanner.Application` (интерфейс репозитория, конфигурация, сервис вывода ресурсов)
- `VR.RoutePlanner` → `VR.RoutePlanner.Infrastructure` (EF Core DbContext, реализация репозитория, кэш, миграции)
- `VR.RoutePlanner` → `VR.RoutePlanner.Tests.Unit` (тесты геометрии, классификации, детерминизма)
- `VR.RoutePlanner` → `VR.RoutePlanner.Tests.Integration` (интеграционный тест чтения карты 801)

## Границы этапа
**Входит:**
- Доменные модели `WarehouseNode`, `WarehouseEdge`, `WarehouseGraph`, `MapPoint`, `EdgeWidth`, `AgvProfile`, `TrafficResource`, `TrafficResourceEdge`, `TrafficResourceNode`, `ResourceConflict`, `ProfileResourceSet`.
- Перечисления `TrafficResourceType`, `CorridorClass`, `WidthSource`.
- Интерфейс `IWarehouseGraphRepository` и его EF Core реализация.
- Кэш графа в памяти с версией `GraphVersion`.
- Расчет ширины коридора из растрового изображения препятствий с distance transform, один раз на `GraphVersion`, с сохранением в `vrt_EdgeWidth`.
- Алгоритм вывода ресурсов из `Edges`, узлов и геометрии `Trajectory`, отдельно по каждому профилю `Routing:AgvProfiles`.
- Классификация коридоров C1..C5 по формуле `Nmax = floor((W+S)/(B+S))`, где `B` ширина профиля, и порог проходимости `W >= B + 2*S`.
- Формирование `ResourceConflict` для пересекающихся ресурсов одного профиля.
- EF Core миграции с историей `__EFMigrationsHistory_RoutePlanner` и идемпотентный SQL для ручного применения.
- Конфигурация профилей роботов и параметров геометрии с валидацией на старте.
- Unit- и integration-тесты.

**Осознанно не входит:**
- A* и SIPP (поиск путей).
- Резервирование ресурсов во времени (Reservation Engine).
- HTTP API, MQTT, VDA 5050, Kafka.
- Управление роботами и диспетчеризация.
- Автоматическая миграция продуктивной базы.
- Переиспользование или копирование кода AGVControl.
- Чтение таблицы `public."Agvs"` и любая логика массы, нагрузки и грузоподъемности.

## Доменная модель

### Идентификаторы
В БД существуют `uuid Id` (внутренний первичный ключ) и строковые `NodeId`/`EdgeId` (внешние идентификаторы VDA). Доменная модель использует оба: `Guid Id` для связей внутри БД и `string NodeId`/`EdgeId` для обмена с VDA. Для карты разведены два поля: `Guid MapKey` (uuid из `Maps.Id`) и `string MapId` (внешний идентификатор, например `'801'`). Отображение: `Id` → `NodeId`/`EdgeId` при исходящих сообщениях, `NodeId`/`EdgeId` → `Id` при входящих. В доменных моделях хранятся оба поля, связь по `Id`.

### Типы (пространство имен `VR.RoutePlanner.Domain.Graph`)

```csharp
public sealed record MapPoint(double X, double Y, double? Z);

public sealed record WarehouseNode(
    Guid Id,
    string NodeId,
    Guid MapKey,
    double X,
    double Y,
    double Theta,
    double? AllowedDeviationXY,
    double? AllowedDeviationTheta,
    string NodeType,
    Guid? RackId,
    double? Z,
    bool IsActive);

public sealed record WarehouseEdge(
    Guid Id,
    string EdgeId,
    Guid FromNodeId,
    Guid ToNodeId,
    Guid MapKey,
    double Length,
    double MaxSpeed,
    bool Bidirectional,
    string? Corridor,
    double? MaxRotationSpeed,
    double? Orientation,
    string? OrientationType,
    bool? RotationAllowed,
    IReadOnlyList<MapPoint> TrajectoryPoints,
    string? TrajectoryRaw,
    bool IsActive);

public sealed class WarehouseGraph
{
    public Guid MapKey { get; init; }
    public string MapId { get; init; } = string.Empty;
    public string MapVersion { get; init; } = string.Empty;
    public IReadOnlyDictionary<Guid, WarehouseNode> Nodes { get; init; } = new Dictionary<Guid, WarehouseNode>();
    public IReadOnlyDictionary<Guid, IReadOnlyList<WarehouseEdge>> Edges { get; init; } = new Dictionary<Guid, IReadOnlyList<WarehouseEdge>>();
}

public sealed record AgvProfile(
    string Name,
    double WidthMeters,
    double LengthMeters,
    double HeightMeters)
{
    public double FootprintRadius => Math.Sqrt(LengthMeters * LengthMeters + WidthMeters * WidthMeters) / 2;
}

public sealed record EdgeWidth(
    Guid MapVersionId,
    Guid EdgeId,
    double WidthMeters,
    double LengthMeters,
    int SampledPoints);
```

`AgvProfile` это профиль робота: `Name` уникальное имя (`tusk-forklift-1102`), `WidthMeters`/`LengthMeters`/`HeightMeters` габариты корпуса, `FootprintRadius = sqrt((L/2)^2 + (W/2)^2)` вычисляется, в этапе 1 не применяется. Массы в профиле нет: масса в этапе 1 не участвует, а `Agvs.Weight` не является снаряженной массой робота (см. раздел «Таблица `Agvs` и трактовка массы»). `EdgeWidth` это ширина коридора `W` и длина ребра, посчитанные из растра **один раз на `MapVersionId` и не зависящие от профиля**: одна строка на ребро, общая для всех роботов.

### Транспортные ресурсы (пространство имен `VR.RoutePlanner.Domain.Traffic`)

```csharp
public enum TrafficResourceType
{
    EXCLUSIVE,
    DIRECTIONAL,
    PARALLEL,
    CONFLICT_ZONE,
    INDEPENDENT
}

public enum CorridorClass
{
    C1,
    C2,
    C3,
    C4,
    C5
}

public enum WidthSource
{
    Raster,
    Manual,
    Default
}

public sealed record TrafficResource(
    Guid Id,
    Guid MapVersionId,
    string Code,
    string AgvProfile,
    TrafficResourceType ResourceType,
    int Capacity,
    double MinEntryInterval,
    double MaxOccupancyTime,
    bool IsActive);

public sealed record TrafficResourceEdge(
    Guid TrafficResourceId,
    Guid EdgeId,
    string Direction,
    int Sequence,
    string AgvProfile,
    CorridorClass Class,
    bool IsPassable);

public sealed record TrafficResourceNode(
    Guid TrafficResourceId,
    Guid NodeId,
    string AgvProfile);

public sealed record ResourceConflict(
    Guid ResourceId,
    Guid ConflictingResourceId,
    string ConflictType);
```

## Загрузка графа

### Интерфейс репозитория (пространство имен `VR.RoutePlanner.Application.Graph`)

```csharp
public interface IWarehouseGraphRepository
{
    Task<WarehouseGraph> LoadAsync(string mapId, CancellationToken ct);
    Task<string> GetGraphVersionAsync(string mapId, CancellationToken ct);
    Task InvalidateAsync(string mapId, CancellationToken ct);
}
```

### Реализация в Infrastructure
- `WarehouseGraphRepository` использует `RoutePlannerDbContext` (EF Core + Npgsql).
- Чтение таблиц `Maps`, `Nodes`, `Edges` только на чтение, без записи.
- **Активность данных:** в запросах `Nodes` и `Edges` фильтр `IsActive = true`. В `Maps` фильтр `IsActive = true`.
- **Несколько активных карт:** если для `MapId` найдено несколько активных записей, выбирается запись с максимальным `MapVersion` (семантическое сравнение), остальные игнорируются с предупреждением в лог. Если активных записей нет, `LoadAsync` бросает `InvalidOperationException`.
- Загрузка в память: `WarehouseGraph` кэшируется в `IMemoryCache` или singleton-сервисе.
- `GraphVersion` = `MapVersion` + SHA256 от отсортированного списка `NodeId` и `EdgeId`. Хеш считается при загрузке.
- `InvalidateAsync` сбрасывает кэш для указанного `mapId`.
- При старте приложения прогревается кэш для активной карты.
- Поиск путей (будущий A*) не обращается к БД на каждом шаге, использует только кэш.
- `Trajectory` разбирается при загрузке в `IReadOnlyList<MapPoint>`. Если JSON невалиден, `TrajectoryPoints` пуст, `TrajectoryRaw` сохраняется для диагностики, в лог пишется предупреждение.

### Схема БД (существующие таблицы, только чтение)
- `Maps`: `Id uuid`, `MapId varchar`, `MapVersion varchar`, `FacilityName`, `Width double`, `Height double`, `OriginX double`, `OriginY double`, `Resolution double`, `ImageUrl`, `MapDownloadLink`, `IsActive boolean`.
- `Nodes`: `Id uuid`, `NodeId varchar`, `MapId uuid`, `PositionX double`, `PositionY double`, `Theta double`, `AllowedDeviationXY`, `AllowedDeviationTheta`, `NodeType varchar`, `RackId uuid`, `Z double`, `IsActive boolean`.
- `Edges`: `Id uuid`, `EdgeId varchar`, `FromNodeId uuid`, `ToNodeId uuid`, `MapId uuid`, `Length double`, `MaxSpeed double`, `Bidirectional boolean`, `Corridor varchar`, `MaxRotationSpeed double`, `Orientation double`, `OrientationType varchar`, `RotationAllowed boolean`, `Trajectory jsonb`, `IsActive boolean`.

## Расчет ширины коридора из растра

### Контракт получения растра
Интерфейс `IMapRasterProvider` (пространство имен `VR.RoutePlanner.Application.Graph`):
```csharp
public interface IMapRasterProvider
{
    Task<MapRaster> GetAsync(string mapId, CancellationToken ct);
}
```
`MapRaster` содержит `byte[] Pixels`, `int Width`, `int Height`, `double Resolution`, `double OriginX`, `double OriginY`.

Реализация `MapRasterProvider` в Infrastructure:
- Источник по умолчанию локальный: `Routing:MapRasterPath` = `/var/data/rms-map-files` (владелец `www-data`, 4.5 МБ). Имя файла не хардкодится, а берется как `Path.GetFileName(map.ImageUrl)` из `Maps.ImageUrl`; затем `Path.Combine(MapRasterPath, имя)`. Для карты 801 это `inflation_map_H80_2608281345.png`.
- `Routing:MapRasterUrl` остается запасным вариантом: если `MapRasterPath` пуст, файл скачивается по HTTP из store-map. Значение по умолчанию указывает на `localhost`.
- Подтвержденный формат файла 801: PNG, 2112 x 1472, 8-bit grayscale, non-interlaced. `Maps.Resolution = 0.04`, `Maps.Width = 84.48`, `Maps.Height = 58.88`; `2112 * 0.04 = 84.48`, `1472 * 0.04 = 58.88`, то есть перевод пиксель → метры: `x = OriginX + col * Resolution`, `y = OriginY + row * Resolution`.
- В каталоге также лежит базовая карта `implant_map_200826.png` (8448 x 6144, RGBA, 0.01 м/пиксель) и другие inflation-карты (Z78, Z80, H80 с другими метками времени), плюс zip-архивы. Поэтому имя файла всегда определяется через `Maps.ImageUrl`, а не выбирается по маске.
- Проверка на старте в режиме `Raster`: файл существует, формат grayscale PNG, `Width * Resolution` и `Height * Resolution` совпадают с `Maps.Width` и `Maps.Height` с допуском 1 пиксель. При расхождении — `InvalidOperationException` с фактическими и ожидаемыми значениями. Это ловит подстановку растра от другой версии карты.

### Маска препятствий и distance transform
- Маска препятствий: пиксели со значением ниже `Routing:ObstacleThreshold` (по умолчанию 128) считаются препятствиями.
- Карта уже инфлирована, поэтому `d` это расстояние до границы уже расширенных препятствий, а не до исходной геометрии. Ширина коридора восстанавливается как `W = 2 * (d + InflationRadius)`.
- Евклидова distance transform вычисляется один раз на `GraphVersion` и кэшируется в памяти (`IMemoryCache`).
- Для каждой точки траектории ребра вычисляется расстояние до ближайшего препятствия `d`.
- Ширина коридора: `W = 2 * (d + r_inflation)`, где `r_inflation = Routing:InflationRadius`, по умолчанию 0.4 м. Это соответствует `H80` в имени растра (80 см = 0.4 м радиуса инфляции) и **не выводится из профиля робота**: ширина коридора является свойством карты, а не робота, поэтому одна и та же для всех профилей.
- Профили роботов задаются в `Routing:AgvProfiles` (см. раздел «Конфигурация»). В этапе 1 габариты профиля используются только для классификации коридора (`Nmax`) и для порога геометрических конфликтов; ширина коридора от них не зависит. `RobotFootprintRadius = sqrt((Length / 2)^2 + (Width / 2)^2)` вычисляется в `AgvProfile`, но в этапе 1 не применяется.
- `InflationRadius` задан отдельно, потому что имя файла растра (`H80`) обозначает величину инфляции, которая не обязана совпадать с шириной робота. Если имя файла содержит `H<число>`, а `число` в сантиметрах расходится с `2 * InflationRadius * 100` более чем на 10 процентов, в лог пишется предупреждение с обоими значениями.
- Если точек траектории нет, используется `DefaultCorridorWidth` с предупреждением.

### Режим `WidthSource`
- `Raster`: ширина считается из растра. Отсутствие файла или ошибка чтения — ошибка старта (`InvalidOperationException`).
- `Manual`: числовое значение ширины берется из `Routing:ManualCorridorWidth`, либо из `Routing:ManualCorridorWidths` по ключу `Edges.Corridor` (`{ "corridor-name": 2.4 }`). Поле `Edges.Corridor` это varchar с именем коридора, а не число, поэтому как источник ширины оно используется только как метка для поиска в `ManualCorridorWidths`. Если ключ не найден, применяется `ManualCorridorWidth` с предупреждением.
- `Default`: ширина равна `Routing:DefaultCorridorWidth`. Отсутствие файла — предупреждение в лог, не ошибка.

## Алгоритм вывода ресурсов
Пространство имен `VR.RoutePlanner.Application.Traffic`, класс `TrafficResourceBuilder`.

1. **Вход:** `WarehouseGraph`, `IMapRasterProvider`, `RoutePlannerOptions`.
2. **Порядок обхода:** ребра сортируются по `EdgeId` (ordinal), затем по `Id`. Узлы сортируются по `NodeId`. Это обеспечивает детерминизм.
3. **Ширина коридора W:** рассчитывается по разделу «Расчет ширины коридора из растра» в зависимости от `WidthSource`.
4. **Пересчет длины:** длина ребра в метрах это длина дискретизированной полилинии `TrajectoryPoints`. Если полилиния пуста, длина считается по координатам узлов. Поле `Edges.Length` физично, но записано в сантиметрах: метры = `Length * 100`. Проверено на всех ребрах с `degree = 1`, отношение хорды к `Length` ровно 100.00 (`801-wa152-wa167`: 0.00999 против 1.0 м, `801-wa145-wo-0-4`: 0.049 против 4.9 м). Расхождение полученной длины с `Length * 100` больше 5 процентов логируется с `EdgeId` и обоими значениями и покрывается тестом.
5. **Классификация C1..C5 (для каждого профиля):**
   - Классификация выполняется отдельно для каждого профиля из `Routing:AgvProfiles`, потому что `Nmax` зависит от ширины робота, а `W` от профиля не зависит. Для профиля `p`: `Nmax(p) = floor((W + S) / (B(p) + S))`, где `B(p) = p.Width`, `S = SideMargin`.
   - C1: `Nmax <= 1`
   - C2: `Nmax == 2`
   - C3: `Nmax == 3`
   - C4: `Nmax == 4`
   - C5: `Nmax >= 5`
   - Пороги выносятся в конфигурацию, значения по умолчанию: 1, 2, 3, 4, 5. Пороги общие для всех профилей.
   - Если профилей в конфигурации нет, старт падает с `InvalidOperationException` (см. валидацию конфигурации).
6. **Формирование `TrafficResource` для ребер:**
   - Ресурсы строятся отдельно для каждого профиля: набор ресурсов одного профиля не пересекается с набором другого, поле `AgvProfile` содержит имя профиля.
   - Смежные ребра с одинаковым `CorridorClass` **данного профиля** объединяются в один ресурс, но **только через узел степени 2** (ровно два инцидентных активных ребра). Узел любой другой степени (перекресток, тупик, стык версий) разрывает ресурс: до него и после него это разные ресурсы. Непроходимое ребро также разрывает ресурс.
   - Обоснование: объединение через перекрестки давало один ресурс на весь склад (730 связей на профиль, все конфликты `NODE_IN_CORRIDOR` вели на него), и этап 3 не смог бы резервировать участки независимо.
   - `Code` = `"{MapId}-{AgvProfile}-{CorridorClass}-{порядковый номер}"`, порядковый номер сквозной внутри профиля.
   - `ResourceType`:
     - C1 → `EXCLUSIVE`
     - C2 → `DIRECTIONAL`
     - C3 → `PARALLEL`
     - C4 → `PARALLEL`
     - C5 → `INDEPENDENT`
   - `Capacity` = `Nmax(p)`.
   - `MinEntryInterval` и `MaxOccupancyTime` = 0 на этапе 1 (заполняются позже).
   - `IsActive` = true, `MapVersionId` = идентификатор версии графа, к которой относятся ресурсы.
7. **Формирование `TrafficResourceEdge`:**
   - Для каждого ребра ресурса создается запись с `Direction` (`"FORWARD"` и/или `"BACKWARD"`) и `Sequence` (порядковый номер в ресурсе).
   - Для одностороннего ребра (`Bidirectional = false`) создается одна запись `FORWARD`.
   - Для двунаправленного ребра (`Bidirectional = true`) создаются две записи: `FORWARD` и `BACKWARD`, по одной на направление обхода. Ключ `(TrafficResourceId, EdgeId, Direction)` уникален.
   - Unit-тест обязан проверить, что двунаправленное ребро дает ровно две записи, а одностороннее — одну.
8. **Формирование ресурсов узлов:**
   - Для каждого активного узла создается **ровно один** `TrafficResource`: `ResourceType = EXCLUSIVE`, `Code = "{MapId}-NODE-{NodeId}"`, `Capacity = 1`. Если степень узла больше 2, создается `ResourceType = CONFLICT_ZONE` с тем же правилом «один ресурс на узел» и `Code = "{MapId}-CZ-{NodeId}"`; ресурс `-NODE-` для такого узла не создается.
   - Второй ресурс на тот же узел запрещен ограничением `UNIQUE ("NodeId","AgvProfile")` (проверено на PostgreSQL: `23505 duplicate key value violates unique constraint "UQ_vrt_TrafficResourceNode_Node_Profile"`).
   - Связь с узлом через `TrafficResourceNode` создается **только** для узловых ресурсов (`...-NODE-...`, `...-CZ-...`). Коридорные ресурсы связей с узлами не создают: иначе один узел попадал бы в несколько ресурсов и нарушал `UNIQUE ("NodeId","AgvProfile")` (проверено на PostgreSQL: `23505 duplicate key value violates unique constraint "UQ_vrt_TrafficResourceNode_Node_Profile"`). Связь коридора с узлами выводится из `Edges.StartNodeId`/`EndNodeId` его ребер.
   - Это позволяет этапу 3 резервировать любой узел, включая узлы коридора, а не только перекрестки.
9. **Формирование `ResourceConflict`:**
   - Ресурсы, имеющие общие узлы или пересекающиеся геометрические области, получают запись в `ResourceConflict` с `ConflictType = "SHARED_NODE"` или `"GEOMETRIC_OVERLAP"`.
   - Конфликты строятся в двух видах: между коридорными ресурсами (`SHARED_NODE` при общем узле, `GEOMETRIC_OVERLAP` при пересечении областей) и между узловым ресурсом и каждым коридорным ресурсом, в который входит ребро, инцидентное этому узлу (`ConflictType = "NODE_IN_CORRIDOR"`). Второй вид обязателен, потому что коридорные ресурсы не содержат связей с узлами, и без него узел можно было бы одновременно занять через узловой ресурс и через коридор.
   - Конфликты сортируются по `ResourceId`, затем по `ConflictingResourceId`.

## Формат Trajectory jsonb

Проверено на базе `rms`, таблица `Edges`, карта 801, 365 ребер: `jsonb_typeof("Trajectory") = 'object'` у всех. Это не полилиния, а **NURBS-кривая**:

```json
{"degree": 3.0,
 "knotVector": [0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0, 1.0],
 "controlPoints": [{"x": 31.58, "y": 36, "weight": 1}, {"x": 31.58, "y": 37.45, "weight": 1}]}
```

Встречаются `degree = 1` (прямые участки) и `degree = 3` (кривые). Доменный тип `TrajectoryCurve` содержит `Degree`, `IReadOnlyList<double> KnotVector`, `IReadOnlyList<ControlPoint> ControlPoints` (x, y, weight) и `string? Raw` для диагностики.

**Дискретизация в полилинию.** `TrajectoryPoints` (`IReadOnlyList<MapPoint>`) получается оценкой кривой через de Boor с учетом весов, шаг не грубее `Routing:TrajectorySampleStep` (по умолчанию 0.05 м). Для `degree = 1` частный случай: кривая это ломаная, проходящая через **все** контрольные точки в порядке списка, промежуточные точки не отбрасываются (при двух точках получается отрезок). Реализация собственная, новые NuGet-зависимости не вводятся.

**Валидация кривой при загрузке:** `KnotVector.Length == ControlPoints.Length + Degree + 1`, `Degree >= 1`, knotVector неубывающий, все веса больше нуля. Нарушение это `Warning`, пустая полилиния, `TrajectoryRaw` сохраняется, загрузка графа не падает.

**Обработка отказов:**
- `Trajectory` NULL, невалидный JSON или нарушенная валидация: `TrajectoryPoints` пуст, в лог `Warning`, ширина коридора берется из `Routing:DefaultCorridorWidth`.
- Загрузка графа не падает из-за `Trajectory` ни в одном случае.

## Персистентность

Механизм: **EF Core migrations**. Порядок тот же, что в `RMS.Common` и `RMS.WMS.Common`, но реализация своя, код этих сервисов не копируется и не подключается ссылкой: репозитории независимые.

### История миграций

- Таблица истории: `__EFMigrationsHistory_RoutePlanner`, задается через `npgsql.MigrationsHistoryTable(...)` и в рантайме (`UseNpgsql`), и в design-time фабрике.
- Общая `__EFMigrationsHistory` не используется и не изменяется: она принадлежит другим сервисам в той же базе `rms` (у них `__EFMigrationsHistory_RmsCommon`, `__EFMigrationsHistory_Wms`).
- Файл миграции этапа: `VR.RoutePlanner.Infrastructure/Migrations/2026-11-20_Stage1_Graph.cs`.

### MigrationRunner

`VR.RoutePlanner.Infrastructure/Persistence/MigrationRunner.cs`, контракт `MigrateAsync(DbContext db, string historyTable, string lockKey, ILogger logger, CancellationToken ct)`.

Поведение:
1. Открыть выделенное соединение и взять `pg_advisory_lock` по ключу `rms:migrations:routeplanner` (хеш строки), с release в `finally`.
2. Создать таблицу истории, если ее нет.
3. Перенести отметки из legacy `__EFMigrationsHistory`, если такая таблица существует.
4. Вычислить ожидающие миграции, применить их по одной в транзакции, каждую записать в таблицу истории.
5. Логировать имя каждой примененной миграции и итог; любая ошибка прерывает старт сервиса.

### Применение

- Конфигурация `Database:AutoMigrate`: `true` в Development, `false` в Production. При `false` сервис только проверяет отсутствие ожидающих миграций и падает с понятным сообщением, если они есть, вместо молчаливой работы на старой схеме.
- Design-time фабрика берет строку подключения из переменной окружения `VRP_RMS_CONNECTION`, паролей в репозитории нет.
- На сервере 131 два пути: `dotnet ef database update` при разработке и идемпотентный скрипт (`dotnet ef migrations script --idempotent -o stage1.sql`, затем `psql -f stage1.sql`) для ручного применения. Адрес сервера и учетные данные берутся из окружения.

### DDL (генерируется EF Core, идемпотентный)
```sql
CREATE TABLE IF NOT EXISTS public."vrt_MapVersion" (
    "Id" uuid PRIMARY KEY,
    "MapId" varchar(128) NOT NULL,
    "MapVersion" varchar(128) NOT NULL,
    "GraphVersion" varchar(128) NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "UQ_vrt_MapVersion_MapId_GraphVersion" UNIQUE ("MapId", "GraphVersion")
);

CREATE TABLE IF NOT EXISTS public."vrt_EdgeWidth" (
    "MapVersionId" uuid NOT NULL REFERENCES public."vrt_MapVersion"("Id"),
    "EdgeId" uuid NOT NULL REFERENCES public."Edges"("Id"),
    "Width" double precision NOT NULL,
    "WidthSource" varchar(16) NOT NULL,
    "WidthSourceDetail" varchar(256) NOT NULL,
    PRIMARY KEY ("MapVersionId", "EdgeId")
);

CREATE TABLE IF NOT EXISTS public."vrt_TrafficResource" (
    "Id" uuid PRIMARY KEY,
    "MapVersionId" uuid NOT NULL REFERENCES public."vrt_MapVersion"("Id"),
    "AgvProfile" varchar(128) NOT NULL,
    "Code" varchar(256) NOT NULL,
    "ResourceType" varchar(32) NOT NULL,
    "Capacity" integer NOT NULL,
    "MinEntryInterval" double precision NOT NULL DEFAULT 0,
    "MaxOccupancyTime" double precision NOT NULL DEFAULT 0,
    "IsActive" boolean NOT NULL DEFAULT true,
    CONSTRAINT "UQ_vrt_TrafficResource_Id_Profile" UNIQUE ("Id", "AgvProfile"),
    CONSTRAINT "UQ_vrt_TrafficResource_MapVersion_Profile_Code" UNIQUE ("MapVersionId", "AgvProfile", "Code")
);

CREATE TABLE IF NOT EXISTS public."vrt_TrafficResourceEdge" (
    "TrafficResourceId" uuid NOT NULL,
    "AgvProfile" varchar(128) NOT NULL,
    "EdgeId" uuid NOT NULL REFERENCES public."Edges"("Id"),
    "Direction" varchar(16) NOT NULL,
    "Sequence" integer NOT NULL,
    "Class" integer NOT NULL,
    "IsPassable" boolean NOT NULL,
    PRIMARY KEY ("TrafficResourceId", "EdgeId", "Direction"),
    CONSTRAINT "FK_vrt_TrafficResourceEdge_Resource" FOREIGN KEY ("TrafficResourceId", "AgvProfile")
        REFERENCES public."vrt_TrafficResource"("Id", "AgvProfile"),
    CONSTRAINT "UQ_vrt_TrafficResourceEdge_Edge_Direction_Profile" UNIQUE ("EdgeId", "Direction", "AgvProfile")
);

CREATE TABLE IF NOT EXISTS public."vrt_TrafficResourceNode" (
    "TrafficResourceId" uuid NOT NULL,
    "AgvProfile" varchar(128) NOT NULL,
    "NodeId" uuid NOT NULL REFERENCES public."Nodes"("Id"),
    PRIMARY KEY ("TrafficResourceId", "NodeId"),
    CONSTRAINT "FK_vrt_TrafficResourceNode_Resource" FOREIGN KEY ("TrafficResourceId", "AgvProfile")
        REFERENCES public."vrt_TrafficResource"("Id", "AgvProfile"),
    CONSTRAINT "UQ_vrt_TrafficResourceNode_Node_Profile" UNIQUE ("NodeId", "AgvProfile")
);

CREATE TABLE IF NOT EXISTS public."vrt_ResourceConflict" (
    "ResourceId" uuid NOT NULL REFERENCES public."vrt_TrafficResource"("Id"),
    "ConflictingResourceId" uuid NOT NULL REFERENCES public."vrt_TrafficResource"("Id"),
    "ConflictType" varchar(64) NOT NULL,
    PRIMARY KEY ("ResourceId", "ConflictingResourceId")
);

CREATE INDEX IF NOT EXISTS "IX_vrt_TrafficResourceEdge_EdgeId" ON public."vrt_TrafficResourceEdge"("EdgeId");
CREATE INDEX IF NOT EXISTS "IX_vrt_TrafficResourceNode_NodeId" ON public."vrt_TrafficResourceNode"("NodeId");
CREATE INDEX IF NOT EXISTS "IX_vrt_ResourceConflict_ResourceId" ON public."vrt_ResourceConflict"("ResourceId");
```

**Ссылочная целостность:** `EdgeId` и `NodeId` в `vrt_TrafficResourceEdge` и `vrt_TrafficResourceNode` это uuid из `public."Edges"."Id"` и `public."Nodes"."Id"`. База одна, схема та же, поэтому FK добавляются (`REFERENCES public."Edges"("Id")`, `REFERENCES public."Nodes"("Id")`). Это исключает ресурсы, ссылающиеся на несуществующие ребра и узлы.

**Разбиение множеств:** ограничения `UNIQUE ("EdgeId","Direction","AgvProfile")` и `UNIQUE ("NodeId","AgvProfile")` гарантируют, что одно ребро в одном направлении и один узел принадлежат ровно одному ресурсу внутри профиля; у разных профилей наборы ресурсов независимы. Та же уникальность проверяется в коде при построении ресурсов до записи в БД: повтор ребра или узла в двух ресурсах это `InvalidOperationException` с перечнем конфликтующих ребер, а не ошибка вставки.

**Добавочная таблица на согласование владельцу:** `vrt_TrafficResourceNode` введена для резервирования узлов. Если владелец предпочитает хранить узловые ресурсы иначе, таблица исключается, а `TrafficResourceNode` остается в домене.

## Таблица `Agvs` и трактовка массы

Схема `public."Agvs"` проверена по коду AGVControl: `Id uuid`, `Manufacturer text`, `SerialNumber text`, `AgvType text`, `Kinematic text`, `CurrentState text`, `OperatingMode text`, `BatteryCharge double precision`, `BatteryVoltage double precision`, `IsCharging bool`, `IsPaused bool`, `PositionX double precision`, `PositionY double precision`, `Theta double precision`, `MapId text`, `LastNodeId text`, `CurrentOrderId text`, `Velocity double precision`, `MaxSpeed double precision`, `MaxLoadMass double precision`, `HasLoad bool`, `LoadType text`, `EStopStatus text`, `FieldViolation bool`, `LastUpdateTime timestamptz`, `ConnectionState text`, `CreatedDate timestamptz`, `UpdatedDate timestamptz`, `Weight double precision`, `Driving bool`. Габаритов робота (ширина, длина, высота) в таблице нет, поэтому `AgvProfile` берется из `Routing:AgvProfiles`, а не из БД.

Трактовка массы:
- `Agvs.Weight` это вес **текущего груза** на борту в килограммах, а не снаряженная масса робота.
- `Agvs.MaxLoadMass` это максимальная масса груза (грузоподъемность), а не масса робота.
- `Agvs.HasLoad` и `Agvs.LoadType` описывают наличие и тип груза, а не профиль робота.

Следствия для этапа 1:
1. Этап 1 не читает `Agvs` и не использует массу: ширина коридора считается только из растра и габаритов, класс C1..C5 только из ширины.
2. `AgvProfile` не содержит поля массы. `Agvs.Weight` нельзя подставлять ни в `AgvProfile`, ни в геометрию, ни в `FootprintRadius`.
3. Снаряженная масса робота не выводится из `Agvs.Weight` и `Agvs.MaxLoadMass`. Если масса понадобится в этапе 2+, она задается явно в конфигурации профиля (`TareMassKg`), а не читается из таблицы.
4. `RoutePlannerDbContext` не маппит `Agvs`; unit-тест проверяет отсутствие этой сущности в модели. Когда понадобится состояние роботов (этап 2+), `Agvs` добавляется только на чтение, без миграции и без записи.
5. `Agvs` общая таблица AGVControl: любая запись в нее из RoutePlanner запрещена.

## Конфигурация
Секция `Routing` в `src/VR.RoutePlanner.Api/appsettings.json` и `src/VR.RoutePlanner.Workers/appsettings.json` (существующие ключи сохраняются):
```json
{
  "Routing": {
    "BaseNodesCount": 40,
    "ReservationHorizonSeconds": 30,
    "ReleaseHorizonNodes": 15,
    "ReleaseHorizonSeconds": 60,
    "TrajectorySampleStep": 0.05,
    "AgvProfiles": {
      "tusk-forklift-1102": {
        "WidthMeters": 1.102,
        "LengthMeters": 1.455,
        "HeightMeters": 0.17
      }
    },
    "DefaultAgvProfile": "tusk-forklift-1102",
    "SideMargin": 0.2,
    "InflationRadius": 0.4,
    "DefaultCorridorWidth": 2.0,
    "ManualCorridorWidth": 2.0,
    "ManualCorridorWidths": {},
    "WidthSource": "Raster",
    "MapRasterPath": "/var/data/rms-map-files",
    "MapRasterUrl": "http://localhost:5247/map-files",
    "ObstacleThreshold": 128,
    "CorridorClassThresholds": {
      "C1": 1,
      "C2": 2,
      "C3": 3,
      "C4": 4,
      "C5": 5
    }
  }
}
```
Существующие ключи `BaseNodesCount`, `ReservationHorizonSeconds`, `ReleaseHorizonNodes`, `ReleaseHorizonSeconds` сохраняются со своими текущими значениями (40/30/15/60); новые ключи добавляются рядом. `MapRasterPath` по умолчанию `/var/data/rms-map-files`, имя файла всегда `Path.GetFileName(Maps.ImageUrl)`. `MapRasterUrl` это запасной вариант, он используется только если `MapRasterPath` пуст; в примере указывает на `localhost`. В продуктивной среде оба значения переопределяются конфигурацией окружения и не хардкодятся в коде.

Валидация на старте: список `AgvProfiles` не пуст, у каждого профиля `Name` непустое и уникальное (ordinal, без учета регистра), `WidthMeters > 0`, `LengthMeters > 0`, `HeightMeters > 0`, `LengthMeters >= WidthMeters`, `SideMargin >= 0`, `InflationRadius > 0`, `DefaultCorridorWidth > 0`, `ManualCorridorWidth > 0`, `TrajectorySampleStep > 0`, `ObstacleThreshold` в диапазоне 0..255, пороги строго возрастают. При нарушении — `InvalidOperationException` с понятным сообщением. Тип конфигурации: `AgvProfileOptions` (`Name`, `WidthMeters`, `LengthMeters`, `HeightMeters`) в `RoutePlannerOptions.cs`, маппится в доменный `AgvProfile` при построении `TrafficResourceBuilder`. В режиме `Raster` отсутствие файла, не-grayscale PNG или расхождение `Width * Resolution` / `Height * Resolution` с `Maps.Width` / `Maps.Height` более чем на 1 пиксель — ошибка старта с фактическими и ожидаемыми значениями.

## Критерии приёмки и тест-план

### Unit-тесты
- Геометрия: расчет `Nmax` для разных W, B, S, включая граничные значения.
- Классификация: маппинг `Nmax` в `CorridorClass` для всех порогов.
- Детерминизм: два вызова `TrafficResourceBuilder` на одном графе дают идентичные списки ресурсов и конфликтов.
- Обработка `Trajectory`: NULL, невалидный JSON, нарушение `KnotVector.Length == ControlPoints.Length + Degree + 1`, немонотонный knotVector, нулевой вес — не падает, дает пустую полилинию и `DefaultCorridorWidth`.
- Дискретизация NURBS: `degree = 1` совпадает с отрезком, `degree = 3` дает монотонную полилинию с шагом не грубее `TrajectorySampleStep`, длина полилинии близка к длине кривой.
- Пересчет длины: расхождение полученной длины с `Edges.Length * 100` больше 5 процентов логируется и покрыто тестом.
- Ресурсы узлов: каждый активный узел получает `EXCLUSIVE` с `Capacity = 1`, узлы со степенью > 2 дополнительно получают `CONFLICT_ZONE`.
- `MigrationRunner`: повторный запуск не применяет миграции дважды; параллельный запуск двух экземпляров сериализуется advisory lock; при `Database:AutoMigrate = false` и наличии ожидающих миграций старт падает с понятным сообщением. База для теста поднимается в Testcontainers PostgreSQL, при недоступном Docker тесты помечаются `Skip`.
- Валидация размеров растра: файл с `Width * Resolution` / `Height * Resolution`, совпадающими с `Maps.Width` / `Maps.Height` (в пределах 1 пикселя), принимается; файл с расхождением больше 1 пикселя вызывает `InvalidOperationException` с фактическими и ожидаемыми значениями. Тест подкладывает синтетический PNG, каталог растра не нужен.
- Модель EF: `RoutePlannerDbContext.Model` содержит только `Maps`, `Nodes`, `Edges`, `vrt_EdgeWidth`, `vrt_TrafficResource`, `vrt_TrafficResourceEdge`, `vrt_TrafficResourceNode`, `vrt_ResourceConflict`; сущности `Agvs` в модели нет. Тест не подключается к БД.

### Integration-тест
- Читает реальную карту 801 из БД (только чтение).
- Строка подключения из переменной окружения `VRP_RMS_CONNECTION`. При отсутствии переменной тест помечается `Skip`.
- Проверяет, что `LoadAsync` возвращает граф с 293 узлами и 365 ребрами.
- Проверяет, что `GraphVersion` непустой и стабилен между вызовами.
- Проверяет, что `TrafficResourceBuilder` строит ресурсы без исключений.
- Успех: все проверки пройдены, БД не изменена.

### Integration-тест на реальном растре
- Открывает `/var/data/rms-map-files/inflation_map_H80_2608281345.png` (имя файла берется из `Maps.ImageUrl` карты 801, путь к каталогу из того же ключа `Routing:MapRasterPath`).
- Строит distance transform и проверяет, что ширина коридора в известной точке попадает в ожидаемый диапазон.
- При отсутствии файла тест помечается `Skip`, а не падает.

## План проверки

Контролёр прогоняет сам, из каталога `C:\Users\User\source\repos\VR.RoutePlanner` (junction на `C:\Projects\VRouter\VR.RoutePlanner`).

```powershell
dotnet build VR.RoutePlanner.sln -c Debug --nologo
dotnet test tests\VR.RoutePlanner.Tests.Unit\VR.RoutePlanner.Tests.Unit.csproj -c Debug --nologo
# только если задана VRP_RMS_CONNECTION; иначе тесты должны быть Skipped, а не Failed
dotnet test tests\VR.RoutePlanner.Tests.Integration\VR.RoutePlanner.Tests.Integration.csproj -c Debug --nologo
```

Что контролёр обязан проверить в диффе:
1. Сборка без ошибок и без новых предупреждений уровня warning-as-error, если такие включены в репозитории.
2. Все unit-тесты зелёные, число пройденных больше нуля. Integration-тесты без `VRP_RMS_CONNECTION` имеют статус `Skipped`, а не `Failed`.
3. `git -C C:\Projects\VRouter\VR.RoutePlanner diff --stat` показывает только файлы из раздела «Список файлов». Изменений в `AGVControl` и остальных репозиториях workspace нет.
4. В `Infrastructure` нет записи в `Maps`, `Nodes`, `Edges`: поиск `SaveChanges`, `ExecuteUpdate`, `ExecuteSql`, `INSERT`, `UPDATE`, `DELETE` по каталогу `src/VR.RoutePlanner.Infrastructure` не находит операций над этими таблицами.
5. В `*.csproj` нет новых `PackageReference`, кроме уже присутствующих EF Core, Npgsql и Testcontainers.
6. SQL миграции идемпотентен, не содержит `DROP TABLE`, `DROP COLUMN` и `TRUNCATE`, история ведётся в `__EFMigrationsHistory_RoutePlanner`.
7. В модели EF нет сущности `Agvs`, в `AgvProfile` нет поля массы, а `Agvs.Weight` нигде не читается.
8. Ширина коридора не берётся из `DefaultCorridorWidth` в режиме `WidthSource = Raster`: поиск по `DefaultCorridorWidth` показывает только ветки `Manual`/`Default` и фолбэк без траектории.

## Риски и открытые вопросы
1. **Габариты робота** не подтверждены. Ширина робота берется из конфигурации (`Routing:AgvProfiles`), а не из БД. При появлении реальных габаритов потребуется обновить профиль и пересчитать классы. Профилей может быть несколько: у каждого свой набор ресурсов, поэтому добавление профиля не меняет существующие коды.
2. **Формат `Trajectory` подтвержден**: это NURBS-кривая (`degree`, `knotVector`, `controlPoints` с весами) у всех 365 ребер карты 801. Риск в другом: реализация de Boor с весами должна совпасть с той, что применяет робот. Проверка через пересчет длины: для `801-wa145-wa146` (`degree = 3`) полученная длина должна сойтись с `Length * 100` в пределах 5 процентов.
3. **Растр карты 801** подтвержден: `/var/data/rms-map-files/inflation_map_H80_2608281345.png`, PNG 2112 x 1472, 8-bit grayscale, non-interlaced. `2112 * 0.04 = 84.48` и `1472 * 0.04 = 58.88` совпадают с `Maps.Width` и `Maps.Height`. Остается риск подстановки файла от другой версии карты: его ловит валидация размеров на старте. Базовая карта `implant_map_200826.png` (8448 x 6144, RGBA, 0.01 м/пиксель) в 4 раза подробнее и для расчета ширины не используется.
4. **Единицы длины** в `Edges.Length` определены: это сантиметры, метры = `Length * 100`. Проверено на всех ребрах с `degree = 1`, отношение хорды к полю ровно 100.00. Остается риск, что для кривых `degree = 3` поле считалось по другой методике; его ловит сверка с длиной дискретизированной кривой и предупреждение при расхождении больше 5 процентов.
5. **Масса и таблица `Agvs`**: `Agvs.Weight` это вес текущего груза, а не снаряженная масса робота, и габаритов робота в таблице нет. Этап 1 массу не использует. Риск: при этапе 2+ кто-то подставит `Agvs.Weight` или `Agvs.MaxLoadMass` в массу робота и исказит динамику. Митигация: поле массы в `AgvProfile` не заводится, `Agvs` не маппится, правило зафиксировано в разделе «Таблица `Agvs` и трактовка массы» и в unit-тесте модели EF.

## Список файлов
- `src/VR.RoutePlanner.Domain/Graph/MapPoint.cs`
- `src/VR.RoutePlanner.Domain/Graph/WarehouseNode.cs`
- `src/VR.RoutePlanner.Domain/Graph/WarehouseEdge.cs`
- `src/VR.RoutePlanner.Domain/Graph/WarehouseGraph.cs`
- `src/VR.RoutePlanner.Domain/Graph/AgvProfile.cs`
- `src/VR.RoutePlanner.Domain/Graph/EdgeWidth.cs`
- `src/VR.RoutePlanner.Domain/Traffic/TrafficResourceType.cs`
- `src/VR.RoutePlanner.Domain/Traffic/CorridorClass.cs`
- `src/VR.RoutePlanner.Domain/Traffic/WidthSource.cs`
- `src/VR.RoutePlanner.Domain/Traffic/TrafficResource.cs`
- `src/VR.RoutePlanner.Domain/Traffic/TrafficResourceEdge.cs`
- `src/VR.RoutePlanner.Domain/Graph/TrajectoryCurve.cs`
- `src/VR.RoutePlanner.Application/Graph/NurbsDiscretizer.cs`
- `src/VR.RoutePlanner.Domain/Traffic/TrafficResourceNode.cs`
- `src/VR.RoutePlanner.Domain/Traffic/ResourceConflict.cs`
- `src/VR.RoutePlanner.Application/Graph/IWarehouseGraphRepository.cs`
- `src/VR.RoutePlanner.Application/Graph/IMapRasterProvider.cs`
- `src/VR.RoutePlanner.Application/Graph/MapRaster.cs`
- `src/VR.RoutePlanner.Application/Traffic/TrafficResourceBuilder.cs`
- `src/VR.RoutePlanner.Application/Configuration/RoutePlannerOptions.cs`
- `src/VR.RoutePlanner.Infrastructure/Persistence/RoutePlannerDbContext.cs`
- `src/VR.RoutePlanner.Infrastructure/Persistence/WarehouseGraphRepository.cs`
- `src/VR.RoutePlanner.Infrastructure/Graph/MapRasterProvider.cs`
- `src/VR.RoutePlanner.Infrastructure/Persistence/MigrationRunner.cs`
- `src/VR.RoutePlanner.Infrastructure/Persistence/DesignTimeDbContextFactory.cs`
- `src/VR.RoutePlanner.Infrastructure/Migrations/2026-11-20_Stage1_Graph.cs`
- `tests/VR.RoutePlanner.Tests.Unit/Graph/TrafficResourceBuilderTests.cs`
- `tests/VR.RoutePlanner.Tests.Unit/Graph/GeometryTests.cs`
- `tests/VR.RoutePlanner.Tests.Unit/Graph/LengthRecalculationTests.cs`
- `tests/VR.RoutePlanner.Tests.Unit/Graph/MapRasterValidationTests.cs`
- `tests/VR.RoutePlanner.Tests.Unit/Persistence/RoutePlannerDbContextModelTests.cs`
- `tests/VR.RoutePlanner.Tests.Integration/Graph/WarehouseGraphRepositoryTests.cs`
- `tests/VR.RoutePlanner.Tests.Integration/Graph/MapRasterDistanceTransformTests.cs`

## Изменения этой ревизии
1. Ширина коридора считается геометрически из растра: маска препятствий, distance transform, `W = 2 * (d + r_inflation)`, кэш на `GraphVersion`, режим `WidthSource`.
2. Разведены `Guid MapKey` и `string MapId`; `WarehouseEdge.MapId` заменен на `MapKey`.
3. Добавлены ресурсы узлов (`TrafficResourceNode`, `vrt_TrafficResourceNode`) для резервирования перекрестков.
4. `Trajectory` разбирается в `IReadOnlyList<MapPoint>`, сырой JSON сохранен для диагностики.
5. В DDL добавлены уникальности `(MapId, GraphVersion)` и `Code`, ключ `vrt_TrafficResourceEdge` включает `Direction`.
6. Выбран механизм EF Core migrations с историей `__EFMigrationsHistory_RoutePlanner` и ручным применением идемпотентного SQL на 131.
7. Конфигурация сведена в существующую секцию `Routing`.
8. Исключены `IsActive = false`; описано поведение при нескольких активных картах.
9. Требуется пересчет длины из `Trajectory` или координат узлов, расхождение логируется и тестируется.
10. Integration-тест использует `VRP_RMS_CONNECTION` и пропускается при отсутствии переменной.
11. Источник растра подтвержден: каталог `/var/data/rms-map-files`, файл `inflation_map_H80_2608281345.png` (2112 x 1472, 8-bit grayscale, 0.04 м/пиксель), совпадает с `Maps.Width`/`Maps.Height` карты 801; добавлены валидация размеров растра на старте, проверка `H<число>` в имени файла и тесты на реальном растре.
12. `Trajectory` это NURBS (`degree`, `knotVector`, `controlPoints` с весами) у всех 365 ребер карты 801, а не полилиния. Добавлены оценка кривой методом de Boor без новых зависимостей, дискретизация с шагом `TrajectorySampleStep`, валидация knotVector и весов.
13. `Edges.Length` хранит сантиметры: метры = `Length * 100`, проверено на ребрах с `degree = 1` (отношение хорды к полю ровно 100.00). Сверка длины переведена на этот множитель.
14. История миграций ведется в отдельной таблице `__EFMigrationsHistory_RoutePlanner` по образцу `RMS.Common` и `RMS.WMS.Common`: собственный `MigrationRunner` с advisory lock `rms:migrations:routeplanner`, `Database:AutoMigrate`, design-time фабрика на `VRP_RMS_CONNECTION`.
15. В список файлов добавлены `TrajectoryCurve.cs`, `NurbsDiscretizer.cs`, `MigrationRunner.cs` и design-time фабрика.
16. Робот описан профилем `AgvProfile` (`Name`, `WidthMeters`, `LengthMeters`, `HeightMeters`, вычисляемый `FootprintRadius`), конфигурация `Routing:AgvProfiles` + `Routing:DefaultAgvProfile`, скалярные `RobotWidth`/`RobotLength`/`RobotHeight` убраны. Ширина `W` считается из растра один раз на `MapVersionId` и хранится в `vrt_EdgeWidth`; классы C1..C5 и `Capacity` считаются на профиль; проходимость `W >= B + 2 * SideMargin`; ресурсы и конфликты строятся отдельно по профилю (`AgvProfile` в `vrt_TrafficResource`, `Class` и `IsPassable` в `vrt_TrafficResourceEdge`).
17. `Agvs.Weight` трактуется как вес текущего груза, а не снаряженная масса робота; `AgvProfile` без поля массы, `Agvs` не маппится, добавлен раздел «Таблица `Agvs` и трактовка массы» и unit-тест модели EF.
18. В список файлов добавлены `AgvProfile.cs` и `EdgeWidth.cs`; добавлен раздел «План проверки» с командами, которые контролёр прогоняет сам.

## Ревизия 4: разрыв ресурсов, кэш ширин, проходимость

Правки по итогам проверки на живой базе (`rms_vrp_test`, карта 801).

1. **Разрыв коридорного ресурса.** Объединение ребер в ресурс разрешено только через узел степени 2 (ровно два инцидентных активных ребра) при равном `CorridorClass` профиля. Перекресток (степень больше 2), тупик (степень 1), узел стыка версий и непроходимое ребро разрывают ресурс. Ожидаемый порядок величин для карты 801: десятки-сотни ресурсов на профиль, а не один.
2. **Идентификатор ресурса.** `DeterministicId` обязан включать `MapVersionId`: две версии одной карты с одинаковыми `Code` не должны конфликтовать по первичному ключу.
3. **Кэш ширин.** `EdgeWidthRepository.GetOrCreateAsync` возвращает сохраненные строки только если набор `WidthSource` в них совпадает с ожидаемым режимом из конфигурации (`Routing:WidthSource`) и число строк равно числу активных ребер. Иначе строки версии удаляются и ширины пересчитываются. Причина: строки с `Default` оставались навсегда и растр не применялся, хотя `WidthSource = Raster`.
4. **Проходимость выносится из связи.** Поля `Class` и `IsPassable` переносятся из `vrt_TrafficResourceEdge` в новую таблицу `vrt_EdgePassability` с ключом `("MapVersionId","AgvProfile","EdgeId")` и колонками `IsPassable boolean NOT NULL`, `CorridorClass varchar(8) NOT NULL`, `Reason varchar(64) NULL`. Непроходимое ребро не входит ни в один ресурс, но обязано иметь строку здесь: это единственный источник истины о том, что ребро исключено и почему. Причина: в связи `IsPassable` всегда была true (1460 из 1460), так как проходимые и непроходимые ребра хранятся в разных таблицах.
5. **Удаление ресурсов.** При пересчете сначала удаляются дочерние строки (`vrt_ResourceConflict`, `vrt_TrafficResourceEdge`, `vrt_TrafficResourceNode`), затем `vrt_TrafficResource`; все в одной транзакции. Внешние ключи без каскада, поэтому порядок обязателен.
6. **Проверка версии карты.** `SaveAsync` проверяет существование `MapVersionId` в `vrt_MapVersion` и падает с `InvalidOperationException` с понятным текстом вместо `23503`.
7. **ModelSnapshot не ведем.** Миграции остаются рукописным идемпотентным SQL (так же, как в `RMS.Common` и `RMS.WMS.Common`). Вместо снапшота добавляется интеграционный тест соответствия: модель против `information_schema` (колонки, типы, nullability, уникальные ограничения, индексы) по всем `vrt_*`. Расхождение модели и БД ловится тестом, а не генератором.
8. **Узкий перехват.** В `EdgeWidthRepository` вместо `catch (DbUpdateException)` ловится `PostgresException` с `SqlState = "23505"` для гонки двух экземпляров, остальные ошибки пробрасываются.