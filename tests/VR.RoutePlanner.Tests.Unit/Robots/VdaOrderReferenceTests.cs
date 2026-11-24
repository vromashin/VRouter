using System.Text.Json.Nodes;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Infrastructure.Mqtt;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Форма заказа по эталону AGVControl (снимки ref-pick-drop и ref-rev-leg2-pick-drop):
/// действие только в цели (D1), стоянка в цели (D2), курс первого ребра (D3),
/// курс первого узла (D4).
/// </summary>
public sealed class VdaOrderReferenceTests
{
    [Fact]
    public void Build_MessageStartingAtAnchor_FirstNodeIsAnchorWithoutTheta()
    {
        var (graph, slot, _, far) = SlotDepartureGraph();
        var route = RouteFromSlot();

        // Сообщение плеча N > 0 начинается на узле-якоре: координаты карты, курса нет,
        // допуски 0.3/3.14. Действие вил — на дубле узла за виртуальным ребром.
        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            orderUpdateId: 1,
            firstNodeTheta: true,
            robotX: 3.0,
            robotY: 0.1,
            robotTheta: 1.0,
            anchorNode: slot,
            anchorActionType: "drop"));

        var nodes = order["nodes"]!.AsArray();
        var edges = order["edges"]!.AsArray();
        Assert.Equal(4, nodes.Count);
        Assert.Equal(3, edges.Count);

        var pose = nodes[0]!;
        Assert.Equal(slot.NodeId, pose["nodeId"]!.GetValue<string>());
        Assert.Equal(0, pose["sequenceId"]!.GetValue<int>());
        Assert.Empty(pose["actions"]!.AsArray());
        Assert.Equal(2.525, pose["nodePosition"]!["x"]!.GetValue<double>(), 9);
        Assert.Null(pose["nodePosition"]!["theta"]);
        Assert.Equal(0.3, pose["nodePosition"]!["allowedDeviationXY"]!.GetValue<double>(), 9);
        Assert.Equal(3.14, pose["nodePosition"]!["allowedDeviationTheta"]!.GetValue<double>(), 9);

        var edge = edges[0]!;
        Assert.Equal("VIRTUAL_START_" + slot.NodeId, edge["edgeId"]!.GetValue<string>());
        Assert.Equal(slot.NodeId, edge["startNodeId"]!.GetValue<string>());
        Assert.Equal(slot.NodeId, edge["endNodeId"]!.GetValue<string>());
        Assert.False(edge["rotationAllowed"]!.GetValue<bool>());

        // Робот уходит из узла задним ходом (стоит вилами к стеллажу): первое ребро маршрута — PI.
        Assert.Equal(Math.PI, edges[1]!["orientation"]!.GetValue<double>(), 9);
        Assert.Equal(0.0, edges[2]!["orientation"]!.GetValue<double>(), 9);

        var duplicate = nodes[1]!;
        Assert.Equal(slot.NodeId, duplicate["nodeId"]!.GetValue<string>());
        Assert.Equal(2, duplicate["sequenceId"]!.GetValue<int>());
        Assert.Equal(0.0, duplicate["nodePosition"]!["theta"]!.GetValue<double>(), 9);
        Assert.Equal(["drop"], TaskOrderBuilderTests.ActionTypes(duplicate));

        Assert.Equal(far.NodeId, nodes[^1]!["nodeId"]!.GetValue<string>());
        Assert.All(nodes.Skip(2), node => Assert.Empty(node!["actions"]!.AsArray()));
    }

    [Fact]
    public void Build_FirstMessageFromWorkingNode_NoPickOnDeparture()
    {
        var (graph, _, _, _) = SlotDepartureGraph();
        var route = RouteFromSlot();

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            robotX: 2.525,
            robotY: 0,
            robotTheta: 0));

        Assert.All(order["nodes"]!.AsArray(), node => Assert.Empty(node!["actions"]!.AsArray()));
    }

    [Fact]
    public void Build_LeaveWorkingNodeAlongStoredEdge_FirstEdgeOrientationIsPi()
    {
        // Ребро хранится от слота (как 801-wo-2-7-wa62): старое правило дало бы 0.
        // Робот стоит вилами к стеллажу (курс 0), а выезжает в сторону -X: задний ход.
        var (graph, _, _, _) = SlotDepartureGraph();
        var route = RouteFromSlot();

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            robotX: 2.525,
            robotY: 0,
            robotTheta: 0));

        var edges = order["edges"]!.AsArray();
        Assert.Equal(Math.PI, edges[0]!["orientation"]!.GetValue<double>(), 9);
        Assert.Equal(0.0, edges[1]!["orientation"]!.GetValue<double>(), 9);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(Math.PI, Math.PI)]
    public void Build_WaypointStart_FirstEdgeOrientationFollowsRobotHeading(double robotTheta, double expected)
    {
        var (graph, edges) = OrderFixtures.LineGraph(storedBackwards: false);
        var route = OrderFixtures.Route([(edges[0].EdgeId, RouteEdgeDirection.Forward)]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            robotX: 0,
            robotY: 0,
            robotTheta: robotTheta));

        Assert.Equal(expected, order["edges"]![0]!["orientation"]!.GetValue<double>(), 9);
    }

    [Fact]
    public void OrderOptions_FirstNodeTheta_IsOnByDefault()
    {
        Assert.True(new OrderOptions().FirstNodeTheta);
    }

    [Theory]
    [InlineData(1.2, true)]
    [InlineData(null, false)]
    public void Build_FirstNodeTheta_WritesRobotHeadingWhenKnown(double? robotTheta, bool hasTheta)
    {
        var (graph, edges) = OrderFixtures.LineGraph(storedBackwards: false);
        var route = OrderFixtures.Route([(edges[0].EdgeId, RouteEdgeDirection.Forward)]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            firstNodeTheta: true,
            robotX: 0,
            robotY: 0,
            robotTheta: robotTheta));

        var theta = order["nodes"]![0]!["nodePosition"]!["theta"];
        Assert.Equal(hasTheta, theta is not null);
        if (hasTheta)
        {
            Assert.Equal(robotTheta!.Value, theta!.GetValue<double>(), 9);
        }
    }

    [Fact]
    public void Build_StationaryAtWorkingNode_PoseNodeVirtualEdgeAndActionNode()
    {
        var (graph, _, _, slot) = OrderFixtures.SlotGraph();
        var route = new PlannedRoute([], 0, 0, 0, 0, double.PositiveInfinity, false, null);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            firstNodeTheta: true,
            robotX: 2.525,
            robotY: 0,
            robotTheta: 0,
            anchorNode: slot,
            anchorActionType: "pick"));

        var nodes = order["nodes"]!.AsArray();
        var edges = order["edges"]!.AsArray();
        Assert.Equal(2, nodes.Count);
        Assert.Single(edges);

        // Узел позы: тот же nodeId, широкие допуски, без курса и действий.
        var pose = nodes[0]!;
        Assert.Equal(slot.NodeId, pose["nodeId"]!.GetValue<string>());
        Assert.Equal(0, pose["sequenceId"]!.GetValue<int>());
        Assert.Equal(0.3, pose["nodePosition"]!["allowedDeviationXY"]!.GetValue<double>(), 9);
        Assert.Equal(3.14, pose["nodePosition"]!["allowedDeviationTheta"]!.GetValue<double>(), 9);
        Assert.Null(pose["nodePosition"]!["theta"]);
        Assert.Empty(pose["actions"]!.AsArray());

        // Дубль узла: точка остановки, курс к паллете и действие вил.
        var target = nodes[1]!;
        Assert.Equal(slot.NodeId, target["nodeId"]!.GetValue<string>());
        Assert.Equal(2, target["sequenceId"]!.GetValue<int>());
        Assert.Equal(0.1, target["nodePosition"]!["allowedDeviationXY"]!.GetValue<double>(), 9);
        Assert.Equal(0.1, target["nodePosition"]!["allowedDeviationTheta"]!.GetValue<double>(), 9);
        Assert.Equal(0.0, target["nodePosition"]!["theta"]!.GetValue<double>(), 9);
        var action = Assert.Single(target["actions"]!.AsArray());
        Assert.Equal("pick", action!["actionType"]!.GetValue<string>());

        var edge = edges[0]!;
        Assert.Equal("VIRTUAL_START_" + slot.NodeId, edge["edgeId"]!.GetValue<string>());
        Assert.Equal(1, edge["sequenceId"]!.GetValue<int>());
        Assert.Equal(slot.NodeId, edge["startNodeId"]!.GetValue<string>());
        Assert.Equal(slot.NodeId, edge["endNodeId"]!.GetValue<string>());
        Assert.False(edge["rotationAllowed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CreateOrder_RobotAlreadyAtWorkingTarget_PublishesStationaryOrder()
    {
        var (graph, _, _, slot) = OrderFixtures.SlotGraph();
        var created = RobotOrderServiceTests.CreateWithTraffic(
            order: new OrderOptions(),
            graph: graph,
            composer: new VdaOrderComposer());
        created.States.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot(
            null,
            lastNodeId: slot.NodeId,
            positionX: slot.X,
            positionY: slot.Y);

        var order = await created.Service.CreateOrderAsync(
            OrderFixtures.MapId,
            OrderTestData.Serial,
            slot.NodeId,
            false,
            CancellationToken.None);

        // Раньше заказ закрывался сразу (Completed, план "{}"), и pick не выполнялся.
        Assert.Equal(RobotOrderStatuses.Sent, order.Status);
        Assert.Equal(2, order.Nodes.Count);
        var plan = JsonNode.Parse(order.PlanJson!)!;
        Assert.Equal("VIRTUAL_START_" + slot.NodeId, plan["edges"]![0]!["edgeId"]!.GetValue<string>());
        Assert.Equal("pick", plan["nodes"]![1]!["actions"]![0]!["actionType"]!.GetValue<string>());

        // Первое состояние: робот в узле позы (sequenceId 0), заказ еще не выполнен.
        var accepted = await created.Service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, lastNodeId: slot.NodeId, lastNodeSequenceId: 0),
            CancellationToken.None);
        Assert.Equal(RobotOrderStatuses.Accepted, accepted!.Status);

        // Робот дошел до дубля узла (sequenceId 2) и закончил действие: заказ закрыт.
        // Версию робот сообщает ту, что получил последней (продление могло ее поднять).
        var version = created.Orders.Orders[order.OrderId].OrderUpdateId;
        var completed = await created.Service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, orderUpdateId: version, lastNodeId: slot.NodeId, lastNodeSequenceId: 2),
            CancellationToken.None);
        Assert.Equal(RobotOrderStatuses.Completed, completed!.Status);
    }

    /// <summary>
    /// Слот <c>wo-1-1</c> (4, 0), подход <c>wa200</c> (2, 0), дальний узел <c>wa201</c> (0, 0).
    /// Ребро слота хранится от слота к подходу, как <c>801-wo-2-7-wa62</c> на стенде.
    /// </summary>
    private static (WarehouseGraph Graph, WarehouseNode Slot, WarehouseNode Approach, WarehouseNode Far)
        SlotDepartureGraph()
    {
        var slot = OrderFixtures.Node("wo-1-1", 4, 0, "WORKING");
        var approach = OrderFixtures.Node("wa200", 2, 0);
        var far = OrderFixtures.Node("wa201", 0, 0);
        var graph = OrderFixtures.Graph(
            [slot, approach, far],
            [OrderFixtures.Edge(slot, approach, "801-wo-1-1-wa200"), OrderFixtures.Edge(approach, far, "801-wa200-wa201")]);
        return (graph, slot, approach, far);
    }

    private static PlannedRoute RouteFromSlot() => OrderFixtures.Route(
    [
        ("801-wo-1-1-wa200", RouteEdgeDirection.Forward),
        ("801-wa200-wa201", RouteEdgeDirection.Forward),
    ]);
}
