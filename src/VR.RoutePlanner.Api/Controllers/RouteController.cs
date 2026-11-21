using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Api.Contracts;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Api.Controllers;

/// <summary>
/// Планирование маршрута и сводка по графу.
/// </summary>
[ApiController]
[Route("api/route")]
public sealed class RouteController : ControllerBase
{
    private readonly IPathPlanner _planner;
    private readonly IWarehouseGraphRepository _graphs;
    private readonly IMapVersionRepository _versions;
    private readonly IEdgeWidthRepository _widths;
    private readonly IEdgePassabilityRepository _passability;
    private readonly IOptions<RoutePlannerOptions> _options;

    /// <summary>
    /// Создает контроллер маршрутов.
    /// </summary>
    /// <param name="planner">Планировщик.</param>
    /// <param name="graphs">Графы карт.</param>
    /// <param name="versions">Версии карт.</param>
    /// <param name="widths">Ширины ребер.</param>
    /// <param name="passability">Проходимость по профилям.</param>
    /// <param name="options">Параметры <c>Routing</c>.</param>
    public RouteController(
        IPathPlanner planner,
        IWarehouseGraphRepository graphs,
        IMapVersionRepository versions,
        IEdgeWidthRepository widths,
        IEdgePassabilityRepository passability,
        IOptions<RoutePlannerOptions> options)
    {
        _planner = planner;
        _graphs = graphs;
        _versions = versions;
        _widths = widths;
        _passability = passability;
        _options = options;
    }

    /// <summary>
    /// Строит маршрут между узлами карты.
    /// </summary>
    /// <param name="body">Параметры планирования.</param>
    /// <param name="explain">Вернуть разложение цены.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns>Маршрут либо код причины.</returns>
    [HttpPost("plan")]
    public async Task<IActionResult> PlanAsync(
        [FromBody] RoutePlanBody? body,
        [FromQuery] bool explain = false,
        CancellationToken ct = default)
    {
        if (body is null
            || string.IsNullOrWhiteSpace(body.MapId)
            || string.IsNullOrWhiteSpace(body.Profile)
            || string.IsNullOrWhiteSpace(body.FromNodeId)
            || string.IsNullOrWhiteSpace(body.ToNodeId))
        {
            return NotFoundProblem(RoutePlanReason.MapNotFound, "mapId, profile, fromNodeId и toNodeId обязательны.");
        }

        var options = _options.Value;
        if (!options.AgvProfiles.ContainsKey(body.Profile))
        {
            return BadRequestProblem(RoutePlanReason.ProfileUnknown, $"Профиль '{body.Profile}' не описан в Routing:AgvProfiles.");
        }

        var graph = await _graphs.LoadAsync(body.MapId, ct).ConfigureAwait(false);
        if (graph is null || graph.Nodes.Count == 0)
        {
            return NotFoundProblem(RoutePlanReason.MapNotFound, $"Карта '{body.MapId}' не найдена.");
        }

        var from = FindNode(graph, body.FromNodeId);
        var to = FindNode(graph, body.ToNodeId);
        if (from is null || to is null)
        {
            return NotFoundProblem(RoutePlanReason.NodeNotFound, "Узел отправления или назначения не найден на карте.");
        }

        if (from.Id == to.Id)
        {
            return BadRequestProblem(RoutePlanReason.StartEqualsGoal, "Узел отправления совпадает с узлом назначения.");
        }

        var request = new RoutePlanRequest(body.MapId, body.Profile, from.Id, to.Id, body.HasLoad);
        var result = await _planner.PlanAsync(request, ct).ConfigureAwait(false);
        if (!result.Found)
        {
            return Ok(NotFoundResponse(result.Reason));
        }

        return Ok(MapResult(result, explain));
    }

    /// <summary>
    /// Сводка по графу карты для профиля.
    /// </summary>
    /// <param name="mapId">Идентификатор карты.</param>
    /// <param name="profile">Профиль робота.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns>Количество узлов и ребер и диапазон запаса.</returns>
    [HttpGet("graph/{mapId}/summary")]
    public async Task<IActionResult> SummaryAsync(
        string mapId,
        [FromQuery] string? profile,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return BadRequestProblem(RoutePlanReason.ProfileUnknown, "Параметр profile обязателен.");
        }

        var options = _options.Value;
        if (!options.AgvProfiles.TryGetValue(profile, out var profileOptions))
        {
            return BadRequestProblem(RoutePlanReason.ProfileUnknown, $"Профиль '{profile}' не описан в Routing:AgvProfiles.");
        }

        var graph = await _graphs.LoadAsync(mapId, ct).ConfigureAwait(false);
        if (graph is null || graph.Nodes.Count == 0)
        {
            return NotFoundProblem(RoutePlanReason.MapNotFound, $"Карта '{mapId}' не найдена.");
        }

        var versionId = await _versions.TryGetAsync(mapId, graph.GraphVersion, ct).ConfigureAwait(false);
        if (versionId is null)
        {
            return NotFoundProblem(RoutePlanReason.NoMapVersion, "Для карты нет строки версии.");
        }

        var widths = await _widths.ReadAsync(versionId.Value, ct).ConfigureAwait(false);
        if (widths.Widths.Count == 0)
        {
            return NotFoundProblem(RoutePlanReason.NoWidths, "Для версии карты нет строк ширин.");
        }

        var passability = await _passability.GetAsync(versionId.Value, profile, ct).ConfigureAwait(false);
        if (passability.Count == 0)
        {
            return NotFoundProblem(RoutePlanReason.NoPassability, $"Для профиля '{profile}' нет строк проходимости.");
        }

        var summary = BuildSummary(mapId, profile, graph, widths.Widths, passability, profileOptions, options.Cost);
        return Ok(summary);
    }

    private static WarehouseNode? FindNode(WarehouseGraph graph, string nodeId) =>
        graph.Nodes.Values.FirstOrDefault(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal));

    private static RoutePlanBodyResponse NotFoundResponse(string? reason) =>
        new(false, reason, false, null, null, null, null, null, null, null);

    private static RoutePlanBodyResponse MapResult(RoutePlanResult result, bool explain)
    {
        var route = result.Route!;
        var segments = route.Segments
            .Select(segment => new RouteSegmentBody(
                segment.EdgeCode,
                segment.Direction == RouteEdgeDirection.Forward ? "Forward" : "Backward",
                segment.LengthMeters,
                segment.SpeedMetersPerSecond,
                segment.ClearanceMeters,
                segment.CostSeconds,
                segment.CumulativeSeconds,
                segment.IsNarrow))
            .ToList();

        RouteExplainBody? explanation = null;
        if (explain && result.Explanation is not null)
        {
            explanation = new RouteExplainBody(
                result.Explanation.Segments
                    .Select(item => new SegmentExplainBody(
                        item.EdgeCode,
                        item.TurnDegrees,
                        item.TravelSeconds,
                        item.TurnSeconds,
                        item.ClearanceSeconds,
                        item.LengthSeconds,
                        item.WaitSeconds))
                    .ToList(),
                result.Explanation.TotalTravelSeconds,
                result.Explanation.TotalTurnSeconds,
                result.Explanation.TotalClearanceSeconds,
                result.Explanation.TotalLengthSeconds,
                result.Explanation.TotalWaitSeconds,
                result.Explanation.Formula);
        }

        return new RoutePlanBodyResponse(
            true,
            route.Reason,
            route.PredictedRisk,
            route.TotalLengthMeters,
            route.TotalSeconds,
            route.TotalCost,
            route.TurnCount,
            route.MinClearanceMeters,
            segments,
            explanation);
    }

    private static RouteGraphSummaryBody BuildSummary(
        string mapId,
        string profile,
        WarehouseGraph graph,
        IReadOnlyList<EdgeWidth> widths,
        IReadOnlyList<EdgePassability> passability,
        AgvProfileOptions profileOptions,
        CostOptions cost)
    {
        var widthsByEdge = new Dictionary<Guid, double>();
        foreach (var width in widths)
        {
            widthsByEdge[width.EdgeId] = width.WidthMeters;
        }

        var passable = new HashSet<Guid>();
        foreach (var row in passability)
        {
            if (row.IsPassable)
            {
                passable.Add(row.EdgeId);
            }
        }

        var activeEdges = graph.Edges.Values
            .SelectMany(list => list)
            .Where(edge => edge.IsActive)
            .GroupBy(edge => edge.Id)
            .Select(group => group.First())
            .ToList();

        var passableCount = 0;
        double? minClearance = null;
        double? maxClearance = null;
        foreach (var edge in activeEdges)
        {
            if (!passable.Contains(edge.Id) || !widthsByEdge.TryGetValue(edge.Id, out var widthMeters))
            {
                continue;
            }

            passableCount++;
            var clearance = Math.Max(widthMeters - profileOptions.WidthMeters, cost.ClearanceFloorMeters);
            minClearance = minClearance is null ? clearance : Math.Min(minClearance.Value, clearance);
            maxClearance = maxClearance is null ? clearance : Math.Max(maxClearance.Value, clearance);
        }

        return new RouteGraphSummaryBody(
            mapId,
            profile,
            graph.Nodes.Values.Count(node => node.IsActive),
            activeEdges.Count,
            passableCount,
            activeEdges.Count - passableCount,
            minClearance,
            maxClearance);
    }

    private IActionResult BadRequestProblem(string code, string message) =>
        base.Problem(detail: message, title: code, statusCode: StatusCodes.Status400BadRequest);

    private IActionResult NotFoundProblem(string code, string message) =>
        base.Problem(detail: message, title: code, statusCode: StatusCodes.Status404NotFound);
}
