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
using VR.RoutePlanner.Tests.Integration.Graph;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Traffic;

/// <summary>
/// Сохранение транспортных ресурсов двух профилей в PostgreSQL на графе карты 801.
/// Без переменной окружения <c>VRP_RMS_CONNECTION</c> тест пропускается.
/// </summary>
[Collection(VrtWriteCollection.Name)]
    public sealed class ResourceRepositoryTests
{
    private const string NarrowProfile = "narrow-0600";
    private const string WideProfile = "tusk-forklift-1102";

    [SkippableFact]
    public async Task SaveAsync_TwoProfiles_WritesRowsOnce()
    {
        var connectionString = TestDatabase.RequireConnection();
        var rasterPath = Environment.GetEnvironmentVariable(RealRasterWidthTests.RasterVariable);
        var rasterDirectory = string.IsNullOrWhiteSpace(rasterPath) || !File.Exists(rasterPath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(rasterPath));
        var options = BuildOptions(rasterDirectory);
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

        // Проходимость: строка на каждое активное ребро карты для каждого профиля (365 * 2),
        // включая непроходимые ребра, которых нет в ресурсах.
        Assert.Equal(365 * first.Profiles, first.Passability);

        // По реальному растру часть ребер уже габарита робота: широкий профиль (1.102 м)
        // обязан получить строки IsPassable = false, иначе проходимость считается не по расту.
        if (rasterDirectory is not null)
        {
            Assert.True(
                first.Impassable > 0,
                "По растру H80 ожидались непроходимые ребра, но все строки IsPassable = true.");
        }

        // Ревизия 4: ресурс рвется на каждом перекрестке, поэтому на профиль их десятки-сотни,
        // а не единицы и не тысячи.
        Assert.InRange(first.Corridors, 10, 5000);

        // Конфликты не должны повторяться и не должны ссылаться на ресурс сам на себя.
        var selfPairs = await CountSelfConflictsAsync(factory);
        Assert.Equal(0, selfPairs);

        var duplicatePairs = sets
            .SelectMany(set => set.Conflicts)
            .GroupBy(conflict => (conflict.ResourceId, conflict.ConflictingResourceId, conflict.ConflictType))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        Assert.True(duplicatePairs.Count == 0, $"В конфликтах есть дубли пар: {duplicatePairs.Count}.");

        var conflictsInSets = sets.Sum(set => set.Conflicts.Count);
        var distinctPairsInSets = sets
            .SelectMany(set => set.Conflicts)
            .Select(conflict => (conflict.ResourceId, conflict.ConflictingResourceId))
            .Distinct()
            .Count();
        Assert.Equal(conflictsInSets, distinctPairsInSets);

        await repository.SaveAsync(mapVersionId, sets, CancellationToken.None);

        var second = await ReadCountsAsync(factory, mapVersionId);
        Assert.Equal(first.Resources, second.Resources);
        Assert.Equal(first.Edges, second.Edges);
        Assert.Equal(first.Nodes, second.Nodes);
        Assert.Equal(first.Conflicts, second.Conflicts);
    }

    private static async Task<int> CountSelfConflictsAsync(IDbContextFactory<RoutePlannerDbContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync().ConfigureAwait(false);

        return await context.ResourceConflicts
            .AsNoTracking()
            .CountAsync(row => row.ResourceId == row.ConflictingResourceId)
            .ConfigureAwait(false);
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

        var passability = await context.EdgePassabilities
            .AsNoTracking()
            .Where(record => record.MapVersionId == mapVersionId)
            .CountAsync()
            .ConfigureAwait(false);

        var impassable = await context.EdgePassabilities
            .AsNoTracking()
            .Where(record => record.MapVersionId == mapVersionId && !record.IsPassable)
            .CountAsync()
            .ConfigureAwait(false);

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

        var corridors = await context.Database
            .SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM public.\"vrt_TrafficResource\" "
                + "WHERE \"MapVersionId\" = {0} AND \"Code\" ~ '-C[0-9]+-'",
                mapVersionId)
            .SingleAsync()
            .ConfigureAwait(false);

        return new TableCounts(resources, profiles, edges, nodes, conflicts, orphanEdges, emptyEdges, passability, corridors, impassable);
    }

    private static RoutePlannerOptions BuildOptions(string? rasterDirectory = null) => new()
    {
        WidthSource = rasterDirectory is null ? WidthSource.Default : WidthSource.Raster,
        MapRasterPath = rasterDirectory,
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
        int EmptyEdges,
        int Passability,
        int Corridors,
        int Impassable);
}
