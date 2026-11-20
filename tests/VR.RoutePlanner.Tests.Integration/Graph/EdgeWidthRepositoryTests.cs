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
/// Запись ширин в <c>public."vrt_EdgeWidth"</c> на реальной базе <c>rms</c>.
/// </summary>
public sealed class EdgeWidthRepositoryTests
{
    [SkippableFact]
    public async Task GetOrCreateAsync_PersistsRowsOnce()
    {
        var connectionString = TestDatabase.RequireConnection();
        var (graphRepository, builder) = CreateServices(connectionString);
        var graph = await graphRepository.LoadAsync(TestDatabase.MapId, CancellationToken.None);

        var mapVersionId = await EnsureMapVersionAsync(connectionString, graph);
        var repository = new EdgeWidthRepository(
            new TestDbContextFactory(TestDatabase.BuildOptions(connectionString)),
            builder,
            NullLogger<EdgeWidthRepository>.Instance);

        var first = await repository.GetOrCreateAsync(mapVersionId, graph, TestDatabase.MapId, CancellationToken.None);
        var second = await repository.GetOrCreateAsync(mapVersionId, graph, TestDatabase.MapId, CancellationToken.None);

        Assert.NotEmpty(first.Widths);
        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Equal(first.Widths.Count, second.Widths.Count);
        Assert.Equal(
            first.Widths.OrderBy(item => item.EdgeId).Select(item => item.WidthMeters),
            second.Widths.OrderBy(item => item.EdgeId).Select(item => item.WidthMeters));

        await using var context = new RoutePlannerDbContext(TestDatabase.BuildOptions(connectionString));
        var rows = await context.EdgeWidths
            .AsNoTracking()
            .Where(record => record.MapVersionId == mapVersionId)
            .ToListAsync();

        Assert.Equal(first.Widths.Count, rows.Count);
        Assert.All(rows, row => Assert.Equal(mapVersionId, row.MapVersionId));
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

    private static (IWarehouseGraphRepository GraphRepository, TrafficResourceBuilder Builder) CreateServices(
        string connectionString)
    {
        var options = Options.Create(new RoutePlannerOptions
        {
            WidthSource = WidthSource.Default,
            InflationRadius = 0.4,
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

        var factory = new TestDbContextFactory(TestDatabase.BuildOptions(connectionString));
        var graphRepository = new WarehouseGraphRepository(
            factory,
            new MemoryCache(new MemoryCacheOptions()),
            options,
            NullLogger<WarehouseGraphRepository>.Instance);
        var rasterProvider = new MapRasterProvider(
            factory,
            graphRepository,
            new MemoryCache(new MemoryCacheOptions()),
            options,
            NullLogger<MapRasterProvider>.Instance);
        var builder = new TrafficResourceBuilder(rasterProvider, options, NullLogger<TrafficResourceBuilder>.Instance);

        return (graphRepository, builder);
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
