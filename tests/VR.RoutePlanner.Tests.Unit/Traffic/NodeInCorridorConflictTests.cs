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
/// Конфликты узловых ресурсов с коридорами инцидентных ребер (<c>NODE_IN_CORRIDOR</c>).
/// </summary>
public sealed class NodeInCorridorConflictTests
{
    private static readonly Guid MapVersionId = Guid.Parse("6f1d1f60-9f4c-4a43-9c2a-0f0f3f0a1a02");

    [Fact]
    public async Task BuildAsync_NodeWithTwoEdgesOfOneCorridor_GivesSymmetricPairs()
    {
        var first = GraphFixtures.Node("wa100", 0, 0);
        var middle = GraphFixtures.Node("wa101", 1, 0);
        var last = GraphFixtures.Node("wa102", 2, 0);
        var farFrom = GraphFixtures.Node("wo100", 0, 10);
        var farTo = GraphFixtures.Node("wo101", 1, 10);

        var graph = GraphFixtures.Graph(
            new[] { first, middle, last, farFrom, farTo },
            new[]
            {
                GraphFixtures.Edge(first, middle, corridor: "narrow"),
                GraphFixtures.Edge(middle, last, corridor: "narrow"),
                GraphFixtures.Edge(farFrom, farTo, corridor: "wide"),
            });

        var builder = CreateBuilder(options =>
        {
            options.WidthSource = WidthSource.Manual;
            options.ManualCorridorWidth = 2.0;
            options.ManualCorridorWidths = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["narrow"] = 1.4,
                ["wide"] = 3.0,
            };
            options.SideMargin = 0.2;
        });

        var result = await BuildAsync(builder, graph);

        var nodeResource = Assert.Single(
            result.Resources,
            resource => resource.Code.Contains("-NODE-wa101", StringComparison.Ordinal));
        var corridorResources = result.Resources
            .Where(resource => resource.Code.Contains("-C", StringComparison.Ordinal) && resource.Id != nodeResource.Id)
            .ToList();
        Assert.Equal(2, corridorResources.Count);

        var narrowCorridor = Assert.Single(
            corridorResources,
            resource => resource.Code.Contains("-C1-", StringComparison.Ordinal));
        var wideCorridor = Assert.Single(
            corridorResources,
            resource => resource.Code.Contains("-C3-", StringComparison.Ordinal));

        var pairs = result.Conflicts
            .Where(conflict => conflict.ResourceId == nodeResource.Id || conflict.ConflictingResourceId == nodeResource.Id)
            .ToList();

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, conflict => Assert.Equal("NODE_IN_CORRIDOR", conflict.ConflictType));
        Assert.Contains(pairs, conflict => conflict.ResourceId == nodeResource.Id && conflict.ConflictingResourceId == narrowCorridor.Id);
        Assert.Contains(pairs, conflict => conflict.ResourceId == narrowCorridor.Id && conflict.ConflictingResourceId == nodeResource.Id);

        // Коридор без инцидентных ребер этого узла в конфликте не участвует.
        Assert.DoesNotContain(pairs, conflict => conflict.ResourceId == wideCorridor.Id || conflict.ConflictingResourceId == wideCorridor.Id);
    }

    [Fact]
    public async Task BuildAsync_DenseGraph_ConflictPairsHaveNoDuplicates()
    {
        // Аналог карты 801: плотная сетка, где пересекаются все три типа конфликтов.
        var nodes = new List<WarehouseNode>();
        for (var x = 0; x < 4; x++)
        {
            for (var y = 0; y < 4; y++)
            {
                nodes.Add(GraphFixtures.Node($"wa{x}{y}", x, y));
            }
        }

        var edges = new List<WarehouseEdge>();
        for (var x = 0; x < 4; x++)
        {
            for (var y = 0; y < 4; y++)
            {
                if (x < 3)
                {
                    edges.Add(GraphFixtures.Edge(nodes[(x * 4) + y], nodes[((x + 1) * 4) + y]));
                }

                if (y < 3)
                {
                    edges.Add(GraphFixtures.Edge(nodes[(x * 4) + y], nodes[(x * 4) + y + 1]));
                }
            }
        }

        var graph = GraphFixtures.Graph(nodes, edges);
        var builder = CreateBuilder(options =>
        {
            options.WidthSource = WidthSource.Default;
            options.DefaultCorridorWidth = 2.0;
            options.SideMargin = 0.2;
        });

        var result = await BuildAsync(builder, graph);

        Assert.NotEmpty(result.Conflicts);
        var duplicates = result.Conflicts
            .GroupBy(conflict => (conflict.ResourceId, conflict.ConflictingResourceId))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.ResourceId}/{group.Key.ConflictingResourceId}")
            .ToList();

        Assert.True(duplicates.Count == 0, $"Дубли пар конфликтов: {string.Join(", ", duplicates)}");
        Assert.DoesNotContain(result.Conflicts, conflict => conflict.ResourceId == conflict.ConflictingResourceId);
        Assert.Contains(result.Conflicts, conflict => conflict.ConflictType == "NODE_IN_CORRIDOR");
    }

    private static readonly IMapRasterProvider Raster =
        FakeMapRasterProvider.WithError(new InvalidOperationException("нет растра"));

    private static TrafficResourceBuilder CreateBuilder(Action<RoutePlannerOptions> configure)
    {
        var options = new RoutePlannerOptions
        {
            AgvProfiles = TestProfiles.Create(),
            DefaultAgvProfile = TestProfiles.Name,
        };
        configure(options);

        return new TrafficResourceBuilder(
            Raster,
            Options.Create(options),
            NullLogger<TrafficResourceBuilder>.Instance);
    }

    private static async Task<ProfileResourceSet> BuildAsync(TrafficResourceBuilder builder, WarehouseGraph graph)
        => Assert.Single(await builder.BuildAsync(graph, MapVersionId));
}
