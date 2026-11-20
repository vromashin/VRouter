using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Tests.Unit.Support;

namespace VR.RoutePlanner.Tests.Unit.Traffic;

/// <summary>
/// Ресурсы по готовому набору ширин: растр не читается, ширина берется из набора.
/// </summary>
public sealed class TrafficResourceBuilderEdgeWidthTests
{
    private static readonly Guid MapVersionId = Guid.Parse("5c1a2b3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");

    [Fact]
    public async Task BuildAsync_WithEdgeWidths_DoesNotCallRasterProvider()
    {
        var raster = FakeMapRasterProvider.WithError(new InvalidOperationException("растр не должен читаться"));
        var builder = new TrafficResourceBuilder(raster, CreateOptions(), NullLogger<TrafficResourceBuilder>.Instance);
        var graph = Graph();
        var edgeId = graph.Edges.Values.SelectMany(list => list).Single().Id;
        var widths = new[]
        {
            new EdgeWidth(MapVersionId, edgeId, 2.0, 1.0, 2, Domain.Traffic.WidthSource.Manual, "2.0"),
        };

        var sets = await builder.BuildAsync(graph, widths, MapVersionId, CancellationToken.None);

        Assert.Equal(0, raster.Calls);
        var set = Assert.Single(sets);
        var link = Assert.Single(set.ResourceEdges, item => item.Direction == "FORWARD");
        Assert.True(link.IsPassable);
        Assert.Equal(Domain.Traffic.CorridorClass.C2, link.Class);
    }

    private static IOptions<RoutePlannerOptions> CreateOptions()
    {
        var options = new RoutePlannerOptions
        {
            WidthSource = Domain.Traffic.WidthSource.Manual,
            ManualCorridorWidth = 2.0,
            DefaultCorridorWidth = 2.0,
            SideMargin = 0.2,
            InflationRadius = 0.4,
            AgvProfiles = TestProfiles.Create(0.6),
            DefaultAgvProfile = TestProfiles.Name,
        };

        return Options.Create(options);
    }

    private static WarehouseGraph Graph()
    {
        var first = GraphFixtures.Node("wa1", 0, 0);
        var second = GraphFixtures.Node("wa2", 1, 0);
        return GraphFixtures.Graph(new[] { first, second }, new[] { GraphFixtures.Edge(first, second, length: 1.0) });
    }
}
