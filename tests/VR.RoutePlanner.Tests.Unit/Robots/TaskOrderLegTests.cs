using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Domain.Robots;
using VR.RoutePlanner.Infrastructure.Kafka;
using VR.RoutePlanner.Infrastructure.Legacy;
using VR.RoutePlanner.Infrastructure.Mqtt;
using VR.RoutePlanner.Tests.Unit.Planning;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Один заказ на задачу WMS, плечи как order update (spec-order-updates, критерии 1-3):
/// нумерация от базы, поза робота в первом узле, действие вил только в цели плеча.
/// </summary>
public sealed class TaskOrderBuilderTests
{
    [Fact]
    public void SequenceIdBase_ContinuesNumbering()
    {
        var (graph, first, second, a, _, _) = OrderFixtures.TwoEdgeGraph();
        var route = OrderFixtures.Route(first.EdgeId, second.EdgeId);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            orderUpdateId: 1,
            robotX: a.X,
            robotY: a.Y,
            sequenceIdBase: 12,
            firstNodeIsPose: false));

        Assert.Equal([12, 14, 16], Sequences(order, "nodes"));
        Assert.Equal([13, 15], Sequences(order, "edges"));
    }

    [Fact]
    public void SequenceIdBase_WithVirtualStart_VirtualNodeTakesBase()
    {
        var (graph, first, second, a, _, _) = OrderFixtures.TwoEdgeGraph();
        var route = OrderFixtures.Route(first.EdgeId, second.EdgeId);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            orderUpdateId: 1,
            robotX: a.X - 5,
            robotY: a.Y,
            sequenceIdBase: 12,
            firstNodeIsPose: false));

        var nodes = order["nodes"]!.AsArray();
        Assert.Equal(12, nodes[0]!["sequenceId"]!.GetValue<int>());
        Assert.Equal(a.X - 5, nodes[0]!["nodePosition"]!["x"]!.GetValue<double>(), 9);
        Assert.Equal(a.NodeId, nodes[1]!["nodeId"]!.GetValue<string>());
        Assert.Equal(14, nodes[1]!["sequenceId"]!.GetValue<int>());
        Assert.Equal(13, order["edges"]!.AsArray()[0]!["sequenceId"]!.GetValue<int>());
    }

    [Fact]
    public void UpdateWithPose_FirstNodeIsRobotPoseWithWideDeviation()
    {
        var (graph, first, second, a, _, _) = OrderFixtures.TwoEdgeGraph();
        var route = OrderFixtures.Route(first.EdgeId, second.EdgeId);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            orderUpdateId: 1,
            firstNodeTheta: true,
            robotX: a.X + 0.2,
            robotY: a.Y - 0.1,
            robotTheta: 1.25,
            sequenceIdBase: 12,
            firstNodeIsPose: true));

        var nodes = order["nodes"]!.AsArray();
        var pose = nodes[0]!["nodePosition"]!;
        Assert.Equal(12, nodes[0]!["sequenceId"]!.GetValue<int>());
        Assert.Equal(a.X + 0.2, pose["x"]!.GetValue<double>(), 9);
        Assert.Equal(a.Y - 0.1, pose["y"]!.GetValue<double>(), 9);
        Assert.Equal(1.25, pose["theta"]!.GetValue<double>(), 9);
        Assert.Equal(VdaOrderBuilder.FirstNodeDeviationXy, pose["allowedDeviationXY"]!.GetValue<double>(), 9);
        Assert.Equal(VdaOrderBuilder.FirstNodeDeviationTheta, pose["allowedDeviationTheta"]!.GetValue<double>(), 9);

        foreach (var node in nodes.Skip(1))
        {
            Assert.Equal(VdaOrderBuilder.NodeDeviationXy, node!["nodePosition"]!["allowedDeviationXY"]!.GetValue<double>(), 9);
            Assert.Equal(VdaOrderBuilder.NodeDeviationTheta, node["nodePosition"]!["allowedDeviationTheta"]!.GetValue<double>(), 9);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("detectObject")]
    public void UpdateToWorkingNode_ForkActionOnlyInLastNode(string? terminal)
    {
        var (graph, edge, start, _) = OrderFixtures.SlotGraph("LOADING");
        var route = OrderFixtures.Route([(edge.EdgeId, RouteEdgeDirection.Forward)]);

        var order = VdaOrderBuilder.Build(OrderFixtures.Request(
            route,
            graph,
            orderUpdateId: 1,
            robotX: start.X,
            robotY: start.Y,
            terminalActionType: terminal,
            sequenceIdBase: 12,
            firstNodeIsPose: true));

        var nodes = order["nodes"]!.AsArray();
        var withActions = nodes.Where(node => node!["actions"]!.AsArray().Count > 0).ToList();
        Assert.Single(withActions);
        Assert.Same(nodes[^1], withActions[0]);

        var types = ActionTypes(nodes[^1]!);
        Assert.Contains("pick", types);
        Assert.Equal(terminal is not null, types.Contains("detectObject"));
    }

    internal static int[] Sequences(JsonObject order, string array) =>
        order[array]!.AsArray().Select(item => item!["sequenceId"]!.GetValue<int>()).ToArray();

    internal static List<string> ActionTypes(JsonNode node) =>
        node["actions"]!.AsArray().Select(action => action!["actionType"]!.GetValue<string>()).ToList();
}

/// <summary>
/// Граф задачи: старт <c>wa500</c> (0, 0) -> подбор <c>wo-5-1</c> (4, 0) -> <c>wa501</c> (4, 3)
/// -> выгрузка <c>wo-5-3</c> (8, 3). Планировщик отдает маршруты по настоящим ребрам.
/// </summary>
internal sealed class TaskOrderStand
{
    /// <summary>Отступ точки остановки у рабочего узла (длина корпуса).</summary>
    public const double StopOffset = 1.475;

    public TaskOrderStand(OrderOptions? order = null)
    {
        Start = OrderFixtures.Node("wa500", 0, 0);
        Pick = OrderFixtures.Node("wo-5-1", 4, 0, "LOADING");
        Middle = OrderFixtures.Node("wa501", 4, 3);
        Drop = OrderFixtures.Node("wo-5-3", 8, 3, "UNLOADING");
        var toPick = OrderFixtures.Edge(Start, Pick, "801-wa500-wo-5-1");
        var toMiddle = OrderFixtures.Edge(Pick, Middle, "801-wo-5-1-wa501");
        var toDrop = OrderFixtures.Edge(Middle, Drop, "801-wa501-wo-5-3");
        var graph = OrderFixtures.Graph([Start, Pick, Middle, Drop], [toPick, toMiddle, toDrop]);

        Planner.Routes[(Start.Id, Pick.Id)] = OrderFixtures.Route([(toPick.EdgeId, RouteEdgeDirection.Forward)]);
        Planner.Routes[(Pick.Id, Drop.Id)] = OrderFixtures.Route(
            [(toMiddle.EdgeId, RouteEdgeDirection.Forward), (toDrop.EdgeId, RouteEdgeDirection.Forward)]);

        var created = RobotOrderServiceTests.CreateWithTraffic(
            order: order ?? new OrderOptions(),
            graph: graph,
            composer: new VdaOrderComposer(),
            publisher: Publisher,
            planner: Planner,
            numbers: Numbers);
        Service = created.Service;
        Orders = created.Orders;
        Time = created.Time;
        States = created.States;
        Traffic = created.Traffic;
    }

    public WarehouseNode Start { get; }

    public WarehouseNode Pick { get; }

    public WarehouseNode Middle { get; }

    public WarehouseNode Drop { get; }

    public TablePlanner Planner { get; } = new();

    public FakePublisher Publisher { get; } = new();

    public FakeNumberSource Numbers { get; } = new();

    public RobotOrderService Service { get; }

    public FakeOrderRepository Orders { get; }

    public TestTimeProvider Time { get; }

    public FakeStateReader States { get; }

    public FakeTrafficCoordinator Traffic { get; }

    /// <summary>State робота: узел, позиция, заказ.</summary>
    public RobotSnapshot State(
        string lastNodeId,
        double x,
        double y,
        string? orderId = null,
        int orderUpdateId = 0,
        bool driving = false,
        int? lastNodeSequenceId = null) =>
        States.Snapshots[OrderTestData.Serial] = OrderTestData.Snapshot(
            orderId,
            orderUpdateId,
            driving,
            receivedAt: Time.Now,
            lastNodeId: lastNodeId,
            lastNodeSequenceId: lastNodeSequenceId,
            positionX: x,
            positionY: y);

    /// <summary>Робот стоит в точке остановки у узла подбора, первое плечо подтверждено.</summary>
    public RobotSnapshot AtPick(string orderId, int orderUpdateId = 0, bool driving = false) =>
        State(Pick.NodeId, Pick.X - StopOffset, Pick.Y, orderId, orderUpdateId, driving, lastNodeSequenceId: 2);

    /// <summary>Первое плечо задачи: старт -> подбор, цель задачи - выгрузка.</summary>
    public async Task<RobotOrder> CreateFirstLegAsync()
    {
        State(Start.NodeId, Start.X, Start.Y);
        return await Service.CreateTaskOrderAsync(
            OrderFixtures.MapId,
            OrderTestData.Serial,
            Pick.NodeId,
            false,
            Drop.NodeId,
            CancellationToken.None);
    }

    public JsonObject Message(int index) => JsonNode.Parse(Publisher.Orders[index].Payload)!.AsObject();
}

/// <summary>
/// Сервис заказа задачи (spec-order-updates, критерии 4-7, 9, 10).
/// </summary>
public sealed class TaskOrderServiceTests
{
    [Fact]
    public async Task CreateTaskOrder_PublishesOneMessage_KeepsFinalTarget()
    {
        var stand = new TaskOrderStand();

        var order = await stand.CreateFirstLegAsync();

        var message = stand.Message(0);
        Assert.Single(stand.Publisher.Orders);
        Assert.Equal(0, message["orderUpdateId"]!.GetValue<int>());
        Assert.Equal(order.OrderId, message["orderId"]!.GetValue<string>());
        Assert.Equal(stand.Drop.NodeId, order.TargetNodeId);
        Assert.Equal(RobotOrderStatuses.Sent, order.Status);
        Assert.Equal(1, stand.Numbers.Calls);

        // Плечо промежуточное: pick в цели плеча, завершающего действия задачи нет.
        var last = message["nodes"]!.AsArray()[^1]!;
        Assert.Equal(stand.Pick.NodeId, last["nodeId"]!.GetValue<string>());
        Assert.Equal(["pick"], TaskOrderBuilderTests.ActionTypes(last));
    }

    [Fact]
    public async Task AppendLeg_PublishesUpdateOfSameOrder_ContinuesNumbering()
    {
        var stand = new TaskOrderStand();
        var first = await stand.CreateFirstLegAsync();
        var firstLast = TaskOrderBuilderTests.Sequences(stand.Message(0), "nodes")[^1];
        var robot = stand.AtPick(first.OrderId);

        var updated = await stand.Service.AppendLegAsync(first.OrderId, stand.Drop.NodeId, true, CancellationToken.None);

        Assert.Equal(2, stand.Publisher.Orders.Count);
        var message = stand.Message(1);
        Assert.Equal(first.OrderId, message["orderId"]!.GetValue<string>());
        Assert.Equal(1, message["orderUpdateId"]!.GetValue<int>());
        Assert.Equal([firstLast, firstLast + 2, firstLast + 4], TaskOrderBuilderTests.Sequences(message, "nodes"));
        Assert.Equal([firstLast + 1, firstLast + 3], TaskOrderBuilderTests.Sequences(message, "edges"));

        var nodes = message["nodes"]!.AsArray();
        Assert.Equal(robot.PositionX, nodes[0]!["nodePosition"]!["x"]!.GetValue<double>(), 9);
        Assert.Empty(nodes[0]!["actions"]!.AsArray());
        Assert.Empty(nodes[1]!["actions"]!.AsArray());
        Assert.Equal(stand.Drop.NodeId, nodes[^1]!["nodeId"]!.GetValue<string>());
        Assert.Contains("drop", TaskOrderBuilderTests.ActionTypes(nodes[^1]!));
        Assert.Contains("detectObject", TaskOrderBuilderTests.ActionTypes(nodes[^1]!));

        Assert.Equal(1, updated.OrderUpdateId);
        Assert.Equal(stand.Drop.NodeId, updated.TargetNodeId);
        Assert.Equal(1, stand.Numbers.Calls);
    }

    [Fact]
    public async Task AppendLeg_RobotDriving_RejectsWithoutPublishing()
    {
        var stand = new TaskOrderStand();
        var first = await stand.CreateFirstLegAsync();
        stand.AtPick(first.OrderId, driving: true);

        var error = await Assert.ThrowsAsync<OrderRejectedException>(
            () => stand.Service.AppendLegAsync(first.OrderId, stand.Drop.NodeId, true, CancellationToken.None));

        Assert.Equal(RobotOrderService.ReasonLegNotFinished, error.Reason);
        Assert.Single(stand.Publisher.Orders);
        Assert.Equal(0, stand.Orders.Orders[first.OrderId].OrderUpdateId);
    }

    [Fact]
    public async Task AppendLeg_RobotNotAtLegTarget_RejectsWithoutPublishing()
    {
        var stand = new TaskOrderStand();
        var first = await stand.CreateFirstLegAsync();
        stand.State(stand.Start.NodeId, stand.Start.X, stand.Start.Y, first.OrderId, lastNodeSequenceId: 0);

        Assert.False(await stand.Service.IsLegFinishedAsync(first.OrderId, CancellationToken.None));
        var error = await Assert.ThrowsAsync<OrderRejectedException>(
            () => stand.Service.AppendLegAsync(first.OrderId, stand.Drop.NodeId, true, CancellationToken.None));

        Assert.Equal(RobotOrderService.ReasonLegNotFinished, error.Reason);
        Assert.Single(stand.Publisher.Orders);
    }

    [Fact]
    public async Task AppendLeg_RobotAlreadyInWorkingTarget_PublishesStationaryUpdate()
    {
        var stand = new TaskOrderStand();
        var first = await stand.CreateFirstLegAsync();
        var firstLast = TaskOrderBuilderTests.Sequences(stand.Message(0), "nodes")[^1];
        stand.AtPick(first.OrderId);

        await stand.Service.AppendLegAsync(first.OrderId, stand.Pick.NodeId, false, CancellationToken.None);

        var message = stand.Message(1);
        var nodes = message["nodes"]!.AsArray();
        Assert.Equal(1, message["orderUpdateId"]!.GetValue<int>());
        Assert.Equal([firstLast, firstLast + 2], TaskOrderBuilderTests.Sequences(message, "nodes"));
        Assert.All(nodes, node => Assert.Equal(stand.Pick.NodeId, node!["nodeId"]!.GetValue<string>()));
        Assert.Empty(nodes[0]!["actions"]!.AsArray());
        Assert.Equal(["pick"], TaskOrderBuilderTests.ActionTypes(nodes[1]!));

        var edge = Assert.Single(message["edges"]!.AsArray())!;
        Assert.Equal(firstLast + 1, edge["sequenceId"]!.GetValue<int>());
        Assert.StartsWith("VIRTUAL_START", edge["edgeId"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckTimeouts_UpdateNotAcceptedInTime_FailsAndReleasesTraffic()
    {
        var stand = new TaskOrderStand();
        var first = await stand.CreateFirstLegAsync();
        stand.Time.Now += TimeSpan.FromSeconds(5);
        stand.AtPick(first.OrderId);
        await stand.Service.AppendLegAsync(first.OrderId, stand.Drop.NodeId, true, CancellationToken.None);

        // Через 7 с робот еще может принять обновление.
        stand.Time.Now += TimeSpan.FromSeconds(7);
        stand.AtPick(first.OrderId);
        Assert.Equal(0, await stand.Service.CheckTimeoutsAsync(CancellationToken.None));

        // Через 11 с orderUpdateId в state так и остался 0.
        stand.Time.Now += TimeSpan.FromSeconds(4);
        stand.AtPick(first.OrderId);
        Assert.Equal(1, await stand.Service.CheckTimeoutsAsync(CancellationToken.None));

        var order = stand.Orders.Orders[first.OrderId];
        Assert.Equal(RobotOrderStatuses.Failed, order.Status);
        Assert.Equal(RobotOrderService.ReasonUpdateNotAccepted, order.FailureReason);
        Assert.Contains(OrderTestData.Serial, stand.Traffic.Releases);
        Assert.Equal(2, stand.Publisher.Orders.Count);
        Assert.Empty(stand.Publisher.Actions);
    }

    [Fact]
    public async Task CheckTimeouts_UpdateAccepted_KeepsOrder()
    {
        var stand = new TaskOrderStand();
        var first = await stand.CreateFirstLegAsync();
        stand.Time.Now += TimeSpan.FromSeconds(5);
        stand.AtPick(first.OrderId);
        await stand.Service.AppendLegAsync(first.OrderId, stand.Drop.NodeId, true, CancellationToken.None);

        stand.Time.Now += TimeSpan.FromSeconds(11);
        stand.AtPick(first.OrderId, orderUpdateId: 1, driving: true);

        Assert.Equal(0, await stand.Service.CheckTimeoutsAsync(CancellationToken.None));
        Assert.NotEqual(RobotOrderStatuses.Failed, stand.Orders.Orders[first.OrderId].Status);
    }

    [Fact]
    public async Task CreateOrder_RobotKeepsPreviousOrderId_PublishesWithoutCancelAndDelay()
    {
        var stand = new TaskOrderStand();
        var started = stand.Time.Now;
        stand.State(stand.Start.NodeId, stand.Start.X, stand.Start.Y, orderId: "ORD_00001", orderUpdateId: 3);

        var order = await stand.Service.CreateOrderAsync(
            OrderFixtures.MapId,
            OrderTestData.Serial,
            stand.Pick.NodeId,
            false,
            CancellationToken.None);

        Assert.Empty(stand.Publisher.Actions);
        Assert.Single(stand.Publisher.Orders);
        Assert.Equal(started, stand.Time.Now);
        Assert.Equal(RobotOrderStatuses.Sent, order.Status);
        Assert.Equal(stand.Pick.NodeId, order.TargetNodeId);
    }
}

/// <summary>
/// Обработчик задач WMS (spec-order-updates, критерий 11): задача из двух действий дает
/// один заказ, второе действие уходит обновлением того же заказа.
/// </summary>
public sealed class SimpleTaskWorkerLegTests
{
    [Fact]
    public async Task PublishLegs_TwoActions_OneOrderTwoMessages()
    {
        var stand = new TaskOrderStand();

        // Первое чтение state - робот в старте без заказа; дальше он уже в узле подбора
        // и подтвердил первое плечо (номер заказа известен: FakeNumberSource начинает с 4331).
        stand.States.Sequence.Enqueue(OrderTestData.Snapshot(
            null,
            receivedAt: stand.Time.Now,
            lastNodeId: stand.Start.NodeId,
            positionX: stand.Start.X,
            positionY: stand.Start.Y));
        stand.AtPick($"ORD_{stand.Numbers.Next:D5}");

        var worker = new SimpleTaskWorker(
            new FixedOptions<KafkaOptions>(new KafkaOptions()),
            stand.Service,
            new EmptyAgvRepository(),
            new FixedOptions<RoutePlannerOptions>(PlanningFixtures.Options()),
            NullLogger<SimpleTaskWorker>.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
        };
        var task = new WmsTask(
            "T-1",
            OrderFixtures.MapId,
            stand.Time.Now,
            [
                new WmsTaskAction(1, stand.Pick.NodeId, WmsTaskParser.PickUp),
                new WmsTaskAction(2, stand.Drop.NodeId, WmsTaskParser.DropOff),
            ]);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var order = await worker.PublishLegsAsync(task, timeout.Token);

        Assert.Equal(1, stand.Numbers.Calls);
        Assert.Equal(2, stand.Publisher.Orders.Count);
        Assert.All(
            [stand.Message(0), stand.Message(1)],
            message => Assert.Equal(order.OrderId, message["orderId"]!.GetValue<string>()));
        Assert.Equal(0, stand.Message(0)["orderUpdateId"]!.GetValue<int>());
        Assert.Equal(1, stand.Message(1)["orderUpdateId"]!.GetValue<int>());
        Assert.Equal(1, order.OrderUpdateId);
        Assert.Equal(stand.Drop.NodeId, order.TargetNodeId);
        Assert.Single(stand.Orders.Orders);
    }

    private sealed class EmptyAgvRepository : ILegacyAgvRepository
    {
        public Task<IReadOnlyList<LegacyAgvRecord>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LegacyAgvRecord>>([]);

        public Task<LegacyAgvRecord?> FindAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<LegacyAgvRecord?>(null);
    }
}
