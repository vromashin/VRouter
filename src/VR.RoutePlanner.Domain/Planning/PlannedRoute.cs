namespace VR.RoutePlanner.Domain.Planning;

/// <summary>
/// Готовый маршрут: сегменты и итоговые величины.
/// </summary>
/// <param name="Segments">Сегменты по порядку движения; пустой список, если старт совпадает с целью.</param>
/// <param name="TotalLengthMeters">Сумма длин сегментов, метры.</param>
/// <param name="TotalSeconds">Сумма времени проезда сегментов, секунды.</param>
/// <param name="TotalCost">Сумма цен сегментов с поворотами и ожиданием, секунды.</param>
/// <param name="TurnCount">Число поворотов больше порога.</param>
/// <param name="MinClearanceMeters">Минимальный запас на маршруте; <see cref="double.PositiveInfinity"/>, если сегментов нет.</param>
/// <param name="PredictedRisk">Маршрут проходит по узкому месту, объезд дороже лимита или не найден.</param>
/// <param name="Reason">Код причины риска: <c>SAFE_DETOUR_TOO_EXPENSIVE</c> или <c>NO_SAFE_DETOUR</c>.</param>
public sealed record PlannedRoute(
    IReadOnlyList<RouteSegment> Segments,
    double TotalLengthMeters,
    double TotalSeconds,
    double TotalCost,
    int TurnCount,
    double MinClearanceMeters,
    bool PredictedRisk,
    string? Reason);
