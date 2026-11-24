using System.Text.Json.Nodes;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Infrastructure.Mqtt;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Коррекция позы перед заказом: рабочий узел на карте стоит в позиции паллеты,
/// поэтому сверять позу робота с его координатами нельзя, иначе initPosition
/// отправляет робота внутрь стеллажа, а на стенде запускается перезапуск симулятора.
/// </summary>
public sealed class PoseCorrectionTests
{
    private const double WrkLength = 1.475;

    [Fact]
    public async Task CreateOrder_RobotAtWorkingStopPoint_NoPoseCorrection()
    {
        var (graph, _, _, slot) = OrderFixtures.SlotGraph();
        var publisher = new FakePublisher();
        var created = RobotOrderServiceTests.CreateWithTraffic(
            order: new OrderOptions(),
            graph: graph,
            composer: new VdaOrderComposer(),
            publisher: publisher);

        // Робот стоит там, где он и должен стоять: на точке остановки перед паллетой.
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

        Assert.DoesNotContain(
            publisher.Actions,
            action => action.Payload.Contains("initPosition", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateOrder_PoseFarFromWorkingNode_CorrectsToStopPointNotToRack()
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
            positionX: slot.X + 6,
            positionY: slot.Y);

        await created.Service.CreateOrderAsync(
            OrderFixtures.MapId,
            OrderTestData.Serial,
            slot.NodeId,
            false,
            CancellationToken.None);

        var initPosition = publisher.Actions
            .First(action => action.Payload.Contains("initPosition", StringComparison.Ordinal));

        var payload = JsonNode.Parse(initPosition.Payload)!;
        var x = payload["actions"]![0]!["actionParameters"]!
            .AsArray()
            .First(item => item!["key"]!.GetValue<string>() == "x")!["value"]!
            .GetValue<double>();

        // Цель коррекции - точка остановки со стороны робота, а не паллета в стеллаже.
        Assert.Equal(slot.X + WrkLength, x, 9);
        Assert.NotEqual(slot.X, x);
    }
}
