using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Tests.Unit.Support;

/// <summary>
/// Построение тестовых графов без обращения к базе.
/// </summary>
internal static class GraphFixtures
{
    public const string MapId = "801";

    public static readonly Guid MapKey = new("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// Узел с координатами и типом по умолчанию.
    /// </summary>
    public static WarehouseNode Node(string nodeId, double x, double y) => new(
        StableId(nodeId, 0),
        nodeId,
        MapKey,
        x,
        y,
        Theta: 0,
        AllowedDeviationXY: 0.1,
        AllowedDeviationTheta: 0.1,
        NodeType: "WAYPOINT",
        RackId: null,
        Z: 0,
        IsActive: true);

    /// <summary>
    /// Ребро между узлами по прямой линии с дискретизированной траекторией.
    /// </summary>
    public static WarehouseEdge Edge(
        WarehouseNode from,
        WarehouseNode to,
        bool bidirectional = false,
        string? corridor = null,
        double length = 0)
    {
        var points = new List<MapPoint>
        {
            new(from.X, from.Y, null),
            new(to.X, to.Y, null),
        };

        return new WarehouseEdge(
            StableId(from.NodeId + to.NodeId, 1),
            $"{MapId}-{from.NodeId}-{to.NodeId}",
            from.Id,
            to.Id,
            MapKey,
            length,
            MaxSpeed: 1.0,
            Bidirectional: bidirectional,
            Corridor: corridor,
            MaxRotationSpeed: 0.5,
            Orientation: null,
            OrientationType: "TANGENTIAL",
            RotationAllowed: true,
            TrajectoryPoints: points,
            TrajectoryRaw: null,
            IsActive: true);
    }

    /// <summary>
    /// Граф из узлов и ребер, разложенных по словарю начальных узлов.
    /// </summary>
    public static WarehouseGraph Graph(
        IReadOnlyList<WarehouseNode> nodes,
        IReadOnlyList<WarehouseEdge> edges)
    {
        var edgesByNode = new Dictionary<Guid, List<WarehouseEdge>>();
        foreach (var edge in edges)
        {
            AddEdge(edgesByNode, edge.FromNodeId, edge);
            if (edge.Bidirectional && edge.ToNodeId != edge.FromNodeId)
            {
                AddEdge(edgesByNode, edge.ToNodeId, edge);
            }
        }

        return new WarehouseGraph
        {
            MapKey = MapKey,
            MapId = MapId,
            MapVersion = "2.8.1",
            GraphVersion = "2.8.1-test",
            Nodes = nodes.ToDictionary(node => node.Id),
            Edges = edgesByNode.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<WarehouseEdge>)pair.Value),
        };
    }

    private static void AddEdge(
        Dictionary<Guid, List<WarehouseEdge>> edgesByNode,
        Guid nodeId,
        WarehouseEdge edge)
    {
        if (!edgesByNode.TryGetValue(nodeId, out var list))
        {
            list = new List<WarehouseEdge>();
            edgesByNode[nodeId] = list;
        }

        list.Add(edge);
    }

    private static Guid StableId(string value, byte discriminator)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, bytes.Length);
        bytes[15] = discriminator;
        return new Guid(bytes);
    }
}
