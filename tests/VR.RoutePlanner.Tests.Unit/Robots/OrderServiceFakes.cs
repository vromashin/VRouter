using System.Text.Json;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Domain.Robots;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Подставные хранилища, транспорт и время для тестов заказов.
/// </summary>
internal sealed class FakeOrderRepository : IRobotOrderRepository
{
    public Dictionary<string, RobotOrder> Orders { get; } = new(StringComparer.Ordinal);

    public Task SaveAsync(RobotOrder order, CancellationToken ct)
    {
        Orders[order.OrderId] = order;
        return Task.CompletedTask;
    }

    public Task<RobotOrder?> GetAsync(string orderId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Orders.GetValueOrDefault(orderId));
    }

    public Task<RobotOrder?> GetLatestAsync(string agvSerialNumber, CancellationToken ct) =>
        Task.FromResult(Orders.Values
            .Where(order => order.AgvSerialNumber == agvSerialNumber)
            .OrderByDescending(order => order.CreatedAt)
            .FirstOrDefault());

    public Task<IReadOnlyList<RobotOrder>> GetActiveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string[] active = [RobotOrderStatuses.Draft, RobotOrderStatuses.Sent, RobotOrderStatuses.Accepted, RobotOrderStatuses.Executing];
        return Task.FromResult<IReadOnlyList<RobotOrder>>(
            Orders.Values.Where(order => active.Contains(order.Status)).ToList());
    }

    public Task<RobotOrder?> GetActiveForRobotAsync(string agvSerialNumber, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string[] active = [RobotOrderStatuses.Draft, RobotOrderStatuses.Sent, RobotOrderStatuses.Accepted, RobotOrderStatuses.Executing];
        return Task.FromResult(Orders.Values
            .Where(order => order.AgvSerialNumber == agvSerialNumber && active.Contains(order.Status))
            .OrderByDescending(order => order.CreatedAt)
            .FirstOrDefault());
    }
}

internal sealed class FakeStateReader : IRobotStateReader
{
    public Dictionary<string, RobotSnapshot> Snapshots { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Очередь состояний: выдается раньше словаря, нужна сценариям очистки перед новым заказом.
    /// </summary>
    public Queue<RobotSnapshot?> Sequence { get; } = new();

    public Task<RobotSnapshot?> GetLatestAsync(string serialNumber, CancellationToken ct)
    {
        if (Sequence.Count > 0)
        {
            return Task.FromResult(Sequence.Dequeue());
        }

        return Task.FromResult(Snapshots.GetValueOrDefault(serialNumber));
    }

    public Task<IReadOnlyCollection<RobotSnapshot>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyCollection<RobotSnapshot>>(Snapshots.Values.ToList());
}

internal sealed class FakeNumberSource : IRobotOrderNumberSource
{
    public long Next { get; set; } = 4331;

    public Task<long> NextAsync(CancellationToken ct) => Task.FromResult(Next++);
}

internal sealed class FakePublisher : IRobotOrderPublisher
{
    public List<(string Manufacturer, string Serial, string Payload)> Orders { get; } = new();

    public List<(string Serial, string Payload)> Actions { get; } = new();

    public Task PublishAsync(string manufacturer, string serialNumber, string orderJson, CancellationToken ct)
    {
        Orders.Add((manufacturer, serialNumber, orderJson));
        return Task.CompletedTask;
    }

    public Task PublishInstantActionAsync(string manufacturer, string serialNumber, string payload, CancellationToken ct)
    {
        Actions.Add((serialNumber, payload));
        return Task.CompletedTask;
    }
}

internal sealed class FakeComposer : IVdaOrderComposer
{
    /// <summary>Запросы сборки, которые получил подставной сборщик.</summary>
    public List<VdaOrderRequest> Requests { get; } = new();

    public string Compose(VdaOrderRequest request)
    {
        Requests.Add(request);

        // Как настоящий сборщик: узлов на один больше, чем сегментов маршрута,
        // нумерация сквозная (узел 2i, ребро 2i + 1).
        var all = new List<object>();
        for (var index = 0; index <= request.Route.Segments.Count; index++)
        {
            all.Add(new
            {
                nodeId = $"wa{index * 2}",
                sequenceId = index * 2,
                released = true,
                actions = Array.Empty<object>(),
            });
        }

        var start = Math.Clamp(request.SliceStartIndex, 0, Math.Max(0, all.Count - 1));
        var count = request.SliceNodeCount <= 0
            ? all.Count - start
            : Math.Min(request.SliceNodeCount, all.Count - start);
        var taken = all.Skip(start).Take(count).ToList();
        var edges = Enumerable.Range(start, Math.Max(0, taken.Count - 1))
            .Select(index => new
            {
                edgeId = $"801-e{index}",
                sequenceId = (index * 2) + 1,
                released = true,
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            headerId = request.HeaderId,
            version = "2.1.0",
            manufacturer = request.Manufacturer,
            serialNumber = request.SerialNumber,
            orderId = request.OrderId,
            orderUpdateId = request.OrderUpdateId,
            nodes = taken,
            edges,
        });
    }
}

internal sealed class FakePlanner(int nodes) : Application.Planning.IPathPlanner
{
    public Task<RoutePlanResult> PlanAsync(Application.Planning.RoutePlanRequest request, CancellationToken ct)
    {
        // Как настоящий планировщик: маршрут из nodes узлов состоит из nodes - 1 сегментов.
        var segments = Enumerable.Range(0, Math.Max(0, nodes - 1))
            .Select(_ => new RouteSegment(
                "801-a-b",
                RouteEdgeDirection.Forward,
                1,
                1,
                1,
                1,
                1,
                false,
                Guid.NewGuid(),
                true))
            .ToList();

        var route = new PlannedRoute(segments, nodes, nodes, nodes, 0, 1, false, null);
        return Task.FromResult(RoutePlanResult.Success(route, null));
    }
}

internal sealed class HeaderIds : IHeaderIdSource
{
    private long _last;

    public long Next() => ++_last;
}

internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Подставной диспетчер трафика: запоминает вызовы и позволяет задать отказ.
/// </summary>
internal sealed class FakeTrafficCoordinator : Application.Traffic.ITrafficCoordinator
{
    public List<(string Serial, string? PlanJson)> Reservations { get; } = [];

    public List<(string Serial, string? PlanJson)> States { get; } = [];

    public List<string> Releases { get; } = [];

    public int Sweeps { get; private set; }

    public Application.Traffic.TrafficReservationResult OrderResult { get; set; } =
        Application.Traffic.TrafficReservationResult.Accept();

    public Application.Traffic.TrafficReservationResult StateResult { get; set; } =
        Application.Traffic.TrafficReservationResult.Accept();

    public Task<Application.Traffic.TrafficReservationResult> TryReserveForOrderAsync(
        RobotSnapshot snapshot,
        string? planJson,
        CancellationToken ct)
    {
        Reservations.Add((snapshot.SerialNumber, planJson));
        return Task.FromResult(OrderResult);
    }

    public Task<Application.Traffic.TrafficReservationResult> OnStateAsync(
        RobotSnapshot snapshot,
        string? planJson,
        CancellationToken ct)
    {
        States.Add((snapshot.SerialNumber, planJson));
        return Task.FromResult(StateResult);
    }

    public Task<int> ReleaseAsync(string agvSerialNumber, CancellationToken ct)
    {
        Releases.Add(agvSerialNumber);
        return Task.FromResult(1);
    }

    public Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        Sweeps++;
        return Task.FromResult(0);
    }
}

/// <summary>
/// Вспомогательные данные заказов.
/// </summary>
internal static class OrderTestData
{
    public const string Serial = "98001";

    public static RobotSnapshot Snapshot(
        string? orderId,
        int orderUpdateId = 0,
        bool driving = false,
        IEnumerable<(string NodeId, int Sequence, bool Released)>? nodes = null,
        IEnumerable<string>? errors = null,
        DateTimeOffset? receivedAt = null,
        string? lastNodeId = null,
        int? lastNodeSequenceId = null,
        bool newBaseRequest = false,
        IEnumerable<(string EdgeId, int Sequence, bool Released)>? edges = null,
        double positionX = 1,
        double positionY = 2) => new(
        Serial,
        "ONLINE",
        driving,
        orderId,
        orderUpdateId,
        positionX,
        positionY,
        0,
        "801",
        80,
        errors?.ToList() ?? [],
        nodes?.Select(node => new RobotNodeState(node.NodeId, node.Sequence, node.Released)).ToList() ?? [],
        receivedAt ?? DateTimeOffset.UtcNow,
        "2.1.0",
        lastNodeId ?? "wa0",
        lastNodeSequenceId,
        newBaseRequest,
        null,
        edges?.Select(edge => new RobotEdgeState(edge.EdgeId, edge.Sequence, edge.Released)).ToList());

    public static RobotOrder Order(
        string orderId = "ORD_04331",
        string status = RobotOrderStatuses.Sent,
        DateTimeOffset? sentAt = null,
        DateTimeOffset? acceptedAt = null,
        IReadOnlyList<RobotOrderNode>? nodes = null,
        string planJson = "{}",
        int orderUpdateId = 0,
        string? targetNodeId = "wa4")
    {
        var created = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        return new RobotOrder(
            orderId,
            Serial,
            orderUpdateId,
            status,
            planJson,
            2,
            targetNodeId,
            null,
            created,
            sentAt ?? created,
            acceptedAt,
            null,
            null,
            nodes ?? [Node(orderId, 0, "wa0"), Node(orderId, 2, "wa2"), Node(orderId, 4, "wa4")]);
    }

    /// <summary>
    /// План из <paramref name="nodeCount"/> узлов: узлы <c>wa0, wa2, ...</c>, ребра между ними.
    /// </summary>
    public static string Plan(int nodeCount, int firstSequence = 0)
    {
        var nodes = Enumerable.Range(0, nodeCount)
            .Select(index => new
            {
                nodeId = $"wa{firstSequence + (index * 2)}",
                sequenceId = firstSequence + (index * 2),
                released = true,
                actions = Array.Empty<object>(),
            })
            .ToList();

        var edges = Enumerable.Range(0, Math.Max(0, nodeCount - 1))
            .Select(index => new
            {
                edgeId = $"801-e{index}",
                sequenceId = firstSequence + (index * 2) + 1,
                released = true,
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            headerId = 1,
            version = "2.1.0",
            manufacturer = "Tuskrobots",
            serialNumber = Serial,
            orderId = "ORD_04331",
            orderUpdateId = 0,
            nodes,
            edges,
        });
    }

    public static RobotOrderNode Node(string orderId, int sequence, string nodeId, bool released = true, bool reached = false) =>
        new(orderId, sequence, nodeId, released, reached, 0);
}
