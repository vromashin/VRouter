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
        double? firstNodeTheta = null)
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
                nodes.Add(BuildNode(from, sequence, mapId, true, firstNodeX, firstNodeY, firstNodeTheta));
                previous = from;
            }
            else if (previous.Id != from.Id)
            {
                throw new InvalidOperationException(
                    $"Маршрут разрывается: ребро {segment.EdgeCode} начинается не в узле {previous.NodeId}.");
            }

            orderEdges.Add(BuildEdge(edge, segment.Direction, sequence + 1, from, to));
            nodes.Add(BuildNode(to, sequence + 2, mapId, false, null, null, null));
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
            ["orderUpdateId"] = 0,
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
        var degree = Math.Min(MaxTrajectoryDegree, Math.Max(1, ordered.Count - 1));
        var knots = new JsonArray();
        for (var i = 0; i < degree + 1; i++)
        {
            knots.Add(0);
        }

        for (var i = 0; i < degree + 1; i++)
        {
            knots.Add(1);
        }

        var controlPoints = new JsonArray();
        foreach (var point in ordered)
        {
            controlPoints.Add(new JsonObject
            {
                ["x"] = point.X,
                ["y"] = point.Y,
                ["weight"] = 1,
            });
        }

        return new JsonObject
        {
            ["degree"] = degree,
            ["knotVector"] = knots,
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
        double? firstTheta)
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
            ["released"] = true,
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
        WarehouseNode to) => new()
    {
        ["edgeId"] = edge.EdgeId,
        ["sequenceId"] = sequenceId,
        ["startNodeId"] = from.NodeId,
        ["endNodeId"] = to.NodeId,
        ["orientation"] = 0,
        ["orientationType"] = "TANGENTIAL",
        ["released"] = true,
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
