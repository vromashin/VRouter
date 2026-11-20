using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Traffic;
using VR.RoutePlanner.Tests.Unit.Support;

namespace VR.RoutePlanner.Tests.Unit.Traffic;

/// <summary>
/// Проверки построения транспортных ресурсов на синтетическом графе.
/// </summary>
public sealed class TrafficResourceBuilderTests
{
    [Fact]
    public async Task BuildAsync_SingleBidirectionalEdge_CreatesEdgeAndNodeResources()
    {
        var graph = TwoNodeGraph();
        var builder = CreateBuilder(FakeMapRasterProvider.WithError(new InvalidOperationException("нет растра")));

        var result = await BuildAsync(builder, graph);

        Assert.Equal(3, result.Resources.Count);
        Assert.Equal(2, result.ResourceEdges.Count);
        Assert.Equal(2, result.ResourceNodes.Count);
        Assert.Contains(result.ResourceEdges, link => link.Direction == TransactionDirectionForward);
        Assert.Contains(result.ResourceEdges, link => link.Direction == TransactionDirectionBackward);
    }

    [Fact]
    public async Task BuildAsync_DefaultWidth_GivesC2DirectionalResource()
    {
        var graph = TwoNodeGraph();
        var builder = CreateBuilder(FakeMapRasterProvider.WithError(new InvalidOperationException("нет растра")));

        var result = await BuildAsync(builder, graph);

        var edgeResource = Assert.Single(result.Resources, resource => resource.ResourceType == TrafficResourceType.DIRECTIONAL);
        Assert.Contains("-C2-", edgeResource.Code, StringComparison.Ordinal);
        Assert.Equal(2, edgeResource.Capacity);
    }

    [Fact]
    public async Task BuildAsync_RasterWidth_UsesDistanceToObstacle()
    {
        var graph = TwoNodeGraph();
        var raster = FakeMapRasterProvider.CreateDefault();
        var builder = CreateBuilder(FakeMapRasterProvider.WithRaster(raster), options => options.WidthSource = WidthSource.Raster);

        var result = await BuildAsync(builder, graph);

        var edgeResource = Assert.Single(result.Resources, resource => resource.ResourceType == TrafficResourceType.PARALLEL);
        Assert.Equal(3, edgeResource.Capacity);
        Assert.Contains("-C3-", edgeResource.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_RasterWidthAndBrokenRaster_Throws()
    {
        var graph = TwoNodeGraph();
        var builder = CreateBuilder(
            FakeMapRasterProvider.WithError(new InvalidOperationException("растр от другой карты")),
            options => options.WidthSource = WidthSource.Raster);

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(graph, MapVersionId));
    }

    [Fact]
    public async Task BuildAsync_ManualWidth_UsesConfiguredWidth()
    {
        var graph = TwoNodeGraph();
        var builder = CreateBuilder(
            FakeMapRasterProvider.WithError(new InvalidOperationException("нет растра")),
            options =>
            {
                options.WidthSource = WidthSource.Manual;
                options.ManualCorridorWidth = 4.0;
            });

        var result = await BuildAsync(builder, graph);

        var edgeResource = Assert.Single(result.Resources, resource => resource.ResourceType != TrafficResourceType.EXCLUSIVE);
        Assert.Equal(4, edgeResource.Capacity);
        Assert.Contains("-C4-", edgeResource.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_RasterInflationDiffersFromRadius_LogsWarning()
    {
        // Имя растра H40, а 2 * InflationRadius = 0.8 м = 80 см: расхождение 100 процентов.
        var options = new RoutePlannerOptions
        {
            WidthSource = WidthSource.Default,
            InflationRadius = 0.4,
            AgvProfiles = TestProfiles.Create(),
            DefaultAgvProfile = "test",
        };
        var logger = new RecordingLogger<TrafficResourceBuilder>();
        var builder = new TrafficResourceBuilder(
            FakeMapRasterProvider.WithRaster(FakeMapRasterProvider.CreateDefault("inflation_map_H40_2608281345.png")),
            Options.Create(options),
            logger);

        await BuildAsync(builder, TwoNodeGraph());

        var warning = Assert.Single(
            logger.Warnings,
            message => message.Contains("InflationRadius", StringComparison.Ordinal));
        Assert.Contains("H40", warning, StringComparison.Ordinal);
        Assert.Contains("80", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_RasterInflationMatchesRadius_NoWarning()
    {
        var options = new RoutePlannerOptions
        {
            WidthSource = WidthSource.Default,
            InflationRadius = 0.4,
            AgvProfiles = TestProfiles.Create(),
            DefaultAgvProfile = "test",
        };
        var logger = new RecordingLogger<TrafficResourceBuilder>();
        var builder = new TrafficResourceBuilder(
            FakeMapRasterProvider.WithRaster(FakeMapRasterProvider.CreateDefault("inflation_map_H80.png")),
            Options.Create(options),
            logger);

        await BuildAsync(builder, TwoNodeGraph());

        Assert.DoesNotContain(logger.Warnings, message => message.Contains("InflationRadius", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_EveryNode_GetsExclusiveNodeResource()
    {
        var graph = TwoNodeGraph();
        var builder = CreateBuilder(FakeMapRasterProvider.WithError(new InvalidOperationException("нет растра")));

        var result = await BuildAsync(builder, graph);

        var nodeResources = result.Resources
            .Where(resource => resource.Code.Contains("-NODE-", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, nodeResources.Count);
        Assert.All(nodeResources, resource =>
        {
            Assert.Equal(TrafficResourceType.EXCLUSIVE, resource.ResourceType);
            Assert.Equal(1, resource.Capacity);
        });
    }

    [Fact]
    public async Task BuildAsync_NodeOfDegreeThree_GetsConflictZone()
    {
        var center = GraphFixtures.Node("wa100", 0, 0);
        var first = GraphFixtures.Node("wa101", 1, 0);
        var second = GraphFixtures.Node("wa102", 0, 1);
        var third = GraphFixtures.Node("wa103", -1, 0);
        var graph = GraphFixtures.Graph(
            new[] { center, first, second, third },
            new[]
            {
                GraphFixtures.Edge(center, first),
                GraphFixtures.Edge(center, second),
                GraphFixtures.Edge(center, third),
            });
        var builder = CreateBuilder(FakeMapRasterProvider.WithError(new InvalidOperationException("нет растра")));

        var result = await BuildAsync(builder, graph);

        var conflictZone = Assert.Single(result.Resources, resource => resource.ResourceType == TrafficResourceType.CONFLICT_ZONE);
        Assert.Contains("801-test-CZ-wa100", conflictZone.Code, StringComparison.Ordinal);
        Assert.Equal(1, conflictZone.Capacity);
    }

    [Fact]
    public async Task BuildAsync_TwoEdgesSharingNodes_ProducesSharedNodeConflict()
    {
        var first = GraphFixtures.Node("wa100", 0, 0);
        var middle = GraphFixtures.Node("wa101", 1, 0);
        var last = GraphFixtures.Node("wa102", 2, 0);
        var graph = GraphFixtures.Graph(
            new[] { first, middle, last },
            new[]
            {
                // Смежные ребра с одинаковым классом объединяются в один ресурс, поэтому
                // для конфликта нужны ребра разных классов: C1 (1.0 м) и C3 (3.0 м).
                GraphFixtures.Edge(first, middle, corridor: "narrow"),
                GraphFixtures.Edge(middle, last, corridor: "wide"),
            });
        var builder = CreateBuilder(FakeMapRasterProvider.WithError(new InvalidOperationException("нет растра")), options =>
        {
            options.WidthSource = WidthSource.Manual;
            options.ManualCorridorWidth = 1.0;
            options.ManualCorridorWidths["narrow"] = 1.0;
            options.ManualCorridorWidths["wide"] = 3.0;
        });

        var result = await BuildAsync(builder, graph);

        var sharedNode = Assert.Single(
            result.Conflicts,
            conflict => conflict.ConflictType == "SHARED_NODE");
        Assert.Contains(
            result.Resources,
            resource => resource.Id == sharedNode.ResourceId);
        Assert.Contains(
            result.Resources,
            resource => resource.Id == sharedNode.ConflictingResourceId);
        Assert.All(result.Conflicts, conflict =>
            Assert.NotEqual(conflict.ResourceId, conflict.ConflictingResourceId));
    }

    [Fact]
    public async Task BuildAsync_SameInput_ProducesSameIdentifiers()
    {
        var builder = CreateBuilder(FakeMapRasterProvider.WithError(new InvalidOperationException("нет растра")));

        var first = await BuildAsync(builder, TwoNodeGraph());
        var second = await BuildAsync(builder, TwoNodeGraph());

        Assert.Equal(
            first.Resources.Select(resource => resource.Id).ToList(),
            second.Resources.Select(resource => resource.Id).ToList());
        Assert.Equal(
            first.Resources.Select(resource => resource.Code).ToList(),
            second.Resources.Select(resource => resource.Code).ToList());
    }

    [Fact]
    public async Task BuildAsync_PointsInNeighbourCells_ProducesGeometricOverlap()
    {
        // Ребра геометрически рядом (0.1 м при ширине робота 0.8 м), но точки попадают
        // в соседние ячейки сетки: пересечение должно находиться и между ячейками.
        var first = GraphFixtures.Node("wa200", 0.0, 0.0);
        var second = GraphFixtures.Node("wa201", 0.75, 0.0);
        var third = GraphFixtures.Node("wa202", 0.85, 0.0);
        var fourth = GraphFixtures.Node("wa203", 1.6, 0.0);

        var left = GraphFixtures.Edge(first, second, bidirectional: false, length: 0.75);
        var right = GraphFixtures.Edge(third, fourth, bidirectional: false, length: 0.75);
        var graph = GraphFixtures.Graph(
            new[] { first, second, third, fourth },
            new[] { left, right });

        var builder = CreateBuilder(FakeMapRasterProvider.WithError(new InvalidOperationException("нет растра")));

        var result = await BuildAsync(builder, graph);

        var leftResource = Assert.Single(
            result.ResourceEdges,
            link => link.EdgeId == left.Id).TrafficResourceId;
        var rightResource = Assert.Single(
            result.ResourceEdges,
            link => link.EdgeId == right.Id).TrafficResourceId;

        var conflict = Assert.Single(
            result.Conflicts,
            pair => (pair.ResourceId == leftResource && pair.ConflictingResourceId == rightResource)
                || (pair.ResourceId == rightResource && pair.ConflictingResourceId == leftResource));

        Assert.Equal("GEOMETRIC_OVERLAP", conflict.ConflictType);
    }

    [Fact]
    public async Task BuildAsync_InvalidOptions_Throws()
    {
        var builder = CreateBuilder(
            FakeMapRasterProvider.WithError(new InvalidOperationException("нет растра")),
            options => options.DefaultAgvProfile = "no-such-profile");

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(TwoNodeGraph(), MapVersionId));
    }

    private const string TransactionDirectionForward = "FORWARD";
    private const string TransactionDirectionBackward = "BACKWARD";

    private static readonly Guid MapVersionId = Guid.Parse("6f1d1f60-9f4c-4a43-9c2a-0f0f3f0a1a01");

    private static async Task<ProfileResourceSet> BuildAsync(TrafficResourceBuilder builder, WarehouseGraph graph)
        => Assert.Single(await builder.BuildAsync(graph, MapVersionId));

    private static TrafficResourceBuilder CreateBuilder(
        IMapRasterProvider rasterProvider,
        Action<RoutePlannerOptions>? configure = null)
    {
        // Ширина профиля задана явно: числовые ожидания тестов посчитаны от 0.8 м.
        var options = new RoutePlannerOptions
        {
            WidthSource = WidthSource.Default,
            InflationRadius = 0.4,
            AgvProfiles = TestProfiles.Create(),
            DefaultAgvProfile = TestProfiles.Name,
        };
        configure?.Invoke(options);

        return new TrafficResourceBuilder(
            rasterProvider,
            Options.Create(options),
            NullLogger<TrafficResourceBuilder>.Instance);
    }

    private static WarehouseGraph TwoNodeGraph()
    {
        var first = GraphFixtures.Node("wa145", 0.0, 1.0);
        var second = GraphFixtures.Node("wa146", 1.0, 1.0);
        var edge = GraphFixtures.Edge(first, second, bidirectional: true, length: 1.0);

        return GraphFixtures.Graph(new[] { first, second }, new[] { edge });
    }
}
