using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Planning;

/// <summary>
/// Тесты 8-11: правило безопасности на узком коридоре, допустимость эвристики,
/// поворот по геометрии вместо theta узла.
/// </summary>
public sealed class PlannerSafetyTests
{
    [Fact]
    public async Task PlanAsync_NarrowRouteWithSafeDetour_TakesDetour()
    {
        var (planner, narrow, _, from, to) = BuildCorridor(detourX: 1);

        var result = await planner.PlanAsync(Request(from, to), CancellationToken.None);

        Assert.True(result.Found);
        Assert.NotNull(result.Route);
        Assert.False(result.PredictedRisk);
        Assert.Null(result.Reason);
        Assert.DoesNotContain(result.Route!.Segments, segment => segment.EdgeCode == narrow.EdgeId);
        Assert.All(result.Route.Segments, segment => Assert.False(segment.IsNarrow));
    }

    [Fact]
    public async Task PlanAsync_SafeDetourTooExpensive_KeepsNarrowRoute()
    {
        var (planner, narrow, _, from, to) = BuildCorridor(detourX: 16);

        var result = await planner.PlanAsync(Request(from, to), CancellationToken.None);

        Assert.True(result.Found);
        Assert.NotNull(result.Route);
        Assert.True(result.PredictedRisk);
        Assert.Equal(RoutePlanReason.SafeDetourTooExpensive, result.Reason);
        var segment = Assert.Single(result.Route!.Segments);
        Assert.Equal(narrow.EdgeId, segment.EdgeCode);
        Assert.True(segment.IsNarrow);
        Assert.True(segment.ClearanceMeters < 0.45);
    }

    [Fact]
    public async Task Heuristic_NeverExceedsRealCost()
    {
        var nodes = new List<WarehouseNode>();
        var edges = new List<WarehouseEdge>();
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                nodes.Add(PlanningFixtures.Node(Guid.NewGuid(), $"n{row}{column}", column * 2, row * 2));
            }
        }

        for (var index = 0; index < nodes.Count; index++)
        {
            var row = index / 3;
            var column = index % 3;
            if (column < 2)
            {
                edges.Add(PlanningFixtures.Edge(nodes[index].Id, nodes[index + 1].Id, 2.0, column * 2, row * 2, (column + 1) * 2, row * 2));
            }

            if (row < 2)
            {
                edges.Add(PlanningFixtures.Edge(nodes[index].Id, nodes[index + 3].Id, 2.0, column * 2, row * 2, column * 2, (row + 1) * 2));
            }
        }

        var graph = PlanningFixtures.Graph(nodes, edges);
        var planner = Build(graph, edges.Select(edge => (edge, 2.0)).ToArray(), []);
        var estimator = new HeuristicEstimator(graph.Nodes, new CostOptions().ProfileTopSpeedMetersPerSecond);
        var random = new Random(20261121);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var from = nodes[random.Next(nodes.Count)];
            var to = nodes[random.Next(nodes.Count)];
            if (from.Id == to.Id)
            {
                attempt--;
                continue;
            }

            var result = await planner.PlanAsync(Request(from.Id, to.Id), CancellationToken.None);

            Assert.True(result.Found);
            var estimate = estimator.Estimate(from.Id, to.Id);
            Assert.True(estimate >= 0);
            Assert.True(
                estimate <= result.Route!.TotalCost + 1e-6,
                $"эвристика {estimate} больше фактической стоимости {result.Route.TotalCost} для {from.NodeId}->{to.NodeId}");
        }
    }

    [Fact]
    public async Task Calculate_TurnByGeometry_UsesTrajectoryNotNodeTheta()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var first = PlanningFixtures.Edge(a, b, 2.0, 0, 0, 2, 0);
        var second = PlanningFixtures.Edge(b, c, 2.0, 2, 0, 2, 2);
        var planner = Build(
            PlanningFixtures.Graph(
                [
                    PlanningFixtures.Node(a, "A", 0, 0),
                    PlanningFixtures.Node(b, "B", 2, 0),
                    PlanningFixtures.Node(c, "C", 2, 2),
                ],
                [first, second]),
            [(first, 2.0), (second, 2.0)],
            []);

        var degrees = EdgeGeometry.TurnDegrees(first, RouteEdgeDirection.Forward, second, RouteEdgeDirection.Forward);

        Assert.Equal(90, degrees, 6);

        var result = await planner.PlanAsync(Request(a, c), CancellationToken.None);

        Assert.True(result.Found);
        Assert.Equal(1, result.Route!.TurnCount);
        Assert.NotNull(result.Explanation);
        var turn = Assert.Single(result.Explanation!.Segments, segment => segment.TurnSeconds > 0);
        Assert.Equal(90, turn.TurnDegrees, 6);
        Assert.Equal(2.0, result.Explanation.TotalTurnSeconds, 6);
    }

    private static (AStarPathPlanner Planner, WarehouseEdge Narrow, WarehouseEdge[] Wide, Guid From, Guid To) BuildCorridor(
        double detourX)
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var narrow = PlanningFixtures.Edge(a, b, 3.0, 0, 0, 0, 3);
        var first = PlanningFixtures.Edge(a, c, detourX, 0, 0, detourX, 0);
        var second = PlanningFixtures.Edge(c, b, Math.Sqrt((detourX * detourX) + 9), detourX, 0, 0, 3);
        var graph = PlanningFixtures.Graph(
            [
                PlanningFixtures.Node(a, "A", 0, 0),
                PlanningFixtures.Node(b, "B", 0, 3),
                PlanningFixtures.Node(c, "C", detourX, 0),
            ],
            [narrow, first, second]);

        var options = PlanningFixtures.Options(new CostOptions { NarrowClearanceMeters = 0.45 });
        var planner = Build(graph, [(narrow, 0.7), (first, 1.3), (second, 1.3)], [], options);

        return (planner, narrow, [first, second], a, b);
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

        return new AStarPathPlanner(
            new FakeGraphRepository(graph),
            new FakeMapVersionRepository(PlanningFixtures.MapVersionId),
            new FakeEdgeWidthRepository(widthRows),
            new FakeEdgePassabilityRepository(passabilityRows),
            new ZeroWaitEstimator(),
            new FixedOptions<RoutePlannerOptions>(options ?? PlanningFixtures.Options()),
            NullLogger<AStarPathPlanner>.Instance);
    }
}
