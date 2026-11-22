using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Tests.Unit.Planning;

/// <summary>Сборка планировщика на фейках для тестов без базы.</summary>
public static class PlanningTestPlanner
{
    public static AStarPathPlanner Build(
        WarehouseGraph graph,
        (WarehouseEdge Edge, double Width)[] widths,
        WarehouseEdge[]? impassable = null,
        RoutePlannerOptions? options = null)
    {
        impassable ??= [];
        var blocked = new HashSet<WarehouseEdge>(impassable);
        var widthRows = widths.Select(item => PlanningFixtures.Width(item.Edge, item.Width)).ToList();
        var passabilityRows = widths
            .Where(item => !blocked.Contains(item.Edge))
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
