using System.Text.Json;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Domain.Traffic;
using VR.RoutePlanner.Tests.Unit.Planning;

namespace VR.RoutePlanner.Tests.Unit.Traffic;

/// <summary>
/// Данные для тестов трафика: параметры, разрешение ресурсов и планы заказов.
/// </summary>
internal static class TrafficFixtures
{
    public const string Profile = PlanningFixtures.Profile;

    public static readonly Guid MapVersionId = PlanningFixtures.MapVersionId;

    /// <summary>Параметры <c>Routing</c> с управляемыми горизонтами.</summary>
    public static RoutePlannerOptions Options(
        int releaseHorizonNodes = 15,
        double reservationHorizonSeconds = 30,
        double releaseHorizonSeconds = 60)
    {
        var options = PlanningFixtures.Options();
        options.DefaultAgvProfile = Profile;
        options.ReleaseHorizonNodes = releaseHorizonNodes;
        options.ReservationHorizonSeconds = reservationHorizonSeconds;
        options.ReleaseHorizonSeconds = releaseHorizonSeconds;
        return options;
    }

    /// <summary>Ресурс горизонта в памяти.</summary>
    public static TrafficHorizonEntry Entry(
        Guid resourceId,
        TrafficResourceType type = TrafficResourceType.EXCLUSIVE,
        int capacity = 1,
        RouteEdgeDirection? direction = null,
        bool isNode = false,
        int sequenceId = 0,
        string? code = null) =>
        new(resourceId, code ?? resourceId.ToString("N")[..8], type, capacity, direction, sequenceId, isNode);

    /// <summary>Снимок робота с картой и последним узлом плана.</summary>
    public static RobotSnapshot Snapshot(
        string serialNumber = "98001",
        int? lastNodeSequenceId = 0,
        string mapId = "801",
        string? lastNodeId = "wa0",
        bool driving = true) =>
        new(
            serialNumber,
            "ONLINE",
            driving,
            "ORD_04331",
            0,
            0,
            0,
            0,
            mapId,
            0,
            [],
            [],
            DateTimeOffset.UnixEpoch,
            "2.1.0",
            lastNodeId,
            lastNodeSequenceId);

    /// <summary>
    /// План заказа: узлы <c>wa2i</c>, ребра <c>801-ei</c> с <c>maxSpeed</c>, как у настоящего
    /// <c>VdaOrderBuilder</c>. Нумерация VDA: узел <c>2i</c>, ребро <c>2i + 1</c>.
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
                startNodeId = $"wa{firstSequence + (index * 2)}",
                endNodeId = $"wa{firstSequence + (index * 2) + 2}",
                maxSpeed = 1.0,
                released = true,
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            headerId = 1,
            version = "2.1.0",
            manufacturer = "Tuskrobots",
            serialNumber = "98001",
            orderId = "ORD_04331",
            orderUpdateId = 0,
            nodes,
            edges,
        });
    }
}

/// <summary>
/// Разрешение ресурсов в памяти: узлы по <c>wa*</c> и ребра по <c>801-e*</c>.
/// </summary>
internal sealed class FakeTrafficResourceMap : ITrafficResourceMap
{
    private readonly Dictionary<string, List<(string FromNodeId, TrafficEdgeResource Resource)>> _edges =
        new(StringComparer.Ordinal);

    public Guid MapVersionId { get; init; } = TrafficFixtures.MapVersionId;

    public string AgvProfile { get; init; } = TrafficFixtures.Profile;

    public Dictionary<string, TrafficResourceRef> Nodes { get; } = new(StringComparer.Ordinal);

    public FakeTrafficResourceMap Node(
        string nodeId,
        Guid resourceId,
        TrafficResourceType type = TrafficResourceType.CONFLICT_ZONE,
        int capacity = 1)
    {
        Nodes[nodeId] = new TrafficResourceRef(resourceId, $"CZ-{nodeId}", type, capacity);
        return this;
    }

    public FakeTrafficResourceMap Edge(
        string edgeId,
        Guid resourceId,
        TrafficResourceType type = TrafficResourceType.EXCLUSIVE,
        RouteEdgeDirection direction = RouteEdgeDirection.Forward,
        string fromNodeId = "wa0",
        int capacity = 1)
    {
        if (!_edges.TryGetValue(edgeId, out var list))
        {
            list = [];
            _edges[edgeId] = list;
        }

        list.Add((fromNodeId, new TrafficEdgeResource(resourceId, $"C-{edgeId}", type, capacity, direction)));
        return this;
    }

    public TrafficResourceRef? FindNode(string nodeId) =>
        Nodes.TryGetValue(nodeId, out var resource) ? resource : null;

    public TrafficEdgeResource? FindEdge(string edgeId, string? startNodeId)
    {
        if (!_edges.TryGetValue(edgeId, out var list) || list.Count == 0)
        {
            return null;
        }

        if (list.Count == 1)
        {
            return list[0].Resource;
        }

        var forward = string.Equals(list[0].FromNodeId, startNodeId, StringComparison.Ordinal);
        var direction = forward ? RouteEdgeDirection.Forward : RouteEdgeDirection.Backward;
        return list.FirstOrDefault(item => item.Resource.Direction == direction).Resource
            ?? list[0].Resource;
    }
}

/// <summary>
/// Подставное разрешение ресурсов: всегда отдает заданную карту либо <c>null</c>.
/// </summary>
internal sealed class FakeTrafficResourceLookup(ITrafficResourceMap? map) : ITrafficResourceLookup
{
    public int Calls { get; private set; }

    public Task<ITrafficResourceMap?> GetAsync(string mapId, string agvProfile, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(map);
    }
}
