using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Планировщик маршрута: готовит прохождения ребер по ширинам и проходимости версии карты,
/// затем ищет A* в два прохода по узким коридорам.
/// </summary>
public sealed class AStarPathPlanner : IPathPlanner
{
    private readonly IWarehouseGraphRepository _graphs;
    private readonly IMapVersionRepository _versions;
    private readonly IEdgeWidthRepository _widths;
    private readonly IEdgePassabilityRepository _passability;
    private readonly IWaitEstimator _wait;
    private readonly IOptions<RoutePlannerOptions> _options;
    private readonly ILogger<AStarPathPlanner> _logger;

    /// <summary>
    /// Создает планировщик.
    /// </summary>
    /// <param name="graphs">Графы карт.</param>
    /// <param name="versions">Версии карт.</param>
    /// <param name="widths">Ширины ребер.</param>
    /// <param name="passability">Проходимость по профилям.</param>
    /// <param name="wait">Оценка ожидания.</param>
    /// <param name="options">Параметры <c>Routing</c>.</param>
    /// <param name="logger">Журнал.</param>
    public AStarPathPlanner(
        IWarehouseGraphRepository graphs,
        IMapVersionRepository versions,
        IEdgeWidthRepository widths,
        IEdgePassabilityRepository passability,
        IWaitEstimator wait,
        IOptions<RoutePlannerOptions> options,
        ILogger<AStarPathPlanner> logger)
    {
        _graphs = graphs;
        _versions = versions;
        _widths = widths;
        _passability = passability;
        _wait = wait;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RoutePlanResult> PlanAsync(RoutePlanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _options.Value;
        if (!options.AgvProfiles.TryGetValue(request.Profile, out var profile))
        {
            return RoutePlanResult.NotFound(RoutePlanReason.ProfileUnknown);
        }

        var graph = await _graphs.LoadAsync(request.MapId, ct).ConfigureAwait(false);
        if (graph is null || graph.Nodes.Count == 0)
        {
            return RoutePlanResult.NotFound(RoutePlanReason.MapNotFound);
        }

        if (!graph.Nodes.TryGetValue(request.FromNodeId, out var fromNode)
            || !graph.Nodes.TryGetValue(request.ToNodeId, out var toNode))
        {
            return RoutePlanResult.NotFound(RoutePlanReason.NodeNotFound);
        }

        if (!fromNode.IsActive || !toNode.IsActive)
        {
            return RoutePlanResult.NotFound(RoutePlanReason.NodeInactive);
        }

        var costs = new EdgeCostCalculator(options.Cost);
        var assembler = new RouteAssembler(costs, options.Cost);

        if (request.FromNodeId == request.ToNodeId)
        {
            var empty = assembler.Build(new SearchOutcome(Array.Empty<EdgeTraversal>(), 0), null, false);
            return RoutePlanResult.Success(empty.Route, empty.Explanation);
        }

        var mapVersionId = await _versions.TryGetAsync(request.MapId, graph.GraphVersion, ct).ConfigureAwait(false);
        if (mapVersionId is null)
        {
            return RoutePlanResult.NotFound(RoutePlanReason.NoMapVersion);
        }

        var widths = await _widths.ReadAsync(mapVersionId.Value, ct).ConfigureAwait(false);
        if (widths.Widths.Count == 0)
        {
            return RoutePlanResult.NotFound(RoutePlanReason.NoWidths);
        }

        var passability = await _passability.GetAsync(mapVersionId.Value, request.Profile, ct).ConfigureAwait(false);
        if (passability.Count == 0)
        {
            return RoutePlanResult.NotFound(RoutePlanReason.NoPassability);
        }

        var traversals = await PrepareAsync(
            request,
            graph,
            widths.Widths,
            passability,
            profile,
            request.HasLoad,
            costs,
            ct).ConfigureAwait(false);

        var search = new RouteSearch(graph.Nodes, traversals, costs);
        var first = search.Find(request.FromNodeId, request.ToNodeId, avoidNarrow: false);
        if (first is null)
        {
            return RoutePlanResult.NotFound(RoutePlanReason.NoPath);
        }

        var fast = assembler.Build(first, null, false);
        var hasNarrow = fast.Route.Segments.Any(segment => segment.IsNarrow);
        if (!hasNarrow)
        {
            _logger.LogDebug("Маршрут {From}->{To} без узких коридоров", request.FromNodeId, request.ToNodeId);
            return RoutePlanResult.Success(fast.Route, fast.Explanation);
        }

        var safe = search.Find(request.FromNodeId, request.ToNodeId, avoidNarrow: true);
        if (safe is null)
        {
            var risky = assembler.Build(first, RoutePlanReason.NoSafeDetour, true);
            return RoutePlanResult.Success(risky.Route, risky.Explanation);
        }

        var safeRoute = assembler.Build(safe, null, false);
        if (safeRoute.Route.TotalCost <= costs.SafetyOverrideRatio * fast.Route.TotalCost)
        {
            return RoutePlanResult.Success(safeRoute.Route, safeRoute.Explanation);
        }

        var fallback = assembler.Build(first, RoutePlanReason.SafeDetourTooExpensive, true);
        return RoutePlanResult.Success(fallback.Route, fallback.Explanation);
    }

    private async Task<List<EdgeTraversal>> PrepareAsync(
        RoutePlanRequest request,
        WarehouseGraph graph,
        IReadOnlyList<EdgeWidth> widths,
        IReadOnlyList<EdgePassability> passability,
        AgvProfileOptions profile,
        bool hasLoad,
        EdgeCostCalculator costs,
        CancellationToken ct)
    {
        var widthsByEdge = new Dictionary<Guid, double>();
        foreach (var width in widths)
        {
            widthsByEdge[width.EdgeId] = width.WidthMeters;
        }

        var passableEdges = new HashSet<Guid>();
        foreach (var row in passability)
        {
            if (row.IsPassable)
            {
                passableEdges.Add(row.EdgeId);
            }
        }

        var traversals = new List<EdgeTraversal>();
        var waits = new Dictionary<Guid, double>();
        foreach (var pair in graph.Edges)
        {
            foreach (var edge in pair.Value)
            {
                if (pair.Key != edge.FromNodeId)
                {
                    // Двунаправленное ребро лежит и под ключом узла конца: берем его один раз.
                    continue;
                }

                if (!edge.IsActive || !passableEdges.Contains(edge.Id))
                {
                    continue;
                }

                if (!widthsByEdge.TryGetValue(edge.Id, out var widthMeters))
                {
                    continue;
                }

                if (!waits.TryGetValue(edge.Id, out var waitSeconds))
                {
                    waitSeconds = await _wait.EstimateAsync(request, edge.Id, ct).ConfigureAwait(false);
                    waits[edge.Id] = waitSeconds;
                }

                var breakdown = costs.Calculate(
                    EdgeGeometry.LengthMeters(edge),
                    widthMeters,
                    profile.WidthMeters,
                    hasLoad);

                traversals.Add(new EdgeTraversal(
                    edge,
                    RouteEdgeDirection.Forward,
                    edge.FromNodeId,
                    edge.ToNodeId,
                    breakdown,
                    waitSeconds));

                if (edge.Bidirectional)
                {
                    traversals.Add(new EdgeTraversal(
                        edge,
                        RouteEdgeDirection.Backward,
                        edge.ToNodeId,
                        edge.FromNodeId,
                        breakdown,
                        waitSeconds));
                }
            }
        }

        return traversals;
    }
}
