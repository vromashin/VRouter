namespace VR.RoutePlanner.Domain.Planning;

/// <summary>
/// Итог планирования: маршрут либо код причины, по которой его нет.
/// </summary>
/// <param name="Found">Маршрут построен.</param>
/// <param name="Reason">Код причины отсутствия маршрута либо риска, см. <see cref="RoutePlanReason"/>.</param>
/// <param name="PredictedRisk">Маршрут построен по узкому месту вопреки правилу безопасности.</param>
/// <param name="Route">Найденный маршрут либо <c>null</c>.</param>
/// <param name="Explanation">Разложение цены; заполняется только по запросу.</param>
public sealed record RoutePlanResult(
    bool Found,
    string? Reason,
    bool PredictedRisk,
    PlannedRoute? Route,
    RouteExplanation? Explanation,
    string? ApproachNodeId = null,
    double? ApproachDistanceMeters = null)
{
    /// <summary>
    /// Результат «маршрута нет» с кодом причины.
    /// </summary>
    /// <param name="reason">Код из <see cref="RoutePlanReason"/>.</param>
    /// <returns>Результат с <c>Found = false</c>.</returns>
    public static RoutePlanResult NotFound(string reason) => new(false, reason, false, null, null);

    /// <summary>
    /// Результат с построенным маршрутом.
    /// </summary>
    /// <param name="route">Маршрут.</param>
    /// <param name="explanation">Разложение цены либо <c>null</c>.</param>
    /// <returns>Результат с <c>Found = true</c>.</returns>
    public static RoutePlanResult Success(PlannedRoute route, RouteExplanation? explanation)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new(true, route.Reason, route.PredictedRisk, route, explanation);
    }
}
