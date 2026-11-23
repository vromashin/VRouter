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
}

internal sealed class FakeStateReader : IRobotStateReader
{
    public Dictionary<string, RobotSnapshot> Snapshots { get; } = new(StringComparer.Ordinal);

    public Task<RobotSnapshot?> GetLatestAsync(string serialNumber, CancellationToken ct) =>
        Task.FromResult(Snapshots.GetValueOrDefault(serialNumber));

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
    public string Compose(
        PlannedRoute route,
        Domain.Graph.WarehouseGraph graph,
        string mapId,
        string orderId,
        string manufacturer,
        string serialNumber,
        long orderNumber,
        long headerId,
        int releasedNodeCount,
        DateTimeOffset timestamp,
        double? firstNodeX = null,
        double? firstNodeY = null,
        double? firstNodeTheta = null,
        string? terminalActionType = null,
        string? terminalActionLoadId = null,
        double? containerX = null,
        double? containerY = null,
        double? containerTheta = null)
    {
        var nodes = new List<object>();
        var sequence = 0;
        var index = 0;
        foreach (var _ in route.Segments)
        {
            nodes.Add(new
            {
                nodeId = $"wa{sequence}",
                sequenceId = sequence,
                released = index < releasedNodeCount,
                actions = Array.Empty<object>(),
            });
            sequence += 2;
            index++;
        }

        return JsonSerializer.Serialize(new
        {
            headerId,
            version = "2.1.0",
            manufacturer,
            serialNumber,
            orderId,
            orderUpdateId = 0,
            nodes,
            edges = Array.Empty<object>(),
        });
    }
}

internal sealed class FakePlanner(int nodes) : Application.Planning.IPathPlanner
{
    public Task<RoutePlanResult> PlanAsync(Application.Planning.RoutePlanRequest request, CancellationToken ct)
    {
        var segments = Enumerable.Range(0, nodes)
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
        string? lastNodeId = null) => new(
        Serial,
        "ONLINE",
        driving,
        orderId,
        orderUpdateId,
        1,
        2,
        0,
        "801",
        80,
        errors?.ToList() ?? [],
        nodes?.Select(node => new RobotNodeState(node.NodeId, node.Sequence, node.Released)).ToList() ?? [],
        receivedAt ?? DateTimeOffset.UtcNow,
        "2.1.0",
        lastNodeId ?? "wa0");

    public static RobotOrder Order(
        string orderId = "ORD_04331",
        string status = RobotOrderStatuses.Sent,
        DateTimeOffset? sentAt = null,
        DateTimeOffset? acceptedAt = null,
        IReadOnlyList<RobotOrderNode>? nodes = null)
    {
        var created = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        return new RobotOrder(
            orderId,
            Serial,
            0,
            status,
            "{}",
            2,
            "wa4",
            null,
            created,
            sentAt ?? created,
            acceptedAt,
            null,
            null,
            nodes ?? [Node(orderId, 0, "wa0"), Node(orderId, 2, "wa2"), Node(orderId, 4, "wa4")]);
    }

    public static RobotOrderNode Node(string orderId, int sequence, string nodeId, bool released = true, bool reached = false) =>
        new(orderId, sequence, nodeId, released, reached, 0);
}
