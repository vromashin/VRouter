using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Planning;

/// <summary>Маршрут не должен разворачиваться на одном ребре и ходить петлями.</summary>
public sealed class PlannerNoUturnTests
{
    [Fact]
    public async Task PlanAsync_DoesNotReverseOnSameEdge()
    {
        var wa1 = Guid.NewGuid();
        var wa37 = Guid.NewGuid();
        var wa38 = Guid.NewGuid();
        var wo = Guid.NewGuid();
        var e1 = PlanningFixtures.Edge(wa1, wa37, 1.0, 0, 0, 1, 0);
        var e2 = PlanningFixtures.Edge(wa37, wa38, 1.0, 0, 1, 2, 0);
        var e3 = PlanningFixtures.Edge(wa37, wo, 1.0, 0, 1, 2, 0);

        var graph = PlanningFixtures.Graph(
            [
                PlanningFixtures.Node(wa1, "wa1", 0, 0),
                PlanningFixtures.Node(wa37, "wa37", 1, 0),
                PlanningFixtures.Node(wa38, "wa38", 2, 0),
                PlanningFixtures.Node(wo, "wo-1", 1, 1),
            ],
            [e1, e2, e3]);

        var planner = PlanningTestPlanner.Build(
            graph,
            [(e1, 2.0), (e2, 2.0), (e3, 2.0)]);

        var result = await planner.PlanAsync(
            new RoutePlanRequest(PlanningFixtures.MapId, PlanningFixtures.Profile, wa1, wo, false),
            CancellationToken.None);

        Assert.True(result.Found);
        var codes = result.Route!.Segments.Select(segment => segment.EdgeCode).ToList();
        Assert.Equal(2, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }
}
