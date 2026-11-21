namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Оценка ожидания на ребре. На этапе 2 всегда ноль; этап 3 подставит резервирования.
/// </summary>
public interface IWaitEstimator
{
    /// <summary>
    /// Оценивает ожидание при входе на ребро.
    /// </summary>
    /// <param name="request">Запрос на маршрут.</param>
    /// <param name="edgeId">Внутренний ключ ребра.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Ожидание в секундах, ноль или больше.</returns>
    ValueTask<double> EstimateAsync(RoutePlanRequest request, Guid edgeId, CancellationToken ct);
}
