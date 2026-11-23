using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Domain.Traffic;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Traffic;

/// <summary>
/// Горизонт: узлы по <c>ReleaseHorizonNodes</c>, ребра до последнего узла горизонта,
/// правило <c>sequenceId = 2 * index</c>, независимые и узловые эксклюзивные ресурсы.
/// </summary>
public sealed class TrafficHorizonCalculatorTests
{
    [Fact]
    public void Calculate_HorizonCoversReleaseHorizonNodesAndEdgesToItsLastNode()
    {
        var map = MapWithResources(nodeCount: 12, edgeCount: 11);
        var options = TrafficFixtures.Options(releaseHorizonNodes: 2);

        // Робот на первом узле плана: горизонт — узлы 0, 2, 4 и ребра 1, 3.
        var horizon = TrafficHorizonCalculator.Calculate(TrafficFixtures.Plan(12), 0, map, options);

        var nodes = horizon.Entries.Where(entry => entry.IsNode).ToList();
        var edges = horizon.Entries.Where(entry => !entry.IsNode).ToList();
        Assert.Equal([0, 2, 4], nodes.Select(entry => entry.SequenceId));
        Assert.Equal([1, 3], edges.Select(entry => entry.SequenceId));
        Assert.Empty(horizon.MissingEdgeIds);
    }

    [Fact]
    public void Calculate_LastNodeSequenceIdInMiddleOfPlan_SkipsPassedResources()
    {
        var map = MapWithResources(nodeCount: 12, edgeCount: 11);
        var options = TrafficFixtures.Options(releaseHorizonNodes: 2);

        var horizon = TrafficHorizonCalculator.Calculate(TrafficFixtures.Plan(12), 6, map, options);

        Assert.Equal([6, 8, 10], horizon.Entries.Where(entry => entry.IsNode).Select(entry => entry.SequenceId));
        Assert.Equal([7, 9], horizon.Entries.Where(entry => !entry.IsNode).Select(entry => entry.SequenceId));
    }

    [Fact]
    public void Calculate_LastNodeIsEndOfPlan_ReturnsEmptyHorizon()
    {
        var map = MapWithResources(nodeCount: 3, edgeCount: 2);

        var horizon = TrafficHorizonCalculator.Calculate(
            TrafficFixtures.Plan(3),
            4,
            map,
            TrafficFixtures.Options(releaseHorizonNodes: 15));

        Assert.Equal([4], horizon.Entries.Where(entry => entry.IsNode).Select(entry => entry.SequenceId));
        Assert.DoesNotContain(horizon.Entries, entry => !entry.IsNode);
    }

    [Fact]
    public void Calculate_EmptyOrMissingPlan_ReturnsEmpty()
    {
        var map = MapWithResources(nodeCount: 3, edgeCount: 2);

        Assert.Empty(TrafficHorizonCalculator.Calculate(null, 0, map, TrafficFixtures.Options()).Entries);
        Assert.Empty(TrafficHorizonCalculator.Calculate("{}", 0, map, TrafficFixtures.Options()).Entries);
        Assert.Empty(TrafficHorizonCalculator.Calculate("не json", 0, map, TrafficFixtures.Options()).Entries);
        Assert.Empty(TrafficHorizonCalculator.Calculate(TrafficFixtures.Plan(3), 0, null, TrafficFixtures.Options()).Entries);
    }

    [Fact]
    public void Calculate_EdgeWithoutResource_IsReportedNotThrown()
    {
        var map = new FakeTrafficResourceMap();
        for (var index = 0; index < 3; index++)
        {
            map.Node($"wa{index * 2}", Guid.NewGuid());
        }

        map.Edge("801-e0", Guid.NewGuid());

        var horizon = TrafficHorizonCalculator.Calculate(
            TrafficFixtures.Plan(3),
            0,
            map,
            TrafficFixtures.Options(releaseHorizonNodes: 15));

        Assert.Equal(["801-e1"], horizon.MissingEdgeIds);
        Assert.Single(horizon.Entries, entry => !entry.IsNode);
    }

    [Fact]
    public void Calculate_IndependentEdgeAndNodeExclusive_AreDropped_ConflictZoneKept()
    {
        var map = new FakeTrafficResourceMap();
        var conflictZone = Guid.NewGuid();
        var nodeExclusive = Guid.NewGuid();
        map.Node("wa0", conflictZone, TrafficResourceType.CONFLICT_ZONE);
        map.Node("wa2", nodeExclusive, TrafficResourceType.EXCLUSIVE);
        map.Edge("801-e0", Guid.NewGuid(), TrafficResourceType.INDEPENDENT);
        map.Edge("801-e1", Guid.NewGuid(), TrafficResourceType.EXCLUSIVE);

        var horizon = TrafficHorizonCalculator.Calculate(
            TrafficFixtures.Plan(4),
            0,
            map,
            TrafficFixtures.Options(releaseHorizonNodes: 15));

        // wa2 — узловой EXCLUSIVE, ребро e0 — INDEPENDENT: оба не резервируются.
        Assert.Equal(2, horizon.Entries.Count);
        Assert.Single(horizon.Entries, entry => entry.IsNode && entry.ResourceId == conflictZone);
        Assert.Single(horizon.Entries, entry => !entry.IsNode && entry.ResourceType == TrafficResourceType.EXCLUSIVE);
        Assert.DoesNotContain(horizon.Entries, entry => entry.ResourceId == nodeExclusive);
        Assert.DoesNotContain(horizon.Entries, entry => entry.ResourceType == TrafficResourceType.INDEPENDENT);
    }

    [Fact]
    public void Calculate_SameCorridorResourceForManyEdges_AppearsOnce()
    {
        var map = new FakeTrafficResourceMap();
        var corridor = Guid.NewGuid();
        map.Edge("801-e0", corridor);
        map.Edge("801-e1", corridor);
        map.Edge("801-e2", corridor);

        var horizon = TrafficHorizonCalculator.Calculate(
            TrafficFixtures.Plan(4),
            0,
            map,
            TrafficFixtures.Options(releaseHorizonNodes: 15));

        var entry = Assert.Single(horizon.Entries);
        Assert.Equal(corridor, entry.ResourceId);
        Assert.Equal(1, entry.SequenceId);
        Assert.Equal(RouteEdgeDirection.Forward, entry.Direction);
    }

    [Fact]
    public void Calculate_VirtualStartEdgeWithoutMaxSpeed_IsIgnored()
    {
        var map = new FakeTrafficResourceMap();
        for (var index = 0; index < 3; index++)
        {
            map.Node($"wa{index * 2}", Guid.NewGuid());
        }

        map.Edge("801-e0", Guid.NewGuid());

        // Виртуальное стартовое ребро VdaOrderBuilder не имеет maxSpeed и в карте не лежит.
        var plan = """
        {"nodes":[{"nodeId":"wa0","sequenceId":0},{"nodeId":"wa1","sequenceId":2}],
         "edges":[{"edgeId":"VIRTUAL_START_wa1","sequenceId":1},{"edgeId":"801-e0","sequenceId":3}]}
        """;

        var horizon = TrafficHorizonCalculator.Calculate(
            plan,
            0,
            map,
            TrafficFixtures.Options(releaseHorizonNodes: 15));

        Assert.Empty(horizon.MissingEdgeIds);
    }

    private static FakeTrafficResourceMap MapWithResources(int nodeCount, int edgeCount)
    {
        var map = new FakeTrafficResourceMap();
        for (var index = 0; index < nodeCount; index++)
        {
            map.Node($"wa{index * 2}", Guid.NewGuid());
        }

        for (var index = 0; index < edgeCount; index++)
        {
            map.Edge($"801-e{index}", Guid.NewGuid(), fromNodeId: $"wa{index * 2}");
        }

        return map;
    }
}
