using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Traffic;
using VR.RoutePlanner.Infrastructure.Graph;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Startup;

/// <summary>
/// Прогрев карты 801 на живой базе: ширины, ресурсы, проходимость и идемпотентность.
/// </summary>
[Collection(VrtWriteCollection.Name)]
public sealed class GraphWarmupServiceTests
{
    /// <summary>Переменная окружения с полным путем к растру.</summary>
    public const string RasterVariable = "VRP_MAP_RASTER";

    private const string PrimaryProfile = "narrow-0900";
    private const string SecondaryProfile = "tusk-forklift-1102";

    [SkippableFact]
    public async Task WarmupAsync_EmptyTables_CalculatesWidthsResourcesAndPassability()
    {
        var connectionString = TestDatabase.RequireConnection();
        var rasterDirectory = RequireRasterDirectory();

        await CleanAsync(connectionString);
        var service = CreateService(connectionString, rasterDirectory);

        var result = await service.WarmupAsync(TestDatabase.MapId, CancellationToken.None);

        Assert.False(result.ReusedWidths);
        Assert.Equal(TestDatabase.MapId, result.MapId);
        Assert.NotEqual(Guid.Empty, result.MapVersionId);
        Assert.Equal(365, result.EdgeCount);

        await using var context = new RoutePlannerDbContext(TestDatabase.BuildOptions(connectionString));

        var versions = await context.MapVersions
            .Where(record => record.MapId == TestDatabase.MapId)
            .ToListAsync();
        Assert.Single(versions);
        Assert.Equal(result.MapVersionId, versions[0].Id);
        Assert.Equal(result.GraphVersion, versions[0].GraphVersion);

        var widthCount = await context.EdgeWidths
            .CountAsync(record => record.MapVersionId == result.MapVersionId);
        Assert.Equal(365, widthCount);
        Assert.Equal(widthCount, result.EdgeCount);

        var resources = await context.TrafficResources
            .Where(record => record.MapVersionId == result.MapVersionId)
            .GroupBy(record => record.AgvProfile)
            .Select(group => new { Profile = group.Key, Count = group.Count() })
            .ToListAsync();

        Assert.Equal(2, resources.Count);
        foreach (var item in resources)
        {
            Assert.True(item.Count > 0, $"у профиля {item.Profile} нет ресурсов");
            Assert.Equal(item.Count, result.ResourceCount[item.Profile]);
        }

        Assert.True(result.ResourceCount[PrimaryProfile] > 0);

        var passability = await context.EdgePassabilities
            .Where(record => record.MapVersionId == result.MapVersionId)
            .GroupBy(record => record.AgvProfile)
            .Select(group => new { Profile = group.Key, Impassable = group.Count(item => !item.IsPassable) })
            .ToListAsync();

        Assert.Equal(2, passability.Count);
        foreach (var item in passability)
        {
            Assert.Equal(item.Impassable, result.ImpassableCount[item.Profile]);
        }

        Assert.True(
            result.ImpassableCount[PrimaryProfile] <= result.ImpassableCount[SecondaryProfile],
            "узкий профиль не может иметь больше непроходимых ребер, чем широкий");
    }
    [SkippableFact]
    public async Task WarmupAsync_SecondRun_KeepsRowCountsAndReusesWidths()
    {
        var connectionString = TestDatabase.RequireConnection();
        var rasterDirectory = RequireRasterDirectory();

        await CleanAsync(connectionString);
        var service = CreateService(connectionString, rasterDirectory);

        var first = await service.WarmupAsync(TestDatabase.MapId, CancellationToken.None);
        var firstCounts = await CountsAsync(connectionString);
        var second = await service.WarmupAsync(TestDatabase.MapId, CancellationToken.None);
        var secondCounts = await CountsAsync(connectionString);

        Assert.True(second.ReusedWidths);
        Assert.Equal(first.MapVersionId, second.MapVersionId);
        Assert.Equal(first.EdgeCount, second.EdgeCount);
        Assert.Equal(firstCounts, secondCounts);
        Assert.Equal(1, secondCounts.MapVersions);
        Assert.Equal(365, secondCounts.EdgeWidths);
    }

    [SkippableFact]
    public async Task WarmupAsync_ThenMigrations_HaveNothingPending()
    {
        var connectionString = TestDatabase.RequireConnection();
        var rasterDirectory = RequireRasterDirectory();

        await CleanAsync(connectionString);
        var service = CreateService(connectionString, rasterDirectory);
        await service.WarmupAsync(TestDatabase.MapId, CancellationToken.None);

        await using var context = new RoutePlannerDbContext(TestDatabase.BuildOptions(connectionString));
        var pending = await context.Database.GetPendingMigrationsAsync();
        var applied = await context.Database.GetAppliedMigrationsAsync();

        Assert.Empty(pending);
        Assert.Equal(context.Database.GetMigrations().Count(), applied.Count());
        Assert.Contains(applied, id => id.EndsWith("_Baseline", StringComparison.Ordinal));
    }
    private static string RequireRasterDirectory()
    {
        var rasterPath = Environment.GetEnvironmentVariable(RasterVariable);

        if (string.IsNullOrWhiteSpace(rasterPath) || !File.Exists(rasterPath))
        {
            Skip.If(true, $"Переменная {RasterVariable} не задана или файл растра не найден.");
        }

        return Path.GetDirectoryName(Path.GetFullPath(rasterPath!))!;
    }

    private static GraphWarmupService CreateService(string connectionString, string rasterDirectory)
    {
        var options = BuildOptions(rasterDirectory);
        var factory = new TestDbContextFactory(TestDatabase.BuildOptions(connectionString));
        var graphRepository = new WarehouseGraphRepository(
            factory,
            new MemoryCache(new MemoryCacheOptions()),
            options,
            NullLogger<WarehouseGraphRepository>.Instance);
        var builder = new TrafficResourceBuilder(
            new MapRasterProvider(
                factory,
                graphRepository,
                new MemoryCache(new MemoryCacheOptions()),
                options,
                NullLogger<MapRasterProvider>.Instance),
            options,
            NullLogger<TrafficResourceBuilder>.Instance);

        return new GraphWarmupService(
            graphRepository,
            new MapVersionRepository(factory),
            new EdgeWidthRepository(factory, builder, NullLogger<EdgeWidthRepository>.Instance),
            builder,
            new ResourceRepository(factory),
            NullLogger<GraphWarmupService>.Instance);
    }

    private static IOptions<RoutePlannerOptions> BuildOptions(string rasterDirectory)
    {
        return Options.Create(new RoutePlannerOptions
        {
            WidthSource = WidthSource.Raster,
            MapRasterPath = rasterDirectory,
            InflationRadius = 0.4,
            ObstacleThreshold = 128,
            DefaultAgvProfile = PrimaryProfile,
            AgvProfiles = new Dictionary<string, AgvProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [PrimaryProfile] = new()
                {
                    Name = PrimaryProfile,
                    WidthMeters = 0.900,
                    LengthMeters = 1.200,
                    HeightMeters = 0.17,
                },
                [SecondaryProfile] = new()
                {
                    Name = SecondaryProfile,
                    WidthMeters = 1.102,
                    LengthMeters = 1.455,
                    HeightMeters = 0.17,
                },
            },
        });
    }
    private static async Task<WarmupRowCounts> CountsAsync(string connectionString)
    {
        await using var context = new RoutePlannerDbContext(TestDatabase.BuildOptions(connectionString));

        var versionIds = await context.MapVersions
            .Where(record => record.MapId == TestDatabase.MapId)
            .Select(record => record.Id)
            .ToListAsync();

        return new WarmupRowCounts(
            versionIds.Count,
            await context.EdgeWidths.CountAsync(record => versionIds.Contains(record.MapVersionId)),
            await context.TrafficResources.CountAsync(record => versionIds.Contains(record.MapVersionId)),
            await context.TrafficResourceEdges
                .CountAsync(record => context.TrafficResources
                    .Any(resource => resource.Id == record.TrafficResourceId && versionIds.Contains(resource.MapVersionId))),
            await context.EdgePassabilities.CountAsync(record => versionIds.Contains(record.MapVersionId)));
    }

    private static async Task CleanAsync(string connectionString)
    {
        await using var context = new RoutePlannerDbContext(TestDatabase.BuildOptions(connectionString));

        var versionIds = await context.MapVersions
            .Where(record => record.MapId == TestDatabase.MapId)
            .Select(record => record.Id)
            .ToListAsync();

        if (versionIds.Count > 0)
        {
            var resourceIds = await context.TrafficResources
                .Where(record => versionIds.Contains(record.MapVersionId))
                .Select(record => record.Id)
                .ToListAsync();

            await context.ResourceConflicts
                .Where(record => resourceIds.Contains(record.ResourceId))
                .ExecuteDeleteAsync();
            await context.TrafficResourceEdges
                .Where(record => resourceIds.Contains(record.TrafficResourceId))
                .ExecuteDeleteAsync();
            await context.TrafficResourceNodes
                .Where(record => resourceIds.Contains(record.TrafficResourceId))
                .ExecuteDeleteAsync();
            await context.TrafficResources
                .Where(record => versionIds.Contains(record.MapVersionId))
                .ExecuteDeleteAsync();
            await context.EdgePassabilities
                .Where(record => versionIds.Contains(record.MapVersionId))
                .ExecuteDeleteAsync();
            await context.EdgeWidths
                .Where(record => versionIds.Contains(record.MapVersionId))
                .ExecuteDeleteAsync();
        }

        await context.MapVersions
            .Where(record => record.MapId == TestDatabase.MapId)
            .ExecuteDeleteAsync();
    }

    private sealed record WarmupRowCounts(
        int MapVersions,
        int EdgeWidths,
        int Resources,
        int ResourceEdges,
        int EdgePassabilities);

    private sealed class TestDbContextFactory : IDbContextFactory<RoutePlannerDbContext>
    {
        private readonly DbContextOptions<RoutePlannerDbContext> _options;

        public TestDbContextFactory(DbContextOptions<RoutePlannerDbContext> options) => _options = options;

        public RoutePlannerDbContext CreateDbContext() => new(_options);

        public ValueTask<RoutePlannerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(CreateDbContext());
    }
}