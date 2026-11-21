using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Tests.Unit.Planning;

/// <summary>
/// Синтетический граф и параметры стоимости для юнит-тестов планировщика.
/// </summary>
internal static class PlanningFixtures
{
    public const string MapId = "801";
    public const string Profile = "test-profile";
    public const string OtherProfile = "other-profile";

    /// <summary>Идентификатор версии карты для подставных репозиториев.</summary>
    public static readonly Guid MapVersionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static RoutePlannerOptions Options(CostOptions? cost = null) => new()
    {
        AgvProfiles = new Dictionary<string, AgvProfileOptions>
        {
            [Profile] = new() { Name = Profile, WidthMeters = 0.5, LengthMeters = 1.0, HeightMeters = 0.3 },
            [OtherProfile] = new() { Name = OtherProfile, WidthMeters = 1.0, LengthMeters = 1.0, HeightMeters = 0.3 },
        },
        Cost = cost ?? new CostOptions(),
    };

    /// <summary>Строит узел с координатами из плана.</summary>
    public static WarehouseNode Node(Guid id, string code, double x, double y, bool isActive = true) => new(
        id,
        code,
        Guid.Empty,
        x,
        y,
        0,
        null,
        null,
        "Waypoint",
        null,
        null,
        isActive);

    /// <summary>Строит прямоугольное ребро по координатам концов.</summary>
    public static WarehouseEdge Edge(
        Guid from,
        Guid to,
        double lengthMeters,
        double x0,
        double y0,
        double x1,
        double y1,
        bool bidirectional = true,
        bool rotationAllowed = true,
        bool isActive = true) => new(
        Guid.NewGuid(),
        $"{from:N}-{to:N}",
        from,
        to,
        Guid.Empty,
        lengthMeters,
        1.0,
        bidirectional,
        null,
        null,
        null,
        null,
        rotationAllowed,
        new[] { new MapPoint(x0, y0, null), new MapPoint(x1, y1, null) },
        null,
        isActive);

    /// <summary>Собирает граф из узлов и ребер, раскладывая ребра по узлам начала и конца.</summary>
    public static WarehouseGraph Graph(
        IReadOnlyList<WarehouseNode> nodes,
        IReadOnlyList<WarehouseEdge> edges)
    {
        var byNode = nodes.ToDictionary(node => node.Id, _ => new List<WarehouseEdge>());
        foreach (var edge in edges)
        {
            byNode[edge.FromNodeId].Add(edge);
            if (edge.Bidirectional)
            {
                byNode[edge.ToNodeId].Add(edge);
            }
        }

        return new WarehouseGraph
        {
            MapId = MapId,
            MapVersion = "v1",
            GraphVersion = "v1-hash",
            Nodes = nodes.ToDictionary(node => node.Id),
            Edges = byNode.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<WarehouseEdge>)pair.Value),
        };
    }

    /// <summary>Ширина ребра для подставного репозитория.</summary>
    public static EdgeWidth Width(WarehouseEdge edge, double widthMeters) => new(
        MapVersionId,
        edge.Id,
        widthMeters,
        edge.Length,
        2,
        Domain.Traffic.WidthSource.Raster,
        null);

    /// <summary>Строка проходимости ребра для подставного репозитория.</summary>
    public static EdgePassability Passability(
        WarehouseEdge edge,
        string profile,
        bool isPassable = true) => new(
        MapVersionId,
        edge.Id,
        profile,
        isPassable,
        Domain.Traffic.CorridorClass.C1,
        null);
}
