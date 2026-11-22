using System.Text.Json.Nodes;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Сборка заказа VDA 5050 2.1.0 по образцу заказа, снятого с робота: узлы с четными
/// <c>sequenceId</c>, ребра с нечетными, траектория NURBS из точек ребра.
/// </summary>
public static class VdaOrderBuilder
{
    /// <summary>Версия протокола в заказе.</summary>
    public const string ProtocolVersion = "2.1.0";

    /// <summary>Допуск по координатам первого узла, метры.</summary>
    public const double FirstNodeDeviationXy = 0.3;

    /// <summary>Допуск по ориентации первого узла, радианы.</summary>
    public const double FirstNodeDeviationTheta = 3.14;

    /// <summary>Допуск по координатам остальных узлов, метры.</summary>
    public const double NodeDeviationXy = 0.1;

    /// <summary>Допуск по ориентации остальных узлов, радианы.</summary>
    public const double NodeDeviationTheta = 0.1;

    /// <summary>Предельная степень NURBS-траектории.</summary>
    public const int MaxTrajectoryDegree = 3;

    /// <summary>
    /// Собирает заказ.
    /// </summary>
    /// <param name="route">Маршрут планировщика.</param>
    /// <param name="graph">Граф карты: из него берутся точки ребер и координаты узлов.</param>
    /// <param name="mapId">Идентификатор карты.</param>
    /// <param name="manufacturer">Производитель робота.</param>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="orderNumber">Номер заказа; в <c>orderId</c> пишется пятью цифрами.</param>
    /// <param name="headerId">Идентификатор сообщения из таймера.</param>
    /// <param name="timestamp">Момент сборки заказа.</param>
    /// <param name="firstNodeX">Координата X первого узла, если она известна точнее графа.</param>
    /// <param name="firstNodeY">Координата Y первого узла.</param>
    /// <param name="firstNodeTheta">Ориентация первого узла, радианы.</param>
    /// <param name="releasedNodeCount">Сколько первых узлов входит в базовый план.</param>
    /// <param name="orderUpdateId">Версия заказа.</param>
    /// <returns>Заказ как JSON-объект.</returns>
    public static JsonObject Build(
        PlannedRoute route,
        WarehouseGraph graph,
        string mapId,
        string manufacturer,
        string serialNumber,
        long orderNumber,
        long headerId,
        DateTimeOffset timestamp,
        double? firstNodeX = null,
        double? firstNodeY = null,
        double? firstNodeTheta = null,
        int releasedNodeCount = int.MaxValue,
        int orderUpdateId = 0)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(graph);

        var edges = EdgeLookup(graph);
        var nodes = new JsonArray();
        var orderEdges = new JsonArray();

        WarehouseNode? previous = null;
        var sequence = 0;
        foreach (var segment in route.Segments)
        {
            if (!edges.TryGetValue(segment.EdgeCode, out var edge))
            {
                throw new InvalidOperationException($"Ребра {segment.EdgeCode} нет в графе карты.");
            }

            var fromId = segment.Direction == RouteEdgeDirection.Forward ? edge.FromNodeId : edge.ToNodeId;
            var toId = segment.Direction == RouteEdgeDirection.Forward ? edge.ToNodeId : edge.FromNodeId;
            var from = graph.Nodes[fromId];
            var to = graph.Nodes[toId];

            if (previous is null)
            {
                nodes.Add(BuildNode(from, sequence, mapId, true, firstNodeX, firstNodeY, firstNodeTheta, releasedNodeCount > 0));
                previous = from;
            }
            else if (previous.Id != from.Id)
            {
                throw new InvalidOperationException(
                    $"Маршрут разрывается: ребро {segment.EdgeCode} начинается не в узле {previous.NodeId}.");
            }

            var nextReleased = releasedNodeCount > (sequence / 2) + 1;
            orderEdges.Add(BuildEdge(edge, segment.Direction, sequence + 1, from, to, nextReleased));
            nodes.Add(BuildNode(to, sequence + 2, mapId, false, null, null, null, nextReleased));
            sequence += 2;
            previous = to;
        }

        return new JsonObject
        {
            ["headerId"] = headerId,
            ["timestamp"] = timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
            ["version"] = ProtocolVersion,
            ["manufacturer"] = manufacturer,
            ["serialNumber"] = serialNumber,
            ["orderId"] = $"ORD_{orderNumber:D5}",
            ["orderUpdateId"] = orderUpdateId,
            ["nodes"] = nodes,
            ["edges"] = orderEdges,
        };
    }

    /// <summary>
    /// Сериализует заказ в тело сообщения.
    /// </summary>
    /// <param name="order">Заказ.</param>
    /// <returns>JSON без отступов.</returns>
    public static string Serialize(JsonObject order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return order.ToJsonString();
    }

    /// <summary>
    /// Строит NURBS-траекторию по точкам ребра в направлении движения.
    /// </summary>
    /// <param name="points">Точки ребра в его собственном направлении.</param>
    /// <param name="direction">Направление прохождения.</param>
    /// <returns>Объект <c>trajectory</c>.</returns>
    public static JsonObject BuildTrajectory(IReadOnlyList<MapPoint> points, RouteEdgeDirection direction)
    {
        var ordered = direction == RouteEdgeDirection.Forward ? points : points.Reverse().ToList();

        // Эталон AGVControl публикует прямую: ровно две точки, degree 1, knotVector [0,0,1,1].
        // Симулятор на длинной NURBS-кривой не начинает движение, поэтому форму держим как в эталоне.
        var controlPoints = new JsonArray();
        if (ordered.Count > 0)
        {
            var first = ordered[0];
            var last = ordered[^1];
            controlPoints.Add(new JsonObject { ["x"] = first.X, ["y"] = first.Y, ["weight"] = 1 });
            if (ordered.Count > 1)
            {
                controlPoints.Add(new JsonObject { ["x"] = last.X, ["y"] = last.Y, ["weight"] = 1 });
            }
        }

        return new JsonObject
        {
            ["degree"] = 1,
            ["knotVector"] = new JsonArray { 0, 0, 1, 1 },
            ["controlPoints"] = controlPoints,
        };
    }

    private static JsonObject BuildNode(
        WarehouseNode node,
        int sequenceId,
        string mapId,
        bool first,
        double? firstX,
        double? firstY,
        double? firstTheta,
        bool released)
    {
        var position = new JsonObject
        {
            ["x"] = first ? firstX ?? node.X : node.X,
            ["y"] = first ? firstY ?? node.Y : node.Y,
        };

        if (first)
        {
            position["theta"] = firstTheta ?? node.Theta;
        }

        position["allowedDeviationXY"] = first ? FirstNodeDeviationXy : NodeDeviationXy;
        position["allowedDeviationTheta"] = first ? FirstNodeDeviationTheta : NodeDeviationTheta;
        position["mapId"] = mapId;

        return new JsonObject
        {
            ["nodeId"] = node.NodeId,
            ["sequenceId"] = sequenceId,
            ["released"] = released,
            ["nodePosition"] = position,
            ["nodeDescription"] = "{}",
            ["actions"] = new JsonArray(),
        };
    }

    private static JsonObject BuildEdge(
        WarehouseEdge edge,
        RouteEdgeDirection direction,
        int sequenceId,
        WarehouseNode from,
        WarehouseNode to,
        bool released) => new()
    {
        ["edgeId"] = edge.EdgeId,
        ["sequenceId"] = sequenceId,
        ["startNodeId"] = from.NodeId,
        ["endNodeId"] = to.NodeId,
        ["orientation"] = 0,
        ["orientationType"] = "TANGENTIAL",
        ["released"] = released,
        ["trajectory"] = BuildTrajectory(edge.TrajectoryPoints, direction),
        ["actions"] = new JsonArray(),
    };

    private static Dictionary<string, WarehouseEdge> EdgeLookup(WarehouseGraph graph)
    {
        var result = new Dictionary<string, WarehouseEdge>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges.Values.SelectMany(list => list))
        {
            result.TryAdd(edge.EdgeId, edge);
        }

        return result;
    }
}
