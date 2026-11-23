using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Infrastructure.Traffic;

/// <summary>
/// Разрешение ресурсов <c>vrt_*</c> в память процесса: ключ кэша — <c>(mapId, AgvProfile)</c>,
/// признак свежести — <c>WarehouseGraph.GraphVersion</c>. Смена версии графа (а значит и строки
/// <c>vrt_MapVersion</c>) пересобирает разрешение, поэтому отдельная инвалидация не нужна.
/// </summary>
public sealed class TrafficResourceLookup : ITrafficResourceLookup
{
    private readonly IDbContextFactory<RoutePlannerDbContext> _contextFactory;
    private readonly IWarehouseGraphRepository _graphs;
    private readonly IMapVersionRepository _mapVersions;
    private readonly ConcurrentDictionary<string, CachedMap> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Создает разрешение ресурсов.
    /// </summary>
    /// <param name="contextFactory">Фабрика контекста базы <c>rms</c>.</param>
    /// <param name="graphs">Графы карт: нужны для перевода <c>Guid</c> узлов и ребер в VDA-идентификаторы.</param>
    /// <param name="mapVersions">Строки <c>vrt_MapVersion</c>.</param>
    public TrafficResourceLookup(
        IDbContextFactory<RoutePlannerDbContext> contextFactory,
        IWarehouseGraphRepository graphs,
        IMapVersionRepository mapVersions)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(mapVersions);

        _contextFactory = contextFactory;
        _graphs = graphs;
        _mapVersions = mapVersions;
    }

    /// <inheritdoc />
    public async Task<ITrafficResourceMap?> GetAsync(
        string mapId,
        string agvProfile,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agvProfile);

        var graph = await _graphs.LoadAsync(mapId, ct).ConfigureAwait(false);
        if (graph is null)
        {
            return null;
        }

        var key = $"{mapId}|{agvProfile}";
        if (_cache.TryGetValue(key, out var cached)
            && string.Equals(cached.GraphVersion, graph.GraphVersion, StringComparison.Ordinal))
        {
            return cached.Map;
        }

        var mapVersionId = await _mapVersions
            .TryGetAsync(mapId, graph.GraphVersion, ct)
            .ConfigureAwait(false);
        if (mapVersionId is null)
        {
            // Прогрев карты не выполнен: строки vrt_MapVersion нет, резервировать нечего.
            return null;
        }

        var map = await BuildAsync(mapVersionId.Value, agvProfile, graph, ct).ConfigureAwait(false);
        if (map is null)
        {
            return null;
        }

        _cache[key] = new CachedMap(graph.GraphVersion, map);
        return map;
    }

    private async Task<ITrafficResourceMap?> BuildAsync(
        Guid mapVersionId,
        string agvProfile,
        WarehouseGraph graph,
        CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var resources = await context.TrafficResources
            .AsNoTracking()
            .Where(row => row.MapVersionId == mapVersionId && row.AgvProfile == agvProfile)
            .Select(row => new TrafficResourceRef(row.Id, row.Code, row.ResourceType, row.Capacity))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (resources.Count == 0)
        {
            return null;
        }

        var byId = resources.ToDictionary(resource => resource.ResourceId);
        var ids = byId.Keys.ToList();

        var nodeLinks = await context.TrafficResourceNodes
            .AsNoTracking()
            .Where(row => row.AgvProfile == agvProfile && ids.Contains(row.TrafficResourceId))
            .Select(row => new { row.TrafficResourceId, row.NodeId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var edgeLinks = await context.TrafficResourceEdges
            .AsNoTracking()
            .Where(row => row.AgvProfile == agvProfile && ids.Contains(row.TrafficResourceId))
            .Select(row => new { row.TrafficResourceId, row.EdgeId, row.Direction })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var nodes = new Dictionary<string, TrafficResourceRef>(StringComparer.Ordinal);
        foreach (var link in nodeLinks)
        {
            if (!byId.TryGetValue(link.TrafficResourceId, out var resource)
                || !graph.Nodes.TryGetValue(link.NodeId, out var node))
            {
                continue;
            }

            nodes[node.NodeId] = resource;
        }

        var edgeById = new Dictionary<Guid, WarehouseEdge>();
        foreach (var edge in graph.Edges.Values.SelectMany(list => list))
        {
            edgeById.TryAdd(edge.Id, edge);
        }

        var edges = new Dictionary<string, List<EdgeLink>>(StringComparer.Ordinal);
        foreach (var link in edgeLinks)
        {
            if (!byId.TryGetValue(link.TrafficResourceId, out var resource)
                || !edgeById.TryGetValue(link.EdgeId, out var edge))
            {
                continue;
            }

            var direction = string.Equals(link.Direction, TrafficDirections.Backward, StringComparison.Ordinal)
                ? RouteEdgeDirection.Backward
                : RouteEdgeDirection.Forward;

            if (!edges.TryGetValue(edge.EdgeId, out var list))
            {
                list = [];
                edges[edge.EdgeId] = list;
            }

            list.Add(new EdgeLink(
                new TrafficEdgeResource(
                    resource.ResourceId,
                    resource.ResourceCode,
                    resource.ResourceType,
                    resource.Capacity,
                    direction),
                edge));
        }

        return new TrafficResourceMap(mapVersionId, agvProfile, nodes, edges);
    }

    private sealed record CachedMap(string GraphVersion, ITrafficResourceMap Map);

    private sealed record EdgeLink(TrafficEdgeResource Resource, WarehouseEdge Edge);

    private sealed class TrafficResourceMap(
        Guid mapVersionId,
        string agvProfile,
        IReadOnlyDictionary<string, TrafficResourceRef> nodes,
        IReadOnlyDictionary<string, List<EdgeLink>> edges) : ITrafficResourceMap
    {
        public Guid MapVersionId { get; } = mapVersionId;

        public string AgvProfile { get; } = agvProfile;

        public TrafficResourceRef? FindNode(string nodeId) =>
            nodes.TryGetValue(nodeId, out var resource) ? resource : null;

        public TrafficEdgeResource? FindEdge(string edgeId, string? startNodeId)
        {
            if (!edges.TryGetValue(edgeId, out var links) || links.Count == 0)
            {
                return null;
            }

            if (links.Count == 1)
            {
                return links[0].Resource;
            }

            // Двустороннее ребро лежит в ресурсе обоими направлениями: направление проезда
            // задает узел входа плана относительно хранения ребра.
            var forward = string.Equals(
                links[0].Edge.FromNodeId.ToString(),
                startNodeId,
                StringComparison.OrdinalIgnoreCase);

            var direction = forward ? RouteEdgeDirection.Forward : RouteEdgeDirection.Backward;
            return links.FirstOrDefault(link => link.Resource.Direction == direction)?.Resource
                ?? links[0].Resource;
        }
    }
}
