using System.Text.Json;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Infrastructure.Mqtt;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Режимы нарезки заказа <c>Routing:Order:ReleasedNodes</c> и обновления плана.
/// </summary>
public sealed class OrderUpdateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Slice_ReleasedNodesZeroOrLess_WholeRouteInOneChunk(int releasedNodes)
    {
        var chunks = OrderChunkPlanner.Slice(7, releasedNodes);

        var chunk = Assert.Single(chunks);
        Assert.Equal(0, chunk.StartIndex);
        Assert.Equal(7, chunk.NodeCount);
        Assert.Equal(6, chunk.EndIndex);
    }

    [Fact]
    public void Slice_ReleasedNodesThree_OverlapsByOneNodeAndCoversWholeRoute()
    {
        var chunks = OrderChunkPlanner.Slice(7, 3);

        Assert.Equal(3, chunks.Count);
        Assert.Equal([(0, 3), (2, 3), (4, 3)], chunks.Select(chunk => (chunk.StartIndex, chunk.NodeCount)));
        Assert.Equal(6, chunks[^1].EndIndex);
    }

    [Fact]
    public void Slice_ReleasedNodesGreaterThanRoute_SingleChunk()
    {
        Assert.Single(OrderChunkPlanner.Slice(3, 40));
    }

    [Fact]
    public void Slice_RemainderShorterThanChunk_LastChunkIsShort()
    {
        var chunks = OrderChunkPlanner.Slice(10, 3);

        Assert.Equal([(0, 3), (2, 3), (4, 3), (6, 3), (8, 2)], chunks.Select(chunk => (chunk.StartIndex, chunk.NodeCount)));
    }

    [Fact]
    public void Build_Slice_KeepsGlobalSequenceIdsAndReleasesAllNodes()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();
        var route = OrderFixtures.Route(first.EdgeId, second.EdgeId);

        var full = VdaOrderBuilder.Build(OrderFixtures.Request(route, graph));
        var tail = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            orderUpdateId: 1,
            sliceStartIndex: 1,
            sliceNodeCount: 2));

        Assert.Equal([0, 2, 4], full["nodes"]!.AsArray().Select(node => node!["sequenceId"]!.GetValue<int>()));
        Assert.Equal([1, 3], full["edges"]!.AsArray().Select(edge => edge!["sequenceId"]!.GetValue<int>()));

        var nodes = tail["nodes"]!.AsArray();
        Assert.Equal([2, 4], nodes.Select(node => node!["sequenceId"]!.GetValue<int>()));
        Assert.Equal(["wa101", "wa102"], nodes.Select(node => node!["nodeId"]!.GetValue<string>()));
        Assert.All(nodes, node => Assert.True(node!["released"]!.GetValue<bool>()));

        var edges = tail["edges"]!.AsArray();
        var edge = Assert.Single(edges);
        Assert.Equal(3, edge!["sequenceId"]!.GetValue<int>());
        Assert.True(edge["released"]!.GetValue<bool>());
    }

    [Fact]
    public void BuildUpdate_StartsAtLastNodeAndKeepsSequenceIds()
    {
        var plan = Plan(5);

        var update = VdaOrderUpdater.BuildUpdate(plan, 1, 3, 42, DateTimeOffset.UnixEpoch, "wa2", 2);

        Assert.NotNull(update);
        using var document = JsonDocument.Parse(update!);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("orderUpdateId").GetInt32());
        Assert.Equal(42, root.GetProperty("headerId").GetInt64());

        var nodes = root.GetProperty("nodes");
        Assert.Equal(3, nodes.GetArrayLength());
        Assert.Equal("wa2", nodes[0].GetProperty("nodeId").GetString());
        Assert.Equal(2, nodes[0].GetProperty("sequenceId").GetInt32());
        Assert.Equal("wa6", nodes[2].GetProperty("nodeId").GetString());
        Assert.Equal(6, nodes[2].GetProperty("sequenceId").GetInt32());
        Assert.All(
            Enumerable.Range(0, nodes.GetArrayLength()),
            index => Assert.True(nodes[index].GetProperty("released").GetBoolean()));

        var edges = root.GetProperty("edges");
        Assert.Equal(2, edges.GetArrayLength());
        Assert.Equal(3, edges[0].GetProperty("sequenceId").GetInt32());
        Assert.Equal(5, edges[1].GetProperty("sequenceId").GetInt32());
        Assert.True(edges[0].GetProperty("released").GetBoolean());
    }

    [Fact]
    public void BuildUpdate_NothingAfterReachedNode_IsNull()
    {
        var plan = Plan(3);

        Assert.Null(VdaOrderUpdater.BuildUpdate(plan, 1, 2, 42, DateTimeOffset.UnixEpoch, "wa4", 4));
    }

    [Fact]
    public void CountUnreachedReleased_CountsOnlyBasePlan()
    {
        var plan = Plan(5);

        Assert.Equal(5, VdaOrderUpdater.CountUnreachedReleased(plan, []));
        Assert.Equal(3, VdaOrderUpdater.CountUnreachedReleased(plan, ["wa0", "wa2"]));
        Assert.Equal(0, VdaOrderUpdater.CountUnreachedReleased(plan, ["wa0", "wa2", "wa4", "wa6", "wa8"]));
    }

    [Fact]
    public void CountUnreachedInChunk_UsesReleasedFromState()
    {
        var plan = Plan(6);
        RobotNodeState[] states =
        [
            new("wa0", 0, true),
            new("wa2", 2, true),
            new("wa4", 4, true),
        ];

        Assert.Equal(1, VdaOrderUpdater.CountUnreachedInChunk(plan, 2, 3, states));
        Assert.Equal(0, VdaOrderUpdater.CountUnreachedInChunk(plan, 4, 3, states));
    }

    [Fact]
    public void CountUnreachedInChunk_WithoutState_CountsChunkFromPlan()
    {
        var plan = Plan(6);

        // Кусок 3 узла от начала плана: на первом узле впереди два узла куска, на втором — один.
        Assert.Equal(2, VdaOrderUpdater.CountUnreachedInChunk(plan, 0, 3));
        Assert.Equal(1, VdaOrderUpdater.CountUnreachedInChunk(plan, 2, 3));
        Assert.Equal(0, VdaOrderUpdater.CountUnreachedInChunk(plan, 10, 3));
    }

    [Fact]
    public async Task ApplyState_BasePlanExhausted_PublishesUpdate()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = ExecutingOrder(Plan(6));
        orders.Orders[order.OrderId] = order;

        var updated = await service.ApplyStateAsync(
            State(order.OrderId, "wa2", 2, driving: true, released: [0, 2, 4]),
            CancellationToken.None);

        Assert.Equal(1, updated!.OrderUpdateId);
        Assert.Equal("wa2", updated.Nodes[0].NodeId);
        Assert.Equal(3, updated.Nodes.Count);
        Assert.Equal(RobotOrderStatuses.Executing, updated.Status);
    }

    [Fact]
    public async Task ApplyState_ManyReleasedAhead_DoesNotPublish()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = ExecutingOrder(Plan(6));
        orders.Orders[order.OrderId] = order;

        var updated = await service.ApplyStateAsync(
            State(order.OrderId, "wa0", 0, driving: true, released: [0, 2, 4]),
            CancellationToken.None);

        Assert.Equal(0, updated!.OrderUpdateId);
    }

    [Fact]
    public async Task ApplyState_NoProgress_DoesNotPublishTwice()
    {
        var (service, orders, time, _) = RobotOrderServiceTests.Create();
        var order = ExecutingOrder(Plan(6));
        orders.Orders[order.OrderId] = order;

        var first = await service.ApplyStateAsync(
            State(order.OrderId, "wa2", 2, driving: true, released: [0, 2, 4]),
            CancellationToken.None);
        Assert.Equal(1, first!.OrderUpdateId);

        time.Now = time.Now.AddSeconds(3);
        var second = await service.ApplyStateAsync(
            State(order.OrderId, "wa2", 2, driving: true, released: [0, 2, 4]),
            CancellationToken.None);

        Assert.Equal(1, second!.OrderUpdateId);
        Assert.Equal(first.PlanJson, second.PlanJson);
    }

    [Fact]
    public async Task ApplyState_Throttle_DoesNotPublishTwiceWithinTwoSeconds()
    {
        var (service, orders, time, _) = RobotOrderServiceTests.Create();
        var order = ExecutingOrder(Plan(6));
        orders.Orders[order.OrderId] = order;

        var first = await service.ApplyStateAsync(
            State(order.OrderId, "wa2", 2, driving: true, released: [0, 2, 4]),
            CancellationToken.None);
        Assert.Equal(1, first!.OrderUpdateId);

        time.Now = time.Now.AddSeconds(1);
        var second = await service.ApplyStateAsync(
            State(order.OrderId, "wa4", 4, driving: true, released: [2, 4, 6]),
            CancellationToken.None);

        Assert.Equal(1, second!.OrderUpdateId);
    }

    [Fact]
    public async Task ApplyState_MaxOrderUpdatesReached_DoesNotPublish()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = ExecutingOrder(Plan(6)) with { OrderUpdateId = 32 };
        orders.Orders[order.OrderId] = order;

        var updated = await service.ApplyStateAsync(
            State(order.OrderId, "wa2", 2, driving: true, released: [0, 2, 4]) with { OrderUpdateId = 32 },
            CancellationToken.None);

        Assert.Equal(32, updated!.OrderUpdateId);
    }

    [Fact]
    public async Task ApplyState_ReleasedNodesZero_NeverPublishes()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create(releasedNodes: 0);
        var order = ExecutingOrder(Plan(6));
        orders.Orders[order.OrderId] = order;

        var updated = await service.ApplyStateAsync(
            State(order.OrderId, "wa2", 2, driving: true, released: [0, 2, 4]) with { NewBaseRequest = true },
            CancellationToken.None);

        Assert.Equal(0, updated!.OrderUpdateId);
        Assert.Equal(order.PlanJson, updated.PlanJson);
    }

    [Fact]
    public async Task ApplyState_CompletedOrder_DoesNotPublishUpdate()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = ExecutingOrder(Plan(6));
        orders.Orders[order.OrderId] = order;

        var completed = await service.ApplyStateAsync(
            State(order.OrderId, "wa10", 10, driving: false, released: [0, 2, 4]),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Completed, completed!.Status);
        Assert.Equal(0, completed.OrderUpdateId);
    }

    private static RobotOrder ExecutingOrder(string planJson) => new(
        "ORD_04331",
        OrderTestData.Serial,
        0,
        RobotOrderStatuses.Executing,
        planJson,
        3,
        "wa10",
        null,
        new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 21, 12, 0, 1, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 21, 12, 0, 2, TimeSpan.Zero),
        null,
        null,
        [
            new RobotOrderNode("ORD_04331", 0, "wa0", true, true, 0),
            new RobotOrderNode("ORD_04331", 2, "wa2", true, true, 0),
            new RobotOrderNode("ORD_04331", 4, "wa4", true, false, 0),
            new RobotOrderNode("ORD_04331", 6, "wa6", false, false, 0),
            new RobotOrderNode("ORD_04331", 8, "wa8", false, false, 0),
            new RobotOrderNode("ORD_04331", 10, "wa10", false, false, 0),
        ]);

    /// <summary>План из <paramref name="nodeCount"/> узлов: все узлы выпущены, нумерация сквозная.</summary>
    private static string Plan(int nodeCount)
    {
        var nodes = new List<object>();
        var edges = new List<object>();
        for (var index = 0; index < nodeCount; index++)
        {
            nodes.Add(new
            {
                nodeId = $"wa{index * 2}",
                sequenceId = index * 2,
                released = true,
                nodePosition = new { x = index, y = index },
                actions = Array.Empty<object>(),
            });

            if (index > 0)
            {
                edges.Add(new
                {
                    edgeId = $"801-wa{(index - 1) * 2}-wa{index * 2}",
                    sequenceId = (index * 2) - 1,
                    startNodeId = $"wa{(index - 1) * 2}",
                    endNodeId = $"wa{index * 2}",
                    released = true,
                    trajectory = new { degree = 1 },
                    actions = Array.Empty<object>(),
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            headerId = 1,
            version = "2.1.0",
            orderId = "ORD_04331",
            orderUpdateId = 0,
            nodes,
            edges,
        });
    }

    /// <summary>
    /// Состояние робота: <paramref name="released"/> — номера узлов, которые робот считает выпущенными.
    /// </summary>
    private static RobotSnapshot State(
        string orderId,
        string lastNodeId,
        int sequence,
        bool driving,
        IReadOnlyList<int> released)
    {
        var nodes = Enumerable.Range(0, (sequence / 2) + 1)
            .Select(index => ($"wa{index * 2}", index * 2, released.Contains(index * 2)))
            .ToList();

        foreach (var releasedSequence in released.Where(item => item > sequence))
        {
            nodes.Add(($"wa{releasedSequence}", releasedSequence, true));
        }

        return OrderTestData.Snapshot(orderId, orderUpdateId: 0, driving: driving, nodes: nodes) with
        {
            LastNodeId = lastNodeId,
            LastNodeSequenceId = sequence,
        };
    }
}
