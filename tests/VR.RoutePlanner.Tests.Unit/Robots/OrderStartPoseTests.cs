using System.Text.Json.Nodes;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Infrastructure.Mqtt;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Старт заказа по правилу AGVControl: поза робота перед заказом не корректируется,
/// initPosition в потоке заказа не отправляется. Первый узел получает фактические
/// координаты робота с широкими допусками, поэтому коррекция не нужна.
/// </summary>
public sealed class OrderStartPoseTests
{
    private const double WrkLength = 1.475;

    [Fact]
    public async Task CreateOrder_RobotAtWorkingStopPoint_SendsNoPoseCorrection()
    {
        var (graph, _, _, slot) = OrderFixtures.SlotGraph();
        var publisher = new FakePublisher();
        var created = RobotOrderServiceTests.CreateWithTraffic(
            order: new OrderOptions(),
            graph: graph,
            composer: new VdaOrderComposer(),
            publisher: publisher);

        created.States.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot(
            null,
            lastNodeId: slot.NodeId,
            positionX: slot.X - WrkLength,
            positionY: slot.Y);

        await created.Service.CreateOrderAsync(
            OrderFixtures.MapId,
            OrderTestData.Serial,
            slot.NodeId,
            false,
            CancellationToken.None);

        Assert.Empty(publisher.Actions);
    }

    [Fact]
    public async Task CreateOrder_PoseFarFromWorkingNode_KeepsPoseAndSendsNoCorrection()
    {
        var (graph, _, _, slot) = OrderFixtures.SlotGraph();
        var publisher = new FakePublisher();
        var created = RobotOrderServiceTests.CreateWithTraffic(
            order: new OrderOptions(),
            graph: graph,
            composer: new VdaOrderComposer(),
            publisher: publisher);

        var robotX = slot.X + 6;
        created.States.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot(
            null,
            lastNodeId: slot.NodeId,
            positionX: robotX,
            positionY: slot.Y);

        await created.Service.CreateOrderAsync(
            OrderFixtures.MapId,
            OrderTestData.Serial,
            slot.NodeId,
            false,
            CancellationToken.None);

        Assert.DoesNotContain(
            publisher.Actions,
            action => action.Payload.Contains("initPosition", StringComparison.Ordinal));

        var order = JsonNode.Parse(Assert.Single(publisher.Orders).Payload)!;
        var firstNode = order["nodes"]!.AsArray()[0]!;
        Assert.Equal(robotX, firstNode["nodePosition"]!["x"]!.GetValue<double>(), 9);
    }
}
