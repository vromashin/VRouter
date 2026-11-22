using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Infrastructure.Mqtt;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Unit.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Сборка заказа и публикация: сравнение с образцом по структуре, а не по тексту.
/// </summary>
public sealed class VdaOrderBuilderTests
{
    [Fact]
    public void Build_TwoEdgeRoute_StructureMatchesSample()
    {
        var sample = JsonNode.Parse(ReadAsset("vda-order.json"))!;
        var order = OrderFixtures.Order();

        CompareStructure(sample, order, "order");
    }

    [Fact]
    public void Build_TwoEdgeRoute_HasExpectedHeaderAndOrderId()
    {
        var order = OrderFixtures.Order();

        Assert.Equal(639307655688479568, order["headerId"]!.GetValue<long>());
        Assert.Equal("2026-11-21T18:14:29.029Z", order["timestamp"]!.GetValue<string>());
        Assert.Equal("2.1.0", order["version"]!.GetValue<string>());
        Assert.Equal("Tuskrobots", order["manufacturer"]!.GetValue<string>());
        Assert.Equal("98001", order["serialNumber"]!.GetValue<string>());
        Assert.Equal("ORD_00007", order["orderId"]!.GetValue<string>());
        Assert.Equal(0, order["orderUpdateId"]!.GetValue<int>());
    }

    [Fact]
    public void Build_TwoEdgeRoute_SequenceIdsAlternate()
    {
        var order = OrderFixtures.Order();
        var nodes = order["nodes"]!.AsArray();
        var edges = order["edges"]!.AsArray();

        Assert.Equal(3, nodes.Count);
        Assert.Equal(2, edges.Count);
        Assert.Equal([0, 2, 4], nodes.Select(node => node!["sequenceId"]!.GetValue<int>()));
        Assert.Equal([1, 3], edges.Select(edge => edge!["sequenceId"]!.GetValue<int>()));
        Assert.Equal(["wa100", "wa101", "wa102"], nodes.Select(node => node!["nodeId"]!.GetValue<string>()));
    }

    [Fact]
    public void Build_FirstNode_HasLargerDeviationThanRest()
    {
        var order = OrderFixtures.Order();
        var nodes = order["nodes"]!.AsArray();
        var first = nodes[0]!["nodePosition"]!;
        var second = nodes[1]!["nodePosition"]!;

        Assert.Equal(0.3, first["allowedDeviationXY"]!.GetValue<double>(), 9);
        Assert.Equal(3.14, first["allowedDeviationTheta"]!.GetValue<double>(), 9);
        Assert.NotNull(first["theta"]);
        Assert.Equal(0.1, second["allowedDeviationXY"]!.GetValue<double>(), 9);
        Assert.Equal(0.1, second["allowedDeviationTheta"]!.GetValue<double>(), 9);
        Assert.Null(second["theta"]);
        Assert.Equal("801", first["mapId"]!.GetValue<string>());
    }

    [Fact]
    public void Build_Edges_CarryNodesAndStraightTrajectory()
    {
        var order = OrderFixtures.Order();
        var edges = order["edges"]!.AsArray();

        foreach (var edge in edges)
        {
            Assert.Equal("TANGENTIAL", edge!["orientationType"]!.GetValue<string>());
            Assert.Equal(0, edge["orientation"]!.GetValue<int>());
            Assert.True(edge["released"]!.GetValue<bool>());
            var trajectory = edge["trajectory"]!;
            Assert.Equal(1, trajectory["degree"]!.GetValue<int>());
            Assert.Equal(4, trajectory["knotVector"]!.AsArray().Count);
            Assert.Equal(2, trajectory["controlPoints"]!.AsArray().Count);
            Assert.All(
                trajectory["controlPoints"]!.AsArray(),
                point => Assert.Equal(1, point!["weight"]!.GetValue<int>()));
        }

        Assert.Equal("801-wa100-wa101", edges[0]!["edgeId"]!.GetValue<string>());
        Assert.Equal("wa100", edges[0]!["startNodeId"]!.GetValue<string>());
        Assert.Equal("wa101", edges[0]!["endNodeId"]!.GetValue<string>());
    }

    [Fact]
    public void Build_BackwardEdge_ReversesTrajectoryAndSwapsNodes()
    {
        var (graph, _, second, _, _, _) = OrderFixtures.TwoEdgeGraph();
        var route = new PlannedRoute(
            [OrderFixtures.SingleSegment(second.EdgeId, RouteEdgeDirection.Backward)],
            2.0,
            2.0,
            2.0,
            0,
            1.5,
            false,
            null);
        var order = VdaOrderBuilder.Build(route, graph, "801", "Tuskrobots", "98001", 7, 1, DateTimeOffset.UnixEpoch);

        var edge = order["edges"]![0]!;
        Assert.Equal("801-wa101-wa102", edge["edgeId"]!.GetValue<string>());
        Assert.Equal("wa102", edge["startNodeId"]!.GetValue<string>());
        Assert.Equal("wa101", edge["endNodeId"]!.GetValue<string>());

        Assert.Equal(["wa102", "wa101"], order["nodes"]!.AsArray().Select(node => node!["nodeId"]!.GetValue<string>()));

        var points = edge["trajectory"]!["controlPoints"]!.AsArray();
        Assert.Equal(2.0, points[0]!["x"]!.GetValue<double>(), 9);
        Assert.Equal(2.0, points[0]!["y"]!.GetValue<double>(), 9);
        Assert.Equal(0.0, points[1]!["y"]!.GetValue<double>(), 9);
    }

    [Fact]
    public void Build_MultiPointEdge_UsesStraightTwoPointCurveAsInReference()
    {
        var points = new[]
        {
            new VR.RoutePlanner.Domain.Graph.MapPoint(0, 0, null),
            new VR.RoutePlanner.Domain.Graph.MapPoint(1, 0, null),
            new VR.RoutePlanner.Domain.Graph.MapPoint(2, 1, null),
            new VR.RoutePlanner.Domain.Graph.MapPoint(2, 2, null),
        };

        var trajectory = VdaOrderBuilder.BuildTrajectory(points, RouteEdgeDirection.Forward);

        // Эталон AGVControl публикует прямую: degree 1, четыре узла, две точки.
        Assert.Equal(1, trajectory["degree"]!.GetValue<int>());
        Assert.Equal(4, trajectory["knotVector"]!.AsArray().Count);
        Assert.Equal(2, trajectory["controlPoints"]!.AsArray().Count);
    }

    [Fact]
    public void Build_RouteWithGap_Throws()
    {
        var (graph, _, second, _, _, _) = OrderFixtures.TwoEdgeGraph();
        var route = OrderFixtures.Route(second.EdgeId, second.EdgeId);

        Assert.Throws<InvalidOperationException>(() =>
            VdaOrderBuilder.Build(route, graph, "801", "Tuskrobots", "98001", 7, 1, DateTimeOffset.UnixEpoch));
    }

    private static string ReadAsset(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Robots", "Assets", fileName));

    private static void CompareStructure(JsonNode sample, JsonNode actual, string path)
    {
        if (sample is JsonObject sampleObject)
        {
            Assert.True(actual is JsonObject, $"{path}: ожидался объект");
            var actualObject = actual.AsObject();
            Assert.Equal(
                sampleObject.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal),
                actualObject.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));

            foreach (var pair in sampleObject)
            {
                CompareStructure(pair.Value!, actualObject[pair.Key]!, $"{path}.{pair.Key}");
            }

            return;
        }

        if (sample is JsonArray sampleArray)
        {
            Assert.True(actual is JsonArray, $"{path}: ожидался массив");
            var actualArray = actual.AsArray();
            if (sampleArray.Count == 0)
            {
                Assert.Empty(actualArray);
                return;
            }

            Assert.NotEmpty(actualArray);
            CompareStructure(sampleArray[0]!, actualArray[0]!, $"{path}[0]");
            return;
        }

        Assert.True(sample is JsonValue && actual is JsonValue, $"{path}: ожидалось скалярное значение");
        var sampleKind = ((JsonValue)sample).GetValueKind();
        var actualKind = ((JsonValue)actual).GetValueKind();
        Assert.True(
            sampleKind == actualKind,
            $"{path}: тип {sampleKind} в образце, {actualKind} в заказе");
    }
}

/// <summary>
/// Публикация: топик и тело без реального брокера.
/// </summary>
public sealed class VdaOrderPublisherTests
{
    [Fact]
    public async Task PublishAsync_TwoEdgeOrder_SendsToRobotOrderTopic()
    {
        var transport = new RecordingTransport();
        var publisher = new VdaOrderPublisher(transport, NullLogger<VdaOrderPublisher>.Instance);
        var order = VdaOrderBuilder.Serialize(OrderFixtures.Order());

        await publisher.PublishAsync("Tuskrobots", "98001", order, CancellationToken.None);

        var message = Assert.Single(transport.Messages);
        Assert.Equal("APR/v2/Tuskrobots/98001/order", message.Topic);
        Assert.Equal(order, message.Payload);

        using var document = JsonDocument.Parse(message.Payload);
        Assert.Equal("ORD_00007", document.RootElement.GetProperty("orderId").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Equal(2, document.RootElement.GetProperty("edges").GetArrayLength());
    }

    [Fact]
    public void NextHeaderId_StrictlyIncreases()
    {
        var first = VdaOrderPublisher.NextHeaderId();
        var second = VdaOrderPublisher.NextHeaderId();

        Assert.True(second > first);
    }

    private sealed class RecordingTransport : IRobotOrderTransport
    {
        public List<(string Topic, string Payload)> Messages { get; } = new();

        public Task PublishAsync(string topic, string payload, CancellationToken ct)
        {
            Messages.Add((topic, payload));
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Номер заказа: база без таблицы <c>vrt_RobotOrder</c> дает фолбэк 1.
/// </summary>
public sealed class RobotOrderNumberSourceTests
{
    [Fact]
    public async Task NextAsync_NoTable_FallsBackToOne()
    {
        using var factory = SqliteContextFactory.Create();
        var logger = new RecordingLogger<VrtRobotOrderNumberSource>();
        var source = new VrtRobotOrderNumberSource(factory, logger);

        var number = await source.NextAsync(CancellationToken.None);

        Assert.Equal(1, number);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }
}

/// <summary>
/// Состояние заказа из сообщения <c>state</c>.
/// </summary>
public sealed class RobotOrderStatusParserTests
{
    [Fact]
    public void TryParse_SampleState_ReadsOrderFields()
    {
        var payload = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Robots", "Assets", "vda-state-after.json"));

        Assert.True(RobotOrderStatusParser.TryParse(payload, out var status, out var error), error);
        Assert.NotNull(status);
        Assert.Equal("ORD_04330", status!.OrderId);
        Assert.Equal(0, status.OrderUpdateId);
        Assert.False(status.Driving);
        Assert.Empty(status.NodeStates);
        Assert.Empty(status.Errors);
        Assert.True(RobotOrderStatusParser.HasOrder(payload));
    }

    [Fact]
    public void ParseOrNull_BrokenJson_IsNull()
    {
        Assert.Null(RobotOrderStatusParser.ParseOrNull("{ \"orderId\": "));
        Assert.False(RobotOrderStatusParser.HasOrder("не json"));
    }
}
