using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Tests.Unit.Planning;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Режимы <c>Routing:Order:ReleasedNodes</c>: одно сообщение на весь маршрут либо куски.
/// </summary>
public sealed class OrderChunkingTests
{
    [Fact]
    public async Task CreateOrder_ReleasedNodesZero_PublishesWholeRoute()
    {
        var (service, states, publisher, composer) = Create(releasedNodes: 0, nodes: 7);
        states.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot(
            null,
            lastNodeId: "wa0",
            positionX: 0,
            positionY: 0);

        var order = await service.CreateOrderAsync("801", OrderTestData.Serial, "wa4", false, CancellationToken.None);

        var message = Assert.Single(publisher.Orders);
        using var document = System.Text.Json.JsonDocument.Parse(message.Payload);
        Assert.Equal(7, document.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Equal(6, document.RootElement.GetProperty("edges").GetArrayLength());

        // Весь маршрут в одном сообщении: единственный запрос сборки без нарезки.
        var request = Assert.Single(composer.Requests);
        Assert.Equal(0, request.SliceStartIndex);
        Assert.Equal(0, request.SliceNodeCount);

        Assert.Equal(7, order.Nodes.Count);
        Assert.All(order.Nodes, node => Assert.True(node.Released));
        Assert.Equal(7, order.ReleasedNodeCount);
    }

    [Fact]
    public async Task CreateOrder_ReleasedNodesThree_PublishesFirstChunkOnly()
    {
        var (service, states, publisher, composer) = Create(releasedNodes: 3, nodes: 7);
        states.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot(
            null,
            lastNodeId: "wa0",
            positionX: 0,
            positionY: 0);

        var order = await service.CreateOrderAsync("801", OrderTestData.Serial, "wa4", false, CancellationToken.None);

        var message = Assert.Single(publisher.Orders);
        using var document = System.Text.Json.JsonDocument.Parse(message.Payload);
        Assert.Equal(3, document.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Equal(2, document.RootElement.GetProperty("edges").GetArrayLength());

        var request = composer.Requests[^1];
        Assert.Equal(0, request.SliceStartIndex);
        Assert.Equal(3, request.SliceNodeCount);

        // В PlanJson остается полный план, выпущены только узлы первого куска.
        Assert.Equal(7, order.Nodes.Count);
        Assert.Equal(3, order.ReleasedNodeCount);
        Assert.Equal(3, order.Nodes.Count(node => node.Released));
        Assert.Contains("\"wa12\"", order.PlanJson);
    }

    [Fact]
    public async Task CreateOrder_TargetNode_IsStoredForCompletion()
    {
        var (service, states, _, _) = Create(releasedNodes: 0, nodes: 2);
        states.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot(
            null,
            lastNodeId: "wa0",
            positionX: 0,
            positionY: 0);

        var order = await service.CreateOrderAsync("801", OrderTestData.Serial, "wa4", false, CancellationToken.None);

        Assert.Equal("wa4", order.TargetNodeId);
        Assert.Equal(RobotOrderStatuses.Sent, order.Status);
    }

    private static (RobotOrderService Service, FakeStateReader States, FakePublisher Publisher, FakeComposer Composer) Create(
        int releasedNodes,
        int nodes)
    {
        var orders = new FakeOrderRepository();
        var states = new FakeStateReader();
        var publisher = new FakePublisher();
        var composer = new FakeComposer();
        var options = PlanningFixtures.Options();
        options.Order = new OrderOptions { ReleasedNodes = releasedNodes };
        var service = new RobotOrderService(
            new FakePlanner(nodes),
            composer,
            new HeaderIds(),
            new FakeGraphRepository(Graph()),
            new FakeNumberSource(),
            publisher,
            orders,
            states,
            new FixedOptions<RoutePlannerOptions>(options),
            new TestTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<RobotOrderService>.Instance,
            new FakeTrafficCoordinator());

        return (service, states, publisher, composer);
    }

    private static VR.RoutePlanner.Domain.Graph.WarehouseGraph Graph()
    {
        var nodes = Enumerable.Range(0, 5)
            .Select(index => PlanningFixtures.Node(Guid.NewGuid(), $"wa{index}", index, index))
            .ToList();

        return PlanningFixtures.Graph(nodes, []);
    }
}
