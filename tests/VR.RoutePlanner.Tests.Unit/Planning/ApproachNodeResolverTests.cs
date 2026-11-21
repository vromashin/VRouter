using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Tests.Unit.Support;

namespace VR.RoutePlanner.Tests.Unit.Planning;

public sealed class ApproachNodeResolverTests
{
    [Fact]
    public void Resolve_TargetWithoutPassableEdge_ReturnsNearestPassableNode()
    {
        var slot = GraphFixtures.Node("wo-1-1", 5.0, 0);
        var near = GraphFixtures.Node("wa1", 5.0, 2.0);
        var far = GraphFixtures.Node("wa2", 5.0, 9.0);
        var graph = GraphFixtures.Graph(new[] { slot, near, far }, Array.Empty<VR.RoutePlanner.Domain.Graph.WarehouseEdge>());

        var result = ApproachNodeResolver.Resolve(
            graph,
            new[] { near.Id, far.Id },
            slot.Id,
            25.0);

        Assert.NotNull(result);
        Assert.Equal("wa1", result!.Code);
        Assert.Equal(2.0, result.DistanceMeters, 6);
    }

    [Fact]
    public void Resolve_TargetAlreadyPassable_ReturnsNull()
    {
        var node = GraphFixtures.Node("wa1", 0, 0);
        var graph = GraphFixtures.Graph(new[] { node }, Array.Empty<VR.RoutePlanner.Domain.Graph.WarehouseEdge>());

        Assert.Null(ApproachNodeResolver.Resolve(graph, new[] { node.Id }, node.Id, 25.0));
    }

    [Fact]
    public void Resolve_NoPassableNodeInsideRadius_ReturnsNull()
    {
        var slot = GraphFixtures.Node("wo-1-1", 0, 0);
        var far = GraphFixtures.Node("wa9", 0, 100.0);
        var graph = GraphFixtures.Graph(new[] { slot, far }, Array.Empty<VR.RoutePlanner.Domain.Graph.WarehouseEdge>());

        Assert.Null(ApproachNodeResolver.Resolve(graph, new[] { far.Id }, slot.Id, 25.0));
    }

    [Fact]
    public void Resolve_EqualDistance_TieBreaksByNodeCode()
    {
        var slot = GraphFixtures.Node("wo-1-1", 0, 0);
        var b = GraphFixtures.Node("wa-b", 0, 3.0);
        var a = GraphFixtures.Node("wa-a", 3.0, 0);
        var graph = GraphFixtures.Graph(new[] { slot, a, b }, Array.Empty<VR.RoutePlanner.Domain.Graph.WarehouseEdge>());

        var result = ApproachNodeResolver.Resolve(graph, new[] { a.Id, b.Id }, slot.Id, 25.0);

        Assert.Equal("wa-a", result!.Code);
    }
}
