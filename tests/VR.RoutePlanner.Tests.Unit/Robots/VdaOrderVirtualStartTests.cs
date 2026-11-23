using System.Text.Json.Nodes;
using VR.RoutePlanner.Infrastructure.Mqtt;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Виртуальный старт обновления заказа: робот стоит между узлами, а сообщение начинается
/// с первого узла маршрута. Как в AGVControl, перед планом появляется виртуальный узел
/// с позой робота и виртуальное ребро <c>VIRTUAL_START_&lt;nodeId&gt;</c>.
/// </summary>
public sealed class VdaOrderVirtualStartTests
{
    [Fact]
    public void Build_UpdateWithRobotBetweenNodes_AddsVirtualNodeAndEdge()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            OrderFixtures.Route(first.EdgeId, second.EdgeId),
            graph,
            orderUpdateId: 1,
            robotX: 1.0,
            robotY: 1.0,
            robotTheta: 0.5));

        var nodes = order["nodes"]!.AsArray();
        var edges = order["edges"]!.AsArray();

        // Виртуальный узел плюс два узла маршрута: число узлов = число ребер + 1.
        Assert.Equal(4, nodes.Count);
        Assert.Equal(3, edges.Count);

        var virtualNode = nodes[0]!;
        Assert.Equal("VIRTUAL_START_wa100", virtualNode["nodeId"]!.GetValue<string>());
        Assert.Equal(0, virtualNode["sequenceId"]!.GetValue<int>());
        Assert.True(virtualNode["released"]!.GetValue<bool>());
        Assert.Empty(virtualNode["actions"]!.AsArray());

        var position = virtualNode["nodePosition"]!;
        Assert.Equal(1.0, position["x"]!.GetValue<double>(), 9);
        Assert.Equal(1.0, position["y"]!.GetValue<double>(), 9);
        Assert.Equal(0.5, position["theta"]!.GetValue<double>(), 9);
        Assert.Equal("801", position["mapId"]!.GetValue<string>());

        var virtualEdge = edges[0]!;
        Assert.Equal("VIRTUAL_START_wa100", virtualEdge["edgeId"]!.GetValue<string>());
        Assert.Equal(1, virtualEdge["sequenceId"]!.GetValue<int>());
        Assert.True(virtualEdge["released"]!.GetValue<bool>());
        Assert.Equal("VIRTUAL_START_wa100", virtualEdge["startNodeId"]!.GetValue<string>());
        Assert.Equal("wa100", virtualEdge["endNodeId"]!.GetValue<string>());
        Assert.False(virtualEdge["rotationAllowed"]!.GetValue<bool>());
        Assert.True(virtualEdge.AsObject().ContainsKey("orientation"));
        Assert.Null(virtualEdge["orientation"]);
        Assert.True(virtualEdge.AsObject().ContainsKey("orientationType"));
        Assert.Null(virtualEdge["orientationType"]);
    }

    [Fact]
    public void Build_UpdateWithRobotBetweenNodes_SequenceIdsStayEvenOdd()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            OrderFixtures.Route(first.EdgeId, second.EdgeId),
            graph,
            orderUpdateId: 1,
            robotX: 1.0,
            robotY: 1.0,
            robotTheta: 0.5));

        var nodeIds = order["nodes"]!.AsArray().Select(node => node!["nodeId"]!.GetValue<string>()).ToList();
        var nodeSequences = order["nodes"]!.AsArray().Select(node => node!["sequenceId"]!.GetValue<int>()).ToList();
        var edgeSequences = order["edges"]!.AsArray().Select(edge => edge!["sequenceId"]!.GetValue<int>()).ToList();

        Assert.Equal(["VIRTUAL_START_wa100", "wa100", "wa101", "wa102"], nodeIds);
        Assert.Equal([0, 2, 4, 6], nodeSequences);
        Assert.Equal([1, 3, 5], edgeSequences);

        // Ребро всегда между своими узлами: sequenceId ребра = sequenceId узла + 1.
        foreach (var edge in order["edges"]!.AsArray())
        {
            var edgeSequence = edge!["sequenceId"]!.GetValue<int>();
            Assert.True(edgeSequence % 2 == 1, $"ребро нечетное: {edgeSequence}");
            Assert.Equal(
                edge["startNodeId"]!.GetValue<string>(),
                nodeIds[(edgeSequence - 1) / 2]);
            Assert.Equal(
                edge["endNodeId"]!.GetValue<string>(),
                nodeIds[((edgeSequence - 1) / 2) + 1]);
        }
    }

    [Fact]
    public void Build_FirstOrderWithRobotBetweenNodes_HasNoVirtualStart()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();

        // Первое сообщение заказа: позу робота получает сам первый узел, виртуальное ребро не нужно.
        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            OrderFixtures.Route(first.EdgeId, second.EdgeId),
            graph,
            robotX: 1.0,
            robotY: 1.0,
            robotTheta: 0.5));

        Assert.Equal(3, order["nodes"]!.AsArray().Count);
        Assert.Equal(["wa100", "wa101", "wa102"], order["nodes"]!.AsArray().Select(node => node!["nodeId"]!.GetValue<string>()));
        Assert.Equal([0, 2, 4], order["nodes"]!.AsArray().Select(node => node!["sequenceId"]!.GetValue<int>()));
        Assert.Equal("801-wa100-wa101", order["edges"]![0]!["edgeId"]!.GetValue<string>());
    }

    [Fact]
    public void Build_UpdateWithRobotAtFirstNode_HasNoVirtualStart()
    {
        var (graph, first, second, _, _, _) = OrderFixtures.TwoEdgeGraph();

        // Робот стоит в первом узле маршрута (wa100 = 0/0): виртуального ребра нет.
        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            OrderFixtures.Route(first.EdgeId, second.EdgeId),
            graph,
            orderUpdateId: 1,
            robotX: 0.0,
            robotY: 0.0,
            robotTheta: 0.0));

        Assert.Equal(3, order["nodes"]!.AsArray().Count);
        Assert.Equal("wa100", order["nodes"]![0]!["nodeId"]!.GetValue<string>());
        Assert.Equal("801-wa100-wa101", order["edges"]![0]!["edgeId"]!.GetValue<string>());
    }
}
