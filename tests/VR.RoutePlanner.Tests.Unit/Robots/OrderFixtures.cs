using System.Text.Json.Nodes;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Сборка графов, маршрутов и запросов для тестов заказа: без базы и планировщика.
/// </summary>
internal static class OrderFixtures
{
    public const string MapId = "801";
    public const string Manufacturer = "Tuskrobots";
    public const string SerialNumber = "98001";

    /// <summary>Момент сборки заказов в тестах.</summary>
    public static readonly DateTimeOffset Timestamp = new(2026, 11, 21, 18, 14, 29, 29, TimeSpan.Zero);

    /// <summary>Граф из трех узлов буквой Г с двумя ребрами по две точки.</summary>
    public static (WarehouseGraph Graph, WarehouseEdge First, WarehouseEdge Second, WarehouseNode A, WarehouseNode B, WarehouseNode C)
        TwoEdgeGraph(bool secondBidirectional = true)
    {
        var a = new WarehouseNode(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "wa100", Guid.Empty, 0, 0, 0, null, null, "Waypoint", null, null, true);
        var b = new WarehouseNode(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), "wa101", Guid.Empty, 2, 0, 0, null, null, "Waypoint", null, null, true);
        var c = new WarehouseNode(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), "wa102", Guid.Empty, 2, 2, 0, null, null, "Waypoint", null, null, true);

        var first = Edge(a.Id, b.Id, "801-wa100-wa101", new MapPoint(0, 0, null), new MapPoint(2, 0, null));
        var second = Edge(b.Id, c.Id, "801-wa101-wa102", new MapPoint(2, 0, null), new MapPoint(2, 2, null), secondBidirectional);

        var edges = new Dictionary<Guid, IReadOnlyList<WarehouseEdge>>
        {
            [a.Id] = [first],
            [b.Id] = secondBidirectional ? [first, second] : [first],
            [c.Id] = [second],
        };

        var graph = new WarehouseGraph
        {
            MapId = MapId,
            MapVersion = "test",
            GraphVersion = "test",
            Nodes = new Dictionary<Guid, WarehouseNode> { [a.Id] = a, [b.Id] = b, [c.Id] = c },
            Edges = edges,
        };

        return (graph, first, second, a, b, c);
    }

    /// <summary>
    /// Граф с рабочим слотом: <c>wa200</c> (2, 0) → слот <c>wo-1-1</c> (4, 0).
    /// У слота единственное входящее ребро, поэтому точка подхода — <c>wa200</c>.
    /// </summary>
    /// <param name="slotType">Тип узла слота: по умолчанию <c>WORKING</c>.</param>
    public static (WarehouseGraph Graph, WarehouseEdge Edge, WarehouseNode Start, WarehouseNode Slot)
        SlotGraph(string slotType = "WORKING")
    {
        var start = Node("wa200", 2, 0);
        var slot = Node("wo-1-1", 4, 0, slotType);
        var edge = Edge(start, slot, "801-wa200-wo-1-1");

        return (Graph([start, slot], [edge]), edge, start, slot);
    }

    /// <summary>
    /// Линейный граф из четырех узлов в две стороны. Ребра хранятся в порядке проезда
    /// <c>wa300 → wa301 → wa302 → wa303</c>, если <paramref name="storedBackwards"/> ложно.
    /// </summary>
    /// <param name="storedBackwards">Хранить ребра против проезда, тогда маршрут идет задним ходом.</param>
    public static (WarehouseGraph Graph, IReadOnlyList<WarehouseEdge> Edges) LineGraph(bool storedBackwards)
    {
        var a = Node("wa300", 0, 0);
        var b = Node("wa301", 2, 0);
        var c = Node("wa302", 4, 0);
        var d = Node("wa303", 4, 2);

        var edges = storedBackwards
            ? new[] { Edge(b, a, "801-wa301-wa300"), Edge(b, c, "801-wa301-wa302"), Edge(d, c, "801-wa303-wa302") }
            : new[] { Edge(a, b, "801-wa300-wa301"), Edge(b, c, "801-wa301-wa302"), Edge(c, d, "801-wa302-wa303") };

        return (Graph([a, b, c, d], edges), edges);
    }

    /// <summary>Узел карты теста.</summary>
    public static WarehouseNode Node(string code, double x, double y, string nodeType = "WAYPOINT") =>
        new(Guid.NewGuid(), code, Guid.Empty, x, y, 0, null, null, nodeType, null, null, true);

    /// <summary>Ребро карты теста: траектория из двух точек, скорость 1 м/с.</summary>
    public static WarehouseEdge Edge(WarehouseNode from, WarehouseNode to, string code) =>
        new(
            Guid.NewGuid(),
            code,
            from.Id,
            to.Id,
            Guid.Empty,
            200,
            1.0,
            true,
            null,
            null,
            null,
            null,
            true,
            [new MapPoint(from.X, from.Y, null), new MapPoint(to.X, to.Y, null)],
            null,
            true);

    /// <summary>Граф из узлов и ребер: каждое ребро кладется в обе стороны.</summary>
    public static WarehouseGraph Graph(
        IReadOnlyList<WarehouseNode> nodes,
        IReadOnlyList<WarehouseEdge> edges)
    {
        var byNode = nodes.ToDictionary(node => node.Id, _ => new List<WarehouseEdge>());
        foreach (var edge in edges)
        {
            byNode[edge.FromNodeId].Add(edge);
            byNode[edge.ToNodeId].Add(edge);
        }

        return new WarehouseGraph
        {
            MapId = MapId,
            MapVersion = "test",
            GraphVersion = "test",
            Nodes = nodes.ToDictionary(node => node.Id),
            Edges = byNode.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<WarehouseEdge>)pair.Value),
        };
    }

    /// <summary>Один сегмент маршрута для тестов направления.</summary>
    public static RouteSegment SingleSegment(string code, RouteEdgeDirection direction) =>
        Segment(code, direction, 2.0);

    public static PlannedRoute Route(string firstCode, string secondCode, bool secondBackward = false)
    {
        var segments = new List<RouteSegment>
        {
            Segment(firstCode, RouteEdgeDirection.Forward, 2.0),
            Segment(secondCode, secondBackward ? RouteEdgeDirection.Backward : RouteEdgeDirection.Forward, 2.0),
        };

        return new PlannedRoute(segments, 4.0, 4.0, 4.0, 1, 1.5, false, null);
    }

    /// <summary>Маршрут по ребрам в заданном порядке и направлении.</summary>
    public static PlannedRoute Route(
        IReadOnlyList<(string EdgeId, RouteEdgeDirection Direction)> segments)
    {
        var list = segments
            .Select(item => Segment(item.EdgeId, item.Direction, 2.0))
            .ToList();

        return new PlannedRoute(list, list.Count * 2.0, 2.0, 2.0, 1, 1.5, false, null);
    }

    /// <summary>Запрос сборки заказа для маршрута и графа.</summary>
    public static VdaOrderRequest Request(
        PlannedRoute route,
        WarehouseGraph graph,
        long headerId = 639307655688479568,
        long orderNumber = 7,
        int orderUpdateId = 0,
        int sliceStartIndex = 0,
        int sliceNodeCount = 0,
        bool firstNodeTheta = false,
        double? robotX = null,
        double? robotY = null,
        double? robotTheta = null,
        string? terminalActionType = null,
        WarehouseNode? approachTarget = null,
        WarehouseNode? anchorNode = null,
        string? anchorActionType = null,
        string? targetActionType = null,
        int sequenceIdBase = 0,
        bool? firstNodeIsPose = null) => new(
        route,
        graph,
        MapId,
        $"ORD_{orderNumber:D5}",
        Manufacturer,
        SerialNumber,
        headerId,
        Timestamp,
        orderUpdateId,
        sliceStartIndex,
        sliceNodeCount,
        firstNodeTheta,
        robotX,
        robotY,
        robotTheta,
        terminalActionType,
        approachTarget,
        anchorNode,
        anchorActionType,
        targetActionType,
        sequenceIdBase,
        // Как в сервисе: позу робота получает только первое сообщение задачи.
        firstNodeIsPose ?? orderUpdateId <= 0);

    /// <summary>Собирает заказ для маршрута из двух ребер.</summary>
    public static JsonObject Order(double orderNumber = 7)
    {
        var (graph, first, second, _, _, _) = TwoEdgeGraph();

        return Infrastructure.Mqtt.VdaOrderBuilder.Build(
            Request(Route(first.EdgeId, second.EdgeId), graph, orderNumber: (long)orderNumber));
    }

    private static RouteSegment Segment(string code, RouteEdgeDirection direction, double length) => new(
        code,
        direction,
        length,
        1.0,
        1.5,
        length,
        length,
        false,
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
        true);

    private static WarehouseEdge Edge(
        Guid from,
        Guid to,
        string code,
        MapPoint start,
        MapPoint end,
        bool bidirectional = true) => new(
        Guid.NewGuid(),
        code,
        from,
        to,
        Guid.Empty,
        2.0,
        1.0,
        bidirectional,
        null,
        null,
        null,
        null,
        true,
        [start, end],
        null,
        true);
}
