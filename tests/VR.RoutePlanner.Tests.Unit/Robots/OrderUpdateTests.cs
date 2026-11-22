using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Infrastructure.Mqtt;
using VR.RoutePlanner.Tests.Unit.Planning;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Окно выпуска: базовый план и горизонт, обновления заказа с ростом <c>orderUpdateId</c>.
/// </summary>
public sealed class OrderUpdateTests
{
    [Fact]
    public void Build_ThreeReleasedNodes_RestIsHorizon()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();
        var order = VdaOrderBuilder.Build(
            OrderFixtures.Route(first.EdgeId, second.EdgeId),
            graph,
            "801",
            "Tuskrobots",
            "98001",
            7,
            1,
            DateTimeOffset.UnixEpoch,
            releasedNodeCount: 3);

        var nodes = order["nodes"]!.AsArray();
        var edges = order["edges"]!.AsArray();

        Assert.All(nodes, node => Assert.True(node!["released"]!.GetValue<bool>()));
        Assert.All(edges, edge => Assert.True(edge!["released"]!.GetValue<bool>()));
    }

    [Fact]
    public void Build_TwoReleasedNodes_ThirdNodeAndEdgeAreHorizon()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();
        var order = VdaOrderBuilder.Build(
            OrderFixtures.Route(first.EdgeId, second.EdgeId),
            graph,
            "801",
            "Tuskrobots",
            "98001",
            7,
            1,
            DateTimeOffset.UnixEpoch,
            releasedNodeCount: 2);

        var nodes = order["nodes"]!.AsArray();
        var edges = order["edges"]!.AsArray();

        Assert.True(nodes[0]!["released"]!.GetValue<bool>());
        Assert.True(nodes[1]!["released"]!.GetValue<bool>());
        Assert.False(nodes[2]!["released"]!.GetValue<bool>());
        Assert.True(edges[0]!["released"]!.GetValue<bool>());
        Assert.False(edges[1]!["released"]!.GetValue<bool>());
    }

    [Fact]
    public void BuildUpdate_StartsAtBaseBoundaryAndGrowsVersion()
    {
        var plan = Plan(5);

        var update = VdaOrderUpdater.BuildUpdate(plan, 1, 2, 42, DateTimeOffset.UnixEpoch, "wa2", 2);

        Assert.NotNull(update);
        using var document = JsonDocument.Parse(update!);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("orderUpdateId").GetInt32());
        Assert.Equal(42, root.GetProperty("headerId").GetInt64());

        var nodes = root.GetProperty("nodes");
        Assert.Equal(3, nodes.GetArrayLength());
        Assert.Equal("wa4", nodes[0].GetProperty("nodeId").GetString());
        Assert.Equal(4, nodes[0].GetProperty("sequenceId").GetInt32());
        Assert.True(nodes[0].GetProperty("released").GetBoolean());
        Assert.True(nodes[1].GetProperty("released").GetBoolean());
        Assert.False(nodes[2].GetProperty("released").GetBoolean());

        var edges = root.GetProperty("edges");
        Assert.Equal(2, edges.GetArrayLength());
        Assert.Equal(5, edges[0].GetProperty("sequenceId").GetInt32());
        Assert.True(edges[0].GetProperty("released").GetBoolean());
        Assert.False(edges[1].GetProperty("released").GetBoolean());
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

        Assert.Equal(3, VdaOrderUpdater.CountUnreachedReleased(plan, []));
        Assert.Equal(1, VdaOrderUpdater.CountUnreachedReleased(plan, ["wa0", "wa2"]));
        Assert.Equal(0, VdaOrderUpdater.CountUnreachedReleased(plan, ["wa0", "wa2", "wa4"]));
    }

    [Fact]
    public async Task ApplyState_BasePlanExhausted_PublishesUpdate()
    {
        var (service, orders, time, states) = RobotOrderServiceTests.Create();
        var order = ExecutingOrder(Plan(6), releasedCount: 3);
        orders.Orders[order.OrderId] = order;

        var updated = await service.ApplyStateAsync(
            State(order.OrderId, "wa2", 2, driving: true),
            CancellationToken.None);

        Assert.Equal(1, updated!.OrderUpdateId);
        Assert.Equal(3, updated.ReleasedNodeCount);
        Assert.StartsWith("wa4", updated.Nodes[0].NodeId, StringComparison.Ordinal);
        Assert.Equal(RobotOrderStatuses.Executing, updated.Status);
    }

    [Fact]
    public async Task ApplyState_NoProgress_DoesNotPublishTwice()
    {
        var (service, orders, time, _) = RobotOrderServiceTests.Create();
        var order = ExecutingOrder(Plan(6), releasedCount: 3);
        orders.Orders[order.OrderId] = order;

        var first = await service.ApplyStateAsync(
            State(order.OrderId, "wa2", 2, driving: true),
            CancellationToken.None);
        Assert.Equal(1, first!.OrderUpdateId);

        time.Now = time.Now.AddSeconds(3);
        var second = await service.ApplyStateAsync(
            State(order.OrderId, "wa2", 2, driving: true),
            CancellationToken.None);

        Assert.Equal(1, second!.OrderUpdateId);
        Assert.Equal(first.PlanJson, second.PlanJson);
    }

    [Fact]
    public async Task ApplyState_CompletedOrder_DoesNotPublishUpdate()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = ExecutingOrder(Plan(6), releasedCount: 3);
        orders.Orders[order.OrderId] = order;

        var completed = await service.ApplyStateAsync(
            State(order.OrderId, "wa10", 10, driving: false),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Completed, completed!.Status);
        Assert.Equal(0, completed.OrderUpdateId);
    }

    private static RobotOrder ExecutingOrder(string planJson, int releasedCount) => new(
        "ORD_04331",
        OrderTestData.Serial,
        0,
        RobotOrderStatuses.Executing,
        planJson,
        releasedCount,
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
            new RobotOrderNode("ORD_04331", 6, "wa6", true, false, 0),
            new RobotOrderNode("ORD_04331", 8, "wa8", false, false, 0),
            new RobotOrderNode("ORD_04331", 10, "wa10", false, false, 0),
        ]);

    /// <summary>План из <paramref name="nodeCount"/> узлов, первые три в базовом плане.</summary>
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
                released = index < 3,
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
                    released = index < 3,
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

    private static RobotSnapshot State(string orderId, string lastNodeId, int sequence, bool driving)
    {
        var reached = Enumerable.Range(0, (sequence / 2) + 1)
            .Select(index => ($"wa{index * 2}", index * 2, true))
            .ToList();

        return OrderTestData.Snapshot(orderId, orderUpdateId: 0, driving: driving, nodes: reached) with
        {
            LastNodeId = lastNodeId,
            LastNodeSequenceId = sequence,
        };
    }
}
