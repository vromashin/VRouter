using System.Text.Json.Nodes;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Сборка тела VDA-заказа: узлы и ребра сообщения по правилам старого AGVControl.
///
/// Нумерация сквозная по полному маршруту: узел четный (<c>2i</c>), ребро нечетное
/// (<c>2i + 1</c>), ребро всегда равно узлу плюс один. Кусок сообщения меняет только состав
/// узлов и ребер, но не их номера, поэтому обновление продолжает нумерацию исходного плана.
/// </summary>
public static class VdaOrderBuilder
{
    /// <summary>Версия протокола VDA 5050 в сообщениях.</summary>
    public const string ProtocolVersion = "2.1.0";

    /// <summary>Допуск по XY у первого узла первого сообщения, метры.</summary>
    public const double FirstNodeDeviationXy = 0.3;

    /// <summary>Допуск по курсу у первого узла первого сообщения, радианы.</summary>
    public const double FirstNodeDeviationTheta = 3.14;

    /// <summary>Допуск по XY у остальных узлов, метры.</summary>
    public const double NodeDeviationXy = 0.1;

    /// <summary>Допуск по курсу у остальных узлов, радианы.</summary>
    public const double NodeDeviationTheta = 0.1;

    private const string OrientationTypeTangential = "TANGENTIAL";

    private const string VirtualEdgePrefix = "VIRTUAL_START_";

    /// <summary>
    /// Собирает заказ.
    /// </summary>
    /// <param name="request">Запрос сборки.</param>
    /// <returns>JSON заказа.</returns>
    /// <exception cref="InvalidOperationException">Маршрут разрывается или ссылается на неизвестное ребро.</exception>
    public static JsonObject Build(VdaOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = ExpandPlan(request);
        var start = Math.Clamp(request.SliceStartIndex, 0, plan.Nodes.Count - 1);
        var count = request.SliceNodeCount <= 0
            ? plan.Nodes.Count - start
            : Math.Min(request.SliceNodeCount, plan.Nodes.Count - start);
        var last = start + count - 1;

        var nodes = new JsonArray();
        for (var index = start; index <= last; index++)
        {
            nodes.Add(BuildNode(request, plan, index, index == start));
        }

        var edges = new JsonArray();
        for (var index = start; index < last; index++)
        {
            edges.Add(BuildEdge(request, plan, index, index == start));
        }

        return new JsonObject
        {
            ["headerId"] = request.HeaderId,
            ["timestamp"] = request.Timestamp.UtcDateTime.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                System.Globalization.CultureInfo.InvariantCulture),
            ["version"] = ProtocolVersion,
            ["manufacturer"] = request.Manufacturer,
            ["serialNumber"] = request.SerialNumber,
            ["orderId"] = request.OrderId,
            ["orderUpdateId"] = request.OrderUpdateId,
            ["nodes"] = nodes,
            ["edges"] = edges,
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
            var lastPoint = ordered[^1];
            controlPoints.Add(new JsonObject { ["x"] = first.X, ["y"] = first.Y, ["weight"] = 1 });
            if (ordered.Count > 1)
            {
                controlPoints.Add(new JsonObject { ["x"] = lastPoint.X, ["y"] = lastPoint.Y, ["weight"] = 1 });
            }
        }

        return new JsonObject
        {
            ["degree"] = 1,
            ["knotVector"] = new JsonArray { 0, 0, 1, 1 },
            ["controlPoints"] = controlPoints,
        };
    }

    /// <summary>
    /// Разворачивает маршрут в узлы и ребра полного плана, проверяя связность.
    /// </summary>
    private static PlanChain ExpandPlan(VdaOrderRequest request)
    {
        var lookup = EdgeLookup(request.Graph);
        var nodes = new List<WarehouseNode>();
        var edges = new List<WarehouseEdge>();
        var directions = new List<RouteEdgeDirection>();

        foreach (var segment in request.Route.Segments)
        {
            if (!lookup.TryGetValue(segment.EdgeCode, out var edge))
            {
                throw new InvalidOperationException($"Ребро {segment.EdgeCode} отсутствует в графе.");
            }

            if (!request.Graph.Nodes.TryGetValue(edge.FromNodeId, out var from)
                || !request.Graph.Nodes.TryGetValue(edge.ToNodeId, out var to))
            {
                throw new InvalidOperationException($"Ребро {segment.EdgeCode} ссылается на узлы вне графа.");
            }

            if (segment.Direction == RouteEdgeDirection.Backward)
            {
                (from, to) = (to, from);
            }

            if (nodes.Count == 0)
            {
                nodes.Add(from);
            }
            else if (nodes[^1].Id != from.Id)
            {
                throw new InvalidOperationException(
                    $"Маршрут разрывается: ребро {segment.EdgeCode} начинается не в узле {nodes[^1].NodeId}.");
            }

            nodes.Add(to);
            edges.Add(edge);
            directions.Add(segment.Direction);
        }

        if (nodes.Count == 0)
        {
            throw new InvalidOperationException("Маршрут пуст: собирать заказ не из чего.");
        }

        return new PlanChain(nodes, edges, directions);
    }

    private static JsonObject BuildNode(VdaOrderRequest request, PlanChain plan, int index, bool first)
    {
        var node = plan.Nodes[index];
        var isWorking = WorkingNodeGeometry.IsWorking(node);
        WorkingNodeStop stop = default;
        if (isWorking)
        {
            stop = WorkingNodeStopResolver.Resolve(
                request.Graph,
                plan.Nodes,
                index,
                request.RobotX ?? node.X,
                request.RobotY ?? node.Y);
        }

        // Узел подхода: маршрут закончился до целевого рабочего узла, действие ставится здесь.
        var isApproachFallback = request.ApproachTarget is not null
            && index == plan.Nodes.Count - 1
            && !isWorking;

        var position = new JsonObject
        {
            ["x"] = isWorking ? stop.StopX : node.X,
            ["y"] = isWorking ? stop.StopY : node.Y,
        };

        double? theta = null;
        var containerX = node.X;
        var containerY = node.Y;
        double? containerTheta = null;

        if (isWorking)
        {
            theta = WorkingNodeGeometry.HasTheta(node) ? stop.Theta : null;
            containerTheta = stop.Theta;
        }

        if (isApproachFallback)
        {
            var target = request.ApproachTarget!;
            var course = WorkingNodeGeometry.Course(node.X, node.Y, target.X, target.Y);
            containerX = target.X;
            containerY = target.Y;
            containerTheta = course;
            theta = WorkingNodeGeometry.HasTheta(target) ? course : null;
        }

        // Первый узел первого сообщения: поза робота, широкие допуски, курс по флагу.
        if (first && request.OrderUpdateId <= 0)
        {
            position["x"] = request.RobotX ?? node.X;
            position["y"] = request.RobotY ?? node.Y;
            if (request.FirstNodeTheta)
            {
                position["theta"] = request.RobotTheta ?? node.Theta;
            }

            position["allowedDeviationXY"] = FirstNodeDeviationXy;
            position["allowedDeviationTheta"] = FirstNodeDeviationTheta;
        }
        else
        {
            if (theta is not null)
            {
                position["theta"] = theta.Value;
            }

            position["allowedDeviationXY"] = Math.Max(node.AllowedDeviationXY ?? 0, NodeDeviationXy);
            position["allowedDeviationTheta"] = Math.Max(node.AllowedDeviationTheta ?? 0, NodeDeviationTheta);
        }

        position["mapId"] = request.MapId;

        var actions = new JsonArray();
        var actionNode = isApproachFallback ? request.ApproachTarget! : node;
        if ((isWorking || isApproachFallback) && request.SendForkActions)
        {
            actions.Add(BuildForkAction(actionNode, request, containerX, containerY, containerTheta));
        }

        // Завершающее действие ставится только в последнем узле всего плана.
        if (index == plan.Nodes.Count - 1 && !string.IsNullOrWhiteSpace(request.TerminalActionType))
        {
            actions.Add(BuildAction(
                request.TerminalActionType!,
                containerX,
                containerY,
                containerTheta ?? 0));
        }

        return new JsonObject
        {
            ["nodeId"] = node.NodeId,
            ["sequenceId"] = index * 2,
            ["released"] = true,
            ["nodePosition"] = position,
            ["nodeDescription"] = "{}",
            ["actions"] = actions,
        };
    }

    private static JsonObject BuildEdge(VdaOrderRequest request, PlanChain plan, int index, bool first)
    {
        var edge = plan.Edges[index];
        var from = plan.Nodes[index];
        var to = plan.Nodes[index + 1];

        // Как в AGVControl: курс задается только у первого ребра сообщения. Если первый
        // перегон идет против хранения ребра, робот едет задним ходом (orientation = PI).
        // У остальных ребер orientation = 0 независимо от направления.
        var orientation = first && plan.Directions[index] == RouteEdgeDirection.Backward
            ? Math.PI
            : 0;

        var result = new JsonObject
        {
            ["edgeId"] = edge.EdgeId,
            ["sequenceId"] = (index * 2) + 1,
            ["startNodeId"] = from.NodeId,
            ["endNodeId"] = to.NodeId,
            ["maxSpeed"] = edge.MaxSpeed,
            ["orientation"] = orientation,
            ["orientationType"] = string.IsNullOrWhiteSpace(edge.OrientationType)
                ? OrientationTypeTangential
                : edge.OrientationType,
        };

        if (edge.MaxRotationSpeed is not null)
        {
            result["maxRotationSpeed"] = edge.MaxRotationSpeed.Value;
        }

        result["released"] = true;
        result["trajectory"] = BuildTrajectory(edge.TrajectoryPoints, plan.Directions[index]);
        result["actions"] = new JsonArray();
        return result;
    }

    private static JsonObject BuildForkAction(
        WarehouseNode node,
        VdaOrderRequest request,
        double containerX,
        double containerY,
        double? containerTheta)
    {
        var actionType = node.NodeType.ToUpperInvariant() switch
        {
            "LOADING" => "pick",
            "UNLOADING" => "drop",

            // Тип узла не задает операцию (например слот wo-*): решаем по наличию груза.
            _ => request.HasLoad ? "drop" : "pick",
        };

        return BuildAction(actionType, containerX, containerY, containerTheta ?? 0);
    }

    private static JsonObject BuildAction(
        string actionType,
        double containerX,
        double containerY,
        double containerTheta)
    {
        var parameters = new JsonArray
        {
            new JsonObject { ["key"] = "containerTypeId", ["value"] = 2 },
            new JsonObject { ["key"] = "containerX", ["value"] = containerX },
            new JsonObject { ["key"] = "containerY", ["value"] = containerY },
            new JsonObject { ["key"] = "containerTheta", ["value"] = containerTheta },
            new JsonObject { ["key"] = "onlyFork", ["value"] = true },
            new JsonObject { ["key"] = "autoReturnNode", ["value"] = true },
            new JsonObject { ["key"] = "noScan", ["value"] = false },
            new JsonObject { ["key"] = "adjustWithoutOA", ["value"] = true },
        };

        return new JsonObject
        {
            ["actionId"] = Guid.NewGuid().ToString(),
            ["actionType"] = actionType,
            ["actionDescription"] = actionType,
            ["blockingType"] = "SOFT",
            ["actionParameters"] = parameters,
        };
    }

    private static Dictionary<string, WarehouseEdge> EdgeLookup(WarehouseGraph graph)
    {
        var result = new Dictionary<string, WarehouseEdge>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges.Values.SelectMany(list => list))
        {
            result.TryAdd(edge.EdgeId, edge);
        }

        return result;
    }

    /// <summary>Полный план маршрута: узлы, ребра и направление проезда каждого ребра.</summary>
    private sealed record PlanChain(
        List<WarehouseNode> Nodes,
        List<WarehouseEdge> Edges,
        List<RouteEdgeDirection> Directions);
}
