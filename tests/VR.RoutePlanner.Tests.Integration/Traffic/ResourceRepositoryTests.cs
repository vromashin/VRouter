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

namespace VR.RoutePlanner.Tests.Integration.Traffic;

/// <summary>
/// Сохранение транспортных ресурсов двух профилей в PostgreSQL на графе карты 801.
/// Без переменной окружения <c>VRP_RMS_CONNECTION</c> тест пропускается.
/// </summary>
public sealed class ResourceRepositoryTests
{
    private const string NarrowProfile = "narrow-0600";
    private const string WideProfile = "tusk-forklift-1102";

    [SkippableFact]
    public async Task SaveAsync_TwoProfiles_WritesRowsOnce()
    {
        var connectionString = TestDatabase.RequireConnection();
        var options = BuildOptions();
        var graphRepository = CreateGraphRepository(connectionString, options);
        var graph = await graphRepository.LoadAsync(TestDatabase.MapId, CancellationToken.None);
        var mapVersionId = await MapVersionSeed.EnsureAsync(connectionString, graph, CancellationToken.None);

        var factory = new TestDbContextFactory(TestDatabase.BuildOptions(connectionString));
        var widthRepository = new EdgeWidthRepository(
            factory,
            CreateBuilder(connectionString, graphRepository, options),
            NullLogger<EdgeWidthRepository>.Instance);

        var widths = await widthRepository.GetOrCreateAsync(
            mapVersionId,
            graph,
            TestDatabase.MapId,
            CancellationToken.None);

        var builder = CreateBuilder(connectionString, graphRepository, options);
        var sets = await builder.BuildAsync(graph, widths, mapVersionId, CancellationToken.None);

        Assert.Equal(2, sets.Count);

        var repository = new ResourceRepository(factory);
        await repository.SaveAsync(mapVersionId, sets, CancellationToken.None);

        var first = await ReadCountsAsync(factory, mapVersionId);
        Assert.True(first.Resources > 0, "После сохранения в vrt_TrafficResource нет строк.");
        Assert.Equal(2, first.Profiles);
        Assert.Equal(0, first.OrphanEdges);
        Assert.Equal(0, first.EmptyEdges);

        await repository.SaveAsync(mapVersionId, sets, CancellationToken.None);

        var second = await ReadCountsAsync(factory, mapVersionId);
        Assert.Equal(first.Resources, second.Resources);
        Assert.Equal(first.Edges, second.Edges);
        Assert.Equal(first.Nodes, second.Nodes);
        Assert.Equal(first.Conflicts, second.Conflicts);
    }

    private static async Task<TableCounts> ReadCountsAsync(
        IDbContextFactory<RoutePlannerDbContext> factory,
        Guid mapVersionId)
    {
        await using var context = await factory.CreateDbContextAsync().ConfigureAwait(false);

        var resources = await context.TrafficResources
            .AsNoTracking()
            .CountAsync(record => record.MapVersionId == mapVersionId)
            .ConfigureAwait(false);

        var profiles = await context.TrafficResources
            .AsNoTracking()
            .Where(record => record.MapVersionId == mapVersionId)
            .Select(record => record.AgvProfile)
            .Distinct()
            .CountAsync()
            .ConfigureAwait(false);

        var edges = await context.TrafficResourceEdges.AsNoTracking().CountAsync().ConfigureAwait(false);
        var nodes = await context.TrafficResourceNodes.AsNoTracking().CountAsync().ConfigureAwait(false);
        var conflicts = await context.ResourceConflicts.AsNoTracking().CountAsync().ConfigureAwait(false);

        var resourceIds = context.TrafficResources
            .AsNoTracking()
            .Where(record => record.MapVersionId == mapVersionId)
            .Select(record => record.Id);

        var orphanEdges = await context.TrafficResourceEdges
            .AsNoTracking()
            .CountAsync(
                record => !resourceIds.Contains(record.TrafficResourceId)
                    || record.TrafficResourceId == Guid.Empty)
            .ConfigureAwait(false);

        var emptyEdges = await context.TrafficResourceEdges
            .AsNoTracking()
            .CountAsync(record => record.TrafficResourceId == Guid.Empty)
            .ConfigureAwait(false);

        return new TableCounts(resources, profiles, edges, nodes, conflicts, orphanEdges, emptyEdges);
    }

    private static RoutePlannerOptions BuildOptions() => new()
    {
        WidthSource = WidthSource.Default,
        DefaultCorridorWidth = 2.0,
        SideMargin = 0.2,
        AgvProfiles = new Dictionary<string, AgvProfileOptions>(StringComparer.Ordinal)
        {
            [NarrowProfile] = new()
            {
                Name = NarrowProfile,
                WidthMeters = 0.6,
                LengthMeters = 1.0,
                HeightMeters = 0.2,
            },
            [WideProfile] = new()
            {
                Name = WideProfile,
                WidthMeters = 1.102,
                LengthMeters = 1.455,
                HeightMeters = 0.17,
            },
        },
        DefaultAgvProfile = WideProfile,
    };

    private static WarehouseGraphRepository CreateGraphRepository(string connectionString, RoutePlannerOptions options)
    {
        return new WarehouseGraphRepository(
            new TestDbContextFactory(TestDatabase.BuildOptions(connectionString)),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(options),
            NullLogger<WarehouseGraphRepository>.Instance);
    }

    private static TrafficResourceBuilder CreateBuilder(
        string connectionString,
        IWarehouseGraphRepository graphRepository,
        RoutePlannerOptions options)
    {
        var raster = new MapRasterProvider(
            new TestDbContextFactory(TestDatabase.BuildOptions(connectionString)),
            graphRepository,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(options),
            NullLogger<MapRasterProvider>.Instance);

        return new TrafficResourceBuilder(raster, Options.Create(options), NullLogger<TrafficResourceBuilder>.Instance);
    }

    private sealed record TableCounts(
        int Resources,
        int Profiles,
        int Edges,
        int Nodes,
        int Conflicts,
        int OrphanEdges,
        int EmptyEdges);
}
