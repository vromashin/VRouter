using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Traffic;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Unit.Support;

namespace VR.RoutePlanner.Tests.Unit.Persistence;

/// <summary>
/// Проверки кэширования ширин в vrt_EdgeWidth: растр читается один раз на версию карты.
/// </summary>
public sealed class EdgeWidthRepositoryTests
{
    private static readonly Guid MapVersionId = Guid.Parse("2f0e0a51-6f5b-4b6e-9f3b-6a1f5c7d8e90");

    [Fact]
    public async Task GetOrCreateAsync_SecondCall_DoesNotReadRasterAgain()
    {
        using var factory = SqliteContextFactory.Create();
        var raster = FakeMapRasterProvider.WithError(new InvalidOperationException("нет растра"));
        var settings = BuildOptions();
        var builder = new TrafficResourceBuilder(raster, OptionsFactory(settings), NullLogger<TrafficResourceBuilder>.Instance);
        var repository = new EdgeWidthRepository(factory, builder, NullLogger<EdgeWidthRepository>.Instance);
        var graph = Graph();

        var first = await repository.GetOrCreateAsync(MapVersionId, graph, "801", CancellationToken.None);
        var second = await repository.GetOrCreateAsync(MapVersionId, graph, "801", CancellationToken.None);

        Assert.Single(first);
        Assert.Equal(1, raster.Calls);
        Assert.Equal(first.Select(item => item.WidthMeters), second.Select(item => item.WidthMeters));
        Assert.Equal(first.Select(item => item.EdgeId), second.Select(item => item.EdgeId));
    }

    private static RoutePlannerOptions BuildOptions() => new()
    {
        WidthSource = WidthSource.Default,
        DefaultCorridorWidth = 2.0,
        InflationRadius = 0.4,
        AgvProfiles = TestProfiles.Create(),
        DefaultAgvProfile = TestProfiles.Name,
    };

    private static IOptions<RoutePlannerOptions> OptionsFactory(RoutePlannerOptions options) => Options.Create(options);

    private static WarehouseGraph Graph()
    {
        var first = GraphFixtures.Node("wa1", 0, 0);
        var second = GraphFixtures.Node("wa2", 1, 0);
        return GraphFixtures.Graph(new[] { first, second }, new[] { GraphFixtures.Edge(first, second, length: 1.0) });
    }
}
