using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Api.Contracts;
using VR.RoutePlanner.Api.Controllers;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Tests.Unit.Planning;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Автомат статусов заказа, таймауты, восстановление и HTTP оператора.
/// </summary>
public sealed class RobotOrderServiceTests
{
    [Fact]
    public async Task ApplyState_SentToAcceptedToExecutingToCompleted()
    {
        var (service, orders, _, _) = Create();
        var order = OrderTestData.Order();
        orders.Orders[order.OrderId] = order;

        var accepted = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, orderUpdateId: 1),
            CancellationToken.None);
        Assert.Equal(RobotOrderStatuses.Accepted, accepted!.Status);
        Assert.NotNull(accepted.AcceptedAt);

        var executing = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, orderUpdateId: 1, driving: true),
            CancellationToken.None);
        Assert.Equal(RobotOrderStatuses.Executing, executing!.Status);

        var completed = await service.ApplyStateAsync(
            OrderTestData.Snapshot(
                order.OrderId,
                orderUpdateId: 1,
                nodes:
                [
                    ("wa0", 0, true),
                    ("wa2", 2, true),
                    ("wa4", 4, true),
                ],
                lastNodeId: "wa4"),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Completed, completed!.Status);
        Assert.NotNull(completed.CompletedAt);
        Assert.All(completed.Nodes, node => Assert.True(node.Reached));
    }

    [Fact]
    public async Task ApplyState_RobotErrors_FailOrder()
    {
        var (service, orders, _, _) = Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing);
        orders.Orders[order.OrderId] = order;

        var failed = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, errors: ["препятствие"]),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Failed, failed!.Status);
        Assert.Equal(RobotOrderService.ReasonRobotError, failed.FailureReason);
    }

    [Fact]
    public async Task ApplyState_LowerOrderUpdate_IsIgnored()
    {
        var (service, orders, _, _) = Create();
        var order = OrderTestData.Order() with { OrderUpdateId = 3 };
        orders.Orders[order.OrderId] = order;

        var result = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, orderUpdateId: 1),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Sent, result!.Status);
    }

    [Fact]
    public async Task ApplyState_LastNodeEqualsTarget_MarksCompleted()
    {
        var (service, orders, _, _) = Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing);
        orders.Orders[order.OrderId] = order;

        var completed = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, lastNodeId: "wa4"),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Completed, completed!.Status);
        Assert.NotNull(completed.CompletedAt);
    }

    [Fact]
    public async Task CheckTimeouts_NoAccept_FailsOrder()
    {
        var (service, orders, time, _) = Create();
        var order = OrderTestData.Order();
        orders.Orders[order.OrderId] = order;
        time.Now = order.SentAt!.Value.AddSeconds(11);

        var failed = await service.CheckTimeoutsAsync(CancellationToken.None);

        Assert.Equal(1, failed);
        Assert.Equal(RobotOrderStatuses.Failed, orders.Orders[order.OrderId].Status);
        Assert.Equal(RobotOrderService.ReasonNoAccept, orders.Orders[order.OrderId].FailureReason);
    }

    [Fact]
    public async Task CheckTimeouts_StateSilence_FailsOrder()
    {
        var (service, orders, time, _) = Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing);
        orders.Orders[order.OrderId] = order;
        time.Now = order.SentAt!.Value.AddSeconds(5);

        var failed = await service.CheckTimeoutsAsync(CancellationToken.None);

        Assert.Equal(1, failed);
        Assert.Equal(RobotOrderService.ReasonStateSilence, orders.Orders[order.OrderId].FailureReason);
    }

    [Fact]
    public async Task Recover_MatchingDrivingState_KeepsOrder()
    {
        var (service, orders, time, states) = Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing);
        orders.Orders[order.OrderId] = order;
        states.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot(
            order.OrderId,
            driving: true,
            receivedAt: time.Now);

        var failed = await service.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, failed);
        Assert.Equal(RobotOrderStatuses.Executing, orders.Orders[order.OrderId].Status);
    }

    [Fact]
    public async Task Recover_StateMismatch_FailsOrder()
    {
        var (service, orders, time, states) = Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Sent);
        orders.Orders[order.OrderId] = order;
        states.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot("ORD_09999", receivedAt: time.Now);

        var failed = await service.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, failed);
        Assert.Equal(RobotOrderStatuses.Failed, orders.Orders[order.OrderId].Status);
        Assert.Equal(RobotOrderService.ReasonStateMismatch, orders.Orders[order.OrderId].FailureReason);
    }

    internal static (RobotOrderService Service, FakeOrderRepository Orders, TestTimeProvider Time, FakeStateReader States) Create(
        int nodes = 3,
        int releasedNodes = 3,
        bool firstNodeTheta = false)
    {
        var orders = new FakeOrderRepository();
        var states = new FakeStateReader();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var options = Planning.PlanningFixtures.Options();
        options.Order = new OrderOptions { ReleasedNodes = releasedNodes, FirstNodeTheta = firstNodeTheta };
        var service = new RobotOrderService(
            new FakePlanner(nodes),
            new FakeComposer(),
            new HeaderIds(),
            new FakeGraphRepository(Graph()),
            new FakeNumberSource(),
            new FakePublisher(),
            orders,
            states,
            new FixedOptions<RoutePlannerOptions>(options),
            time,
            NullLogger<RobotOrderService>.Instance);

        return (service, orders, time, states);
    }

    private static VR.RoutePlanner.Domain.Graph.WarehouseGraph Graph()
    {
        var nodes = Enumerable.Range(0, 5)
            .Select(index => Planning.PlanningFixtures.Node(Guid.NewGuid(), $"wa{index}", index, index))
            .ToList();

        return Planning.PlanningFixtures.Graph(nodes, []);
    }
}

/// <summary>
/// HTTP-контракты оператора: вызовы контроллера без хоста.
/// </summary>
public sealed class AgvControllerTests
{
    [Fact]
    public async Task CreateOrder_ReturnsOrder()
    {
        var (controller, orders, states, _) = Create();
        states.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot(null);

        var result = await controller.CreateOrderAsync(
            OrderTestData.Serial,
            new AgvOrderBody("801", "wa4", false),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<AgvOrderResponse>(ok.Value);
        Assert.Equal("ORD_04331", body.OrderId);
        Assert.Equal(RobotOrderStatuses.Sent, body.Status);
        Assert.Equal(3, body.Nodes.Count);
        Assert.True(body.Nodes[0].Released);
        Assert.Contains(body.OrderId, orders.Orders.Keys);
    }

    [Fact]
    public async Task CreateOrder_ActiveOrder_Returns409()
    {
        var (controller, orders, states, _) = Create();
        states.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot(null);
        orders.Orders["ORD_04300"] = OrderTestData.Order("ORD_04300", RobotOrderStatuses.Executing);

        var result = await controller.CreateOrderAsync(
            OrderTestData.Serial,
            new AgvOrderBody("801", "wa4", false),
            CancellationToken.None);

        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_NoRobotState_Returns404()
    {
        var (controller, _, _, _) = Create();

        var result = await controller.CreateOrderAsync(
            OrderTestData.Serial,
            new AgvOrderBody("801", "wa4", false),
            CancellationToken.None);

        var notFound = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, notFound.StatusCode);
    }

    [Fact]
    public async Task GetState_Unknown_Returns404_Known_ReturnsState()
    {
        var (controller, _, states, _) = Create();

        var missing = await controller.GetStateAsync("98099", CancellationToken.None);
        Assert.Equal(404, Assert.IsType<ObjectResult>(missing).StatusCode);

        states.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot("ORD_04331", driving: true);
        var found = await controller.GetStateAsync(OrderTestData.Serial, CancellationToken.None);
        var body = Assert.IsType<AgvStateResponse>(Assert.IsType<OkObjectResult>(found).Value);
        Assert.True(body.Driving);
        Assert.Equal("ORD_04331", body.OrderId);
    }

    [Fact]
    public async Task GetOrder_Unknown_Returns404_OtherRobot_Returns404()
    {
        var (controller, orders, _, _) = Create();
        orders.Orders["ORD_04331"] = OrderTestData.Order();

        var unknown = await controller.GetOrderAsync(OrderTestData.Serial, "ORD_00001", CancellationToken.None);
        Assert.Equal(404, Assert.IsType<ObjectResult>(unknown).StatusCode);

        var other = await controller.GetOrderAsync("98099", "ORD_04331", CancellationToken.None);
        Assert.Equal(404, Assert.IsType<ObjectResult>(other).StatusCode);

        var known = await controller.GetOrderAsync(OrderTestData.Serial, "ORD_04331", CancellationToken.None);
        var body = Assert.IsType<AgvOrderResponse>(Assert.IsType<OkObjectResult>(known).Value);
        Assert.Equal("ORD_04331", body.OrderId);
    }

    [Fact]
    public async Task InstantAction_KnownType_Publishes_UnknownType_Returns400()
    {
        var (controller, orders, _, publisher) = Create();
        orders.Orders["ORD_04331"] = OrderTestData.Order(status: RobotOrderStatuses.Executing);

        var sent = await controller.InstantActionAsync(
            OrderTestData.Serial,
            new AgvInstantActionBody("pause", "act-1"),
            CancellationToken.None);
        var body = Assert.IsType<AgvInstantActionResponse>(Assert.IsType<OkObjectResult>(sent).Value);
        Assert.Equal("act-1", body.ActionId);
        Assert.Single(publisher.Actions);
        Assert.Contains("\"actionType\":\"pause\"", publisher.Actions[0].Payload);

        var unsupported = await controller.InstantActionAsync(
            OrderTestData.Serial,
            new AgvInstantActionBody("fly", null),
            CancellationToken.None);
        Assert.Equal(400, Assert.IsType<ObjectResult>(unsupported).StatusCode);

        await controller.InstantActionAsync(
            OrderTestData.Serial,
            new AgvInstantActionBody("cancelOrder", "act-2"),
            CancellationToken.None);
        Assert.Equal(RobotOrderStatuses.Cancelled, orders.Orders["ORD_04331"].Status);
    }

    [Fact]
    public async Task InstantAction_Payload_HasVdaFields()
    {
        var payload = VdaInstantActionPayload.Build("pause", "act-9", DateTimeOffset.UnixEpoch);
        using var document = System.Text.Json.JsonDocument.Parse(payload);

        Assert.Equal("2.1.0", document.RootElement.GetProperty("version").GetString());
        var action = document.RootElement.GetProperty("actions")[0];
        Assert.Equal("pause", action.GetProperty("actionType").GetString());
        Assert.Equal("act-9", action.GetProperty("actionId").GetString());
    }

    private static (AgvController Controller, FakeOrderRepository Orders, FakeStateReader States, FakePublisher Publisher) Create()
    {
        var (service, orders, _, states) = RobotOrderServiceTests.Create();
        var publisherField = (FakePublisher)typeof(RobotOrderService)
            .GetField("_publisher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(service)!;

        return (new AgvController(service, states, orders), orders, states, publisherField);
    }
}
