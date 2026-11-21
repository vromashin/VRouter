namespace VR.RoutePlanner.Api.Contracts;

/// <summary>
/// Тело <c>POST /api/route/plan</c>. Узлы задаются внешними кодами.
/// </summary>
/// <param name="MapId">Идентификатор карты.</param>
/// <param name="Profile">Профиль робота из <c>Routing:AgvProfiles</c>.</param>
/// <param name="FromNodeId">Внешний код узла отправления.</param>
/// <param name="ToNodeId">Внешний код узла назначения.</param>
/// <param name="HasLoad">Робот везет груз.</param>
public sealed record RoutePlanBody(
    string? MapId,
    string? Profile,
    string? FromNodeId,
    string? ToNodeId,
    bool HasLoad);

/// <summary>
/// Сегмент маршрута в ответе.
/// </summary>
public sealed record RouteSegmentBody(
    string EdgeCode,
    string Direction,
    double LengthMeters,
    double SpeedMetersPerSecond,
    double ClearanceMeters,
    double CostSeconds,
    double CumulativeSeconds,
    bool IsNarrow,
    Guid TrafficResourceId,
    bool Passable);

/// <summary>
/// Разложение цены сегмента в ответе.
/// </summary>
public sealed record SegmentExplainBody(
    string EdgeCode,
    double TurnDegrees,
    double TravelSeconds,
    double TurnSeconds,
    double ClearanceSeconds,
    double LengthSeconds,
    double WaitSeconds);

/// <summary>
/// Разложение цены маршрута в ответе.
/// </summary>
public sealed record RouteExplainBody(
    IReadOnlyList<SegmentExplainBody> Segments,
    double TotalTravelSeconds,
    double TotalTurnSeconds,
    double TotalClearanceSeconds,
    double TotalLengthSeconds,
    double TotalWaitSeconds,
    string Formula);

/// <summary>
/// Ответ <c>POST /api/route/plan</c>.
/// </summary>
public sealed record RoutePlanBodyResponse(
    bool Found,
    string? Reason,
    bool PredictedRisk,
    double? TotalLengthMeters,
    double? TotalSeconds,
    double? TotalCost,
    int? TurnCount,
    double? MinClearanceMeters,
    IReadOnlyList<RouteSegmentBody>? Segments,
    RouteExplainBody? Explain);

/// <summary>
/// Ответ <c>GET /api/route/graph/{mapId}/summary</c>.
/// </summary>
public sealed record RouteGraphSummaryBody(
    string MapId,
    string Profile,
    int Nodes,
    int Edges,
    int PassableEdges,
    int ImpassableEdges,
    double? MinClearanceMeters,
    double? MaxClearanceMeters);

/// <summary>
/// Ответ с ошибкой в формате ProblemDetails.
/// </summary>
public sealed record RouteProblemBody(string Code, string Message);
