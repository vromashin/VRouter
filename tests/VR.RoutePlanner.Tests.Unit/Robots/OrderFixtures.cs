using System.Text.Json.Nodes;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Сборка графов и маршрутов для тестов заказа: без базы и планировщика.
/// </summary>
internal static class OrderFixtures
{
    public const string MapId = "801";
    public const string Manufacturer = "Tuskrobots";
    public const string SerialNumber = "98001";

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
            [c.Id] = secondBidirectional ? [second] : [second],
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

    /// <summary>Собирает заказ для маршрута из двух ребер.</summary>
    public static JsonObject Order(double orderNumber = 7)
    {
        var (graph, first, second, _, _, _) = TwoEdgeGraph();
        var order = Infrastructure.Mqtt.VdaOrderBuilder.Build(
            Route(first.EdgeId, second.EdgeId),
            graph,
            MapId,
            Manufacturer,
            SerialNumber,
            (long)orderNumber,
            639307655688479568,
            new DateTimeOffset(2026, 11, 21, 18, 14, 29, 29, TimeSpan.Zero));

        return order;
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
