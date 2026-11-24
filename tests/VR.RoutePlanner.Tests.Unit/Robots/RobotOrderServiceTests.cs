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
        bool firstNodeTheta = false,
        Application.Traffic.ITrafficCoordinator? traffic = null)
    {
        var created = CreateWithTraffic(nodes, releasedNodes, firstNodeTheta, traffic);
        return (created.Service, created.Orders, created.Time, created.States);
    }

    internal static (
        RobotOrderService Service,
        FakeOrderRepository Orders,
        TestTimeProvider Time,
        FakeStateReader States,
        FakeTrafficCoordinator Traffic) CreateWithTraffic(
        int nodes = 3,
        int releasedNodes = 3,
        bool firstNodeTheta = false,
        Application.Traffic.ITrafficCoordinator? traffic = null,
        OrderOptions? order = null,
        VR.RoutePlanner.Domain.Graph.WarehouseGraph? graph = null,
        IVdaOrderComposer? composer = null,
        FakePublisher? publisher = null,
        Application.Planning.IPathPlanner? planner = null,
        IRobotOrderNumberSource? numbers = null)
    {
        var orders = new FakeOrderRepository();
        var states = new FakeStateReader();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var trafficFake = new FakeTrafficCoordinator();
        var options = Planning.PlanningFixtures.Options();
        options.Order = order ?? new OrderOptions { ReleasedNodes = releasedNodes, FirstNodeTheta = firstNodeTheta };
        var service = new RobotOrderService(
            planner ?? new FakePlanner(nodes),
            composer ?? new FakeComposer(),
            new HeaderIds(),
            new FakeGraphRepository(graph ?? Graph()),
            numbers ?? new FakeNumberSource(),
            publisher ?? new FakePublisher(),
            orders,
            states,
            new FixedOptions<RoutePlannerOptions>(options),
            time,
            NullLogger<RobotOrderService>.Instance,
            traffic ?? trafficFake);

        return (service, orders, time, states, trafficFake);
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
/// Автомат статусов по таблице §3.1 спеки шага 3: переходы 1–16, АС-8, АС-9, АС-10,
/// сериализация сообщений одного робота и связь с трафиком.
/// </summary>
public sealed class RobotOrderStateMachineTests
{
    [Fact]
    public async Task Row1_Sent_ConfirmingState_BecomesAccepted()
    {
        var (service, orders, time, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order();
        orders.Orders[order.OrderId] = order;

        var accepted = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, orderUpdateId: 0),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Accepted, accepted!.Status);
        Assert.Equal(time.Now, accepted.AcceptedAt);
    }

    [Fact]
    public async Task Row2_Sent_BlockingErrorBeforeMove_Rejected()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order();
        orders.Orders[order.OrderId] = order;

        var rejected = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, errors: ["E|нет связи"]),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Rejected, rejected!.Status);
        Assert.Equal(RobotOrderService.ReasonRobotError, rejected.FailureReason);
    }

    [Fact]
    public async Task Row3_Sent_BlockingErrorAfterMove_Failed()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order();
        orders.Orders[order.OrderId] = order;

        var failed = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, errors: ["E|нет связи"], lastNodeId: "wa2"),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Failed, failed!.Status);
        Assert.Equal(RobotOrderService.ReasonRobotError, failed.FailureReason);
    }

    [Fact]
    public async Task Row4_Accepted_DrivingOrReachedNode_BecomesExecuting()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Accepted);
        orders.Orders[order.OrderId] = order;

        var driving = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, driving: true),
            CancellationToken.None);
        Assert.Equal(RobotOrderStatuses.Executing, driving!.Status);

        var (second, secondOrders, _, _) = RobotOrderServiceTests.Create();
        secondOrders.Orders[order.OrderId] = order;
        var reached = await second.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, lastNodeId: "wa2"),
            CancellationToken.None);
        Assert.Equal(RobotOrderStatuses.Executing, reached!.Status);
    }

    [Fact]
    public async Task Row5_Executing_BlockingError_Failed()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing);
        orders.Orders[order.OrderId] = order;

        var failed = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, errors: ["FATAL|авария"]),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Failed, failed!.Status);
        Assert.Equal(RobotOrderService.ReasonRobotError, failed.FailureReason);
    }

    [Fact]
    public async Task Row6_Executing_OnLastNodeOfIntermediateLeg_StaysExecuting()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing, targetNodeId: "wa999");
        orders.Orders[order.OrderId] = order;

        // lastNodeId совпадает с последним узлом сообщения (конец плеча), но не с целью задачи.
        var result = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, lastNodeId: "wa4", lastNodeSequenceId: 4),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Executing, result!.Status);
        Assert.Null(result.CompletedAt);
    }

    [Fact]
    public async Task Row6_Executing_OnLastNodeAtTaskTarget_Completed()
    {
        var (service, orders, time, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing, targetNodeId: "wa4");
        orders.Orders[order.OrderId] = order;

        var completed = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, lastNodeId: "wa4", lastNodeSequenceId: 4),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Completed, completed!.Status);
        Assert.Equal(time.Now, completed.CompletedAt);
        Assert.All(completed.Nodes, node => Assert.True(node.Reached));
    }

    [Fact]
    public async Task Row6_Executing_AtTaskTargetButNotLastNodeOfMessage_StaysExecuting()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();

        // Цель задачи встречается в середине сообщения: последний узел не достигнут.
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing, targetNodeId: "wa2");
        orders.Orders[order.OrderId] = order;

        var result = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, lastNodeId: "wa2", lastNodeSequenceId: 2),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Executing, result!.Status);
    }

    [Fact]
    public async Task Row7_EmptyOrderId_OnLastPlanNodeNotTarget_KeepsOrder()
    {
        var (service, orders, time, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing, targetNodeId: "wa999");
        orders.Orders[order.OrderId] = order;

        var result = await service.ApplyStateAsync(
            OrderTestData.Snapshot(null, lastNodeId: "wa4", receivedAt: time.Now),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(RobotOrderStatuses.Executing, orders.Orders[order.OrderId].Status);
    }

    [Fact]
    public async Task Row6_Executing_RunningAction_DoesNotComplete()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing);
        orders.Orders[order.OrderId] = order;

        var state = OrderTestData.Snapshot(order.OrderId, lastNodeId: "wa4") with
        {
            ActionStates = [new RobotActionState("act-1", "pick", "RUNNING")],
        };

        var stillExecuting = await service.ApplyStateAsync(state, CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Executing, stillExecuting!.Status);
    }

    [Fact]
    public async Task Row7_EmptyOrderId_AtTarget_ClosesOrder()
    {
        var (service, orders, time, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing);
        orders.Orders[order.OrderId] = order;

        var completed = await service.ApplyStateAsync(
            OrderTestData.Snapshot(null, lastNodeId: "wa4", receivedAt: time.Now),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Completed, completed!.Status);
        Assert.Equal(RobotOrderStatuses.Completed, orders.Orders[order.OrderId].Status);
        Assert.Equal(time.Now, completed.CompletedAt);
    }

    [Fact]
    public async Task Row7_EmptyOrderId_NotAtTarget_KeepsOrder()
    {
        var (service, orders, time, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing);
        orders.Orders[order.OrderId] = order;

        var result = await service.ApplyStateAsync(
            OrderTestData.Snapshot(null, lastNodeId: "wa2", receivedAt: time.Now),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(RobotOrderStatuses.Executing, orders.Orders[order.OrderId].Status);
    }

    [Fact]
    public async Task Row8_CancelOrderFinished_Cancelled()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing);
        orders.Orders[order.OrderId] = order;

        var state = OrderTestData.Snapshot(order.OrderId, driving: true) with
        {
            ActionStates = [new RobotActionState("act-9", "cancelOrder", "FINISHED")],
        };

        var cancelled = await service.ApplyStateAsync(state, CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Cancelled, cancelled!.Status);
    }

    [Fact]
    public async Task Row11_HigherOrderUpdate_DoesNotExtendPlan()
    {
        var plan = OrderTestData.Plan(3);
        var (service, orders, _, _) = RobotOrderServiceTests.Create(releasedNodes: 3);
        var order = OrderTestData.Order(
            status: RobotOrderStatuses.Executing,
            planJson: plan) with
        {
            OrderUpdateId = 0,
            ReleasedNodeCount = 3,
        };
        orders.Orders[order.OrderId] = order;

        var updated = await service.ApplyStateAsync(
            OrderTestData.Snapshot(
                order.OrderId,
                orderUpdateId: 1,
                driving: true,
                nodes: [("wa0", 0, true), ("wa2", 2, true)],
                lastNodeId: "wa2",
                lastNodeSequenceId: 2),
            CancellationToken.None);

        Assert.Equal(0, updated!.OrderUpdateId);
        Assert.Equal(plan, updated.PlanJson);
        Assert.Equal(0, orders.Orders[order.OrderId].OrderUpdateId);
    }

    [Fact]
    public async Task Row16_TerminalOrder_IgnoresFurtherStates()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Completed);
        orders.Orders[order.OrderId] = order;

        var result = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, errors: ["E|ошибка"], lastNodeId: "wa2"),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Completed, result!.Status);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task MergeNodes_EmptyLastNodeId_DoesNotTakeReachedFromReleased()
    {
        var (service, orders, _, _) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order();
        orders.Orders[order.OrderId] = order;

        var state = OrderTestData.Snapshot(
            order.OrderId,
            nodes: [("wa0", 0, true), ("wa2", 2, true), ("wa4", 4, true)]) with
        {
            LastNodeId = null,
        };

        var updated = await service.ApplyStateAsync(state, CancellationToken.None);

        Assert.All(updated!.Nodes, node => Assert.False(node.Reached));
        Assert.All(updated.Nodes, node => Assert.True(node.Released));
        Assert.Equal(RobotOrderStatuses.Accepted, updated.Status);
    }

    [Fact]
    public async Task CheckTimeouts_ProgressTimeout_CountedFromLastProgress()
    {
        var (service, orders, time, states) = RobotOrderServiceTests.Create();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing);
        orders.Orders[order.OrderId] = order;

        var start = OrderTestData.Snapshot(order.OrderId, driving: true, lastNodeId: "wa2", receivedAt: time.Now);
        states.Snapshots[OrderTestData.Serial] = start;
        await service.ApplyStateAsync(start, CancellationToken.None);

        time.Now = time.Now.AddSeconds(120);
        states.Snapshots[OrderTestData.Serial] = start with { ReceivedAt = time.Now };

        var failed = await service.CheckTimeoutsAsync(CancellationToken.None);

        Assert.Equal(1, failed);
        Assert.Equal(RobotOrderService.ReasonProgressTimeout, orders.Orders[order.OrderId].FailureReason);
    }

    [Fact]
    public async Task CheckTimeouts_GrowingLastNodeId_KeepsExecuting()
    {
        var (service, orders, time, states) = RobotOrderServiceTests.Create(nodes: 5, releasedNodes: 5);
        var order = OrderTestData.Order(
            status: RobotOrderStatuses.Executing,
            planJson: OrderTestData.Plan(5),
            targetNodeId: "wa8") with
        {
            Nodes =
            [
                OrderTestData.Node("ORD_04331", 0, "wa0"),
                OrderTestData.Node("ORD_04331", 2, "wa2"),
                OrderTestData.Node("ORD_04331", 4, "wa4"),
                OrderTestData.Node("ORD_04331", 6, "wa6"),
                OrderTestData.Node("ORD_04331", 8, "wa8"),
            ],
        };
        orders.Orders[order.OrderId] = order;

        // 150 секунд пути при ProgressTimeoutSeconds = 60: прогресс идет, заказ жив.
        foreach (var nodeId in new[] { "wa2", "wa4", "wa6" })
        {
            time.Now = time.Now.AddSeconds(50);
            var state = OrderTestData.Snapshot(
                order.OrderId,
                driving: true,
                lastNodeId: nodeId,
                lastNodeSequenceId: nodeId == "wa2" ? 2 : nodeId == "wa4" ? 4 : 6,
                receivedAt: time.Now);
            states.Snapshots[OrderTestData.Serial] = state;
            await service.ApplyStateAsync(state, CancellationToken.None);

            Assert.Equal(0, await service.CheckTimeoutsAsync(CancellationToken.None));
            Assert.Equal(RobotOrderStatuses.Executing, orders.Orders[order.OrderId].Status);
        }
    }

    [Fact]
    public async Task CheckTimeouts_FailedByTimeout_ReleasesTraffic()
    {
        var (service, orders, time, _, traffic) = RobotOrderServiceTests.CreateWithTraffic();
        var order = OrderTestData.Order();
        orders.Orders[order.OrderId] = order;
        time.Now = order.SentAt!.Value.AddSeconds(11);

        await service.CheckTimeoutsAsync(CancellationToken.None);

        Assert.Contains(OrderTestData.Serial, traffic.Releases);
    }

    [Fact]
    public async Task ApplyState_Completed_ReleasesTraffic()
    {
        var (service, orders, _, _, traffic) = RobotOrderServiceTests.CreateWithTraffic();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing);
        orders.Orders[order.OrderId] = order;

        await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, lastNodeId: "wa4"),
            CancellationToken.None);

        Assert.Contains(OrderTestData.Serial, traffic.Releases);
    }

    [Fact]
    public async Task ApplyState_TrafficBlocked_DoesNotFailOrder()
    {
        var (service, orders, _, _, traffic) = RobotOrderServiceTests.CreateWithTraffic();
        var order = OrderTestData.Order(status: RobotOrderStatuses.Executing);
        orders.Orders[order.OrderId] = order;
        traffic.StateResult = Application.Traffic.TrafficReservationResult.Blocked(
            "98009",
            Guid.NewGuid(),
            "801-C1-1");

        var updated = await service.ApplyStateAsync(
            OrderTestData.Snapshot(order.OrderId, driving: true),
            CancellationToken.None);

        Assert.Equal(RobotOrderStatuses.Executing, updated!.Status);
        Assert.Null(updated.FailureReason);
    }

    [Fact]
    public async Task ApplyState_TwoStatesInParallel_PublishSingleExtension()
    {
        var plan = OrderTestData.Plan(6);
        var (service, orders, _, _) = RobotOrderServiceTests.Create(nodes: 6, releasedNodes: 3);
        var order = OrderTestData.Order(
            status: RobotOrderStatuses.Executing,
            planJson: plan,
            targetNodeId: "wa10") with
        {
            Nodes =
            [
                OrderTestData.Node("ORD_04331", 0, "wa0", reached: true),
                OrderTestData.Node("ORD_04331", 2, "wa2", reached: true),
                OrderTestData.Node("ORD_04331", 4, "wa4"),
                OrderTestData.Node("ORD_04331", 6, "wa6"),
                OrderTestData.Node("ORD_04331", 8, "wa8"),
                OrderTestData.Node("ORD_04331", 10, "wa10"),
            ],
        };
        orders.Orders[order.OrderId] = order;

        var state = OrderTestData.Snapshot(
            order.OrderId,
            driving: true,
            nodes: [("wa0", 0, true), ("wa2", 2, true)],
            lastNodeId: "wa2",
            lastNodeSequenceId: 2,
            newBaseRequest: true);

        await Task.WhenAll(
            service.ApplyStateAsync(state, CancellationToken.None),
            service.ApplyStateAsync(state, CancellationToken.None));

        var publisher = (FakePublisher)typeof(RobotOrderService)
            .GetField("_publisher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(service)!;

        Assert.Single(publisher.Orders);
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

    [Fact]
    public async Task CreateOrder_TrafficBlocked_Returns409()
    {
        var created = RobotOrderServiceTests.CreateWithTraffic();
        created.Traffic.OrderResult = Application.Traffic.TrafficReservationResult.Blocked(
            "98009",
            Guid.NewGuid(),
            "801-tusk-forklift-1102-C1-1");
        created.States.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot(null);
        var controller = new AgvController(created.Service, created.States, created.Orders);

        var result = await controller.CreateOrderAsync(
            OrderTestData.Serial,
            new AgvOrderBody("801", "wa4", false),
            CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(created.Orders.Orders);
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
