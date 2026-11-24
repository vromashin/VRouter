using System.Text.Json.Nodes;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Сборка тела VDA-заказа: узлы и ребра сообщения по правилам старого AGVControl.
///
/// Нумерация сквозная по полному маршруту от базы <c>SequenceIdBase</c>: узел
/// <c>база + 2i</c>, ребро <c>база + 2i + 1</c>, ребро всегда равно узлу плюс один. Кусок
/// сообщения меняет только состав узлов и ребер, но не их номера, поэтому обновление
/// продолжает нумерацию исходного плана, а новое плечо заказа продолжает нумерацию прошлого.
///
/// Исключение — префикс плана (робот стоит не в первом узле маршрута): перед маршрутом
/// появляются узел <c>sequenceId = база</c> и ребро с <c>sequenceId = база + 1</c>, остальные
/// номера сдвигаются на два. Префикс бывает двух видов: поза робота перед первым узлом
/// (виртуальный узел) и тройка узла-якоря — узел карты без курса, ребро
/// <c>VIRTUAL_START_&lt;nodeId&gt;</c> (<c>start = end</c>) и дубль узла с курсом к паллете
/// и действием вил (эталон <c>ref-rev-leg2-pick-drop</c>).
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

    /// <summary>Допуск «робот стоит в первом узле маршрута», метры.</summary>
    public const double VirtualStartDeviationXy = 0.1;

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

        // Чанк не должен разрывать тройку «узел-якорь — виртуальное ребро — дубль»: без всех
        // трех узлов робот не свяжет действие с продолжением заказа, поэтому минимум три узла.
        if (plan.AnchorStart && start == 0 && count < 3)
        {
            count = Math.Min(3, plan.Nodes.Count - start);
        }

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
    /// Разворачивает заказ в узлы и ребра полного плана, проверяя связность.
    /// </summary>
    private static PlanChain ExpandPlan(VdaOrderRequest request)
    {
        var route = RouteChain(request);

        // Сообщение плеча начинается на узле-якоре: тройка «узел — виртуальное ребро — дубль»
        // перед маршрутом. Маршрут может быть пустым: робот уже стоит в узле-якоре.
        if (request.AnchorNode is { } anchor)
        {
            return AnchorPlan(anchor, route);
        }

        if (route.Nodes.Count == 0)
        {
            throw new InvalidOperationException("Маршрут пуст: собирать заказ не из чего.");
        }

        return NeedsVirtualStart(request, route) ? PrependVirtualStart(route) : route;
    }

    /// <summary>
    /// Узлы и ребра маршрута без префикса плана.
    /// </summary>
    private static PlanChain RouteChain(VdaOrderRequest request)
    {
        var lookup = EdgeLookup(request.Graph);
        var nodes = new List<WarehouseNode>();
        var edges = new List<PlanEdge>();

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
            edges.Add(new PlanEdge(edge, segment.Direction));
        }

        return new PlanChain(nodes, edges, VirtualStart: false);
    }

    /// <summary>
    /// Нужен ли виртуальный старт: обновление заказа начинается не в том узле, где стоит робот.
    ///
    /// Как в AGVControl (<c>MakeNodesAndEdgesForOrderAsync</c>, <c>addVirualEdge</c>): виртуальное
    /// ребро добавляется только к обновлению (<c>orderUpdateId &gt; 0</c>) и только в начало
    /// сообщения. В первом сообщении заказа позу робота получает сам первый узел (допуски 0.3/3.14),
    /// поэтому виртуальное ребро там не нужно и нумерация плана не сдвигается.
    /// </summary>
    private static bool NeedsVirtualStart(VdaOrderRequest request, PlanChain plan)
    {
        // Первое сообщение плеча само начинается с позы робота: виртуальное ребро не нужно.
        if (request.OrderUpdateId <= 0 || request.SliceStartIndex > 0 || request.FirstNodeIsPose)
        {
            return false;
        }

        if (request.RobotX is not { } robotX || request.RobotY is not { } robotY)
        {
            return false;
        }

        var first = plan.Nodes[0];
        var distance = Math.Sqrt(Math.Pow(first.X - robotX, 2) + Math.Pow(first.Y - robotY, 2));
        return distance > VirtualStartDeviationXy;
    }

    /// <summary>
    /// Ставит перед планом виртуальный узел с текущей позой робота и виртуальное ребро
    /// <c>VIRTUAL_START_&lt;nodeId&gt;</c> к первому узлу маршрута. Номера сдвигаются на два:
    /// виртуальный узел получает <c>sequenceId = база</c>, виртуальное ребро — <c>база + 1</c>,
    /// первый узел маршрута становится <c>база + 2</c>.
    /// </summary>
    private static PlanChain PrependVirtualStart(PlanChain plan)
    {
        var nodes = new List<WarehouseNode>(plan.Nodes.Count + 1) { plan.Nodes[0] };
        nodes.AddRange(plan.Nodes);

        var edges = new List<PlanEdge>(plan.Edges.Count + 1) { new(null, RouteEdgeDirection.Forward) };
        edges.AddRange(plan.Edges);

        return new PlanChain(nodes, edges, VirtualStart: true);
    }

    /// <summary>
    /// План сообщения, которое начинается на узле-якоре, как в эталоне AGVControl
    /// (<c>ref-rev-leg2-pick-drop</c>): узел-якорь с координатами карты и без курса, ребро
    /// <c>VIRTUAL_START_&lt;nodeId&gt;</c> от узла к нему же (робот проходит отрезок сам,
    /// поворот запрещен), дубль узла с курсом к паллете и действием вил, затем маршрут.
    /// Пустой маршрут дает сообщение из одной тройки: робот уже стоит в узле-якоре.
    /// </summary>
    private static PlanChain AnchorPlan(WarehouseNode anchor, PlanChain route)
    {
        if (route.Nodes.Count > 0 && route.Nodes[0].Id != anchor.Id)
        {
            throw new InvalidOperationException(
                $"Маршрут начинается не в узле-якоре {anchor.NodeId}, а в {route.Nodes[0].NodeId}.");
        }

        var nodes = new List<WarehouseNode>(route.Nodes.Count + 2) { anchor };
        if (route.Nodes.Count == 0)
        {
            // Стоянка в узле-якоре: дубль без маршрута, между ними виртуальное ребро.
            nodes.Add(anchor);
        }
        else
        {
            nodes.AddRange(route.Nodes);
        }

        var edges = new List<PlanEdge>(route.Edges.Count + 1) { new(null, RouteEdgeDirection.Forward) };
        edges.AddRange(route.Edges);

        return new PlanChain(nodes, edges, VirtualStart: false, AnchorStart: true);
    }

    private static JsonObject BuildNode(VdaOrderRequest request, PlanChain plan, int index, bool first)
    {
        if (plan.VirtualStart && index == 0)
        {
            return BuildVirtualStartNode(request, plan);
        }

        var node = plan.Nodes[index];
        var isWorking = WorkingNodeGeometry.IsWorking(node);
        WorkingNodeStop stop = default;
        if (isWorking)
        {
            stop = ResolveStop(request, plan, index);
        }

        // Узел-якорь: первый узел сообщения плеча. Координаты карты, курса нет, допуски 0.3/3.14,
        // действий нет — за ним идет виртуальное ребро и дубль узла с действием.
        var isAnchorPose = plan.AnchorStart && index == 0;

        // Дубль узла-якоря: здесь и только здесь выполняется действие вил начала плеча.
        var isAnchorAction = plan.AnchorStart && index == 1;

        // Цель заказа — последний узел плана.
        var isTarget = index == plan.Nodes.Count - 1;

        // Узел подхода: маршрут закончился до целевого рабочего узла, действие ставится здесь.
        var isApproachFallback = request.ApproachTarget is not null
            && isTarget
            && !isWorking;

        var position = new JsonObject
        {
            ["x"] = isWorking ? stop.StopX : node.X,
            ["y"] = isWorking ? stop.StopY : node.Y,
        };

        // Действие вил ставится ровно один раз за задачу: на дубле узла-якоря либо в последнем
        // узле плана. Узел отправления без действия робот только покидает.
        var anchorAction = isAnchorAction ? request.AnchorActionType : null;
        var targetAction = (isWorking && isTarget) || isApproachFallback ? request.TargetActionType : null;
        var actionType = string.IsNullOrWhiteSpace(anchorAction) ? targetAction : anchorAction;

        // Курс к паллете ставится по тому же признаку, что и действие (Д-1): узел без действия
        // остается точкой проезда, доворачивать вилы некуда.
        var hasAction = !string.IsNullOrWhiteSpace(actionType)
            || (isTarget && !string.IsNullOrWhiteSpace(request.TerminalActionType));

        double? theta = null;
        var containerX = node.X;
        var containerY = node.Y;
        double? containerTheta = null;

        if (isWorking)
        {
            containerTheta = stop.Theta;

            // Дубль узла-якоря и целевой рабочий узел: робот должен довернуться к стеллажу,
            // поэтому курс задается всегда, когда в узле есть действие вил (иначе вилы
            // выезжают под случайным углом). У узла-якоря курса нет даже при действии.
            if (isAnchorAction || (isTarget && hasAction))
            {
                theta = stop.Theta;
            }
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

        if (isAnchorPose)
        {
            theta = null;
        }

        // Первый узел первого сообщения задачи: поза робота, широкие допуски, курс фактической
        // позы, если он известен. У узла-якоря позы нет: координаты берутся из карты, курса нет.
        if (first && request.SliceStartIndex == 0)
        {
            if (request.FirstNodeIsPose)
            {
                position["x"] = request.RobotX ?? node.X;
                position["y"] = request.RobotY ?? node.Y;
                if (request.FirstNodeTheta && request.RobotTheta is { } robotTheta && !isAnchorPose)
                {
                    position["theta"] = robotTheta;
                }
            }
            else if (theta is not null)
            {
                position["theta"] = theta.Value;
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
        if (!string.IsNullOrWhiteSpace(actionType))
        {
            actions.Add(BuildAction(actionType!, containerX, containerY, containerTheta ?? 0));
        }

        // Завершающее действие ставится только в последнем узле всего плана.
        if (isTarget && !string.IsNullOrWhiteSpace(request.TerminalActionType))
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
            ["sequenceId"] = request.SequenceIdBase + (index * 2),
            ["released"] = true,
            ["nodePosition"] = position,
            ["nodeDescription"] = "{}",
            ["actions"] = actions,
        };
    }

    private static JsonObject BuildEdge(VdaOrderRequest request, PlanChain plan, int index, bool first)
    {
        var link = plan.Edges[index];
        var from = plan.Nodes[index];
        var to = plan.Nodes[index + 1];

        if (link.Edge is null)
        {
            return BuildVirtualStartEdge(request, plan, index);
        }

        var edge = link.Edge;

        // Как в AGVControl: курс задается только у первого ребра сообщения, у остальных
        // orientation = 0 независимо от направления. Скорости ребра (maxSpeed,
        // maxRotationSpeed) эталон не отдает ни в одном снимке, поэтому их в теле нет.
        //
        // Узел-якорь: ребро 0 — виртуальное, первое ребро маршрута (1) начинается на дубле узла,
        // где робот стоит вилами к паллете, поэтому курс считается по дублю (эталон:
        // 801-wo-1-6-wa178 и 801-wo-2-7-wa62 дают PI).
        var firstMovement = plan.AnchorStart ? index == 1 : first;
        var orientation = firstMovement ? FirstEdgeOrientation(request, plan, index, link) : 0;

        return new JsonObject
        {
            ["edgeId"] = edge.EdgeId,
            ["sequenceId"] = request.SequenceIdBase + (index * 2) + 1,
            ["startNodeId"] = from.NodeId,
            ["endNodeId"] = to.NodeId,
            ["orientation"] = orientation,
            ["orientationType"] = string.IsNullOrWhiteSpace(edge.OrientationType)
                ? OrientationTypeTangential
                : edge.OrientationType,
            ["released"] = true,
            ["trajectory"] = BuildTrajectory(edge.TrajectoryPoints, link.Direction),
            ["actions"] = new JsonArray(),
        };
    }

    /// <summary>
    /// <c>orientation</c> первого ребра сообщения: <c>PI</c>, если робот начинает движение
    /// задним ходом, то есть направление на следующий узел противоположно курсу робота
    /// в стартовом узле. Курс в рабочем узле равен курсу к паллете (робот стоит вилами
    /// к стеллажу), в остальных узлах это фактический курс робота.
    /// Эталон: <c>801-wo-2-7-wa62</c> и <c>801-wo-1-6-wa178</c> дают <c>PI</c>.
    /// </summary>
    private static double FirstEdgeOrientation(VdaOrderRequest request, PlanChain plan, int index, PlanEdge link)
    {
        var from = plan.Nodes[index];
        var to = plan.Nodes[index + 1];

        double? heading = null;
        var fromX = from.X;
        var fromY = from.Y;
        if (WorkingNodeGeometry.IsWorking(from))
        {
            var stop = ResolveStop(request, plan, index);
            heading = stop.Theta;
            fromX = stop.StopX;
            fromY = stop.StopY;
        }
        else if (index == 0 && !plan.VirtualStart)
        {
            // Поза робота относится только к началу плана: в куске с середины маршрута
            // робот стоит не в стартовом узле сообщения.
            heading = request.RobotTheta;
        }

        if (heading is not { } robotHeading)
        {
            // Курс робота неизвестен: прежнее правило, задний ход при проезде против хранения ребра.
            return link.Direction == RouteEdgeDirection.Backward ? Math.PI : 0;
        }

        var course = WorkingNodeGeometry.Course(fromX, fromY, to.X, to.Y);
        var difference = Math.Abs(Math.Atan2(Math.Sin(course - robotHeading), Math.Cos(course - robotHeading)));
        return difference > Math.PI / 2 ? Math.PI : 0;
    }

    /// <summary>
    /// Точка остановки и курс к паллете у рабочего узла плана. У дубля узла-якоря подход
    /// считается как у первого узла маршрута: робот подъезжает к нему из предыдущего плеча,
    /// поэтому предыдущий узел плана — он сам, и опорой служит единственный входящий узел карты
    /// (эталон: курс <c>wo-1-6</c> — 0, <c>wo-2-7</c> — <c>PI</c>).
    /// </summary>
    private static WorkingNodeStop ResolveStop(VdaOrderRequest request, PlanChain plan, int index)
    {
        var node = plan.Nodes[index];
        return WorkingNodeStopResolver.Resolve(
            request.Graph,
            plan.Nodes,
            plan.AnchorStart && index <= 1 ? 0 : index,
            request.RobotX ?? node.X,
            request.RobotY ?? node.Y);
    }

    /// <summary>
    /// Виртуальный узел старта: текущая поза робота перед первым узлом маршрута.
    /// </summary>
    private static JsonObject BuildVirtualStartNode(VdaOrderRequest request, PlanChain plan)
    {
        var target = plan.Nodes[1];
        var position = new JsonObject
        {
            ["x"] = request.RobotX ?? target.X,
            ["y"] = request.RobotY ?? target.Y,
            ["theta"] = request.RobotTheta ?? target.Theta,
            ["allowedDeviationXY"] = Math.Max(target.AllowedDeviationXY ?? 0, NodeDeviationXy),
            ["allowedDeviationTheta"] = Math.Max(target.AllowedDeviationTheta ?? 0, NodeDeviationTheta),
            ["mapId"] = request.MapId,
        };

        return new JsonObject
        {
            ["nodeId"] = VirtualStartId(target),
            ["sequenceId"] = request.SequenceIdBase,
            ["released"] = true,
            ["nodePosition"] = position,
            ["nodeDescription"] = "{}",
            ["actions"] = new JsonArray(),
        };
    }

    /// <summary>
    /// Виртуальное стартовое ребро от позы робота к первому узлу маршрута.
    /// Как <c>CreateVirtualEdge</c> в AGVControl: курса нет, поворот запрещен,
    /// скорость и траектория не задаются — робот проходит этот отрезок сам.
    /// </summary>
    private static JsonObject BuildVirtualStartEdge(VdaOrderRequest request, PlanChain plan, int index)
    {
        var target = plan.Nodes[index + 1];

        // Тройка узла-якоря: ребро идет от узла к нему же (эталон: start = end = wo-1-6).
        // Виртуальный старт позы: от виртуального узла к первому узлу маршрута.
        var startNodeId = plan.AnchorStart && index == 0
            ? plan.Nodes[index].NodeId
            : plan.VirtualStart && index == 0
                ? VirtualStartId(target)
                : plan.Nodes[index].NodeId;

        return new JsonObject
        {
            ["edgeId"] = VirtualStartId(target),
            ["sequenceId"] = request.SequenceIdBase + (index * 2) + 1,
            ["startNodeId"] = startNodeId,
            ["endNodeId"] = target.NodeId,
            ["orientation"] = null,
            ["orientationType"] = null,
            ["rotationAllowed"] = false,
            ["released"] = true,
            ["actions"] = new JsonArray(),
        };
    }

    private static string VirtualStartId(WarehouseNode node) => VirtualEdgePrefix + node.NodeId;

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

    /// <summary>Ребро плана: ребро графа и направление проезда; <c>Edge = null</c> — виртуальный старт.</summary>
    private sealed record PlanEdge(WarehouseEdge? Edge, RouteEdgeDirection Direction);

    /// <summary>
    /// Полный план сообщения: узлы, ребра и направление проезда каждого ребра.
    ///
    /// <c>VirtualStart = true</c> — план начинается виртуальным узлом робота
    /// (<c>Nodes[0]</c> — заглушка, виртуальный узел собирается из <c>Nodes[1]</c>),
    /// а <c>Edges[0]</c> — виртуальное стартовое ребро без ребра графа.
    ///
    /// <c>AnchorStart = true</c> — план начинается тройкой узла-якоря: <c>Nodes[0]</c> — узел
    /// карты без курса, <c>Edges[0]</c> — виртуальное ребро <c>start = end</c>, <c>Nodes[1]</c> —
    /// дубль узла с курсом к паллете и действием вил, дальше маршрут. Пустой маршрут дает
    /// ровно два узла и одно ребро.
    /// </summary>
    private sealed record PlanChain(
        List<WarehouseNode> Nodes,
        List<PlanEdge> Edges,
        bool VirtualStart,
        bool AnchorStart = false);
}
