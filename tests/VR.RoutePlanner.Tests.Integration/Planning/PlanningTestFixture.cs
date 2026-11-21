using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Domain.Traffic;
using VR.RoutePlanner.Infrastructure.Graph;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Infrastructure.Startup;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Planning;

/// <summary>
/// Общая обвязка планировщика на живой базе: реальные репозитории и одна прогретая карта 801.
/// </summary>
internal sealed class PlanningTestFixture
{
    /// <summary>Профиль из <c>Routing:AgvProfiles</c>, которым планируют тесты.</summary>
    public const string Profile = "tusk-forklift-1102";

    /// <summary>Переменная окружения с файлом карты для первичного прогрева.</summary>
    public const string RasterVariable = "VRP_MAP_RASTER";

    private PlanningTestFixture(
        IOptions<RoutePlannerOptions> options,
        WarehouseGraph graph,
        Guid mapVersionId,
        AStarPathPlanner planner,
        IEdgePassabilityRepository passability,
        IEdgeWidthRepository widths,
        IWarehouseGraphRepository graphs,
        IMapVersionRepository versions)
    {
        Options = options;
        Graph = graph;
        MapVersionId = mapVersionId;
        Planner = planner;
        Passability = passability;
        Widths = widths;
        Graphs = graphs;
        Versions = versions;
    }

    public IOptions<RoutePlannerOptions> Options { get; }

    public WarehouseGraph Graph { get; }

    public Guid MapVersionId { get; }

    public AStarPathPlanner Planner { get; }

    public IEdgePassabilityRepository Passability { get; }

    public IEdgeWidthRepository Widths { get; }

    public IWarehouseGraphRepository Graphs { get; }

    /// <summary>Ребра карты по внешнему коду: ребро лежит в графе под обоими узлами.</summary>
    public IReadOnlyDictionary<string, WarehouseEdge> EdgesByCode =>
        _edgesByCode ??= Graph.Edges.Values
            .SelectMany(list => list)
            .DistinctBy(edge => edge.Id)
            .ToDictionary(edge => edge.EdgeId, StringComparer.Ordinal);

    /// <summary>Репозиторий версий карты.</summary>
    public IMapVersionRepository Versions { get; }

    private Dictionary<string, WarehouseEdge>? _edgesByCode;

    /// <summary>
    /// Планировщик с другим порогом узкого коридора: нужен, чтобы правило безопасности
    /// срабатывало на реальной карте при значении по умолчанию, равном нижней границе запаса.
    /// </summary>
    public AStarPathPlanner PlannerWithNarrowClearance(double narrowClearanceMeters)
    {
        var options = new RoutePlannerOptions
        {
            WidthSource = Options.Value.WidthSource,
            MapRasterPath = Options.Value.MapRasterPath,
            InflationRadius = Options.Value.InflationRadius,
            ObstacleThreshold = Options.Value.ObstacleThreshold,
            DefaultAgvProfile = Options.Value.DefaultAgvProfile,
            AgvProfiles = Options.Value.AgvProfiles,
            Cost = new CostOptions
            {
                NarrowClearanceMeters = narrowClearanceMeters,
                SafeClearanceMeters = Math.Max(narrowClearanceMeters + 0.01, Options.Value.Cost.SafeClearanceMeters),
            },
        };

        return new AStarPathPlanner(
            Graphs,
            Versions,
            Widths,
            Passability,
            new ZeroWaitEstimator(),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<AStarPathPlanner>.Instance);
    }

    /// <summary>
    /// Первая найденная пара активных узлов с маршрутом.
    /// </summary>
    public async Task<(Guid From, Guid To, RoutePlanResult Result)> FirstRouteAsync(CancellationToken ct)
    {
        var nodes = Graph.Nodes.Values.Where(node => node.IsActive).OrderBy(node => node.NodeId).ToList();

        foreach (var from in nodes)
        {
            foreach (var to in nodes.Skip(7))
            {
                if (from.Id == to.Id)
                {
                    continue;
                }

                var result = await Planner.PlanAsync(
                    new RoutePlanRequest(TestDatabase.MapId, Profile, from.Id, to.Id, false),
                    ct);

                if (result.Found && result.Route is { Segments.Count: > 0 })
                {
                    return (from.Id, to.Id, result);
                }
            }
        }

        Skip.If(true, "на карте 801 не нашлось пары узлов с маршрутом");
        return default;
    }

    /// <summary>Параметры стоимости, которыми проверяется правило безопасности.</summary>
    public RoutePlannerOptions RouteOptions => Options.Value;

    /// <summary>
    /// Готовит обвязку: прогревает карту, если по ней еще нет версии и ширин.
    /// </summary>
    public static async Task<PlanningTestFixture> CreateAsync(CancellationToken ct)
    {
        var connectionString = TestDatabase.RequireConnection();
        var options = BuildOptions();
        var factory = new TestDbContextFactory(TestDatabase.BuildOptions(connectionString));
        var graphs = new WarehouseGraphRepository(
            factory,
            new MemoryCache(new MemoryCacheOptions()),
            options,
            NullLogger<WarehouseGraphRepository>.Instance);
        var builder = new TrafficResourceBuilder(
            new MapRasterProvider(
                factory,
                graphs,
                new MemoryCache(new MemoryCacheOptions()),
                options,
                NullLogger<MapRasterProvider>.Instance),
            options,
            NullLogger<TrafficResourceBuilder>.Instance);
        var widths = new EdgeWidthRepository(factory, builder, NullLogger<EdgeWidthRepository>.Instance);
        var passability = new EdgePassabilityRepository(factory);
        var versions = new MapVersionRepository(factory);

        var graph = await graphs.LoadAsync(TestDatabase.MapId, ct);
        Assert.NotNull(graph);
        Assert.NotEmpty(graph.Nodes);

        var version = await versions.TryGetAsync(TestDatabase.MapId, graph.GraphVersion, ct);
        if (version is null || !await HasPlanningRowsAsync(widths, passability, version.Value, ct))
        {
            version = await WarmupAsync(graphs, builder, widths, versions, ct);
        }

        var planner = new AStarPathPlanner(
            graphs,
            versions,
            widths,
            passability,
            new ZeroWaitEstimator(),
            options,
            NullLogger<AStarPathPlanner>.Instance);

        return new PlanningTestFixture(options, graph, version.Value, planner, passability, widths, graphs, versions);
    }

    private static async Task<Guid> WarmupAsync(
        WarehouseGraphRepository graphs,
        TrafficResourceBuilder builder,
        EdgeWidthRepository widths,
        MapVersionRepository versions,
        CancellationToken ct)
    {
        var rasterPath = Environment.GetEnvironmentVariable(RasterVariable);
        if (string.IsNullOrWhiteSpace(rasterPath) || !File.Exists(rasterPath))
        {
            Skip.If(true, $"Нет прогретых таблиц и не задан {RasterVariable}: базу подготовить нечем.");
        }

        var service = new GraphWarmupService(
            graphs,
            versions,
            widths,
            builder,
            new ResourceRepository(new TestDbContextFactory(
                TestDatabase.BuildOptions(TestDatabase.RequireConnection()))),
            new PostgresWarmupLock(
                new TestDbContextFactory(TestDatabase.BuildOptions(TestDatabase.RequireConnection())),
                NullLogger<PostgresWarmupLock>.Instance),
            new WarmupStateRepository(new TestDbContextFactory(
                TestDatabase.BuildOptions(TestDatabase.RequireConnection()))),
            NullLogger<GraphWarmupService>.Instance);

        var result = await service.WarmupAsync(TestDatabase.MapId, ct);
        return result.MapVersionId;
    }

    private static async Task<bool> HasPlanningRowsAsync(
        IEdgeWidthRepository widths,
        IEdgePassabilityRepository passability,
        Guid mapVersionId,
        CancellationToken ct)
    {
        var loaded = await widths.ReadAsync(mapVersionId, ct);
        if (loaded.Widths.Count == 0)
        {
            return false;
        }

        var rows = await passability.GetAsync(mapVersionId, Profile, ct);
        return rows.Count > 0;
    }

    private static IOptions<RoutePlannerOptions> BuildOptions() => Microsoft.Extensions.Options.Options.Create(new RoutePlannerOptions
    {
        WidthSource = WidthSource.Raster,
        MapRasterPath = Path.GetDirectoryName(Path.GetFullPath(
            Environment.GetEnvironmentVariable(RasterVariable) ?? "map.png")),
        InflationRadius = 0.4,
        ObstacleThreshold = 128,
        DefaultAgvProfile = Profile,
        AgvProfiles = new Dictionary<string, AgvProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            [Profile] = new()
            {
                Name = Profile,
                WidthMeters = 1.102,
                LengthMeters = 1.455,
                HeightMeters = 0.17,
            },
        },
    });
}
