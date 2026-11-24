using System.Text.Json.Nodes;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Infrastructure.Mqtt;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Сборка узлов, ребер и действий заказа по правилам этапа 2.5.
/// </summary>
public sealed class VdaOrderNodeTests
{
    [Fact]
    public void Build_WorkingNode_StopsBeforeSlotAndPutsActionOnSlot()
    {
        var (graph, edge, start, slot) = OrderFixtures.SlotGraph();
        var route = OrderFixtures.Route([(edge.EdgeId, RouteEdgeDirection.Forward)]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(route, graph, targetActionType: "pick"));

        var node = order["nodes"]![1]!;
        Assert.Equal("wo-1-1", node["nodeId"]!.GetValue<string>());
        Assert.Equal(2, node["sequenceId"]!.GetValue<int>());
        Assert.True(node["released"]!.GetValue<bool>());

        var position = node["nodePosition"]!;
        var stopX = position["x"]!.GetValue<double>();
        var stopY = position["y"]!.GetValue<double>();
        var distance = Math.Sqrt(Math.Pow(stopX - slot.X, 2) + Math.Pow(stopY - slot.Y, 2));
        Assert.Equal(WorkingNodeGeometry.WrkLength, distance, 6);

        // Курс подхода — от единственного входящего узла к слоту (вдоль +X).
        // Робот должен довернуться к стеллажу, поэтому theta узла задается курсом подхода,
        // даже если тип узла WORKING; containerTheta дублирует его для действия вил.
        Assert.Equal(0.0, position["theta"]!.GetValue<double>(), 9);
        Assert.Equal(slot.X - WorkingNodeGeometry.WrkLength, stopX, 6);
        Assert.Equal(0.0, stopY, 9);
        Assert.Equal(0.1, position["allowedDeviationXY"]!.GetValue<double>(), 9);

        var action = node["actions"]![0]!;
        Assert.Equal("pick", action["actionType"]!.GetValue<string>());
        Assert.Equal("SOFT", action["blockingType"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(action["actionId"]!.GetValue<string>()));

        var parameters = action["actionParameters"]!.AsArray();
        Assert.Equal(
            ["containerTypeId", "containerX", "containerY", "containerTheta", "onlyFork", "autoReturnNode", "noScan", "adjustWithoutOA"],
            parameters.Select(parameter => parameter!["key"]!.GetValue<string>()));

        // Паллета — в координатах узла слота, курс — на стеллаж.
        Assert.Equal(2, parameters[0]!["value"]!.GetValue<int>());
        Assert.Equal(slot.X, parameters[1]!["value"]!.GetValue<double>(), 9);
        Assert.Equal(slot.Y, parameters[2]!["value"]!.GetValue<double>(), 9);
        Assert.Equal(0.0, parameters[3]!["value"]!.GetValue<double>(), 9);
        Assert.True(parameters[4]!["value"]!.GetValue<bool>());
        Assert.True(parameters[5]!["value"]!.GetValue<bool>());
        Assert.False(parameters[6]!["value"]!.GetValue<bool>());
        Assert.True(parameters[7]!["value"]!.GetValue<bool>());

        Assert.Equal(start.NodeId, order["nodes"]![0]!["nodeId"]!.GetValue<string>());
    }

    [Fact]
    public void Build_TargetActionType_DoesNotDependOnNodeType()
    {
        var (graph, edge, _, slot) = OrderFixtures.SlotGraph("LOADING");
        var route = OrderFixtures.Route([(edge.EdgeId, RouteEdgeDirection.Forward)]);

        // Тип действия задает сервис, а не тип узла: на LOADING-узле может стоять drop.
        var order = VdaOrderBuilder.Build(OrderFixtures.Request(route, graph, targetActionType: "drop"));

        var node = order["nodes"]![1]!;
        Assert.Equal("drop", node["actions"]![0]!["actionType"]!.GetValue<string>());
        Assert.Equal(0.0, node["nodePosition"]!["theta"]!.GetValue<double>(), 9);
        Assert.Equal(slot.NodeId, node["nodeId"]!.GetValue<string>());
    }

    [Fact]
    public void Build_NoActionTypes_HasNoActionsAndNoTheta()
    {
        var (graph, edge, _, _) = OrderFixtures.SlotGraph();
        var route = OrderFixtures.Route([(edge.EdgeId, RouteEdgeDirection.Forward)]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(route, graph));

        Assert.All(order["nodes"]!.AsArray(), node => Assert.Empty(node!["actions"]!.AsArray()));
        Assert.All(order["nodes"]!.AsArray(), node => Assert.Null(node!["nodePosition"]!["theta"]));
    }

    [Fact]
    public void Build_Waypoint_HasNoTheta()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();
        var route = OrderFixtures.Route(first.EdgeId, second.EdgeId);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(route, graph));

        Assert.All(order["nodes"]!.AsArray(), node => Assert.Null(node!["nodePosition"]!["theta"]));
    }

    [Fact]
    public void Build_TerminalAction_OnlyInLastPlanNode()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();
        var route = OrderFixtures.Route(first.EdgeId, second.EdgeId);

        var order = VdaOrderBuilder.Build(
            OrderFixtures.Request(route, graph, terminalActionType: "detectObject"));

        var nodes = order["nodes"]!.AsArray();
        Assert.Empty(nodes[0]!["actions"]!.AsArray());
        Assert.Empty(nodes[1]!["actions"]!.AsArray());
        var action = nodes[2]!["actions"]![0]!;
        Assert.Equal("detectObject", action["actionType"]!.GetValue<string>());
        Assert.Equal("SOFT", action["blockingType"]!.GetValue<string>());
    }

    [Fact]
    public void Build_TerminalAction_NotDuplicatedInSlice()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();
        var route = OrderFixtures.Route(first.EdgeId, second.EdgeId);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            sliceStartIndex: 0,
            sliceNodeCount: 2,
            terminalActionType: "detectObject"));

        // Кусок кончается раньше последнего узла плана: завершающего действия в нем нет.
        Assert.All(order["nodes"]!.AsArray(), node => Assert.Empty(node!["actions"]!.AsArray()));
    }

    [Fact]
    public void Build_FirstNode_IsRobotPoseWithWideDeviations()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();
        var route = OrderFixtures.Route(first.EdgeId, second.EdgeId);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            robotX: 0.25,
            robotY: 0.5,
            robotTheta: 0.7));

        var position = order["nodes"]![0]!["nodePosition"]!;
        Assert.Equal(0.25, position["x"]!.GetValue<double>(), 9);
        Assert.Equal(0.5, position["y"]!.GetValue<double>(), 9);
        Assert.Equal(0.3, position["allowedDeviationXY"]!.GetValue<double>(), 9);
        Assert.Equal(3.14, position["allowedDeviationTheta"]!.GetValue<double>(), 9);
        Assert.Null(position["theta"]);
    }

    [Fact]
    public void Build_FirstNodeTheta_AddsRobotCourse()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();
        var route = OrderFixtures.Route(first.EdgeId, second.EdgeId);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            robotTheta: 0.7,
            firstNodeTheta: true));

        Assert.Equal(0.7, order["nodes"]![0]!["nodePosition"]!["theta"]!.GetValue<double>(), 9);
    }

    [Fact]
    public void Build_ApproachFallback_PutsActionInLastNodeWithTargetCoordinates()
    {
        // Цель — слот, но маршрут кончился на соседнем узле: действие ставится в последнем узле маршрута.
        var approach = OrderFixtures.Node("wa400", 0, 0);
        var neighbor = OrderFixtures.Node("wa401", 2, 0);
        var slot = OrderFixtures.Node("wo-1-1", 4, 0, "LOADING");
        var first = OrderFixtures.Edge(approach, neighbor, "801-wa400-wa401");
        var second = OrderFixtures.Edge(neighbor, slot, "801-wa401-wo-1-1");
        var graph = OrderFixtures.Graph([approach, neighbor, slot], [first, second]);
        var route = OrderFixtures.Route([(first.EdgeId, RouteEdgeDirection.Forward)]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            approachTarget: slot,
            targetActionType: "pick"));

        var node = order["nodes"]![1]!;
        Assert.Equal(neighbor.NodeId, node["nodeId"]!.GetValue<string>());
        var parameters = node["actions"]![0]!["actionParameters"]!.AsArray();
        Assert.Equal(slot.X, parameters[1]!["value"]!.GetValue<double>(), 9);
        Assert.Equal(slot.Y, parameters[2]!["value"]!.GetValue<double>(), 9);
        Assert.Equal(0.0, parameters[3]!["value"]!.GetValue<double>(), 9);
        Assert.Equal(0.0, node["nodePosition"]!["theta"]!.GetValue<double>(), 9);
    }

    [Fact]
    public void Build_Edges_HaveNoSpeedAndTangentialOrientation()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();
        var route = OrderFixtures.Route(first.EdgeId, second.EdgeId);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(route, graph));

        foreach (var edge in order["edges"]!.AsArray())
        {
            // D5: эталон AGVControl скоростей ребра не отдает.
            Assert.False(edge!.AsObject().ContainsKey("maxSpeed"));
            Assert.False(edge.AsObject().ContainsKey("maxRotationSpeed"));
            Assert.Equal("TANGENTIAL", edge["orientationType"]!.GetValue<string>());
            Assert.True(edge["released"]!.GetValue<bool>());
            Assert.Equal(1, edge["trajectory"]!["degree"]!.GetValue<int>());
            Assert.Equal([0, 0, 1, 1], edge["trajectory"]!["knotVector"]!.AsArray().Select(item => item!.GetValue<int>()));
        }
    }

    [Fact]
    public void Build_EdgeWithRotationSpeed_DoesNotWriteSpeeds()
    {
        var from = OrderFixtures.Node("wa400", 0, 0);
        var to = OrderFixtures.Node("wa401", 2, 0);
        var edge = OrderFixtures.Edge(from, to, "801-wa400-wa401") with { MaxRotationSpeed = 0.4 };
        var graph = OrderFixtures.Graph([from, to], [edge]);
        var route = OrderFixtures.Route([(edge.EdgeId, RouteEdgeDirection.Forward)]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(route, graph));

        // D5: даже заданная в карте скорость поворота в ребро заказа не попадает.
        var body = order["edges"]![0]!.AsObject();
        Assert.False(body.ContainsKey("maxRotationSpeed"));
        Assert.False(body.ContainsKey("maxSpeed"));
    }

    [Fact]
    public void Build_SingleNodeRoute_ReturnsNodeWithoutEdges()
    {
        var (graph, edge, start, _) = OrderFixtures.SlotGraph();
        var route = new PlannedRoute(
            [OrderFixtures.SingleSegment(edge.EdgeId, RouteEdgeDirection.Forward)],
            2.0,
            2.0,
            2.0,
            0,
            1.5,
            false,
            null);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(route, graph, sliceNodeCount: 1));

        var node = Assert.Single(order["nodes"]!.AsArray());
        Assert.Equal(start.NodeId, node!["nodeId"]!.GetValue<string>());
        Assert.Equal(0, node["sequenceId"]!.GetValue<int>());
        Assert.Empty(order["edges"]!.AsArray());
    }
}
