using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Traffic;
using VR.RoutePlanner.Infrastructure.Graph;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Graph;

/// <summary>
/// Проверка, что залежавшиеся ширины с источником <c>Default</c> пересчитываются,
/// когда конфигурация требует растр: иначе строки живут в базе вечно и растр не применяется.
/// </summary>
[Collection(VrtWriteCollection.Name)]
    public sealed class EdgeWidthCacheInvalidationTests
{
    /// <summary>Переменная окружения с полным путем к растру.</summary>
    public const string RasterVariable = "VRP_MAP_RASTER";

    [SkippableFact]
    public async Task GetOrCreateAsync_StaleDefaultRows_RecomputesFromRaster()
    {
        var connectionString = TestDatabase.RequireConnection();
        var rasterPath = Environment.GetEnvironmentVariable(RasterVariable);

        if (string.IsNullOrWhiteSpace(rasterPath) || !File.Exists(rasterPath))
        {
            Skip.If(true, $"Переменная {RasterVariable} не задана или файл растра не найден.");
        }

        var options = BuildOptions(Path.GetDirectoryName(Path.GetFullPath(rasterPath!))!);
        var factory = new TestDbContextFactory(TestDatabase.BuildOptions(connectionString));

        var graphRepository = new WarehouseGraphRepository(
            factory,
            new MemoryCache(new MemoryCacheOptions()),
            options,
            NullLogger<WarehouseGraphRepository>.Instance);

        var graph = await graphRepository.LoadAsync(TestDatabase.MapId, CancellationToken.None);
        var mapVersionId = await EnsureMapVersionAsync(connectionString, graph);

        await SeedStaleRowsAsync(connectionString, mapVersionId, graph);

        var builder = new TrafficResourceBuilder(
            new MapRasterProvider(
                factory,
                graphRepository,
                new MemoryCache(new MemoryCacheOptions()),
                options,
                NullLogger<MapRasterProvider>.Instance),
            options,
            NullLogger<TrafficResourceBuilder>.Instance);

        var repository = new EdgeWidthRepository(
            factory,
            builder,
            NullLogger<EdgeWidthRepository>.Instance);

        var widths = await repository.GetOrCreateAsync(
            mapVersionId,
            graph,
            TestDatabase.MapId,
            CancellationToken.None);

        var expectedEdgeCount = graph.Edges.Values
            .SelectMany(list => list)
            .Select(edge => edge.Id)
            .Distinct()
            .Count();

        Assert.Equal(expectedEdgeCount, widths.Widths.Count);
        Assert.All(widths.Widths, width => Assert.Equal(WidthSource.Raster, width.WidthSource));
        Assert.Contains(widths.Widths, width => Math.Abs(width.WidthMeters - 2.0) > 0.001);
        Assert.True(widths.Widths.Max(width => width.WidthMeters) > 2.0);

        await using var context = new RoutePlannerDbContext(TestDatabase.BuildOptions(connectionString));
        var stored = await context.EdgeWidths
            .AsNoTracking()
            .Where(record => record.MapVersionId == mapVersionId)
            .ToListAsync();

        Assert.Equal(widths.Widths.Count, stored.Count);
        Assert.All(stored, record => Assert.Equal(nameof(WidthSource.Raster), record.WidthSource, ignoreCase: true));
    }

    [SkippableFact]
    public async Task ComputeWidthsAsync_RealRaster_LeavesImpassableEdgesForWideProfile()
    {
        var connectionString = TestDatabase.RequireConnection();
        var rasterPath = Environment.GetEnvironmentVariable(RasterVariable);

        if (string.IsNullOrWhiteSpace(rasterPath) || !File.Exists(rasterPath))
        {
            Skip.If(true, $"Переменная {RasterVariable} не задана или файл растра не найден.");
        }

        var options = BuildOptions(Path.GetDirectoryName(Path.GetFullPath(rasterPath!))!);
        var factory = new TestDbContextFactory(TestDatabase.BuildOptions(connectionString));

        var graphRepository = new WarehouseGraphRepository(
            factory,
            new MemoryCache(new MemoryCacheOptions()),
            options,
            NullLogger<WarehouseGraphRepository>.Instance);

        var graph = await graphRepository.LoadAsync(TestDatabase.MapId, CancellationToken.None);

        var builder = new TrafficResourceBuilder(
            new MapRasterProvider(
                factory,
                graphRepository,
                new MemoryCache(new MemoryCacheOptions()),
                options,
                NullLogger<MapRasterProvider>.Instance),
            options,
            NullLogger<TrafficResourceBuilder>.Instance);

        var widths = await builder.ComputeWidthsAsync(graph, Guid.NewGuid(), CancellationToken.None);
        var minimumForProfile = 1.102 + (2 * options.Value.SideMargin);

        Assert.Contains(widths, width => width.WidthMeters < minimumForProfile);
    }

    private static IOptions<RoutePlannerOptions> BuildOptions(string rasterDirectory)
    {
        return Options.Create(new RoutePlannerOptions
        {
            WidthSource = WidthSource.Raster,
            MapRasterPath = rasterDirectory,
            InflationRadius = 0.4,
            ObstacleThreshold = 128,
            AgvProfiles = new Dictionary<string, AgvProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["tusk-forklift-1102"] = new()
                {
                    Name = "tusk-forklift-1102",
                    WidthMeters = 1.102,
                    LengthMeters = 1.455,
                    HeightMeters = 0.17,
                },
            },
            DefaultAgvProfile = "tusk-forklift-1102",
        });
    }

    private static async Task SeedStaleRowsAsync(string connectionString, Guid mapVersionId, WarehouseGraph graph)
    {
        await using var context = new RoutePlannerDbContext(TestDatabase.BuildOptions(connectionString));

        var existing = await context.EdgeWidths
            .Where(record => record.MapVersionId == mapVersionId)
            .ToListAsync();

        if (existing.Count > 0)
        {
            context.EdgeWidths.RemoveRange(existing);
            await context.SaveChangesAsync();
        }

        foreach (var edge in graph.Edges.Values.SelectMany(list => list).DistinctBy(edge => edge.Id))
        {
            context.EdgeWidths.Add(new EdgeWidthRecord
            {
                MapVersionId = mapVersionId,
                EdgeId = edge.Id,
                Width = 2.0,
                WidthSource = nameof(WidthSource.Default),
                WidthSourceDetail = "DefaultCorridorWidth=2.0",
            });
        }

        await context.SaveChangesAsync();
    }

    private static async Task<Guid> EnsureMapVersionAsync(string connectionString, WarehouseGraph graph)
    {
        await using var context = new RoutePlannerDbContext(TestDatabase.BuildOptions(connectionString));

        var existing = await context.MapVersions
            .AsNoTracking()
            .Where(record => record.MapId == TestDatabase.MapId && record.GraphVersion == graph.GraphVersion)
            .Select(record => record.Id)
            .FirstOrDefaultAsync();

        if (existing != Guid.Empty)
        {
            return existing;
        }

        var record = new MapVersionRecord
        {
            Id = Guid.NewGuid(),
            MapId = TestDatabase.MapId,
            MapVersion = graph.MapVersion,
            GraphVersion = graph.GraphVersion,
            CreatedAt = DateTime.UtcNow,
        };

        context.MapVersions.Add(record);
        await context.SaveChangesAsync();
        return record.Id;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<RoutePlannerDbContext>
    {
        private readonly DbContextOptions<RoutePlannerDbContext> _options;

        public TestDbContextFactory(DbContextOptions<RoutePlannerDbContext> options) => _options = options;

        public RoutePlannerDbContext CreateDbContext() => new(_options);

        public ValueTask<RoutePlannerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(CreateDbContext());
    }
}
