using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Детерминированный вывод транспортных ресурсов и конфликтов по графу карты.
/// </summary>
public sealed class TrafficResourceBuilder
{
    private const double LengthTolerancePercent = 5.0;

    private readonly IMapRasterProvider _rasterProvider;
    private readonly IOptions<RoutePlannerOptions> _options;
    private readonly ILogger<TrafficResourceBuilder> _logger;

    /// <summary>
    /// Создает построитель ресурсов.
    /// </summary>
    /// <param name="rasterProvider">Поставщик растра инфлированной карты.</param>
    /// <param name="options">Параметры маршрутизации.</param>
    /// <param name="logger">Логгер.</param>
    public TrafficResourceBuilder(
        IMapRasterProvider rasterProvider,
        IOptions<RoutePlannerOptions> options,
        ILogger<TrafficResourceBuilder> logger)
    {
        ArgumentNullException.ThrowIfNull(rasterProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _rasterProvider = rasterProvider;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Строит ресурсы ребер, ресурсы узлов и конфликты между ними.
    /// </summary>
    /// <param name="graph">Загруженный граф карты.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Набор ресурсов, связей и конфликтов.</returns>
    /// <exception cref="InvalidOperationException">Ребро или узел попали в два ресурса, либо нарушены параметры.</exception>
    public async Task<TrafficResourceBuildResult> BuildAsync(WarehouseGraph graph, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var options = _options.Value;
        var configError = options.Validate();
        if (configError is not null)
        {
            throw new InvalidOperationException(configError);
        }

        var raster = await LoadRasterAsync(graph.MapId, options, ct).ConfigureAwait(false);
        WarnAboutInflationRadius(raster, options);

        var edges = graph.Edges.Values
            .SelectMany(list => list)
            .DistinctBy(edge => edge.Id)
            .OrderBy(edge => edge.EdgeId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Id)
            .ToList();

        var nodes = graph.Nodes.Values
            .OrderBy(node => node.NodeId, StringComparer.Ordinal)
            .ThenBy(node => node.Id)
            .ToList();

        var edgeInfos = edges
            .Select(edge => BuildEdgeInfo(graph, edge, raster, options))
            .ToList();

        var groups = GroupEdges(edgeInfos);
        var resources = new List<TrafficResource>();
        var resourceEdges = new List<TrafficResourceEdge>();
        var resourceByEdge = new Dictionary<Guid, Guid>();

        var sequenceByClass = new Dictionary<CorridorClass, int>();
        foreach (var group in groups)
        {
            var corridorClass = group[0].Class;
            var number = sequenceByClass.TryGetValue(corridorClass, out var current) ? current + 1 : 1;
            sequenceByClass[corridorClass] = number;

            var code = string.Create(
                CultureInfo.InvariantCulture,
                $"{graph.MapId}-{corridorClass}-{number}");

            var resourceId = DeterministicId.FromString(code);
            resources.Add(new TrafficResource(
                resourceId,
                code,
                CorridorClassifier.ResourceTypeOf(corridorClass),
                group[0].Nmax,
                MinEntryInterval: 0,
                MaxOccupancyTime: 0,
                IsActive: true));

            var sequence = 0;
            foreach (var info in group)
            {
                sequence++;
                resourceByEdge[info.Edge.Id] = resourceId;
                resourceEdges.Add(new TrafficResourceEdge(resourceId, info.Edge.Id, TrafficDirections.Forward, sequence));
                if (info.Edge.Bidirectional)
                {
                    resourceEdges.Add(new TrafficResourceEdge(resourceId, info.Edge.Id, TrafficDirections.Backward, sequence));
                }
            }
        }

        var resourceNodes = new List<TrafficResourceNode>();
        var degreeByNode = CountDegrees(edges);
        foreach (var node in nodes)
        {
            var code = string.Create(CultureInfo.InvariantCulture, $"{graph.MapId}-NODE-{node.NodeId}");
            var resourceId = DeterministicId.FromString(code);
            resources.Add(new TrafficResource(
                resourceId,
                code,
                TrafficResourceType.EXCLUSIVE,
                Capacity: 1,
                MinEntryInterval: 0,
                MaxOccupancyTime: 0,
                IsActive: true));
            resourceNodes.Add(new TrafficResourceNode(resourceId, node.Id));

            var degree = degreeByNode.TryGetValue(node.Id, out var value) ? value : 0;
            if (degree > 2)
            {
                var conflictZoneCode = string.Create(CultureInfo.InvariantCulture, $"{graph.MapId}-CZ-{node.NodeId}");
                var conflictZoneId = DeterministicId.FromString(conflictZoneCode);
                resources.Add(new TrafficResource(
                    conflictZoneId,
                    conflictZoneCode,
                    TrafficResourceType.CONFLICT_ZONE,
                    Capacity: 1,
                    MinEntryInterval: 0,
                    MaxOccupancyTime: 0,
                    IsActive: true));
                resourceNodes.Add(new TrafficResourceNode(conflictZoneId, node.Id));
            }
        }

        EnsureUniqueMembership(resourceEdges, resourceNodes);

        var conflicts = BuildConflicts(edgeInfos, edges, resourceByEdge, options.RobotWidth);

        resources.Sort((left, right) => string.CompareOrdinal(left.Code, right.Code));
        resourceEdges.Sort(CompareResourceEdges);
        resourceNodes.Sort(CompareResourceNodes);
        var result = new TrafficResourceBuildResult(resources, resourceEdges, resourceNodes, conflicts);

        _logger.LogInformation(
            "Ресурсы карты {MapId} построены: {ResourceCount} ресурсов, {EdgeLinkCount} связей с ребрами, "
            + "{NodeLinkCount} связей с узлами, {ConflictCount} конфликтов.",
            graph.MapId,
            result.Resources.Count,
            result.ResourceEdges.Count,
            result.ResourceNodes.Count,
            result.Conflicts.Count);

        return result;
    }

    private async Task<MapRaster?> LoadRasterAsync(string mapId, RoutePlannerOptions options, CancellationToken ct)
    {
        if (options.WidthSource == Domain.Traffic.WidthSource.Manual)
        {
            return null;
        }

        try
        {
            return await _rasterProvider.GetAsync(mapId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (options.WidthSource == Domain.Traffic.WidthSource.Default)
        {
            _logger.LogWarning(
                exception,
                "Растр карты {MapId} недоступен, режим WidthSource=Default использует ширину по умолчанию {Width}.",
                mapId,
                options.DefaultCorridorWidth);
            return null;
        }
    }

    private void WarnAboutInflationRadius(MapRaster? raster, RoutePlannerOptions options)
    {
        if (raster is null || string.IsNullOrEmpty(raster.SourceName))
        {
            return;
        }

        var inflation = RasterNameParser.TryReadInflationCentimeters(raster.SourceName);
        if (inflation is null)
        {
            return;
        }

        var robotCentimeters = options.RobotWidth * 100.0;
        if (robotCentimeters <= 0)
        {
            return;
        }

        var deviation = Math.Abs(inflation.Value - robotCentimeters) / robotCentimeters;
        if (deviation > 0.10)
        {
            _logger.LogWarning(
                "Имя растра {FileName} содержит инфляцию {InflationCm} см, что расходится с RobotWidth {RobotCm} см более чем на 10 процентов.",
                raster.SourceName,
                inflation.Value,
                robotCentimeters);
        }
    }

    private EdgeInfo BuildEdgeInfo(WarehouseGraph graph, WarehouseEdge edge, MapRaster? raster, RoutePlannerOptions options)
    {
        var points = edge.TrajectoryPoints;
        var width = ResolveWidth(edge, points, raster, options);
        var nmax = CorridorClassifier.ComputeNmax(width, options.RobotWidth, options.SideMargin);
        var corridorClass = CorridorClassifier.Classify(nmax, options.CorridorClassThresholds);

        var lengthMeters = points.Count > 0
            ? NurbsDiscretizer.PolylineLength(points)
            : DistanceByNodes(graph, edge);

        var declared = edge.Length * 100.0;
        if (declared > 0 && lengthMeters > 0)
        {
            var deviation = Math.Abs(lengthMeters - declared) / declared * 100.0;
            if (deviation > LengthTolerancePercent)
            {
                _logger.LogWarning(
                    "Длина ребра {EdgeId} расходится с Edges.Length * 100: получено {Computed} м, объявлено {Declared} м, расхождение {Deviation} процентов.",
                    edge.EdgeId,
                    lengthMeters,
                    declared,
                    deviation);
            }
        }

        return new EdgeInfo(edge, width, nmax, corridorClass, lengthMeters, points);
    }

    private double ResolveWidth(
        WarehouseEdge edge,
        IReadOnlyList<MapPoint> points,
        MapRaster? raster,
        RoutePlannerOptions options)
    {
        switch (options.WidthSource)
        {
            case Domain.Traffic.WidthSource.Raster:
                if (raster is null)
                {
                    throw new InvalidOperationException(
                        $"Режим WidthSource=Raster требует растр карты, ребро {edge.EdgeId} осталось без ширины.");
                }

                return WidthFromRaster(edge, points, raster, options);

            case Domain.Traffic.WidthSource.Manual:
                var corridor = edge.Corridor;
                if (!string.IsNullOrWhiteSpace(corridor)
                    && options.ManualCorridorWidths.TryGetValue(corridor, out var manualWidth)
                    && manualWidth > 0)
                {
                    return manualWidth;
                }

                if (!string.IsNullOrWhiteSpace(corridor))
                {
                    _logger.LogWarning(
                        "Ширина коридора {Corridor} ребра {EdgeId} не найдена в Routing:ManualCorridorWidths, применена ManualCorridorWidth {Width}.",
                        corridor,
                        edge.EdgeId,
                        options.ManualCorridorWidth);
                }

                return options.ManualCorridorWidth;

            case Domain.Traffic.WidthSource.Default:
                return options.DefaultCorridorWidth;

            default:
                throw new InvalidOperationException($"Неизвестный WidthSource: {options.WidthSource}.");
        }
    }

    private double WidthFromRaster(
        WarehouseEdge edge,
        IReadOnlyList<MapPoint> points,
        MapRaster raster,
        RoutePlannerOptions options)
    {
        if (points.Count == 0)
        {
            _logger.LogWarning(
                "Траектория ребра {EdgeId} недоступна, ширина коридора взята из Routing:DefaultCorridorWidth {Width}.",
                edge.EdgeId,
                options.DefaultCorridorWidth);
            return options.DefaultCorridorWidth;
        }

        var narrowest = double.MaxValue;
        foreach (var point in points)
        {
            var distance = raster.GetObstacleDistanceMeters(point.X, point.Y);
            if (distance < narrowest)
            {
                narrowest = distance;
            }
        }

        if (narrowest == double.MaxValue)
        {
            return options.DefaultCorridorWidth;
        }

        return 2.0 * (narrowest + options.EffectiveInflationRadius);
    }

    private static double DistanceByNodes(WarehouseGraph graph, WarehouseEdge edge)
    {
        if (!graph.Nodes.TryGetValue(edge.FromNodeId, out var from) || !graph.Nodes.TryGetValue(edge.ToNodeId, out var to))
        {
            return 0;
        }

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static List<List<EdgeInfo>> GroupEdges(List<EdgeInfo> edgeInfos)
    {
        var parent = new int[edgeInfos.Count];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot != rightRoot)
            {
                parent[Math.Max(leftRoot, rightRoot)] = Math.Min(leftRoot, rightRoot);
            }
        }

        var byNode = new Dictionary<Guid, List<int>>();
        for (var i = 0; i < edgeInfos.Count; i++)
        {
            foreach (var nodeId in new[] { edgeInfos[i].Edge.FromNodeId, edgeInfos[i].Edge.ToNodeId })
            {
                if (!byNode.TryGetValue(nodeId, out var list))
                {
                    list = new List<int>();
                    byNode[nodeId] = list;
                }

                list.Add(i);
            }
        }

        foreach (var nodeEdges in byNode.Values)
        {
            for (var i = 1; i < nodeEdges.Count; i++)
            {
                if (edgeInfos[nodeEdges[0]].Class == edgeInfos[nodeEdges[i]].Class)
                {
                    Union(nodeEdges[0], nodeEdges[i]);
                }
            }
        }

        var groups = new Dictionary<int, List<EdgeInfo>>();
        for (var i = 0; i < edgeInfos.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<EdgeInfo>();
                groups[root] = list;
            }

            list.Add(edgeInfos[i]);
        }

        var ordered = groups.Values
            .Select(group => group
                .OrderBy(info => info.Edge.EdgeId, StringComparer.Ordinal)
                .ThenBy(info => info.Edge.Id)
                .ToList())
            .OrderBy(group => group[0].Edge.EdgeId, StringComparer.Ordinal)
            .ThenBy(group => group[0].Edge.Id)
            .ToList();

        return ordered;
    }

    private static Dictionary<Guid, int> CountDegrees(List<WarehouseEdge> edges)
    {
        var degrees = new Dictionary<Guid, int>();
        foreach (var edge in edges)
        {
            degrees[edge.FromNodeId] = degrees.TryGetValue(edge.FromNodeId, out var from) ? from + 1 : 1;
            if (edge.ToNodeId != edge.FromNodeId)
            {
                degrees[edge.ToNodeId] = degrees.TryGetValue(edge.ToNodeId, out var to) ? to + 1 : 1;
            }
        }

        return degrees;
    }

    private static void EnsureUniqueMembership(
        List<TrafficResourceEdge> resourceEdges,
        List<TrafficResourceNode> resourceNodes)
    {
        var duplicateEdges = resourceEdges
            .GroupBy(link => (link.EdgeId, link.Direction))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.EdgeId}/{group.Key.Direction}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        if (duplicateEdges.Count > 0)
        {
            throw new InvalidOperationException(
                "Ребро в одном направлении попало в несколько ресурсов: " + string.Join(", ", duplicateEdges) + ".");
        }

        // Узел с степенью больше двух получает и NODE-ресурс, и CONFLICT_ZONE,
        // больше двух связей на узел недопустимо.
        var overloadedNodes = resourceNodes
            .GroupBy(link => link.NodeId)
            .Where(group => group.Count() > 2)
            .Select(group => group.Key)
            .OrderBy(id => id)
            .ToList();

        if (overloadedNodes.Count > 0)
        {
            throw new InvalidOperationException(
                "Узел попал более чем в два ресурса: "
                + string.Join(", ", overloadedNodes.Select(id => id.ToString())) + ".");
        }
    }

    private static List<ResourceConflict> BuildConflicts(
        List<EdgeInfo> edgeInfos,
        List<WarehouseEdge> edges,
        Dictionary<Guid, Guid> resourceByEdge,
        double robotWidth)
    {
        var conflicts = new HashSet<(Guid, Guid)>();
        var sharedNodePairs = new HashSet<(Guid, Guid)>();

        var nodeOwners = new Dictionary<Guid, List<Guid>>();
        foreach (var edge in edges)
        {
            if (!resourceByEdge.TryGetValue(edge.Id, out var resourceId))
            {
                continue;
            }

            foreach (var nodeId in new[] { edge.FromNodeId, edge.ToNodeId })
            {
                if (!nodeOwners.TryGetValue(nodeId, out var list))
                {
                    list = new List<Guid>();
                    nodeOwners[nodeId] = list;
                }

                if (!list.Contains(resourceId))
                {
                    list.Add(resourceId);
                }
            }
        }

        foreach (var ownersList in nodeOwners.Values)
        {
            ownersList.Sort();
            for (var i = 0; i < ownersList.Count; i++)
            {
                for (var j = i + 1; j < ownersList.Count; j++)
                {
                    var pair = Order(ownersList[i], ownersList[j]);
                    sharedNodePairs.Add(pair);
                    conflicts.Add(pair);
                }
            }
        }

        var cellSize = Math.Max(robotWidth, 0.1);
        var grid = new Dictionary<(int, int), Dictionary<Guid, List<MapPoint>>>();
        foreach (var info in edgeInfos)
        {
            if (!resourceByEdge.TryGetValue(info.Edge.Id, out var resourceId))
            {
                continue;
            }

            foreach (var point in info.Points)
            {
                var cell = ((int)Math.Floor(point.X / cellSize), (int)Math.Floor(point.Y / cellSize));
                if (!grid.TryGetValue(cell, out var inCell))
                {
                    inCell = new Dictionary<Guid, List<MapPoint>>();
                    grid[cell] = inCell;
                }

                if (!inCell.TryGetValue(resourceId, out var points))
                {
                    points = new List<MapPoint>();
                    inCell[resourceId] = points;
                }

                points.Add(point);
            }
        }

        var geometric = new HashSet<(Guid, Guid)>();

        // Пары ресурсов проверяются по ячейке и восьми ее соседям: точки двух ресурсов
        // могут оказаться в разных ячейках, но ближе ширины робота.
        var cellKeys = grid.Keys
            .OrderBy(cell => cell.Item1)
            .ThenBy(cell => cell.Item2)
            .ToList();

        foreach (var cell in cellKeys)
        {
            var vicinity = new Dictionary<Guid, List<MapPoint>>();
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!grid.TryGetValue((cell.Item1 + dx, cell.Item2 + dy), out var neighbour))
                    {
                        continue;
                    }

                    foreach (var (resourceId, points) in neighbour)
                    {
                        if (!vicinity.TryGetValue(resourceId, out var merged))
                        {
                            merged = new List<MapPoint>();
                            vicinity[resourceId] = merged;
                        }

                        merged.AddRange(points);
                    }
                }
            }

            if (vicinity.Count < 2)
            {
                continue;
            }

            var ids = vicinity.Keys.ToList();
            ids.Sort();
            for (var i = 0; i < ids.Count; i++)
            {
                for (var j = i + 1; j < ids.Count; j++)
                {
                    var pair = (ids[i], ids[j]);
                    if (sharedNodePairs.Contains(pair) || geometric.Contains(pair))
                    {
                        continue;
                    }

                    if (MinDistance(vicinity[ids[i]], vicinity[ids[j]]) < robotWidth)
                    {
                        geometric.Add(pair);
                    }
                }
            }
        }

        foreach (var pair in geometric)
        {
            conflicts.Add(pair);
        }

        var result = conflicts
            .Select(pair => new ResourceConflict(
                pair.Item1,
                pair.Item2,
                sharedNodePairs.Contains(pair) ? "SHARED_NODE" : "GEOMETRIC_OVERLAP"))
            .OrderBy(conflict => conflict.ResourceId)
            .ThenBy(conflict => conflict.ConflictingResourceId)
            .ToList();

        return result;
    }

    private static (Guid, Guid) Order(Guid left, Guid right) =>
        left.CompareTo(right) <= 0 ? (left, right) : (right, left);

    private static double MinDistance(List<MapPoint> left, List<MapPoint> right)
    {
        var min = double.MaxValue;
        foreach (var first in left)
        {
            foreach (var second in right)
            {
                var dx = first.X - second.X;
                var dy = first.Y - second.Y;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                if (distance < min)
                {
                    min = distance;
                }
            }
        }

        return min;
    }

    private static int CompareResourceEdges(TrafficResourceEdge left, TrafficResourceEdge right)
    {
        var byResource = left.TrafficResourceId.CompareTo(right.TrafficResourceId);
        if (byResource != 0)
        {
            return byResource;
        }

        var byEdge = left.EdgeId.CompareTo(right.EdgeId);
        return byEdge != 0 ? byEdge : string.CompareOrdinal(left.Direction, right.Direction);
    }

    private static int CompareResourceNodes(TrafficResourceNode left, TrafficResourceNode right)
    {
        var byResource = left.TrafficResourceId.CompareTo(right.TrafficResourceId);
        return byResource != 0 ? byResource : left.NodeId.CompareTo(right.NodeId);
    }

    private sealed record EdgeInfo(
        WarehouseEdge Edge,
        double Width,
        int Nmax,
        CorridorClass Class,
        double LengthMeters,
        IReadOnlyList<MapPoint> Points);
}

/// <summary>
/// Результат вывода транспортных ресурсов.
/// </summary>
/// <param name="Resources">Ресурсы ребер, узлов и конфликтных зон.</param>
/// <param name="ResourceEdges">Связи ресурсов с ребрами и направлениями.</param>
/// <param name="ResourceNodes">Связи ресурсов с узлами.</param>
/// <param name="Conflicts">Конфликты ресурсов.</param>
public sealed record TrafficResourceBuildResult(
    IReadOnlyList<TrafficResource> Resources,
    IReadOnlyList<TrafficResourceEdge> ResourceEdges,
    IReadOnlyList<TrafficResourceNode> ResourceNodes,
    IReadOnlyList<ResourceConflict> Conflicts);

/// <summary>
/// Значения направлений обхода ребра.
/// </summary>
public static class TrafficDirections
{
    /// <summary>Прямое направление от <c>FromNodeId</c> к <c>ToNodeId</c>.</summary>
    public const string Forward = "FORWARD";

    /// <summary>Обратное направление от <c>ToNodeId</c> к <c>FromNodeId</c>.</summary>
    public const string Backward = "BACKWARD";
}

/// <summary>
/// Детерминированные идентификаторы ресурсов: GUID выводится из кода ресурса,
/// поэтому повторный расчет на той же карте дает те же значения.
/// </summary>
public static class DeterministicId
{
    /// <summary>
    /// Считает GUID из строкового ключа.
    /// </summary>
    /// <param name="value">Ключ ресурса, например <c>801-C1-1</c>.</param>
    /// <returns>Детерминированный GUID.</returns>
    public static Guid FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}

/// <summary>
/// Разбор имени файла растра на величину инфляции в сантиметрах, например <c>H80</c>.
/// </summary>
public static class RasterNameParser
{
    /// <summary>
    /// Ищет в имени файла метку <c>H&lt;число&gt;</c>.
    /// </summary>
    /// <param name="fileName">Имя файла растра.</param>
    /// <returns>Величина инфляции в сантиметрах либо <c>null</c>.</returns>
    public static double? TryReadInflationCentimeters(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        foreach (var part in name.Split('_', '-', '.'))
        {
            if (part.Length < 2 || part[0] is not ('H' or 'h'))
            {
                continue;
            }

            if (double.TryParse(part[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }
}
