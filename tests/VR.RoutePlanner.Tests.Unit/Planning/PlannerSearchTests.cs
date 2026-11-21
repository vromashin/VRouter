using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Planning;

/// <summary>
/// Тесты 4-7: допустимость ребер, старт равный цели и отсутствие пути.
/// </summary>
public sealed class PlannerSearchTests
{
    [Fact]
    public async Task PlanAsync_ExcludesUnpassableEdge()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var direct = PlanningFixtures.Edge(a, b, 1.0, 0, 0, 1, 0);
        var first = PlanningFixtures.Edge(a, c, 3.0, 0, 0, 0, 3);
        var second = PlanningFixtures.Edge(c, b, 3.16, 0, 3, 1, 0);

        var planner = Build(
            PlanningFixtures.Graph(
                [
                    PlanningFixtures.Node(a, "A", 0, 0),
                    PlanningFixtures.Node(b, "B", 1, 0),
                    PlanningFixtures.Node(c, "C", 0, 3),
                ],
                [direct, first, second]),
            [(direct, 2.0), (first, 2.0), (second, 2.0)],
            [direct]);

        var result = await planner.PlanAsync(Request(a, b), CancellationToken.None);

        Assert.True(result.Found);
        Assert.NotNull(result.Route);
        Assert.Equal(2, result.Route!.Segments.Count);
        Assert.DoesNotContain(result.Route.Segments, segment => segment.EdgeCode == direct.EdgeId);
        Assert.Equal([first.EdgeId, second.EdgeId], result.Route.Segments.Select(segment => segment.EdgeCode));
    }

    [Fact]
    public async Task PlanAsync_StartEqualsGoal_ReturnsEmptyRoute()
    {
        var a = Guid.NewGuid();
        var edge = PlanningFixtures.Edge(a, Guid.NewGuid(), 1.0, 0, 0, 1, 0);
        var planner = Build(
            PlanningFixtures.Graph([PlanningFixtures.Node(a, "A", 0, 0), PlanningFixtures.Node(edge.ToNodeId, "B", 1, 0)], [edge]),
            [(edge, 2.0)],
            []);

        var result = await planner.PlanAsync(Request(a, a), CancellationToken.None);

        Assert.True(result.Found);
        Assert.Null(result.Reason);
        Assert.False(result.PredictedRisk);
        Assert.NotNull(result.Route);
        Assert.Empty(result.Route!.Segments);
        Assert.Equal(0, result.Route.TotalCost);
        Assert.Equal(0, result.Route.TotalLengthMeters);
        Assert.Equal(0, result.Route.TotalSeconds);
    }

    [Fact]
    public async Task PlanAsync_NoResourceForEdge_ReturnsResourceMissing()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var edge = PlanningFixtures.Edge(a, b, 1.0, 0, 0, 1, 0);
        var graph = PlanningFixtures.Graph(
            [PlanningFixtures.Node(a, "A", 0, 0), PlanningFixtures.Node(b, "B", 1, 0)],
            [edge]);

        var planner = new AStarPathPlanner(
            new FakeGraphRepository(graph),
            new FakeMapVersionRepository(PlanningFixtures.MapVersionId),
            new FakeEdgeWidthRepository([PlanningFixtures.Width(edge, 2.0)]),
            new FakeEdgePassabilityRepository(
                [PlanningFixtures.Passability(edge, PlanningFixtures.Profile, true)]),
            new FakeResourceEdgeRepository([]),
            new ZeroWaitEstimator(),
            new FixedOptions<RoutePlannerOptions>(PlanningFixtures.Options()),
            NullLogger<AStarPathPlanner>.Instance);

        var result = await planner.PlanAsync(Request(a, b), CancellationToken.None);

        Assert.False(result.Found);
        Assert.Equal(RoutePlanReason.ResourceMissing, result.Reason);
        Assert.Null(result.Route);
    }

    [Fact]
    public async Task PlanAsync_NoPath_ReturnsNotFoundWithReason()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var island = Guid.NewGuid();
        var islandPeer = Guid.NewGuid();
        var edge = PlanningFixtures.Edge(a, b, 1.0, 0, 0, 1, 0);
        var islandEdge = PlanningFixtures.Edge(island, islandPeer, 2.0, 0, 10, 12, 0);
        var planner = Build(
            PlanningFixtures.Graph(
                [
                    PlanningFixtures.Node(a, "A", 0, 0),
                    PlanningFixtures.Node(b, "B", 1, 0),
                    PlanningFixtures.Node(island, "D", 10, 10),
                    PlanningFixtures.Node(islandPeer, "E", 10, 12),
                ],
                [edge, islandEdge]),
            [(edge, 2.0), (islandEdge, 2.0)],
            []);

        var result = await planner.PlanAsync(Request(a, island), CancellationToken.None);

        Assert.False(result.Found);
        Assert.Equal(RoutePlanReason.NoPath, result.Reason);
        Assert.Null(result.Route);
    }

    [Fact]
    public async Task PlanAsync_OneWayEdge_NotUsedAgainstDirection()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var edge = PlanningFixtures.Edge(a, b, 1.0, 0, 0, 1, 0, bidirectional: false);
        var planner = Build(
            PlanningFixtures.Graph([PlanningFixtures.Node(a, "A", 0, 0), PlanningFixtures.Node(b, "B", 1, 0)], [edge]),
            [(edge, 2.0)],
            []);

        var backward = await planner.PlanAsync(Request(b, a), CancellationToken.None);
        var forward = await planner.PlanAsync(Request(a, b), CancellationToken.None);

        Assert.False(backward.Found);
        Assert.Equal(RoutePlanReason.NoPath, backward.Reason);
        Assert.True(forward.Found);
        var segment = Assert.Single(forward.Route!.Segments);
        Assert.Equal(RouteEdgeDirection.Forward, segment.Direction);
    }

    private static RoutePlanRequest Request(Guid from, Guid to) =>
        new(PlanningFixtures.MapId, PlanningFixtures.Profile, from, to, false);

    private static AStarPathPlanner Build(
        WarehouseGraph graph,
        (WarehouseEdge Edge, double Width)[] widths,
        WarehouseEdge[] impassable,
        RoutePlannerOptions? options = null)
    {
        var widthRows = widths.Select(item => PlanningFixtures.Width(item.Edge, item.Width)).ToList();
        var passabilityRows = widths
            .Where(item => !impassable.Contains(item.Edge))
            .Select(item => PlanningFixtures.Passability(item.Edge, PlanningFixtures.Profile))
            .ToList();

        var resourceRows = widths
            .SelectMany(item => PlanningFixtures.ResourceEdges(item.Edge, PlanningFixtures.Profile))
            .ToList();

        return new AStarPathPlanner(
            new FakeGraphRepository(graph),
            new FakeMapVersionRepository(PlanningFixtures.MapVersionId),
            new FakeEdgeWidthRepository(widthRows),
            new FakeEdgePassabilityRepository(passabilityRows),
            new FakeResourceEdgeRepository(resourceRows),
            new ZeroWaitEstimator(),
            new FixedOptions<RoutePlannerOptions>(options ?? PlanningFixtures.Options()),
            NullLogger<AStarPathPlanner>.Instance);
    }
}
